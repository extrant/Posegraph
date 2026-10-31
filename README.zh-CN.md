# Posegraph

[English](README.md) | **简体中文**

把一张照片或插画里的人物姿势，摆到你的 FF14 角色身上。

**第一次用？先看[图文教程](docs/tutorial/README.zh-CN.md)。**

<p>
  <img src="docs/tutorial/images/ingame-wall.jpg" alt="倚墙" width="49%">
  <img src="docs/tutorial/images/ingame-kalaripayattu.jpg" alt="武术腾空" width="49%">
</p>

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
- **全部可玩种族（男女）**：按角色外貌自动识别种族（也可手动指定）。中原系与精灵男已在游戏内实测，拉拉菲尔与鲁加系男换种族后体型比例错误的问题已在 0.3.2 修复。
  女性骨架已用游戏原版身体 + IVCS 骨架离线核对，表现与男性一致，待游戏内复测。
- **调参不用重新识别**：分部位幅度（脊柱、颈、头、锁骨、手腕）、手部末端补偿、重力校正、自动贴地、辅助骨强度，
  以及 3D 火柴人里的逐关节手动偏移。拖动滑杆只刷新预览，松手才应用（Brio 撤销历史保持干净）。
- **图库**：导入、文件夹 / 标签 / 收藏分类，批量识别并缓存结果，存到 Brio 姿势库。
- **多个角色一起摆**（角色列表）：GPose 里每个角色绑一张图，一键全部应用。
- **辅助视角**：挂上同一动作其他角度的图，修正单张照片猜不准的前后深度。
- **合理性提示**：阈值取自游戏动画采样，带一键修正建议。
- **手部末端补偿**：手贴着头、胸、髋时，即使种族比例和照片里的人不同，也能保持接触；肘部保持纯铰链。
- **复刻镜头**：在 GPose 里还原照片的机位、距离和视角；深度图（MoGe-2）帮忙估镜头焦距和地面。
- **面部表情（测试）**：照片表情换算成游戏脸部骨骼，按照片实际幅度还原、不超过游戏极限；眼睑、眉毛、嘴部、下巴、视线可分项调；
  面捕还会修正头部朝向。
