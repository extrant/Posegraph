"""视频逐帧识别：解码、粗查切镜、跟踪选定的人、逐帧识别与重定向、每帧跟拍镜头、时间平滑。

流程（run_job，后台线程）：
1. 按帧率抽帧，每帧找人；从用户选人的那一帧起向后、向前跟踪（框的重叠度 + 匀速预测，匈牙利匹配）
2. 每帧把跟住的人一起识别（SAM 3D Body 按框），算站姿重力校正后丢掉网格顶点（顶点只用来估重心）
3. 重力校正按主角（第一个选定的人）逐帧估，时间上重度平滑后全员共用（同一台相机拍的，重力方向一致）
4. 逐帧重定向；镜头按主角算在「角色模型系」里（照片镜头复刻的 photo_camera_in_world，模型变换取单位），
   角色原地不动，人在画面里的走动表现为镜头移动（每帧跟拍）
5. 骨骼旋转 / 位置、镜头在时间上平滑；没识别到的帧按前后插值补上（标记 missing）
1~3 步和种族无关，结果存成 Raw（raw_to_bytes，插件存在片段旁边）；4~6 步是 build_clip，换种族、调表情权重时只重做这一半
识别时还逐帧解算原视频镜头的运动（motion.CameraTracker）、记下切镜；片段里另给角色走位 root 和三条镜头轨道
（原运镜 / 平滑跟拍 / 定点摇镜，motion.world_tracks），插件的走动模式按它们摆角色和镜头
6. 表情（参数里开了 face 时）：每帧在头部区域找脸（同照片面捕），换成面部骨架的局部姿势，
   在局部空间里补空帧、平滑（比身体更重，偏保守：识别抖动不当表情），再挂到每帧的头上

片段格式（to_clip）：骨骼名只列一次，每帧每根骨骼 10 个数（位置 3、四元数 xyzw 4、缩放 3），镜头每帧
position / forward / up（模型系，米）+ fovY（度）。插件按这个格式插值播放、存片段文件。
"""

from __future__ import annotations

import os
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import numpy as np
from scipy.spatial.transform import Rotation as R

CUT_SAMPLE_FPS = 6.0
CUT_THRESHOLD = 0.55      # 相邻采样帧 HSV 直方图相关系数低于这个算切镜
FADE_SECONDS = 1.0        # 叠化：隔这么久的两帧差得像切镜，中间却是慢慢变过去的
FADE_STABLE = 0.9         # 叠化前、后各自的画面要稳定（相邻帧相关系数至少这么高），免得把摇镜头、灯光变化当成切镜
CUT_THUMB_DIFF = 8.0      # 同一个场景换机位（颜色几乎不变）：相邻帧 64×36 灰度缩略图平均差至少这么多（0~255）…
CUT_THUMB_RATIO = 6.0     # …且是全片相邻帧差中位数的这么多倍（人跳舞、镜头移动时相邻帧差中位约 0.7）
MATCH_IOU = 0.25          # 跟踪：框重叠度至少这么多才算同一个人
LOST_AFTER_SECONDS = 1.0  # 连续这么久没匹配上就当这个人离开了画面，之后不再按位置预测（只按外观找回）
REACQUIRE_SIM = 0.8       # 按外观找回跟丢的人：外观相似度至少这么高（画面里还有别的没认领的人时）
REACQUIRE_SIM_ALONE = 0.6 # 只剩一个没认领的人时放宽一些（单人视频切镜后，灯光、远近不同也认得回来）
OCCLUDED_COVER = 0.6      # 没跟上的人，预测框有这么多面积落在别人的框里：当成被挡住（拥抱、交叠），沿用之前的框
OCCLUDED_SECONDS = 6.0    # 被挡住最多这么久还沿用（拥抱常有好几秒）
BOX_JUMP = 0.6            # 多人时框的高度和前后 BOX_WINDOW_SECONDS 的中位数差这么多以上（跑近镜头时框也会变大：只拦成倍的突变）：当成认错人（合成大框、跳到别人身上），丢掉这一帧
BOX_WINDOW_SECONDS = 1.0
APPEARANCE_KEEP = 0.9     # 外观模板的更新：每次可靠匹配后 新 = 0.9 × 旧 + 0.1 × 这一帧
UPRIGHT_SIGMA_SECONDS = 1.5
FACE_MIN_SIGMA_SECONDS = 0.08   # 表情至少按这么宽的高斯平滑（眨眼约 0.1~0.15 秒，还留得住）
FPS_CHOICES = (5, 10, 15, 30)


@dataclass
class VideoInfo:
    path: Path
    fps: float
    frame_count: int
    width: int
    height: int

    @property
    def duration(self) -> float:
        return self.frame_count / self.fps if self.fps > 0 else 0.0

    def describe(self) -> dict:
        return {"fps": round(self.fps, 3), "frames": self.frame_count, "duration": round(self.duration, 3),
                "width": self.width, "height": self.height}


def open_video(path: Path) -> VideoInfo:
    import cv2

    cap = cv2.VideoCapture(str(path))
    try:
        if not cap.isOpened():
            raise ValueError("cannot open")
        fps = float(cap.get(cv2.CAP_PROP_FPS) or 0)
        count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        w, h = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
        ok, _ = cap.read()
        if not ok or w <= 0 or h <= 0:
            raise ValueError("no frames")
        if fps <= 0 or fps > 1000:
            fps = 30.0
        return VideoInfo(path, fps, max(count, 1), w, h)
    finally:
        cap.release()


def read_frame(info: VideoInfo, t: float):
    """第 t 秒那一帧（RGB）。"""
    import cv2

    cap = cv2.VideoCapture(str(info.path))
    try:
        cap.set(cv2.CAP_PROP_POS_FRAMES, _frame_index(info, t))
        ok, bgr = cap.read()
        if not ok:
            raise ValueError("cannot read frame")
        return cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    finally:
        cap.release()


def _frame_index(info: VideoInfo, t: float) -> int:
    return int(np.clip(round(t * info.fps), 0, info.frame_count - 1))


