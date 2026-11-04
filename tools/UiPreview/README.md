# UiPreview：离线界面截图

不进游戏，直接运行插件的真实窗口代码（`Posegraph.dll`），用 Dalamud 自带的 cimgui 生成绘制数据，再用 SkiaSharp 软件光栅化成 PNG。
用来快速检查排版（默认窗口大小下有没有裁切）、制作 README / 教程 / 更新日志的截图。

## 用法

先编译插件（Release），再：

```
dotnet run -c Release -- all --out shots
dotnet run -c Release -- main changelog --lang en --scale 1.25 --out shots_en
```

- 不带场景名时列出全部场景；`all` 为全部
- `--lang zh|en` 界面语言，`--scale` 界面缩放（对应 Dalamud 的全局缩放）
- 每个场景输出一张按窗口裁切的 PNG

场景在 `Scenes.cs` 里加：设好窗口状态，调用 `app.Shot(...)`。

## 原理与限制

- 字体用 Dalamud 资源目录里的 Noto Sans CJK 与 FontAwesome（`%AppData%\XIVLauncherCN\dalamudAssets\dev\UIRes`），颜色用 Dalamud 默认主题，插件窗口再套自己的主题，和游戏里看到的基本一致
- Dalamud 服务（`IDalamudPluginInterface`、`ITextureProvider` 等）是替身（`Stub.cs`、`Services.cs`）；
  依赖游戏的静态成员（字体、界面缩放）和读游戏内存 / Brio IPC 的入口用 Harmony 换掉（`Host.cs`、`PluginPatches.cs`）
- 不能读 `UiBuilder.DefaultFontSizePx` 这类取游戏服务配置的成员，离线时会一直等待
- 需要识别结果的画面（图像叠加、3D 骨架）要后端在线（见下面的教程截图）；其余场景都是未识别状态
- Skia 遇到纹理坐标共线的三角形会跳过，纯色和整数粗细的抗锯齿线都按顶点颜色画（`SkiaRenderer.cs`）

## 教程截图

`docs/tutorial` 里的图这样生成（需要本机后端在默认端口 42430 运行，第一次会真实识别示例图，之后读缓存）：

```
dotnet run -c Release -- tut-empty tut-image tut-3d tut-tuning tut-hints tut-settings tut-library tut-split tut-aux tut-changelog tut-idle ^
    --demo ..\..\docs\tutorial\images --lang zh --out shots_zh
python annotate.py shots_zh ..\..\docs\tutorial\images\zh --lang zh
```

英文把 `zh` 换成 `en`。`--demo` 用临时目录里的图库和识别缓存（不碰自己的图库，个人图片不会被拍进教程），GPose 里放几个假角色。
`tut-image` 要在 `tut-3d`、`tut-tuning`、`tut-hints`、`tut-split` 之前（它们沿用同一个识别结果）。
`tut-idle`（转换为 idle 窗口）离线没有游戏的 Havok：青色是后端真实算出的姿势，灰色「原版」是骨架绑定姿势放下手臂的示意站姿。

`annotate.py` 按 `tutorial_annotations.json` 在截图上放编号标记或高亮框，说明文字按编号列在右侧。
坐标是截图像素；英文文字更长、位置不同时用 `at_en` / `box_en`，编号挤在一起时用 `dir` 换个方向。

## 横幅

`make_banner.py` 生成更新日志窗口的横幅 `Posegraph/Changelog/banner.png`（几个识别出的姿势画成骨架节点图）：

```
python make_banner.py ../../backend/outputs/dancing.npz ../../backend/outputs/flex.npz ...
```
