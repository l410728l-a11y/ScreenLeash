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
    private readonly ToolStripMenuItem _startupItem;
    private readonly MessageWindow _messageWindow;
    private readonly LeashHost _host;

    internal static TrayApplicationContext? Current { get; private set; }

    internal TrayApplicationContext()
    {
        Current = this;

        AppPaths paths = AppPaths.Default;
        paths.EnsureCreated();
        AppSettings settings = AppSettings.Load(paths);

        _host = new LeashHost(paths, settings);

        _toggleItem = new ToolStripMenuItem("隐藏绳子", null, (_, _) => ToggleVisibility());
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

        _messageWindow = new MessageWindow(Program.ActivateMessageId);
        _ = _messageWindow.Handle;

        UpdateTooltip();
    }

    internal static void ShowBalloon(string title, string text)
    {
        TrayApplicationContext? current = Current;
        if (current is null)
        {
            return;
        }

        NotifyIcon icon = current._trayIcon;
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = text;
        icon.ShowBalloonTip(5000);
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

    /// <summary>永不显示的顶层窗口：只负责在 WndProc 里接收激活广播。</summary>
    private sealed class MessageWindow : Form
    {
        private readonly uint _activateMessage;

        internal MessageWindow(uint activateMessage)
        {
            _activateMessage = activateMessage;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
        }

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

            base.WndProc(ref m);
        }
    }
}
