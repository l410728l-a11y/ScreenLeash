using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CostProbe;

/// <summary>
/// 测「钩子回调里做一次完整焦点判定」的真实耗时，用来裁决「零分配 vs 零阻塞」之争。
/// 回调内执行与 FocusProbe.ShouldTakeOverPaste 等价的工作：
///   GetForegroundWindow → GetWindowThreadProcessId → GetGUIThreadInfo → GetClassNameW（含一次 StringBuilder 分配）。
/// 模拟 60 次 Ctrl+V，统计 min / avg / p99 / max（微秒）。
/// </summary>
internal static class Probe
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_V = 0x56;
    private const int VK_CONTROL = 0x11;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const int KEYSTATE_DOWN = 0x8000;
    private const int CLASSNAME_BUFFER = 256;
    private const int SAMPLE_TARGET = 60;
    private const int BURST_PER_TICK = 10;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
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
        public int rcLeft;
        public int rcTop;
        public int rcRight;
        public int rcBottom;
    }

    private static IntPtr _hook;
    private static readonly List<double> Samples = new();
    private static readonly LowLevelKeyboardProc Callback = HookCallback;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "cost-probe.log");

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message == WM_KEYDOWN)
            {
                var vk = Marshal.ReadInt32(lParam);
                var ctrl = (GetAsyncKeyState(VK_CONTROL) & KEYSTATE_DOWN) != 0;
                if (vk == VK_V && ctrl)
                {
                    var sw = Stopwatch.StartNew();
                    var noTarget = DoFullFocusProbe();
                    sw.Stop();
                    Samples.Add(sw.Elapsed.TotalMicroseconds);
                    if (Samples.Count <= 3)
                    {
                        Log($"sample #{Samples.Count}: {sw.Elapsed.TotalMicroseconds:F1} us (noPasteTarget={noTarget})");
                    }

                    return CallNextHookEx(_hook, nCode, wParam, lParam);
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>与 FocusProbe.ShouldTakeOverPaste 等价的一次完整判定。</summary>
    private static bool DoFullFocusProbe()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return true;
        }

        var threadId = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        if (threadId == 0)
        {
            return false;
        }

        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero)
        {
            return true;
        }

        var buffer = new StringBuilder(CLASSNAME_BUFFER);
        var len = GetClassNameW(info.hwndFocus, buffer, buffer.Capacity);
        if (len <= 0)
        {
            return false;
        }

        var name = buffer.ToString();
        return name.Equals("Progman", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase);
    }

    [STAThread]
    private static void Main()
    {
        try { File.Delete(LogPath); } catch (IOException) { }

        Application.EnableVisualStyles();
        var form = new Form { Text = "CostProbe", Width = 300, Height = 120, TopMost = true };

        form.Shown += (_, _) =>
        {
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, Callback, GetModuleHandleW(null), 0);
            Log($"hook installed={_hook != IntPtr.Zero} lastError={Marshal.GetLastWin32Error()}");
            if (_hook == IntPtr.Zero)
            {
                Log("COST_FAIL");
                form.Close();
                return;
            }

            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            var ticks = 0;
            timer.Tick += (_, _) =>
            {
                ticks++;
                if (Samples.Count < SAMPLE_TARGET)
                {
                    for (var i = 0; i < BURST_PER_TICK; i++)
                    {
                        SendCtrlV();
                    }

                    return;
                }

                timer.Stop();
                Report();
                form.Close();
            };
            timer.Start();

            // 兜底：最多 20 秒。
            var guard = new System.Windows.Forms.Timer { Interval = 20000 };
            guard.Tick += (_, _) =>
            {
                guard.Stop();
                Report();
                form.Close();
            };
            guard.Start();
        };

        Application.Run(form);
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
        }
    }

    private static void Report()
    {
        if (Samples.Count == 0)
        {
            Log("COST_FAIL (no samples; synthetic keys may not reach the hook)");
            return;
        }

        Samples.Sort();
        double sum = 0;
        foreach (var s in Samples)
        {
            sum += s;
        }

        var avg = sum / Samples.Count;
        var p99 = Samples[(int)Math.Min(Samples.Count - 1, Math.Floor(Samples.Count * 0.99))];
        Log($"count={Samples.Count} min={Samples[0]:F1}us avg={avg:F1}us p99={p99:F1}us max={Samples[^1]:F1}us");
        Log(p99 < 1000 ? "COST_PASS" : "COST_WARN");
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
}
