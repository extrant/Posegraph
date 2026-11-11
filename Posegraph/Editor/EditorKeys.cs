using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace Posegraph.Editor;

/// <summary>
/// 编辑器的快捷键。Dalamud 只在 ImGui 文本框输入时才把字母键交给 ImGui，所以 ImGui.IsKeyPressed 收不到 G / R；
/// 这里直接读键盘的物理状态（GetAsyncKeyState，游戏窗口在前台时）判断按下，
/// 并在游戏读按键之前（Framework.Update）把这些键在游戏的按键表里清掉，游戏就收不到（不会因为 Esc 退出 GPose）。
/// </summary>
public sealed class EditorKeys(IKeyState keyState)
{
    public const int Escape = 0x1B, Enter = 0x0D, Backspace = 0x08, Shift = 0x10, Ctrl = 0x11, Alt = 0x12;
    public const int G = 'G', R = 'R', X = 'X', Y = 'Y', Z = 'Z', P = 'P', M = 'M';
    public const int Minus = 0xBD, Period = 0xBE, NumpadMinus = 0x6D, NumpadDecimal = 0x6E;

    /// <summary>选中节点时归编辑器的键。</summary>
    public static readonly int[] SelectionKeys = [G, R, P, M, X, Y, Z, Escape];

    /// <summary>G / R 操作中另外归编辑器的键（数字、确认……）。</summary>
    public static readonly int[] OperationKeys =
    [
        Enter, Backspace, Minus, Period, NumpadMinus, NumpadDecimal,
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
    ];

    private readonly bool[] _down = new bool[256];
    private readonly bool[] _pressed = new bool[256];

    /// <summary>每帧（Draw 开头）读一次：哪些键是这一帧刚按下的。</summary>
    public void Poll()
    {
        var foreground = GameInForeground();
        foreach (var vk in SelectionKeys.Concat(OperationKeys).Append(Shift).Append(Ctrl).Append(Alt))
        {
            var down = foreground && (GetAsyncKeyState(vk) & 0x8000) != 0;
            _pressed[vk] = down && !_down[vk];
            _down[vk] = down;
        }
    }

    public bool Pressed(int vk) => _pressed[vk];

    public bool Down(int vk) => _down[vk];

    /// <summary>游戏主线程（Framework.Update）调用：把这些键从游戏的按键表里清掉。</summary>
    public void Block(IEnumerable<int> keys)
    {
        foreach (var vk in keys)
            if (Enum.IsDefined(typeof(VirtualKey), (VirtualKey)vk) && keyState.IsVirtualKeyValid((VirtualKey)vk) && keyState[(VirtualKey)vk])
                keyState[(VirtualKey)vk] = false;
    }

    private static readonly uint ProcessId = (uint)Environment.ProcessId;

    private static bool GameInForeground()
    {
        var window = GetForegroundWindow();
        if (window == nint.Zero) return false;
        GetWindowThreadProcessId(window, out var pid);
        return pid == ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
