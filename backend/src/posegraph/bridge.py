"""Posegraph（Dalamud 插件）的 HTTP 客户端。接口定义见 Posegraph/README.md。"""

from __future__ import annotations

import httpx

from .config import settings


class BridgeClient:
    def __init__(self, base_url: str | None = None, timeout: float = 10.0):
        # 插件在本机：不读环境变量 / 系统代理（代理软件会改写或拦截本机请求）
        self._http = httpx.Client(base_url=base_url or settings.bridge_url, timeout=timeout, trust_env=False)

    def status(self) -> dict:
        return self._http.get("/status").raise_for_status().json()

    def actors(self) -> list[dict]:
        return self._http.get("/actors").raise_for_status().json()

    def get_pose(self, actor: int | None = None) -> str:
        """取角色当前姿势（Brio pose JSON 文本）。actor 为空时用 GPose 当前目标。"""
        params = {} if actor is None else {"actor": actor}
        return self._http.get("/pose", params=params).raise_for_status().text

    def get_camera(self) -> dict:
        return self._http.get("/camera").raise_for_status().json()

    def set_camera(self, **fields) -> dict:
        """fields：distance, angle[2], pan[2], roll, zoom, delimit, snap（跳过游戏的平滑过渡；旧版插件忽略）。"""
        return self._http.post("/camera", json=fields).raise_for_status().json()

    def apply_pose(self, pose_json: str, actor: int | None = None) -> dict:
        params = {} if actor is None else {"actor": actor}
        r = self._http.post("/pose", params=params, content=pose_json.encode("utf-8"),
                            headers={"Content-Type": "application/json"})
        return r.raise_for_status().json()
