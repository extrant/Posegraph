using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Posegraph.UI;

/// <summary>单行文字放不下时的处理：截成「…」结尾，悬停显示全文（窗口窄、英文更长时不会被生硬裁掉）。</summary>
public static class UiText
{
    /// <summary>截到 width 宽以内，放不下时以「…」结尾。</summary>
    public static string Ellipsize(string text, float width)
    {
        if (width <= 0) return "";
        if (ImGui.CalcTextSize(text).X <= width) return text;
        // 二分找能放下的最长前缀
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + "…").X <= width) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? "…" : text[..lo] + "…";
    }

    /// <summary>彩色单行文字，按剩余宽度截断，截断时悬停显示全文。</summary>
    public static void Fit(string text, Vector4 color, float? width = null)
    {
        var shown = Ellipsize(text, width ?? ImGui.GetContentRegionAvail().X);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(shown);
        if (shown != text && ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    /// <summary>下一个控件（宽 width）在这一行放得下就接在后面，否则换行。在上一个控件之后调用（窗口窄、英文更长时不会被裁掉）。</summary>
    public static void SameLineIfFits(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width) ImGui.NewLine();
    }

    /// <summary>勾选框连文字的宽度（文字里 ## 之后的不算）。</summary>
    public static float CheckboxWidth(string label)
        => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label.Split("##")[0]).X;

    /// <summary>图标加文字按钮（ImGuiComponents.IconButtonWithText）的大概宽度。</summary>
    public static float IconButtonWidth(string label)
        => ImGui.GetFrameHeight() + ImGui.CalcTextSize(label.Split("##")[0]).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemInnerSpacing.X;

    /// <summary>下拉框的预览文字：去掉右侧箭头占的宽度后截断。</summary>
    public static string ComboPreview(string text, float comboWidth)
        => Ellipsize(text, comboWidth - ImGui.GetFrameHeight() - ImGui.GetStyle().FramePadding.X * 2);
}
