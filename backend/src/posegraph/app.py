"""本地识别服务（FastAPI），供 Posegraph 插件界面调用。

运行：python -m posegraph.app

接口：
  GET  /health                         服务、模型、显存状态
  POST /model/load | /model/unload     预加载 / 释放模型
  POST /model/config                   设备（auto/cuda/cpu）、释放策略（识别后空闲多少秒释放，0 立即、-1 常驻）、MHR 是否放 CPU
  POST /sessions?name=xx               请求体为图片原始字节（jpg/png/webp/bmp/gif 首帧/tiff/avif），识别一次并缓存
                                       boxes=x1,y1,x2,y2;… 只识别这些人物框（合并模式），不给时整张图当一个人
  POST /detect?kind=auto               找出图里的人（真人 photo / 插画 anime / 自动），不跑姿势模型
  GET  /sessions/{id}/image            预览图（PNG，长边不超过 1280）
  DELETE /sessions/{id}                删除图片及预识别缓存
  POST /sessions/{id}/retarget         按参数重定向（只算重定向，几十毫秒），返回 Brio 姿势、叠加层、火柴人、合理性提示
  POST /sessions/{id}/camera           按照片镜头设置 GPose 相机（经 Posegraph HTTP 闭环求解）
  POST /sessions/{id}/views            挂上辅助视角（别的会话里识别好的同一姿势），重定向默认用融合结果（multiview.py）
  GET  /presets                        参数预设
  GET  /races                          支持的种族骨架（retarget/races.py）
  POST /shutdown                       退出服务

会话里的 humans 是多人：person 为人物序号（合并模式按 boxes 的顺序；不给 boxes 时整图按一个人识别）。
"""

from __future__ import annotations

import io
import threading
import time
import uuid

import numpy as np
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import Response
from starlette.concurrency import run_in_threadpool
from pydantic import BaseModel, Field

from . import depth, detect, masks, session_archive, tuning
from .config import SERVICE_ROOT, settings
from .estimators import create_estimator
from . import RUNTIME_REQUIRED, __version__
from .gpu import query_gpu
from .i18n import set_language, tr
from .retarget import races
from .retarget.brio_pose import BrioPose
from .retarget.ffxiv import load_mhr_rest, to_brio_pose_json
from .session_cache import Session, SessionCache
from .i18n import _lang
from .parallel_batch import ParallelBatch
from .shared_inference import ModelLifecycle

app = FastAPI(title="posegraph", version=__version__)
_estimator = create_estimator()
# 串行识别：加载、推理、释放互斥（释放不能打断正在进行的识别）
_model_lock = threading.RLock()
# 并行批量与串行共用：卸载、改配置时等正在进行的识别结束
_model_lifecycle = ModelLifecycle()
_model_state = {"loading": False, "error": None}
_release_after = settings.release_after
_use_depth = settings.use_depth
_last_used = time.monotonic()
# 插件批量识别（主图 + 辅助视角、拆分模式全部应用、批量队列）时请求带 hold：这张识别完先不释放，
# 整批结束插件再调 /model/unload。插件中途退出没调时，按 HOLD_TIMEOUT 秒空闲兜底释放
_held = False
HOLD_TIMEOUT = 60


def _shared_config() -> dict:
    return {key: getattr(_estimator, key) for key in
            ("device", "precision", "share_mhr", "mhr_on_cpu", "min_free_vram_mb")
            if hasattr(_estimator, key)}


# 批量识别的共享模型并行（实验）：只处理标了 parallel 的请求，出问题交回串行，见 parallel_batch.py
_parallel = ParallelBatch(settings.recognition_parallelism, _shared_config(),
                          disabled="POSEGRAPH_DISABLE_PARALLEL=1" if settings.disable_parallel else None)

PREVIEW_MAX_SIDE = 1280
MAX_SESSIONS = 8
OUTPUTS = SERVICE_ROOT / "outputs" / "ui"


# 大批图片只保留最近使用的 8 份在内存，其余暂存磁盘；选择时直接恢复，不再推理。
_sessions = SessionCache(MAX_SESSIONS)


@app.middleware("http")
async def _language(request: Request, call_next):
    """插件在 Accept-Language 里带界面语言，返回的提示 / 错误按它翻译（i18n.py）。"""
    set_language(request.headers.get("accept-language"))
    return await call_next(request)


def _load_model() -> None:
    with _model_lock:
        if _estimator.loaded:
            return
        _model_state.update(loading=True, error=None)
        try:
            _estimator.load()
        except Exception as ex:  # 显存不足、权重缺失等都要让界面看到
            _model_state["error"] = f"{type(ex).__name__}: {ex}"
            raise
        finally:
            _model_state["loading"] = False


def _unload_model() -> None:
    global _held
    with _model_lifecycle.exclusive(), _model_lock:
        _parallel.close_if_idle()
        _estimator.unload()
        detect.unload()
        _held = False


def _release_loop() -> None:
    """按策略释放模型：识别后空闲超过 release_after 秒就卸载（0 在识别结束时立即卸载，见 _estimate）。"""
    while True:
        time.sleep(2)
        try:
            _release_idle_models()
        except Exception as error:
            _model_state["error"] = f"{type(error).__name__}: {error}"


def _release_idle_models() -> None:
    global _held
    limit = lambda: HOLD_TIMEOUT if _release_after == 0 and _held else _release_after
    idle = lambda: (limit() > 0 and (_estimator.loaded or _parallel.loaded) and not _model_state["loading"]
                    and not _parallel.active and time.monotonic() - _last_used > limit())
    if idle():
        with _model_lifecycle.exclusive(), _model_lock:   # 拿到锁时可能刚识别完，再判断一次
            if idle():
                _parallel.close_if_idle()
                _estimator.unload()
                detect.unload()
                _held = False