- **Brio 改动收进图库**：应用后在 Brio 里手动改的骨骼，一键换成这张图的修正存进图库。
- **灯光**：11 套一键打光方案和「照片同款」（按照片里的明暗估出主光、照片配色），灯跟着人物和镜头摆，全身也照得到，可以存进图库，独立 3D 布光窗口逐盏调整、加动态灯效。
- **构图与截图**：构图框、参考线、等效全画幅焦距、叠原图对照，一键截游戏渲染原图。
- **视频动作（测试）**：逐帧识别视频里的动作、表情和原视频镜头的运动，在 GPose 里播放（跟拍或在场景里走动、多种机位）；多人同场；时间轴修正（Brio 里改骨骼收成关键帧、复制粘贴到别的帧）；同步放音乐、录成视频。
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
  插件会从本仓库对应标签下载同版本的源码（`archive/refs/tags/vX.Y.Z.zip`），替换 `backend\src`；
  新功能要的 Python 包和模型做成**组件**（`backend/src/posegraph/components.json` 列出，文件在 Release
  [backend-components-1](https://github.com/extrant/Posegraph/releases/tag/backend-components-1)），缺哪个下载哪个、校验 SHA-256 后解压进一键包。
  老一键包点一次更新就补齐面捕（约 64MB）和深度图（约 400MB），不用重新下载整个一键包。
  旧版保留为 `backend\src.bak`，新版启动失败会自动回滚。连不上 GitHub 时可以填加速前缀，例如 `https://gh-proxy.org/`。
  插件里下载不了时可以手动下载、解压，见 [手动更新后端](docs/manual-update.zh-CN.md)（组件压缩包也可以朋友之间传，附校验方法）。
- 只有 Python / PyTorch 本身要换时才需要重新下载完整一键包，插件会提示（一键包的 `runtime.json` 低于新版后端的要求）。
- 源码安装：用 `git pull` 或切换到同版本标签更新。

## 使用

1. 进入 GPose，选中要摆姿势的角色（或在角色列表里选；列表只列玩家、Brio 新建的角色和雇员等人形 NPC）。
2. 打开图库（图片按钮）双击图片，或把图片拖到「图像」页（jpg、png、webp、bmp、gif 首帧、tiff、avif）。
   拖进来的图不会自动存入图库；识别后点工具栏的「文件夹 +」按钮存入，会带上 3D / 调整页的修正。
3. 首次识别会加载模型（约 10~20 秒），之后每张几秒。识别结果按文件内容缓存，识别过的图再打开不用等。
4. 看「图像」页叠加层（绿 = 识别骨架，橙 = FF14 骨架）和「3D」页。
5. 在「调整」页微调，看「提示」页，然后点 **应用**（▶）。相机按钮复刻照片镜头，↶ 还原第一次应用前的姿势，
   磁盘按钮导出 `.pose` 或存到 Brio 姿势库。

### 图库

- 图片放在 `文档\Posegraph\Images`（导入、导入文件夹最多 500 张、剪切板粘贴的图都复制到这里；图库里已有的图按内容查重，不重复复制）；
  已有的文件夹可以添加为来源，不复制。
- 用子文件夹、标签（搜索 `#标签`）、收藏分类；可以重命名、移动（也可以拖到文件夹上）、删除到回收站。
- 选中多张批量识别，识别过的显示绿色对勾；识别结果缓存在 `文档\Posegraph\Cache`。
- 「存到 Brio 姿势库」把带预览图和标签的 `.pose` 存到 `文档\Brio\Poses\Posegraph`，在 Brio 资料库里直接能看到。

图库批量识别默认逐张进行。「设置 → 识别后端 → 多图识别并行数（实验）」设为 2～8 时，批量队列几张图共享一份模型同时识别
（RTX 4060 笔记本实测整批约省 35% 的时间，多占约 300MB 显存）；单张识别、辅助视角始终逐张。
后端按剩余显存 / 内存自动减少路数，共享模型不可用时自动改回逐张识别并提示一次。详见[并行识别说明](docs/parallel-recognition.md)。

状态栏图标从左到右：Brio、GPose、IVCS（GPose 里选中角色后显示）、后端、显存、内存，鼠标悬停看详情。

### 设置

| 设置 | 默认 | 说明 |
|---|---|---|
| 语言 | 自动 | 游戏客户端是中文就用中文，否则看 Dalamud 界面语言，都不是中文则用英文 |
| 识别设备 | 自动 | 可用显存约 3GB 时用 GPU，否则用 CPU。「GPU」强制用显卡，「CPU」完全不占显存 |
| 多图识别并行数（实验） | 1 | 只影响批量识别；2～8 共享一份模型同时识别，下批生效，设回 1 即逐张 |
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
| 女性骨架 | 全部 | 已离线核对，待游戏内复测 |

识别出的种族不对时（换种族 MOD、Customize+ 改体型），在角色列表旁的种族下拉框里手动指定。
手动指定按角色分别记住（本次 GPose 内）；在角色之间切换不会清除它们在 Brio 里的姿势。

骨架数据来自 IVCS。角色没有加载 IVCS 骨骼时也能用：手指第三节的弯曲会合并到第二节，状态栏 IVCS 图标显示黄色。

## 已知限制

- 单张图片的前后深度有歧义（比如手在头前还是头后）。
- 还没有碰撞避让：拉拉菲尔举起的手臂可能穿过头，鲁加 / 硌狮高抬腿时大腿可能插进肚子。
- 肘部极度弯曲时褶皱比游戏动画明显（约 145°，游戏动画约 130°）。
- 跳跃、腾空的图会被按到地面上，需要关掉「自动贴地」。
- 一张图多人（合并模式）还在打磨，这一版关着，目前整张图按一个人处理（视频里可以多人）。
- 视频：手指用识别模型自带的结果，小手、模糊的手常不准，需要用时间轴修正手动调；有切镜的视频建议截取一段连续镜头；远景里人太小时动作和表情会变差。
- 面部表情：吐舌头做不到；单眨一只眼和笑眯眼有时分不清；插画的脸多半找不到。
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
`RECOGNITION_PARALLELISM`：批量识别并行数（1～8，默认 1，插件会在每批开始前设置）；`DISABLE_PARALLEL=1` 完全关掉并行，所有识别逐张进行。

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