def iter_frames(info: VideoInfo, times: list[float], stop: Callable[[], bool] = lambda: False):
    """按时间顺序依次给出 (序号, RGB)。一次定位到起点后顺序读（grab 跳过不要的帧），比逐帧定位快得多。"""
    import cv2

    if not times:
        return
    targets = [_frame_index(info, t) for t in times]
    cap = cv2.VideoCapture(str(info.path))
    try:
        pos = targets[0]
        cap.set(cv2.CAP_PROP_POS_FRAMES, pos)
        frame = None
        for k, target in enumerate(targets):
            if stop():
                return
            if frame is None or target != pos - 1:
                while pos < target:
                    if not cap.grab():
                        return
                    pos += 1
                ok, bgr = cap.read()
                if not ok:
                    return
                pos += 1
                frame = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
            yield k, frame
    finally:
        cap.release()


def sample_times(start: float, end: float, fps: float) -> list[float]:
    if end <= start or fps <= 0:
        return [start]
    n = int(np.floor((end - start) * fps + 1e-6)) + 1
    return [round(start + i / fps, 6) for i in range(n)]


@dataclass
class FrameSignature:
    """切镜判断用的一帧摘要：HSV 颜色直方图 + 64×36 灰度缩略图。"""
    hist: np.ndarray
    thumb: np.ndarray


def frame_signature(rgb: np.ndarray) -> FrameSignature:
    import cv2

    small = cv2.resize(rgb, (160, max(1, round(160 * rgb.shape[0] / rgb.shape[1]))), interpolation=cv2.INTER_AREA)
    hsv = cv2.cvtColor(small, cv2.COLOR_RGB2HSV)
    hist = cv2.calcHist([hsv], [0, 1], None, [32, 16], [0, 180, 0, 256])
    cv2.normalize(hist, hist)
    thumb = cv2.cvtColor(cv2.resize(rgb, (64, 36), interpolation=cv2.INTER_AREA), cv2.COLOR_RGB2GRAY).astype(np.float32)
    return FrameSignature(hist, thumb)


