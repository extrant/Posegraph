#!/usr/bin/env python3
"""把双显卡后端补丁应用到一份 Posegraph 后端包（drop-in，覆盖前先备份）。

用法：
    python apply.py <后端 src/posegraph 目录>

例如：
    python apply.py "D:/Downloads/Posegraph-Backend-win64/Posegraph-Backend/backend/src/posegraph"

- 把本目录 backend/src/posegraph/ 下 5 个已打补丁的文件复制过去
  （gpu.py, config.py, estimators/__init__.py, estimators/sam3d_body.py, app.py）。
- 复制前会先把目标目录里同名文件备份到 <目标>/../posegraph-gpu-select-backup-<时间戳>/，便于回退。
- 注意：直接覆盖会丢失作者在这些文件上的后续改动。若作者更新了这些文件，请改为手工把差异重新移植。
"""
import os
import sys
import shutil
import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
PATCH_ROOT = os.path.join(HERE, "backend", "src", "posegraph")
PATCH_FILES = [
    "gpu.py",
    "config.py",
    "estimators/__init__.py",
    "estimators/sam3d_body.py",
    "app.py",
]


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    target = sys.argv[1]
    if not os.path.isdir(target) or not os.path.exists(os.path.join(target, "app.py")):
        print(f"找不到后端目录（缺少 app.py）：{target}")
        return 1

    ts = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    backup = os.path.join(os.path.dirname(os.path.abspath(target)), f"posegraph-gpu-select-backup-{ts}")
    os.makedirs(backup, exist_ok=True)

    for rel in PATCH_FILES:
        src = os.path.join(PATCH_ROOT, rel)
        dst = os.path.join(target, rel)
        if not os.path.exists(src):
            print(f"补丁文件缺失：{src}")
            return 1
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if os.path.exists(dst):
            backup_dst = os.path.join(backup, rel)
            os.makedirs(os.path.dirname(backup_dst), exist_ok=True)
            shutil.copy2(dst, backup_dst)
        shutil.copy2(src, dst)
        print(f"已应用：{rel}")

    print(f"备份在：{backup}")
    print("完成。重启后端（或重启 Dalamud / 最终幻想14）生效。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
