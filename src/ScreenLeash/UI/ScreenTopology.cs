using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using ScreenLeash.Core;

namespace ScreenLeash.UI;

/// <summary>一个屏幕的快照。抽出为值类型是为了让拓扑计算成为可测的纯函数。</summary>
/// <param name="DeviceName">屏幕设备名（重建后用于对位，不保证稳定）。</param>
/// <param name="Bounds">屏幕整块矩形（px）。</param>
/// <param name="WorkingArea">工作区矩形（px，已排除任务栏）。</param>
/// <param name="Dpi">该屏幕自身的 DPI。</param>
/// <param name="Primary">是否主屏。</param>
public readonly record struct ScreenInfo(string DeviceName, Rectangle Bounds, Rectangle WorkingArea, int Dpi, bool Primary);

/// <summary>一条绳子 = 一个屏幕的一条边。</summary>
public readonly record struct EdgeSlot(ScreenInfo Screen, EdgeKind Edge)
{
    /// <summary>该绳子的窗口矩形，唯一出口仍是 <see cref="EdgeGeometry.Compute"/>。</summary>
    public Rectangle Bounds(LeashVisualState state) =>
        EdgeGeometry.Compute(Edge, Screen.WorkingArea, Screen.Dpi, state);
}

/// <summary>
/// 屏幕拓扑：把「哪几块屏 × 四条边」解析成 <see cref="EdgeSlot"/> 列表。
/// 每屏用自己的 DeviceDpi；显示器参数变化（DisplaySettingsChanged）后重建；
/// WM_DPICHANGED 由窗口侧调用 <see cref="Rebuild"/> 后取新 DPI 重算（本类不依赖窗口消息）。
/// 拓扑列表本身不可变，可在任意线程读取；<see cref="Rebuilt"/> 可能来自非 UI 线程，
/// 订阅方负责切回 UI 线程（本类在构造线程有 SynchronizationContext 时会自动 Post）。
/// </summary>
public sealed class ScreenTopology : IDisposable
{
    private readonly Func<IReadOnlyList<ScreenInfo>> _probe;
    private readonly SynchronizationContext? _sync;
    private readonly object _gate = new();
    private IReadOnlyList<EdgeSlot> _slots;
    private bool _disposed;

    /// <summary>用真实 WinForms 屏幕信息构造。</summary>
    public ScreenTopology(bool onlyPrimaryScreen)
        : this(onlyPrimaryScreen, ProbeScreens, SynchronizationContext.Current)
    {
    }

    /// <summary>注入屏幕来源（测试用）。</summary>
    public ScreenTopology(bool onlyPrimaryScreen, Func<IReadOnlyList<ScreenInfo>> probe)
        : this(onlyPrimaryScreen, probe, SynchronizationContext.Current)
    {
    }

