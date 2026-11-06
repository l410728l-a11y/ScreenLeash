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

    // ── 全局粘贴钩子 ─────────────────────────────────────────────
    /// <summary>WM_APP + 1：钩子线程 PostMessage 给接收窗口的「粘贴请求」消息。</summary>
    public const int WmAppPaste = 0x8000 + 1;

    /// <summary>WM_QUIT：PostThreadMessage 让钩子线程退出消息泵。</summary>
    public const int WmQuit = 0x0012;

    /// <summary>装钩子时等待钩子线程就绪的上限（毫秒）。</summary>
    public const int HookReadyTimeoutMs = 2000;

    /// <summary>卸钩子时等待钩子线程退出（Join）的上限（毫秒）。</summary>
    public const int HookShutdownTimeoutMs = 2000;

    // ── 全局热键（Ctrl+V 兜底的降级入口）─────────────────────────
    /// <summary>WM_HOTKEY：RegisterHotKey 注册的组合键被按下时投递给窗口的消息。</summary>
    public const int WmHotkey = 0x0312;

    /// <summary>MOD_ALT。</summary>
    public const int ModAlt = 0x0001;

    /// <summary>MOD_CONTROL。</summary>
    public const int ModControl = 0x0002;

    /// <summary>MOD_NOREPEAT：按住不放只触发一次。</summary>
    public const int ModNoRepeat = 0x4000;

    /// <summary>降级热键 Ctrl+Alt+V 的标识（用来在 WndProc 里区分其它 RegisterHotKey 调用方）。</summary>
    public const int PasteHotkeyId = 0x5C1;

    /// <summary>剪贴板 STA 读取的总预算（毫秒）：超时即提示「剪贴板被占用」，绝不让 UI 线程等。</summary>
    public const int ClipboardReadTimeoutMs = 3000;

    // ── 剪贴板 ───────────────────────────────────────────────────
    /// <summary>WM_CLIPBOARDUPDATE：AddClipboardFormatListener 的回调消息。</summary>
    public const int WmClipboardUpdate = 0x031D;

    /// <summary>HTML Format 剪贴板格式名（带空格，非 DataFormats 成员）。</summary>
    public const string HtmlFormat = "HTML Format";

    // ── 窗口样式 ─────────────────────────────────────────────────
    /// <summary>WS_EX_NOACTIVATE：点击不抢焦点。</summary>
    public const int WsExNoActivate = 0x08000000;

    /// <summary>WM_NCHITTEST：系统问"这个点归哪个窗口"。</summary>
    public const int WmNcHitTest = 0x0084;

    /// <summary>HTTRANSPARENT：让鼠标继续去找下面的窗口（等于点击穿透）。
    /// 只影响鼠标消息，不影响 OLE 拖放——后者由 RegisterDragDrop 按窗口矩形投递。</summary>
    public const int HtTransparent = -1;

    /// <summary>WM_NCHITTEST 的 lParam 里，X 与 Y 各占的位宽（低 16 位 X、高 16 位 Y）。</summary>
    public const int HitTestCoordBits = 16;

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