def find_cuts(sigs: list[FrameSignature], times: list[float], fps: float) -> list[float]:
    """找切镜（秒）：相邻两帧颜色突然差很多，或画面结构突然变了（同一个场景换机位，颜色差不多）——硬切；
    隔 FADE_SECONDS 的两帧颜色差得像切镜、中间是慢慢变过去的、前后两段画面各自稳定——叠化，记在中间。
    同一场景换机位的叠化这里认不出（颜色、结构都是渐变的），跟踪按外观找回人、镜头解算断开时兜底。"""
    import cv2

    corr = lambda a, b: cv2.compareHist(sigs[a].hist, sigs[b].hist, cv2.HISTCMP_CORREL)
    n, lag = len(sigs), max(2, round(FADE_SECONDS * fps))
    step = [corr(k - 1, k) for k in range(1, n)]
    diff = np.array([np.abs(sigs[k].thumb - sigs[k - 1].thumb).mean() for k in range(1, n)])
    jump = max(CUT_THUMB_DIFF, CUT_THUMB_RATIO * float(np.median(diff))) if len(diff) else np.inf
    cuts = [k for k in range(1, n) if step[k - 1] < CUT_THRESHOLD or diff[k - 1] >= jump]
    for k in range(lag + 1, n - 1):
        a = k - lag
        if (corr(a, k) < CUT_THRESHOLD and min(step[a:k]) >= CUT_THRESHOLD
                and corr(a - 1, a) >= FADE_STABLE and corr(k, k + 1) >= FADE_STABLE
                and not any(abs(c - (a + k) / 2) <= lag for c in cuts)):
            cuts.append((a + k) // 2)
    return [round(times[k], 2) for k in sorted(cuts)]


def detect_cuts(info: VideoInfo, stop: Callable[[], bool] = lambda: False) -> list[float]:
    """粗查切镜（硬切和叠化）：按 CUT_SAMPLE_FPS 抽帧比颜色直方图（find_cuts）。只用来提示用户「这段有切镜」，不精确到帧。"""
    times = sample_times(0.0, info.duration, CUT_SAMPLE_FPS)
    sigs = [frame_signature(rgb) for _, rgb in iter_frames(info, times, stop)]
    return find_cuts(sigs, times[:len(sigs)], CUT_SAMPLE_FPS)


# ---------------------------------------------------------------- 录制（游戏画面 → mp4）


def _ascii_dir(preferred: Path) -> Path:
    """OpenCV 在 Windows 上按本地代码页解释路径，中文路径会写成乱码文件名：找一个纯 ASCII 的目录放临时文件。"""
    import tempfile

    for d in (preferred, Path(tempfile.gettempdir()), Path.home(), Path(os.environ.get("SystemDrive", "C:") + "\\") / "PosegraphTemp"):
        if str(d).isascii():
            d.mkdir(parents=True, exist_ok=True)
            return d
    raise ValueError("no ASCII-only folder for the temporary video file")


class FrameWriter:
    """插件逐帧截的游戏画面编码成 mp4。优先 Windows 自带的 Media Foundation H.264（不用额外组件，手机、浏览器都能放），
    不行再退回 OpenCV 自带的 MPEG-4（mp4v，个别播放器放不了）。H.264 要求宽高是偶数，奇数时裁掉最后一行 / 列。
    OpenCV 不认中文路径：先写到纯 ASCII 路径的临时文件，close 时再搬到 path（片段名里有种族、视频名常有中文）。"""

    def __init__(self, path: Path, width: int, height: int, fps: float):
        import cv2

        self.path = Path(path)
        self.size = (width - width % 2, height - height % 2)
        self.frames = 0
        self.codec = ""
        self.touched = time.monotonic()   # 最近一次收到帧（插件或游戏中途没了时，后端按这个清掉没录完的）
        self.music: tuple[Path, float] | None = None   # 录完配上的音乐：(来源文件, 从第几秒起)
        self.music_seconds = 0.0
        self.music_error = ""
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._temp = _ascii_dir(self.path.parent) / f".posegraph_rec_{uuid.uuid4().hex[:12]}.mp4"
        for api, codec in ((cv2.CAP_MSMF, "H264"), (cv2.CAP_FFMPEG, "mp4v")):
            writer = cv2.VideoWriter(str(self._temp), api, cv2.VideoWriter_fourcc(*codec), float(fps), self.size)
            if writer.isOpened():
                self._writer, self.codec = writer, codec
                return
            writer.release()
            self._temp.unlink(missing_ok=True)
        raise ValueError("no video encoder")

    def write(self, data: bytes, width: int, height: int, order: str = "bgra") -> None:
        """一帧原始像素（每像素 4 字节，order 为 bgra 或 rgba），尺寸要和打开时一致（奇数边会被裁掉）。"""
        image = np.frombuffer(data, np.uint8)
        if image.size != width * height * 4:
            raise ValueError(f"frame size {image.size} != {width}x{height}x4")
        image = image.reshape(height, width, 4)[: self.size[1], : self.size[0]]
        if (image.shape[1], image.shape[0]) != self.size:
            raise ValueError("frame size changed")
        bgr = image[:, :, [2, 1, 0]] if order == "rgba" else image[:, :, :3]
        self._writer.write(np.ascontiguousarray(bgr))
        self.frames += 1
        self.touched = time.monotonic()

    def close(self) -> None:
        """写完：配上音乐（有的话；失败时保留无声视频，原因记在 music_error），搬到 path（已有同名文件时替换）。"""
        import shutil

        self._writer.release()
        if self.music is not None and self._temp.exists() and self.frames > 0:
            from . import audio

            try:
                self.music_seconds = audio.add_music(self._temp, *self.music)
            except Exception as ex:
                self.music_error = str(ex) or type(ex).__name__
        if self._temp.exists():
            self.path.unlink(missing_ok=True)
            shutil.move(str(self._temp), str(self.path))

    def discard(self) -> None:
        """不要了（取消、中途断了）：关掉并删掉临时文件。"""
        self._writer.release()
        self._temp.unlink(missing_ok=True)


# ---------------------------------------------------------------- 跟踪


def iou(a, b) -> float:
    x1, y1 = max(a[0], b[0]), max(a[1], b[1])
    x2, y2 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def appearance(rgb: np.ndarray, box) -> np.ndarray:
    """人物框里的外观（找回跟丢的人用）：上半身、下半身各一个 HSV 色相 × 饱和度直方图，拼起来归一化。"""
    import cv2

    h, w = rgb.shape[:2]
    x1, y1, x2, y2 = (int(round(v)) for v in box)
    x1, y1, x2, y2 = max(0, x1), max(0, y1), min(w, x2), min(h, y2)
    if x2 - x1 < 4 or y2 - y1 < 8:
        return np.zeros(2 * 16 * 8, np.float32)
    crop = cv2.resize(rgb[y1:y2, x1:x2], (32, 64), interpolation=cv2.INTER_AREA)
    hsv = cv2.cvtColor(crop, cv2.COLOR_RGB2HSV)
    parts = [cv2.calcHist([hsv[r]], [0, 1], None, [16, 8], [0, 180, 0, 256]).ravel() for r in (slice(4, 32), slice(32, 60))]
    v = np.concatenate([p / max(p.sum(), 1e-9) for p in parts]).astype(np.float32)
    return v / 2


def similarity(a: np.ndarray, b: np.ndarray) -> float:
    """两个外观的相似度 0~1（直方图的 Bhattacharyya 系数）。"""
    return float(np.sum(np.sqrt(np.clip(a, 0, None) * np.clip(b, 0, None))))


def track(detections: list[list[list[float]]], seeds: list[list[float]], seed_index: int, lost_after: int,
          appearances: list[list[np.ndarray]] | None = None,
          seed_appearances: list[np.ndarray] | None = None, occluded_after: int = 0) -> list[list[list[float] | None]]:
    """detections：每个采样帧找到的人物框；seeds：用户在 seed_index 那一帧选的框。
    从 seed 帧向后、向前各跟一遍，返回 [帧][人] → 框或 None（这一帧没跟上）。
    给了外观（appearances 和 detections 一一对应、seed_appearances 每人一个）时，按位置跟不上的人
    （切镜、叠化后人跳到画面别处，或离开画面又回来）按外观从没认领的人里找回。
    occluded_after > 0（多人）：没跟上的人如果预测框大部分落在别人的框里（被挡住、两人合成一个框），
    最多这么多帧里沿用预测框（不往前推速度），让识别照样能估这个人。"""
    n = len(detections)
    out: list[list[list[float] | None]] = [[None] * len(seeds) for _ in range(n)]
    for k, box in enumerate(seeds):
        out[seed_index][k] = list(box)
    for order in (range(seed_index + 1, n), range(seed_index - 1, -1, -1)):
        _track_direction(detections, out, seeds, list(order), lost_after, appearances, seed_appearances, occluded_after)
    return out


def drop_box_jumps(boxes: list[list[list[float] | None]], window: int) -> int:
    """每个人的框高度和前后 window 帧的中位数比，差 BOX_JUMP 以上的帧置 None（就地改）。返回丢掉的帧数。"""
    dropped = 0
    people = len(boxes[0]) if boxes else 0
    for p in range(people):
        h = np.array([b[p][3] - b[p][1] if b[p] is not None else np.nan for b in boxes])
        for k in np.flatnonzero(np.isfinite(h)):
            near = h[max(0, k - window):k + window + 1]
            mid = np.nanmedian(near)
            if np.isfinite(mid) and mid > 0 and abs(h[k] - mid) > BOX_JUMP * mid:
                boxes[k][p] = None
                dropped += 1
    return dropped


def _covered(box, other) -> float:
    """box 有多少比例的面积落在 other 里。"""
    x1, y1 = max(box[0], other[0]), max(box[1], other[1])
    x2, y2 = min(box[2], other[2]), min(box[3], other[3])
    area = max(1e-6, (box[2] - box[0]) * (box[3] - box[1]))
    return max(0.0, x2 - x1) * max(0.0, y2 - y1) / area


def _track_direction(detections, out, seeds, order, lost_after, appearances=None, seed_appearances=None, occluded_after=0):
    from scipy.optimize import linear_sum_assignment

    last = [np.asarray(b, float) for b in seeds]
    velocity = [np.zeros(4) for _ in seeds]
    missed = [0] * len(seeds)
    hidden = [0] * len(seeds)   # 连续被挡住的帧数
    model = [None if seed_appearances is None else np.asarray(a, np.float32) for a in (seed_appearances or [None] * len(seeds))]
    for f in order:
        live = [k for k in range(len(seeds)) if missed[k] <= lost_after]
        predicted = {k: last[k] + velocity[k] * (missed[k] + 1) for k in live}
        boxes = [np.asarray(b, float) for b in detections[f]]
        looks = appearances[f] if appearances is not None else None
        matched, taken = set(), set()

        def take(k, c, reset):
            velocity[k] = np.zeros(4) if reset else 0.5 * velocity[k] + 0.5 * (boxes[c] - last[k]) / (missed[k] + 1)
            last[k], missed[k] = boxes[c], 0
            out[f][k] = boxes[c].tolist()
            matched.add(k)
            taken.add(c)
            if looks is not None and model[k] is not None and not reset:
                model[k] = APPEARANCE_KEEP * model[k] + (1 - APPEARANCE_KEEP) * looks[c]

        if live and boxes:
            cost = np.array([[1.0 - iou(predicted[k], b) for b in boxes] for k in live])
            rows, cols = linear_sum_assignment(cost)
            for r, c in zip(rows, cols):
                if 1.0 - cost[r, c] >= MATCH_IOU:
                    take(live[r], c, reset=False)
        if looks is not None:
            # 按位置没跟上的：在没认领的人里按外观找，相似度高的先配
            free = [c for c in range(len(boxes)) if c not in taken]
            pairs = sorted(((similarity(model[k], looks[c]), k, c) for k in range(len(seeds)) if k not in matched and model[k] is not None
                            for c in free), reverse=True)
            for sim, k, c in pairs:
                if k in matched or c in taken:
                    continue
                if sim >= (REACQUIRE_SIM_ALONE if len(free) == 1 else REACQUIRE_SIM):
                    take(k, c, reset=True)
        for k in range(len(seeds)):
            if k in matched:
                hidden[k] = 0
                continue
            # 被挡住：预测框大部分落在某个框里（别人的，或两人合成的一个大框）——沿用之前的框，速度归零
            if occluded_after and hidden[k] < occluded_after and boxes and missed[k] <= lost_after:
                guess = last[k]
                if max(_covered(guess, b) for b in boxes) >= OCCLUDED_COVER:
                    out[f][k] = guess.tolist()
                    velocity[k] = np.zeros(4)
                    hidden[k] += 1
                    continue
            missed[k] += 1


# ---------------------------------------------------------------- 平滑


def _gaussian(sigma: float) -> np.ndarray:
    if sigma <= 0:
        return np.ones(1)
    r = max(1, int(np.ceil(3 * sigma)))
    x = np.arange(-r, r + 1)
    k = np.exp(-0.5 * (x / sigma) ** 2)
    return k / k.sum()


def fill_gaps(values: np.ndarray, valid: np.ndarray) -> np.ndarray:
    """没值的帧按前后有值的帧线性插值，两头按最近的值延长。values：(T, …)。"""
    values = np.array(values, dtype=np.float64, copy=True)
    idx = np.flatnonzero(valid)
    if len(idx) == 0 or len(idx) == len(values):
        return values
    flat = values.reshape(len(values), -1)
    t = np.arange(len(values))
    for c in range(flat.shape[1]):
        flat[:, c] = np.interp(t, idx, flat[idx, c])
    return flat.reshape(values.shape)


def smooth(values: np.ndarray, sigma: float) -> np.ndarray:
    """沿时间（第 0 维）做高斯平滑，两头按边缘值延长（不会往 0 拉）。"""
    values = np.asarray(values, dtype=np.float64)
    k = _gaussian(sigma)
    if len(k) == 1 or len(values) < 2:
        return values.copy()
    r = len(k) // 2
    padded = np.concatenate([np.repeat(values[:1], r, 0), values, np.repeat(values[-1:], r, 0)])
    out = np.zeros_like(values)
    for i, w in enumerate(k):
        out += w * padded[i:i + len(values)]
    return out


def smooth_quats(q: np.ndarray, sigma: float) -> np.ndarray:
    """四元数序列 (T, …, 4) 平滑：先逐帧翻符号对齐到上一帧（q 和 -q 是同一个转动），平滑后归一化。"""
    q = np.array(q, dtype=np.float64, copy=True)
    for t in range(1, len(q)):
        dot = np.sum(q[t] * q[t - 1], axis=-1, keepdims=True)
        q[t] = np.where(dot < 0, -q[t], q[t])
    s = smooth(q, sigma)
    return s / np.linalg.norm(s, axis=-1, keepdims=True).clip(1e-9)


def smooth_frames(frames: np.ndarray, valid: np.ndarray, sigma: float) -> np.ndarray:
    """骨骼帧 (T, 骨骼数, 10)：补空帧后，位置 / 旋转平滑，缩放不动。"""
    filled = fill_gaps(frames, valid)
    for t in np.flatnonzero(~valid):   # 插值出来的四元数归一化
        filled[t, :, 3:7] /= np.linalg.norm(filled[t, :, 3:7], axis=-1, keepdims=True).clip(1e-9)
    out = filled.copy()
    out[:, :, 0:3] = smooth(filled[:, :, 0:3], sigma)
    out[:, :, 3:7] = smooth_quats(filled[:, :, 3:7], sigma)
    return out


def smooth_camera(cams: list[dict | None], sigma: float) -> list[dict]:
    valid = np.array([c is not None for c in cams])
    if not valid.any():
        return []
    blank = next(c for c in cams if c is not None)
    pack = lambda c: np.r_[c["position"], c["forward"], c["up"], c["fovY"]]
    arr = fill_gaps(np.array([pack(c if c is not None else blank) for c in cams]), valid)
    arr = smooth(arr, sigma)
    out = []
    for row in arr:
        f = row[3:6] / np.linalg.norm(row[3:6]).clip(1e-9)
        u = row[6:9] - f * np.dot(row[6:9], f)
        u /= np.linalg.norm(u).clip(1e-9)
        out.append({"position": row[0:3].tolist(), "forward": f.tolist(), "up": u.tolist(), "fovY": float(row[9])})
    return out


# ---------------------------------------------------------------- 任务


@dataclass
class PersonSpec:
    box: list[float]
    race: str
    reference: object | None = None   # BrioPose（游戏角色当前姿势，骨长 / 缩放按它）
    reference_lengths: bool = True
    ivcs: bool = True
    params: object | None = None      # tuning.RetargetParams
    edits: list | None = None         # 时间轴修正的关键帧（edits.Key），片段生成时按帧插值施加


@dataclass
class FaceObs:
    """一帧的面捕结果（存进原始数据，换种族重算表情用）：478 个关键点、脸的朝向（可能没有）、表情系数。"""
    landmarks: np.ndarray
    rotation: np.ndarray | None
    blendshapes: dict[str, float]


HUMAN_KEYS = ("joint_global_rots", "joint_coords", "rest_joint_coords", "rest_joint_rots", "keypoints_2d", "focal_length", "cam_t", "bbox")


@dataclass
class Raw:
    """识别的原始结果（和种族无关）：每人每帧的 SAM 输出（去掉网格）、平滑后的重力校正、面捕。
    换种族 / 换角色骨长时只用它重做重定向、镜头、表情（build_clip，几秒），不用再跑识别。"""
    fps: float
    start: float
    times: list[float]
    image_size: tuple[int, int]
    upright: np.ndarray                          # (帧, 4) 平滑后的站姿重力校正（全员共用）
    humans: list[list[dict | None]]              # [人][帧]，键见 HUMAN_KEYS
    rest_vertices: list[np.ndarray | None]       # 每人一份零姿态网格（身体尺寸按它换算，各帧几乎一样）
    faces: list[list[FaceObs | None]]            # [人][帧]，没开表情 / 没找到脸为 None
    camera: object | None = None                 # motion.CameraSteps：原视频镜头每一步的运动（旧数据没有）
    breaks: np.ndarray | None = None             # (帧,) bool：这一帧是切镜（主角在画面里跳了位置）

    def human(self, p: int, k: int) -> dict | None:
        h = self.humans[p][k]
        if h is None:
            return None
        return {**h, "image_size": np.array(self.image_size), "rest_vertices": self.rest_vertices[p]}


def raw_to_bytes(raw: Raw) -> bytes:
    """存成 npz（压缩）：每人每个键一个 (帧, …) 数组 + 有效标记，元数据是 JSON。"""
    import io
    import json

    n = len(raw.times)
    arrays: dict[str, np.ndarray] = {"upright": raw.upright.astype(np.float32)}
    meta = {"version": 2, "fps": raw.fps, "start": raw.start, "times": raw.times, "imageSize": list(raw.image_size),
            "people": len(raw.humans), "blendshapes": []}
    if raw.camera is not None:
        arrays["cam_steps"] = raw.camera.steps.astype(np.float32)
        arrays["cam_ok"] = raw.camera.ok
        meta["camMode"] = raw.camera.mode
    if raw.breaks is not None:
        arrays["breaks"] = np.asarray(raw.breaks, bool)
    for p, frames in enumerate(raw.humans):
        valid = np.array([h is not None for h in frames])
        arrays[f"p{p}_valid"] = valid
        first = next((h for h in frames if h is not None), None)
        if first is not None:
            for key in HUMAN_KEYS:
                shape = np.shape(first[key])
                arrays[f"p{p}_{key}"] = np.stack([np.asarray(h[key], dtype=np.float32) if h is not None else np.zeros(shape, np.float32)
                                                 for h in frames])
        if raw.rest_vertices[p] is not None:
            arrays[f"p{p}_rest_vertices"] = np.asarray(raw.rest_vertices[p], dtype=np.float32)
        faces = raw.faces[p]
        have = np.array([f is not None for f in faces])
        names = sorted({k for f in faces if f is not None for k in f.blendshapes})
        meta["blendshapes"].append(names)
        if have.any():
            arrays[f"p{p}_face_valid"] = have
            arrays[f"p{p}_face_landmarks"] = np.stack([np.asarray(f.landmarks, np.float32) if f is not None else np.full((478, 3), np.nan, np.float32)
                                                      for f in faces])
            arrays[f"p{p}_face_rotation"] = np.stack([np.asarray(f.rotation, np.float32) if f is not None and f.rotation is not None
                                                     else np.full((3, 3), np.nan, np.float32) for f in faces])
            arrays[f"p{p}_face_blendshapes"] = np.array([[f.blendshapes.get(k, 0.0) if f is not None else 0.0 for k in names] for f in faces],
                                                       np.float32).reshape(n, len(names))
    arrays["meta"] = np.frombuffer(json.dumps(meta).encode("utf-8"), np.uint8)
    buffer = io.BytesIO()
    np.savez_compressed(buffer, **arrays)
    return buffer.getvalue()


def raw_from_bytes(data: bytes) -> Raw:
    import io
    import json

    with np.load(io.BytesIO(data), allow_pickle=False) as z:
        meta = json.loads(z["meta"].tobytes().decode("utf-8"))
        n, people = len(meta["times"]), int(meta["people"])
        humans, rest, faces = [], [], []
        for p in range(people):
            valid = z[f"p{p}_valid"]
            cols = {key: z[f"p{p}_{key}"] for key in HUMAN_KEYS if f"p{p}_{key}" in z}
            humans.append([{key: (float(v[k]) if key == "focal_length" else v[k]) for key, v in cols.items()} if valid[k] and cols else None
                           for k in range(n)])
            rest.append(z[f"p{p}_rest_vertices"] if f"p{p}_rest_vertices" in z else None)
            if f"p{p}_face_valid" in z:
                have, lm, rot, bs = (z[f"p{p}_face_{x}"] for x in ("valid", "landmarks", "rotation", "blendshapes"))
                names = meta["blendshapes"][p]
                faces.append([FaceObs(lm[k], None if np.isnan(rot[k]).any() else rot[k], dict(zip(names, map(float, bs[k]))))
                              if have[k] else None for k in range(n)])
            else:
                faces.append([None] * n)
        camera = None
        if "cam_steps" in z:
            from .motion import CameraSteps

            camera = CameraSteps(z["cam_steps"].astype(np.float64), z["cam_ok"].astype(bool), str(meta.get("camMode", "none")))
        breaks = z["breaks"].astype(bool) if "breaks" in z else None
        return Raw(float(meta["fps"]), float(meta["start"]), list(meta["times"]), tuple(meta["imageSize"]), z["upright"].astype(np.float64),
                   humans, rest, faces, camera, breaks)


@dataclass
class FaceHooks:
    """表情的三步（app.py 用 face / face_rig 实现，测试里换成假的）：
    analyze(rgb, human, image_size) → 这一帧的面捕结果 FaceObs，找不到或不可信时 None；
    solve(person, obs) → 面部骨架局部姿势 (位移 (n,3), 四元数 (n,4), 缩放 (n,))，用不了时 None；
    attach(person, t, q, s, head) → 挂到头上（head = j_kao 的 (位置, 四元数, 缩放)）后的骨骼 {名: (位置, 四元数, 缩放)}。"""
    analyze: Callable
    solve: Callable
    attach: Callable


@dataclass
class JobSpec:
    start: float
    end: float
    fps: float
    seed_time: float
    people: list[PersonSpec]
    smooth: float = 0.5   # 0 不平滑 ~ 1 很平滑
    detector: str = "auto"


@dataclass
class Job:
    id: str
    video_id: str
    spec: JobSpec
    state: str = "queued"      # queued / detecting / estimating / retargeting / done / error / cancelled
    done: int = 0
    total: int = 0
    error: str | None = None
    result: dict | None = None
    started: float = field(default_factory=time.monotonic)
    finished: float | None = None
    cancel: threading.Event = field(default_factory=threading.Event)
    raw: object | None = None          # Raw：识别的原始结果（换种族重算用）

    def describe(self) -> dict:
        elapsed = (self.finished or time.monotonic()) - self.started
        return {"jobId": self.id, "videoId": self.video_id, "state": self.state, "done": self.done, "total": self.total,
                "error": self.error, "elapsed": round(elapsed, 1)}


def new_job(video_id: str, spec: JobSpec) -> Job:
    return Job(uuid.uuid4().hex[:12], video_id, spec)


class Cancelled(Exception):
    pass


def run_job(job: Job, info: VideoInfo, estimate: Callable, detect_people: Callable, retarget: Callable,
            camera_local: Callable, upright_of: Callable, face: FaceHooks | None = None,
            geometry_of: Callable | None = None, hand_detect: Callable | None = None) -> None:
    """estimate(rgb, boxes) → EstimateResult；detect_people(rgb) → [框]；
    retarget(human, image_size, person_spec, upright) → RetargetResult；camera_local(human, rt) → dict；
    upright_of(human, image_size) → 四元数 xyzw 或 None（站姿重力校正，脚不在画面里 / 不是站姿时 None）；
    face：表情（只对参数里开了 face 的人做），None 不做；geometry_of(human, rt) → motion.FrameGeometry（角色走位用，None 时片段不带走位）；
    hand_detect(rgb, human) → hands.detect 的结果（手指精修），None 不做。
    结果在 job.result，原始数据在 job.raw（换种族重算用）。"""
    raw = recognize(job, info, estimate, detect_people, upright_of, face, hand_detect)
    job.raw = raw
    job.state, job.done = "retargeting", 0

    def check():
        if job.cancel.is_set():
            raise Cancelled()

    def progress(k: int):
        job.done = k

    job.result = build_clip(raw, job.spec.people, job.spec.smooth, retarget, camera_local, face, progress, check, geometry_of)


def recognize(job: Job, info: VideoInfo, estimate: Callable, detect_people: Callable, upright_of: Callable,
              face: FaceHooks | None = None, hand_detect: Callable | None = None) -> Raw:
    """1~3 步：找人跟踪、逐帧识别（+ 面捕、手指精修）、镜头运动解算、重力校正平滑。和种族无关，结果存成 Raw。"""
    from .motion import CameraTracker

    spec = job.spec
    times = sample_times(spec.start, spec.end, spec.fps)
    seed = int(np.argmin([abs(t - spec.seed_time) for t in times]))
    stop = job.cancel.is_set
    n, people = len(times), len(spec.people)

    def check():
        if stop():
            raise Cancelled()

    # 1. 每帧找人，跟踪
    job.state, job.done, job.total = "detecting", 0, n
    detections: list[list[list[float]]] = [[] for _ in range(n)]
    looks: list[list[np.ndarray]] = [[] for _ in range(n)]
    sigs: list[FrameSignature | None] = [None] * n
    seed_looks: list[np.ndarray] = []
    for k, rgb in iter_frames(info, times, stop):
        detections[k] = detect_people(rgb)
        looks[k] = [appearance(rgb, b) for b in detections[k]]
        sigs[k] = frame_signature(rgb)
        if k == seed:
            seed_looks = [appearance(rgb, p.box) for p in spec.people]
        job.done = k + 1
    check()
    boxes = track(detections, [p.box for p in spec.people], seed, lost_after=max(1, round(LOST_AFTER_SECONDS * spec.fps)),
                  appearances=looks, seed_appearances=seed_looks or None,
                  occluded_after=round(OCCLUDED_SECONDS * spec.fps) if people > 1 else 0)
    if people > 1:
        drop_box_jumps(boxes, max(1, round(BOX_WINDOW_SECONDS * spec.fps)))
    cuts = find_cuts(sigs, times, spec.fps) if all(s is not None for s in sigs) else []
    # 切镜：画面突变的帧，和主角的框一下跳开（同一场景换机位、叠化后按外观找回）的帧
    breaks = np.zeros(n, bool)
    for t in cuts:
        breaks[int(np.argmin([abs(x - t) for x in times]))] = True
    for k in range(1, n):
        a, b = boxes[k - 1][0] if people else None, boxes[k][0] if people else None
        if a is not None and b is not None and iou(a, b) < 0.1:
            breaks[k] = True

    # 2. 逐帧识别；网格顶点只用来估站姿重力，估完就丢（零姿态网格每人留一份）
    job.state, job.done = "estimating", 0
    humans: list[list[dict | None]] = [[None] * n for _ in range(people)]
    faces: list[list[FaceObs | None]] = [[None] * n for _ in range(people)]
    rest: list[np.ndarray | None] = [None] * people
    want_face = [face is not None and bool(getattr(p.params, "face", False)) for p in spec.people]
    hand_found: list[list[dict | None]] = [[None] * n for _ in range(people)]
    uprights = np.full((n, 4), np.nan)
    tracker = CameraTracker()
    for k, rgb in iter_frames(info, times, stop):
        present = [p for p in range(people) if boxes[k][p] is not None]
        focal = None
        if present:
            result = estimate(rgb, np.array([boxes[k][p] for p in present], dtype=np.float32))
            for p, h in zip(present, result.humans):
                human = _human_dict(h, result.image_size)
                if p == 0 or focal is None:
                    focal = float(human["focal_length"])
                if p == 0 and (u := upright_of(human, result.image_size)) is not None:
                    uprights[k] = u
                if rest[p] is None and human.get("rest_vertices") is not None:
                    rest[p] = np.asarray(human["rest_vertices"], dtype=np.float32)
                if want_face[p]:
                    faces[p][k] = face.analyze(rgb, human, result.image_size)
                if hand_detect is not None:
                    hand_found[p][k] = hand_detect(rgb, human)
                humans[p][k] = {key: human[key] for key in HUMAN_KEYS}
        exclude = list(detections[k]) + [b for b in boxes[k] if b is not None]
        try:
            tracker.add(rgb, exclude, focal)
        except Exception:   # 镜头解算只是锦上添花：出错就当这一帧没解出来
            tracker = _FailedTracker(tracker, n)
        job.done = k + 1
    check()

    if hand_detect is not None:
        from .hands import refine_track

        for p in range(people):
            humans[p] = refine_track(humans[p], hand_found[p], spec.fps)

    # 3. 重力校正（全员共用）：按主角站姿估、时间上重度平滑
    camera = tracker.result()
    valid = ~np.isnan(uprights[:, 0])
    if valid.any():
        upright = smooth_quats(fill_gaps(np.where(valid[:, None], uprights, 0), valid), UPRIGHT_SIGMA_SECONDS * spec.fps)
    else:
        upright = np.tile([0.0, 0.0, 0.0, 1.0], (n, 1))
    return Raw(spec.fps, spec.start, times, (info.width, info.height), upright, humans, rest, faces, camera, breaks)


class _FailedTracker:
    """镜头解算出错后的替身：之前解出来的保留，之后每一步都标成不可用。"""

    def __init__(self, tracker, n: int):
        self._tracker = tracker
        self._n = n

    def add(self, *args, **kwargs) -> None:
        pass

    def result(self):
        from .motion import CameraSteps

        r = self._tracker.result()
        m = min(len(r.ok), self._n)
        steps = np.tile([0, 0, 0, 1, 0, 0, 0], (self._n, 1)).astype(float)
        ok = np.zeros(self._n, bool)
        steps[:m], ok[:m] = r.steps[:m], r.ok[:m]
        return CameraSteps(steps, ok, r.mode)


def build_clip(raw: Raw, people: list[PersonSpec], smooth_amount: float, retarget: Callable, camera_local: Callable,
               face: FaceHooks | None = None, progress: Callable[[int], None] = lambda k: None,
               check: Callable[[], None] = lambda: None, geometry_of: Callable | None = None,
               frame_range: tuple[int, int] | None = None) -> dict:
    """4~6 步：逐帧重定向、镜头、表情，时间平滑，出片段。people 的种族 / 骨长 / 参数可以和识别时不同（换种族重算）。
    geometry_of 给了时另出主角的走位和三条镜头轨道（motion.world_tracks）。people 带时间轴修正（edits）时，
    每帧按 edits.at 插值后交给 retarget(…, edits=…)。
    frame_range=(a, b)：只重算第 a~b 帧的骨骼（改了时间轴修正后用），前后多算平滑核那么宽，结果和整段重算一样；
    返回 {"range": [a, b], "people": [{"bones", "frames"}]}，镜头、走位不算。"""
    from . import edits as edit_keys

    n = len(raw.times)
    count = min(len(people), len(raw.humans))
    upright_seq = [R.from_quat(q) for q in raw.upright]

    # 4. 逐帧重定向、镜头
    names: list[list[str] | None] = [None] * count
    frames = [np.zeros((n, 0, 10)) for _ in range(count)]
    valid_frames = np.zeros((count, n), bool)
    cams: list[dict | None] = [None] * n
    want_face = [face is not None and bool(getattr(people[p].params, "face", False)) for p in range(count)]
    face_locals: list[list[tuple | None]] = [[None] * n for _ in range(count)]
    geos: list = [None] * n
    person_geos: list[list] = [[None] * n for _ in range(count)]   # 每个人每帧的几何（多人站位用）
    sigma = float(np.clip(smooth_amount, 0, 1)) * 0.12 * raw.fps   # 最多 0.12 秒的高斯
    window = range(n)
    if frame_range is not None:
        first, last = max(0, frame_range[0]), min(n - 1, frame_range[1])
        margin = len(_gaussian(max(sigma, FACE_MIN_SIGMA_SECONDS * raw.fps))) // 2 + 1
        window = range(max(0, first - margin), min(n, last + margin + 1))
    for k in window:
        check()
        for p in range(count):
            human = raw.human(p, k)
            if human is None:
                continue
            keys = people[p].edits
            frame_edits = edit_keys.at(keys, k / raw.fps) if keys else None
            if frame_edits:
                rt = retarget(human, raw.image_size, people[p], upright_seq[k], edits=frame_edits)
            else:
                rt = retarget(human, raw.image_size, people[p], upright_seq[k])
            if names[p] is None:
                names[p] = list(rt.bones)
                frames[p] = np.zeros((n, len(names[p]), 10))
            for b, name in enumerate(names[p]):
                if name in rt.bones:
                    pos, q, sc = rt.bones[name]
                    frames[p][k, b] = np.r_[pos, q, sc]
            valid_frames[p, k] = True
            if frame_range is not None:
                if want_face[p] and raw.faces[p][k] is not None:
                    face_locals[p][k] = face.solve(p, raw.faces[p][k])
                continue
            if p == 0:
                cams[k] = camera_local(human, rt)
            if geometry_of is not None and (p == 0 or count > 1):
                person_geos[p][k] = geometry_of(human, rt)
                if p == 0:
                    geos[k] = person_geos[p][k]
            if want_face[p] and raw.faces[p][k] is not None:
                face_locals[p][k] = face.solve(p, raw.faces[p][k])
        progress(k + 1)

    if frame_range is not None:
        return _clip_window(raw, people, face, names, frames, valid_frames, face_locals, want_face, sigma, window, (first, last))

    # 5~6. 表情挂到头上，平滑
    face_frames = [0] * count
    for p in range(count):
        if names[p] is None or "j_kao" not in names[p]:
            continue
        added = _add_face(face, p, face_locals[p], frames[p], names[p], valid_frames[p], max(sigma, FACE_MIN_SIGMA_SECONDS * raw.fps))
        if added is not None:
            frames[p], names[p], face_frames[p] = added
    clip_people = []
    for p in range(count):
        if names[p] is None:
            clip_people.append({"bones": [], "frames": [], "missing": [True] * n, "race": people[p].race})
            continue
        sm = smooth_frames(frames[p], valid_frames[p], sigma)
        clip_people.append({"bones": names[p], "frames": np.round(sm.reshape(n, -1), 6).tolist(),
                            "missing": (~valid_frames[p]).tolist(), "race": people[p].race,
                            "face": {"enabled": want_face[p], "frames": face_frames[p]}})
    # 配角相对主角的站位（第一个人是主角：镜头、走位都按他）
    if geometry_of is not None and count > 1:
        from .motion import person_offsets

        for p in range(1, count):
            if not clip_people[p]["bones"]:
                continue
            offset, together = person_offsets(person_geos[0], person_geos[p], raw.fps, sigma)
            clip_people[p]["offset"] = np.round(offset, 5).tolist()
            clip_people[p]["together"] = int(together.sum())
    camera = smooth_camera(cams, sigma * 1.5)
    rounded = lambda cams_: [{k: (np.round(v, 6).tolist() if isinstance(v, list) else round(v, 4)) for k, v in c.items()} for c in cams_]
    result = {
        "version": 1, "fps": raw.fps, "start": raw.start, "times": raw.times, "imageSize": list(raw.image_size),
        "smooth": smooth_amount, "people": clip_people,
        "camera": rounded(camera),
    }
    if geometry_of is not None and camera and names[0] is not None and any(g is not None for g in geos):
        from .motion import world_tracks

        sm0 = np.array(clip_people[0]["frames"]).reshape(n, len(names[0]), 10)
        tracks = world_tracks(raw, geos, names[0], sm0, valid_frames[0], camera, sigma)
        result["root"] = np.round(tracks["root"], 5).tolist()
        result["cameras"] = {k: rounded(v) for k, v in tracks["cameras"].items()}
        result["motion"] = tracks["info"]
        # 配角也按踩着的脚推站位（换到场景里推，再换回相对主角）
        from .motion import partner_steps

        for p in range(1, count):
            person = clip_people[p]
            if "offset" not in person or not person["bones"]:
                continue
            fr = np.array(person["frames"]).reshape(n, len(person["bones"]), 10)
            feet = {b: fr[:, person["bones"].index(b), 0:3] for b in ("j_asi_d_l", "j_asi_e_l", "j_asi_d_r", "j_asi_e_r")
                    if b in person["bones"]}
            offset, before, after = partner_steps(raw, p, tracks["root"], np.array(person["offset"]), feet, raw.fps, tracks["breaks"])
            person["offset"] = np.round(offset, 5).tolist()
            person["footSlide"] = [round(before, 3), round(after, 3)]
    return result


def _clip_window(raw: Raw, people: list[PersonSpec], face: FaceHooks | None, names: list, frames: list, valid: np.ndarray,
                 face_locals: list, want_face: list[bool], sigma: float, window: range, keep: tuple[int, int]) -> dict:
    """build_clip 只算一段时：在窗口里挂表情、平滑，只交回第 keep 段的帧。"""
    w = slice(window.start, window.stop)
    a, b = keep[0] - window.start, keep[1] - window.start + 1
    out = []
    for p in range(len(names)):
        if names[p] is None:
            out.append({"bones": [], "frames": []})
            continue
        fr, bones = frames[p][w], names[p]
        if want_face[p] and "j_kao" in bones:
            added = _add_face(face, p, face_locals[p][w], fr, bones, valid[p][w], max(sigma, FACE_MIN_SIGMA_SECONDS * raw.fps))
            if added is not None:
                fr, bones, _ = added
        sm = smooth_frames(fr, valid[p][w], sigma)[a:b]
        out.append({"bones": bones, "frames": np.round(sm.reshape(len(sm), -1), 6).tolist(), "race": people[p].race})
    return {"range": list(keep), "people": out}


def _add_face(face: FaceHooks | None, person: int, locals_: list[tuple | None], frames: np.ndarray, names: list[str],
              valid: np.ndarray, sigma: float) -> tuple[np.ndarray, list[str], int] | None:
    """面部骨架的局部姿势补空帧、平滑后挂到每帧的头上，追加到骨骼帧后面。返回 (骨骼帧, 骨骼名, 用上表情的帧数)；一帧都没有时 None。"""
    have = np.array([x is not None for x in locals_])
    if face is None or not have.any():
        return None
    first = locals_[int(np.flatnonzero(have)[0])]
    t = np.stack([x[0] if x is not None else np.zeros_like(first[0]) for x in locals_])
    q = np.stack([x[1] if x is not None else np.tile([0.0, 0.0, 0.0, 1.0], (len(first[1]), 1)) for x in locals_])
    t = smooth(fill_gaps(t, have), sigma)
    q = fill_gaps(q, have)
    q /= np.linalg.norm(q, axis=-1, keepdims=True).clip(1e-9)
    q = smooth_quats(q, sigma)
    kao = names.index("j_kao")
    extra_names: list[str] | None = None
    extra = None
    for k in np.flatnonzero(valid):
        h = frames[k, kao]
        bones = face.attach(person, t[k], q[k], first[2], (h[0:3], h[3:7], h[7:10]))
        if extra_names is None:
            extra_names = [b for b in bones if b not in names]
            extra = np.zeros((len(frames), len(extra_names), 10))
        for i, b in enumerate(extra_names):
            pos, rot, sc = bones[b]
            extra[k, i] = np.r_[pos, rot, np.broadcast_to(np.asarray(sc, dtype=np.float64).reshape(-1), (3,))]
    if not extra_names:
        return None
    return np.concatenate([frames, extra], axis=1), names + extra_names, int(have.sum())


def _human_dict(h, image_size) -> dict:
    return {"joint_global_rots": h.joint_global_rots, "joint_coords": h.joint_coords,
            "rest_joint_coords": h.rest_joint_coords, "rest_joint_rots": h.rest_joint_rots,
            "keypoints_2d": h.keypoints_2d, "focal_length": h.focal_length, "cam_t": h.cam_t,
            "bbox": h.bbox, "image_size": np.array(image_size),
            "vertices": h.extra["vertices"], "rest_vertices": h.extra["rest_vertices"]}
