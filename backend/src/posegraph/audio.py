"""录制的视频配上原视频的音乐（PyAV：画面直接拷贝，音乐解码后从指定时刻截一段、重新编码成 AAC）。

音乐来源：给的文件里有音轨就用它；没有（比如网站下载的视频画面、声音分成两个文件）就在同一文件夹里
找同名的音频文件（.m4a .mp3 .aac .wav .ogg .opus .webm .flac）。PyAV 没装时不合成，录制照常是无声视频。
"""

from __future__ import annotations

import shutil
from pathlib import Path

import numpy as np

AUDIO_EXTS = (".m4a", ".mp3", ".aac", ".wav", ".ogg", ".opus", ".webm", ".flac", ".mp4", ".mkv", ".mov")
RATE = 48000


def available() -> bool:
    try:
        import av  # noqa: F401
    except ImportError:
        return False
    return True


def _has_audio(path: Path) -> bool:
    import av

    try:
        with av.open(str(path)) as c:
            return any(s.type == "audio" for s in c.streams)
    except Exception:
        return False


def find_source(path: Path) -> Path | None:
    """有音轨的文件：自己，或同一文件夹里同名的音频文件。"""
    path = Path(path)
    if path.is_file() and _has_audio(path):
        return path
    for ext in AUDIO_EXTS:
        other = path.with_suffix(ext)
        if other != path and other.is_file() and _has_audio(other):
            return other
    return None


def mux(video: Path, source: Path, start: float, out: Path) -> float:
    """video（无声 mp4）+ source 从 start 秒起的音乐 → out。返回配上的音乐秒数。"""
    import av

    with av.open(str(video)) as vin, av.open(str(source)) as ain, av.open(str(out), "w", format="mp4") as dst:
        vs = vin.streams.video[0]
        duration = float(vs.frames / vs.average_rate) if vs.frames and vs.average_rate else float(vin.duration or 0) / av.time_base
        v_out = dst.add_stream(template=vs)
        a_src = ain.streams.audio[0]
        a_out = dst.add_stream("aac", rate=RATE)
        a_out.layout = "stereo"
        a_out.bit_rate = 192000

        for packet in vin.demux(vs):
            if packet.dts is None:
                continue
            packet.stream = v_out
            dst.mux(packet)

        resampler = av.AudioResampler(format="fltp", layout="stereo", rate=RATE)
        if start > 0:
            ain.seek(int(start / a_src.time_base), stream=a_src, backward=True, any_frame=False)
        need = int(round(duration * RATE))
        got = 0
        pts = 0
        for frame in ain.decode(a_src):
            if frame.pts is None:
                continue
            t0 = float(frame.pts * a_src.time_base)
            for rf in resampler.resample(frame):
                data = rf.to_ndarray()
                skip = int(round((start - t0) * RATE)) if t0 < start else 0
                t0 += data.shape[1] / RATE
                if skip >= data.shape[1]:
                    continue
                data = data[:, max(0, skip):][:, :need - got]
                if data.shape[1] == 0:
                    continue
                piece = av.AudioFrame.from_ndarray(np.ascontiguousarray(data), format="fltp", layout="stereo")
                piece.sample_rate = RATE
                piece.pts = pts
                pts += data.shape[1]
                got += data.shape[1]
                for p in a_out.encode(piece):
                    dst.mux(p)
            if got >= need:
                break
        for p in a_out.encode(None):
            dst.mux(p)
    return got / RATE


def add_music(video: Path, source: Path, start: float) -> float:
    """给录好的 video 配上音乐（就地替换）。返回配上的秒数；失败抛异常（视频保持原样）。"""
    video = Path(video)
    tmp = video.with_name(video.stem + ".music" + video.suffix)
    try:
        seconds = mux(video, source, start, tmp)
        video.unlink(missing_ok=True)
        shutil.move(str(tmp), str(video))
        return seconds
    finally:
        tmp.unlink(missing_ok=True)


# ---------------------------------------------------------------- 播放时的音乐（插件按片段时间同步放）

PLAY_RATE = 44100
_pcm_cache: dict = {}   # (路径, 修改时间, 大小) → (帧, 2) int16，只留最近两个
_pcm_lock = __import__("threading").Lock()


def pcm(path: Path) -> np.ndarray:
    """整个文件解码成 44.1kHz 立体声 int16（缓存，同一首歌反复拖进度不用重新解码）。"""
    import av

    path = Path(path)
    st = path.stat()
    key = (str(path), st.st_mtime, st.st_size)
    with _pcm_lock:
        if key in _pcm_cache:
            return _pcm_cache[key]
    chunks = []
    with av.open(str(path)) as c:
        stream = c.streams.audio[0]
        resampler = av.AudioResampler(format="s16", layout="stereo", rate=PLAY_RATE)
        for frame in c.decode(stream):
            for rf in resampler.resample(frame):
                chunks.append(rf.to_ndarray().reshape(-1, 2))
        for rf in resampler.resample(None):
            chunks.append(rf.to_ndarray().reshape(-1, 2))
    data = np.concatenate(chunks) if chunks else np.zeros((0, 2), np.int16)
    with _pcm_lock:
        while len(_pcm_cache) >= 2:
            _pcm_cache.pop(next(iter(_pcm_cache)))
        _pcm_cache[key] = data
    return data


def wav(source: Path, start: float, end: float, speed: float = 1.0, volume: float = 1.0) -> bytes:
    """source 从 start 到 end 秒（原视频时间）的一段，按 speed 变速（像磁带，音调跟着变）、按 volume 缩放，打包成 WAV。"""
    import io
    import wave

    data = pcm(source)
    a = int(np.clip(round(start * PLAY_RATE), 0, len(data)))
    b = int(np.clip(round(end * PLAY_RATE), a, len(data)))
    seg = data[a:b].astype(np.float32)
    if abs(speed - 1.0) > 1e-3 and len(seg) > 1:
        n = max(1, int(len(seg) / speed))
        x = np.linspace(0, len(seg) - 1, n)
        seg = np.stack([np.interp(x, np.arange(len(seg)), seg[:, ch]) for ch in range(2)], axis=1)
    seg = np.clip(seg * float(np.clip(volume, 0.0, 2.0)), -32768, 32767).astype(np.int16)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(PLAY_RATE)
        w.writeframes(seg.tobytes())
    return buf.getvalue()
