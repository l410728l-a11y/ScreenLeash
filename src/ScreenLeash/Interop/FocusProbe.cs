using System;
using System.Runtime.InteropServices;
using System.Text;
using ScreenLeash.Core;

namespace ScreenLeash.Interop;

/// <summary>
/// 判断「当前前台焦点有没有可粘贴的目标」。策略是**保守黑名单**：
/// 只有明确认定「这里 Ctrl+V 没有意义」时才让绳子接管，其余（包括所有没见过的类名）一律放行。
///
/// <para>为什么反转成黑名单：早前的白名单思路（枚举 Edit/RichEdit/Scintilla 等可编辑类放行）
/// 会因为漏掉未知控件而吞掉用户正常的粘贴。错放行的代价是「粘贴没生效」，错接管的代价是
/// 「粘贴跑进绳子、目标应用没反应」——后者更让人困惑，所以只有确定无目标时才接管。</para>
///
/// <para>明确接管的情况：无焦点句柄、桌面本体（Progman/WorkerW/SHELLDLL_DefView）。
/// 注意资源管理器窗口<b>不接管</b>——在文件夹里 Ctrl+V 是「粘贴文件」，必须留给系统。</para>
/// </summary>
public static class FocusProbe
{
    private static readonly string[] DesktopClassNames =
    {
        "Progman",
        "WorkerW",
        "SHELLDLL_DefView",
    };

    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = false)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public Rect rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// 当前是否应该由绳子接管控件粘贴。true = 前台确实没有可粘贴的地方。
    /// 任何探测失败都返回 false（放行给系统），遵循「宁可不管，不可误管」。
    /// </summary>
    public static bool ShouldTakeOverPaste() => ShouldTakeOverPaste(force: false);

    /// <summary>
    /// 同上，并允许调用方显式强制接管（<paramref name="force"/>）。
    /// 组合律只有一份实现，落在纯函数 <see cref="PasteTargetPolicy.ShouldTakeOver"/> 里，
    /// 这里只负责探测「前台有没有可粘贴目标」这一半事实。
    /// </summary>
    /// <param name="force">用户/设置显式要求接管，优先于探测结果。</param>
    public static bool ShouldTakeOverPaste(bool force)
    {
        bool foregroundHasNoPasteTarget = ProbeForegroundHasNoPasteTarget();
        return PasteTargetPolicy.ShouldTakeOver(force, foregroundHasNoPasteTarget);
    }

    /// <summary>探测：前台是否确实没有可粘贴目标。任何探测失败都返回 false（放行给系统）。</summary>
    private static bool ProbeForegroundHasNoPasteTarget()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            // 没有任何前台窗口（例如焦点在桌面且未激活任何程序）：接管。
            return true;
        }

        var focus = TryGetFocusWindow(foreground);
        if (focus == IntPtr.Zero)
        {
            // 前台线程没有焦点句柄：典型情况就是点在桌面空白处。
            return true;
        }

        var className = TryGetClassName(focus);
        if (string.IsNullOrEmpty(className))
        {
            // 注意：与 IsNoPasteTargetClass("") 的约定相反——**探测**失败时保守放行，避免误吞；
            // 只有「确实拿不到焦点窗口」才接管。两者不可合并。
            return false;
        }

        return IsNoPasteTargetClass(className);
    }

    /// <summary>取前台线程的焦点窗口；失败返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr TryGetFocusWindow(IntPtr foreground)
    {
        var threadId = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        if (threadId == 0)
        {
            return IntPtr.Zero;
        }

        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info))
        {
            return IntPtr.Zero;
        }

        return info.hwndFocus;
    }

    /// <summary>取窗口类名；失败返回空串。</summary>
    public static string TryGetClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(InteropConstants.ClassNameBufferLength);
        var length = GetClassNameW(hWnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString(0, Math.Min(length, buffer.Length));
    }

    /// <summary>
    /// 纯判定（可单测）：给定焦点窗口类名，是否属于「无粘贴目标」集合。
    /// 传入空/null 视为无焦点 → 接管。
    /// </summary>
    public static bool IsNoPasteTargetClass(string? className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return true;
        }

        foreach (var desktopClass in DesktopClassNames)
        {
            if (string.Equals(className, desktopClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