def _model_info() -> dict:
    parallel = _parallel.info()
    info = {"loaded": _estimator.loaded or parallel["loaded"], "loading": _model_state["loading"], "error": _model_state["error"],
            "device": getattr(_estimator, "active_device", None), "precision": getattr(_estimator, "active_precision", None),
            "deviceReason": _reason(getattr(_estimator, "device_reason", ("",))),
            "config": {"device": getattr(_estimator, "device", None), "releaseAfter": _release_after,
                       "mhrOnCpu": getattr(_estimator, "mhr_on_cpu", None), "fast": getattr(_estimator, "fast", None),
                       "backboneScale": getattr(_estimator, "backbone_scale", None),
                       "minFreeVramMb": getattr(_estimator, "min_free_vram_mb", None),
                       "recognitionParallelism": parallel["requested"], "useDepth": _use_depth},
            "depthAvailable": depth.available(),
            "parallel": parallel}
    shared = next((w for w in parallel["workers"] if w["loaded"]), None)
    if shared and not _estimator.loaded:   # 批量并行时显示共享模型的设备
        info.update(device=shared["device"], precision=shared["precision"], deviceReason=_reason(shared["device_reason"]))
    info["parallel"]["workers"] = [{**w, "device_reason": _reason(w["device_reason"])} for w in parallel["workers"]]
    return info


def _reason(r) -> str:
    """估计器的设备选择原因：(中文模板, 参数) → 当前语言的文字。"""
    if isinstance(r, str):
        return r
    return tr(r[0], **(r[1] if len(r) > 1 else {})) if r[0] else ""


@app.get("/health")
def health() -> dict:
    # 显卡信息走 nvidia-smi：模型不在 GPU 上时不能为了查询去初始化 CUDA（会常驻几百 MB 显存）
    return {"status": "ok", "version": app.version, "runtime": RUNTIME_REQUIRED, "estimator": _estimator.name, "model": _model_info(),
            "gpu": query_gpu(), "sessions": len(_sessions),
            "detectors": detect.available()}


@app.post("/model/load")
def model_load() -> dict:
    if not _estimator.loaded and not _model_state["loading"]:
        threading.Thread(target=lambda: _safe(_load_model), daemon=True).start()
    return {"model": _model_info()}


def _safe(fn) -> None:
    try:
        fn()
    except Exception:
        pass


@app.post("/model/unload")
def model_unload() -> dict:
    _unload_model()
    return {"model": _model_info()}


class ModelConfig(BaseModel):
    device: str | None = None          # auto / cuda / cpu
    release_after: float | None = None  # 秒；0 立即释放，-1 常驻
    mhr_on_cpu: bool | None = None
    min_free_vram_mb: int | None = None
    recognition_parallelism: int | None = Field(default=None, ge=1, le=8, strict=True)
    use_depth: bool | None = None
    fast: bool | None = None
    backbone_scale: float | None = Field(default=None, ge=0.5, le=1.0)


@app.post("/model/config")
def model_config(cfg: ModelConfig) -> dict:
    """运行时修改设备与释放策略。设备相关的改动在下次加载时生效（已加载的模型会先卸载）。
    recognition_parallelism 只影响之后标了 parallel 的批量请求。"""
    global _release_after, _held, _use_depth
    if cfg.device is not None and cfg.device not in ("auto", "cuda", "cpu"):
        raise HTTPException(400, tr("device 只能是 auto / cuda / cpu"))
    with _model_lifecycle.exclusive(), _model_lock:
        changes = {key: getattr(cfg, key) for key in ("device", "mhr_on_cpu", "min_free_vram_mb", "fast", "backbone_scale")
                   if getattr(cfg, key) is not None and getattr(_estimator, key) != getattr(cfg, key)}
        if changes:
            _estimator.unload()
            _held = False
        for key, value in changes.items():
            setattr(_estimator, key, value)
        requested = cfg.recognition_parallelism or _parallel.requested
        if changes or requested != _parallel.requested:
            _parallel.configure(requested, _shared_config())
        if cfg.release_after is not None:
            _release_after = cfg.release_after
        if cfg.use_depth is not None:
            _use_depth = cfg.use_depth
            if not _use_depth:
                depth.unload()   # 关掉后立刻还显存 / 内存
    return {"model": _model_info()}


def _decode(data: bytes) -> np.ndarray:
    """任意常见格式 → RGB。按 EXIF 方向摆正（手机照片），GIF/动图取首帧，透明背景垫白。"""
    from PIL import Image, ImageOps

    try:
        img = Image.open(io.BytesIO(data))
        img.seek(0)
        img = ImageOps.exif_transpose(img)
    except Exception as ex:
        raise HTTPException(400, tr("无法解码图片：{error}", error=ex))
    if img.mode in ("RGBA", "LA", "P"):
        img = img.convert("RGBA")
        bg = Image.new("RGBA", img.size, (255, 255, 255, 255))
        img = Image.alpha_composite(bg, img)
    return np.asarray(img.convert("RGB"))


def _preview_png(rgb: np.ndarray) -> bytes:
    from PIL import Image

    img = Image.fromarray(rgb)
    img.thumbnail((PREVIEW_MAX_SIDE, PREVIEW_MAX_SIDE))
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def _session(sid: str) -> Session:
    try:
        return _sessions[sid]
    except KeyError:
        raise HTTPException(404, tr("会话不存在（后端重启过？请重新识别）"))


