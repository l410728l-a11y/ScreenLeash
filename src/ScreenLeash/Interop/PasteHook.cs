using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenLeash.Core;

namespace ScreenLeash.Interop;

/// <summary>
/// 全局 Ctrl+V 钩子（WH_KEYBOARD_LL）：前台没有可粘贴目标时，把「粘贴到绳子」的请求
/// 投递给 UI 线程的接收窗口，并**吞掉**这次按键，避免目标程序也粘一次。
///
/// <para><b>快照与命中</b>：命中判定读 <see cref="LeashSnapshotSlot"/> 里的不可变快照
/// （<see cref="LeashSnapshot"/> + <see cref="LeashTarget"/>，定义在
/// <c>Core/PasteTargetPolicy.cs</c>），命中即得到目标边，**不依赖 tick 粒度的
/// <c>LeashHost.ActiveEdge</c>**（它由 120ms 的宿主 tick 刷新，随时可能落后）。
/// 引用赋值原子 + <see cref="Volatile"/> 栅栏 ⇒ 读方不可能看到「半个 Rectangle」。</para>
///
/// <para><b>发布时机</b>：本类只负责「存」和「读」快照，发布由持有窗口的一方在
/// 展开态变化 / 窗口重建 / 窗口位置或 DPI 变化时**立即**调用 <see cref="Publish"/>
/// ——这些是事件驱动，不是 tick 驱动。卸载时 <see cref="Stop"/> 会发布
/// <see cref="LeashSnapshot.Empty"/>。</para>
///
/// <para><b>线程模型</b>：钩子装在专用后台 STA 线程上并跑自己的消息泵（低层钩子必须在带
/// 消息泵的线程上安装；P2 实测：hook thread=4 → PostMessage → sink thread=1）。
/// 回调只做三件事：判 Ctrl+V、<see cref="FocusProbe.ShouldTakeOverPaste"/>、PostMessage。
/// 回调内**禁止** Clipboard/File/跨进程 API、lock、无界循环——P4 实测回调耗时稳态
/// 9–32μs、avg=54.2μs、p99=913.2μs，远低于 LowLevelHooksTimeout（秒级），
/// 性能口径是「零阻塞」而非「零分配」。</para>
///
/// <para><b>消息接收方</b>：本类不创建任何窗口。接收 <see cref="InteropConstants.WmAppPaste"/>
/// 的窗口由 UI 层持有，且必须独立于四边窗口而存在——P3 实测四边全关时进程里没有任何窗口、
/// SynchronizationContext 为 NULL，所以接收器不能挂在 LeashForm 上。</para>
/// </summary>
public sealed class PasteHook : IDisposable
{
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(out Msg msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(ref Point32 point);

    /// <summary>
    /// 静态委托：既供 SetWindowsHookEx 安装，也作为强引用防止 GC 回收——
    /// 低层钩子的委托一旦被回收，下一次按键回调就会让进程崩溃。
    /// </summary>
    private static readonly LowLevelKeyboardProc HookCallbackDelegate = HookCallback;

    /// <summary>当前活动的钩子实例（回调是静态的，靠它找到快照与接收窗口）。</summary>
    private static PasteHook? _current;

    /// <summary>进程级单钩子闸门：同一进程重复 Start 会返回 false，而不是装两个钩子收两份回调。</summary>
    private static int _activeHook;

    private readonly LeashSnapshotSlot _slot = new();

    /// <summary>接收窗口句柄（以 long 存取，保证跨线程读写原子）。</summary>
    private long _sink;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>当前快照（永不为 null）。</summary>
    public LeashSnapshot Current => _slot.Current;

    /// <summary>钩子是否已装上。</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>钩子线程是否在跑。</summary>
    public bool IsRunning => _thread is not null;

    /// <summary>
    /// 指定接收 <see cref="InteropConstants.WmAppPaste"/> 的窗口句柄。
    /// 该窗口必须由 UI 线程持有并活在钩子整个生命周期内（不能是 LeashForm——四边全关时它不存在）。
    /// 传 <see cref="IntPtr.Zero"/> 表示暂不接收：此时回调**不吞**按键，用户的粘贴照常落到目标程序。
    /// </summary>
    public void SetSink(IntPtr hwnd) => Volatile.Write(ref _sink, hwnd.ToInt64());

    /// <summary>发布新快照（引用赋值原子；null 等价于空快照）。</summary>
    public void Publish(LeashSnapshot? snapshot) => _slot.Publish(snapshot);

    /// <summary>发布空快照：四边全关 / 卸钩子时调用。</summary>
    public void PublishEmpty() => _slot.Publish(LeashSnapshot.Empty);

    /// <summary>
    /// 起钩子线程并安装 WH_KEYBOARD_LL。返回 false 表示没装上（已被别的实例占用、系统拒绝、
    /// 或钩子线程在超时内没就绪）。装不上不致命：托盘菜单的「粘贴挂上」仍可用。
    /// </summary>
    public bool Start()
    {
        if (_disposed)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _activeHook, 1, 0) != 0)
        {
            return false;
        }

