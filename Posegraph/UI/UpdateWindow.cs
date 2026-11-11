using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Posegraph.UI;

/// <summary>
/// 更新日志窗口：插件更新后第一次打开主窗口时弹出（新用户也弹一次），设置页可以再打开。
/// 横幅 + 标语、链接按钮、按版本折叠的更新内容（当前版本展开）、按住关闭（防止误关）。
/// 内容在 Changelog/changelog.json（中英两份）；只显示不高于当前插件版本的条目，下个版本的内容可以先写好。
/// </summary>
public sealed class UpdateWindow : Window, IDisposable
{
    private const float HoldSeconds = 0.7f;

    private static readonly (string Label, string Url, Vector4 Color)[] Links =
    [
        ("Discord", Community.Discord, Community.DiscordColor),
        ("QQ 群", Community.QqGroup, Community.QqColor),
        ("爱发电", Community.Afdian, Community.AfdianColor),
        ("GitHub", Community.GitHub, new(0.30f, 0.33f, 0.38f, 1)),
        ("问题反馈", "https://github.com/extrant/Posegraph/issues", new(0.18f, 0.55f, 0.51f, 1)),
        ("全部版本", "https://github.com/extrant/Posegraph/releases", new(0.26f, 0.40f, 0.62f, 1)),
    ];

    private sealed record Localized(string Zh, string En)
    {
        public string Text => Loc.Lang == "en" && En.Length > 0 ? En : Zh;
    }

    private sealed record Entry(string Version, string Date, Localized Title, Dictionary<string, string[]> Items)
    {
        public string[] Lines => Items.GetValueOrDefault(Loc.Lang == "en" ? "en" : "zh") ?? Items.GetValueOrDefault("zh") ?? [];
    }

    private sealed record Changelog(Localized Tagline, Entry[] Entries);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly ITextureProvider _textures;
    private Changelog? _changelog;
    private Task<IDalamudTextureWrap>? _banner;
    private float _hold;
    private bool _scrollTop;

    /// <summary>当前插件版本（三段，如 0.3.10）。</summary>
    public static Version Current { get; } = Trim(typeof(UpdateWindow).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public UpdateWindow(Configuration config, IDalamudPluginInterface pi, ITextureProvider textures)
        : base(Loc.T("Posegraph 更新日志") + "###PosegraphChangelog",
               ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar
               | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking)
    {
        _config = config;
        _pi = pi;
        _textures = textures;
        Size = new Vector2(620, 640);
        SizeCondition = ImGuiCond.Always;
    }

    public void Dispose()
    {
        if (_banner is { IsCompletedSuccessfully: true }) _banner.Result.Dispose();
    }

    /// <summary>插件更新后（或新用户）第一次打开主窗口时调用：没看过这个版本的更新日志就弹出。</summary>
    public void ShowIfUpdated()
    {
        if (_config.LastChangelog == Current.ToString()) return;
        _config.LastChangelog = Current.ToString();
        _pi.SavePluginConfig(_config);
        IsOpen = true;
    }

    public override void OnOpen()
    {
        _changelog ??= Load();
        _banner ??= LoadBanner();
        _scrollTop = true;
        _hold = 0;
    }

    private static Stream Resource(string name)
        => typeof(UpdateWindow).Assembly.GetManifestResourceStream("Posegraph.Changelog." + name)
           ?? throw new FileNotFoundException(name);

    private static Changelog Load()
    {
        using var stream = Resource("changelog.json");
        var log = JsonSerializer.Deserialize<Changelog>(stream, Json)!;
        // 只列已发布的（不高于当前版本），新的在前
        var shown = log.Entries.Where(e => Version.TryParse(e.Version, out var v) && v <= Current)
                               .OrderByDescending(e => Version.Parse(e.Version)).ToArray();
        return log with { Entries = shown };
    }

    private Task<IDalamudTextureWrap> LoadBanner()
    {
        using var stream = Resource("banner.png");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return _textures.CreateFromImageAsync(ms.ToArray());
    }

    public override void PreDraw()
    {
        PosegraphTheme.Push();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;

        // 横幅：铺满宽度，按原图比例
        if (_banner is { IsCompletedSuccessfully: true } t)
        {
            var wrap = t.Result;
            ImGui.Image(wrap.Handle, new Vector2(width, width * wrap.Height / wrap.Width));
        }
        else ImGui.Dummy(new Vector2(width, width * 360 / 1280));

        // 标语与版本
        ImGui.Spacing();
        ImGui.TextColored(PosegraphTheme.Accent, _changelog?.Tagline.Text ?? "");
        ImGui.SameLine();
        ImGui.TextDisabled(Loc.F("— 当前版本 {0}", Current));

        // 链接按钮，平分一行
        ImGui.Spacing();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonW = (width - spacing * (Links.Length - 1)) / Links.Length;
        for (var i = 0; i < Links.Length; i++)
        {
            var (label, url, color) = Links[i];
            if (i > 0) ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, color))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, (color * 1.2f) with { W = 1 }))
                if (ImGui.Button(Loc.T(label), new Vector2(buttonW, ImGui.GetFrameHeight() * 1.25f)))
                    Open(url);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(url);
        }

        // 更新内容
        ImGui.Spacing();
        var footer = ImGui.GetFrameHeight() * 1.4f + ImGui.GetStyle().ItemSpacing.Y * 2;
        using (var child = ImRaii.Child("##entries", new Vector2(0, -footer), true))
        {
            if (child)
            {
                if (_scrollTop)
                {
                    ImGui.SetScrollY(0);
                    _scrollTop = false;
                }
                foreach (var entry in _changelog?.Entries ?? [])
                    DrawEntry(entry, entry.Version == Current.ToString());
            }
        }

        // 按住关闭
        var closeW = 260 * scale;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - closeW) / 2);
        if (HoldButton(Loc.T("按住关闭"), new Vector2(closeW, ImGui.GetFrameHeight() * 1.4f)))
            IsOpen = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("按住约 0.7 秒关闭（防止误关）。设置页可以再打开更新日志"));
    }

    private static void DrawEntry(Entry entry, bool current)
    {
        var header = $"v{entry.Version}  {entry.Title.Text}" + (entry.Date.Length > 0 ? $"   {entry.Date}" : "") + $"###{entry.Version}";
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, PosegraphTheme.AccentHover, current))
            open = ImGui.CollapsingHeader(header, current ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        if (!open) return;
        ImGui.Spacing();
        foreach (var line in entry.Lines)
        {
            // 圆点 + 自动换行（BulletText 不换行）
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(line);
            ImGui.PopTextWrapPos();
        }
        ImGui.Spacing();
    }

    /// <summary>按住 HoldSeconds 秒才算点击，按住时从左往右填充进度。</summary>
    private bool HoldButton(string label, Vector2 size)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Button(label, size);
        _hold = ImGui.IsItemActive() ? _hold + ImGui.GetIO().DeltaTime : 0;
        if (_hold > 0)
        {
            var fill = Math.Min(1, _hold / HoldSeconds);
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(size.X * fill, size.Y),
                                                    ImGui.GetColorU32(PosegraphTheme.Accent with { W = 0.45f }), ImGui.GetStyle().FrameRounding);
        }
        if (_hold < HoldSeconds) return false;
        _hold = 0;
        return true;
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不影响使用
        }
    }
}