@app.post("/sessions")
async def create_session(request: Request, name: str = "image", hold: bool = False, parallel: bool = False,
                         boxes: str | None = None, mask_width: int | None = None, mask_height: int | None = None) -> dict:
    """hold：后面还有同一批的图要识别，这张识别完先不按「立即释放」卸载模型。
    parallel：插件批量队列的请求，可以共享一份模型并行识别（实验，见 parallel_batch.py）。
    boxes：合并模式里选中的人物框「x1,y1,x2,y2;…」（原图像素，通常来自 /detect）；会话里的人物按这个顺序。
    不给时整张图当一个人。
    请求体是图片；带人物遮罩时改用 multipart：image（图片）+ masks（标签图，见 masks.py，mask_width × mask_height），
    标签 k + 1 为第 k 个框的人。"""
    labels = None
    if request.headers.get("content-type", "").startswith("multipart/form-data"):
        form = await request.form()
        image, packed = form.get("image"), form.get("masks")
        data = await image.read() if image is not None and hasattr(image, "read") else b""
        if packed is not None and hasattr(packed, "read"):
            if boxes is None or not mask_width or not mask_height:
                raise HTTPException(400, tr("人物遮罩要和人物框、遮罩尺寸一起给"))
            try:
                labels = masks.decode(await packed.read(), mask_width, mask_height, boxes.count(";") + 1)
            except ValueError as ex:
                raise HTTPException(400, tr("人物遮罩不对：{error}", error=ex))
    else:
        data = await request.body()
    if not data:
        raise HTTPException(400, tr("请求体为空"))
    # 识别约几秒，放到线程池里跑，期间 /health 仍能响应
    rgb, result = await run_in_threadpool(_estimate, data, hold, parallel, boxes, labels)
    humans = []
    for h in result.humans:
        humans.append({"joint_global_rots": h.joint_global_rots, "joint_coords": h.joint_coords,
                       "rest_joint_coords": h.rest_joint_coords, "rest_joint_rots": h.rest_joint_rots,
                       "keypoints_2d": h.keypoints_2d, "focal_length": h.focal_length, "cam_t": h.cam_t,
                       "bbox": h.bbox, "image_size": np.array(result.image_size),
                       "vertices": h.extra["vertices"], "rest_vertices": h.extra["rest_vertices"],
                       **({"floor": h.extra["floor"]} if "floor" in h.extra else {})})
    session = await run_in_threadpool(_add_session, name, result.image_size, _preview_png(rgb), humans, result.timings_ms["inference"])

    if settings.save_sessions:   # 存一份 npz，命令行脚本（push_pose / preview_overlay / Blender 对比）可直接复用
        OUTPUTS.mkdir(parents=True, exist_ok=True)
        np.savez(OUTPUTS / f"{time.strftime('%Y%m%d-%H%M%S')}_{name}.npz", **humans[0],
                 faces=result.humans[0].extra["faces"])
    return _session_info(session, result.device)


DEPTH_WORK_SIDE = 1024   # 深度、遮罩、网格投影在这么大的图上算（原图大时缩小，省时间）


def _estimate_with_depth(rgb: np.ndarray, bboxes: np.ndarray, person_masks: np.ndarray | None):
    """深度帮识别（见 depth.py）。镜头用深度模型的焦距；给了遮罩时按深度修遮罩，变化够大再识别一次；
    每个人整体缩放到照片深度对应的远近；找地面，结果记在每个人的 "floor"（法线 xyz、d、是否都在地上，相机系、SAM 尺度）。"""
    import cv2

    from . import depth

    H, W = rgb.shape[:2]
    sc = min(1.0, DEPTH_WORK_SIDE / max(W, H))
    size = (max(1, round(W * sc)), max(1, round(H * sc)))
    small = cv2.resize(rgb, size, interpolation=cv2.INTER_AREA) if sc < 1 else rgb
    photo = depth.estimate(small, _estimator.active_device or "cpu")
    focal = photo.focal / sc
    faces = _estimator.faces()
    shrink = lambda m: np.stack([cv2.resize(x.astype(np.uint8), size, interpolation=cv2.INTER_NEAREST) for x in m]).astype(bool)
    meshes = lambda r: [(np.asarray(h.extra["vertices"]) + np.asarray(h.cam_t).reshape(1, 3)) * sc for h in r.humans]

    result = _estimator.estimate(rgb, bboxes, person_masks, focal=focal)
    if person_masks is not None:
        work = shrink(person_masks)
        scale = depth.depth_scale(meshes(result), work, photo, faces, size)
        refined = depth.refine_masks(meshes(result), work, photo, scale, faces, size)
        if depth.masks_changed(work, refined):
            full = np.stack([cv2.resize(m.astype(np.uint8), (W, H), interpolation=cv2.INTER_NEAREST) for m in refined])
            result = _estimator.estimate(rgb, bboxes, full, focal=focal)
        work = refined   # 没重跑也用修过的遮罩算远近、找地面（就是按这次的网格分的像素）
    else:   # 没遮罩时用网格自己的投影当「看得见的部分」
        humans = [{"vertices": h.extra["vertices"], "cam_t": h.cam_t, "focal_length": h.focal_length} for h in result.humans]
        work = shrink(masks.per_person(masks.render_labels(humans, (W, H), faces), (W, H), len(humans)))
        scale = depth.depth_scale(meshes(result), work, photo, faces, size)
    ratios = depth.distance_ratios(meshes(result), work, photo, scale, faces, size)
    for h, r in zip(result.humans, ratios):   # 以镜头为中心整体缩放：照片上不变，远近对上深度
        h.joint_coords, h.cam_t, h.keypoints_3d = h.joint_coords * r, h.cam_t * r, h.keypoints_3d * r
        h.rest_joint_coords = h.rest_joint_coords * r
        h.extra["vertices"] = h.extra["vertices"] * r
        h.extra["rest_vertices"] = h.extra["rest_vertices"] * r
    feet = []   # 看得见的脚（踝、脚跟、脚尖）在深度图上的像素，用来认出脚踩着的那个平面
    for h in result.humans:
        for i in (13, 14, 15, 17, 18, 20):
            x, y = (float(v) * sc for v in np.asarray(h.keypoints_2d)[i][:2])
            if 0 <= x < size[0] and 0 <= y < size[1]:
                feet.append((int(x), int(y)))
    torsos = [(k[5] + k[6]) / 2 - (k[9] + k[10]) / 2 for k in (np.asarray(h.keypoints_3d)[:, :3] for h in result.humans)]
    plane = depth.find_floor(photo, work, feet, torsos)
    if plane is not None:
        n, d = plane[0], plane[1] * scale
        lows = [float(((np.asarray(h.extra["vertices"]) + np.asarray(h.cam_t).reshape(1, 3)) @ n).min() + d) for h in result.humans]
        floor = np.r_[n, d, float(max(lows) < depth.ON_FLOOR)].astype(np.float32)
        for h in result.humans:
            h.extra["floor"] = floor
    return result


