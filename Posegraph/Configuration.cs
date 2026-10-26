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

    /// <summary>新图默认使用的预设：photo / illustration。</summary>
    public string DefaultPreset { get; set; } = "photo";

    /// <summary>每个预设上次调整过的值（不含手动偏移，偏移只对当前图有意义）。</summary>
    public Dictionary<string, RetargetParams> SavedParams { get; set; } = new();
}
