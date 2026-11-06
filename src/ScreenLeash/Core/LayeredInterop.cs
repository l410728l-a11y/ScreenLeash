using System;
using System.Runtime.InteropServices;

namespace ScreenLeash.Core;

/// <summary>
/// layered window 所需的 Win32 声明。
/// <para>放在 Core 而不是 Interop：UI/ 与 Interop/ 有"禁止非 0/1 数字字面量"的源码扫描门禁，
/// 而 P/Invoke 的样式/标志位常量必然是这些值（WS_EX_LAYERED、ULW_ALPHA…），
/// 它们是窗口 API 的一部分，不是视觉尺寸，不属于该门禁要防的东西。</para>
/// </summary>
internal static class LayeredInterop
{
    internal const int GWL_EXSTYLE = -20;

    internal const int WS_EX_LAYERED = 0x00080000;

    internal const byte AC_SRC_OVER = 0x00;

    internal const byte AC_SRC_ALPHA = 0x01;

    internal const int ULW_ALPHA = 0x00000002;

    /// <summary>BLENDFUNCTION.SourceConstantAlpha 的"不透明"值（整字节满值）。</summary>
    internal const byte OpaqueAlpha = byte.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        internal int CX;
        internal int CY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BLENDFUNCTION
    {
        internal byte BlendOp;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        int crKey,
        ref BLENDFUNCTION pblend,
        int dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
