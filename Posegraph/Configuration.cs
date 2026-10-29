using Dalamud.Configuration;
using Posegraph.Backend;

namespace Posegraph;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>本地 HTTP 端口（Brio 自带 WebAPI 用 42428，这里错开）。</summary>
    public int Port { get; set; } = 42429;

    public bool AutoStartServer { get; set; } = true;

    /// <summary>界面语言：auto（跟随游戏客户端）/ zh / en。</summary>
    public string Language { get; set; } = "auto";

    // ---- 识别后端（backend）
    /// <summary>后端虚拟环境的 python.exe，如 D:\...\backend\.venv\Scripts\python.exe。</summary>
    public string BackendPython { get; set; } = "";

    /// <summary>backend 目录（工作目录）。</summary>
    public string BackendDir { get; set; } = "";

    public int BackendPort { get; set; } = 42430;

    /// <summary>插件加载时自动启动后端。</summary>
    public bool AutoStartBackend { get; set; }

    /// <summary>后端启动后立即加载模型，首次识别不用等（"识别后立即释放"时无效）。</summary>
    public bool PreloadModel { get; set; }

    /// <summary>推理设备：auto（可用显存不足时用 CPU）/ cuda / cpu。</summary>
    public string BackendDevice { get; set; } = "auto";

    /// <summary>识别后空闲多少秒释放模型：0 立即释放（玩游戏时不占显存），-1 常驻。</summary>
    public int ReleaseAfter { get; set; }

    /// <summary>GPU 模式下把 MHR 留在 CPU，显存约省 750MB，每张约慢 0.5 秒。</summary>
    public bool MhrOnCpu { get; set; } = true;

    /// <summary>快速识别（移植自 Fast SAM 3D Body）：左右手合成一批、MHR 不算修正形变。</summary>
    public bool FastRecognition { get; set; } = true;

    /// <summary>识别时用深度模型（MoGe）：镜头焦距、多人按深度修遮罩和远近、找地面扶正。</summary>
    public bool UseDepth { get; set; } = true;

    /// <summary>多图预识别最多同时处理的图片数（1～8），默认串行；从下一批图片生效。</summary>
    public int BatchRecognitionParallelism { get; set; } = 1;

    /// <summary>插件卸载 / 游戏退出时关闭由插件启动的后端。</summary>
    public bool StopBackendOnExit { get; set; } = true;

    /// <summary>后端更新的下载加速前缀（加在 GitHub 地址前面，如 https://gh-proxy.org/），空为直连。</summary>
    public string UpdateProxy { get; set; } = "";

    // ---- 操作习惯
    /// <summary>松开滑杆时自动应用到角色（拖动中只更新预览）。</summary>
    public bool PushOnRelease { get; set; } = true;

    /// <summary>进入 GPose 且 Brio 就绪时自动打开主界面（自动打开的在退出 GPose 时关闭）。</summary>
    public bool AutoOpenInGpose { get; set; } = true;

    /// <summary>游戏界面隐藏时仍显示（默认与 Brio 一致：跟随游戏界面隐藏）。</summary>
    public bool ShowWhenUiHidden { get; set; }

    /// <summary>新图识别完成后立即应用到角色。</summary>
    public bool PushAfterRecognize { get; set; }

    public bool ShowMhrOverlay { get; set; } = true;
    public bool ShowFfxivOverlay { get; set; } = true;
    public bool ShowMhrInStick { get; set; }

    public string LastImageDir { get; set; } = "";
    public string ExportDir { get; set; } = "";

    // ---- 图库
    /// <summary>用户添加的文件夹来源（不复制文件）；自带图库（文档\Posegraph\Images）不在这里。</summary>
    public List<Library.LibrarySource> LibrarySources { get; set; } = new();
    public string LibraryFolder { get; set; } = "";
    public float LibraryThumbSize { get; set; } = 120;

    /// <summary>看过更新日志的插件版本：与当前版本不同时，第一次打开主窗口弹出更新日志。</summary>
    public string LastChangelog { get; set; } = "";

    // ---- 拆分模式（角色列表）
    /// <summary>角色列表贴在主窗口右边跟着移动。</summary>
    public bool SplitDock { get; set; } = true;

    /// <summary>角色列表的模式：false 拆分（每个角色一张图），true 合并（一张图里的多个人分给各个角色）。</summary>
    public bool SplitMerge { get; set; }

    /// <summary>合并模式「全部应用」后按照片复刻各角色的站位（以基准人物的角色为准）。</summary>
    public bool MergePlacement { get; set; } = true;

    /// <summary>构图框：显示、比例（"photo" 为当前照片，或 "3:2" 等）、竖构图、框外压暗、参考线（0 无 1 三分 2 中心 3 黄金分割）。</summary>
    public bool FrameShow { get; set; }
    public string FrameAspect { get; set; } = "photo";
    public bool FramePortrait { get; set; }
    public float FrameDim { get; set; } = 0.6f;
    public int FrameGuides { get; set; } = 1;

    /// <summary>原图参考：叠原图（不透明度，0 为不叠）、画原图骨架。只画在界面层，截图里没有。</summary>
    public float FramePhotoAlpha { get; set; }
    public bool FramePhotoSkeleton { get; set; }

    /// <summary>截图文件夹；空为 文档\Posegraph\Screenshots。</summary>
    public string ScreenshotDir { get; set; } = "";

    /// <summary>灯光：转镜头时灯跟着转（始终从镜头这一侧照亮脸）。</summary>
    public bool LightFollowCamera { get; set; } = true;

    /// <summary>灯光：Posegraph 给打光的角色换了姿势、或角色被挪动 / 转身后，自动重新读脸再摆。</summary>
    public bool LightFollowSubject { get; set; } = true;

    /// <summary>灯光的打光范围：auto（看游戏镜头拍到多少）/ face / half / full。</summary>
    public string LightFraming { get; set; } = "auto";

    /// <summary>灯光：3D 布光窗口（贴在灯光窗口右边）展开着。</summary>
    public bool LightSceneOpen { get; set; } = true;

    /// <summary>灯光：给角色套主窗口的图时，这张图在图库里存过灯光就一并恢复。</summary>
    public bool LightRestoreFromLibrary { get; set; } = true;

    /// <summary>灯光 3D 视图的显示选项（专业）：聚光锥、柔和边、镜头、轨道、光照预览球；拖灯方式：自由移动、角度吸附。</summary>
    public bool LightShowCones { get; set; } = true;
    public bool LightShowSoftEdge { get; set; }
    public bool LightShowCamera { get; set; } = true;
    public bool LightShowOrbit { get; set; } = true;
    public bool LightShowPreview { get; set; } = true;
    public bool LightFreeMove { get; set; }
    public bool LightSnap { get; set; }

    /// <summary>自己存的灯光方案（LightRig 的 JSON，灯的位置相对脸和镜头）。</summary>
    public List<string> LightRigs { get; set; } = [];

    /// <summary>合并模式 3D 页叠加其他人的骨架（洋葱皮），看清相对位置。</summary>
    public bool ShowOnion { get; set; } = true;

    /// <summary>合并模式找人用的检测模型：auto（先真人，找不到再插画）/ photo / anime。</summary>
    public string MergeDetector { get; set; } = "auto";

    /// <summary>新图默认使用的预设：photo / illustration。</summary>
    public string DefaultPreset { get; set; } = "photo";

    /// <summary>0.3.4～0.3.10 按预设自动保存的调整值。已不再使用：新图片一律用默认值，每张图的值随「保存修正」存进这张图（留着字段免得旧配置读不进来）。</summary>
    public Dictionary<string, RetargetParams> SavedParams { get; set; } = new();
}