def _add_session(name: str, image_size, preview_png: bytes, humans: list[dict], inference_ms: float) -> Session:
    sid = uuid.uuid4().hex[:12]
    session = Session(sid, name, tuple(image_size), preview_png, humans, inference_ms)
    _sessions[sid] = session
    return session


def _session_info(s: Session, device: str | None) -> dict:
    return {"sessionId": s.id, "name": s.name, "imageSize": list(s.image_size),
            "inferenceMs": round(s.inference_ms), "device": device,
            "humans": [{"bbox": [round(float(v), 1) for v in h["bbox"]]} for h in s.humans]}


@app.get("/sessions/{sid}/archive")
def session_archive_get(sid: str) -> Response:
    """会话存档（npz），插件存进图库的识别缓存。"""
    s = _session(sid)
    return Response(session_archive.pack(s.name, s.image_size, s.preview_png, s.humans, s.inference_ms),
                    media_type="application/octet-stream")


@app.post("/sessions/archive")
async def session_archive_restore(request: Request, name: str | None = None) -> dict:
    """从识别缓存恢复会话，不加载模型。"""
    try:
        a = session_archive.unpack(await request.body())
    except ValueError as ex:
        raise HTTPException(400, tr("识别缓存无法读取：{error}", error=ex))
    if not a["humans"]:
        raise HTTPException(400, tr("识别缓存无法读取：{error}", error="humans"))
    session = await run_in_threadpool(_add_session, name or a["name"], a["image_size"], a["preview_png"], a["humans"], a["inference_ms"])
    return _session_info(session, None)


@app.delete("/sessions/{sid}")
def session_delete(sid: str) -> dict:
    _sessions.discard(sid)
    return {"ok": True}


def _parse_boxes(text: str, width: int, height: int) -> np.ndarray:
    try:
        boxes = np.array([[float(v) for v in part.split(",")] for part in text.split(";") if part.strip()], dtype=np.float32)
    except ValueError:
        boxes = np.zeros((0, 3))
    if boxes.ndim != 2 or boxes.shape[1] != 4 or len(boxes) == 0:
        raise HTTPException(400, tr("人物框格式不对，应为 x1,y1,x2,y2;…"))
    boxes = np.clip(boxes, 0, [width, height, width, height])
    if np.any(boxes[:, 2] - boxes[:, 0] < detect.MIN_SIDE) or np.any(boxes[:, 3] - boxes[:, 1] < detect.MIN_SIDE):
        raise HTTPException(400, tr("人物框太小或超出图片范围"))
    return boxes


def _estimate(data: bytes, hold: bool = False, parallel: bool = False, boxes: str | None = None,
              labels: np.ndarray | None = None):
    global _last_used, _held
    rgb = _decode(data)
    bboxes = None if boxes is None else _parse_boxes(boxes, rgb.shape[1], rgb.shape[0])
    person_masks = None if labels is None else masks.per_person(labels, (rgb.shape[1], rgb.shape[0]), len(bboxes))
    with _model_lifecycle.inference():
        result = None
        with _model_lock:
            # 批量并行正在跑时，夹在中间的单张也交给共享模型，内存里不同时放两份模型；
            # 指定了人物框（合并模式）的请求只走串行（共享模型不接收框）
            shared = bboxes is None and (_parallel.wanted(parallel) or not _parallel.close_if_idle())
            if shared:
                _estimator.unload()
                _held = _held or hold
        if shared:
            try:
                result = _parallel.estimate(rgb, _lang.get())   # None：共享模型不可用或这张出错，交回串行重试
            finally:
                with _model_lock:
                    _last_used = time.monotonic()
                    if _release_after == 0 and not _held and not _parallel.active:
                        _parallel.close_if_idle()
        if result is None:
            with _model_lock:
                _parallel.close_if_idle()
                try:
                    _load_model()
                except Exception as ex:
                    raise HTTPException(503, tr("模型加载失败：{error}", error=ex))
                try:
                    if _use_depth and depth.available():
                        result = _estimate_with_depth(rgb, bboxes, person_masks)
                    else:
                        result = _estimator.estimate(rgb, bboxes, person_masks)
                finally:
                    _last_used = time.monotonic()
                    _held = hold
                    if _release_after == 0 and not hold:
                        _estimator.unload()
    if not result.humans:
        raise HTTPException(422, tr("未检测到人物"))
    return rgb, result


@app.post("/detect")
async def detect_people(request: Request, kind: str = "auto") -> dict:
    """合并模式：找出图里的人（不跑姿势模型，几十到一两百毫秒）。kind：auto / photo（真人）/ anime（插画）。
    返回的人物从左到右编号；插件让用户增删、调整、勾选后，把要识别的框传给 POST /sessions?boxes=…"""
    if kind not in detect.KINDS:
        raise HTTPException(400, tr("kind 只能是 auto / photo / anime"))
    data = await request.body()
    if not data:
        raise HTTPException(400, tr("请求体为空"))
    rgb = await run_in_threadpool(_decode, data)
    try:
        found = await run_in_threadpool(detect.detect, rgb, kind)
    except FileNotFoundError as ex:
        raise HTTPException(503, str(ex))
    return {"imageSize": [rgb.shape[1], rgb.shape[0]], **found}


