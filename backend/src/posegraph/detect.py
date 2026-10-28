"""人物检测（合并模式：一张图里有几个人）。

两个 ONNX 模型，用 onnxruntime 在 CPU 上跑（几十到一两百毫秒，不占显存）：
- photo：Ultralytics YOLO11m（COCO 第 0 类 person）。真人照片准，插画常漏检
- anime：deepghs/anime_person_detection v1.3_s（单类 person）。插画准，但会把照片里的狗、器材当成人
kind=auto 先用 photo，一个人都没找到再用 anime。两个模型输出格式相同：(1, 4 + 类别数, 8400)，前 4 行是框的中心与宽高。
"""

from __future__ import annotations

import os
import threading
import time
from dataclasses import dataclass

import numpy as np

from .config import settings
from .i18n import tr

SIZE = 640          # 模型输入边长（等比缩放后四周补灰）
NMS_IOU = 0.5       # 重叠超过这个比例的框只留分高的
MIN_SIDE = 16       # 太小的框（原图像素）不算人


@dataclass(frozen=True)
class _Spec:
    file: str
    threshold: float
    person_class: int = 0


DETECTORS = {
    "photo": _Spec("yolo11m.onnx", 0.25),
    "anime": _Spec("anime_person_v1.3_s.onnx", 0.324),   # 阈值取模型自带 threshold.json（F1 最优）
}
KINDS = ("auto", *DETECTORS)

_sessions: dict = {}
_lock = threading.Lock()


def _runtime() -> bool:
    import importlib.util

    return importlib.util.find_spec("onnxruntime") is not None


def available() -> dict[str, bool]:
    """各检测模型能不能用：模型文件在、onnxruntime 装了（旧一键包没有）。"""
    runtime = _runtime()
    return {kind: runtime and (settings.detector_dir / spec.file).is_file() for kind, spec in DETECTORS.items()}


def _session(kind: str):
    with _lock:
        if kind not in _sessions:
            if not _runtime():
                raise FileNotFoundError(tr("后端缺少人物检测组件（onnxruntime），请更新一键包"))
            import onnxruntime as ort

            path = settings.detector_dir / DETECTORS[kind].file
            if not path.is_file():
                raise FileNotFoundError(tr("找不到人物检测模型：{path}", path=path))
            options = ort.SessionOptions()
            options.intra_op_num_threads = max(1, (os.cpu_count() or 4) // 2)   # 别把 CPU 占满，游戏还在跑
            # 路径可能含中文：读成字节再交给 onnxruntime
            _sessions[kind] = ort.InferenceSession(path.read_bytes(), options, providers=["CPUExecutionProvider"])
        return _sessions[kind]


def unload() -> None:
    """释放检测模型（与姿势模型一起按释放策略卸载）。"""
    with _lock:
        _sessions.clear()


def _letterbox(rgb: np.ndarray) -> tuple[np.ndarray, float, int, int]:
    import cv2

    h, w = rgb.shape[:2]
    r = SIZE / max(h, w)
    nw, nh = max(1, round(w * r)), max(1, round(h * r))
    canvas = np.full((SIZE, SIZE, 3), 114, np.uint8)
    left, top = (SIZE - nw) // 2, (SIZE - nh) // 2
    canvas[top:top + nh, left:left + nw] = cv2.resize(rgb, (nw, nh), interpolation=cv2.INTER_LINEAR)
    return canvas.transpose(2, 0, 1)[None].astype(np.float32) / 255, r, left, top


def _nms(boxes: np.ndarray, scores: np.ndarray, iou: float) -> list[int]:
    order = scores.argsort()[::-1]
    area = (boxes[:, 2] - boxes[:, 0]) * (boxes[:, 3] - boxes[:, 1])
    keep = []
    while order.size:
        i = order[0]
        keep.append(int(i))
        x1 = np.maximum(boxes[i, 0], boxes[order[1:], 0])
        y1 = np.maximum(boxes[i, 1], boxes[order[1:], 1])
        x2 = np.minimum(boxes[i, 2], boxes[order[1:], 2])
        y2 = np.minimum(boxes[i, 3], boxes[order[1:], 3])
        inter = np.clip(x2 - x1, 0, None) * np.clip(y2 - y1, 0, None)
        order = order[1:][inter / (area[i] + area[order[1:]] - inter + 1e-9) <= iou]
    return keep


def _detect(rgb: np.ndarray, kind: str) -> list[dict]:
    spec = DETECTORS[kind]
    session = _session(kind)
    x, r, left, top = _letterbox(rgb)
    out = session.run(None, {session.get_inputs()[0].name: x})[0][0]   # (4 + 类别数, 候选数)
    scores = out[4 + spec.person_class]
    keep = scores >= spec.threshold
    cx, cy, bw, bh = out[:4, keep]
    h, w = rgb.shape[:2]
    boxes = np.stack([(cx - bw / 2 - left) / r, (cy - bh / 2 - top) / r,
                      (cx + bw / 2 - left) / r, (cy + bh / 2 - top) / r], axis=1)
    boxes = np.clip(boxes, 0, [w, h, w, h])
    scores = scores[keep]
    people = []
    for i in _nms(boxes, scores, NMS_IOU):
        b = boxes[i]
        if min(b[2] - b[0], b[3] - b[1]) >= MIN_SIDE:
            people.append({"bbox": [round(float(v), 1) for v in b], "score": round(float(scores[i]), 3), "source": kind})
    return people


def detect(rgb: np.ndarray, kind: str = "auto") -> dict:
    """返回 {"people": [...], "kind": 实际用的模型, "elapsedMs": ...}。人物按框的中心从左到右编号。"""
    if kind not in KINDS:
        raise ValueError(kind)
    t0 = time.perf_counter()
    used = "photo" if kind == "auto" else kind
    people = _detect(rgb, used)
    if kind == "auto" and not people and available()["anime"]:
        used, people = "anime", _detect(rgb, "anime")
    people.sort(key=lambda p: (p["bbox"][0] + p["bbox"][2]) / 2)
    return {"people": people, "kind": used, "elapsedMs": round((time.perf_counter() - t0) * 1000, 1)}
