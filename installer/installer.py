"""Posegraph 后端一键安装（第二阶段）：由 Install-Posegraph.ps1 用刚解压的便携 Python 运行，只用标准库。

按 lock.json 下载并安装和发布的一键包一样的运行环境：
Python 包（PyPI 或国内镜像）、PyTorch（按显卡选：CUDA 12.8 / 12.6、CPU，官方或阿里云镜像；AMD 显卡用 AMD 官方的 ROCm 版，
配 Python 3.12）、SAM 3D Body 源码（GitHub）和权重（魔搭）、Posegraph 后端源码（GitHub 标签）、后端组件（GitHub Release）。
每个文件都校验 SHA-256，镜像、加速站、代理改不了内容。多个文件同时下、大文件分块多线程下，下完一个装一个。
开始前先核对目录里已有的东西：新装、中断后接着装、更新 / 修复老的一键包都一样，只补缺的、换不对的。

不改系统和用户设置：不写注册表、环境变量、PATH、pip 配置，不用用户目录下的 pip 缓存，所有东西都在安装目录里。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import time
import traceback
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
LANG = "zh"


def L(zh: str, en: str) -> str:
    return zh if LANG == "zh" else en


# ------------------------------------------------------------------ 终端

ESC = "\x1b["
C = {"dim": "2", "bold": "1", "red": "91", "green": "92", "yellow": "93", "blue": "94", "magenta": "95", "cyan": "96",
     "white": "97", "teal": "38;5;37", "grey": "90"}
VT = False


def enable_vt() -> bool:
    """打开这个控制台窗口的 ANSI 颜色 / 光标控制（只影响本窗口，关掉就没了）。"""
    if os.name != "nt":
        return True
    try:
        import ctypes
        k = ctypes.windll.kernel32
        h = k.GetStdHandle(-11)
        mode = ctypes.c_uint32()
        if not k.GetConsoleMode(h, ctypes.byref(mode)):
            return False
        return bool(k.SetConsoleMode(h, mode.value | 0x0004))
    except Exception:
        return False


def c(text: str, *styles: str) -> str:
    if not VT or not styles:
        return text
    return ESC + ";".join(C[s] for s in styles) + "m" + text + ESC + "0m"


def width(text: str) -> int:
    return sum(2 if unicodedata.east_asian_width(ch) in "WF" else 1 for ch in text)


def fit(text: str, w: int) -> str:
    """按显示宽度截断（中文占两格），不够补空格。"""
    if width(text) <= w:
        return text + " " * (w - width(text))
    out, n = "", 0
    for ch in text:
        cw = 2 if unicodedata.east_asian_width(ch) in "WF" else 1
        if n + cw > w - 1:
            break
        out += ch
        n += cw
    return out + "~" + " " * (w - 1 - n)


ANSI = __import__("re").compile(r"\[[0-9;?]*[A-Za-z]")


def clip(line: str, w: int) -> str:
    """整行不超过窗口宽度（超了会换行，刷新时光标就对不上了）；超了就去掉颜色截断。"""
    plain = ANSI.sub("", line)
    return line if width(plain) <= w else fit(plain, w).rstrip()


def size(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.0f} {unit}" if unit in ("B", "KB") else f"{n:.1f} {unit}"
        n /= 1024
    return ""


def eta(seconds: float) -> str:
    if seconds <= 0 or seconds > 99 * 3600:
        return "--:--"
    s = int(seconds)
    return f"{s // 3600}:{s // 60 % 60:02d}:{s % 60:02d}" if s >= 3600 else f"{s // 60}:{s % 60:02d}"


def bar(frac: float, w: int, color: str = "teal") -> str:
    frac = min(max(frac, 0.0), 1.0)
    full = int(frac * w)
    part = " ▏▎▍▌▋▊▉"[int((frac * w - full) * 8)] if full < w else ""
    part = "" if part == " " else part
    return c("█" * full + part, color) + c("░" * (w - full - len(part)), "grey")


LOGO = r"""
   ___                                         _
  / _ \___  ___ ___  __ _ _ __ __ _ _ __ | |__
 / /_)/ _ \/ __/ _ \/ _` | '__/ _` | '_ \| '_ \
