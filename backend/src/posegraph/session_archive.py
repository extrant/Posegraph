"""识别会话存档：把一次识别结果打包成 npz 字节，插件存进图库的识别缓存，下次直接恢复，不用重跑模型。

格式改编自 PR #1（uiharuayako）的 SessionCache：数组放进 npz，其余字段编成 JSON 元数据，不用 pickle。
"""

from __future__ import annotations

import io
import json

import numpy as np

FORMAT_VERSION = 1
MAX_ARCHIVE_BYTES = 64 * 1024 * 1024


def pack(name: str, image_size: tuple[int, int], preview_png: bytes, humans: list[dict], inference_ms: float) -> bytes:
    arrays = {"preview": np.frombuffer(preview_png, dtype=np.uint8)}
    fields_per_human = []
    for i, human in enumerate(humans):
        fields = {}
        for key, value in human.items():
            if isinstance(value, np.generic):
                value = value.item()
            if value is None or isinstance(value, (str, bool, int, float, list, dict)):
                fields[key] = {"value": value}   # 标量、列表、字典（以后多人的检测分数、角色分配等）走 JSON
            else:
                array = np.asarray(value)
                if array.dtype.hasobject:
                    raise ValueError(f"会话字段 {key} 不能存档")
                arrays[f"h{i}_{key}"] = array
                fields[key] = {"array": f"h{i}_{key}"}
        fields_per_human.append(fields)
    meta = {"version": FORMAT_VERSION, "name": name, "image_size": list(image_size), "inference_ms": inference_ms,
            "humans": fields_per_human}
    arrays["meta"] = np.frombuffer(json.dumps(meta, ensure_ascii=False).encode("utf-8"), dtype=np.uint8)
    buf = io.BytesIO()
    np.savez_compressed(buf, **arrays)
    return buf.getvalue()


def unpack(data: bytes) -> dict:
    """返回 name / image_size / preview_png / humans / inference_ms。格式不对抛 ValueError。"""
    if len(data) > MAX_ARCHIVE_BYTES:
        raise ValueError("存档过大")
    try:
        with np.load(io.BytesIO(data), allow_pickle=False) as z:
            meta = json.loads(z["meta"].tobytes().decode("utf-8"))
            if meta.get("version") != FORMAT_VERSION:
                raise ValueError(f"不支持的存档版本 {meta.get('version')}")
            humans = [{key: z[f["array"]].copy() if "array" in f else f["value"] for key, f in fields.items()}
                      for fields in meta["humans"]]
            return {"name": str(meta["name"]), "image_size": tuple(int(v) for v in meta["image_size"]),
                    "preview_png": z["preview"].tobytes(), "humans": humans,
                    "inference_ms": float(meta["inference_ms"])}
    except (KeyError, OSError, json.JSONDecodeError, TypeError) as ex:
        raise ValueError(f"存档已损坏：{ex}") from None
