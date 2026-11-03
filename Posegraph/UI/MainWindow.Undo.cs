using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Posegraph.Backend;

namespace Posegraph.UI;

/// <summary>
/// 调整页、3D 页的撤销 / 重做：记的是整份重定向参数（预设 + 调整页的数值 + 关节偏移）。
/// 不在每个滑杆里埋点，而是每帧看参数和上次记下的是否不同——没在拖控件、没按着鼠标时才算一步（拖一次滑杆、转一次关节环 = 一步）。
/// 换了图、换了人（合并会话里切人）时清空记录，等几帧参数读进来稳定了再开始记。撤销后按「松手应用」的设置重算、应用到角色。
/// </summary>
public sealed partial class MainWindow
{
    private const int MaxParamHistory = 60;
    private const int ParamSettleFrames = 10;   // 换图 / 换人后等这么多帧再开始记（参数可能在后台线程里刚换上）

    private readonly Stack<(string Preset, RetargetParams Params)> _paramUndo = new();
    private readonly Stack<(string Preset, RetargetParams Params)> _paramRedo = new();
    private (string Preset, RetargetParams Params)? _paramCommitted;
    private string? _paramCommittedSnapshot;
    private (string? Key, int Person, bool Merge)? _paramContext;
    private int _paramSettle;

    /// <summary>每帧调：参数和上次记下的不一样、且这一步改完了（没在拖控件），就把上次的那份压进撤销。</summary>
    private void TrackParamsHistory()
    {
        var context = (_sessionKey, _person, _mergeSession);
        if (_paramContext != context)
        {
            _paramContext = context;
            _paramUndo.Clear();
            _paramRedo.Clear();
            _paramSettle = ParamSettleFrames;
        }
        if (_paramSettle > 0)
        {
            _paramSettle--;
            CommitParams();
            return;
        }
        if (ImGui.IsAnyItemActive() || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        var snapshot = FullSnapshot(_preset, _params);
        if (snapshot == _paramCommittedSnapshot || _paramCommitted is not { } before) return;
        PushParams(_paramUndo, before);
        _paramRedo.Clear();
        CommitParams();
    }

    private void CommitParams()
    {
        _paramCommitted = (_preset, _params.Clone());
        _paramCommittedSnapshot = FullSnapshot(_preset, _params);
    }

    private static void PushParams(Stack<(string, RetargetParams)> stack, (string, RetargetParams) item)
    {
        stack.Push(item);
        if (stack.Count <= MaxParamHistory) return;
        var keep = stack.Take(MaxParamHistory).Reverse().ToList();
        stack.Clear();
        foreach (var s in keep) stack.Push(s);
    }

    private void UndoParams() => StepParams(_paramUndo, _paramRedo, Loc.T("已撤销"));

    private void RedoParams() => StepParams(_paramRedo, _paramUndo, Loc.T("已重做"));

    private void StepParams(Stack<(string Preset, RetargetParams Params)> from, Stack<(string, RetargetParams)> to, string done)
    {
        if (from.Count == 0 || _session is null) return;
        PushParams(to, (_preset, _params.Clone()));
        var (preset, p) = from.Pop();
        _preset = preset;
        _params = p.Clone();
        CommitParams();
        RequestRetarget(_config.PushOnRelease);
        SetStatus(done, Grey);
    }

    /// <summary>撤销、重做按钮（调整页、3D 页顶上的修正栏里）。</summary>
    private void DrawParamsUndoRedo()
    {
        if (IconButton("##paramUndo", FontAwesomeIcon.Undo, Loc.T("撤销调整（Ctrl+Z）：调整页的数值、3D 页的关节偏移"), _paramUndo.Count > 0))
            UndoParams();
        ImGui.SameLine();
        if (IconButton("##paramRedo", FontAwesomeIcon.Redo, Loc.T("重做（Ctrl+Y）"), _paramRedo.Count > 0))
            RedoParams();
    }

    /// <summary>主窗口有焦点（不在输入框里打字）时 Ctrl+Z 撤销调整、Ctrl+Y / Ctrl+Shift+Z 重做。</summary>
    private void HandleParamsShortcuts()
    {
        var io = ImGui.GetIO();
        if (!io.KeyCtrl || io.WantTextInput || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Z, false))
        {
            if (io.KeyShift) RedoParams();
            else UndoParams();
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Y, false)) RedoParams();
    }
}