@app.get("/sessions/{sid}/masks")
def session_masks(sid: str, render: bool = True) -> dict:
    """合并模式的人物遮罩（见 masks.py）：把会话里每个人的识别网格投影到照片上，近的挡住远的。
    返回标签图与同尺寸的照片像素（插件的魔棒按它取色），都是按行展开、zlib 压缩、base64。
    render=false 时不投影，标签全是 0（插件从空白开始涂人，只要照片）。"""
    import base64

    from PIL import Image

    s = _session(sid)
    if render:
        faces = _estimator.faces()
        if faces is None:   # 还没加载过模型（权重目录里也没存三角形）：加载一次
            with _model_lock:
                try:
                    _load_model()
                except Exception as ex:
                    raise HTTPException(503, tr("模型加载失败：{error}", error=ex))
                faces = _estimator.faces()
        labels = masks.render_labels(s.humans, s.image_size, faces)
    else:
        w, h = masks.label_size(s.image_size)
        labels = np.zeros((h, w), dtype=np.uint8)
    h, w = labels.shape
    photo = Image.open(io.BytesIO(s.preview_png)).convert("RGB")
    if photo.size != (w, h):
        photo = photo.resize((w, h), Image.BILINEAR)
    return {"width": w, "height": h, "count": len(s.humans),
            "labels": base64.b64encode(masks.encode(labels)).decode("ascii"),
            "image": base64.b64encode(masks.encode(np.asarray(photo))).decode("ascii")}


@app.get("/sessions/{sid}/image")
def session_image(sid: str) -> Response:
    return Response(_session(sid).preview_png, media_type="image/png")


class OnionPerson(BaseModel):
    person: int
    params: tuning.RetargetParams = tuning.RetargetParams()


class RetargetRequest(BaseModel):
    person: int = 0
    params: tuning.RetargetParams = tuning.RetargetParams()
    reference: str | None = None    # 游戏内角色当前姿势（Brio pose JSON），提供时骨长/缩放按游戏实际值
    overlay: bool = True
    stick: bool = True
    race: str | None = None         # 骨架编号 cXXXX（插件按角色外貌数据识别）；缺省时按 reference 的骨长推断
    trust_reference: bool = False   # 插件已清除过 Brio 姿势栈：骨长不符也按游戏实际值（Customize+、改体型 MOD）
    use_aux: bool = True            # 挂了辅助视角时用融合结果
    onion: list[OnionPerson] = []   # 合并模式 3D 页的洋葱皮：这些人的骨架也算出来，按站位摆到这个人的模型系里


def _ivcs(ref: BrioPose | None, race: dict) -> bool | None:
    """游戏角色是否加载了 IVCS 骨骼（Brio 读回的姿势含骨架里的全部骨骼，有 iv_ 骨骼就是装了）。
    没有游戏姿势时为 None（按装了计算）；没装时把提示加到 race["warnings"]。"""
    if ref is None:
        return None
    if any(name.startswith("iv_") for name in ref.bone_names):
        return True
    race["warnings"].append(tr("未检测到 IVCS 骨骼：手指第三节的弯曲已合并到第二节"))
    return False


def _reference_lengths(ref: BrioPose | None, race: dict, trust: bool) -> tuple[bool, bool]:
    """返回 (骨长是否取游戏值, 游戏骨长是否像残留的旧姿势)，并把提示加到 race["warnings"]。
    换种族后 Brio 会把按旧骨架算的位置增量继续叠加在新骨架上，读回的骨长是错的，而且重新应用也恢复不了
    （读回的是上次自己写进去的错误骨长）。"""
    if ref is None:
        return True, False
    dev = races.length_deviation(ref, race["code"])
    if dev is None or dev <= races.STALE_DEVIATION:
        return True, False
    if trust:
        race["warnings"].append(tr("游戏骨长与{name}相差 {pct}%（Customize+ 或改体型 MOD？），按游戏实际骨长计算",
                                   name=race["name"], pct=round(dev * 100)))
        return True, True
    race["warnings"].append(tr("游戏骨长与{name}相差 {pct}%，多半是换种族前残留的 Brio 姿势，已改用{name}的标准骨长",
                               name=race["name"], pct=round(dev * 100)))
    return False, True


def _resolve_race(requested: str | None, ref: BrioPose | None) -> dict:
    """确定使用的骨架。返回 races.describe 的内容，另加 source（plugin/guess/default）与 warnings。"""
    guessed = races.guess_race(ref) if ref is not None else None
    warnings = []
    if requested in races.RACES:
        code, source = requested, "plugin"
        # 骨长与识别出的种族明显不符：换种族/改体型 MOD、Customize+ 等
        if guessed and guessed[1] < 0.03 and races.RACES[guessed[0]].group != races.RACES[code].group:
            warnings.append(tr("游戏角色的骨长更像{group}，与所选{name}不符（刚换过种族？改体型 MOD？），身体比例会不对",
                               group=tr(races.RACES[guessed[0]].group), name=tr(races.RACES[code].name)))
    elif guessed and guessed[1] < 0.05:
        code, source = guessed[0], "guess"
        if requested:
            warnings.append(tr("没有 {code} 的骨架数据，按骨长推断为{group}", code=requested, group=tr(races.RACES[code].group)))
    else:
        code, source = races.DEFAULT_RACE, "default"
        warnings.append(tr("无法确定种族，按人族中原男计算") + (tr("（没有 {code} 的骨架数据）", code=requested) if requested else ""))
    info = races.describe(code)
    info.update(name=tr(info["name"]), group=tr(info["group"]))
    return {**info, "source": source, "warnings": warnings}


