using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HookProbe;

/// <summary>
/// 两阶段探针。
///   阶段 A（默认 / --swallow）：钩子能否安装、能否捕获 Ctrl+V、返回 1 能否吞掉。
///   阶段 B（--e2e）：端到端链路——钩子装在后台 STA 线程 → 回调 PostMessage → 主线程隐藏窗口收到。
///   阶段 C（--scc）：在 Application.Run 之前，SynchronizationContext.Current 到底是什么。
/// 结果写 hook-probe.log，末行 PASS/FAIL。
/// </summary>
internal static class Probe
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_APP_PASTE = 0x8000 + 1;
    private const int VK_V = 0x56;
    private const int VK_CONTROL = 0x11;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const int KEYSTATE_DOWN = 0x8000;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private static IntPtr _hook;
    private static int _hookHits;
    private static int _windowHits;
    private static int _pasteRequests;
    private static bool _swallow;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "hook-probe.log");
    private static readonly LowLevelKeyboardProc Callback = HookCallback;

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                var vk = Marshal.ReadInt32(lParam);
                var ctrl = (GetAsyncKeyState(VK_CONTROL) & KEYSTATE_DOWN) != 0;
                if (vk == VK_V && ctrl)
                {
                    _hookHits++;
                    Log($"hook hit #{_hookHits} thread={Environment.CurrentManagedThreadId} swallow={_swallow}");

                    if (TargetWindow != IntPtr.Zero)
                    {
                        // 端到端：只投递消息，绝不在回调里解析剪贴板。
                        var posted = PostMessageW(TargetWindow, WM_APP_PASTE, IntPtr.Zero, IntPtr.Zero);
                        Log($"post to 0x{TargetWindow.ToInt64():X} ok={posted}");
                        return new IntPtr(1);
                    }

                    if (_swallow)
                    {
                        return new IntPtr(1);
                    }
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static IntPtr TargetWindow = IntPtr.Zero;

    [STAThread]
    private static void Main(string[] args)
    {
        try { File.Delete(LogPath); } catch (IOException) { }

        _swallow = args.Contains("--swallow");

        if (args.Contains("--scc"))
        {
            RunSyncContextProbe();
            return;
        }

        if (args.Contains("--e2e"))
        {
            RunE2E();
            return;
        }

        RunBasic();
    }

    /// <summary>阶段 C：Application.Run 之前 SynchronizationContext.Current 是什么。</summary>
    private static void RunSyncContextProbe()
    {
        Application.EnableVisualStyles();
        Log($"scc: before any control = {Describe(SynchronizationContext.Current)}");

        var f = new Form { Text = "scc", Width = 200, Height = 100, ShowInTaskbar = false };
        Log($"scc: after new Form = {Describe(SynchronizationContext.Current)}");

        _ = f.Handle;
        Log($"scc: after Handle created = {Describe(SynchronizationContext.Current)}");

        f.Show();
        Log($"scc: after Show = {Describe(SynchronizationContext.Current)}");

        f.Close();
        f.Dispose();

        // 不建任何窗时（四边全关场景）
        var g = new Form { Text = "scc2", Width = 200, Height = 100, ShowInTaskbar = false };
        Log($"scc: (no-show path) current = {Describe(SynchronizationContext.Current)}");
        g.Dispose();

        Log("SCC_DONE");

        static string Describe(SynchronizationContext? c) =>
            c is null ? "NULL" : c.GetType().FullName ?? "unknown";
    }

    /// <summary>阶段 B：钩子装在后台 STA 线程 → PostMessage → 主线程窗口收到。</summary>
    private static void RunE2E()
    {
        Application.EnableVisualStyles();

        var window = new PasteSinkWindow();
        TargetWindow = window.Handle;
        Log($"e2e: sink hwnd=0x{window.Handle.ToInt64():X}");

        var ready = new ManualResetEventSlim(false);
        var hookThread = new Thread(() =>
        {
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, Callback, GetModuleHandleW(null), 0);
            Log($"e2e: hook installed={_hook != IntPtr.Zero} lastError={Marshal.GetLastWin32Error()} thread={Environment.CurrentManagedThreadId}");
            ready.Set();

            // 低层钩子需要消息泵才能接收回调。
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        })
        {
            IsBackground = true,
            Name = "hook-thread",
        };
        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();

        if (!ready.Wait(2000))
        {
            Log("e2e: hook thread did not signal in 2s");
        }

        var timer = new System.Windows.Forms.Timer { Interval = 900 };
        var step = 0;
        timer.Tick += (_, _) =>
        {
            step++;
            switch (step)
            {
                case 1:
                    Log($"e2e: main thread={Environment.CurrentManagedThreadId}, sending Ctrl+V");
                    SendCtrlV();
                    break;
                case 2:
                    SendCtrlV();
                    break;
                case 3:
                    timer.Stop();
                    var ok = _hookHits > 0 && _pasteRequests > 0;
                    Log($"e2e: RESULT hookHits={_hookHits} pasteRequests={_pasteRequests} windowKeyDowns={_windowHits}");
                    Log(ok ? "E2E_PASS" : "E2E_FAIL");
                    if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                    Application.Exit();
                    break;
            }
        };
        timer.Start();
        Application.Run();
    }

    /// <summary>阶段 A：原对照实验。</summary>
    private static void RunBasic()
    {
        Application.EnableVisualStyles();

        var form = new Form
        {
            Text = "HookProbe",
            Width = 420,
            Height = 160,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
        };

        form.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                _windowHits++;
                Log($"window KeyDown V #{_windowHits}");
            }
        };

        form.Shown += (_, _) =>
        {
            form.Activate();
            form.Focus();

            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, Callback, GetModuleHandleW(null), 0);
            Log($"hook installed={_hook != IntPtr.Zero} lastError={Marshal.GetLastWin32Error()}");
            if (_hook == IntPtr.Zero)
            {
                Log("PROBE_FAIL (hook not installed)");
                form.Close();
                return;
            }

            var timer = new System.Windows.Forms.Timer { Interval = 900 };
            var step = 0;
            timer.Tick += (_, _) =>
            {
                step++;
                if (step == 1)
                {
                    form.Activate();
                    form.Focus();
                    Log("sending synthetic Ctrl+V");
                    SendCtrlV();
                }
                else if (step == 2)
                {
                    Log("sending synthetic Ctrl+V (2nd)");
                    SendCtrlV();
                }
                else if (step == 3)
                {
                    timer.Stop();
                    var ok = _hookHits > 0 && (!_swallow || _windowHits == 0);
                    Log($"RESULT hookHits={_hookHits} windowHits={_windowHits} swallow={_swallow}");
                    Log(ok ? "PROBE_PASS" : "PROBE_FAIL");
                    form.Close();
                }
            };
            timer.Start();
        };

        Application.Run(form);

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
        }
    }

    private static void SendCtrlV()
    {
        keybd_event((byte)VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event((byte)VK_V, 0, 0, UIntPtr.Zero);
        keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>主线程侧的接收窗口：证明 PostMessage 真的送到了 UI 线程。</summary>
    private sealed class PasteSinkWindow : NativeWindow
    {
        public PasteSinkWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "paste-sink",
                Style = 0,
                ExStyle = 0,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APP_PASTE)
            {
                _pasteRequests++;
                Log($"sink: paste request #{_pasteRequests} on thread={Environment.CurrentManagedThreadId}");
                return;
            }

            base.WndProc(ref m);
        }
    }
}
