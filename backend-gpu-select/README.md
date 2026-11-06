# 双显卡后端补丁（drop-in）

配套 C# 插件的「指定显卡（双显卡）」功能。C# 侧（`feature/gpu-select` 分支）只负责**选卡并把卡告诉后端**，  
真正的按卡推理逻辑在后端这里。

> 为什么放在 `tools/` 而不是 `backend/`？  
> 仓库 `backend/` 是 `main` 分支的旧基线；你本机实际跑的后端包是作者**较新的发布版**（即「版本错位」，属正常）。  
> 如果直接把这里的补丁文件覆盖进仓库 `backend/`，会和旧基线其它文件混成跑不起来的版本。所以这里只做**可重复应用的副本**，  
> 每次后端包重新下载后，用 `apply.py` 把 5 个文件重新打进去即可。

## 协议（和 C# 的约定，已编译进已安装的 dll，不能改）

- 启动后端时，C# 在进程环境里写入：
  - `CUDA_VISIBLE_DEVICES = <卡>`（把选中的物理 N 卡映射成唯一的 `cuda:0`，其余隐藏）
  - `POSEGRAPH_CUDA_DEVICE = <卡>`（同一张卡的 index 或 UUID；空 = 不指定，用默认 `cuda:0`）
  - `<卡>` 形如 `"0"` / `"1"` / GPU UUID；都由插件设置页的「指定显卡」下拉框决定。
- 运行时 `POST /model/config` 的 JSON 体里带 `cuda_device`（值同上，可能为空串 `""`）。
- `BackendGpu` 为空 = 不指定（用默认 `cuda:0`）。

## 这 5 个文件各自改了什么

| 文件                         | 改动                                                                                                                                 |
| -------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `gpu.py`                   | `list_gpus()` 枚举全部物理 N 卡；`query_gpu(gpu_id=...)` 可按 index / UUID 取指定卡的显存（空=第一张，兼容旧行为）                                              |
| `config.py`                | 新增 `cuda_device` 配置项；在**任何 torch 导入之前**把 `POSEGRAPH_CUDA_DEVICE` 写进 `CUDA_VISIBLE_DEVICES`（缺失时兜底）                                  |
| `estimators/__init__.py`   | 把 `cuda_device` 透传给 `Sam3dBodyEstimator`                                                                                           |
| `estimators/sam3d_body.py` | `__init__` 收 `cuda_device`；`_pick_device` 在指定了卡时锁定该卡（找不到才退回 CPU），未指定时完全沿用原逻辑                                                       |
| `app.py`                   | `/health` 的 `gpu` 改报**被选中的那张卡**；新增 `GET /gpus` 诊断接口；`ModelConfig` + `/model/config` 支持 `cuda_device`；`_model_info` 返回 `cudaDevice` |

## 应用 / 重新应用

```powershell
# 在本机终端（不是沙箱）执行，把 5 个文件覆盖进你的后端包 src/posegraph 目录
cd D:\WorkBuddyWorks\2026-10-26-20-54-13\Posegraph-fork\tools\backend-gpu-select
python apply.py "D:\Downloads\Posegraph-Backend-win64\Posegraph-Backend\backend\src\posegraph"
```

- 脚本会**先备份**目标目录里被覆盖的同名文件到 `..\posegraph-gpu-select-backup-<时间戳>\`，方便回退。
- 覆盖会丢掉作者在这些文件上的后续改动；若作者更新了这些文件，请改为手工把差异重新移植（见上表逐项对照）。
- 改完**重启后端**（或重启 Dalamud / 最终幻想14）才生效。