def _scene(s: Session):
    """多人会话的共同重力校正与是否都在地上（floor.py）；单人会话的 upright 为 None（按单人站姿估算）。"""
    from . import floor

    if s.scene is None:
        s.scene = floor.scene(s.humans)
    return s.scene


def _human(s: Session, person: int, use_aux: bool = True) -> dict:
    if not 0 <= person < len(s.humans):
        raise HTTPException(400, tr("人物序号超出范围（共 {count} 人）", count=len(s.humans)))
    if use_aux and person == 0 and s.fused is not None:
        return s.fused
    return s.humans[person]


class ViewsRequest(BaseModel):
    aux: list[str] = []    # 辅助图的会话（各取第 0 人）；空列表为去掉辅助视角


@app.post("/sessions/{sid}/views")
def session_views(sid: str, req: ViewsRequest) -> dict:
    """融合在这里算一次存进会话（几十毫秒），之后的重定向直接用；辅助图的会话用完插件可以删掉。"""
    from . import multiview

    s = _session(sid)
    aux = [_session(a).humans[0] for a in req.aux]
    if not aux:
        fused, views = None, None
    else:
        views = multiview.describe(s.humans[0], aux)
        fused = multiview.fuse(s.humans[0], aux)
    try:
        _sessions.set_views(sid, fused, views)
    except KeyError:
        raise HTTPException(404, tr("会话不存在（后端重启过？请重新识别）"))
    return {"views": views or []}


@app.post("/sessions/{sid}/retarget")
def session_retarget(sid: str, req: RetargetRequest) -> dict:
    s = _session(sid)
    human, head_diff = _head_from_face(s, req.person, _human(s, req.person, req.use_aux), req.params)
    t0 = time.perf_counter()
    ref = BrioPose.loads(req.reference) if req.reference else None
    race = _resolve_race(req.race, ref)
    use_lengths, stale = _reference_lengths(ref, race, req.trust_reference)
    ivcs = _ivcs(ref, race)
    rt = tuning.run(human, s.image_size, req.params, ref, race["code"], reference_lengths=use_lengths,
                    ivcs=ivcs is not False, upright_override=_scene(s).upright)
    warnings, severity = tuning.check(rt, race["code"])
    face_bones, face_info = _face_pose(s, req.person, rt, ref, race["code"], req.params) if req.params.face else ({}, None)
    out = {"pose": to_brio_pose_json(rt, face_bones), "warnings": warnings, "severity": severity,
           "compensation": rt.hand_compensation, "race": race, "referenceStale": stale, "ivcs": ivcs, "face": face_info,
           "headFromFaceDeg": None if head_diff is None else round(head_diff, 1)}
    if req.overlay:
        out["overlay"] = tuning.overlay(human, s.image_size, rt, race["code"])
    if req.stick:
        out["stick"] = tuning.stick_figure(human, rt, race["code"])
    if req.onion and len(s.humans) > 1:
        out["onion"] = _onion(s, human, rt, req)
    out["elapsedMs"] = round((time.perf_counter() - t0) * 1000, 1)
    return out


def _face_result(s: Session, person: int):
    """第 person 个人的面捕结果（face.FaceResult），第一次问到时算（CPU 几十毫秒），之后用会话里的缓存；面捕没装时为 None。"""
    from . import face

    if not face.available():
        return None
    if s.faces is None:
        s.faces = {}
    if person not in s.faces:
        from PIL import Image

        rgb = np.asarray(Image.open(io.BytesIO(s.preview_png)).convert("RGB"))
        s.faces[person] = face.analyze(rgb, s.humans[person]["keypoints_2d"], s.image_size)
    return s.faces[person]


@app.get("/sessions/{sid}/face")
def session_face(sid: str, person: int = 0) -> dict:
    """第 person 个人的面部表情（见 face.py）：52 个表情系数、头部朝向和置信度；置信度过低时 used 为 false，不要用。
    在会话的预览图上找。"""
    s = _session(sid)
    _human(s, person, use_aux=False)
    result = _face_result(s, person)
    if result is None:
        return {"available": False, "found": False, "confidence": 0.0, "used": False,
                "reason": tr("面捕组件没装（mediapipe 或 face_landmarker 模型）"), "blendshapes": {}}
    out = result.to_json()
    out["reason"] = tr(out["reason"]) if out["reason"] else ""
    return {"available": True, **out}


class BrioEditsRequest(RetargetRequest):
    edited: str                     # 用户在 Brio 里改过之后读回的姿势（Brio pose JSON）


BRIO_EDIT_MIN_DEG = 0.5   # 比这个小的差别当读回的舍入误差


