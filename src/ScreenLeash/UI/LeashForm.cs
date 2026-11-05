using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenLeash.Core;
using ScreenLeash.Interop;

namespace ScreenLeash.UI;

/// <summary>
/// 一条绳子 = 一个贴边窗口。负责：显隐动画、卡片绘制、拖入接收、拖出、悬停与滚动。
/// 不做拓扑决策（由 <see cref="LeashHost"/> 决定有几条、在哪条边），也不做持久化（交给 <see cref="ItemStore"/>）。
///
/// <para><b>不抢焦点</b>：窗口带 WS_EX_NOACTIVATE，点它不会把前台程序切走；代价是永远收不到键盘焦点，
/// 所以「粘贴」只能走全局钩子（<c>PasteHook</c>）/剪贴板监听，不能靠窗口自己的 Ctrl+V。</para>
///
/// <para><b>动画</b>：厚度在收起与展开之间按 <see cref="Metrics.EaseDivisor"/> 缓动，
/// 只有厚度真正变化时才 SetBounds，避免每帧重排；拖出期间（Dragging）不接受收起。</para>
/// </summary>
public sealed class LeashForm : Form
{
    private readonly EdgeSlot _slot;
    private readonly ItemStore _store;
    private readonly Thumbnailer? _thumbnails;
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;

    private LeashVisualState _state = LeashVisualState.Idle;
    private int _thicknessPx;
    private int _scrollPx;
    private int _hoverIndex = -1;
    private int _pressedIndex = -1;
    private Point _pressPoint;
    private bool _dragging;
    private bool _dropTarget;
    private float _lastOpacity = Metrics.CollapsedOpacity;

    public LeashForm(EdgeSlot slot, ItemStore store, Thumbnailer? thumbnails, AppPaths paths, AppSettings settings)
    {
        _slot = slot;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _thumbnails = thumbnails;
        _paths = paths ?? AppPaths.Default;
        _settings = settings ?? new AppSettings();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceOpaque;
        DoubleBuffered = true;
        AllowDrop = true;
        Text = "ScreenLeash";
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;

        _thicknessPx = Metrics.Px(Metrics.CollapsedThickness, Dpi);
        Bounds = EdgeGeometry.ComputeWithThickness(Edge, _slot.Screen.WorkingArea, _thicknessPx);
        Opacity = Metrics.CollapsedOpacity;
        _lastOpacity = (float)Opacity;
    }

    /// <summary>所在边。</summary>
    public EdgeKind Edge => _slot.Edge;

    /// <summary>所在屏幕的 DPI（窗口矩形与绘制都按它换算）。</summary>
    public int Dpi => _slot.Screen.Dpi;

    /// <summary>当前视觉状态。</summary>
    public LeashVisualState State => _state;

    /// <summary>该边当前挂了几张卡片。</summary>
    public int ItemCount => _store.ForEdge(Edge).Count;