    /// <summary>注入屏幕来源与同步上下文（测试用）。</summary>
    public ScreenTopology(bool onlyPrimaryScreen, Func<IReadOnlyList<ScreenInfo>> probe, SynchronizationContext? sync)
    {
        OnlyPrimaryScreen = onlyPrimaryScreen;
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _sync = sync;
        _slots = BuildSlots(_probe(), onlyPrimaryScreen);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>拓扑重建完成（显示器增删、分辨率/DPI/任务栏变化）。</summary>
    public event EventHandler? Rebuilt;

    /// <summary>是否只保留主屏的四条绳子。</summary>
    public bool OnlyPrimaryScreen { get; }

    /// <summary>当前拓扑快照（不可变，任意线程可读）。</summary>
    public IReadOnlyList<EdgeSlot> Slots
    {
        get
        {
            lock (_gate)
            {
                return _slots;
            }
        }
    }

    /// <summary>重新探测屏幕并重建拓扑；DPI 变化后同样走这里。</summary>
    public void Rebuild()
    {
        if (_disposed)
        {
            return;
        }

        var slots = BuildSlots(_probe(), OnlyPrimaryScreen);
        lock (_gate)
        {
            _slots = slots;
        }

        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>纯函数：屏幕列表 × 四条边 → 绳子拓扑。</summary>
    public static IReadOnlyList<EdgeSlot> BuildSlots(IReadOnlyList<ScreenInfo> screens, bool onlyPrimaryScreen)
    {
        if (screens is null)
        {
            throw new ArgumentNullException(nameof(screens));
        }

        var selected = onlyPrimaryScreen
            ? screens.Where(s => s.Primary).Take(1).ToArray()
            : screens.ToArray();

        if (selected.Length == 0 && screens.Count > 0)
        {
            // 主屏标记缺失时退化为第一块屏，避免拓扑为空导致四条绳子全部消失。
            selected = new[] { screens[0] };
        }

        var edges = Enum.GetValues<EdgeKind>();
        var slots = new List<EdgeSlot>(selected.Length * edges.Length);
        foreach (var screen in selected)
        {
            foreach (EdgeKind edge in edges)
            {
                slots.Add(new EdgeSlot(screen, edge));
            }
        }

        return slots;
    }

    /// <summary>按屏幕矩形包含关系解析点所在屏幕；无命中返回 <c>null</c>。</summary>
    public static ScreenInfo? ScreenFromPoint(IReadOnlyList<ScreenInfo> screens, Point point)
    {
        if (screens is null)
        {
            throw new ArgumentNullException(nameof(screens));
        }

        foreach (var screen in screens)
        {
            if (screen.Bounds.Contains(point))
            {
                return screen;
            }
        }

        return null;
    }

    /// <summary>读取真实屏幕快照（WinForms <see cref="Screen"/> 包装，每屏用自身 DPI）。</summary>
    public static IReadOnlyList<ScreenInfo> ProbeScreens()
    {
        var screens = Screen.AllScreens;
        var result = new List<ScreenInfo>(screens.Length);
        foreach (var screen in screens)
        {
            result.Add(ToInfo(screen));
        }

        return result;
    }

    /// <summary>当前光标所在屏幕（WinForms 包装）。</summary>
    public static ScreenInfo CursorScreen() => ToInfo(Screen.FromPoint(Cursor.Position));

    private static ScreenInfo ToInfo(Screen screen)
    {
        int dpi = MonitorDpi.ForPoint(CenterOf(screen.Bounds));
        if (dpi <= 0)
        {
            // 极端情况（远程会话未就绪 / 老系统无 shcore）：退回系统 DPI，不让拓扑整体失败。
            dpi = Metrics.DpiBaseline;
        }

        return new ScreenInfo(screen.DeviceName, screen.Bounds, screen.WorkingArea, dpi, screen.Primary);
    }

    private static Point CenterOf(Rectangle bounds) =>
        new(bounds.Left + (bounds.Width >> 1), bounds.Top + (bounds.Height >> 1));

    /// <summary>
    /// 每显示器 DPI 查询。WinForms 的 <see cref="Screen"/> 不暴露 DPI，只能走 Win32：
    /// MonitorFromPoint 取 HMONITOR → GetDpiForMonitor(MDT_EFFECTIVE_DPI)。
    /// 需要进程为 PerMonitorV2 感知（由 bootstrap 任务在 csproj/manifest 设置）；
    /// 否则返回的是被 DPI 虚拟化后的系统值，属可接受的降级。
    /// </summary>
    private static class MonitorDpi
    {
        private const uint MonitorDefaultToNearest = 0x2;
        private const int MdtEffectiveDpi = 0x0;
        private const int S_OK = 0x0;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        internal static int ForPoint(Point point)
        {
            var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) != S_OK)
                {
                    return 0;
                }

                return dpiX > 0 ? (int)dpiX : 0;
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
            catch (EntryPointNotFoundException)
            {
                return 0;
            }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_sync is null || _sync == SynchronizationContext.Current)
        {
            Rebuild();
            return;
        }

        _sync.Post(_ => Rebuild(), null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
