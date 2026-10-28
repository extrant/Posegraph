"""Builds the add-on component zips that old one-click packages download through the plugin's backend updater,
and writes backend/src/posegraph/components.json (the manifest the updater reads from the new source).

    python packaging/build_backend_components.py <package dir: ...\\Posegraph-Backend> <output dir> [--tag backend-components-1]

Each zip holds files at their paths relative to the package root (python\\Lib\\site-packages\\..., backend\\checkpoints\\...),
so the updater just extracts it there. Upload the zips to the GitHub release named by --tag (a prerelease, so it never
becomes "Latest"); bump the tag and the zip names whenever a component's contents change.
Components only add Python packages and model files the Python / PyTorch of runtime 1 can use; anything that needs a new
interpreter or PyTorch still needs a new full package (bump "runtime").
"""

from __future__ import annotations

import argparse
import hashlib
import json
import zipfile
from pathlib import Path

REPO = "extrant/Posegraph"
SITE = "python/Lib/site-packages"

# id, display names (zh, en), what to pack (relative to the package root), and the files whose absence means "missing"
COMPONENTS = [
    {
        "id": "face", "name": "面捕（表情、头部朝向）", "nameEn": "Face capture (expressions, head direction)",
        "paths": [f"{SITE}/mediapipe", f"{SITE}/mediapipe-1.1.1.dist-info", f"{SITE}/absl", f"{SITE}/absl_py-2.5.1.dist-info",
                  f"{SITE}/flatbuffers", f"{SITE}/flatbuffers-25.12.19.dist-info", "backend/checkpoints/face"],
        "check": [f"{SITE}/mediapipe/__init__.py", "backend/checkpoints/face/face_landmarker.task"],
    },
    {
        "id": "depth", "name": "深度图（镜头焦距、地面）", "nameEn": "Depth (camera focal length, floor)",
        "paths": [f"{SITE}/moge", f"{SITE}/moge-3.0.0.dist-info", f"{SITE}/pipeline", f"{SITE}/pipeline-1.0.0.dist-info",
                  f"{SITE}/utils3d_moge", f"{SITE}/utils3d_moge-1.7.dist-info", "backend/checkpoints/moge"],
        "check": [f"{SITE}/moge/__init__.py", "backend/checkpoints/moge/model.pt"],
    },
    # 找人（合并模式）暂不发：合并模式这一版关着，YOLO11 权重是 AGPL-3.0，发之前要先定授权怎么处理
]

# already compressed: storing is as small and much faster
STORED = {".pt", ".onnx", ".task", ".pyd", ".dll", ".zip"}


def build(package: Path, out: Path, tag: str) -> list[dict]:
    out.mkdir(parents=True, exist_ok=True)
    manifest = []
    for c in COMPONENTS:
        name = f"posegraph-{c['id']}-{tag.rsplit('-', 1)[-1]}.zip"
        target = out / name
        with zipfile.ZipFile(target, "w") as z:
            for rel in c["paths"]:
                src = package / rel
                if not src.exists():
                    raise SystemExit(f"missing in package: {src}")
                files = [src] if src.is_file() else sorted(p for p in src.rglob("*") if p.is_file())
                for f in files:
                    if "__pycache__" in f.parts or f.suffix == ".pyc":
                        continue
                    arc = f.relative_to(package).as_posix()
                    z.write(f, arc, compress_type=zipfile.ZIP_STORED if f.suffix.lower() in STORED else zipfile.ZIP_DEFLATED)
        data = target.read_bytes()
        manifest.append({"id": c["id"], "name": c["name"], "nameEn": c["nameEn"], "check": c["check"],
                         "url": f"https://github.com/{REPO}/releases/download/{tag}/{name}",
                         "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        print(f"{name}: {len(data) / 2**20:.1f} MB")
    return manifest


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("package", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--tag", default="backend-components-1")
    args = ap.parse_args()
    manifest = build(args.package, args.out, args.tag)
    path = Path(__file__).resolve().parents[1] / "backend" / "src" / "posegraph" / "components.json"
    path.write_text(json.dumps({"tag": args.tag, "components": manifest}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("manifest:", path)


if __name__ == "__main__":
    main()
