"""视频逐帧识别（video.py）：抽帧、切镜、跟踪、平滑，以及用假模型跑一遍完整任务。"""
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import video


def write_video(path: Path, colors, fps: float = 10, size=(64, 48)) -> None:
    """每个颜色一帧的小视频（帧号写在左上角像素里，读回来能认出是第几帧）。"""
    import cv2

    writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"MJPG"), fps, size)
    for i, color in enumerate(colors):
        frame = np.zeros((size[1], size[0], 3), np.uint8)
        frame[:] = color
        frame[:8, :8] = (i * 7) % 256
        writer.write(frame)
    writer.release()


class SamplingTest(unittest.TestCase):
    def test_sample_times(self):
        self.assertEqual(video.sample_times(0, 1, 5), [0, 0.2, 0.4, 0.6, 0.8, 1.0])
        self.assertEqual(video.sample_times(2, 2, 10), [2])

    def test_iter_frames_and_cuts(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "a.avi"
            write_video(path, [(40, 40, 200)] * 30 + [(30, 200, 30)] * 30)
            info = video.open_video(path)
            self.assertEqual((info.width, info.height), (64, 48))
            self.assertAlmostEqual(info.fps, 10, places=3)
            times = video.sample_times(0, 2.0, 5)
            got = [(k, int(rgb[2, 2, 0])) for k, rgb in video.iter_frames(info, times)]
            self.assertEqual([k for k, _ in got], list(range(len(times))))
            self.assertEqual([v for _, v in got], [(round(t * 10) * 7) % 256 for t in times])
            cuts = video.detect_cuts(info)
            self.assertEqual(len(cuts), 1)
            self.assertAlmostEqual(cuts[0], 3.0, delta=0.4)

    def test_duplicate_times_reuse_frame(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "b.avi"
            write_video(path, [(100, 100, 100)] * 10, fps=5)
            info = video.open_video(path)
            frames = list(video.iter_frames(info, video.sample_times(0, 1, 10)))
            self.assertEqual(len(frames), 11)


class CutTest(unittest.TestCase):
    def test_same_scene_new_angle_is_a_cut(self):
        # 颜色一样（同一个房间），画面结构突变 → 硬切；平时只有一点点变化
        hist = np.ones((32, 16), np.float32)
        rng = np.random.default_rng(0)
        a, b = rng.random((36, 64)).astype(np.float32) * 255, rng.random((36, 64)).astype(np.float32) * 255
        sigs = [video.FrameSignature(hist, (a if k < 20 else b) + k * 0.3) for k in range(40)]
        times = [k / 10 for k in range(40)]
        self.assertEqual(video.find_cuts(sigs, times, 10), [2.0])


class TrackTest(unittest.TestCase):
    def test_two_people_walk_past_each_other(self):
        # A 从左往右、B 从右往左，中间交错；外加一个不相关的人一直站在最右边
        frames = []
        for t in range(20):
            a = [10 + 8 * t, 20, 50 + 8 * t, 120]
            b = [170 - 8 * t, 25, 210 - 8 * t, 125]
            frames.append([b, a, [300, 0, 340, 100]] if t % 2 else [a, [300, 0, 340, 100], b])
        seeds = [frames[0][0], frames[0][2]]   # 第 0 帧选 A、B
        out = video.track(frames, seeds, 0, lost_after=3)
        for t in range(20):
            self.assertAlmostEqual(out[t][0][0], 10 + 8 * t)
            self.assertAlmostEqual(out[t][1][0], 170 - 8 * t)

    def test_seed_in_middle_and_missing_frames(self):
        frames = [[[10 * t, 0, 10 * t + 40, 80]] for t in range(10)]
        frames[3] = []   # 第 3 帧没找到
        out = video.track(frames, [frames[6][0]], 6, lost_after=3)
        self.assertIsNone(out[3][0])
        self.assertEqual(out[0][0][0], 0)
        self.assertEqual(out[9][0][0], 90)

    def test_reacquire_by_appearance_after_cut(self):
        # 第 5 帧切镜：A 跳到画面另一边（框不重叠），旁边多了个穿别的衣服的人；按外观找回 A，不认错人
        look_a, look_b = np.r_[np.ones(8), np.zeros(8)] / 8, np.r_[np.zeros(8), np.ones(8)] / 8
        frames, looks = [], []
        for t in range(10):
            a = [10 + t, 0, 50 + t, 80] if t < 5 else [300 + t, 10, 340 + t, 90]
            frames.append([[200, 0, 240, 80], a] if t >= 5 else [a])
            looks.append([look_b, look_a] if t >= 5 else [look_a])
        out = video.track(frames, [frames[0][0]], 0, lost_after=3, appearances=looks, seed_appearances=[look_a])
        self.assertTrue(all(out[t][0][0] >= 300 for t in range(5, 10)))
        # 不给外观时照旧：跟丢就是跟丢
        plain = video.track(frames, [frames[0][0]], 0, lost_after=3)
        self.assertIsNone(plain[6][0])

    def test_occluded_person_keeps_box_in_hug(self):
        # A、B 走近后抱在一起：检测器只给一个合并的大框，B 的框大部分落在里面——沿用 B 之前的框
        frames = [[[0, 0, 40, 100], [100, 0, 140, 100]], [[20, 0, 60, 100], [80, 0, 120, 100]]] + [[[30, 0, 120, 100]]] * 5
        out = video.track(frames, frames[0], 0, lost_after=2, occluded_after=10)
        self.assertTrue(all(out[t][0] is not None and out[t][1] is not None for t in range(7)))
        self.assertEqual(out[6][1], [80.0, 0.0, 120.0, 100.0])
        plain = video.track(frames, frames[0], 0, lost_after=2)
        self.assertIsNone(plain[6][1])

    def test_box_jump_dropped(self):
        boxes = [[[0, 0, 40, 100], [100, 0, 140, 100]] for _ in range(9)]
        boxes[4] = [[0, 0, 200, 300], [100, 0, 140, 100]]     # 第 4 帧主角的框突然大了三倍（认错 / 合成大框）
        self.assertEqual(video.drop_box_jumps(boxes, 3), 1)
        self.assertIsNone(boxes[4][0])
        self.assertIsNotNone(boxes[4][1])

    def test_lost_person_not_reattached_to_stranger(self):
        frames = [[[0, 0, 40, 80]]] * 3 + [[]] * 10 + [[[300, 0, 340, 80]]] * 3
        out = video.track(frames, [[0, 0, 40, 80]], 0, lost_after=2)
        self.assertTrue(all(out[t][0] is None for t in range(13, 16)))


class SmoothTest(unittest.TestCase):
    def test_fill_and_smooth_keep_constant(self):
        v = np.ones((10, 3)) * 5
        valid = np.ones(10, bool)
        valid[[0, 4, 9]] = False
        v[~valid] = 0
        filled = video.fill_gaps(v, valid)
        np.testing.assert_allclose(filled, 5)
        np.testing.assert_allclose(video.smooth(filled, 2.0), 5)

    def test_smooth_reduces_jitter(self):
        rng = np.random.default_rng(1)
        clean = np.linspace(0, 1, 50)[:, None]
        noisy = clean + rng.normal(0, 0.05, clean.shape)
        sm = video.smooth(noisy, 2.0)
        self.assertLess(np.abs(sm - clean).mean(), np.abs(noisy - clean).mean() * 0.6)

    def test_smooth_quats_handles_sign_flips(self):
        q = R.from_euler("y", np.linspace(0, 90, 20)[:, None], degrees=True).as_quat()
        q[1::2] *= -1   # 每隔一帧翻符号：同一个转动
        sm = video.smooth_quats(q, 1.0)
        angles = R.from_quat(sm).as_euler("yxz", degrees=True)[:, 0]
        self.assertTrue(np.all(np.diff(angles) > 0))
        self.assertAlmostEqual(angles[0], 0, delta=3)
        self.assertAlmostEqual(angles[-1], 90, delta=3)

    def test_smooth_camera_stays_orthonormal(self):
        cams = [{"position": [0, 1, 3 + 0.01 * i], "forward": [0, 0, -1], "up": [0, 1, 0.1 * (i % 2)], "fovY": 40}
                for i in range(10)]
        cams[5] = None
        out = video.smooth_camera(cams, 1.0)
        self.assertEqual(len(out), 10)
        for c in out:
            self.assertAlmostEqual(np.dot(c["forward"], c["up"]), 0, places=6)
            self.assertAlmostEqual(np.linalg.norm(c["up"]), 1, places=6)


class FrameWriterTest(unittest.TestCase):
    def test_music_added_from_sibling_audio(self):
        import wave

        from posegraph import audio

        if not audio.available():
            self.skipTest("PyAV not installed")
        import av

        with tempfile.TemporaryDirectory() as d:
            src_video = Path(d) / "原视频.avi"
            write_video(src_video, [(50, 50, 50)] * 5)          # 视频没声音，旁边有同名的 wav
            with wave.open(str(Path(d) / "原视频.wav"), "wb") as w:
                w.setnchannels(1)
                w.setsampwidth(2)
                w.setframerate(44100)
                t = np.arange(44100 * 4) / 44100
                w.writeframes((np.sin(2 * np.pi * 440 * t) * 8000).astype(np.int16).tobytes())
            source = audio.find_source(src_video)
            self.assertEqual(source.name, "原视频.wav")
            path = Path(d) / "录制.mp4"
            writer = video.FrameWriter(path, 64, 48, 30)
            writer.music = (source, 1.0)
            for _ in range(60):
                writer.write(np.zeros((48, 64, 4), np.uint8).tobytes(), 64, 48)
            writer.close()
            self.assertEqual(writer.music_error, "")
            self.assertAlmostEqual(writer.music_seconds, 2.0, delta=0.05)
            with av.open(str(path)) as c:
                self.assertEqual(sorted(st.type for st in c.streams), ["audio", "video"])

    def test_playback_wav_slice_speed_volume(self):
        import io
        import wave

        from posegraph import audio

        if not audio.available():
            self.skipTest("PyAV not installed")
        with tempfile.TemporaryDirectory() as d:
            src = Path(d) / "song.wav"
            with wave.open(str(src), "wb") as w:
                w.setnchannels(1)
                w.setsampwidth(2)
                w.setframerate(22050)
                w.writeframes((np.sin(2 * np.pi * 220 * np.arange(22050 * 6) / 22050) * 10000).astype(np.int16).tobytes())
            read = lambda b: wave.open(io.BytesIO(b))
            full = read(audio.wav(src, 1.0, 3.0))
            self.assertEqual((full.getnchannels(), full.getframerate()), (2, audio.PLAY_RATE))
            self.assertAlmostEqual(full.getnframes() / audio.PLAY_RATE, 2.0, delta=0.01)
            half = read(audio.wav(src, 1.0, 3.0, speed=0.5))
            self.assertAlmostEqual(half.getnframes() / audio.PLAY_RATE, 4.0, delta=0.01)
            quiet = np.frombuffer(read(audio.wav(src, 1.0, 3.0, volume=0.5)).readframes(1000), np.int16)
            loud = np.frombuffer(read(audio.wav(src, 1.0, 3.0)).readframes(1000), np.int16)
            self.assertAlmostEqual(np.abs(quiet).max() / np.abs(loud).max(), 0.5, delta=0.02)

    def test_raw_keeps_camera_motion(self):
        from posegraph.motion import CameraSteps

        n = 4
        human = {key: np.zeros((2, 3), np.float32) for key in video.HUMAN_KEYS}
        human["focal_length"] = 500.0
        steps = np.tile([0, 0, 0, 1, 0.1, 0, 0], (n, 1)).astype(float)
        raw = video.Raw(10.0, 0.0, [0, 0.1, 0.2, 0.3], (64, 48), np.tile([0, 0, 0, 1.0], (n, 1)), [[human] * n], [None], [[None] * n],
                        CameraSteps(steps, np.array([False, True, False, True]), "depth"), np.array([False, False, True, False]))
        back = video.raw_from_bytes(video.raw_to_bytes(raw))
        self.assertEqual(back.camera.mode, "depth")
        np.testing.assert_allclose(back.camera.steps, steps, atol=1e-6)
        self.assertEqual(back.camera.ok.tolist(), [False, True, False, True])
        self.assertEqual(back.breaks.tolist(), [False, False, True, False])

    def test_writes_readable_mp4(self):
        import cv2

        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "out" / "r.mp4"
            writer = video.FrameWriter(path, 65, 49, 30)            # 奇数边裁成偶数
            self.assertEqual(writer.size, (64, 48))
            for i in range(10):
                bgra = np.zeros((49, 65, 4), np.uint8)
                bgra[:, :, 2] = 200                                  # 红
                bgra[:, : 6 * i + 6, 1] = 200
                writer.write(bgra.tobytes(), 65, 49)
            writer.close()
            cap = cv2.VideoCapture(str(path))
            ok, frame = cap.read()
            count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
            cap.release()
            self.assertTrue(ok)
            self.assertEqual(frame.shape[:2], (48, 64))
            self.assertGreater(frame[24, 60, 2], 150)                # 红色还在（BGRA → BGR 没弄反）
            self.assertLess(frame[24, 60, 0], 80)
            self.assertEqual(count, 10)

    def test_chinese_path_keeps_name(self):
        """OpenCV 不认中文路径：写完的文件要在原样的中文名下，不留临时文件。"""
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "视频 人族高地 男_1.mp4"
            writer = video.FrameWriter(path, 32, 32, 10)
            writer.write(bytes(32 * 32 * 4), 32, 32)
            writer.close()
            self.assertEqual([p.name for p in Path(d).iterdir()], [path.name])
            self.assertGreater(path.stat().st_size, 0)

    def test_discard_leaves_nothing(self):
        with tempfile.TemporaryDirectory() as d:
            writer = video.FrameWriter(Path(d) / "取消.mp4", 32, 32, 10)
            writer.write(bytes(32 * 32 * 4), 32, 32)
            writer.discard()
            self.assertEqual(list(Path(d).iterdir()), [])

    def test_rejects_wrong_size(self):
        with tempfile.TemporaryDirectory() as d:
            writer = video.FrameWriter(Path(d) / "x.mp4", 32, 32, 10)
            with self.assertRaises(ValueError):
                writer.write(bytes(10), 32, 32)
            writer.close()


class JobTest(unittest.TestCase):
    def test_run_job_with_fakes(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "c.avi"
            write_video(path, [(90, 90, 90)] * 40)
            info = video.open_video(path)

            def detect_people(rgb):
                k = int(rgb[2, 2, 0]) // 7
                return [] if k == 12 else [[k, 0, k + 30, 40]]   # 第 1.2 秒那帧找不到人

            def estimate(rgb, boxes):
                k = int(rgb[2, 2, 0]) // 7
                humans = [SimpleNamespace(joint_global_rots=None, joint_coords=np.zeros((3, 3)), rest_joint_coords=None,
                                          rest_joint_rots=None, keypoints_2d=None, focal_length=500.0, cam_t=np.array([0, 0, 3.0]),
                                          bbox=b, extra={"vertices": np.zeros((2, 3)), "rest_vertices": None, "k": k})
                          for b in boxes]
                for h in humans:
                    h.joint_coords = np.full((3, 3), float(k))
                return SimpleNamespace(humans=humans, image_size=(64, 48))

            def retarget(human, image_size, person, upright):
                k = human["joint_coords"][0, 0]
                q = R.from_euler("z", 3 * k, degrees=True).as_quat()
                return SimpleNamespace(bones={"j_kosi": (np.array([0, 1, 0]), q, np.ones(3)),
                                              "j_kao": (np.array([0, 1.6, 0.01 * k]), [0, 0, 0, 1], np.ones(3))})

            camera = lambda human, rt: {"position": [0, 1, 3 + 0.1 * human["joint_coords"][0, 0]], "forward": [0, 0, -1],
                                        "up": [0, 1, 0], "fovY": 40.0}
            upright = lambda human, size: [0, 0, 0, 1]
            spec = video.JobSpec(0.5, 2.0, 10, 1.0, [video.PersonSpec([10, 0, 40, 40], "c0101")], smooth=0.3)
            job = video.new_job("v", spec)
            video.run_job(job, info, estimate, detect_people, retarget, camera, upright)
            clip = job.result
            self.assertEqual(len(clip["times"]), 16)
            person = clip["people"][0]
            self.assertEqual(person["bones"], ["j_kosi", "j_kao"])
            self.assertEqual(len(person["frames"]), 16)
            self.assertEqual(len(person["frames"][0]), 20)
            self.assertTrue(person["missing"][7])            # 1.2 秒那帧
            self.assertEqual(sum(person["missing"]), 1)
            z = [f[12] for f in person["frames"]]            # j_kao 的 z 位置 = 0.01 × 帧号，缺的那帧插值补上
            np.testing.assert_allclose(z, [0.01 * (5 + i) for i in range(16)], atol=0.002)
            self.assertEqual(len(clip["camera"]), 16)
            self.assertAlmostEqual(clip["camera"][7]["position"][2], 3 + 0.1 * 12, delta=0.05)

    def test_face_added_and_gaps_filled(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "f.avi"
            write_video(path, [(90, 90, 90)] * 40)
            info = video.open_video(path)
            frame_of = lambda rgb: int(rgb[2, 2, 0]) // 7

            def estimate(rgb, boxes):
                k = frame_of(rgb)
                humans = [SimpleNamespace(joint_global_rots=None, joint_coords=np.full((3, 3), float(k)), rest_joint_coords=None,
                                          rest_joint_rots=None, keypoints_2d=None, focal_length=500.0, cam_t=np.array([0, 0, 3.0]),
                                          bbox=b, extra={"vertices": np.zeros((2, 3)), "rest_vertices": None}) for b in boxes]
                return SimpleNamespace(humans=humans, image_size=(64, 48))

            def retarget(human, image_size, person, upright):
                k = human["joint_coords"][0, 0]
                return SimpleNamespace(bones={"j_kosi": (np.array([0, 1, 0]), [0, 0, 0, 1], np.ones(3)),
                                              "j_kao": (np.array([0, 1.6, 0.01 * k]), [0, 0, 0, 1], np.ones(3))})

            # 第 8、9 帧找不到脸；面部骨架只有一根，局部位移 x = 0.001 × 帧号，挂到头上 = 头的位置 + 局部位移
            def analyze(rgb, human, size):
                k = frame_of(rgb)
                return None if k in (8, 9) else video.FaceObs(np.full((478, 3), float(k)), None, {"jawOpen": 0.1 * k})

            face = video.FaceHooks(
                analyze=analyze,
                solve=lambda person, obs: (np.array([[0.001 * obs.landmarks[0, 0], 0, 0]]), np.array([[0, 0, 0, 1.0]]), np.ones(1)),
                attach=lambda person, t, q, s, head: {"j_f_test": (np.asarray(head[0]) + t[0], q[0], s[0])})
            params = SimpleNamespace(face=True)
            spec = video.JobSpec(0.5, 2.0, 10, 1.0, [video.PersonSpec([10, 0, 40, 40], "c0101", params=params)], smooth=0.3)
            job = video.new_job("v", spec)
            video.run_job(job, info, estimate, lambda rgb: [[0, 0, 30, 40]], retarget,
                          lambda human, rt: {"position": [0, 1, 3], "forward": [0, 0, -1], "up": [0, 1, 0], "fovY": 40.0},
                          lambda human, size: None, face)
            person = job.result["people"][0]
            self.assertEqual(person["bones"], ["j_kosi", "j_kao", "j_f_test"])
            self.assertEqual(person["face"], {"enabled": True, "frames": 14})
            for i in (4, 7, 10):                              # 第 9 帧（缺脸，插值）、12、15 帧
                f, k = person["frames"][i], 5 + i
                self.assertAlmostEqual(f[20], 0.001 * k, delta=0.0005)       # 表情跟着帧号走
                self.assertAlmostEqual(f[22], 0.01 * k, delta=0.002)         # 挂在头上

            # 原始数据存取一遍再重算（换种族：头抬高 0.1 米、表情权重翻倍），帧数、补帧、面捕都还在
            raw = video.raw_from_bytes(video.raw_to_bytes(job.raw))
            self.assertEqual(raw.times, job.raw.times)
            self.assertEqual(sum(f is not None for f in raw.faces[0]), 14)
            self.assertAlmostEqual(raw.faces[0][0].blendshapes["jawOpen"], 0.5, places=5)

            def retarget_taller(human, image_size, person, upright):
                rt = retarget(human, image_size, person, upright)
                pos, q, sc = rt.bones["j_kao"]
                rt.bones["j_kao"] = (pos + [0, 0.1, 0], q, sc)
                return rt

            strong = video.FaceHooks(analyze, lambda person, obs: (np.array([[0.002 * obs.landmarks[0, 0], 0, 0]]), np.array([[0, 0, 0, 1.0]]),
                                                                   np.ones(1)), face.attach)
            spec2 = [video.PersonSpec([10, 0, 40, 40], "c0201", params=params)]
            again = video.build_clip(raw, spec2, 0.3, retarget_taller,
                                     lambda human, rt: {"position": [0, 1, 3], "forward": [0, 0, -1], "up": [0, 1, 0], "fovY": 40.0}, strong)
            p2 = again["people"][0]
            self.assertEqual(p2["race"], "c0201")
            self.assertEqual(len(p2["frames"]), 16)
            f, k = p2["frames"][7], 12
            self.assertAlmostEqual(f[11], 1.7, delta=0.001)                  # j_kao 的高度跟着新骨架
            self.assertAlmostEqual(f[20], 0.002 * k, delta=0.0005)           # 新的表情权重

    def test_cancel(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "d.avi"
            write_video(path, [(90, 90, 90)] * 20)
            info = video.open_video(path)
            spec = video.JobSpec(0, 1.5, 10, 0, [video.PersonSpec([0, 0, 30, 40], "c0101")])
            job = video.new_job("v", spec)
            job.cancel.set()
            with self.assertRaises(video.Cancelled):
                video.run_job(job, info, None, lambda rgb: [], None, None, None)


if __name__ == "__main__":
    unittest.main()