@app.post("/sessions/{sid}/brio-edits")
def session_brio_edits(sid: str, req: BrioEditsRequest) -> dict:
    """把用户在 Brio 里对姿势的改动换成调整页的关节偏移（offsets），这样能随「保存修正」存进图库、下次打开自动套上，
    改调整值重新应用时也不会被冲掉。按骨架从根到末端逐根比：这根和 Brio 里的朝向差多少，就在它的偏移上再转多少
    （偏移带着整棵子树转，所以先定上一级再看下一级）。只换朝向；辅助骨跟着主骨自动联动，脸、头发、裙摆这些不在重定向里的骨骼不收。
    返回 {offsets：完整的新偏移, changed：改了哪些骨骼, residualDeg：收完后还差的最大角度}。"""
    from scipy.spatial.transform import Rotation as R

    from .retarget.ffxiv import helper_rules, load_ffxiv_skeleton

    s = _session(sid)
    human, _ = _head_from_face(s, req.person, _human(s, req.person, req.use_aux), req.params)
    ref = BrioPose.loads(req.reference) if req.reference else None
    edited = BrioPose.loads(req.edited)
    race = _resolve_race(req.race, ref)
    use_lengths, _ = _reference_lengths(ref, race, req.trust_reference)
    options = dict(reference=ref, race=race["code"], reference_lengths=use_lengths, ivcs=_ivcs(ref, race) is not False,
                   upright_override=_scene(s).upright)
    params = req.params.model_copy(deep=True)
    params.face = False
    offsets = {k: list(v) for k, v in params.offsets.items()}
    sk = load_ffxiv_skeleton(race["code"])
    helpers = {r.helper for r in helper_rules(race["code"])}
    rt = tuning.run(human, s.image_size, params, **options)
    order = sorted((n for n in rt.bones if n not in helpers and n != "n_root" and edited.get(n) is not None), key=sk.index)
    angle = lambda name: float(np.degrees((R.from_quat(edited.get(name).rotation) * R.from_quat(rt.bones[name][1]).inv()).magnitude()))
    changed = []
    for name in order:
        if angle(name) < BRIO_EDIT_MIN_DEG:
            continue
        delta = R.from_quat(edited.get(name).rotation) * R.from_quat(rt.bones[name][1]).inv()
        old = R.from_euler("xyz", offsets.get(name, [0.0, 0.0, 0.0]), degrees=True)
        offsets[name] = [round(float(v), 2) for v in (delta * old).as_euler("xyz", degrees=True)]
        params.offsets = offsets
        rt = tuning.run(human, s.image_size, params, **options)
        changed.append(name)
    residual = max((angle(n) for n in order), default=0.0)
    return {"offsets": offsets, "changed": changed, "residualDeg": round(residual, 2)}


def _head_from_face(s: Session, person: int, human: dict, params) -> tuple[dict, float | None]:
    """按面捕的脸部朝向修正头部（face.head_from_face）。返回 (human, 和 SAM 原来的头差多少度)；没用上时差值为 None
    （面捕没装、没找到脸、权重为 0），差太多不信时 human 原样、差值照给。"""
    if params.face_head <= 0:
        return human, None
    from PIL import Image

    from . import face

    result = _face_result(s, person)
    if result is None:
        return human, None
    preview_scale = Image.open(io.BytesIO(s.preview_png)).size[0] / float(s.image_size[0])
    return face.head_from_face(human, result, s.image_size, preview_scale, params.face_head)


def _face_pose(s: Session, person: int, rt, ref: BrioPose | None, race: str, params) -> tuple[dict, dict]:
    """把照片表情换算成脸部骨骼（face_rig.py）。返回 (要写进姿势的骨骼, 给插件看的说明)；用不了时骨骼为空、说明里写原因。"""
    from . import face_rig

    result = _face_result(s, person)
    if result is None:
        return {}, {"applied": False, "needsDump": False, "reason": tr("面捕组件没装（mediapipe 或 face_landmarker 模型）")}
    if not result.used:
        return {}, {"applied": False, "needsDump": False, "reason": tr(result.reason) if result.reason else tr("没找到脸（插画、遮挡或背对）")}
    if not face_rig.available(race):
        return {}, {"applied": False, "needsDump": True, "reason": tr("还没有游戏表情数据：在游戏里输入 /posegraph facedump 导出一次")}
    head = rt.bones.get("j_kao")
    if head is None:
        return {}, {"applied": False, "needsDump": False, "reason": tr("姿势里没有头部骨骼")}
    bones, skeleton, controls = face_rig.face_bones(race, ref, head, result.landmarks or None, params.face_strength, result.blendshapes,
                                                    result.rotation or None, params.face_gains)
    shown = {tr(k): v for k, v in controls.items()}
    return bones, {"applied": bool(bones), "needsDump": False, "skeleton": skeleton, "channels": shown, "reason": ""}


def _onion(s: Session, human: dict, rt, req: RetargetRequest) -> list[dict]:
    """洋葱皮：其他人的 FF14 骨架（默认骨架，不按游戏角色的骨长），按复刻站位的相对位置放进正在编辑的人的模型系。"""
    from . import placement

    names = load_mhr_rest()[0]
    scene = _scene(s)
    others = [o for o in req.onion if o.person != req.person and 0 <= o.person < len(s.humans)]
    humans = [_human(s, o.person, use_aux=False) for o in others]
    people = [placement.Person(human, rt, placement.ModelTransform.of(None))]
    people += [placement.Person(h, tuning.run(h, s.image_size, o.params, upright_override=scene.upright), placement.ModelTransform.of(None))
               for h, o in zip(humans, others)]
    placed = placement.place(people, 0, names)
    if scene.on_floor:
        placement.ground(people, placed, 0)
    out = []
    for o, p, m in zip(others, people[1:], placed[1:]):
        stick = tuning.stick_figure(p.human, p.rt)
        for j in stick["joints"]:
            j["pos"] = [round(float(x), 4) for x in m.position + m.rotation.apply(m.scale * np.asarray(j["pos"]))]
        out.append({"person": o.person, "joints": stick["joints"]})
    return out


class PlacementPerson(BaseModel):
    person: int
    params: tuning.RetargetParams = tuning.RetargetParams()
    reference: str | None = None    # 这个人对应角色的游戏姿势（与应用时相同；含 ModelAbsoluteValues）
    race: str | None = None
    trust_reference: bool = False
    # 角色现在的模型变换（插件用 Brio GetModelTransform 读，和移动用的 SetModelTransform 一致）：
    # {"position": [x,y,z], "rotation": [x,y,z,w], "scale": [x,y,z]}；不给时用 reference 里的 ModelAbsoluteValues
    model: dict | None = None
    nudge: list[float] | None = None   # 手动微调：[右, 上, 前（朝镜头）米, 转向 度]，沿场景方向；基准人物忽略


class PlacementRequest(BaseModel):
    base: int = 0                   # people 里基准人物的下标：它的角色不动
    people: list[PlacementPerson]


