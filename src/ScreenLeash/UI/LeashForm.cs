using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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

    /// <summary>外部正拖东西靠近（还没进窗口）：据此画落点底框。纯悬停查看时为 false，不画框。</summary>
    private bool _dragApproach;

    /// <summary>正在做"绳子内部拖动排序"（未交给 OLE）。</summary>
    private bool _internalDrag;

    /// <summary>磁铁吸附高亮：别的绳子拖东西过来时本窗口亮起。</summary>
    private bool _magnetHighlight;

    /// <summary>诊断日志的 Move 打点计数（前几次足够定位）。</summary>
    private int _diagMoves;

    /// <summary>内部拖动时的插入位置（同边排序用；-1 = 跨边或无效）。</summary>
    private int _insertIndex = -1;

    /// <summary>内部拖动时悬停到的目标绳子（跨边排序用）。</summary>
    private LeashForm? _dragTargetForm;
    private float _lastOpacity = Metrics.CollapsedOpacity;

    /// <summary>悬停放大预览的延迟定时器：停够 <see cref="Metrics.PreviewDelayMs"/> 才弹，避免划过就闪。</summary>
    private readonly System.Windows.Forms.Timer _previewTimer;

    /// <summary>当前为哪张卡片计时（-1 = 无）。</summary>
    private int _previewIndex = -1;

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
        // 像素级透明：TransparencyKey 会把这个颜色的像素抠成全透明（连鼠标命中也穿透），
        // 于是卡片之间、绳线上下的空隙都是真的透出壁纸，而不是一层"近黑底色"。
        // 代价：完全透明的像素不接收拖放——拖东西要落在卡片上（缝隙只有 6px 宽，很难正好落进去）。
        BackColor = Theme.TransparencyKey;
        TransparencyKey = Theme.TransparencyKey;
        DoubleBuffered = true;
        AllowDrop = true;
        Text = "ScreenLeash";
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;

        _thicknessPx = Metrics.Px(Metrics.CollapsedThickness, Dpi);
        Bounds = EdgeGeometry.ComputeWithThickness(Edge, _slot.Screen.WorkingArea, _thicknessPx);
        Opacity = Metrics.CollapsedOpacity;
        _lastOpacity = (float)Opacity;

        _previewTimer = new System.Windows.Forms.Timer { Interval = Metrics.PreviewDelayMs };
        _previewTimer.Tick += OnPreviewTimerTick;
    }

    /// <summary>所在边。</summary>
    public EdgeKind Edge => _slot.Edge;

    /// <summary>本窗口所在屏幕的工作区（跨边磁铁吸附判定要用）。
    /// 单独暴露成属性，避免外部直接碰 _slot 字段（CS1690）。</summary>
    internal Rectangle WorkingArea => _slot.Screen.WorkingArea;

    /// <summary>所在屏幕的 DPI（窗口矩形与绘制都按它换算）。</summary>
    public int Dpi => _slot.Screen.Dpi;

    /// <summary>当前视觉状态。</summary>
    public LeashVisualState State => _state;

    /// <summary>该边当前挂了几张卡片。</summary>
    public int ItemCount => _store.ForEdge(Edge).Count;

    /// <summary>是否处于展开（悬停/拖拽）。</summary>
    public bool IsExpanded => EdgeGeometry.IsExpanded(_state);

    /// <summary>
    /// 展开态的窗口矩形（屏幕坐标）。
    ///
    /// <para>无论缓动动画走到哪一帧都返回**满展开**矩形：全局粘贴的命中判定需要稳定几何，
    /// 用当前厚度会让命中区域在动画期间抖动。矩形只随屏幕/DPI/边变化，因此可以与状态事件
    /// 一起当作快照内容发布。</para>
    /// </summary>
    public Rectangle ExpandedBounds =>
        EdgeGeometry.ComputeWithThickness(Edge, _slot.Screen.WorkingArea, Metrics.Px(Metrics.ExpandedThickness, Dpi));

    /// <summary>
    /// 视觉状态（收起 ↔ 展开）发生迁移时触发，参数无意义。
    /// 宿主用它即时重发可粘贴落点快照——否则钩子线程只能看到上一轮 tick 的旧快照。
    /// </summary>
    public event EventHandler? StateChanged;

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
    /// <summary>当前是否为了接收拖放而临时取消了透明键。</summary>
    private bool _dropOpaque;

    /// <summary>
    /// 拖放期间临时取消像素级透明。
    ///
    /// <para>为什么需要：TransparencyKey 抠掉的像素**连鼠标命中也穿透**。
    /// 微信"已发出的消息"带 FileDrop，光标通常停在卡片上，能触发 DragEnter；
    /// 而"草稿里打的字"是纯文本（System.String|UnicodeText|Text），
    /// 光标稍微落到卡片缝隙或空白处就完全穿透 —— 表现为**不显示底框、也挂不上**。
    /// 左键一松开就恢复透明，不影响平时的观感。</para>
    /// </summary>
    public void SetDropOpaque(bool opaque)
    {
        if (_dropOpaque == opaque)
        {
            return;
        }

        _dropOpaque = opaque;

        if (opaque)
        {
            TransparencyKey = Color.Empty;
            BackColor = Theme.SurfaceDropOpaque;
        }
        else
        {
            BackColor = Theme.TransparencyKey;
            TransparencyKey = Theme.TransparencyKey;
        }

        Invalidate();
    }

    public void EvaluateCursor(Point cursor, bool leftButtonDown)
    {
        if (_dragging || _dropTarget)
        {
            SetState(LeashVisualState.Dragging);
            return;
        }

        var area = _slot.Screen.WorkingArea;
        var insideWindow = Bounds.Contains(cursor);

        // 拖拽靠近时用更宽的感应区（DragApproachZone），并画落点底框；
        // 纯悬停仍用窄区且不画框——用户要求「放上去查看时保持没有底框」。
        var zonePx = leftButtonDown
            ? Metrics.Px(Metrics.DragApproachZone, Dpi)
            : Metrics.Px(Metrics.EdgeTriggerZone, Dpi);
        var inZone = EdgeGeometry.IsCursorInTriggerZone(Edge, area, cursor, zonePx);

        var approaching = leftButtonDown && inZone && !insideWindow;
        if (approaching != _dragApproach)
        {
            _dragApproach = approaching;
            Invalidate();
        }

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
        StateChanged?.Invoke(this, EventArgs.Empty);
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

        // 背景不画：TransparencyKey 区域必须保持纯洋红，Windows 才会把那些像素抠成全透明。
        // 绳线用不带抗锯齿的绘制，避免边缘与洋红混出花边。
        g.SmoothingMode = SmoothingMode.None;
        DrawSurface(g, client);
        DrawRopeLine(g, client);
        g.SmoothingMode = SmoothingMode.AntiAlias;

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

        DrawInsertIndicator(g, client);
    }

    /// <summary>拖动排序时画一条插入指示线，告诉用户松手后会插到哪里。</summary>
    private void DrawInsertIndicator(Graphics g, Rectangle client)
    {
        if (!_internalDrag || _insertIndex < 0)
        {
            return;
        }

        var items = _store.ForEdge(Edge);
        if (items.Count == 0)
        {
            return;
        }

        var slots = EdgeLayout.Compose(Edge, client, Dpi, items.Count, _scrollPx);
        var horizontal = EdgeLayout.IsHorizontal(Edge);

        int pos;
        if (_insertIndex < slots.Count)
        {
            var bounds = slots[_insertIndex].Bounds;
            pos = horizontal ? bounds.Left : bounds.Top;
        }
        else
        {
            var last = slots[slots.Count - 1].Bounds;
            pos = horizontal ? last.Right : last.Bottom;
        }

        using var pen = new Pen(Theme.CardBorderActive, Metrics.PxFloat(Metrics.IndicatorWidth, Dpi));
        if (horizontal)
        {
            g.DrawLine(pen, pos, client.Top, pos, client.Bottom);
        }
        else
        {
            g.DrawLine(pen, client.Left, pos, client.Right, pos);
        }
    }

    /// <summary>
    /// 背景：从贴边一侧向内渐隐到全透明。原来整块实心深色面板会挡住视线
    /// （用户原话「很宽的一个阴影面积，有点挡视线」），渐隐后只有卡片本身有底色。
    /// </summary>
    private void DrawSurface(Graphics g, Rectangle client)
    {
        // 常态**什么都不画**：那些像素必须保持纯洋红，Windows 才会抠成全透明、
        // 让壁纸透出来。只有拖入经过时才铺一层不透明深蓝作为落点提示。
        if (!_dropTarget && !_dragApproach && !_magnetHighlight)
        {
            return;
        }

        // 拖入经过 / 磁铁吸附都用深蓝（"能放这里"），只是靠近但还没吸附时用灰蓝。
        using var brush = new SolidBrush(
            _dropTarget || _magnetHighlight ? Theme.SurfaceHover : Theme.DragApproach);
        g.FillRectangle(brush, client);
    }

    /// <summary>
    /// 绳子本体：沿边的一条轻微下垂的贝塞尔曲线，代替原来贴边的一条直线。
    /// 下垂量由 <see cref="Metrics.RopeSagDip"/> 控制（收起态只有 6px 高，弧很小；展开后更明显）。
    /// </summary>
    /// <summary>
    /// 绳子本体：贴边的一条直线（用户反馈弧线不好看，改回直线）。
    /// 用调用方已设好的 <see cref="SmoothingMode.None"/> 绘制，避免边缘与 TransparencyKey 的洋红混出花边。
    /// </summary>
    private void DrawRopeLine(Graphics g, Rectangle client)
    {
        var inset = Metrics.PxFloat(Metrics.RopeEdgeInset, Dpi);
        using var pen = new Pen(Theme.EdgeHighlight, Metrics.PxFloat(Metrics.RopeStrokeWidth, Dpi));

        switch (Edge)
        {
            case EdgeKind.Left:
                g.DrawLine(pen, inset, client.Top, inset, client.Bottom);
                break;
            case EdgeKind.Right:
                g.DrawLine(pen, client.Right - inset, client.Top, client.Right - inset, client.Bottom);
                break;
            case EdgeKind.Top:
                g.DrawLine(pen, client.Left, inset, client.Right, inset);
                break;
            default:
                g.DrawLine(pen, client.Left, client.Bottom - inset, client.Right, client.Bottom - inset);
                break;
        }
    }

    // ── 鼠标 ─────────────────────────────────────────────────────
    /// <summary>
    /// 拖放诊断日志：写到 %LOCALAPPDATA%\ScreenLeash\logs\drag-diag.log。
    /// 拖放行为受 Capture / OLE / 窗口样式共同影响，读代码定不了因，
    /// 只能把事件序列和关键状态打出来看。日志失败绝不影响功能。
    /// </summary>
    private static void DragDiag(string message)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Default.Root, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "drag-diag.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
        }
        catch
        {
            // 诊断日志写不进去也不该影响拖放。
        }
    }

    /// <summary>
    /// 拖拽判定：由 <see cref="LeashHost"/> 每 tick（约 40ms）调用，传入**全局光标**和左键状态。
    ///
    /// <para>为什么不放在 OnMouseMove 里：实测 <c>Capture = true</c> 在这个窗口上设不上
    /// （WS_EX_NOACTIVATE + TransparencyKey），而 Capture 设不上时
    /// <see cref="OnMouseCaptureChanged"/> 的兜底会把 <c>_pressedIndex</c> 清成 -1，
    /// 于是"按下明明命中了卡片（日志 hit=1），拖到一半却变成 -1（StartDragOut index=-1）"。
    /// 轮询不依赖鼠标事件、不依赖捕获，从根上绕开这一整类问题。</para>
    /// </summary>
    public void PollDrag(Point screenCursor, bool leftDown)
    {
        if (_dragging)
        {
            return;
        }

        if (!leftDown)
        {
            // 松手：内部拖拽就提交，然后清空按下状态。
            if (_internalDrag)
            {
                CommitInternalDrag();
                _internalDrag = false;
                _insertIndex = -1;
                _dragTargetForm = null;
                Invalidate();
            }

            _pressedIndex = -1;
            return;
        }

        if (_pressedIndex < 0)
        {
            return;
        }

        var client = PointToClient(screenCursor);
        var dragSize = SystemInformation.DragSize;
        var moved = Math.Abs(client.X - _pressPoint.X) > dragSize.Width
                    || Math.Abs(client.Y - _pressPoint.Y) > dragSize.Height;
        if (!moved)
        {
            return;
        }

        _internalDrag = true;

        if (IsOverAnyLeash(screenCursor, out var target))
        {
            SetDragTarget(target);
            _insertIndex = ReferenceEquals(target, this) ? ComputeInsertIndexAt(client) : -1;
            Invalidate();
            return;
        }

        // 离开所有吸附区 → 交给系统拖出（拖到别的应用）。
        var index = _pressedIndex;
        _internalDrag = false;
        _insertIndex = -1;
        SetDragTarget(null);
        _pressedIndex = -1;
        Invalidate();
        StartDragOut(index);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        UpdateHover(e.Location);

        if (_diagMoves < Metrics.DiagMoveSamples)
        {
            _diagMoves++;
            DragDiag("Move#" + _diagMoves + " btn=" + e.Button + " mouseBtns=" + Control.MouseButtons
                + " capture=" + Capture + " pressed=" + _pressedIndex + " internal=" + _internalDrag
                + " loc=" + e.Location);
        }

        // 拖拽判定已经整体搬到 PollDrag：由 LeashHost 每 tick 用全局光标 + 左键状态驱动。
        // 这里不再判断、也不再用 Capture —— 实测 Capture 在这个窗口上设不上，
        // 曾导致"按下命中 hit=1、拖到一半却变成 index=-1"。
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // 右键卡片 = 删除。绳子窗口没有焦点、也没有菜单栏，右键是最直接的移除入口。
        if (e.Button == MouseButtons.Right)
        {
            DeleteAt(HitIndex(e.Location));
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _pressPoint = e.Location;
        _pressedIndex = HitIndex(e.Location);

        // 这次拖动为什么没起来，关键就看这一行：
        // OnMouseDown 有没有被调用（窗口是 TransparencyKey + NOACTIVATE，可能收不到）、
        // 以及 HitIndex 命中的是几号（-1 = 没命中卡片）。
        DragDiag("MouseDown loc=" + e.Location + " hit=" + _pressedIndex
            + " client=" + ClientRectangle + " count=" + _store.ForEdge(Edge).Count);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_internalDrag)
        {
            CommitInternalDrag();
        }

        Capture = false;
        _internalDrag = false;
        _insertIndex = -1;
        _dragTargetForm = null;
        _pressedIndex = -1;
        Invalidate();
    }

    /// <summary>
    /// 鼠标捕获被系统抢走（Alt+Tab、Esc、别的窗口抢捕获）时，
    /// 必须清掉内部拖拽状态——否则 _internalDrag 会一直为 true，
    /// 下次点击会莫名其妙地提交一次"排序"。
    /// </summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);

        if (!Capture && _internalDrag)
        {
            _internalDrag = false;
            _insertIndex = -1;
            _dragTargetForm = null;
            _pressedIndex = -1;
            Invalidate();
        }
    }

    /// <summary>
    /// 磁铁判定：光标是否吸附到某条绳子。返回最近的一条。
    ///
    /// <para>为什么不用「落在窗口矩形内」：窗口收起时只有 6dip 宽，
    /// 透明键又让缝隙的鼠标命中一起穿透，用矩形判定跨边拖动几乎吸不住。
    /// 改成「到边的距离 ≤ DragApproachZone」后，光标只要靠近屏幕边缘就算吸附，
    /// 这正是"磁铁"的语义。</para>
    /// </summary>
    private static bool IsOverAnyLeash(Point screenPoint, out LeashForm? target)
    {
        target = null;
        var best = int.MaxValue;

        foreach (Form form in Application.OpenForms)
        {
            if (form is not LeashForm leash || !leash.Visible)
            {
                continue;
            }

            var area = leash.WorkingArea;
            var zone = Metrics.Px(Metrics.DragApproachZone, leash.Dpi);
            if (!EdgeGeometry.IsCursorInTriggerZone(leash.Edge, area, screenPoint, zone))
            {
                continue;
            }

            var distance = leash.Edge switch
            {
                EdgeKind.Left => screenPoint.X - area.Left,
                EdgeKind.Right => area.Right - screenPoint.X,
                EdgeKind.Top => screenPoint.Y - area.Top,
                _ => area.Bottom - screenPoint.Y,
            };

            if (distance < best)
            {
                best = distance;
                target = leash;
            }
        }

        return target is not null;
    }

    /// <summary>
    /// 切换"磁铁目标"并同步高亮：源窗口拖动时，被吸附的那条绳子亮起来，
    /// 明确告诉用户"松手就放这里"。
    /// </summary>
    private void SetDragTarget(LeashForm? target)
    {
        if (ReferenceEquals(_dragTargetForm, target))
        {
            return;
        }

        _dragTargetForm?.SetMagnetHighlight(false);
        _dragTargetForm = target;
        _dragTargetForm?.SetMagnetHighlight(true);

        DragDiag("磁铁目标 → " + (target is null ? "无" : target.Edge.ToString()));
    }

    /// <summary>磁铁吸附高亮开关（由源窗口调用）。</summary>
    public void SetMagnetHighlight(bool on)
    {
        if (_magnetHighlight == on)
        {
            return;
        }

        _magnetHighlight = on;
        Invalidate();
    }

    private int ComputeInsertIndexAt(Point clientPoint)
    {
        var items = _store.ForEdge(Edge);
        if (items.Count == 0)
        {
            return 0;
        }

        var slots = EdgeLayout.Compose(Edge, ClientRectangle, Dpi, items.Count, _scrollPx);
        return EdgeLayout.ComputeInsertIndex(slots, clientPoint, EdgeLayout.IsHorizontal(Edge));
    }

    /// <summary>松手：同边插入到指定位置；跨边追加到对侧末尾。</summary>
    private void CommitInternalDrag()
    {
        var items = _store.ForEdge(Edge);
        if (_pressedIndex < 0 || _pressedIndex >= items.Count)
        {
            return;
        }

        var item = items[_pressedIndex];

        if (_dragTargetForm is null || ReferenceEquals(_dragTargetForm, this))
        {
            var target = _insertIndex < 0 ? items.Count : _insertIndex;
            _ = _store.MoveToAsync(item.Id, Edge, target);
        }
        else
        {
            _ = _store.MoveToAsync(item.Id, _dragTargetForm.Edge, int.MaxValue);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            SchedulePreview(-1);
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

        // 滚动后卡片位移，悬停的可能是另一张：预览已显示时立即切到新卡片（“不断弹出”），
        // 否则重新计时，避免刚划过就闪。
        var underCursor = HitIndex(e.Location);
        _hoverIndex = underCursor;
        if (PreviewHost.IsVisible && underCursor >= 0)
        {
            ShowPreviewNow(underCursor);
        }
        else
        {
            SchedulePreview(underCursor);
        }
    }

    private void UpdateHover(Point location)
    {
        var index = HitIndex(location);
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        SchedulePreview(index);
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

    /// <summary>右键删除该位置的卡片（绳子窗口无焦点、无菜单栏，右键是最直接的移除入口）。</summary>
    private async void DeleteAt(int index)
    {
        if (index < 0)
        {
            return;
        }

        var items = _store.ForEdge(Edge);
        if (index >= items.Count)
        {
            return;
        }

        var item = items[index];

        // 删除前先收起预览并停掉计时：否则预览窗会一直停在屏幕上，
        // 看起来像"删了但没消失"（用户反馈：预览卡在上面消不掉）。
        SchedulePreview(-1);
        _hoverIndex = -1;

        try
        {
            await _store.RemoveAsync(item.Id).ConfigureAwait(true);
            Program.Notify("已从绳子移除", CardRenderer.Describe(item));
        }
        catch (Exception ex)
        {
            Program.WriteCrashLog(ex, fatal: false);
        }
    }

    /// <summary>
    /// 安排/取消悬停预览。只有同一张卡片停够 <see cref="Metrics.PreviewDelayMs"/> 才弹；
    /// 换卡片、移出、滚动、开始拖拽都先收起旧预览。
    /// </summary>
    private void SchedulePreview(int index)
    {
        _previewTimer.Stop();
        _previewIndex = index;

        if (index < 0)
        {
            PreviewHost.Hide();
            return;
        }

        PreviewHost.Hide();
        _previewTimer.Start();
    }

    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();

        if (_previewIndex < 0 || _hoverIndex != _previewIndex)
        {
            return;
        }

        ShowPreviewNow(_previewIndex);
    }

    /// <summary>立即显示某张卡片的放大预览（定时器到点与滚动跟随共用，跳过延迟）。</summary>
    private void ShowPreviewNow(int index)
    {
        var items = _store.ForEdge(Edge);
        if (index < 0 || index >= items.Count)
        {
            PreviewHost.Hide();
            return;
        }

        var slots = EdgeLayout.Compose(Edge, ClientRectangle, Dpi, items.Count, _scrollPx);
        if (index >= slots.Count)
        {
            PreviewHost.Hide();
            return;
        }

        var local = slots[index].Bounds;
        var anchor = new Rectangle(PointToScreen(local.Location), local.Size);
        PreviewHost.Show(items[index], _paths, anchor, Dpi, _slot.Screen.WorkingArea);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewTimer.Stop();
            _previewTimer.Tick -= OnPreviewTimerTick;
            _previewTimer.Dispose();
            PreviewHost.Hide();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 双击打开卡片：文件用系统默认程序打开（PDF/Word/图片都能用），
    /// 文字卡片只在内容本身就是 http/https 链接时才打开浏览器。
    /// 打开失败只记日志 + 气泡，不影响绳子本身。
    /// </summary>
    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);

        var index = HitIndex(PointToClient(Cursor.Position));
        if (index < 0)
        {
            return;
        }

        var items = _store.ForEdge(Edge);
        if (index >= items.Count)
        {
            return;
        }

        OpenItem(items[index]);
    }

    private void OpenItem(Item item)
    {
        try
        {
            if (item.Kind == Kind.Text)
            {
                var text = item.Text?.Trim();
                if (!string.IsNullOrEmpty(text)
                    && Uri.TryCreate(text, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }

                return;
            }

            var path = _paths.BlobPath(item.BlobName);
            if (path is null || !File.Exists(path))
            {
                Program.Notify("打不开", "文件已经不在了");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                   || ex is InvalidOperationException
                                   || ex is FileNotFoundException)
        {
            Program.WriteCrashLog(ex, fatal: false);
            Program.Notify("打不开", ex.Message);
        }
    }

    private void StartDragOut(int index)
    {
        DragDiag("StartDragOut index=" + index + " mouseBtns=" + Control.MouseButtons);

        var items = _store.ForEdge(Edge);
        if (index < 0 || index >= items.Count)
        {
            DragDiag("StartDragOut 放弃：index 越界 count=" + items.Count);
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

        // 枚举格式可能抛（某些数据源在拖放早期不可用），不能因此影响拖入本身。
        try
        {
            DragDiag("DragEnter isDragging=" + DragOut.IsDragging
                + " hasFileDrop=" + (drgevent.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                + " effect=" + DragIn.EffectsFor(drgevent.Data)
                + " formats=[" + string.Join("|", drgevent.Data?.GetFormats() ?? Array.Empty<string>()) + "]");
        }
        catch
        {
            // 只为诊断，读不到就跳过。
        }

        // 自己拖出的卡片**也允许进入**：拖到对侧要能弹底框、要能落下去做"移动"。
        // 拦截只能放在 OnDragDrop 里（按移动处理），放在这里会导致拖不过去。
        _dropTarget = true;
        drgevent.Effect = DragIn.EffectsFor(drgevent.Data);
        SchedulePreview(-1);
        SetState(LeashVisualState.Dragging);
        Invalidate();
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);

        // 自己拖出的卡片给 Move 效果：落到本窗口是「移动到这边」，不是复制一份。
        drgevent.Effect = DragOut.IsDragging
            ? DragDropEffects.Move
            : DragIn.EffectsFor(drgevent.Data);
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

        DragDiag("DragDrop isDragging=" + DragOut.IsDragging
            + " hasFileDrop=" + (drgevent.Data?.GetDataPresent(DataFormats.FileDrop) == true));

        // 自己拖出的卡片落到绳子上 = **移动**：拖到对侧就换边，拖回原边则无操作。
        // 绝不能走下面的「拖入」路径，否则会复制一份 blob，名字也变成 GUID。
        if (DragOut.IsDragging)
        {
            var draggingId = DragOut.DraggingItemId;
            if (draggingId is not null)
            {
                _ = _store.MoveAsync(draggingId.Value, Edge);
            }

            drgevent.Effect = DragDropEffects.Move;
            _dropTarget = false;
            SchedulePreview(-1);
            Invalidate();
            return;
        }

        // 必须在事件内**同步**把数据抓成本地副本：
        // DragDrop 返回后，拖放源（尤其微信这类 COM 数据源）不再有义务提供数据，
        // 异步方法里再读只能拿到 null —— 表现就是"拖进来了、拖放指针也变了，但没挂上"。
        // 微信草稿文字正好给的是 System.String|UnicodeText|Text 这种需要实时取值的格式，
        // 所以只有它复现，已发出的消息（可能带 FileDrop）不受影响。
        var data = SnapshotData(drgevent.Data);

        _dropTarget = false;
        drgevent.Effect = DragDropEffects.Copy;
        _ = HandleDropAsync(data);
    }

    /// <summary>把拖放数据抓成本地副本，避免离开 DragDrop 事件后数据源失效。</summary>
    private static IDataObject SnapshotData(IDataObject? source)
    {
        var snapshot = new DataObject();
        if (source is null)
        {
            return snapshot;
        }

        foreach (var format in source.GetFormats())
        {
            try
            {
                var value = source.GetData(format);
                if (value is not null)
                {
                    snapshot.SetData(format, value);
                }
            }
            catch
            {
                // 个别格式读不到就跳过，不影响其他格式。
            }
        }

        return snapshot;
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
