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

    private EdgeKind _activeEdge = EdgeKind.Right;
    private bool _visible = true;
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
        _ = _store.CleanupOrphansAsync();
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
    /// 把剪贴板内容挂到最近活跃边（托盘菜单 / 全局热键 / 钩子降级路径共用）。
    /// 返回挂上的数量；0 表示剪贴板没有可挂内容。
    /// </summary>
    public async Task<int> PasteFromClipboardAsync()
    {
        var parsed = ClipboardParser.ParseClipboard();
        if (!parsed.HasContent)
        {
            Program.Notify("剪贴板没有可挂的内容", "复制一张图、一段文字或一个文件后再试");
            return 0;
        }

        var target = _forms.Count > 0 ? _activeEdge : EdgeKind.Right;
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
            _forms.Add(form);
            if (_visible)
            {
                form.Show();
            }
        }
    }

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

        _store.Changed -= OnStoreChanged;
        _store.ItemsEvicted -= OnItemsEvicted;

        foreach (var form in _forms)
        {
            form.Hide();
            form.Dispose();
        }

        _forms.Clear();
        _store.Dispose();
        _thumbnails.Dispose();
    }
}
