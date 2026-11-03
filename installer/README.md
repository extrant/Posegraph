# Posegraph 后端一键安装 / 更新脚本 / Backend installer

**简体中文** | [English](#english)

不用下载 4GB 的一键包：这个脚本从官方源（或国内镜像）现下现装，装出来的东西和一键包一样
（同样的依赖版本、同样的模型权重，每个文件都按 SHA-256 核对）。已经装过的后端也能用它**更新 / 修复**。

## 怎么用

1. 下载 [Posegraph-Installer.zip](https://github.com/extrant/Posegraph/releases/download/backend-components-1/Posegraph-Installer.zip)，解压到任意位置
   （或者从本仓库的 `installer` 文件夹拿这四个文件：`Install-Posegraph.bat`、`Install-Posegraph.ps1`、`installer.py`、`lock.json`）。
2. 双击 **Install-Posegraph.bat**。
3. 按提示选（上次的选择会记住，直接回车就是上次的）：
   1. **装到哪里 / 更新哪一个**：会列出插件设置里填过的后端和上次装的位置，选一个就是更新 / 修复它；
      也可以装到新位置（默认是空间最多的非系统盘下的 `Posegraph-Backend`）。路径只用英文，新装需要约 16GB 空间。
   2. **计算方式**：自动识别显卡并推荐，也可以自己选：
      | 选项 | 适合 |
      |---|---|
      | NVIDIA · CUDA 12.8 | GTX 16 / RTX 20 系及更新的显卡，驱动 570 以上 |
      | NVIDIA · CUDA 12.6 | GTX 900 / 10 系等老显卡（CUDA 12.8 版不再支持），或驱动在 528~570 之间 |
      | AMD · ROCm | RX 9000 系、RX 7900 / 7800 / 7700、Radeon PRO W7000 / W9700、Ryzen AI Max（8060S / 8050S），需要 AMD 新驱动（带 ROCm 的 Adrenalin） |
      | CPU | 没有独立显卡、显存不到 3GB、显卡太老或驱动太旧；每张约 10~20 秒，一样能用 |
   3. **网络方式**：
      1. **直接下载**：官方地址。海外网络，或者已经开了全局代理 / 系统代理时选这个。
      2. **中国网络**：Python 包走清华 / 阿里 / 华为等镜像，PyTorch 走阿里云镜像，GitHub 走公共加速站（多为 Cloudflare 中转）；
         开始前自动测速，选最快的。（AMD 的 ROCm 包只有 AMD 官方源。）
      3. **走我自己的代理**：填 `地址:端口`，比如 Clash 的 `127.0.0.1:7897`。
4. 等它下完、装完。新装总共下载约 3~6GB（CPU 版最小）；更新只下缺的和变了的。
5. 装好后如果游戏没开，会自动把路径和计算方式填进插件设置（原来的设置备份成 `Posegraph.json.before-installer.bak`）；
   游戏开着就把路径复制到剪切板：`/posegraph` → **设置** → **选择一键包目录…**，选这个文件夹。

中途断了、关掉了都没关系：**再运行一次，选同一个文件夹**，会接着下、跳过已经装好的。

### 更新 / 修复

再运行一次，选已有的后端。脚本先逐个核对目录里的文件，只补缺的、换版本不对或坏掉的；权重和组件没问题就不重新下。
脚本每次启动会从本仓库拿最新的 `lock.json`，所以旧的安装脚本也能装新版本。换计算方式（比如换了显卡）也是这样选，
只换 PyTorch。AMD（ROCm）用的是 Python 3.12，从别的方式换过来会换掉 Python（旧的先改名留着，装好才删）。

## 会不会动我电脑的设置？

不会。脚本只往你选的文件夹里写东西：

- 不写注册表、环境变量、PATH，不改 PowerShell 执行策略（`.bat` 只对这一次运行用 `-ExecutionPolicy Bypass`）；
- 不改 pip 配置、不用用户目录下的 pip 缓存，也不会读到你电脑上另外装的 Python；
- 「直接下载」「中国网络」跟系统一样（系统设了代理就走系统代理），「走我自己的代理」只走你填的代理，不改系统代理；
- 脚本旁边的 `installer-settings.json` 记你上次的选择；
- 装好后填插件设置（游戏关着时，改前先备份）、把路径复制到剪切板。不想自动填设置，用 `Install-Posegraph.ps1 -SkipPluginConfig`。

不想要了，直接删掉安装的文件夹，没有别的残留。

## 下载来源

| 内容 | 直接下载 | 中国网络 |
|---|---|---|
| 便携版 Python 3.11.9（AMD 用 3.12.10，约 11MB） | python.org | npmmirror / 华为云镜像 |
| Python 依赖（77 个包） | PyPI | 清华 / 阿里 / 华为 / 中科大镜像（测速选） |
| PyTorch（CUDA 12.8 / 12.6 约 2.7GB，CPU 约 0.3GB） | download.pytorch.org | 阿里云镜像 / 官方（测速选） |
| PyTorch ROCm 版和 ROCm 运行库（约 2.1GB） | repo.radeon.com（AMD 官方） | 同左 |
| SAM 3D Body 源码 | GitHub | GitHub 加速站（测速选） |
| SAM 3D Body 权重（约 2.4GB） | 魔搭 ModelScope（不用登录） | 魔搭 ModelScope |
| Posegraph 后端、面捕 / 深度图组件 | GitHub | GitHub 加速站 |

一个地址下不了会自动换下一个；文件内容和 `lock.json` 里记的 SHA-256 对不上就删掉重下，所以镜像和加速站改不了内容。
GitHub 源码包每次打包可能不同，按里面每个文件的 SHA-256 核对。

## 常见问题

- **某个文件一直下不了**：再运行一次（会接着下），或者换一种网络方式。详细记录在安装文件夹的 `install.log`。
- **窗口里的进度条是乱的**：老版本 Windows 的控制台不支持彩色刷新，会改成隔一会打印一行进度，不影响安装。
- **推荐的计算方式不对**：自己选别的就行。装完会用显卡跑一次测试，跑不通会提示改用哪种。
- **AMD 显卡推荐了 CPU**：显卡不在 AMD 官方支持列表里，或者驱动里没有 ROCm（`System32\amdhip64_7.dll`）。先更新到 AMD 最新驱动；
  不在列表里的显卡可以硬选 ROCm 试试，不保证能用。

## 维护（发版时）

`lock.json` 由 `packaging/make_installer_lock.py` 从实测过的一键包生成（用一键包自己的 Python）：

```
<一键包>\python\python.exe packaging/make_installer_lock.py <一键包目录> --tag v<版本>
```

ROCm 的几个大文件要下下来算 SHA-256（约 2GB），ROCm 版本没变时加 `--no-rocm-hash` 沿用旧清单里的值。
生成后提交到 main：已经发出去的安装脚本启动时会从 main 拿最新的清单。清单格式变了要改 `format`，旧脚本会提示下载新的安装脚本。

---

## English

Instead of downloading the 4 GB package, this script installs the same thing from official sources (or mainland China
mirrors), every file checked against SHA-256. It also **updates / repairs** an existing backend.

**How to use**

1. Download [Posegraph-Installer.zip](https://github.com/extrant/Posegraph/releases/download/backend-components-1/Posegraph-Installer.zip) and unpack it
   (or take the four files in this repository's `installer` folder).
2. Double-click **Install-Posegraph.bat**.
3. Choose (your last choices are remembered; press Enter to keep them):
   1. **Where / which to update**: backends found in the plugin settings and your last install are listed; pick one to
      update / repair it, or install to a new location (default `Posegraph-Backend` on the non-system drive with the most
      free space). English-only path, about 16 GB free for a new install.
   2. **Compute**: detected and recommended automatically, or pick one yourself:
      - **NVIDIA · CUDA 12.8**: GTX 16 / RTX 20 series and newer, driver 570+;
      - **NVIDIA · CUDA 12.6**: older cards such as GTX 900 / 10 series (dropped by the CUDA 12.8 build), or driver 528–570;
      - **AMD · ROCm**: RX 9000, RX 7900 / 7800 / 7700, Radeon PRO W7000 / W9700, Ryzen AI Max (8060S / 8050S), with a recent
        AMD driver that includes ROCm;
      - **CPU**: no dedicated GPU, under 3 GB VRAM, or a card / driver that's too old; about 10–20 s per image.
   3. **Network**: 1 = direct (official sources); 2 = China network (mainland mirrors and public GitHub accelerators, fastest
      picked automatically; ROCm packages only come from AMD); 3 = your own proxy, `host:port` such as `127.0.0.1:7897`.
4. Wait for it to finish (about 3–6 GB for a new install, CPU is smallest; updates only download what's missing or changed).
5. If the game isn't running, the path and compute mode are written into the plugin settings (the old file is backed up as
   `Posegraph.json.before-installer.bak`); otherwise the path is copied to the clipboard: `/posegraph` → **Settings** →
   **Choose backend package folder…**.

If it's interrupted, **run it again with the same folder**: it resumes and skips what's already installed.

**Update / repair**: run it again and pick the existing backend. Every file is checked; only missing, outdated or broken
pieces are replaced, and weights and components aren't downloaded again if they're fine. The script fetches the latest
`lock.json` from this repository on start, so an old copy of the script still installs the latest version. Switching compute
mode works the same way and only swaps PyTorch; AMD (ROCm) uses Python 3.12, so switching to or from it replaces Python (the
old one is renamed and only deleted after success).

**Does it change my settings?** It only writes into the folder you choose: no registry, environment variables, PATH or
execution policy changes, no pip configuration or user pip cache. `installer-settings.json` next to the script remembers your
choices. After installing it fills in the plugin settings (only while the game is closed, with a backup; skip with
`Install-Posegraph.ps1 -SkipPluginConfig`) and copies the path to the clipboard. To uninstall, delete the folder.

**Maintenance**: regenerate `lock.json` for each release with
`<package>\python\python.exe packaging/make_installer_lock.py <package dir> --tag v<version>` (add `--no-rocm-hash` to reuse
the ROCm hashes when the ROCm version is unchanged) and commit it to main.
