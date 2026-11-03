"""Writes installer/lock.json (next to Install-Posegraph.bat): every file the one-click installer downloads, pinned to the tested package.

    <package python> packaging/make_installer_lock.py <package dir: ...\\Posegraph-Backend> [--tag v0.7.3] [--no-rocm-hash]

Run it with the package's own python (it reads the installed versions and uses packaging.tags for the wheel choice).

Format 2: one entry per GPU flavor (PyTorch build) and per Python environment.
- envs.cp311: Python 3.11.9 and the exact package versions installed in the package (cp311 / win_amd64 wheel, else sdist);
  this is the one-click package itself. envs.cp312: Python 3.12 with the same versions picked for cp312 (AMD's Windows ROCm
  PyTorch only exists for 3.12); the compiled packages that the backend components carry for cp311 (mediapipe, av,
  onnxruntime) are listed per component and installed from PyPI instead, the rest of each component zip is unpacked as is.
- torch: cu128 (the package's own build), cu126 (older NVIDIA cards: PyTorch's CUDA 12.8 builds dropped Pascal and older),
  cpu, rocm (AMD's repo.radeon.com wheels; no published hashes, so they are streamed once here to get SHA-256).
Everything shipped as a backend component (components.json) is left out of the package lists. GitHub source archives
are not byte-stable, so they are pinned by the SHA-256 of every file inside instead.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata as md
import importlib.util
import io
import json
import re
import sys
import urllib.parse
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from packaging.tags import compatible_tags, cpython_tags, sys_tags
from packaging.utils import canonicalize_name, parse_wheel_filename

ROOT = Path(__file__).resolve().parents[1]
REPO = "extrant/Posegraph"
PYTHONS = {"cp311": "3.11.9", "cp312": "3.12.10"}   # 3.12.10 是 3.12 最后一个带 Windows 嵌入版的版本
TORCH_INDEX = "https://download.pytorch.org/whl/{flavor}"
ROCM_BASE = "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/"
ROCM_FILES = [   # 按安装顺序（ROCm 运行库在前；rocm 元包是源码包），和 backend/scripts/setup_sam3d.ps1 一致
    ("rocm-sdk-core", "7.2.1", "rocm_sdk_core-7.2.1-py3-none-win_amd64.whl"),
    ("rocm-sdk-devel", "7.2.1", "rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl"),
    ("rocm-sdk-libraries-custom", "7.2.1", "rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl"),
    ("rocm", "7.2.1", "rocm-7.2.1.tar.gz"),
    ("torch", "2.9.1+rocm7.2.1", "torch-2.9.1+rocm7.2.1-cp312-cp312-win_amd64.whl"),
    ("torchvision", "0.24.1+rocm7.2.1", "torchvision-0.24.1+rocm7.2.1-cp312-cp312-win_amd64.whl"),
]
SAM_COMMIT = "b5c765a0d89d789985e186d396315e7590887b94"
SAM_WEIGHTS = "facebook/sam-3d-body-vith"
# 组件里已经带了（components.json），或者单独处理（pip、torch）
SKIP = {"mediapipe", "absl-py", "flatbuffers", "moge", "pipeline", "utils3d-moge", "onnxruntime", "av", "torch", "torchvision", "pip"}
# 有 3 个包只有源码包（antlr4-python3-runtime、fvcore、iopath），离线构建要 setuptools + wheel
BUILD_EXTRA = {"wheel": "0.45.1"}
# 官方权重（Hugging Face 2025-11-19 版）的 SHA-256，魔搭镜像同一份文件
WEIGHT_SHA = {"model.ckpt": "3b1cb897f4bbd977bf81cbb0b30780a9582681ac642ee112865790ceb4d66056",
              "assets/mhr_model.pt": "352e271a6c42729c68554ceaea0c955e866970160c31e35506d782dc0f7377bc"}
SITE = "python/Lib/site-packages"


def fetch(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "PosegraphInstaller/lock"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return r.read()


def head_size(url: str) -> int:
    req = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "PosegraphInstaller/lock"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return int(r.headers["Content-Length"])


def stream_sha256(url: str) -> tuple[str, int]:
    """边下边算 SHA-256，不落盘（ROCm 的大轮子没有公布哈希）。"""
    h, n = hashlib.sha256(), 0
    req = urllib.request.Request(url, headers={"User-Agent": "PosegraphInstaller/lock"})
    with urllib.request.urlopen(req, timeout=300) as r:
        while chunk := r.read(1 << 20):
            h.update(chunk)
            n += len(chunk)
    return h.hexdigest(), n


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def tag_ranks(env: str) -> dict:
    if env == "cp311":
        tags = list(sys_tags())
    else:
        version = (3, int(env[3:]))
        tags = list(cpython_tags(version, platforms=["win_amd64"])) + list(compatible_tags(version, env, ["win_amd64"]))
    return {t: i for i, t in enumerate(tags)}


def pick(name: str, version: str, rank: dict) -> dict:
    """PyPI 上这个版本最合适的一个文件（按目标 Python 的标签顺序挑轮子，没有就用源码包）。"""
    files = json.loads(fetch(f"https://pypi.org/pypi/{name}/{version}/json"))["urls"]
    best, best_rank = None, None
    for f in files:
        if f["packagetype"] != "bdist_wheel":
            continue
        _, _, _, tags = parse_wheel_filename(f["filename"])
        r = min((rank[t] for t in tags if t in rank), default=None)
        if r is not None and (best_rank is None or r < best_rank):
            best, best_rank = f, r
    if best is None:
        best = next((f for f in files if f["packagetype"] == "sdist"), None)
    if best is None:
        raise SystemExit(f"no usable file for {name}=={version}")
    path = re.sub(r"^https://files\.pythonhosted\.org/", "", best["url"])
    return {"name": name, "version": version, "file": best["filename"], "path": path,
            "sha256": best["digests"]["sha256"], "size": best["size"], "sdist": best["packagetype"] == "sdist"}


def pick_all(names: list[tuple[str, str]], env: str) -> list[dict]:
    rank = tag_ranks(env)
    with ThreadPoolExecutor(12) as ex:
        return list(ex.map(lambda nv: pick(nv[0], nv[1], rank), names))


def torch_file(flavor: str, name: str, version: str) -> dict:
    """PyTorch 索引页里的轮子：文件名、SHA-256（页面链接里带）、大小（取 HEAD）。version 不带 +cu128 这种后缀。"""
    index = TORCH_INDEX.format(flavor=flavor)
    page = fetch(f"{index}/{name}/").decode()
    filename = f"{name}-{version}+{flavor}-cp311-cp311-win_amd64.whl"
    m = re.search(r'href="([^"]*' + re.escape(filename.replace("+", "%2B")) + r')#sha256=([0-9a-f]{64})"', page)
    if not m:
        raise SystemExit(f"{filename} not in {index}")
    size = head_size(f"{index}/{filename.replace('+', '%2B')}")
    return {"name": name, "version": f"{version}+{flavor}", "file": filename, "sha256": m.group(2), "size": size}


def archive_files(url: str, skip_dirs: tuple[str, ...] = ()) -> dict:
    """GitHub 源码压缩包：根目录名和里面每个文件的 SHA-256（行尾按压缩包原样）。"""
    z = zipfile.ZipFile(io.BytesIO(fetch(url)))
    root = z.namelist()[0]
    files = {}
    for n in z.namelist():
        rel = n[len(root):]
        if n.endswith("/") or not rel or rel.split("/")[0] in skip_dirs:
            continue
        files[rel] = sha256(z.read(n))
    return {"root": root.rstrip("/"), "files": files}


def python_embed(version: str) -> dict:
    name = f"python-{version}-embed-amd64.zip"
    data = fetch(f"https://www.python.org/ftp/python/{version}/{name}")
    return {"version": version, "file": name, "sha256": sha256(data), "size": len(data)}


def component_split(package: Path) -> dict:
    """
    每个组件里哪些 Python 包是编译过的（只能给 cp311 用）：{组件 id: {"packages": [(名字, 版本)], "skip": [组件 zip 里要跳过的路径前缀]}}。
    看一键包里这个包 dist-info 的 WHEEL（Root-Is-Purelib: false 就是编译过的），RECORD 里的顶层目录就是要跳过的部分。
    """
    spec = importlib.util.spec_from_file_location("components", ROOT / "packaging" / "build_backend_components.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    result = {}
    for comp in mod.COMPONENTS:
        packages, skip = [], []
        for p in comp["paths"]:
            if not p.startswith(SITE + "/") or not p.endswith(".dist-info"):
                continue
            info = package / p
            wheel = (info / "WHEEL").read_text(encoding="utf-8")
            if re.search(r"^Root-Is-Purelib:\s*true", wheel, re.M | re.I):
                continue
            meta = md.PathDistribution(info).metadata
            packages.append((canonicalize_name(meta["Name"]), meta["Version"]))
            tops = {line.split(",")[0].split("/")[0] for line in (info / "RECORD").read_text(encoding="utf-8").splitlines() if line}
            skip += sorted(f"{SITE}/{t}" for t in tops if t and not t.startswith(".."))
        result[comp["id"]] = {"packages": packages, "skip": sorted(set(skip))}
    return result


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("package", type=Path)
    ap.add_argument("--tag", default=None, help="Posegraph 版本标签，缺省按 backend/src/posegraph/version.json")
    ap.add_argument("--no-rocm-hash", action="store_true", help="不下 ROCm 轮子算哈希（沿用现有 lock.json 里的）")
    args = ap.parse_args()
    version = json.loads((ROOT / "backend/src/posegraph/version.json").read_text(encoding="utf-8"))
    tag = args.tag or f"v{version['version']}"
    old = json.loads((ROOT / "installer" / "lock.json").read_text(encoding="utf-8")) if (ROOT / "installer" / "lock.json").exists() else {}

    installed = sorted((canonicalize_name(d.metadata["Name"]), d.version) for d in md.distributions())
    names = [(n, v) for n, v in installed if n not in SKIP]
    names += [(n, v) for n, v in BUILD_EXTRA.items() if all(x[0] != n for x in names)]
    split = component_split(args.package)

    envs = {}
    for env, pyver in PYTHONS.items():
        packages = pick_all(names, env)
        print(f"{env}: {len(packages)} packages")
        entry = {"python": python_embed(pyver), "pip": pick("pip", md.version("pip"), tag_ranks(env)), "packages": packages}
        if env != "cp311":
            entry["components"] = {cid: {"packages": pick_all(s["packages"], env), "skip": s["skip"]} for cid, s in split.items() if s["packages"]}
        envs[env] = entry

    torch_ver = md.version("torch").split("+")[0]
    vision_ver = md.version("torchvision").split("+")[0]
    torch = {}
    for flavor, label in (("cu128", "CUDA 12.8"), ("cu126", "CUDA 12.6"), ("cpu", "CPU")):
        torch[flavor] = {"env": "cp311", "label": label, "base": TORCH_INDEX.format(flavor=flavor) + "/", "mirrors": True,
                         "files": [torch_file(flavor, "torch", torch_ver), torch_file(flavor, "torchvision", vision_ver)]}
        print(f"torch {flavor}: {torch[flavor]['files'][0]['file']}")
    old_rocm = {f["file"]: f for f in old.get("torch", {}).get("rocm", {}).get("files", [])} if isinstance(old.get("torch"), dict) else {}
    rocm_files = []
    for name, ver, file in ROCM_FILES:
        if args.no_rocm_hash and file in old_rocm:
            rocm_files.append(old_rocm[file])
            continue
        digest, size = stream_sha256(ROCM_BASE + urllib.parse.quote(file))
        rocm_files.append({"name": name, "version": ver, "file": file, "sha256": digest, "size": size, "sdist": file.endswith(".tar.gz")})
        print(f"rocm {file}: {size / 2**20:.0f} MB")
    torch["rocm"] = {"env": "cp312", "label": "ROCm 7.2.1", "base": ROCM_BASE, "mirrors": False, "files": rocm_files}

    weights_dir = args.package / "backend/checkpoints/sam-3d-body-vith"
    weights = []
    for rel in ("model_config.yaml", "LICENSE", "assets/mhr_model.pt", "model.ckpt"):
        p = weights_dir / rel
        digest = WEIGHT_SHA.get(rel) or sha256(p.read_bytes())
        weights.append({"path": rel, "sha256": digest, "size": p.stat().st_size})

    sam = archive_files(f"https://github.com/facebookresearch/sam-3d-body/archive/{SAM_COMMIT}.zip", skip_dirs=("notebook",))
    posegraph = archive_files(f"https://github.com/{REPO}/archive/refs/tags/{tag}.zip")
    components = json.loads(posegraph_component_manifest(tag))

    lock = {
        "format": 2,
        "posegraph": {"version": version["version"], "runtime": version["runtime"], "tag": tag,
                      "url": f"https://github.com/{REPO}/archive/refs/tags/{tag}.zip", **posegraph},
        "envs": envs,
        "torch": torch,
        "sam3d": {"commit": SAM_COMMIT, "url": f"https://github.com/facebookresearch/sam-3d-body/archive/{SAM_COMMIT}.zip", **sam},
        "weights": {"repo": SAM_WEIGHTS, "dir": "sam-3d-body-vith", "files": weights},
        "components": components["components"],
    }
    out = ROOT / "installer" / "lock.json"
    out.write_text(json.dumps(lock, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    base = sum(w["size"] for w in weights) + sum(c["size"] for c in components["components"])
    for flavor, t in torch.items():
        e = envs[t["env"]]
        total = base + sum(p["size"] for p in e["packages"]) + e["pip"]["size"] + e["python"]["size"] + sum(f["size"] for f in t["files"])
        total += sum(p["size"] for c in e.get("components", {}).values() for p in c["packages"])
        print(f"{flavor}: about {total / 2**30:.2f} GB to download (+ source archives)")
    print(out)


def posegraph_component_manifest(tag: str) -> bytes:
    z = zipfile.ZipFile(io.BytesIO(fetch(f"https://github.com/{REPO}/archive/refs/tags/{tag}.zip")))
    root = z.namelist()[0]
    return z.read(root + "backend/src/posegraph/components.json")


if __name__ == "__main__":
    sys.exit(main())
