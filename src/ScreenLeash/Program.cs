using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenLeash.Core;
using ScreenLeash.UI;

namespace ScreenLeash;

/// <summary>
/// 应用入口：单实例守护、崩溃落盘、托盘宿主、绳子窗口生命周期。
/// </summary>
internal static class Program
{
    internal const string ProductName = "ScreenLeash";

    private const string MutexName = @"Local\ScreenLeash.SingleInstance";
    private const string ActivateMessageName = "ScreenLeash.Activate";

    /// <summary>HWND_BROADCAST：向所有顶层窗口广播，用于唤醒已运行的实例。</summary>
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private static readonly object LogLock = new();

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName,
        "logs");

    /// <summary>由 RegisterWindowMessage("ScreenLeash.Activate") 返回的消息号；0 表示注册失败。</summary>
    internal static uint ActivateMessageId { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>注册全局热键（降级入口 Ctrl+Alt+V 用）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    /// <summary>注销全局热键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // 已有实例：广播激活消息后立刻退出。
            ActivateMessageId = RegisterWindowMessageW(ActivateMessageName);
            if (ActivateMessageId != 0)
            {
                _ = PostMessageW(HwndBroadcast, ActivateMessageId, IntPtr.Zero, IntPtr.Zero);
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Application.ThreadException += OnThreadException;

        Application.Run(new TrayApplicationContext());
    }

    /// <summary>托盘气泡提示（各处统一走这里，避免直接依赖具体托盘实例）。</summary>
    internal static void Notify(string title, string text) => TrayApplicationContext.ShowBalloon(title, text);

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.ExceptionObject as Exception, fatal: true);
        TrayApplicationContext.ShowBalloon("ScreenLeash 崩溃", "崩溃日志已写入 %LOCALAPPDATA%\\ScreenLeash\\logs");
    }

    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, fatal: false);
        TrayApplicationContext.ShowBalloon("ScreenLeash 出错", e.Exception.Message);
    }

    /// <summary>把异常写进 %LOCALAPPDATA%\ScreenLeash\logs\crash-&lt;yyyyMMdd&gt;.log，返回日志路径。</summary>
    internal static string WriteCrashLog(Exception? exception, bool fatal)
    {
        string path = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");

        try
        {
            Directory.CreateDirectory(LogDirectory);

            var builder = new StringBuilder();
            builder.Append('[')
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("] fatal=")
                .Append(fatal ? "yes" : "no")
                .AppendLine();
            builder.AppendLine(exception?.ToString() ?? "(no exception object)");
            builder.AppendLine(new string('-', 40));

            lock (LogLock)
            {
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception logFailure)
        {
            // 日志失败不得二次崩溃：只写调试通道。
            Debug.WriteLine($"ScreenLeash: 写入崩溃日志失败 - {logFailure}");
        }

        return path;
    }
}

