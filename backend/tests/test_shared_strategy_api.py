"""串行 / 批量并行的后端接口约定（不需要权重和 Torch）。

并行只处理带 parallel 标记的请求；共享模型出问题时交回串行，内存里不同时放两份模型。
"""
import sys
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
from pathlib import Path
from unittest.mock import patch

import numpy as np
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import app as service
from posegraph import parallel_batch
from posegraph.parallel_batch import ParallelBatch
from posegraph.shared_inference import InferenceFailure, ModelLifecycle
from inference_fakes import FakeEstimator, FakePool

IMAGE = np.zeros((2, 2, 3), dtype=np.uint8)


class UnsupportedSharedRuntime(RuntimeError):
    """与 estimators/shared_sam3d.py 里的同名异常一样按类名识别。"""


class SharedStrategyApiTests(unittest.TestCase):
    def setUp(self):
        self.stack = ExitStack()
        self.addCleanup(self.stack.close)
        for key, value in {
            "_estimator": FakeEstimator(), "_parallel": ParallelBatch(1, {}),
            "_model_lifecycle": ModelLifecycle(), "_release_after": -1, "_held": False,
            "_model_state": {"loading": False, "error": None},
        }.items():
            self.stack.enter_context(patch.object(service, key, value))
        self.factory = self.stack.enter_context(patch.object(parallel_batch, "SharedInferencePool", side_effect=FakePool))
        self.stack.enter_context(patch.object(service, "_decode", side_effect=lambda data: data))
        self.addCleanup(service._unload_model)

    def configure(self, parallelism, **kwargs):
        return service.model_config(service.ModelConfig(recognition_parallelism=parallelism, **kwargs))

    def test_requests_without_flag_stay_serial(self):
        self.configure(3)
        service._load_model()
        result = service._estimate(IMAGE, False, False)[1]
        self.assertEqual(result.humans, [1])
        self.assertTrue(service._estimator.loaded)
        self.factory.assert_not_called()

    def test_flag_is_ignored_when_parallelism_is_one(self):
        service._estimate(IMAGE, True, True)
        self.assertTrue(service._estimator.loaded)
        self.factory.assert_not_called()

    def test_flagged_requests_share_one_model_and_unload_serial(self):
        service._load_model()
        self.configure(3)
        service._estimate(IMAGE, True, True)
        pool = service._parallel._pool
        self.assertIsInstance(pool, FakePool)
        self.assertEqual(pool.parallelism, 3)
        self.assertEqual(pool.estimates, 1)
        self.assertFalse(service._estimator.loaded)
        info = service._model_info()
        self.assertTrue(info["loaded"])
        self.assertTrue(info["parallel"]["loaded"])
        self.assertEqual(info["config"]["recognitionParallelism"], 3)

    def test_runtime_failure_disables_parallel_and_falls_back_to_serial(self):
        for error in (InferenceFailure(503, "fake load failure"), UnsupportedSharedRuntime("fingerprint mismatch")):
            with self.subTest(error=type(error).__name__):
                service._parallel = ParallelBatch(2, {})
                class BrokenPool(FakePool):
                    def estimate(self, rgb, language="zh"):
                        if isinstance(error, InferenceFailure):
                            raise error
                        raise InferenceFailure(500, str(error)) from error
                self.factory.side_effect = BrokenPool
                result = service._estimate(IMAGE, True, True)[1]
                self.assertEqual(result.humans, [1])
                self.assertTrue(service._estimator.loaded)
                info = service._model_info()["parallel"]
                self.assertIsNotNone(info["disabled"])
                self.assertFalse(info["loaded"])
                self.assertIsNone(service._parallel._pool)
                # 之后的批量请求直接走串行，不再尝试共享模型
                self.factory.reset_mock()
                service._estimate(IMAGE, True, True)
                self.factory.assert_not_called()

    def test_single_request_failure_retries_serially_without_disabling(self):
        class FlakyPool(FakePool):
            def estimate(self, rgb, language="zh"):
                raise InferenceFailure(500, "ValueError: bad image")
        self.factory.side_effect = FlakyPool
        self.configure(2)
        result = service._estimate(IMAGE, True, True)[1]
        self.assertEqual(result.humans, [1])
        self.assertIsNone(service._parallel.disabled)

    def test_environment_switch_keeps_everything_serial(self):
        service._parallel = ParallelBatch(4, {}, disabled="POSEGRAPH_DISABLE_PARALLEL=1")
        service._estimate(IMAGE, True, True)
        self.factory.assert_not_called()
        self.assertTrue(service._estimator.loaded)
        self.assertEqual(service._model_info()["parallel"]["disabled"], "POSEGRAPH_DISABLE_PARALLEL=1")

    def test_serial_request_during_parallel_batch_uses_shared_model(self):
        entered, release = threading.Event(), threading.Event()
        class BlockingPool(FakePool):
            def estimate(self, rgb, language="zh"):
                result = super().estimate(rgb, language)
                if rgb == 0:
                    entered.set()
                    if not release.wait(3):
                        raise RuntimeError("test did not release inference")
                return result
        self.factory.side_effect = BlockingPool
        self.configure(2)
        with ThreadPoolExecutor(max_workers=2) as executor:
            try:
                batch = executor.submit(service._estimate, 0, True, True)
                self.assertTrue(entered.wait(3))
                service._estimate(1, False, False)
                pool = service._parallel._pool
                self.assertEqual(pool.estimates, 2)
                self.assertFalse(service._estimator.loaded)
                release.set()
                batch.result(3)
            finally:
                release.set()
        # 批次结束、共享模型空闲时，单张识别先关掉共享模型再加载串行模型
        service._estimate(2, False, False)
        self.assertTrue(pool.closed)
        self.assertTrue(service._estimator.loaded)

    def test_release_immediately_closes_shared_model_after_unheld_request(self):
        self.configure(2, release_after=0)
        service._estimate(IMAGE, True, True)
        pool = service._parallel._pool
        self.assertFalse(pool.closed)
        service._unload_model()   # 插件在批次结束时调用
        self.assertTrue(pool.closed)
        self.assertFalse(service._held)
        service._estimate(IMAGE, False, True)
        self.assertIsNone(service._parallel._pool)
        self.assertFalse(service._model_info()["loaded"])

    def test_config_changes_rebuild_idle_shared_model(self):
        with TestClient(service.app) as client:
            self.assertEqual(client.post("/model/config", json={"recognition_parallelism": 2}).status_code, 200)
            service._estimate(IMAGE, True, True)
            previous = service._parallel._pool
            for payload in ({"recognition_parallelism": 3},
                            {"device": "cuda", "release_after": 60, "mhr_on_cpu": False}):
                response = client.post("/model/config", json=payload)
                self.assertEqual(response.status_code, 200, response.text)
            self.assertTrue(previous.closed)
            config = response.json()["model"]["config"]
            self.assertEqual(config["recognitionParallelism"], 3)
            self.assertEqual(config["device"], "cuda")
            self.assertEqual(config["releaseAfter"], 60)
            self.assertFalse(config["mhrOnCpu"])
            self.assertEqual(service._parallel.config["device"], "cuda")

    def test_invalid_parallelism_is_rejected_without_creating_a_model(self):
        with TestClient(service.app) as client:
            for value in (0, 9, True, "2"):
                with self.subTest(value=value):
                    response = client.post("/model/config", json={"recognition_parallelism": value})
                    self.assertEqual(response.status_code, 422)
        self.factory.assert_not_called()
        self.assertFalse(service._estimator.loaded)

    def test_serial_hold_retains_model_until_nonheld_request(self):
        service._release_after = 0
        service._estimate(IMAGE, hold=True)
        self.assertTrue(service._estimator.loaded)
        self.assertTrue(service._held)
        service._estimate(IMAGE, hold=False)
        self.assertFalse(service._estimator.loaded)
        self.assertFalse(service._held)

    def test_hold_idle_timeout_releases_serial_and_parallel_models(self):
        for parallel in (False, True):
            with self.subTest(parallel=parallel):
                self.configure(2, release_after=0)
                service._estimate(IMAGE, True, parallel)
                self.assertTrue(service._model_info()["loaded"])
                with patch.object(service.time, "monotonic", return_value=service._last_used + service.HOLD_TIMEOUT - 1):
                    service._release_idle_models()
                self.assertTrue(service._model_info()["loaded"])
                with patch.object(service.time, "monotonic", return_value=service._last_used + service.HOLD_TIMEOUT + 1):
                    service._release_idle_models()
                self.assertFalse(service._model_info()["loaded"])
                self.assertFalse(service._held)

    def test_unload_and_configuration_wait_for_parallel_inference(self):
        for action in (service._unload_model, lambda: self.configure(2, device="cuda")):
            with self.subTest(action=action):
                entered, release, writer_started = threading.Event(), threading.Event(), threading.Event()
                class BlockingPool(FakePool):
                    def estimate(self, rgb, language="zh"):
                        result = super().estimate(rgb, language)
                        entered.set()
                        if not release.wait(3):
                            raise RuntimeError("test did not release inference")
                        return result
                self.factory.side_effect = BlockingPool
                self.configure(2, device="cpu", release_after=0)
                def change():
                    writer_started.set()
                    return action()
                with ThreadPoolExecutor(max_workers=2) as executor:
                    try:
                        recognition = executor.submit(service._estimate, IMAGE, True, True)
                        self.assertTrue(entered.wait(3))
                        pool = service._parallel._pool
                        writer = executor.submit(change)
                        self.assertTrue(writer_started.wait(3))
                        self.assertFalse(release.wait(.05))
                        self.assertFalse(writer.done())
                        self.assertFalse(pool.closed)
                        release.set()
                        recognition.result(3)
                        writer.result(3)
                        self.assertTrue(pool.closed)
                    finally:
                        release.set()

    def test_create_session_forwards_hold_and_parallel_query(self):
        for query, expected in (("hold=true", (b"image", True, False, None, None)),
                                ("hold=true&parallel=true", (b"image", True, True, None, None)),
                                ("name=x", (b"image", False, False, None, None)),
                                ("boxes=1,2,30,40", (b"image", False, False, "1,2,30,40", None))):
            with self.subTest(query=query), \
                    patch.object(service, "_estimate", side_effect=service.HTTPException(422, "fake result")) as estimate, \
                    TestClient(service.app) as client:
                response = client.post(f"/sessions?{query}", content=b"image")
                self.assertEqual(response.status_code, 422)
                estimate.assert_called_once_with(*expected)

    def test_multiview_writeback_uses_current_cache_and_keeps_delete_final(self):
        from posegraph.session_cache import Session, SessionCache
        cache = SessionCache(1)
        self.addCleanup(cache.close)
        original, auxiliary, fused = {"pose": "base"}, {"pose": "aux"}, {"pose": "fused"}
        cache["base"] = Session("base", "base", (2, 2), b"png", [original], 1)
        cache["aux"] = Session("aux", "aux", (2, 2), b"png", [auxiliary], 1)
        views = [{"kind": "back"}]
        with patch.object(service, "_sessions", cache), \
                patch("posegraph.multiview.describe", return_value=views), \
                patch("posegraph.multiview.fuse", return_value=fused):
            # 读辅助图会把主图挤到磁盘，融合时手里拿的还是旧对象
            self.assertEqual(service.session_views("base", service.ViewsRequest(aux=["aux"])), {"views": views})
            self.assertEqual(cache["base"].fused, fused)
            self.assertEqual(service._human(cache["base"], 0), fused)
            self.assertEqual(service._human(cache["base"], 0, use_aux=False), original)
            self.assertEqual(service.session_views("base", service.ViewsRequest()), {"views": []})
            self.assertIsNone(cache["base"].fused)
            def delete_during_fusion(*args):
                cache.discard("base")
                return fused
            with patch("posegraph.multiview.fuse", side_effect=delete_during_fusion):
                with self.assertRaises(service.HTTPException) as failure:
                    service.session_views("base", service.ViewsRequest(aux=["aux"]))
            self.assertEqual(failure.exception.status_code, 404)
            self.assertNotIn("base", cache)


if __name__ == "__main__":
    unittest.main()
