"""跨厂商显存探测（gpu.py）与 CUDA / ROCm 自动设备及精度选择单元测试。"""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import gpu
from posegraph.estimators import sam3d_body
from posegraph.estimators.sam3d_body import Sam3dBodyEstimator
from posegraph.i18n import set_language, tr


class GpuProbeTests(unittest.TestCase):
    def setUp(self):
        self._orig_cache = gpu._cache
        self._orig_has_smi = gpu._has_nvidia_smi
        gpu._cache = (0.0, None)
        gpu._has_nvidia_smi = None

    def tearDown(self):
        gpu._cache = self._orig_cache
        gpu._has_nvidia_smi = self._orig_has_smi

    def test_nvidia_smi_parsed_and_strips_vendor_from_query_gpu(self):
        proc = SimpleNamespace(stdout="NVIDIA GeForce RTX 4070, 12282, 2048, 10234\n")
        with patch("subprocess.run", return_value=proc) as run_mock:
            info = gpu.query_gpu(max_age=0)
            info2, vendor = gpu.query_gpu_vendor(max_age=10)
        self.assertEqual(run_mock.call_count, 1)
        self.assertEqual(info, {"name": "NVIDIA GeForce RTX 4070", "totalMB": 12282, "usedMB": 2048, "freeMB": 10234})
        self.assertEqual(info2, info)
        self.assertEqual(vendor, "nvidia")
        self.assertIs(gpu._has_nvidia_smi, True)

    def test_missing_nvidia_smi_cached_and_falls_back_to_dxgi(self):
        dxgi_info = {"name": "AMD Radeon RX 7900 GRE", "totalMB": 16324, "usedMB": 3500, "freeMB": 12824, "vendor": "amd"}
        with patch("subprocess.run", side_effect=FileNotFoundError) as run_mock, \
                patch.object(sys, "platform", "win32"), \
                patch.object(gpu, "_query_windows_dxgi", return_value=dxgi_info) as dxgi_mock:
            first, vendor1 = gpu.query_gpu_vendor(max_age=0)
            second = gpu.query_gpu(max_age=0)
        # FileNotFoundError 后记住本机无 nvidia-smi，后续不再创建子进程
        self.assertEqual(run_mock.call_count, 1)
        self.assertIs(gpu._has_nvidia_smi, False)
        self.assertEqual(dxgi_mock.call_count, 2)
        self.assertEqual(vendor1, "amd")
        self.assertEqual(first, {"name": "AMD Radeon RX 7900 GRE", "totalMB": 16324, "usedMB": 3500, "freeMB": 12824})
        self.assertEqual(second, first)

    def test_linux_amdgpu_sysfs_fallback(self):
        with tempfile.TemporaryDirectory() as tmp:
            drm = Path(tmp)
            dev = drm / "card1" / "device"
            dev.mkdir(parents=True)
            (dev / "mem_info_vram_total").write_text(str(16384 * 1048576), encoding="utf-8")
            (dev / "mem_info_vram_used").write_text(str(2048 * 1048576), encoding="utf-8")
            (dev / "vendor").write_text("0x1002\n", encoding="utf-8")
            (dev / "product_name").write_text("AMD Radeon RX 7900 GRE\n", encoding="utf-8")

            orig_path = gpu.Path
            with patch.object(gpu, "Path", side_effect=lambda p: drm if p == "/sys/class/drm" else orig_path(p)):
                res = gpu._query_linux_sysfs()
        self.assertEqual(
            res,
            {"name": "AMD Radeon RX 7900 GRE", "totalMB": 16384, "usedMB": 2048, "freeMB": 14336, "vendor": "amd"},
        )