@app.post("/sessions/{sid}/placement")
def session_placement(sid: str, req: PlacementRequest) -> dict:
    """合并模式复刻站位（placement.py）：每个人的角色应有的模型变换（世界坐标，旋转 xyzw），基准角色的不变。
    relative 为相对基准角色的位置（基准角色模型系：x 右 / y 上 / z 前，米）与朝向差；
    framingErrorPct 为摆好后用基准人物复刻镜头时，这个人与照片的构图偏差。"""
    from . import placement

    s = _session(sid)
    if not req.people or not 0 <= req.base < len(req.people):
        raise HTTPException(400, tr("人物序号超出范围（共 {count} 人）", count=len(s.humans)))
    names = load_mhr_rest()[0]
    people, settings_of = [], []
    for p in req.people:
        human, _ = _head_from_face(s, p.person, _human(s, p.person, use_aux=False), p.params)
        ref = BrioPose.loads(p.reference) if p.reference else None
        race = _resolve_race(p.race, ref)
        use_lengths, _ = _reference_lengths(ref, race, p.trust_reference)
        options = dict(reference=ref, race=race["code"], reference_lengths=use_lengths, ivcs=_ivcs(ref, race) is not False,
                       upright_override=_scene(s).upright)
        rt = tuning.run(human, s.image_size, p.params, **options)
        model = placement.ModelTransform.from_dict(p.model) if p.model else placement.ModelTransform.of(ref)
        people.append(placement.Person(human, rt, model))
        settings_of.append(options)
    placed = placement.place(people, req.base, names)
    if _scene(s).on_floor:   # 深度找到了地面、大家都在地上：最低点都放到同一地面高度
        placement.ground(people, placed, req.base)
    placement.nudge(people, placed, req.base, [p.nudge for p in req.people])
    errors = placement.framing_errors(people, placed, req.base, s.image_size, names)
    base = placed[req.base]
    out = []
    for p, m, error in zip(req.people, placed, errors):
        rel = base.rotation.inv().apply(m.position - base.position)
        facing = (base.rotation.inv() * m.rotation).apply([0.0, 0.0, 1.0])
        out.append({"person": p.person, "position": m.position.tolist(), "rotation": m.rotation.as_quat().tolist(),
                    "scale": m.scale.tolist(),
                    "relative": {"x": round(float(rel[0]), 3), "y": round(float(rel[1]), 3), "z": round(float(rel[2]), 3),
                                 "yawDeg": round(float(np.degrees(np.arctan2(facing[0], facing[2]))), 1)},
                    "framingErrorPct": None if np.isnan(error) else round(error * 100, 1)})
    return {"people": out}


class CameraRequest(BaseModel):
    person: int = 0
    params: tuning.RetargetParams = tuning.RetargetParams()
    actor: int | None = None
    race: str | None = None
    use_aux: bool = True


@app.post("/sessions/{sid}/camera")
def session_camera(sid: str, req: CameraRequest) -> dict:
    """需要 Posegraph 的 HTTP 服务在运行（插件会调用本接口，本接口再回调插件读写相机）。"""
    from .bridge import BridgeClient
    from .camera import framing_error, photo_camera_in_world, solve_gpose_camera

    s = _session(sid)
    human, _ = _head_from_face(s, req.person, _human(s, req.person, req.use_aux), req.params)
    client = BridgeClient()
    pose_json = client.get_pose(req.actor)
    ref = BrioPose.loads(pose_json)
    race = _resolve_race(req.race, ref)
    rt = tuning.run(human, s.image_size, req.params, ref, race["code"],
                    reference_lengths=_reference_lengths(ref, race, False)[0], ivcs=_ivcs(ref, race) is not False,
                    upright_override=_scene(s).upright)
    model = ref.raw["ModelAbsoluteValues"]
    vec = lambda v: np.array([float(x) for x in v.split(",")])
    target = photo_camera_in_world(human, rt, vec(model["Position"]), vec(model["Rotation"]), vec(model["Scale"]),
                                   load_mhr_rest()[0])
    log: list[str] = []
    result = solve_gpose_camera(client, target, log=log.append)
    framing = framing_error(client, human, BrioPose.loads(client.get_pose(req.actor)), load_mhr_rest()[0])
    return {"positionErrorCm": round(result["position_error_cm"], 1),
            "forwardErrorDeg": round(result["forward_error_deg"], 2),
            "fovDeg": round(result["fov_deg"], 1), "targetFovDeg": round(result["target_fov_deg"], 1),
            "framingErrorPct": round(framing * 100, 1), "log": log}


@app.get("/races")
def race_list() -> dict:
    """插件的种族下拉框用。"""
    return {"default": races.DEFAULT_RACE,
            "races": [{**d, "name": tr(d["name"]), "group": tr(d["group"])} for d in map(races.describe, races.RACES)]}


@app.post("/shutdown")
def shutdown() -> dict:
    """插件"停止后端"用（后端可能不是插件启动的，拿不到进程句柄）。"""
    import os

    def stop():
        try:
            _unload_model()  # 等正在进行的识别结束再释放
            _sessions.close()
        finally:
            os._exit(0)
    threading.Timer(0.3, stop).start()
    return {"ok": True}


@app.get("/presets")
def presets() -> dict:
    return {"presets": {k: v.model_dump() for k, v in tuning.PRESETS.items()},
            "amplitudeGroups": {k: list(v) for k, v in tuning.AMPLITUDE_GROUPS.items()}}


def main() -> None:
    import uvicorn

    threading.Thread(target=_release_loop, daemon=True).start()
    if settings.preload and settings.release_after != 0:
        threading.Thread(target=lambda: _safe(_load_model), daemon=True).start()
    uvicorn.run(app, host=settings.host, port=settings.port)


if __name__ == "__main__":
    main()