/ ___/ (_) \__ \  __/ (_| | | | (_| | |_) | | | |
\/    \___/|___/\___|\__, |_|  \__,_| .__/|_| |_|
                     |___/          |_|
"""


# ------------------------------------------------------------------ 网络

USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PosegraphInstaller/1.0"
PYPI_MIRRORS = {"pypi": "https://files.pythonhosted.org/",
                "tsinghua": "https://pypi.tuna.tsinghua.edu.cn/",
                "aliyun": "https://mirrors.aliyun.com/pypi/",
                "huawei": "https://repo.huaweicloud.com/repository/pypi/",
                "ustc": "https://mirrors.ustc.edu.cn/pypi/"}
GITHUB_PREFIXES = ["https://gh-proxy.com/", "https://gh-proxy.org/", "https://ghfast.top/", "https://gh.llkk.cc/",
                   "https://ghproxy.net/"]
MODELSCOPE = "https://www.modelscope.cn/models/{repo}/resolve/master/{path}"


def make_opener(proxy: str | None):
    """direct / cn：跟系统一样（系统设了代理就走系统代理）；proxy：只走用户填的代理。"""
    if proxy:
        handler = urllib.request.ProxyHandler({"http": f"http://{proxy}", "https": f"http://{proxy}"})
    else:
        handler = urllib.request.ProxyHandler()
    return urllib.request.build_opener(handler)


OPENER = urllib.request.build_opener()


def open_url(url: str, start: int | None = None, end: int | None = None, timeout: float = 30):
    headers = {"User-Agent": USER_AGENT}
    if start is not None:
        headers["Range"] = f"bytes={start}-{'' if end is None else end}"
    return OPENER.open(urllib.request.Request(url, headers=headers), timeout=timeout)


def speed_test(url: str, nbytes: int = 1 << 20, timeout: float = 12) -> float:
    """下前 nbytes 字节的速度（字节 / 秒），失败为 0。"""
    t = time.time()
    try:
        with open_url(url, 0, nbytes - 1, timeout=timeout) as r:
            got = 0
            while got < nbytes:
                b = r.read(65536)
                if not b:
                    break
                got += len(b)
                if time.time() - t > timeout:
                    break
        return got / max(time.time() - t, 1e-3) if got >= min(nbytes, 65536) else 0.0
    except Exception:
        return 0.0


# ------------------------------------------------------------------ 下载

CHUNK = 8 << 20          # 分块大小
SPLIT_MIN = 48 << 20     # 比这个大的文件才分块多线程下


@dataclass
class Job:
    key: str
    label: str
    urls: list[str]
    dest: Path
    size: int
    sha256: str | None
    priority: int
    done: int = 0
    status: str = "wait"          # wait / down / verify / ok / error
    error: str = ""
    url_index: int = 0
    chunks: int = 0
    chunk_done: set = field(default_factory=set)
    threads: int = 0
    attempts: int = 0
    lock: threading.Lock = field(default_factory=threading.Lock)
    samples: list = field(default_factory=list)

    @property
    def url(self) -> str:
        return self.urls[self.url_index % len(self.urls)]

    def state_path(self) -> Path:
        return self.dest.with_name(self.dest.name + ".parts")


class Downloader:
    """多文件同时下、大文件分块多线程（服务器支持 Range 时）。同一个站点同时最多 per_host 个连接。"""

    def __init__(self, workers: int, per_host: int):
        self.jobs: list[Job] = []
        self.tasks: queue.PriorityQueue = queue.PriorityQueue()
        self.seq = 0
        self.workers = workers
        self.per_host = per_host
        self.host_slots: dict[str, threading.Semaphore] = {}
        self.host_lock = threading.Lock()
        self.cond = threading.Condition()
        self.stop = False
        self.bytes_total = 0
        self.bytes_samples: list[tuple[float, int]] = []

    def add(self, job: Job) -> Job:
        self.jobs.append(job)
        if job.dest.exists() and job.dest.stat().st_size == job.size and not job.state_path().exists():
            job.done = job.size
            job.status = "verify"
            self._push(job.priority, ("verify", job, None))
        else:
            self._push(job.priority, ("start", job, None))
        return job

    def _push(self, priority: int, task) -> None:
        self.seq += 1
        self.tasks.put((priority, self.seq, task))

    def start(self) -> None:
        for _ in range(self.workers):
            threading.Thread(target=self._worker, daemon=True).start()

    def downloaded(self) -> int:
        return sum(j.done for j in self.jobs)

    def speed(self) -> float:
        now, total = time.time(), self.downloaded()
        self.bytes_samples.append((now, total))
        self.bytes_samples = [s for s in self.bytes_samples if now - s[0] <= 5]
        t0, b0 = self.bytes_samples[0]
        return (total - b0) / (now - t0) if now - t0 > 0.5 else 0.0

    def _slot(self, url: str) -> threading.Semaphore:
        host = urllib.parse.urlsplit(url).netloc
        with self.host_lock:
            if host not in self.host_slots:
                self.host_slots[host] = threading.Semaphore(self.per_host)
            return self.host_slots[host]

    def _worker(self) -> None:
        while not self.stop:
            try:
                _, _, (kind, job, arg) = self.tasks.get(timeout=0.5)
            except queue.Empty:
                continue
            try:
                if job.status == "error":
                    continue
                {"start": self._start, "chunk": self._chunk, "whole": self._whole, "verify": self._verify}[kind](job, arg)
            except Exception as e:  # noqa: BLE001 —— 任务失败按重试 / 换地址处理
                self._failed(job, kind, arg, e)

    def _failed(self, job: Job, kind: str, arg, e: Exception) -> None:
        with job.lock:
            tries = (arg or {}).get("tries", 0) + 1 if isinstance(arg, dict) else 1
            if tries % 3 == 0:
                job.url_index += 1      # 同一个地址连续失败就换下一个
            if tries > 3 * len(job.urls) + 3:
                job.status = "error"
                job.error = f"{type(e).__name__}: {e}"
                with self.cond:
                    self.cond.notify_all()
                return
        time.sleep(min(2 ** (tries % 4), 8))
        new_arg = dict(arg) if isinstance(arg, dict) else {}
        new_arg["tries"] = tries
        self._push(job.priority, (kind, job, new_arg))

    def _start(self, job: Job, arg) -> None:
        job.status = "down"
        job.dest.parent.mkdir(parents=True, exist_ok=True)
        if job.size >= SPLIT_MIN:
            # 问一下支不支持分块（有的镜像 / 加速站不支持 Range）
            with self._slot(job.url):
                with open_url(job.url, 0, 0, timeout=30) as r:
                    ranged = r.status == 206 and (r.headers.get("Content-Range") or "").endswith(f"/{job.size}")
            if ranged:
                job.chunks = (job.size + CHUNK - 1) // CHUNK
                state = job.state_path()
                if state.exists() and job.dest.exists() and job.dest.stat().st_size == job.size:
                    try:
                        job.chunk_done = set(json.loads(state.read_text()))
                    except Exception:
                        job.chunk_done = set()
                else:
                    with open(job.dest, "wb") as f:
                        f.truncate(job.size)
                    state.write_text("[]")
                job.done = sum(min(CHUNK, job.size - i * CHUNK) for i in job.chunk_done)
                for i in range(job.chunks):
                    if i not in job.chunk_done:
                        self._push(job.priority, ("chunk", job, {"i": i}))
                if len(job.chunk_done) == job.chunks:
                    self._push(job.priority, ("verify", job, None))
                return
        self._push(job.priority, ("whole", job, None))

    def _chunk(self, job: Job, arg) -> None:
        i = arg["i"]
        start = i * CHUNK
        end = min(job.size, start + CHUNK) - 1
        got = 0
        with self._slot(job.url):
            job.threads += 1
            try:
                with open_url(job.url, start, end) as r:
                    if r.status != 206:
                        raise IOError(f"HTTP {r.status} (no range)")
                    buf = bytearray()
                    while True:
                        b = r.read(65536)
                        if not b:
                            break
                        buf += b
                        got += len(b)
                        with job.lock:
                            job.done += len(b)
                    if len(buf) != end - start + 1:
                        raise IOError(f"short read {len(buf)}/{end - start + 1}")
            except Exception:
                with job.lock:
                    job.done -= got
                raise
            finally:
                job.threads -= 1
        with job.lock:
            with open(job.dest, "r+b") as f:
                f.seek(start)
                f.write(buf)
            job.chunk_done.add(i)
            job.state_path().write_text(json.dumps(sorted(job.chunk_done)))
            finished = len(job.chunk_done) == job.chunks
        if finished:
            self._push(job.priority, ("verify", job, None))

    def _whole(self, job: Job, arg) -> None:
        tmp = job.dest.with_name(job.dest.name + ".tmp")
        got = 0
        with self._slot(job.url):
            job.threads = 1
            try:
                with open_url(job.url, timeout=60) as r, open(tmp, "wb") as f:
                    while True:
                        b = r.read(65536)
                        if not b:
                            break
                        f.write(b)
                        got += len(b)
                        with job.lock:
                            job.done += len(b)
            except Exception:
                with job.lock:
                    job.done -= got
                raise
            finally:
                job.threads = 0
        if job.size and got != job.size:
            with job.lock:
                job.done -= got
            raise IOError(f"size {got} != {job.size}")
        os.replace(tmp, job.dest)
        if not job.size:
            job.size = got
        self._verify(job, None)

    def _verify(self, job: Job, arg) -> None:
        job.status = "verify"
        if job.sha256:
            h = hashlib.sha256()
            with open(job.dest, "rb") as f:
                for b in iter(lambda: f.read(4 << 20), b""):
                    h.update(b)
            if h.hexdigest() != job.sha256:
                # 内容不对（镜像坏了 / 被改过）：删掉，换下一个地址重下
                job.dest.unlink(missing_ok=True)
                job.state_path().unlink(missing_ok=True)
                with job.lock:
                    job.done = 0
                    job.chunk_done = set()
                    job.url_index += 1
                    job.attempts += 1
                    if job.attempts > len(job.urls) + 1:
                        job.status = "error"
                        job.error = L("校验不符（SHA-256），每个下载地址都试过了", "checksum mismatch from every source")
                        with self.cond:
                            self.cond.notify_all()
                        return
                    job.status = "wait"
                self._push(job.priority, ("start", job, None))
                return
        job.state_path().unlink(missing_ok=True)
        job.done = job.size
        job.status = "ok"
        with self.cond:
            self.cond.notify_all()


# ------------------------------------------------------------------ 安装

@dataclass
class Step:
    key: str
    label: str
    status: str = "wait"    # wait / run / ok / error
    detail: str = ""


def canon(name: str) -> str:
    """包名规范化（同 packaging.utils.canonicalize_name）：小写，- _ . 连着的都换成一个 -。"""
    return re.sub(r"[-_.]+", "-", name).lower()


def installed_dists(site: Path) -> dict[str, str]:
    """site-packages 里装了哪些包：{规范化的名字: 版本}（看 *.dist-info 目录名，不用启动 Python）。"""
    result = {}
    if site.is_dir():
        for d in site.glob("*.dist-info"):
            m = re.match(r"^(.+?)-([^-]+)\.dist-info$", d.name)
            if m:
                result[canon(m.group(1))] = m.group(2)
    return result


def version_tuple(v: str) -> tuple:
    return tuple(int(x) for x in re.findall(r"\d+", v or "")[:3])


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(4 << 20), b""):
            h.update(b)
    return h.hexdigest()


class Installer:
    """
    flavor：装哪种 PyTorch（cu128 新 N 卡、cu126 老 N 卡、cpu、rocm AMD 卡），决定用哪套 Python 环境（lock.json 的 envs）。
    开始前先看一遍目录里已经有什么（survey）：装对了的跳过，缺的、版本不对的、坏了的才下载安装——
    新装、接着上次装、更新 / 修复已有的一键包都是同一套流程。
    """

    def __init__(self, root: Path, lock: dict, mode: str, proxy: str | None, flavor: str):
        self.root = root
        self.lock = lock
        self.mode = mode
        self.proxy = proxy
        self.flavor = flavor
        self.torch = lock["torch"][flavor]
        self.env_name = self.torch["env"]
        self.env = lock["envs"][self.env_name]
        self.packages = self.env["packages"]
        self.comp_packages = self.env.get("components", {})    # cp312：组件里编译过的包从 PyPI 装
        self.py = root / "python" / "python.exe"
        self.site = root / "python" / "Lib" / "site-packages"
        self.dl = root / ".download"
        self.log_path = root / "install.log"
        self.state_path = root / ".install-state.json"
        self.state: dict = {}
        self.steps: dict[str, Step] = {}
        self.downloader: Downloader | None = None
        self.messages: list[str] = []
        self.failed = False
        self.started = time.time()
        self.sources: dict[str, str] = {}

    # ---- 记录
    def log(self, text: str) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        with open(self.log_path, "a", encoding="utf-8") as f:
            f.write(time.strftime("[%H:%M:%S] ") + text + "\n")

    def mark(self, key: str) -> None:
        self.state[key] = True
        self.state_path.write_text(json.dumps(self.state, indent=1), encoding="utf-8")

    def step(self, key: str, label: str) -> Step:
        s = self.steps.setdefault(key, Step(key, label))
        if self.state.get(key):
            s.status = "ok"
        return s

    # ---- 看看已经装了什么
    def survey(self, save: bool = True) -> None:
        """按磁盘上的实际情况定哪些已经好了（不看上次的记录：中途删过、换过版本、坏了的都能发现）。save：记进 .install-state.json。"""
        st: dict = {}
        dists = installed_dists(self.site)

        def has(p: dict) -> bool:
            return dists.get(canon(p["name"])) == p["version"]
        if has(self.env["pip"]):
            st["pip"] = True
        for p in self.packages:
            if has(p):
                st["pkg:" + p["name"]] = True
        if all(st.get(f"pkg:{n}") for n in ("setuptools", "wheel")):
            st["build"] = True
        if all(st.get("pkg:" + p["name"]) for p in self.packages):
            st["packages"] = True
        if all(has(f) for f in self.torch["files"]):
            st["torch"] = True
        sam = self.root / "backend" / "third_party" / "sam-3d-body"
        if self._files_match(sam, self.lock["sam3d"]["files"], skip=("notebook/",)):
            st["sam3d"] = True
        w = self.lock["weights"]
        wdir = self.root / "backend" / "checkpoints" / w["dir"]
        if all((wdir / f["path"]).is_file() and (wdir / f["path"]).stat().st_size == f["size"] for f in w["files"]):
            st["weights"] = True
        pg = self.lock["posegraph"]
        backend = {rel[len("backend/"):]: h for rel, h in pg["files"].items()
                   if rel.startswith("backend/") and not rel.startswith(("backend/tests/", "backend/outputs/"))}
        installed = self._backend_version()
        if self._files_match(self.root / "backend", backend) or version_tuple(installed) > version_tuple(pg["version"]):
            st["posegraph"] = True   # 插件里已经「更新后端」到比清单还新的版本时不往回装
        for comp in self.lock["components"]:
            key = "component-" + comp["id"]
            extra = self.comp_packages.get(comp["id"], {}).get("packages", [])
            if all((self.root / p).exists() for p in comp["check"]) and all(has(p) for p in extra):
                st[key] = True
        runtime = self._runtime()
        if all(st.get(k) for k in self._keys()) and runtime.get("flavor", "cu128") == self.flavor \
                and runtime.get("posegraph") in (pg["version"], installed):
            st["finish"] = True
        self.state = st
        if save:
            self.state_path.parent.mkdir(parents=True, exist_ok=True)
            self.state_path.write_text(json.dumps(st, indent=1), encoding="utf-8")

    def _keys(self) -> list[str]:
        return ["pip", "build", "packages", "torch", "sam3d", "weights", "posegraph"] + ["component-" + c["id"] for c in self.lock["components"]]

    def _backend_version(self) -> str:
        try:
            return json.loads((self.root / "backend" / "src" / "posegraph" / "version.json").read_text(encoding="utf-8"))["version"]
        except Exception:
            return ""

    def _runtime(self) -> dict:
        try:
            return json.loads((self.root / "runtime.json").read_text(encoding="utf-8"))
        except Exception:
            return {}

    @staticmethod
    def _files_match(base: Path, files: dict, skip: tuple[str, ...] = ()) -> bool:
        """目录里这些文件都在、SHA-256 都对（发布的一键包是 git 在 Windows 上检出的，换行是 CRLF，换成 LF 再比一次）。"""
        for rel, digest in files.items():
            if any(rel.startswith(s) for s in skip):
                continue
            p = base / rel
            if not p.is_file():
                return False
            data = p.read_bytes()
            if hashlib.sha256(data).hexdigest() != digest and hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest() != digest:
                return False
        return True

    def todo(self) -> list[str]:
        return [k for k in self._keys() + ["finish"] if not self.state.get(k)]

    # ---- 选源
    def torch_mirrors(self) -> dict[str, str]:
        base = self.torch["base"]
        if not self.torch.get("mirrors"):
            return {"official": base}
        return {"pytorch": base, "aliyun": f"https://mirrors.aliyun.com/pytorch-wheels/{self.flavor}/"}

    def choose_sources(self) -> None:
        lock = self.lock
        say = lambda t: print("  " + t, flush=True)
        if self.mode == "cn":
            say(c(L("测速：挑最快的国内镜像和 GitHub 加速…", "Speed test: picking the fastest mirrors…"), "cyan"))
            numpy = next(p for p in self.packages if p["name"] == "numpy")
            pypi = self._fastest("PyPI", {k: v + numpy["path"] for k, v in PYPI_MIRRORS.items() if k != "pypi"}, say)
            mirrors = self.torch_mirrors()
            torch = next(iter(mirrors))
            if len(mirrors) > 1 and not self.state.get("torch"):
                small = min(self.torch["files"], key=lambda f: f["size"])["file"]   # 小的那个测速
                torch = self._fastest("PyTorch", {k: v + urllib.parse.quote(small) for k, v in mirrors.items()}, say)
            face = next(x for x in lock["components"] if x["id"] == "face")["url"]
            gh = self._fastest("GitHub", {p: p + face for p in GITHUB_PREFIXES} | {"direct": face}, say)
            self.sources = {"pypi": pypi, "torch": torch, "github": gh}
        else:
            self.sources = {"pypi": "pypi", "torch": next(iter(self.torch_mirrors())), "github": "direct"}

    def _fastest(self, what: str, candidates: dict[str, str], say) -> str:
        results = {}
        threads = []

        def run(k, u):
            results[k] = speed_test(u)
        for k, u in candidates.items():
            t = threading.Thread(target=run, args=(k, u), daemon=True)
            t.start()
            threads.append(t)
        for t in threads:
            t.join(20)
        ranked = sorted(results.items(), key=lambda kv: -kv[1])
        line = "  ".join(f"{k.replace('https://', '').rstrip('/')} {c(size(v) + '/s', 'green') if v else c('×', 'red')}" for k, v in ranked)
        say(f"{c(what, 'bold')}: {line}")
        best = ranked[0][0] if ranked and ranked[0][1] > 0 else next(iter(candidates))
        return best

    def urls_pypi(self, path: str) -> list[str]:
        first = self.sources["pypi"]
        order = [first] + [k for k in PYPI_MIRRORS if k != first] if self.mode == "cn" else ["pypi"] + (["tsinghua", "aliyun"] if self.mode != "proxy" else [])
        return [PYPI_MIRRORS[k] + path for k in dict.fromkeys(order)]

    def urls_torch(self, file: str) -> list[str]:
        mirrors = self.torch_mirrors()
        first = self.sources.get("torch") or next(iter(mirrors))
        order = [first] + [k for k in mirrors if k != first]
        return [mirrors[k] + urllib.parse.quote(file) for k in order if k in mirrors]

    def urls_github(self, url: str) -> list[str]:
        first = self.sources["github"]
        if self.mode != "cn":
            return [url] + ([p + url for p in GITHUB_PREFIXES[:2]] if self.mode == "direct" else [])
        others = [p for p in GITHUB_PREFIXES if p != first]
        return [(url if first == "direct" else first + url)] + [p + url for p in others] + ([url] if first != "direct" else [])

    # ---- 下载清单
    def plan(self) -> Downloader:
        lock = self.lock
        workers = 12 if self.mode == "cn" else 10
        d = Downloader(workers=workers, per_host=6)
        self.dl.mkdir(parents=True, exist_ok=True)
        self.step("pip", L("pip 包管理器", "pip"))
        self.step("build", L("构建工具（setuptools、wheel）", "Build tools (setuptools, wheel)"))
        self.step("packages", L(f"Python 依赖（{len(self.packages)} 个包）", f"Python packages ({len(self.packages)})"))
        torch_ver = next(f["version"] for f in self.torch["files"] if f["name"] == "torch")
        self.step("torch", f"PyTorch {torch_ver.split('+')[0]} ({self.torch['label']})")
        self.step("sam3d", L("SAM 3D Body 源码", "SAM 3D Body source"))
        self.step("weights", L("SAM 3D Body 权重", "SAM 3D Body weights"))
        self.step("posegraph", L(f"Posegraph 后端 {lock['posegraph']['version']}", f"Posegraph backend {lock['posegraph']['version']}"))
        for comp in lock["components"]:
            self.step("component-" + comp["id"], (comp["name"] if LANG == "zh" else comp["nameEn"]))
        self.step("finish", L("收尾与自检", "Finishing and self-test"))

        p = self.env["pip"]
        if not self.state.get("pip"):
            d.add(Job("pip", p["file"], self.urls_pypi(p["path"]), self.dl / p["file"], p["size"], p["sha256"], 0))
        if not self.state.get("packages") or not self.state.get("build"):
            for pk in sorted(self.packages, key=lambda x: x["name"] not in ("setuptools", "wheel")):
                if self.state.get("pkg:" + pk["name"]):
                    continue
                d.add(Job("pkg:" + pk["name"], pk["file"], self.urls_pypi(pk["path"]), self.dl / pk["file"], pk["size"], pk["sha256"],
                          1 if pk["name"] in ("setuptools", "wheel") else 3))
        if not self.state.get("torch"):
            for f in self.torch["files"]:
                d.add(Job("torch:" + f["name"], f["file"], self.urls_torch(f["file"]), self.dl / f["file"], f["size"], f["sha256"], 2))
        if not self.state.get("sam3d"):
            d.add(Job("sam3d", "sam-3d-body.zip", self.urls_github(lock["sam3d"]["url"]), self.dl / "sam-3d-body.zip", 0, None, 4))
        if not self.state.get("posegraph"):
            d.add(Job("posegraph", f"Posegraph-{lock['posegraph']['version']}.zip", self.urls_github(lock["posegraph"]["url"]),
                      self.dl / "posegraph.zip", 0, None, 4))
        if not self.state.get("weights"):
            w = lock["weights"]
            for f in w["files"]:
                dest = self.root / "backend" / "checkpoints" / w["dir"] / f["path"]
                if dest.is_file() and dest.stat().st_size == f["size"] and not dest.with_name(dest.name + ".parts").exists():
                    continue
                d.add(Job("weight:" + f["path"], Path(f["path"]).name, [MODELSCOPE.format(repo=w["repo"], path=f["path"])], dest,
                          f["size"], f["sha256"], 2))
        for comp in lock["components"]:
            key = "component-" + comp["id"]
            if self.state.get(key):
                continue
            if not all((self.root / p).exists() for p in comp["check"]) or self.comp_packages.get(comp["id"]):
                d.add(Job(key, Path(comp["url"]).name, self.urls_github(comp["url"]), self.dl / (comp["id"] + ".zip"),
                          comp["size"], comp["sha256"], 5))
            for pk in self.comp_packages.get(comp["id"], {}).get("packages", []):
                d.add(Job(f"cpkg:{comp['id']}:{pk['name']}", pk["file"], self.urls_pypi(pk["path"]), self.dl / pk["file"],
                          pk["size"], pk["sha256"], 5))
        self.downloader = d
        return d

    # ---- 安装（另一个线程：下完一个装一个）
    def ready(self, key: str) -> bool:
        j = next((j for j in self.downloader.jobs if j.key == key), None)
        return j is None or j.status == "ok"

    def job_error(self) -> Job | None:
        return next((j for j in self.downloader.jobs if j.status == "error"), None)

    def pip(self, *args: str) -> None:
        env = dict(os.environ, PYTHONNOUSERSITE="1", PIP_CONFIG_FILE=os.devnull, PIP_DISABLE_PIP_VERSION_CHECK="1",
                   PIP_NO_INPUT="1", PYTHONUTF8="1", PIP_NO_CACHE_DIR="1")
        env.pop("PYTHONPATH", None)
        cmd = [str(self.py), "-m", "pip", "install", "--no-index", "--no-deps", "--no-compile", "--no-warn-script-location",
               "--disable-pip-version-check", *args]
        self.run(cmd, env)

    def run(self, cmd: list[str], env: dict | None = None, cwd: Path | None = None) -> str:
        self.log("$ " + " ".join(cmd))
        p = subprocess.run(cmd, env=env, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.log(p.stdout + p.stderr)
        if p.returncode != 0:
            tail = [l for l in (p.stderr or p.stdout).strip().splitlines() if l.strip()][-3:]
            raise RuntimeError(" / ".join(tail) or f"exit {p.returncode}")
        return p.stdout

    def install_loop(self) -> None:
        try:
            self._install_all()
        except Exception as e:  # noqa: BLE001
            self.failed = True
            self.log(traceback.format_exc())
            for s in self.steps.values():
                if s.status == "run":
                    s.status, s.detail = "error", str(e)[:200]
                    break
            else:
                self.messages.append(str(e))
        finally:
            with self.downloader.cond:
                self.downloader.cond.notify_all()

    def wait_for(self, keys: list[str], step: Step) -> bool:
        d = self.downloader
        with d.cond:
            while not all(self.ready(k) for k in keys):
                if (j := self.job_error()) is not None and j.key in keys:
                    step.status, step.detail = "error", L(f"下载失败：{j.label}：{j.error}", f"download failed: {j.label}: {j.error}")
                    raise RuntimeError(step.detail)
                d.cond.wait(0.5)
        return True

    def _install_all(self) -> None:
        lock, steps = self.lock, self.steps
        # pip
        if not self.state.get("pip"):
            s = steps["pip"]
            self.wait_for(["pip"], s)
            s.status = "run"
            whl = self.dl / self.env["pip"]["file"]
            # 新版 pip 只肯用「python -m pip」改它自己：把轮子放进 PYTHONPATH 当 pip 来跑
            env = dict(os.environ, PYTHONNOUSERSITE="1", PIP_CONFIG_FILE=os.devnull, PIP_NO_CACHE_DIR="1", PYTHONPATH=str(whl))
            self.run([str(self.py), "-m", "pip", "install", "--no-index", "--no-deps", "--no-warn-script-location", str(whl)], env)
            whl.unlink(missing_ok=True)
            s.status = "ok"
            self.mark("pip")
        # 构建工具，然后其余 Python 包：有几个就先装几个
        if not self.state.get("build"):
            s = steps["build"]
            names = [n for n in ("setuptools", "wheel") if not self.state.get("pkg:" + n)]
            self.wait_for([f"pkg:{n}" for n in names], s)
            s.status = "run"
            self.install_packages(names)
            s.status = "ok"
            self.mark("build")
        packages = [p for p in self.packages if p["name"] not in ("setuptools", "wheel")]
        torch_keys = ["torch:" + f["name"] for f in self.torch["files"]]
        s = steps["packages"]
        while not self.state.get("packages"):
            todo = [p for p in packages if not self.state.get("pkg:" + p["name"])]
            if not todo:
                s.status = "ok"
                self.mark("packages")
                break
            ready = [p for p in todo if self.ready("pkg:" + p["name"])]
            if not ready:
                # 等的时候顺便把已经下好的 PyTorch 装了
                if not self.state.get("torch") and all(self.ready(k) for k in torch_keys):
                    self.install_torch()
                    continue
                err = self.job_error()
                if err is not None and err.key.startswith("pkg:"):
                    self.wait_for([err.key], s)        # 抛出下载失败
                with self.downloader.cond:
                    self.downloader.cond.wait(1.0)
                continue
            s.status = "run"
            s.detail = L(f"已装 {len(packages) - len(todo)}/{len(packages)}，正在装 {len(ready)} 个", f"{len(packages) - len(todo)}/{len(packages)} installed, installing {len(ready)}")
            self.install_packages([p["name"] for p in ready])
        s.detail = ""
        if not self.state.get("torch"):
            self.wait_for(torch_keys, steps["torch"])
            self.install_torch()
        # 源码、权重、组件
        if not self.state.get("sam3d"):
            st = steps["sam3d"]
            self.wait_for(["sam3d"], st)
            st.status = "run"
            self.extract_archive(self.dl / "sam-3d-body.zip", lock["sam3d"], {"": self.root / "backend" / "third_party" / "sam-3d-body"},
                                 skip=("notebook/",))
            st.status = "ok"
            self.mark("sam3d")
        if not self.state.get("posegraph"):
            st = steps["posegraph"]
            self.wait_for(["posegraph"], st)
            st.status = "run"
            self.install_posegraph()
            st.status = "ok"
            self.mark("posegraph")
        if not self.state.get("weights"):
            st = steps["weights"]
            self.wait_for([j.key for j in self.downloader.jobs if j.key.startswith("weight:")], st)
            st.status = "ok"
            self.mark("weights")
        for comp in lock["components"]:
            key = "component-" + comp["id"]
            if self.state.get(key):
                continue
            st = steps[key]
            extra = self.comp_packages.get(comp["id"], {})
            keys = [j.key for j in self.downloader.jobs if j.key == key or j.key.startswith(f"cpkg:{comp['id']}:")]
            self.wait_for(keys, st)
            st.status = "run"
            zip_path = self.dl / (comp["id"] + ".zip")
            if zip_path.exists():
                self.extract_component(zip_path, skip=tuple(extra.get("skip", [])))
                zip_path.unlink(missing_ok=True)
            if extra.get("packages"):
                files = [self.dl / p["file"] for p in extra["packages"]]
                self.pip(*map(str, files))
                for f in files:
                    f.unlink(missing_ok=True)
            st.status = "ok"
            self.mark(key)
        self.finish()

    def install_packages(self, names: list[str]) -> None:
        if not names:
            return
        by_name = {p["name"]: p for p in self.packages}
        files = [self.dl / by_name[n]["file"] for n in names]
        sdists = [f for f in files if not f.name.endswith(".whl")]
        wheels = [f for f in files if f.name.endswith(".whl")]
        if wheels:
            self.pip(*map(str, wheels))
        if sdists:
            self.pip("--no-build-isolation", *map(str, sdists))
        for n, f in zip(names, files):
            f.unlink(missing_ok=True)
            self.mark("pkg:" + n)

    def install_torch(self) -> None:
        """按清单顺序装：ROCm 先装运行库和 rocm 元包（源码包），再装 torch / torchvision。换了版本时 pip 会先卸掉旧的。"""
        st = self.steps["torch"]
        st.status = "run"
        st.detail = L("解压安装好几个 GB，要几分钟", "unpacking several GB, takes a few minutes")
        files = self.torch["files"]
        runtime = [f for f in files if f["name"] not in ("torch", "torchvision")]
        main = [f for f in files if f["name"] in ("torch", "torchvision")]
        wheels = [self.dl / f["file"] for f in runtime if not f.get("sdist")]
        sdists = [self.dl / f["file"] for f in runtime if f.get("sdist")]
        if wheels:
            self.pip(*map(str, wheels))
        if sdists:
            self.pip("--no-build-isolation", *map(str, sdists))
        self.pip(*(str(self.dl / f["file"]) for f in main))
        for f in files:
            (self.dl / f["file"]).unlink(missing_ok=True)
        st.status, st.detail = "ok", ""
        self.mark("torch")

    def extract_archive(self, path: Path, info: dict, targets: dict[str, Path], skip: tuple[str, ...] = ()) -> None:
        """GitHub 源码压缩包：每个文件按锁定的 SHA-256 核对后解压；targets：压缩包里的前缀 → 目标目录（最长前缀优先）。"""
        expected = info["files"]
        with zipfile.ZipFile(path) as z:
            root = z.namelist()[0].split("/")[0] + "/"
            names = {n[len(root):]: n for n in z.namelist() if not n.endswith("/") and n.startswith(root)}
            for rel, digest in expected.items():
                if rel not in names:
                    raise RuntimeError(L(f"源码包里少了 {rel}", f"{rel} missing from the archive"))
            for rel, name in names.items():
                if rel not in expected or any(rel.startswith(s) for s in skip):
                    continue
                prefix = max((p for p in targets if rel.startswith(p)), key=len, default=None)
                if prefix is None:
                    continue
                data = z.read(name)
                if hashlib.sha256(data).hexdigest() != expected[rel]:
                    raise RuntimeError(L(f"源码文件校验不符：{rel}（加速站改了内容？换一个网络方式重试）",
                                         f"source file checksum mismatch: {rel}"))
                out = targets[prefix] / rel[len(prefix):]
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_bytes(data)
        path.unlink(missing_ok=True)

    def install_posegraph(self) -> None:
        """后端源码 backend/、启动脚本和说明 packaging/package/、许可证。"""
        root = self.root
        self.extract_archive(self.dl / "posegraph.zip", self.lock["posegraph"],
                             {"backend/": root / "backend", "packaging/package/": root, "LICENSE": root / "LICENSES" / "Posegraph-LICENSE.txt",
                              "THIRD_PARTY_NOTICES.md": root / "LICENSES" / "THIRD_PARTY_NOTICES.md"},
                             skip=("backend/tests/", "backend/outputs/"))

    def extract_component(self, path: Path, skip: tuple[str, ...] = ()) -> None:
        """组件压缩包解到安装目录；skip：不解的路径前缀（cp312 环境里组件带的 cp311 编译包，改从 PyPI 装）。"""
        root = self.root.resolve()
        with zipfile.ZipFile(path) as z:
            members = []
            for info in z.infolist():
                target = (root / info.filename).resolve()
                if root not in target.parents or str(target).lower().startswith(str(root / "backend" / "src").lower()):
                    raise RuntimeError(L("组件压缩包里有不安全的路径", "unsafe path in component archive"))
                name = info.filename.replace("\\", "/")
                if any(name == s or name.startswith(s + "/") for s in skip):
                    continue
                members.append(info)
            z.extractall(root, members)

    def finish(self) -> None:
        st = self.steps["finish"]
        st.status = "run"
        root, lock = self.root, self.lock
        lic = root / "LICENSES"
        lic.mkdir(exist_ok=True)
        for src, name in ((root / "python" / "LICENSE.txt", "Python-LICENSE.txt"),
                          (root / "backend" / "third_party" / "sam-3d-body" / "LICENSE", "SAM-3D-Body-code-LICENSE.txt"),
                          (root / "backend" / "checkpoints" / lock["weights"]["dir"] / "LICENSE", "SAM-3D-Body-weights-LICENSE.txt")):
            if src.exists():
                shutil.copyfile(src, lic / name)
        torch_ver = next(f["version"] for f in self.torch["files"] if f["name"] == "torch")
        # runtime：和插件的「更新后端」约定的运行环境代号（Python + 依赖变了才加一）；flavor：这里装的是哪种 PyTorch
        (root / "runtime.json").write_text(json.dumps({"runtime": lock["posegraph"]["runtime"], "python": self.env["python"]["version"],
                                                       "torch": torch_ver, "flavor": self.flavor,
                                                       "posegraph": self._backend_version() or lock["posegraph"]["version"]},
                                                      indent=4), encoding="ascii")
        st.detail = L("试着导入全部模块、在显卡上算一下…", "importing everything, a quick GPU check…")
        env = dict(os.environ, PYTHONPATH=str(root / "backend" / "src"), PYTHONNOUSERSITE="1", PYTHONUTF8="1")
        code = ("import torch, torchvision, cv2, scipy, fastapi, uvicorn, timm, mediapipe, moge, av, onnxruntime; "
                "import posegraph.app, posegraph.face, posegraph.depth\n"
                "ok = torch.cuda.is_available()\n"
                "name = torch.cuda.get_device_name(0) if ok else ''\n"
                "if ok:\n"
                "    x = torch.randn(256, 256, device='cuda'); ok = bool(torch.isfinite(x @ x).all().item())\n"
                "print(torch.__version__, ok, name)")
        out = self.run([str(self.py), "-c", code], env, cwd=root / "backend").strip().splitlines()[-1]
        ver, gpu_ok, *gpu = out.split(" ", 2)
        name = gpu[0] if gpu else ""
        if self.flavor == "cpu":
            msg = L(f"PyTorch {ver}（CPU 版）：识别用 CPU，每张约 10~20 秒", f"PyTorch {ver} (CPU build): recognition runs on the CPU, about 10-20 s per image")
        elif gpu_ok == "True":
            msg = L(f"PyTorch {ver}，显卡可用：{name}", f"PyTorch {ver}, GPU: {name}")
        else:
            card = L("AMD 显卡（ROCm）", "AMD GPU (ROCm)") if self.flavor == "rocm" else L("NVIDIA 显卡", "NVIDIA GPU")
            msg = L(f"PyTorch {ver}：没能用上{card}，会用 CPU 识别。显卡驱动更新到最新后再运行一次安装（选同一个文件夹）试试",
                    f"PyTorch {ver}: the {card} isn't usable, recognition will run on the CPU. Update the graphics driver and run the installer again on the same folder")
        self.messages.append(msg)
        shutil.rmtree(self.dl, ignore_errors=True)
        st.status, st.detail = "ok", ""
        self.mark("finish")


# ------------------------------------------------------------------ 界面

SPIN = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"


def render(inst: Installer, frame: int, cols: int, rows: int = 40) -> list[str]:
    """整块界面；高度不超过窗口（超了光标退不回顶上，旧画面会一层层叠下去）：先少列几个文件，还不够就把安装步骤并成一行。"""
    steps = list(inst.steps.values())
    fixed = 5 + len(steps)                     # 总进度、空行、已完成、空行、「安装」标题 + 每个步骤
    files = max(0, min(6, rows - 1 - fixed))
    compact = rows - 1 - fixed < 0
    return _render(inst, frame, cols, files, compact)


def _render(inst: Installer, frame: int, cols: int, files: int, compact: bool) -> list[str]:
    d = inst.downloader
    total = sum(j.size for j in d.jobs if j.size) or 1
    done = d.downloaded()
    spd = d.speed()
    left = max(total - done, 0)
    lines = []
    head = L("下载", "Download")
    lines.append(f" {c(head, 'bold')} {bar(done / total, max(10, min(50, cols - 62)))} {c(f'{done / total * 100:5.1f}%', 'white', 'bold')}"
                 f" {size(done):>9}/{size(total):<9} {c(size(spd) + '/s', 'cyan'):>10} {c(eta(left / spd) if spd > 1 else '--:--', 'grey')}")
    active = [j for j in d.jobs if j.status in ("down", "verify")]
    active.sort(key=lambda j: (-(j.size or 0)))
    shown = active[:files]
    lines.append("")
    for j in shown:
        frac = j.done / j.size if j.size else 0
        tag = c(L("校验", "verify"), "yellow") if j.status == "verify" else c(f"×{j.threads}" if j.threads else "  ", "grey")
        lines.append(f"   {fit(j.label, 30)} {bar(frac, max(8, min(22, cols - 70)), 'blue')} {frac * 100:5.1f}% {size(j.done):>9}/{size(j.size or j.done):<9} {tag}")
    waiting = sum(1 for j in d.jobs if j.status == "wait")
    finished = sum(1 for j in d.jobs if j.status == "ok")
    for _ in range(files - len(shown)):
        lines.append("")
    lines.append(c(f"   {L('已完成', 'done')} {finished}/{len(d.jobs)}   {L('排队', 'queued')} {waiting}", "grey"))
    lines.append("")
    if compact:
        steps = list(inst.steps.values())
        ok = sum(1 for s in steps if s.status == "ok")
        cur = next((s for s in steps if s.status in ("run", "error")), None)
        now = (c(SPIN[frame % len(SPIN)], "cyan") + " " + cur.label + ("  " + cur.detail if cur.detail else "")) if cur else ""
        lines.append(f" {c(L('安装', 'Install'), 'bold')} {ok}/{len(steps)}  {now}")
        return [clip(ln, cols - 2) for ln in lines]
    lines.append(f" {c(L('安装', 'Install'), 'bold')}")
    for s in inst.steps.values():
        icon = {"ok": c("✔", "green"), "run": c(SPIN[frame % len(SPIN)], "cyan"), "error": c("✘", "red"), "wait": c("·", "grey")}[s.status]
        room = max(cols - width(s.label) - 10, 10)
        text = s.label + (c("  " + fit(s.detail, room).rstrip(), "grey" if s.status != "error" else "red") if s.detail else "")
        lines.append(f"   {icon} {text}")
    return [clip(ln, cols - 2) for ln in lines]


def dashboard(inst: Installer) -> None:
    frame, last_h = 0, 0
    sys.stdout.write("\x1b[?25l" if VT else "")
    try:
        while True:
            finished = inst.state.get("finish") or inst.failed
            cols, rows = shutil.get_terminal_size((110, 30))
            lines = render(inst, frame, cols, rows)
            if VT:
                out = (f"\x1b[{last_h}F" if last_h else "") + "".join("\x1b[2K" + ln + "\n" for ln in lines)
                sys.stdout.write(out)
                last_h = len(lines)
            elif frame % 25 == 0:
                print(lines[0])
            sys.stdout.flush()
            if finished:
                break
            frame += 1
            time.sleep(0.2)
    finally:
        sys.stdout.write("\x1b[?25h" if VT else "")


# ------------------------------------------------------------------ 主流程

def main() -> int:
    global LANG, VT, OPENER
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True, type=Path)
    ap.add_argument("--mode", choices=["direct", "cn", "proxy"], default="direct")
    ap.add_argument("--proxy", default=None)
    ap.add_argument("--lang", default="zh")
    ap.add_argument("--flavor", choices=["cu128", "cu126", "cpu", "rocm"], default="cu128")
    ap.add_argument("--lock", type=Path, default=HERE / "lock.json")
    ap.add_argument("--check", action="store_true", help="只看要装什么（JSON 输出到标准输出），不下载不改东西")
    args = ap.parse_args()
    LANG = "zh" if args.lang.startswith("zh") else "en"
    VT = enable_vt() and not args.check
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    OPENER = make_opener(args.proxy if args.mode == "proxy" else None)
    lock = json.loads(args.lock.read_text(encoding="utf-8"))
    if lock.get("format", 1) != 2:
        print(c("  " + L("清单格式不对，请下载最新的安装脚本。", "Unsupported lock file; download the latest installer."), "red"))
        return 1
    inst = Installer(args.root.resolve(), lock, args.mode, args.proxy, args.flavor)
    if args.check:
        inst.state = {}
        if inst.root.exists():
            inst.survey(save=False)
        inst.sources = {"pypi": "pypi", "torch": next(iter(inst.torch_mirrors())), "github": "direct"}
        existed = inst.dl.exists()
        jobs = inst.plan().jobs if inst.root.exists() else []
        if not existed and inst.dl.exists() and not any(inst.dl.iterdir()):
            inst.dl.rmdir()
        print(json.dumps({"todo": inst.todo(), "bytes": sum(j.size for j in jobs), "runtime": inst._runtime()}, ensure_ascii=False))
        return 0
    inst.log(f"start mode={args.mode} proxy={args.proxy} flavor={args.flavor} root={inst.root}")
    inst.survey()
    todo = inst.todo()
    if not todo:
        print(c("  " + L(f"这里已经是最新的（后端 {lock['posegraph']['version']}，{inst.torch['label']}），所有文件都核对过，不用装。",
                         f"Already up to date (backend {lock['posegraph']['version']}, {inst.torch['label']}); every file checked."), "green"))
        return 0
    if inst.state:
        names = {"pip": "pip", "build": L("构建工具", "build tools"), "packages": L("Python 依赖", "Python packages"), "torch": "PyTorch",
                 "sam3d": L("SAM 3D Body 源码", "SAM 3D Body source"), "weights": L("权重", "weights"),
                 "posegraph": L("后端代码", "backend code"), "finish": L("收尾自检", "self-test")}
        names |= {"component-" + x["id"]: (x["name"] if LANG == "zh" else x["nameEn"]) for x in lock["components"]}
        print("  " + L("已有的都核对过了，只补这些：", "Everything present was checked; only these are needed: ")
              + c(L("、", ", ").join(names.get(k, k) for k in todo), "yellow"))
    inst.choose_sources()
    d = inst.plan()
    total = sum(j.size for j in d.jobs)
    print()
    print("  " + L(f"要下载约 {size(total)}（加上源码包）。中途关掉窗口也没关系，再运行会接着下。",
                   f"About {size(total)} to download (plus source archives). You can close the window and rerun to resume."))
    print()
    d.start()
    t = threading.Thread(target=inst.install_loop, daemon=True)
    t.start()
    dashboard(inst)
    d.stop = True
    print()
    if inst.state.get("finish"):
        mins = (time.time() - inst.started) / 60
        print(c(r"""
     ____                   _
    |  _ \  ___  _ __   ___| |
    | | | |/ _ \| '_ \ / _ \ |
    | |_| | (_) | | | |  __/_|
    |____/ \___/|_| |_|\___(_)
""", "teal"))
        print("  " + c(L(f"装好了！用了 {mins:.0f} 分钟。", f"Done in {mins:.0f} min."), "green", "bold"))
        for m in inst.messages:
            print("  " + m)
        return 0
    err = inst.job_error()
    print(c("  " + L("安装没完成：", "Install did not finish: "), "red", "bold")
          + c((f"{err.label}：{err.error}" if err else "") + " ".join(s.detail for s in inst.steps.values() if s.status == "error")
              + " ".join(inst.messages), "red"))
    print("  " + L(f"详细记录在 {inst.log_path}。再运行一次会接着下、跳过已经装好的；也可以换一种网络方式。",
                   f"Details in {inst.log_path}. Run again to resume, or try another network option."))
    return 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.stdout.write("\x1b[?25h\n")
        print(L("已中断。再运行会接着下。", "Interrupted. Run again to resume."))
        sys.exit(130)