        Volatile.Write(ref _current, this);

        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => HookThreadMain(ready))
        {
            IsBackground = true,
            Name = "screenleash-paste-hook",
        };
        thread.SetApartmentState(ApartmentState.STA);
        _thread = thread;
        thread.Start();

        bool signaled = ready.Wait(InteropConstants.HookReadyTimeoutMs);
        ready.Dispose();

        if (!signaled || _hook == IntPtr.Zero)
        {
            Stop();
            return false;
        }

        return true;
    }

    /// <summary>
    /// 卸钩子：PostThreadMessage(WM_QUIT) 让钩子线程的消息泵退出 → 线程内 Unhook → Join。
    /// 可重复调用；调用后快照为空，因此即便回调还在跑也只会放行按键。
    /// </summary>
    public void Stop()
    {
        uint threadId = _threadId;
        var thread = _thread;
        _thread = null;

        if (threadId != 0)
        {
            _ = PostThreadMessageW(threadId, InteropConstants.WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            // 超时也继续往下走：线程是后台线程，进程退出时由 OS 回收；
            // 快照已清空，残留回调只会看到空目标并放行按键。
            _ = thread.Join(InteropConstants.HookShutdownTimeoutMs);
        }

        _threadId = 0;
        _hook = IntPtr.Zero;
        Volatile.Write(ref _current, null);
        Interlocked.Exchange(ref _activeHook, 0);
        PublishEmpty();
    }

    /// <summary>卸钩子并清空状态。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SetSink(IntPtr.Zero);
        Stop();
    }

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        var hook = IntPtr.Zero;
        try
        {
            _threadId = GetCurrentThreadId();

            // 模块句柄为 NULL 表示「当前进程」——托管可执行文件不需要显式 hMod。
            hook = SetWindowsHookExW(InteropConstants.WhKeyboardLl, HookCallbackDelegate, GetModuleHandleW(null), 0);
            _hook = hook;
            ready.Set();

            if (hook == IntPtr.Zero)
            {
                return;
            }

            // 低层钩子靠本线程的消息泵接收回调（P2 实测）。
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref msg);
                _ = DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex) when (ex is ExternalException || ex is DllNotFoundException || ex is EntryPointNotFoundException)
        {
            // 装不上就安静降级：全局粘贴还有托盘菜单这条路径。
        }
        finally
        {
            _hook = IntPtr.Zero;
            if (hook != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(hook);
            }

            ready.Set();
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            if (message == InteropConstants.WmKeyDown || message == InteropConstants.WmSysKeyDown)
            {
                // KBDLLHOOKSTRUCT 的第一个字段就是 vkCode。
                int virtualKey = Marshal.ReadInt32(lParam);
                if (IsPasteChord(
                        virtualKey,
                        IsDown(InteropConstants.VkControl),
                        IsDown(InteropConstants.VkMenu),
                        IsDown(InteropConstants.VkShift)))
                {
                    var current = Volatile.Read(ref _current);
                    if (current is not null && current.TryPostPasteRequest())
                    {
                        // 已由绳子接管：吞掉这次 Ctrl+V，避免目标程序再粘一遍。
                        return new IntPtr(1);
                    }
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// 纯判定（可单测）：这次按键是不是「裸 Ctrl+V」。
    ///
    /// <para>带 Alt 或 Shift 的组合键必须放行：<b>Ctrl+Alt+V 是本程序注册的降级热键</b>
    /// （RegisterHotKey 不会阻止低级钩子先看到按键，钩子若吞键则热键永远收不到），
    /// <b>Ctrl+Shift+V</b> 是各应用的「粘贴为纯文本」。钩子在这里多吞一个修饰键，
    /// 用户就会得到一个「按了没反应」的快捷键。</para>
    /// </summary>
    /// <param name="vkCode">虚拟键码。</param>
    /// <param name="ctrlDown">Ctrl 是否按下。</param>
    /// <param name="altDown">Alt 是否按下。</param>
    /// <param name="shiftDown">Shift 是否按下。</param>
    public static bool IsPasteChord(int vkCode, bool ctrlDown, bool altDown, bool shiftDown) =>
        vkCode == InteropConstants.VkKeyV && ctrlDown && !altDown && !shiftDown;

    private static bool IsDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & InteropConstants.KeyStatePressed) != 0;

    /// <summary>
    /// 回调内的全部业务：<b>先判光标是否落在已展开的绳子上</b>（命中即无条件接管），
    /// 未命中时再看前台是否明确没有可粘贴目标 → PostMessage 投递。
    /// 只有「真的投递成功」才返回 true（才允许吞键）；任何一步失败都放行给系统。
    ///
    /// <para><b>顺序不能反</b>：绳子窗口是 WS_EX_NOACTIVATE，前台始终是被的程序。
    /// 若先判 <see cref="FocusProbe.ShouldTakeOverPaste"/>，只要前台是浏览器/记事本/微信这类
    /// 有粘贴目标的应用就会直接放行，永远走不到命中判定——用户「鼠标放在绳子上按 Ctrl+V
    /// 却粘不上去」正是这个顺序造成的。</para>
    /// </summary>
    private bool TryPostPasteRequest()
    {
        long sink = Volatile.Read(ref _sink);
        if (sink == 0)
        {
            return false;
        }

        var cursor = default(Point32);
        if (!GetCursorPos(ref cursor))
        {
            return false;
        }

        var point = new Point(cursor.X, cursor.Y);

        // ① 命中已展开的绳子：无条件接管，不看前台是谁。
        //    null = 未命中任何展开态绳子，接收方回退到 LeashHost.ActiveEdge。
        var edge = _slot.Current.HitTest(point);

        // ② 未命中绳子时，只有前台确实无处可粘才接管。
        if (!PasteTargetPolicy.ShouldCapture(edge, edge is null && FocusProbe.ShouldTakeOverPaste()))
        {
            return false;
        }

        return PostMessageW(
            new IntPtr(sink),
            InteropConstants.WmAppPaste,
            PasteRequestCodec.Encode(edge),
            IntPtr.Zero);
    }
}
