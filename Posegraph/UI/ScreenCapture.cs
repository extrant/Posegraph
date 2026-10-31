using System.Runtime.InteropServices;

namespace Posegraph.UI;

/// <summary>
/// ReShade 兼容截图：直接拷屏幕上游戏窗口那块（DWM 合成后的最终画面，ReShade 等所有后期都在里面）。
/// 只适用于窗口 / 无边框窗口；全屏独占时读到的是黑的。窗口被别的窗口挡住的部分也会被拷进来。
/// </summary>
internal static class ScreenCapture
{
    /// <summary>拷游戏窗口客户区，返回 BGRA 像素（透明度填满）。</summary>
    public static (byte[] Pixels, int Width, int Height) Capture(nint window)
    {
        if (!GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
            throw new InvalidOperationException(Loc.T("找不到游戏窗口"));
        var origin = new Point();
        ClientToScreen(window, ref origin);
        int width = rect.Right, height = rect.Bottom;
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020))   // SRCCOPY
                throw new InvalidOperationException(Loc.T("拷屏幕失败"));
            SelectObject(memory, old);
            var info = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };   // 负高度：从上往下
            var pixels = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                throw new InvalidOperationException(Loc.T("拷屏幕失败"));
            var black = true;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (black && (pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0) black = false;
                pixels[i + 3] = 255;
            }
            if (black) throw new InvalidOperationException(Loc.T("屏幕上读到的是全黑：全屏独占模式读不到，换成无边框窗口"));
            return (pixels, width, height);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint src, int srcX, int srcY, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
}
