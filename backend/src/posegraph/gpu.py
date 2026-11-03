"""显卡状态查询（NVIDIA nvidia-smi / Windows DXGI+WDDM / Linux amdgpu sysfs）。
不初始化 CUDA / ROCm HIP：在 CPU 模式或模型未加载时调用 torch.cuda 会常驻几百 MB 显存上下文。"""

from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

_cache: tuple[float, dict | None] = (0.0, None)
_has_nvidia_smi: bool | None = None
_d3dkmt_seg_offset: int | None = None

_VENDOR_NAMES = {0x10DE: "nvidia", 0x1002: "amd", 0x8086: "intel"}


def _query_nvidia_smi() -> dict | None:
    global _has_nvidia_smi
    if _has_nvidia_smi is False:
        return None
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total,memory.used,memory.free", "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=5, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        ).stdout.strip().splitlines()
        if out:
            name, total, used, free = (s.strip() for s in out[0].split(","))
            _has_nvidia_smi = True
            return {"name": name, "totalMB": int(total), "usedMB": int(used), "freeMB": int(free), "vendor": "nvidia"}
    except FileNotFoundError:
        _has_nvidia_smi = False
    except (OSError, ValueError, subprocess.SubprocessError):
        pass
    return None


def _query_windows_dxgi() -> dict | None:
    """Windows 通用显存查询：DXGI 枚举物理独显 + WDDM D3DKMTQueryStatistics 读取整卡驻留显存（IDXGIAdapter3 兜底）。"""
    import ctypes
    import struct
    from ctypes import wintypes

    if ctypes.sizeof(ctypes.c_void_p) != 8:
        return None

    class LUID(ctypes.Structure):
        _fields_ = [("LowPart", wintypes.DWORD), ("HighPart", wintypes.LONG)]

    class DXGI_ADAPTER_DESC1(ctypes.Structure):
        _fields_ = [
            ("Description", wintypes.WCHAR * 128),
            ("VendorId", wintypes.UINT),
            ("DeviceId", wintypes.UINT),
            ("SubSysId", wintypes.UINT),
            ("Revision", wintypes.UINT),
            ("DedicatedVideoMemory", ctypes.c_size_t),
            ("DedicatedSystemMemory", ctypes.c_size_t),
            ("SharedSystemMemory", ctypes.c_size_t),
            ("AdapterLuid", LUID),
            ("Flags", wintypes.UINT),
        ]

    class DXGI_QUERY_VIDEO_MEMORY_INFO(ctypes.Structure):
        _fields_ = [
            ("Budget", ctypes.c_uint64),
            ("CurrentUsage", ctypes.c_uint64),
            ("AvailableForReservation", ctypes.c_uint64),
            ("CurrentReservation", ctypes.c_uint64),
        ]

    class GUID(ctypes.Structure):
        _fields_ = [
            ("Data1", wintypes.DWORD),
            ("Data2", wintypes.WORD),
            ("Data3", wintypes.WORD),
            ("Data4", ctypes.c_ubyte * 8),
        ]

    def vtable_fn(ptr, index: int, restype, *argtypes):
        vtbl = ctypes.cast(ptr, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        return ctypes.WINFUNCTYPE(restype, ctypes.c_void_p, *argtypes)(vtbl[index])

    iid_factory1 = GUID(0x770AAE78, 0xF26F, 0x4DBA, (ctypes.c_ubyte * 8)(0xA8, 0x29, 0x25, 0x3C, 0x83, 0xD1, 0xB3, 0x87))
    iid_adapter3 = GUID(0x645967A4, 0x1392, 0x4310, (ctypes.c_ubyte * 8)(0xA7, 0x98, 0x80, 0x53, 0xCE, 0x3E, 0x93, 0xFD))

    try:
        dxgi = ctypes.windll.dxgi
        dxgi.CreateDXGIFactory1.argtypes = [ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
        dxgi.CreateDXGIFactory1.restype = ctypes.c_long

        factory = ctypes.c_void_p()
        if dxgi.CreateDXGIFactory1(ctypes.byref(iid_factory1), ctypes.byref(factory)) != 0 or not factory.value:
            return None
        try:
            enum_adapters = vtable_fn(factory, 12, ctypes.c_long, wintypes.UINT, ctypes.POINTER(ctypes.c_void_p))
            best_desc: DXGI_ADAPTER_DESC1 | None = None
            best_budget: tuple[int, int] | None = None
            idx = 0
            while True:
                adapter = ctypes.c_void_p()
                if enum_adapters(factory, idx, ctypes.byref(adapter)) != 0 or not adapter.value:
                    break
                idx += 1
                try:
                    desc = DXGI_ADAPTER_DESC1()
                    if vtable_fn(adapter, 10, ctypes.c_long, ctypes.POINTER(DXGI_ADAPTER_DESC1))(adapter, ctypes.byref(desc)) != 0:
                        continue
                    # 跳过软件渲染器（DXGI_ADAPTER_FLAG_SOFTWARE = 2）与无专用显存设备
                    if (desc.Flags & 2) or desc.DedicatedVideoMemory == 0:
                        continue
                    if best_desc is None or desc.DedicatedVideoMemory > best_desc.DedicatedVideoMemory:
                        best_desc = desc
                        best_budget = None
                        adapter3 = ctypes.c_void_p()
                        if vtable_fn(adapter, 0, ctypes.c_long, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))(
                            adapter, ctypes.byref(iid_adapter3), ctypes.byref(adapter3)
                        ) == 0 and adapter3.value:
                            try:
                                mem = DXGI_QUERY_VIDEO_MEMORY_INFO()
                                if vtable_fn(adapter3, 14, ctypes.c_long, wintypes.UINT, ctypes.c_int,
                                             ctypes.POINTER(DXGI_QUERY_VIDEO_MEMORY_INFO))(adapter3, 0, 0, ctypes.byref(mem)) == 0:
                                    best_budget = (int(mem.Budget), int(mem.CurrentUsage))
                            finally:
                                vtable_fn(adapter3, 2, wintypes.ULONG)(adapter3)
                finally:
                    vtable_fn(adapter, 2, wintypes.ULONG)(adapter)
        finally:
            vtable_fn(factory, 2, wintypes.ULONG)(factory)

        if best_desc is None:
            return None

        dedicated = int(best_desc.DedicatedVideoMemory)
        total_mb = dedicated // 1048576
        used_mb: int | None = None

        # 通过 WDDM D3DKMTQueryStatistics 读取整卡所有进程的本地显存驻留量（与任务管理器一致）
        used_bytes = _query_d3dkmt_local_vram(best_desc.AdapterLuid.LowPart, best_desc.AdapterLuid.HighPart, dedicated)
        if used_bytes is not None:
            used_mb = min(total_mb, used_bytes // 1048576)
        elif best_budget is not None:
            budget, cur_usage = best_budget
            free_est = max(0, budget - cur_usage)
            used_mb = max(0, total_mb - free_est // 1048576)

        if used_mb is None:
            return None
        vendor = _VENDOR_NAMES.get(int(best_desc.VendorId), "other")
        return {
            "name": best_desc.Description.strip(),
            "totalMB": total_mb,
            "usedMB": used_mb,
            "freeMB": max(0, total_mb - used_mb),
            "vendor": vendor,
        }
    except Exception:
        return None


def _query_d3dkmt_local_vram(low_part: int, high_part: int, expected_dedicated: int) -> int | None:
    """读取 WDDM 非 Aperture 本地显存段的全局驻留字节数；结构体布局不符时返回 None 交由上层兜底。"""
    global _d3dkmt_seg_offset
    import ctypes
    import struct

    try:
        gdi32 = ctypes.windll.gdi32
        gdi32.D3DKMTQueryStatistics.argtypes = [ctypes.c_void_p]
        gdi32.D3DKMTQueryStatistics.restype = ctypes.c_uint32

        buf = ctypes.create_string_buffer(2048)
        # Type = 0 (D3DKMT_QUERYSTATISTICS_ADAPTER)
        struct.pack_into("<IIiI", buf, 0, 0, low_part, high_part, 0)
        if gdi32.D3DKMTQueryStatistics(buf) != 0:
            return None
        nb_segments = struct.unpack_from("<I", buf, 24)[0]
        if nb_segments == 0 or nb_segments > 32:
            return None

        # Segment 0（输入全 0，无需偏移即可查询）
        seg0 = ctypes.create_string_buffer(2048)
        struct.pack_into("<IIiI", seg0, 0, 3, low_part, high_part, 0)
        if gdi32.D3DKMTQueryStatistics(seg0) != 0:
            return None
        sig0 = seg0.raw[24:48]

        if nb_segments > 1 and _d3dkmt_seg_offset is None:
            for off in range(512, 1536, 4):
                probe = ctypes.create_string_buffer(2048)
                struct.pack_into("<IIiI", probe, 0, 3, low_part, high_part, 0)
                struct.pack_into("<I", probe, off, 1)
                if gdi32.D3DKMTQueryStatistics(probe) == 0 and probe.raw[24:48] != sig0:
                    _d3dkmt_seg_offset = off
                    break

        total_limit = 0
        total_resident = 0
        segments = [seg0]
        if nb_segments > 1 and _d3dkmt_seg_offset is not None:
            for s in range(1, nb_segments):
                bs = ctypes.create_string_buffer(2048)
                struct.pack_into("<IIiI", bs, 0, 3, low_part, high_part, 0)
                struct.pack_into("<I", bs, _d3dkmt_seg_offset, s)
                if gdi32.D3DKMTQueryStatistics(bs) == 0:
                    segments.append(bs)

        for bs in segments:
            limit, _committed, resident = struct.unpack_from("<3Q", bs, 24)
            # WDDM 2.4+ D3DKMT_QUERYSTATISTICS_SEGMENT_PROPERTIES 位于偏移 24+104，bit 0 为 Aperture
            props = struct.unpack_from("<I", bs, 128)[0]
            if (props & 1) == 0 and limit > 0:
                total_limit += limit
                total_resident += resident

        # 校验本地段总容量与 DXGI DedicatedVideoMemory 一致，防止未来 OS 结构偏移变动误读
        if total_limit == 0 or not (0.5 * expected_dedicated <= total_limit <= 1.5 * expected_dedicated):
            return None
        return total_resident
    except Exception:
        return None


def _query_linux_sysfs() -> dict | None:
    """Linux amdgpu 内核驱动 sysfs 显存接口（无需额外安装 rocm-smi / amd-smi）。"""
    best: dict | None = None
    for card in sorted(Path("/sys/class/drm").glob("card[0-9]*")):
        dev = card / "device"
        total_file = dev / "mem_info_vram_total"
        used_file = dev / "mem_info_vram_used"
        if not (total_file.is_file() and used_file.is_file()):
            continue
        try:
            total = int(total_file.read_text().strip()) // 1048576
            used = int(used_file.read_text().strip()) // 1048576
            if total <= 0:
                continue
            vendor_hex = (dev / "vendor").read_text().strip().lower() if (dev / "vendor").is_file() else ""
            vendor = "amd" if "1002" in vendor_hex else "nvidia" if "10de" in vendor_hex else "other"
            name = (dev / "product_name").read_text().strip() if (dev / "product_name").is_file() else ""
            if not name:
                name = "AMD Radeon GPU" if vendor == "amd" else card.name
            if best is None or total > best["totalMB"]:
                best = {"name": name, "totalMB": total, "usedMB": used, "freeMB": max(0, total - used), "vendor": vendor}
        except (OSError, ValueError):
            continue
    return best


def _query_raw(max_age: float = 2.0) -> dict | None:
    global _cache
    if time.monotonic() - _cache[0] < max_age:
        return _cache[1]
    info = _query_nvidia_smi()
    if info is None:
        if sys.platform == "win32":
            info = _query_windows_dxgi()
        elif sys.platform.startswith("linux"):
            info = _query_linux_sysfs()
    _cache = (time.monotonic(), info)
    return info


def query_gpu(max_age: float = 2.0) -> dict | None:
    """{name, totalMB, usedMB, freeMB}；没有可用独显时返回 None。结果缓存 max_age 秒。"""
    info = _query_raw(max_age)
    return None if info is None else {k: info[k] for k in ("name", "totalMB", "usedMB", "freeMB")}


def query_gpu_vendor(max_age: float = 2.0) -> tuple[dict | None, str | None]:
    """返回 (query_gpu 字典, 厂商 'nvidia'|'amd'|'intel'|'other')。"""
    info = _query_raw(max_age)
    if info is None:
        return None, None
    return {k: info[k] for k in ("name", "totalMB", "usedMB", "freeMB")}, info.get("vendor", "other")