    /// <summary>是否处于展开（悬停/拖拽）。</summary>
    public bool IsExpanded => EdgeGeometry.IsExpanded(_state);

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= InteropConstants.WsExNoActivate;
            parameters.ExStyle |= InteropConstants.WsExToolWindow;
            return parameters;
        }
    }

    /// <summary>由宿主每 tick 调用：根据光标与按键状态决定该不该展开。</summary>
    public void EvaluateCursor(Point cursor, bool leftButtonDown)
    {
        if (_dragging || _dropTarget)
        {
            SetState(LeashVisualState.Dragging);
            return;
        }

        var area = _slot.Screen.WorkingArea;
        var inZone = EdgeGeometry.IsCursorInTriggerZone(Edge, area, Dpi, cursor);
        var insideWindow = Bounds.Contains(cursor);

        SetState(inZone || insideWindow ? LeashVisualState.Hover : LeashVisualState.Idle);
    }

    /// <summary>由宿主每 tick 调用：推进厚度缓动与透明度。</summary>
    public void Tick()
    {
        var area = _slot.Screen.WorkingArea;
        var collapsed = Metrics.Px(Metrics.CollapsedThickness, Dpi);
        var expanded = Metrics.Px(Metrics.ExpandedThickness, Dpi);
        var target = EdgeGeometry.IsExpanded(_state) ? expanded : collapsed;

        if (_thicknessPx == target)
        {
            // 首帧会被 WinForms 的最小窗口尺寸撑大（实测收起态显示 14px 而非 6px），
            // 这里做一次几何对齐；尺寸稳定后 Bounds 相等，不会重复 SetBounds。
            var expected = EdgeGeometry.ComputeWithThickness(Edge, area, _thicknessPx);
            if (Bounds != expected)
            {
                Bounds = expected;
                Invalidate();
            }

            return;
        }

        var delta = target - _thicknessPx;
        var step = Math.Max(Metrics.Px(Metrics.MinEaseStep, Dpi), Math.Abs(delta) / Metrics.EaseDivisor);
        var next = _thicknessPx + (Math.Sign(delta) * Math.Min(step, Math.Abs(delta)));
        _thicknessPx = next;

        Bounds = EdgeGeometry.ComputeWithThickness(Edge, area, next);

        var span = Math.Max(1, expanded - collapsed);
        var ratio = Math.Clamp((_thicknessPx - collapsed) / (float)span, 0f, 1f);
        var opacity = Metrics.CollapsedOpacity + ((Metrics.ExpandedOpacity - Metrics.CollapsedOpacity) * ratio);
        if (Math.Abs(opacity - _lastOpacity) > float.Epsilon)
        {
            Opacity = opacity;
            _lastOpacity = opacity;
        }

        Invalidate();
    }

    /// <summary>卡片集合变化后刷新（不改变状态）。</summary>
    public void RefreshItems()
    {
        _hoverIndex = -1;
        _scrollPx = EdgeLayout.ClampScroll(Edge, ClientRectangle, Dpi, ItemCount, _scrollPx);
        Invalidate();
    }

    private void SetState(LeashVisualState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        Invalidate();
    }

    // ── 绘制 ─────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        if (g is null)
        {
            return;
        }

        var client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var surface = new SolidBrush(_dropTarget ? Theme.SurfaceHover : Theme.Surface))
        {
            g.FillRectangle(surface, client);
        }

        DrawEdgeLine(g, client);

        var items = _store.ForEdge(Edge);
        if (items.Count == 0)
        {
            return;
        }

        var slots = EdgeLayout.Compose(Edge, client, Dpi, items.Count, _scrollPx);
        foreach (var slot in slots)
        {
            if (slot.Index < 0 || slot.Index >= items.Count)
            {
                continue;
            }

            if (!client.IntersectsWith(slot.Bounds))
            {
                continue;
            }

            CardRenderer.Draw(g, slot.Bounds, items[slot.Index], Dpi, slot.Index == _hoverIndex, _thumbnails, _paths);
        }
    }

    private void DrawEdgeLine(Graphics g, Rectangle client)
    {
        using var pen = new Pen(Theme.EdgeHighlight, Metrics.PxFloat(Metrics.HairlineWidth, Dpi));
        switch (Edge)
        {
            case EdgeKind.Left:
                g.DrawLine(pen, 0, 0, 0, client.Height);
                break;
            case EdgeKind.Right:
                g.DrawLine(pen, client.Width - 1, 0, client.Width - 1, client.Height);
                break;
            case EdgeKind.Top:
                g.DrawLine(pen, 0, 0, client.Width, 0);
                break;
            default:
                g.DrawLine(pen, 0, client.Height - 1, client.Width, client.Height - 1);
                break;
        }
    }

    // ── 鼠标 ─────────────────────────────────────────────────────
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        UpdateHover(e.Location);

        if (e.Button != MouseButtons.Left || _pressedIndex < 0 || _dragging)
        {
            return;
        }

        var dragSize = SystemInformation.DragSize;
        if (Math.Abs(e.X - _pressPoint.X) > dragSize.Width || Math.Abs(e.Y - _pressPoint.Y) > dragSize.Height)
        {
            StartDragOut(_pressedIndex);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _pressPoint = e.Location;
        _pressedIndex = HitIndex(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        _pressedIndex = -1;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        var items = _store.ForEdge(Edge);
        if (items.Count == 0)
        {
            return;
        }

        var step = Metrics.Px(Metrics.ScrollStep, Dpi);
        var direction = e.Delta > 0 ? -1 : 1;
        _scrollPx = EdgeLayout.ClampScroll(Edge, ClientRectangle, Dpi, items.Count, _scrollPx + (direction * step));
        Invalidate();
    }

    private void UpdateHover(Point location)
    {
        var index = HitIndex(location);
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        Invalidate();
    }

    private int HitIndex(Point location)
    {
        var items = _store.ForEdge(Edge);
        if (items.Count == 0)
        {
            return -1;
        }

        var slots = EdgeLayout.Compose(Edge, ClientRectangle, Dpi, items.Count, _scrollPx);
        return EdgeLayout.HitTest(slots, location);
    }

    private void StartDragOut(int index)
    {
        var items = _store.ForEdge(Edge);
        if (index < 0 || index >= items.Count)
        {
            return;
        }

        var item = items[index];
        _dragging = true;
        SetState(LeashVisualState.Dragging);

        try
        {
            DragOut.Start(this, item, _paths, _thumbnails, _settings);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.ExternalException)
        {
            Program.WriteCrashLog(ex, fatal: false);
        }
        finally
        {
            _dragging = false;
            _pressedIndex = -1;
            SetState(LeashVisualState.Idle);
            _ = _store.TouchAsync(item.Id);
            Invalidate();
        }
    }

    // ── 拖入 ─────────────────────────────────────────────────────
    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);

        _dropTarget = true;
        drgevent.Effect = DragIn.EffectsFor(drgevent.Data);
        SetState(LeashVisualState.Dragging);
        Invalidate();
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);

        drgevent.Effect = DragIn.EffectsFor(drgevent.Data);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);

        _dropTarget = false;
        SetState(LeashVisualState.Idle);
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);

        _dropTarget = false;
        drgevent.Effect = DragDropEffects.Copy;

        var data = drgevent.Data;
        _ = HandleDropAsync(data);
    }

    private async Task HandleDropAsync(IDataObject? data)
    {
        try
        {
            var result = await DragIn.IngestAsync(data, _store, _settings, Edge).ConfigureAwait(true);
            RefreshItems();
            if (result.AnyAdded)
            {
                Program.Notify("已挂到绳子", result.Added + " 项");
            }
            else if (result.Skipped > 0)
            {
                Program.Notify("没有挂上", result.LastError ?? "内容不受支持");
            }
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog(ex, fatal: false);
            Program.Notify("挂上失败", ex.Message);
        }
        finally
        {
            SetState(LeashVisualState.Idle);
        }
    }
}
