namespace ScreenLeash.Core;

/// <summary>
/// 互操作层的**非视觉**常量。与 <see cref="Metrics"/> 分工明确：
/// Metrics 管「看起来多大」（DIP→px），本类管「系统接口要多少」（缓冲区长度、消息号、钩子类型）。
/// 放在 Core（不在裸像素门禁扫描范围内）是为了让 Interop/ 下不出现任何裸数字——
/// 视觉数字必须来自 Metrics，系统数字必须来自这里，两边都不许就地写常量。
/// </summary>
public static class InteropConstants
{
    // ── 缓冲区 ───────────────────────────────────────────────────
    /// <summary>GetClassName 缓冲区字符数（Win32 类名上限 256）。</summary>
    public const int ClassNameBufferLength = 256;

    /// <summary>GetWindowText 等通用文本缓冲区字符数。</summary>
    public const int WindowTextBufferLength = 512;

    // ── 键盘钩子 ─────────────────────────────────────────────────
    /// <summary>WH_KEYBOARD_LL：低级键盘钩子类型。</summary>
    public const int WhKeyboardLl = 13;

    /// <summary>WM_KEYDOWN。</summary>
    public const int WmKeyDown = 0x0100;

    /// <summary>WM_SYSKEYDOWN（Alt 组合键）。</summary>
    public const int WmSysKeyDown = 0x0104;

    /// <summary>虚拟键 V。</summary>
    public const int VkKeyV = 0x56;

    /// <summary>虚拟键 Control（左右通用）。</summary>
    public const int VkControl = 0x11;

    /// <summary>虚拟键 Shift。</summary>
    public const int VkShift = 0x10;

    /// <summary>虚拟键 Menu(Alt)。</summary>
    public const int VkMenu = 0x12;

    /// <summary>虚拟键左键（拖拽状态探测）。</summary>
    public const int VkLeftButton = 0x01;

    /// <summary>GetAsyncKeyState 的「当前按下」掩码。</summary>
    public const int KeyStatePressed = 0x8000;

    // ── 剪贴板 ───────────────────────────────────────────────────
    /// <summary>WM_CLIPBOARDUPDATE：AddClipboardFormatListener 的回调消息。</summary>
    public const int WmClipboardUpdate = 0x031D;

    /// <summary>HTML Format 剪贴板格式名（带空格，非 DataFormats 成员）。</summary>
    public const string HtmlFormat = "HTML Format";

    // ── 窗口样式 ─────────────────────────────────────────────────
    /// <summary>WS_EX_NOACTIVATE：点击不抢焦点。</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>WS_EX_TOOLWINDOW：不进 Alt+Tab。</summary>
    public const int WsExToolWindow = 0x00000080;

    // ── 文本摄入 ─────────────────────────────────────────────────
    /// <summary>空白/换行规整后的最小有效文本长度（更短的多半是误复制）。</summary>
    public const int MinMeaningfulTextLength = 1;

    /// <summary>剪贴板去重哈希取用的字节数（BitConverter.ToInt64 需要 8）。</summary>
    public const int HashBytesToCompare = 8;

    // ── 容量显示 ─────────────────────────────────────────────────
    /// <summary>1 MB 的字节数（整数显示用）。</summary>
    public const long BytesPerMegabyte = 1024L * 1024L;

    /// <summary>1 MB 的字节数（浮点格式化用）。</summary>
    public const double BytesPerMegabyteDouble = 1024.0 * 1024.0;
}