class DeviceSelectionTests(unittest.TestCase):
    def _make_estimator(self, device="auto", precision="auto", min_free=3000):
        return Sam3dBodyEstimator(Path("."), Path("."), device=device, precision=precision, min_free_vram_mb=min_free)

    def test_rocm_torch_selects_amd_gpu_when_vram_sufficient(self):
        fake_torch = SimpleNamespace(
            version=SimpleNamespace(cuda=None, hip="7.2.53211"),
            cuda=SimpleNamespace(is_available=MagicMock(return_value=True)),
        )
        amd_gpu = ({"name": "AMD Radeon RX 7900 GRE", "totalMB": 16324, "usedMB": 3000, "freeMB": 13324}, "amd")
        with patch.dict(sys.modules, {"torch": fake_torch}), \
                patch("posegraph.gpu.query_gpu_vendor", return_value=amd_gpu):
            est = self._make_estimator()
            self.assertEqual(est._pick_device(), "cuda")
            self.assertEqual(est.device_reason, ("可用显存 {free} MB", {"free": 13324}))
            fake_torch.cuda.is_available.assert_called_once()

    def test_rocm_torch_skips_context_init_when_vram_low_or_vendor_mismatch(self):
        is_avail = MagicMock(return_value=True)
        fake_torch = SimpleNamespace(
            version=SimpleNamespace(cuda=None, hip="7.2.53211"),
            cuda=SimpleNamespace(is_available=is_avail),
        )
        low_vram = ({"name": "AMD Radeon Graphics", "totalMB": 512, "usedMB": 200, "freeMB": 312}, "amd")
        nvidia_gpu = ({"name": "NVIDIA GeForce RTX 4060", "totalMB": 8188, "usedMB": 1000, "freeMB": 7188}, "nvidia")
        with patch.dict(sys.modules, {"torch": fake_torch}):
            with patch("posegraph.gpu.query_gpu_vendor", return_value=low_vram):
                est = self._make_estimator()
                self.assertEqual(est._pick_device(), "cpu")
                self.assertEqual(est.device_reason[0], "可用显存 {free} MB，低于 {need} MB")
            with patch("posegraph.gpu.query_gpu_vendor", return_value=nvidia_gpu):
                est = self._make_estimator()
                self.assertEqual(est._pick_device(), "cpu")
                self.assertEqual(est.device_reason, ("未检测到 AMD 显卡",))
        # 低显存或显卡厂商不匹配时绝不调用 torch.cuda.is_available() 建上下文
        is_avail.assert_not_called()

    def test_cuda_torch_rejects_amd_gpu_without_initializing_cuda(self):
        is_avail = MagicMock(return_value=False)
        fake_torch = SimpleNamespace(
            version=SimpleNamespace(cuda="12.8", hip=None),
            cuda=SimpleNamespace(is_available=is_avail),
        )
        amd_gpu = ({"name": "AMD Radeon RX 7900 GRE", "totalMB": 16324, "usedMB": 3000, "freeMB": 13324}, "amd")
        with patch.dict(sys.modules, {"torch": fake_torch}), \
                patch("posegraph.gpu.query_gpu_vendor", return_value=amd_gpu):
            est = self._make_estimator()
            self.assertEqual(est._pick_device(), "cpu")
            self.assertEqual(est.device_reason, ("未检测到 NVIDIA 显卡",))
        is_avail.assert_not_called()

    def test_cpu_only_torch_reports_no_gpu_support(self):
        fake_torch = SimpleNamespace(
            version=SimpleNamespace(cuda=None, hip=None),
            cuda=SimpleNamespace(is_available=MagicMock(return_value=False)),
        )
        with patch.dict(sys.modules, {"torch": fake_torch}):
            est = self._make_estimator()
            self.assertEqual(est._pick_device(), "cpu")
            self.assertEqual(est.device_reason, ("PyTorch 未包含 GPU 支持",))

    def test_rocm_auto_precision_and_windows_miopen_workaround(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp) / "repo"
            ckpt_dir = Path(tmp) / "ckpt"
            (ckpt_dir / "assets").mkdir(parents=True)
            repo.mkdir()
            (ckpt_dir / "model.ckpt").write_bytes(b"x")
            (ckpt_dir / "assets" / "mhr_model.pt").write_bytes(b"x")

            for bf16_supported, expected_prec in ((True, "bf16"), (False, "fp16")):
                cudnn = SimpleNamespace(enabled=True)
                fake_torch = SimpleNamespace(
                    version=SimpleNamespace(cuda=None, hip="7.2.53211"),
                    backends=SimpleNamespace(cudnn=cudnn),
                    cuda=SimpleNamespace(
                        is_available=lambda: True,
                        is_bf16_supported=lambda b=bf16_supported: b,
                        empty_cache=lambda: None,
                    ),
                    OutOfMemoryError=RuntimeError,
                )
                fake_sam_mod = SimpleNamespace(SAM3DBodyEstimator=lambda **kw: SimpleNamespace(faces=[[0, 1, 2]]))
                est = Sam3dBodyEstimator(repo, ckpt_dir, device="cuda", precision="auto")
                with patch.dict(sys.modules, {"torch": fake_torch, "sam_3d_body": fake_sam_mod}), \
                        patch.object(sam3d_body.sys, "platform", "win32"), \
                        patch.object(est, "_build", return_value=("model", "cfg")) as build_mock:
                    est.load()
                self.assertEqual(est.active_precision, expected_prec)
                self.assertEqual(build_mock.call_args.args[3], expected_prec)
                self.assertFalse(cudnn.enabled)

            # CUDA 环境下保持 cudnn.enabled 不变
            cuda_cudnn = SimpleNamespace(enabled=True)
            fake_cuda_torch = SimpleNamespace(
                version=SimpleNamespace(cuda="12.8", hip=None),
                backends=SimpleNamespace(cudnn=cuda_cudnn),
                cuda=SimpleNamespace(is_available=lambda: True, empty_cache=lambda: None),
                OutOfMemoryError=RuntimeError,
            )
            est_cuda = Sam3dBodyEstimator(repo, ckpt_dir, device="cuda", precision="auto")
            with patch.dict(sys.modules, {"torch": fake_cuda_torch, "sam_3d_body": fake_sam_mod}), \
                    patch.object(sam3d_body.sys, "platform", "win32"), \
                    patch.object(est_cuda, "_build", return_value=("model", "cfg")):
                est_cuda.load()
            self.assertTrue(cuda_cudnn.enabled)

    def test_new_device_reasons_have_english_translations(self):
        set_language("en")
        try:
            for zh in ("未检测到 AMD 显卡", "未检测到可用独立显卡", "PyTorch 未包含 GPU 支持", "当前 PyTorch 环境不支持此显卡"):
                en = tr(zh)
                self.assertNotEqual(en, zh)
                self.assertTrue(en.isascii())
        finally:
            set_language("zh")


if __name__ == "__main__":
    unittest.main()
