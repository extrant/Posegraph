using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace Posegraph.UI;

/// <summary>交流群、赞助链接（主窗口工具栏的爱心、设置页「关于与支持」、更新日志窗口共用）。</summary>
public static class Community
{
    public const string Afdian = "https://afdian.com/a/Sincraft0515";
    public const string Discord = "https://discord.gg/2z6KdPgacj";
    public const string QqGroup = "https://qm.qq.com/q/TGuxNR7auq";
    public const string GitHub = "https://github.com/extrant/Posegraph";

    public static readonly Vector4 AfdianColor = new(0.55f, 0.36f, 0.85f, 1);
    public static readonly Vector4 DiscordColor = new(0.35f, 0.40f, 0.86f, 1);
    public static readonly Vector4 QqColor = new(0.11f, 0.56f, 0.86f, 1);
    public static readonly Vector4 Heart = new(0.95f, 0.42f, 0.55f, 1);

    public static void Open(string url)
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

    /// <summary>一个彩色链接按钮，悬停显示网址。</summary>
    public static void LinkButton(FontAwesomeIcon icon, string label, string url, Vector4 color, Vector2? size = null)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, color))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, (color * 1.2f) with { W = 1 }))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, (color * 0.9f) with { W = 1 }))
            if (size is { } s ? ImGuiComponents.IconButtonWithText(icon, label, size: s) : ImGuiComponents.IconButtonWithText(icon, label))
                Open(url);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(url);
    }
}

/// <summary>「支持 Posegraph」窗口：一句说明、爱发电 / Discord / QQ 群按钮、微信赞赏码（图片内嵌在插件里）。</summary>
public sealed class SupportWindow : Window, IDisposable
{
    private readonly ITextureProvider _textures;
    private Task<IDalamudTextureWrap>? _qr;

    public SupportWindow(ITextureProvider textures)
        : base(Loc.T("支持 Posegraph") + "###PosegraphSupport",
               ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking
               | ImGuiWindowFlags.AlwaysAutoResize)
    {
        _textures = textures;
    }

    public void Dispose()
    {
        if (_qr is { IsCompletedSuccessfully: true }) _qr.Result.Dispose();
    }

    public override void OnOpen() => _qr ??= LoadQr();

    private Task<IDalamudTextureWrap> LoadQr()
    {
        using var stream = typeof(SupportWindow).Assembly.GetManifestResourceStream("Posegraph.Support.wechat.jpg")
                           ?? throw new FileNotFoundException("wechat.jpg");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return _textures.CreateFromImageAsync(ms.ToArray());
    }

    public override void PreDraw()
    {
        PosegraphTheme.Push();
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void PostDraw() => PosegraphTheme.Pop();

    public override void Draw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = 300 * scale;
        using (ImRaii.PushColor(ImGuiCol.Text, Community.Heart))
            ImGui.TextUnformatted(Loc.T("所有功能都免费，赞助纯属自愿。"));
        UiText.Wrapped(UiText.CjkWrap(Loc.T("这个插件是业余时间做的。觉得好用的话，可以请作者喝杯奶茶；有问题、想要新功能，欢迎来群里聊。")),
                       PosegraphTheme.Grey, width);
        ImGui.Spacing();

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var button = new Vector2((width - spacing) / 2, ImGui.GetFrameHeight() * 1.25f);
        Community.LinkButton(FontAwesomeIcon.Heart, Loc.T("爱发电"), Community.Afdian, Community.AfdianColor, button);
        ImGui.SameLine();
        Community.LinkButton(FontAwesomeIcon.Users, Loc.T("QQ 群"), Community.QqGroup, Community.QqColor, button);
        Community.LinkButton(FontAwesomeIcon.Comments, "Discord", Community.Discord, Community.DiscordColor, button);
        ImGui.SameLine();
        Community.LinkButton(FontAwesomeIcon.Code, "GitHub", Community.GitHub, new Vector4(0.30f, 0.33f, 0.38f, 1), button);

        ImGui.Spacing();
        ImGui.TextDisabled(Loc.T("微信扫码赞赏："));
        if (_qr is { IsCompletedSuccessfully: true } t) ImGui.Image(t.Result.Handle, new Vector2(width, width));
        else ImGui.Dummy(new Vector2(width, width));
        ImGui.TextDisabled(Loc.T("QQ 群：Posegraph交流"));
    }
}
