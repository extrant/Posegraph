using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Posegraph.UI;

/// <summary>设置页的「多图识别并行数」（实验，配合 Library/RecognitionService.Parallel.cs）。</summary>
public sealed partial class MainWindow
{
    private bool DrawRecognitionConcurrencySettings()
    {
        var changed = false;
        var parallelism = Math.Clamp(_config.BatchRecognitionParallelism, 1, 8);
        ImGui.SetNextItemWidth(70 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##imageParallelism", parallelism.ToString()))
        {
            for (var count = 1; count <= 8; count++)
                if (ImGui.Selectable(count.ToString(), parallelism == count))
                {
                    _config.BatchRecognitionParallelism = count;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("多图识别并行数（实验）"));
        ItemTip(Loc.T("只影响图库的批量识别。2～8 时几张图共用一份模型同时识别：GPU 上约省 25%~35% 的时间，每多一路多占约 200MB 显存。\nCPU 模式不建议开"));
        if (parallelism > 1 && _config.BackendDevice == "cpu")
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, Loc.T("CPU 模式提速有限"));
        }
        return changed;
    }
}
