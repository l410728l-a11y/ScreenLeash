using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenLeash.Core;
using ScreenLeash.Interop;

namespace ScreenLeash.UI;

/// <summary>
/// 绳子宿主：持有数据层与托盘之外的运行时状态，负责
/// 「有几条绳（屏幕 × 边）→ 建几个窗口 → 用一个定时器统一驱动」。
///
/// <para><b>为什么只有一个定时器</b>：每条绳各起一个 150ms Timer 会随屏数线性增长；
/// 实测单个 Timer 在 24 核下 CPU 0.024%，但没必要乘上窗口数。宿主每 tick 读一次光标、
/// 广播给全部窗口，判定与动画都在同一帧内完成，也避免了多条绳对同一次移动「各看各的」。</para>
///
/// <para>窗口不抢焦点（WS_EX_NOACTIVATE），所以宿主是唯一知道「用户刚在往哪条边靠」的地方，
/// 全局粘贴兜底由它记录最近活跃边。</para>
/// </summary>
public sealed class LeashHost : IDisposable
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly ItemStore _store;
    private readonly Thumbnailer _thumbnails;
    private readonly ScreenTopology _topology;
    private readonly List<LeashForm> _forms = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly PasteHook _pasteHook = new();

    private EdgeKind _activeEdge = EdgeKind.Right;
    private bool _visible = true;
    private bool _pasteHookInstallFailed;
    private bool _disposed;

    [DllImport("user32.dll", SetLastError = false)]
    private static extern short GetAsyncKeyState(int virtualKey);

    public LeashHost(AppPaths? paths = null, AppSettings? settings = null)
    {
        _paths = paths ?? AppPaths.Default;
        _paths.EnsureCreated();
        _settings = settings ?? AppSettings.Load(_paths);
        _settings.Normalize();

        // 启动即清理上次异常退出留下的拖出临时文件（只删本程序前缀）。
        TempFileManager.CleanupStale();

        _thumbnails = new Thumbnailer(CursorDpi);
        _store = new ItemStore(_paths, _thumbnails);
        _store.Changed += OnStoreChanged;
        _store.ItemsEvicted += OnItemsEvicted;

        _topology = new ScreenTopology(_settings.OnlyPrimaryScreen);
        _topology.Rebuilt += OnTopologyRebuilt;

        RebuildForms();

        _timer = new System.Windows.Forms.Timer { Interval = Metrics.CursorProbeIntervalMs };
        _timer.Tick += OnTick;
        _timer.Start();

        // 孤儿清理（blobs/thumbs 中无引用文件）放到后台，别拖慢启动。
        _ = InitializeStoreAsync();

        // 全局粘贴钩子的**唯一**启动点：PasteHookEnabled 在整个程序里只在这里被读一次。
        // 不在这里装，用户勾了「Ctrl+V 兜底」也永远不会生效（设置字段本身零读取点）。
        // 装不上（安全软件拦截 / 会话策略禁止）就回写 false："设置说我开了、其实没开"
        // 比明确关着更糟。提示放在托盘初始化之后（此刻托盘图标还没建，没地方弹气泡）。
        if (_settings.PasteHookEnabled && !_pasteHook.Start())
        {
            _settings.PasteHookEnabled = false;
            _settings.Save(_paths);
            _pasteHookInstallFailed = true;
        }
    }

    /// <summary>当前设置的只读视图（设置界面回显用）。</summary>
    public AppSettings Settings => _settings;

    /// <summary>卡片存储（设置界面统计占用用）。</summary>
    public ItemStore Store => _store;

    /// <summary>数据目录（托盘「打开数据目录」用）。</summary>
    public AppPaths Paths => _paths;

    /// <summary>最近一次悬停/拖入的边——全局粘贴兜底默认挂到这里。</summary>
    public EdgeKind ActiveEdge => _activeEdge;

    /// <summary>当前绳子窗口数量（0 表示四条边全被关掉）。</summary>
    public int WindowCount => _forms.Count;

    /// <summary>钩子当前是否真的挂在系统上（托盘勾选回显真实状态，而不是设置文件里写的那一个）。</summary>
    public bool IsPasteHookInstalled => _pasteHook.IsInstalled;

    /// <summary>启动时请求开钩子但没装上：托盘据此把菜单置灰并提示降级入口。</summary>
    public bool PasteHookInstallFailed => _pasteHookInstallFailed;

    /// <summary>显示全部绳子。</summary>
    public void ShowAll()
    {
        _visible = true;
        foreach (var form in _forms)
        {
            if (!form.Visible)
            {
                form.Show();
            }
        }

        _timer.Start();
        PublishSnapshot();
    }

    /// <summary>隐藏全部绳子（保留托盘与定时器停止，几乎不耗 CPU）。</summary>
    public void HideAll()
    {
        _visible = false;
        _timer.Stop();
        foreach (var form in _forms)
        {
            form.Hide();
        }

        // 看不见的绳子不接粘贴：快照必须随之收窄到空。
        PublishSnapshot();
    }

    /// <summary>切换显隐，返回切换后是否可见。</summary>
    public bool ToggleVisibility()
    {
        if (_visible)
        {
            HideAll();
        }
        else
        {
            ShowAll();
        }

        return _visible;
    }

    /// <summary>
    /// 把剪贴板内容挂到绳子（托盘菜单 / Ctrl+Alt+V / WM_APP_PASTE 三条路径共用）。
    /// 剪贴板读取在后台 STA 线程完成并带超时，UI 线程只 await——绝不因为
    /// 别的进程占着剪贴板把绳子和托盘卡死。
    /// </summary>
    /// <param name="edge">
    /// 钩子（或热键）判定的落点；<c>null</c> 表示未命中任何展开态绳子，回退最近活跃边。
    /// </param>
    /// <returns>挂上的数量；0 表示剪贴板读不到或没有可挂内容。</returns>
    public async Task<int> PasteFromClipboardAsync(EdgeKind? edge = null)
    {
        var read = await ClipboardParser
            .ReadClipboardStaAsync(TimeSpan.FromMilliseconds(InteropConstants.ClipboardReadTimeoutMs))
            .ConfigureAwait(true);

        switch (read.Outcome)
        {
            case ClipboardReadOutcome.TimedOut:
                Program.Notify("剪贴板被占用", "没能读到剪贴板，稍后再试");
                return 0;
            case ClipboardReadOutcome.Faulted:
                Program.Notify("剪贴板读取失败", "换一份内容或稍后再试");
                return 0;
        }

        var parsed = read.Content;
        if (!parsed.HasContent)
        {
            Program.Notify("剪贴板没有可挂的内容", "复制一张图、一段文字或一个文件后再试");
            return 0;
        }

        var target = edge ?? (_forms.Count > 0 ? _activeEdge : EdgeKind.Right);
        var result = await DragIn.IngestParsedAsync(parsed, _store, _settings, target).ConfigureAwait(true);
        if (result.AnyAdded)
        {
            Program.Notify("已挂到绳子", result.Added + " 项");
        }
        else if (result.Skipped > 0)
        {
            Program.Notify("没有挂上", result.LastError ?? "内容不受支持");
        }

        return result.Added;
    }

    /// <summary>设置变更后调用：重建窗口（边启用/仅主屏会改变窗口数量）。</summary>
    public void ApplySettings()
    {
        _settings.Normalize();
        _settings.Save(_paths);
        _topology.Rebuild();
    }

    /// <summary>
    /// 重发「可粘贴落点」快照（<see cref="PasteHook"/> 内部槽用 Volatile 发布给钩子线程）。
    ///
    /// <para>调用点只有三处：<see cref="RebuildForms"/> 末尾、每个窗口的
    /// <see cref="LeashForm.StateChanged"/>、显隐切换。它们都是事件驱动而非 tick 驱动，
    /// 所以**发布动作**本身没有额外延迟；但快照内容来自 <see cref="LeashForm.IsExpanded"/>，
    /// 而展开态由宿主 tick（<see cref="Metrics.CursorProbeIntervalMs"/>）驱动，
    /// 因此命中精度是 **120ms 级，不是亚毫秒**。这一点必须在验收口径里说清。</para>
    /// </summary>
    public void PublishSnapshot()
    {
        var targets = new List<LeashTarget>(_forms.Count);
        foreach (var form in _forms)
        {
            // 隐藏的绳子不该接粘贴：HideAll 之后窗口对象还在，但用户看不见它。
            if (form.Visible && form.IsExpanded)
            {
                targets.Add(new LeashTarget(form.Edge, form.ExpandedBounds));
            }
        }

        _pasteHook.Publish(new LeashSnapshot(targets.ToArray()));
    }

    /// <summary>
    /// 把接收 <c>WM_APP_PASTE</c> 的窗口交给钩子。
    /// 该窗口必须是托盘持有的顶层窗口（四边全关时依然存活），不能是任意一条 <see cref="LeashForm"/>。
    /// </summary>
    /// <param name="hwnd">接收器窗口句柄。</param>
    public void AttachPasteReceiver(IntPtr hwnd) => _pasteHook.SetSink(hwnd);

    /// <summary>
    /// 托盘「Ctrl+V 兜底」勾选项：即时启停钩子并持久化。
    /// 返回是否达成期望状态——勾上但装不上时返回 false，调用方负责置灰并提示降级入口。
    /// </summary>
    /// <param name="enabled">期望状态。</param>
    public bool TrySetPasteHook(bool enabled)
    {
        if (enabled)
        {
            bool started = _pasteHook.Start();
            _pasteHookInstallFailed = !started;
            _settings.PasteHookEnabled = started;
            _settings.Save(_paths);
            if (started)
            {
                PublishSnapshot();
            }

            return started;
        }

        _pasteHook.Stop();
        _pasteHookInstallFailed = false;
        _settings.PasteHookEnabled = false;
        _settings.Save(_paths);
        return true;
    }

    /// <summary>
    /// 启动时的数据维护，顺序不能反：先升级旧图片卡片（生成缩略图并写回 items.json），
    /// 再清理孤儿文件——清理是按 items.json 的引用扫 thumbs/ 的，迁移产物必须先入库，
    /// 否则刚生成的缩略图会被当成孤儿删掉。
    /// </summary>
    private async Task InitializeStoreAsync()
    {
        try
        {
            var upgraded = await _store.UpgradeImageFileItemsAsync().ConfigureAwait(true);
            if (upgraded > 0)
            {
                Program.Notify("已为旧图片补上预览", upgraded + " 张");
            }
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog(ex, fatal: false);
        }

        try
        {
            await _store.CleanupOrphansAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog(ex, fatal: false);
        }
    }

    private int CursorDpi()
    {
        try
        {
            var slots = _topology.Slots;
            var cursor = Cursor.Position;
            foreach (var slot in slots)
            {
                if (slot.Screen.Bounds.Contains(cursor))
                {
                    return slot.Screen.Dpi;
                }
            }

            if (slots.Count > 0)
            {
                return slots[0].Screen.Dpi;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            // 拓扑正在重建：退回基准 DPI，下一 tick 会拿到正确值。
        }

        return Metrics.DpiBaseline;
    }

    private void RebuildForms()
    {
        foreach (var form in _forms)
        {
            form.StateChanged -= OnFormStateChanged;
            form.Hide();
            form.Dispose();
        }

        _forms.Clear();

        foreach (var slot in _topology.Slots)
        {
            if (!_settings.IsEdgeEnabled(slot.Edge))
            {
                continue;
            }

            var form = new LeashForm(slot, _store, _thumbnails, _paths, _settings);
            form.StateChanged += OnFormStateChanged;
            _forms.Add(form);
            if (_visible)
            {
                form.Show();
            }
        }

        // 窗口集合变了：立即重发快照，否则钩子线程还按旧的展开矩形判命中。
        PublishSnapshot();
    }

    private void OnFormStateChanged(object? sender, EventArgs e) => PublishSnapshot();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var cursor = Cursor.Position;
        var leftDown = (GetAsyncKeyState(InteropConstants.VkLeftButton) & InteropConstants.KeyStatePressed) != 0;

        foreach (var form in _forms)
        {
            form.EvaluateCursor(cursor, leftDown);
            form.Tick();
            if (form.IsExpanded)
            {
                _activeEdge = form.Edge;
            }
        }
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        foreach (var form in _forms)
        {
            form.RefreshItems();
        }
    }

    private void OnItemsEvicted(object? sender, ItemsEvictedEventArgs e)
    {
        Program.Notify("空间不足，已清理最早的内容", "移除了 " + e.Items.Count + " 项（上限 " + (e.QuotaBytes / InteropConstants.BytesPerMegabyte) + " MB）");
    }

    private void OnTopologyRebuilt(object? sender, EventArgs e)
    {
        RebuildForms();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();

        _topology.Rebuilt -= OnTopologyRebuilt;
        _topology.Dispose();
        PreviewHost.Release();

        _store.Changed -= OnStoreChanged;
        _store.ItemsEvicted -= OnItemsEvicted;

        foreach (var form in _forms)
        {
            form.StateChanged -= OnFormStateChanged;
            form.Hide();
            form.Dispose();
        }

        _forms.Clear();
        _pasteHook.Dispose();
        _store.Dispose();
        _thumbnails.Dispose();
    }
}