/// <summary>托盘宿主：持有托盘图标、绳子运行时与接收广播激活消息的顶层窗口。</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _pasteHookItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly MessageWindow _messageWindow;
    private readonly LeashHost _host;
    private readonly int _uiThreadId;
    private readonly SynchronizationContext? _uiContext;

    internal static TrayApplicationContext? Current { get; private set; }

    internal TrayApplicationContext()
    {
        // 先把 UI 线程身份记下来：气泡提示可能从后台线程发起（剪贴板 STA 读取的续接、
        // 钩子线程转发的消息），NotifyIcon 不能在这些线程上直接碰。
        _uiThreadId = Environment.CurrentManagedThreadId;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        Current = this;

        AppPaths paths = AppPaths.Default;
        paths.EnsureCreated();
        AppSettings settings = AppSettings.Load(paths);

        _host = new LeashHost(paths, settings);

        _toggleItem = new ToolStripMenuItem("隐藏绳子", null, (_, _) => ToggleVisibility());

        // 勾选状态回显**真实钩子状态**（IsPasteHookInstalled），不是设置文件里的布尔值：
        // 启动时安装失败会把设置回写成 false，两边必须一致。
        _pasteHookItem = new ToolStripMenuItem("Ctrl+V 兜底", null, (_, _) => TogglePasteHook())
        {
            Checked = _host.IsPasteHookInstalled,
            CheckOnClick = false,
        };

        var pasteItem = new ToolStripMenuItem("粘贴挂上（剪贴板）", null, async (_, _) => await _host.PasteFromClipboardAsync());
        var openItem = new ToolStripMenuItem("打开数据目录", null, (_, _) => OpenDataFolder());
        _startupItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleStartup())
        {
            Checked = AppSettings.IsStartupRegistered(),
            CheckOnClick = false,
        };
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitThread());

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(_pasteHookItem);
        _menu.Items.Add(pasteItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Program.ProductName,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _trayIcon.DoubleClick += (_, _) => ToggleVisibility();

        _messageWindow = new MessageWindow(
            Program.ActivateMessageId,
            edge => _ = _host.PasteFromClipboardAsync(edge),
            () => _ = _host.PasteFromClipboardAsync());

        // 接收器句柄必须在钩子可能投递之前交出去；构造里已强制建过句柄。
        _host.AttachPasteReceiver(_messageWindow.Handle);

        UpdateTooltip();

        ReportPasteFallbackState();
    }

    internal static void ShowBalloon(string title, string text)
    {
        TrayApplicationContext? current = Current;
        if (current is null)
        {
            return;
        }

        // 非 UI 线程直接碰 NotifyIcon 会挂：切回构造线程再弹。
        if (current._uiThreadId != Environment.CurrentManagedThreadId)
        {
            current._uiContext?.Post(_ => ShowBalloon(title, text), null);
            return;
        }

        NotifyIcon icon = current._trayIcon;
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = text;
        icon.ShowBalloonTip(5000);
    }

    /// <summary>
    /// 启动后把「全局粘贴」的真实可用性告诉用户：钩子装不上就置灰并点名降级入口，
    /// 降级热键也被占用就再报一次。两件事都发生时不至于让用户以为 Ctrl+V 还能用。
    /// </summary>
    private void ReportPasteFallbackState()
    {
        if (_host.PasteHookInstallFailed)
        {
            _pasteHookItem.Enabled = false;
            ShowBalloon(
                "Ctrl+V 兜底不可用",
                "全局键盘钩子被系统拒绝或被占用；可用托盘「粘贴挂上（剪贴板）」或 Ctrl+Alt+V");
        }

        if (!_messageWindow.HotkeyRegistered)
        {
            ShowBalloon("快捷键不可用", "Ctrl+Alt+V 已被其它程序占用；可用托盘「粘贴挂上（剪贴板）」");
        }
    }

    private void TogglePasteHook()
    {
        bool enable = !_host.IsPasteHookInstalled;
        bool ok = _host.TrySetPasteHook(enable);
        _pasteHookItem.Checked = _host.IsPasteHookInstalled;

        if (!ok)
        {
            _pasteHookItem.Enabled = false;
            ShowBalloon(
                "Ctrl+V 兜底不可用",
                "全局键盘钩子被系统拒绝或被占用；可用托盘「粘贴挂上（剪贴板）」或 Ctrl+Alt+V");
        }
    }

    private void ToggleVisibility()
    {
        bool visible = _host.ToggleVisibility();
        _toggleItem.Text = visible ? "隐藏绳子" : "显示绳子";
        UpdateTooltip();
    }

    private void ToggleStartup()
    {
        bool enable = !_startupItem.Checked;
        bool ok = AppSettings.ApplyStartupRegistration(enable);
        if (!ok)
        {
            ShowBalloon("设置失败", "无法写入开机自启项（可能被安全软件拦截）");
            return;
        }

        _startupItem.Checked = enable;
        _host.Settings.StartWithWindows = enable;
        _host.Settings.Save(_host.Paths);
    }

    private void OpenDataFolder()
    {
        try
        {
            _host.Paths.EnsureCreated();
            Process.Start(new ProcessStartInfo
            {
                FileName = _host.Paths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
        {
            ShowBalloon("打开失败", ex.Message);
        }
    }

    private void UpdateTooltip()
    {
        int count = _host.Store.Count;
        var text = count > 0 ? $"{Program.ProductName}（{count} 项）" : Program.ProductName;

        // NotifyIcon.Text 上限 63 字符，超长会抛异常。
        _trayIcon.Text = text.Length > 60 ? text.Substring(0, 60) : text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _menu.Dispose();
            _messageWindow.Dispose();
            _host.Dispose();

            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 永不显示的顶层窗口：接收「重复启动」激活广播、钩子的 <c>WM_APP_PASTE</c> 粘贴请求与降级热键。
    ///
    /// <para><b>为什么接收器必须在这里而不是 LeashForm</b>：四边全关时程序里一个绳子窗口都没有，
    /// 但全局粘贴仍要能用（docs/PROBES.md P3 实测：此时进程无窗口，SynchronizationContext 为 NULL）。
    /// 本窗口由托盘持有，生命周期与托盘一致，与绳子窗口数量无关；钩子只 PostMessage 到它的句柄，
    /// 不依赖同步上下文。</para>
    /// </summary>
    private sealed class MessageWindow : Form
    {
        private readonly uint _activateMessage;
        private readonly Action<EdgeKind?> _onPasteRequest;
        private readonly Action _onPasteHotkey;
        private readonly bool _hotkeyRegistered;

        internal MessageWindow(uint activateMessage, Action<EdgeKind?> onPasteRequest, Action onPasteHotkey)
        {
            _activateMessage = activateMessage;
            _onPasteRequest = onPasteRequest ?? throw new ArgumentNullException(nameof(onPasteRequest));
            _onPasteHotkey = onPasteHotkey ?? throw new ArgumentNullException(nameof(onPasteHotkey));
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);

            // 强制建句柄：钩子线程随时可能 PostMessage，句柄必须在此之前就存在。
            _ = Handle;

            // 降级入口：钩子装不上时用户仍能一键粘贴。注册失败不致命（托盘菜单还在）。
            _hotkeyRegistered = Program.RegisterHotKey(
                Handle,
                InteropConstants.PasteHotkeyId,
                InteropConstants.ModControl | InteropConstants.ModAlt | InteropConstants.ModNoRepeat,
                InteropConstants.VkKeyV);
        }

        /// <summary>Ctrl+Alt+V 是否注册成功（被占用时托盘会提示降级到菜单）。</summary>
        internal bool HotkeyRegistered => _hotkeyRegistered;

        protected override bool ShowWithoutActivation => true;

        protected override void SetVisibleCore(bool value)
        {
            _ = value;
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (_activateMessage != 0 && (uint)m.Msg == _activateMessage)
            {
                ShowBalloon(Program.ProductName, "已在运行，重复启动已被忽略");
            }
            else if (m.Msg == InteropConstants.WmAppPaste)
            {
                // 钩子只负责投递与编码；解码后交给宿主，null 由宿主回退最近活跃边。
                _onPasteRequest(PasteRequestCodec.Decode(m.WParam));
            }
            else if (m.Msg == InteropConstants.WmHotkey && m.WParam.ToInt64() == InteropConstants.PasteHotkeyId)
            {
                _onPasteHotkey();
            }

            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _hotkeyRegistered)
            {
                _ = Program.UnregisterHotKey(Handle, InteropConstants.PasteHotkeyId);
            }

            base.Dispose(disposing);
        }
    }
}
