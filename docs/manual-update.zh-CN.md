# 手动更新后端（下载不了时）

[English](manual-update.md) | **简体中文**

插件里的「更新后端」会自动下载后端代码和新功能要的组件。如果插件里下载不了（连不上 GitHub、加速前缀也不行），
可以自己用浏览器下载，或者找已经下好的朋友要压缩包，然后按下面的步骤解压。

整个过程只是把几个压缩包**解压到一键包文件夹里**，不用重新下载 4GB 的一键包。

下文的「一键包文件夹」指你解压一键包得到的那个文件夹，里面能看到 `python`、`backend` 两个文件夹和 `Start-Backend.bat`，
比如 `D:\Posegraph-Backend`。

---

## 0. 开始前

1. 插件先更新到最新版（插件管理器里更新 Posegraph）。
2. 点主窗口右上角的 **电源** 按钮**停止后端**（状态栏的后端图标变灰就是停了），否则文件被占用，解压会失败。

## 1. 需要的文件

| 文件 | 大小 | 用途 | 下载 |
|---|---|---|---|
| `posegraph-face-1.zip` | 约 64MB | 面捕（面部表情、头部跟脸） | [GitHub](https://github.com/extrant/Posegraph/releases/download/backend-components-1/posegraph-face-1.zip) |
| `posegraph-depth-1.zip` | 约 400MB | 深度图（镜头焦距、地面） | [GitHub](https://github.com/extrant/Posegraph/releases/download/backend-components-1/posegraph-depth-1.zip) |
| `posegraph-audio-2.zip` | 约 70MB | 音频（视频播放、录制时放音乐，0.7.0 起） | [GitHub](https://github.com/extrant/Posegraph/releases/download/backend-components-2/posegraph-audio-2.zip) |
| `Posegraph-0.7.0.zip` | 约 5MB | 后端代码 | [GitHub](https://github.com/extrant/Posegraph/archive/refs/tags/v0.7.0.zip) |

- 面捕、深度图在 [backend-components-1](https://github.com/extrant/Posegraph/releases/tag/backend-components-1) 页面，音频在 [backend-components-2](https://github.com/extrant/Posegraph/releases/tag/backend-components-2) 页面；后端代码在
  [v0.7.0](https://github.com/extrant/Posegraph/releases/tag/v0.7.0) 页面最下面的 **Source code (zip)**。
- GitHub 打不开时，可以在链接前面加加速前缀再打开，例如 `https://gh-proxy.org/https://github.com/extrant/Posegraph/releases/download/backend-components-1/posegraph-face-1.zip`。
- 组件只装一次：以后的版本只要组件没变，就不用再下。
- 只能联网更新代码、组件下载不动时：只手动装组件（第 2 步），然后回插件里点「更新后端」，它会跳过已经装好的组件，只下载约 5MB 的代码。

## 2. 解压组件

每个组件的做法一样，以 `posegraph-face-1.zip` 为例。压缩包里是 `python` 和 `backend` 两个文件夹（音频组件只有 `python`），
要让它们**和一键包里的同名文件夹合并**。

**用 Windows 自带的解压（右键「全部解压缩」）：**

1. 右键压缩包 → **全部解压缩…**
2. 点 **浏览…** 选一键包文件夹（比如 `D:\Posegraph-Backend`）。
3. **重点**：确认地址栏里最后**没有**多出 `\posegraph-face-1`。Windows 会自动在后面加上压缩包的名字，要把它删掉，
   地址应该正好是 `D:\Posegraph-Backend`。
4. 点 **提取**。提示有同名文件时选 **替换目标中的文件**。

**用 7-Zip / Bandizip / WinRAR：**

1. 把压缩包放进一键包文件夹（和 `python`、`backend` 放在一起）。
2. 右键 → **解压到当前文件夹**（7-Zip 是「提取到当前位置」，Bandizip 是「解压到当前文件夹」，WinRAR 是「解压到当前文件夹」）。
   不要选「解压到 posegraph-face-1\」。
3. 提示覆盖时选 **全部覆盖**。

解压对了的话，这些文件存在（把 `D:\Posegraph-Backend` 换成你的一键包文件夹）：

- 面捕：`D:\Posegraph-Backend\python\Lib\site-packages\mediapipe\__init__.py` 和 `D:\Posegraph-Backend\backend\checkpoints\face\face_landmarker.task`
- 深度图：`D:\Posegraph-Backend\python\Lib\site-packages\moge\__init__.py` 和 `D:\Posegraph-Backend\backend\checkpoints\moge\model.pt`

如果多出了一个 `D:\Posegraph-Backend\posegraph-face-1\python\…` 这样的文件夹，说明解压到了子文件夹里：
把 `posegraph-face-1` 里面的 `python`、`backend` 两个文件夹剪切到一键包文件夹，合并、覆盖，再删掉空的 `posegraph-face-1`。

## 3. 换后端代码

1. 解压 `Posegraph-0.7.0.zip`（解压到哪里都行，比如桌面）。
2. 打开一键包文件夹里的 `backend`，把里面原来的 `src` 文件夹**改名**为 `src.old`（留作备份，确认新版能用后可以删掉）。
3. 打开刚解压出来的 `Posegraph-0.7.0\backend`，把里面的 `src` 文件夹**复制**到一键包的 `backend` 文件夹里。

完成后应该是：`D:\Posegraph-Backend\backend\src\posegraph\version.json` 里写着 `"version": "0.7.0"`。

## 4. 检查

回到游戏，插件 **设置** 页：

- 显示「后端版本 0.7.0，插件 v0.7.0」和 **已是最新**，就全部装好了。
- 还显示「还缺组件：…」：对应的组件没解压到位，回第 2 步看那几个文件在不在。
- 还显示可更新：第 3 步的 `src` 没换对，看 `version.json` 里的版本号。

然后点主窗口右上角的 **电源** 按钮启动后端。

如果新版后端启动不了：删掉新的 `src`，把 `src.old` 改回 `src`，就回到了更新前的样子，再到 Discord 或 GitHub Issues 反馈。

## 5. 朋友之间传文件：先核对校验值

压缩包可以朋友之间互相传（QQ 群文件、网盘都行）。为了确认文件是完整的、没被改过，解压前核对一下 SHA-256：

1. 在压缩包所在的文件夹，地址栏输入 `cmd` 回车，打开命令行。
2. 输入（文件名按实际的改）：

   ```
   certutil -hashfile posegraph-face-1.zip SHA256
   ```

3. 显示的一串字符要和下表**完全一致**（不分大小写）：

| 文件 | SHA-256 |
|---|---|
| `posegraph-face-1.zip` | `ac62fb58b2a7600f73c48f08811e6bd15a47f1104fb8dd1f00b9f6fd24604721` |
| `posegraph-depth-1.zip` | `23e387d0008c7065fd5573d7b7fec8b28ca7997a899fff4efb4e1eabd80b03bf` |
| `posegraph-audio-2.zip` | `91e63c7c0d97fca103b499437d57cc93246828cc5305732a6356e3a6f3fee378` |

对不上就不要用，重新下载。后端代码 `Posegraph-0.7.0.zip` 每次从 GitHub 下载时会重新打包，没有固定的校验值，建议自己从 GitHub 下载。

另一种省事的办法：把核对过的组件压缩包放进一键包文件夹下的 `.download` 文件夹（没有就新建），改名为 `face.zip` / `depth.zip`，
再在插件里点「更新后端」。插件会先检查这里的文件，校验对得上就直接安装、不再下载（这时只需要能连上 GitHub 下载约 5MB 的代码）。
