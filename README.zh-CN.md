# Posegraph

[English](README.md) | **简体中文**

把一张照片或插画里的人物姿势，摆到你的 FF14 角色身上。

Posegraph 是一个 Dalamud 插件，配合本地运行的 AI 后端。在游戏里选一张图，后端用 Meta 的
[SAM 3D Body](https://github.com/facebookresearch/sam-3d-body) 识别 3D 人体姿态，重定向到你所在种族的 FF14 骨架上，
插件再通过 [Brio](https://github.com/Etheirys/Brio) 应用到 GPose 角色。

```
Posegraph 插件（游戏内 ImGui）  ── HTTP（127.0.0.1）──►  Posegraph 后端（本地 Python）
  状态栏、选图、叠加层、                                  SAM 3D Body 识别（每张图一次）
  3D 预览、调整滑杆、                                     MHR → FF14 重定向（每次调整约 50ms）
  应用 / 镜头 / 导出             ◄── Brio 姿势 JSON ──   种族骨架、合理性检查
        │
        └── Brio IPC：冻结角色、导入姿势
```

## 功能

- **照片和插画都支持**，全身或半身，包括手指。「插画」预设会收敛插画里夸张的头颈比例。
- **全部可玩种族的男性**：按角色外貌自动识别种族（也可手动指定）。中原系与精灵男已在游戏内实测，拉拉菲尔与鲁加系男换种族后体型比例错误的问题已在 0.3.2 修复。
  女性骨架**尚未适配**（骨架数据已包含，但重定向规则是按男性骨架调校的）。
- **调参不用重新识别**：分部位幅度（脊柱、颈、头、锁骨、手腕）、手部末端补偿、重力校正、自动贴地、辅助骨强度，
  以及 3D 火柴人里的逐关节手动偏移。拖动滑杆只刷新预览，松手才应用（Brio 撤销历史保持干净）。
- **合理性提示**：阈值取自游戏动画采样，带一键修正建议。
- **手部末端补偿**：手贴着头、胸、髋时，即使种族比例和照片里的人不同，也能保持接触；肘部保持纯铰链。
- **复刻镜头**：在 GPose 里还原照片的机位、距离和视角。
- **导出** Brio `.pose` 文件。
- **对低显存友好**：自动 / GPU / CPU 三档，默认识别后立即释放模型；CPU 模式完全不占显存。
- **中英文界面**，默认跟随游戏客户端语言。

## 运行要求

- 装好 Dalamud（API 15）并启用 **Brio** 的 FF14
- Windows 10/11 64 位
- 用显卡识别：NVIDIA 显卡，可用显存约 **3GB**（识别时约占 2GB）。没有的话用 CPU：每张约 10~20 秒，内存约 4GB。
- 后端一键包约 7GB 磁盘空间

## 安装

### 1. 后端一键包

1. 下载 **[Posegraph-Backend-win64.zip](https://drive.google.com/file/d/1PnbqdyH0hOTMSPhBaSXQ-Dpjp3fil6k2/view?usp=sharing)**（谷歌硬盘，约 4GB），解压到空间充足的位置，路径建议只用英文。
2. 解压后有 `python`、`backend`、`LICENSES`、`Start-Backend.bat`、`README.txt`，不需要安装任何东西。

### 2. 插件

Posegraph 不在 Dalamud 官方仓库里，从自定义仓库安装：

1. 游戏里 `/xlsettings` → 实验性 → 自定义插件仓库，添加
   `https://raw.githubusercontent.com/extrant/DalamudPlugins/main/pluginmaster.json`，勾选并保存。
2. 在插件管理器里搜索 **Posegraph** 并安装。

也可以从[发布页](https://github.com/extrant/Posegraph/releases)下载 `Posegraph.zip` 解压，把 `Posegraph.dll` 添加到 Dev Plugin Locations。

### 3. 首次使用

1. `/posegraph` 打开主界面。
2. 设置 → **选择一键包目录…** → 选解压出来的文件夹。
3. 点状态栏的**插头**按钮启动后端。可以勾选「插件加载时自动启动后端」。

### 更新

- **插件**：通过插件仓库自动更新。
- **后端（一键包用户）**：插件更新后，设置页会显示「后端版本 …，插件 …」。后端比插件旧时，点旁边的下载按钮。
  插件会从本仓库对应标签下载同版本的源码（`archive/refs/tags/vX.Y.Z.zip`，不到 1MB），只替换 `backend\src`，Python、依赖和模型都不动。
  旧版保留为 `backend\src.bak`，新版启动失败会自动回滚。连不上 GitHub 时可以填加速前缀，例如 `https://gh-proxy.org/`。
- 只有依赖变化时才需要重新下载完整一键包，插件会提示（一键包的 `runtime.json` 低于新版后端的要求）。
- 源码安装：用 `git pull` 或切换到同版本标签更新。

## 使用

1. 进入 GPose，选中要摆姿势的角色（或在角色列表里选；列表只列玩家、Brio 新建的角色和雇员等人形 NPC）。
2. 点文件夹按钮，或把图片拖到「图像」页（jpg、png、webp、bmp、gif 首帧、tiff、avif）。
   也可以先复制图片，再点剪切板按钮识别。图像在内存中编码，不会写入临时图片文件。
   重试按钮会重新识别原先导入的剪切板图像，不受之后剪切板内容变化影响；无需更新后端。
3. 首次识别会加载模型（约 10~20 秒），之后每张几秒。
4. 看「图像」页叠加层（绿 = 识别骨架，橙 = FF14 骨架）和「3D」页。
5. 在「调整」页微调，看「提示」页，然后点 **应用**（▶）。相机按钮复刻照片镜头，↶ 还原第一次应用前的姿势，磁盘按钮导出 `.pose`。

状态栏图标从左到右：Brio、GPose、IVCS（读到游戏姿势后显示）、后端、显存、内存，鼠标悬停看详情。

### 设置

| 设置 | 默认 | 说明 |
|---|---|---|
| 语言 | 自动 | 游戏客户端是中文就用中文，否则看 Dalamud 界面语言，都不是中文则用英文 |
| 识别设备 | 自动 | 可用显存约 3GB 时用 GPU，否则用 CPU。「GPU」强制用显卡，「CPU」完全不占显存 |
| 模型释放 | 识别后立即释放 | 显存/内存归还给游戏，下次识别重新加载（约 5~10 秒） |
| GPU 模式下 MHR 放 CPU | 开 | 显存约省 750MB，每张约慢 0.5 秒 |
| 松开滑杆时自动应用 | 开 | 关掉后需要手动点应用 |

### 种族

| 骨架组 | 种族 | 状态 |
|---|---|---|
| 中原系男 | 人族中原、高地、猫魅、敖龙、维埃拉 | 已在游戏内实测 |
| 精灵男 | 精灵 | 已在游戏内实测 |
| 鲁加系男 | 鲁加、硌狮 | 0.3.2 已修复，待游戏内复测 |
| 拉拉菲尔 | 拉拉菲尔男 / 女 | 0.3.2 已修复，待游戏内复测 |
| 女性骨架 | 全部 | **尚未适配** |

识别出的种族不对时（换种族 MOD、Customize+ 改体型），在角色列表旁的种族下拉框里手动指定。
手动指定按角色分别记住（本次 GPose 内）；在角色之间切换不会清除它们在 Brio 里的姿势。

骨架数据来自 IVCS。角色没有加载 IVCS 骨骼时也能用：手指第三节的弯曲会合并到第二节，状态栏 IVCS 图标显示黄色。

## 已知限制

- 单张图片的前后深度有歧义（比如手在头前还是头后）。
- 还没有碰撞避让：拉拉菲尔举起的手臂可能穿过头，鲁加 / 硌狮高抬腿时大腿可能插进肚子。
- 肘部极度弯曲时褶皱比游戏动画明显（约 145°，游戏动画约 130°）。
- 跳跃、腾空的图会被按到地面上，需要关掉「自动贴地」。
- 多人和视频在界面上预留了入口，尚未实现；目前整张图按一个人处理。
- CPU 模式速度很依赖 CPU（20 线程笔记本 CPU 实测约 10 秒/张）。

## 从源码构建

### 插件

```powershell
cd Posegraph
dotnet build -c Release      # 产物：Posegraph\bin\Release\Posegraph\latest.zip
```

项目使用 `Dalamud.NET.SDK` 15 与 `Brio.API`。国服 XIVLauncherCN 的 Dalamud 路径会自动识别。

### 后端（开发安装）

```powershell
cd backend
powershell -ExecutionPolicy Bypass -File scripts\setup_sam3d.ps1     # 虚拟环境、CUDA 版 PyTorch、上游源码、权重（SHA256 校验）
.venv\Scripts\python -m posegraph.app                               # http://127.0.0.1:42430
```

权重默认从魔搭 ModelScope 下载（免登录）；`-Source hf` 走 Hugging Face 官方仓库（需申请访问）。
环境变量（前缀 `POSEGRAPH_`）：`DEVICE`（auto|cuda|cpu）、`PRECISION`（auto|bf16|fp16|fp32）、`MHR_ON_CPU`、
`MIN_FREE_VRAM_MB`、`RELEASE_AFTER`（秒，0 立即、-1 常驻）、`PRELOAD`、`SAVE_SESSIONS`（每次识别另存 npz 到 `outputs/ui`）、`PORT`、`BRIDGE_URL`、`SAM3D_REPO`、`SAM3D_CKPT`。

### 后端一键包

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build_backend_package.ps1 `
    -Python C:\Path\To\Python311 -Venv backend\.venv `
    -Sam3d backend\third_party\sam-3d-body -Weights backend\checkpoints\sam-3d-body-vith `
    -Out D:\dist -Zip
```

脚本把一份普通的 CPython 3.11 安装复制成便携解释器，再加上虚拟环境的 site-packages、后端源码、SAM 3D Body 源码与权重、许可证文件。

### 开发工具（`backend/scripts`，输出为中文）

| 脚本 | 用途 |
|---|---|
| `photo_to_game.py`、`image_to_pose.py`、`push_pose.py`、`preview_overlay.py` | 不用插件界面的命令行流程 |
| `verify_devices.py`、`bench_sam3d.py` | 比对 CPU/GPU 输出、测显存与速度（见 `docs/benchmark.md`） |
| `build_retarget_data.py` | 从 IVCS `.sklb` 重新生成骨架数据 |
| `sample_frames.py` | 采样游戏内动画帧（用于拟合辅助骨规则） |
| `export_race_compare.py`、`blender_race_compare.py`、`build_body_extents.py` | Blender 多种族对比场景与身体尺寸表 |

## HTTP 接口

插件自带一个本地服务（127.0.0.1:42429），供后端复刻镜头和命令行工具使用：`GET /status`、`GET /actors`、
`GET|POST /pose?actor=N`、`GET|POST /camera`。后端（127.0.0.1:42430）提供 `GET /health`、`POST /sessions`、
`GET /sessions/{id}/image`、`POST /sessions/{id}/retarget`、`POST /sessions/{id}/camera`、`POST /model/load|unload|config`、
`GET /races`、`GET /presets`、`POST /shutdown`。两者都只监听本机。

## 许可证

Posegraph 自身的代码（插件、后端、脚本）以 [MIT 许可证](LICENSE) 授权（0.3.8 及之前的版本为 PolyForm Noncommercial License 1.0.0）。
第三方组件仍按各自的许可证，其中后端一键包附带的 SAM 3D Body 代码与权重为 Meta 的 SAM License。

## 致谢

见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。SAM 3D Body（代码与权重）© Meta，以 SAM License 发布；
FF14 骨架数据提取自 Illusio Vitae Custom Skeletons。

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Posegraph 为玩家自制工具，与 Square Enix 无关，也未获其认可。
