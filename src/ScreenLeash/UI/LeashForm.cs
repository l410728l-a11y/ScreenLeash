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

    // ── 时间基过渡（Wave 2）───────────────────────────────────
    // 原实现用离散步长（|Δ|/EaseDivisor）且由 40ms 探测时钟驱动，
    // 40→140dip 只能走出 5 帧，等效 25fps 的台阶感。
    // 现在：探测时钟只定目标，动画时钟（16ms）按时间进度推 ease-out。
    private float _animT;
    private int _animFromPx;
    private int _animToPx = int.MinValue;
    private int _scrollPx;
    private int _hoverIndex = -1;
    private int _pressedIndex = -1;
    private Point _pressPoint;

    /// <summary>上一次 PollDrag 的光标位置，用来判断「停住了」还是「正在划过去」。</summary>
    private Point _lastPollCursor;

    /// <summary>光标停在别的程序窗口上、且没在移动的累计毫秒数（拖出判定用）。</summary>
    private int _outsideIdleMs;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    private bool _dragging;
    private bool _dropTarget;

    /// <summary>外部东西拖进窗口：只把绳线点亮当提示，**不展开面板**（展开会挡住边缘图标操作）。</summary>
    private bool _dropHighlight;

    /// <summary>是否有外部东西正拖在窗口上（用于决定要不要维持不透明）。</summary>
    public bool IsDropHighlighted => _dropHighlight;

    /// <summary>
    /// 本窗口是否正在拖自己的卡片（内部排序或拖出）。
    /// 与「外部东西拖进来」互斥：前者面板上有卡片像素、不该整块变黑；后者必须不透明才能收 DragEnter。
    /// </summary>
    public bool IsDraggingCard => _internalDrag || _dragging;

    /// <summary>
    /// 本窗口是否有一张卡片正被按住。拖动自己卡片的**启动瞬间**唯一的可靠信号：
    /// 那时 <see cref="IsDraggingCard"/> 还是 false（PollDrag 尚未跑到），
    /// 不靠它就会把源窗口当成「外部东西拖进来」而整块变黑。
    /// </summary>
    public bool HasPressedCard => _pressedIndex >= 0;

    /// <summary>
    /// 光标是否落在该边的磁铁感应区（跨边拖的落点范围）。
    /// 拖自己卡片时只有命中的那条边才取消透明——否则四条边同时变黑，
    /// 用户看到的是"四周一圈黑框"（反馈原话）。
    /// </summary>
    public bool IsCursorNearEdge(Point screenPoint)
        => EdgeGeometry.IsCursorInTriggerZone(
            Edge,
            _slot.Screen.WorkingArea,
            screenPoint,
            Metrics.Px(Metrics.MagnetSnapZone, Dpi));

    /// <summary>正在做"绳子内部拖动排序"（未交给 OLE）。</summary>
    private bool _internalDrag;

    /// <summary>磁铁吸附高亮：别的绳子拖东西过来时本窗口亮起。</summary>
    private bool _magnetHighlight;

    /// <summary>上一次写绘制诊断时的厚度（只在变化时记一行，避免刷屏）。</summary>
    private int _diagLastPaintThickness = -1;

    /// <summary>是否已经把卡片槽位矩形打进日志（每实例一次，避免刷屏）。</summary>
    private bool _diagSlotLogged;

    /// <summary>上一次 DrawSurface 是否画了底色（用于只在切换时打日志，避免每帧刷屏）。</summary>
    private bool _lastDrewSurface;

    /// <summary>上一帧左键是否按下（用来检测"刚按下"那一帧）。</summary>
    private bool _prevLeftDown;

    /// <summary>本次拖动是否冻结展开响应：起点离绳子很近时置位，直到光标离开展开区。</summary>
    private bool _dragFrozen;

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

    /// <summary>文字卡排版参数：设置 → 纯布局参数的唯一换算点。
    /// 设置值已由 AppSettings.Normalize 夹取过，这里再夹一次是为了单元测试直接构造
    /// 非法设置时布局仍不越界。</summary>
    private TextCardStyle TextStyle => new(
        Math.Clamp(_settings.TextFontSize, Metrics.TextFontSizeMin, Metrics.TextFontSizeMax),
        Math.Clamp(_settings.TextMaxLines, Metrics.TextMaxLinesMin, Metrics.TextMaxLinesMax));

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

        // 诊断：这层不透明切换会让整窗变近黑；频繁 true/false 就会表现为「贴着绳子的黑色频闪」。
        DragDiag("SetDropOpaque " + opaque + " edge=" + Edge + " thick=" + _thicknessPx);

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

    /// <summary>拖放期间是否把窗口加宽到磁铁吸附半径，让「黑框」和「能放下的范围」是同一个矩形。</summary>
    private bool _dropReceive;

    /// <summary>
    /// 拖放接收模式：把窗口厚度临时撑到 <see cref="Metrics.MagnetSnapZone"/>。
    ///
    /// <para><b>为什么需要</b>：OLE 的命中区**就是窗口矩形**。收起态窗口只有 40dip，
    /// 而磁铁吸附半径是 90dip —— 用户按住卡片停在离绳子 50dip 处时，视觉上已经"贴住了"，
    /// 光标却落在窗口外，收不到任何拖放事件，卡片放不下去。
    /// 把接收期的窗口撑到 90dip，黑框（不透明区）、命中区、磁铁吸附半径三者重合。</para>
    ///
    /// <para><b>只对接收方生效</b>：源窗口正在拖自己的卡片，撑宽只会变成一大块黑框，
    /// 而那片黑框并不接受落点（放回源边等于没移动），必须排除。</para>
    /// </summary>
    public void SetDropReceive(bool on)
    {
        if (_dropReceive == on)
        {
            return;
        }

        _dropReceive = on;

        // **必须同步撑开**，不能等 LeashHost.OnTick 下一帧：
        // DoDragDrop 是模态循环，拖拽期间 OnTick 基本不跑（实测：整个 1.6 秒拖拽里
        // 窗口厚度一次都没变，拖拽结束后才从 50 跳到 113）。
        // 等 Tick 去撑宽 = 整个拖拽过程都停在 40dip，光标落在 40~90dip 之间就放不下去，
        // 而用户看到的黑框（窗口矩形）和真正能接住的区域就成了两个尺寸。
        if (on)
        {
            var px = Math.Max(_thicknessPx, Metrics.Px(Metrics.MagnetSnapZone, Dpi));
            _thicknessPx = px;
            _animFromPx = px;
            _animToPx = px;
            _animT = 1f;
            Bounds = EdgeGeometry.ComputeWithThickness(Edge, _slot.Screen.WorkingArea, px);
            Invalidate();
        }

        DragDiag("SetDropReceive " + on + " edge=" + Edge + " thick=" + _thicknessPx);
    }

    public void EvaluateCursor(Point cursor, bool leftButtonDown, bool mayBeDragging = false)
    {
        // 帧间状态必须在任何提前 return **之前**更新：
        // 否则 _dropTarget 短暂为真的那几帧不会更新 _prevLeftDown，
        // 回到正常后会被误判成"刚按下"，用**当前**光标位置重算冻结 ——
        // 起点信息就此丢失，表现就是"在边缘按下也会弹"。
        var justPressed = leftButtonDown && !_prevLeftDown;
        _prevLeftDown = leftButtonDown;

        if (_dragging)
        {
            SetState(LeashVisualState.Dragging);
            return;
        }

        // 有拖入物正在悬停（跨边拖或外部拖入）：保持当前尺寸不动。
        // 不能展开——不透明窗口一展开就变成一大块黑框（用户反馈“黑框很宽”）。
        if (_dropTarget)
        {
            return;
        }

        // 外部东西已经拖进窗口（OnDragEnter 已触发）：保持当前尺寸不动。
        // 这时任何 SetBounds 动画都可能让 OLE 重新判定「光标离开」→ DragLeave → 收起 → 再进入，
        // 就是 449c0b0 修掉的「闪一下就缩回去」。
        if (_dropHighlight)
        {
            return;
        }

        var area = _slot.Screen.WorkingArea;
        var insideWindow = Bounds.Contains(cursor);

        // 每帧都算：按下时用来判定是否冻结，冻结期间用来判定是否已经离开。
        var nearPx = Metrics.Px(Metrics.DragFreezeWidth, Dpi);
        var distance = Edge switch
        {
            EdgeKind.Left => Math.Abs(cursor.X - area.Left),
            EdgeKind.Right => Math.Abs(area.Right - cursor.X),
            // 绳子只用左右两条边；顶/底边默认不启用，不参与冻结判定。
            _ => int.MaxValue,
        };

        // 左键刚按下那一帧，记下"起点是不是就在屏幕侧边附近"：
        //   近 → 本次拖动冻结展开响应（在边缘附近按下开始拖东西时，绳子不该冒出来挡路）；
        //   远 → 不冻结（用户从屏幕中间拖东西过来，靠近就该弹）。
        //
        // 判定用**纯距离**而不是 IsCursorInTriggerZone：
        // 后者内部还有 EdgeSpanRatio=80% 的沿边限制（屏幕上下各 10% 判不到），
        // 而桌面图标常排在屏幕顶部/底部，用它会漏掉这些位置。
        if (justPressed)
        {
            _dragFrozen = insideWindow || distance <= nearPx;

            // 按下那一帧把判定依据打出来：区分"没算到"（distance 超阈值）、
            // "算到了但被别处覆盖"（frozen=True 却仍弹）两种情况。
            DragDiag("justPressed edge=" + Edge + " d=" + distance + " near=" + nearPx
                + " inside=" + insideWindow + " frozen=" + _dragFrozen);
        }

        if (!leftButtonDown)
        {
            _dragFrozen = false;
        }

        // 拖东西靠近时用更宽的判定（DragApproachZone）。
        // 纯悬停仍用 EdgeTriggerZone —— 否则鼠标随便靠近边缘就弹，反而更挡事。
        var draggingFromAway = leftButtonDown && !_dragFrozen;
        var zonePx = Metrics.Px(
            draggingFromAway ? Metrics.DragApproachZone : Metrics.EdgeTriggerZone, Dpi);

        // 拖拽时用**纯距离**判定：IsCursorInTriggerZone 内部还有 EdgeSpanRatio=80% 的沿边限制，
        // 屏幕上下各 10% 判不到——从那些位置拖东西过来会一直挂不上（用户反馈"实现不了"）。
        // 悬停仍走原函数，保持"只在边中段响应"的手感，不会因为这次改动变得四处乱弹。
        var inZone = draggingFromAway
            ? distance <= zonePx
            : EdgeGeometry.IsCursorInTriggerZone(Edge, area, cursor, zonePx);

        // 冻结期间保持当前状态不动；只有**真正离开冻结区**才解冻。
        // 不能再用 inZone(40dip) 当解冻判据：起点常落在 40~280dip 之间
        // （日志实测 d=43/54/89），用它会在按下第一帧就把刚设好的冻结解掉，
        // 下一帧光标进入 40dip 就弹出来——这正是"按着拖图标会弹"的原因。
        if (leftButtonDown && _dragFrozen)
        {
            if (distance > nearPx)
            {
                _dragFrozen = false;
            }
            else
            {
                return;
            }
        }

        // 按着左键并移动过 = 可能正在全屏拖东西（桌面图标、文件、别的程序的卡片）。
        // 这时不能因为光标靠近就展开：140dip 的面板会整块盖住正要拖的东西
        // （用户反馈："一拖，上面那个悬浮卡连带着都弹出来了，整体框弹出一百多"）。
        // 例外：按在本窗口卡片上（_pressedIndex >= 0）的内部拖动排序——窗口本来就在展开态。
        if (leftButtonDown && mayBeDragging && _pressedIndex < 0)
        {
            // 已经展开的面板不强制收起（用户可能正在它上面操作），只是别再新展开。
            if (!IsExpanded)
            {
                SetState(LeashVisualState.Idle);
            }

            return;
        }

        SetState(inZone || insideWindow ? LeashVisualState.Hover : LeashVisualState.Idle);
    }

    /// <summary>
    /// 由宿主每 tick（40ms）调用：**只判定目标厚度**，不推进动画。
    /// 目标变化时重置过渡起点；推进交给 <see cref="AdvanceAnimation"/>（动画时钟 16ms）。
    /// </summary>
    public void Tick()
    {
        var area = _slot.Screen.WorkingArea;
        var collapsed = Metrics.Px(Metrics.CollapsedThickness, Dpi);
        var expanded = Metrics.Px(Metrics.ExpandedThickness, Dpi);
        var target = EdgeGeometry.IsExpanded(_state) ? expanded : collapsed;

        // 拖放接收期：窗口必须撑到磁铁吸附半径。否则用户停在离绳子 50dip 处时，
        // 视觉上已经贴住，光标却在窗口外，OLE 收不到任何事件——放不下去。
        if (_dropReceive)
        {
            target = Math.Max(target, Metrics.Px(Metrics.MagnetSnapZone, Dpi));
        }

        if (target != _animToPx)
        {
            // 目标变了：以当前实际厚度为起点重新开始过渡（支持中途反向）。
            _animFromPx = _thicknessPx;
            _animToPx = target;
            _animT = 0f;
        }

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
        }
    }

    /// <summary>是否处于厚度过渡中（宿主据此启动动画时钟）。</summary>
    public bool IsTransitioning => _animT < 1f && _animFromPx != _animToPx;

    /// <summary>
    /// 由宿主在过渡期以动画时钟（<see cref="Metrics.AnimFrameIntervalMs"/>）调用：
    /// 按时间进度推进 ease-out，返回是否仍在过渡。
    /// 与原实现一致：**只有厚度真正变化时才 SetBounds / Invalidate**。
    /// </summary>
    /// <returns>true = 还需继续调用；false = 已到底或无需动画。</returns>
    public bool AdvanceAnimation()
    {
        if (_animT >= 1f || _animFromPx == _animToPx)
        {
            return false;
        }

        var step = (float)(Metrics.AnimFrameIntervalMs / (double)Metrics.AnimDurationMs);
        _animT = Math.Min(1f, _animT + step);

        var eased = 1f - MathF.Pow(1f - _animT, Metrics.EasePower);
        var next = (int)Math.Round(_animFromPx + ((_animToPx - _animFromPx) * eased));

        if (next == _thicknessPx)
        {
            return _animT < 1f;   // 这一帧厚度没变（步长不足 1px），过渡未完
        }

        _thicknessPx = next;

        var area = _slot.Screen.WorkingArea;
        Bounds = EdgeGeometry.ComputeWithThickness(Edge, area, next);

        var collapsed = Metrics.Px(Metrics.CollapsedThickness, Dpi);
        var expanded = Metrics.Px(Metrics.ExpandedThickness, Dpi);
        var span = Math.Max(1, expanded - collapsed);
        var ratio = Math.Clamp((_thicknessPx - collapsed) / (float)span, 0f, 1f);
        var opacity = Metrics.CollapsedOpacity + ((Metrics.ExpandedOpacity - Metrics.CollapsedOpacity) * ratio);
        if (Math.Abs(opacity - _lastOpacity) > float.Epsilon)
        {
            Opacity = opacity;
            _lastOpacity = opacity;
        }

        Invalidate();
        return _animT < 1f;
    }

    /// <summary>卡片集合变化后刷新（不改变状态）。</summary>
    public void RefreshItems()
    {
        _hoverIndex = -1;
        _scrollPx = EdgeLayout.ClampScroll(Edge, ClientRectangle, Dpi, _store.ForEdge(Edge), TextStyle, _scrollPx);
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

        // 诊断：放在所有门控**之前**。之前埋在门控后面，导致动画早期帧根本不写日志，
        // 排查「贴绳黑线」时看不到关键那几帧。现在只要厚度变了就记一行。
        if (_thicknessPx != _diagLastPaintThickness)
        {
            _diagLastPaintThickness = _thicknessPx;
            DragDiag("Paint edge=" + Edge + " thick=" + _thicknessPx + " client=" + client.Width + "x" + client.Height
                + " state=" + _state + " items=" + items.Count + " hover=" + _hoverIndex
                + " opaque=" + _dropOpaque + " dropTarget=" + _dropTarget + " magnet=" + _magnetHighlight);
        }

        if (items.Count == 0)
        {
            return;
        }

        // 收起态不画卡片：窗口现在有 CollapsedThickness(40dip) 宽，
        // EdgeLayout 算出来的卡片矩形仍会和它相交，于是被裁成一条"残留"贴在边上
        // （用户反馈"面板收了一大半，有残留，宽度和拖进去那 40 一样"）。
        // 以前 CollapsedThickness=6 时卡片矩形与窗口不相交，这个问题被掩盖着。
        if (!IsExpanded)
        {
            return;
        }

        // 状态已展开但厚度还在动画中间时也不能画：窗口仍很窄，卡片（196dip）会被裁成一条竖线/横线，
        // 表现为「鼠标靠近时贴着绳子出现灰黑色或灰蓝色的线状频闪」。
        // 原实现不会露这个中间帧，因为状态变更与厚度推进在同一个 40ms tick 内完成；
        // 现在厚度由 16ms 动画时钟独立推进，必须用**几何**而不是状态来门控。
        var expandedPx = Metrics.Px(Metrics.ExpandedThickness, Dpi);
        if (_thicknessPx < (expandedPx * Metrics.CardRevealRatio))
        {
            return;
        }

        var slots = EdgeLayout.Compose(Edge, client, Dpi, items, TextStyle, _scrollPx);

        // 诊断：把卡片的确切矩形打一次（每实例一次）。自动化和人工排查都需要它——
        // 之前靠 CardPadding/CardHeight 手算坐标，结果点到了透明像素上，点击直接穿透。
        if (!_diagSlotLogged)
        {
            _diagSlotLogged = true;
            foreach (var slot in slots)
            {
                DragDiag("Slot edge=" + Edge + " idx=" + slot.Index + " bounds=" + slot.Bounds
                         + " client=" + client + " scroll=" + _scrollPx);
            }
        }

        // 诊断：只在厚度变化时记一行，用来对齐「用户看到频闪的那一刻」与当时真实几何。
        if (_thicknessPx != _diagLastPaintThickness)
        {
            _diagLastPaintThickness = _thicknessPx;
            DragDiag("Paint edge=" + Edge + " thick=" + _thicknessPx + " client=" + client.Width + "x" + client.Height
                + " state=" + _state + " items=" + items.Count + " hover=" + _hoverIndex
                + " opaque=" + _dropOpaque + " dropTarget=" + _dropTarget + " magnet=" + _magnetHighlight);
        }

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

            CardRenderer.Draw(g, slot.Bounds, items[slot.Index], Dpi, slot.Index == _hoverIndex, _thumbnails, _paths,
                _settings.TextFontSize, dropHighlighted: _dropTarget || _magnetHighlight);
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

        var slots = EdgeLayout.Compose(Edge, client, Dpi, items, TextStyle, _scrollPx);
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
        var shouldDraw = _dropTarget || _magnetHighlight;

        // 只在"画/不画"切换时打一次，不刷屏：
        // 用它区分用户看到的"灰色框"到底是 DrawSurface 的深蓝，还是 SetDropOpaque 的近黑。
        if (shouldDraw != _lastDrewSurface)
        {
            _lastDrewSurface = shouldDraw;
            DragDiag("DrawSurface " + shouldDraw + " edge=" + Edge
                + " drop=" + _dropTarget + " magnet=" + _magnetHighlight
                + " count=" + _store.ForEdge(Edge).Count
                + " state=" + _state);
        }

        if (!shouldDraw)
        {
            return;
        }

        // 有卡片时不再铺满整窗：拖入高亮改由每张卡自己画（CardRenderer.DropHighlighted），
        // 卡片之间的缝保持透明——这样看起来才是「一张张独立的卡」而不是一整条面板。
        // 空边仍然铺满，否则用户看不出这条边可以放东西。
        if (_store.ForEdge(Edge).Count > 0)
        {
            return;
        }

        // 拖入经过 / 磁铁吸附都用深蓝（"能放这里"）。
        using var brush = new SolidBrush(Theme.SurfaceHover);
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
        using var pen = new Pen(
            _dropHighlight ? Theme.DropHighlight : Theme.EdgeHighlight,
            Metrics.PxFloat(Metrics.RopeStrokeWidth, Dpi));

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
    private const long DragDiagMaxBytes = 1024 * 1024; // metrics-ok: 日志字节上限，与像素/尺寸无关

    private static void DragDiag(string message)
    {
        try
        {
            var dir = Path.Combine(AppPaths.Default.Root, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "drag-diag.log");

            // 无上限的追加在长时间运行下会无界增长（实测启动阶段约 200 字节/秒）。
            // 超过上限就截断重写，只留一行标记，避免日志成了磁盘黑洞。
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= DragDiagMaxBytes)
            {
                File.WriteAllText(
                    path,
                    string.Format(
                        "[{0:yyyy-MM-dd HH:mm:ss}] log truncated: exceeded {1} bytes{2}",
                        DateTime.Now,
                        DragDiagMaxBytes,
                        Environment.NewLine));
            }

            File.AppendAllText(
                path,
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
                try
                {
                    CommitInternalDrag();
                }
                finally
                {
                    // 高亮与按下次态无论提交是否出错都要复位：残留的 _magnetHighlight
                    // 会让 DrawSurface 每帧填满窗口，收起态就剩一条 40dip 宽的实色残留。
                    _internalDrag = false;
                    _insertIndex = -1;
                    SetDragTarget(null);
                    Invalidate();
                }
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

        // 零延迟拖出：光标一离开本窗口就交给系统拖放。
        //
        // 不依赖 OnMouseLeave —— 实测拖拽期间它根本不触发：日志里 25 次 MouseLeave
        // 全部是 pressed=-1（普通移出窗口），拖拽中一次都没有，所以那个交接从来没跑过。
        // 这里用已经每 40ms 跑一次的轮询判断窗口矩形，最坏多等 40ms，
        // 比原来 PollDrag 末尾的 400ms 兜底快一个数量级。
        if (Metrics.ZeroLatencyDragOut && !Bounds.Contains(screenCursor))
        {
            var leaveIndex = _pressedIndex;
            _internalDrag = false;
            _insertIndex = -1;
            SetDragTarget(null);
            _pressedIndex = -1;
            Invalidate();
            StartDragOut(leaveIndex);
            return;
        }

        _internalDrag = true;

        if (IsOverAnyLeash(screenCursor, out var target))
        {
            // 磁铁高亮是「跨边落点提示」：命中的是自己（同边排序）时不点亮。
            // 否则单击卡片时手抖超过系统拖拽阈值也会走到这里，
            // DrawSurface 会把整块面板填成深蓝——就是用户反馈的"点一下闪一下蓝线"。
            SetDragTarget(MagnetPolicy.ShouldHighlight(target, this) ? target : null);
            _insertIndex = ReferenceEquals(target, this) ? ComputeInsertIndexAt(client) : -1;
            Invalidate();
            return;
        }

        // 光标离开所有吸附区（屏幕中部这片空白）。这里要区分两件事：
        //
        //   跨边移动：终点是另一条绳子 → 全程内部轮询（OLE 同进程命中不可靠，见下）；
        //   拖出到外部应用：终点是别的程序的窗口 → 必须交给 OLE（跨进程是可靠的，
        //     实测 DoDragDrop 返回 Copy，通达信能接住）。
        //
        // 判据不用修饰键，也不用「离开绳子就算」——跨边拖拽必然经过这片空白。
        // 用「光标停在**别的程序**的窗口上、且没在移动」：跨边是一口气划过去，
        // 拖出时用户会对准目标停住。移动超过 3px 就重置计时，慢速划过也不会误判。
        //
        // 实测依据（2026-11-15 22:16–22:17，四次）：从这里直接调 DoDragDrop 时，
        // 返回值是 Copy 说明会话启动、也确实有目标接住，但本进程四条绳子一条
        // DragEnter 都收不到（目标边撑到 90dip + 不透明、源窗口也不透明，全试过）。
        // 所以「拖到别的绳子」绝不能走 OLE，而「拖到别的程序」只能走 OLE。
        var movedNow = Math.Abs(screenCursor.X - _lastPollCursor.X) > Metrics.DragIdleJitterPx
                       || Math.Abs(screenCursor.Y - _lastPollCursor.Y) > Metrics.DragIdleJitterPx;
        _lastPollCursor = screenCursor;

        if (!IsForeignWindowUnderCursor(screenCursor))
        {
            _outsideIdleMs = 0;
            return;
        }

        if (movedNow)
        {
            _outsideIdleMs = 0;
            return;
        }

        _outsideIdleMs += Metrics.CursorProbeIntervalMs;
        if (_outsideIdleMs < Metrics.DragOutGraceMs)
        {
            return;
        }

        // 停在外部窗口上够久了：判定为「拖出来」，交给 OLE。
        var index = _pressedIndex;
        _internalDrag = false;
        _insertIndex = -1;
        _outsideIdleMs = 0;
        SetDragTarget(null);
        _pressedIndex = -1;
        Invalidate();
        StartDragOut(index);
    }

    /// <summary>
    /// 光标下方是不是**别的程序**的窗口（桌面、任务栏也算别的程序）。
    /// 用来把「拖到自家另一条绳子」和「拖到外部应用」分开，不需要任何修饰键。
    /// </summary>
    private static bool IsForeignWindowUnderCursor(Point screenPoint)
    {
        var hwnd = WindowFromPoint(screenPoint);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 && pid != (uint)Environment.ProcessId;
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
        SetDragTarget(null);
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

        // 只有「左键已经松开」才清拖拽状态——那才是这次拖拽真的被打断了
        // （Alt+Tab、Esc、别的窗口抢捕获）。清理本身是必须的：
        // 残留的 _magnetHighlight 会让 DrawSurface 每帧填满窗口，收起态就是一条实色残留
        // （LeashDragCleanupTests 守着这条）。
        //
        // 但**鼠标只是移出窗口时左键仍按着**，这里绝不能清：清掉 _pressedIndex 会让
        // OnMouseLeave 里「离开绳子就交给 OLE」直接早退（日志实测 TryHandOff 一条都没有），
        // 同时 PollDrag 的磁铁吸附也失去起点。
        if (!Capture && _internalDrag && (Control.MouseButtons & MouseButtons.Left) == 0)
        {
            _internalDrag = false;
            _insertIndex = -1;
            SetDragTarget(null);
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
    private bool IsOverAnyLeash(Point screenPoint, out LeashForm? target)
    {
        target = null;

        // 迟滞：已经吸附的边，只要光标还在它的感应区内就继续保持，不重新比距离。
        //
        // 为什么必须这么做：四条边复用同一个 IsCursorInTriggerZone 判据，
        // Left 只看 X、Top 只看 Y，两条判据互不排斥——屏幕左上角会让两条边**同时**满足条件，
        // 距离值只差几个像素。光标轻微抖动就会让「最近的一条」在两者之间来回翻转，
        // 表现为两条边的磁铁高亮反复点亮又熄灭，也就是用户反馈的「弹好几下线、闪得太明显」。
        // 右下角（Right + Bottom）同理。
        if (_dragTargetForm is not null && IsInMagnetZone(_dragTargetForm, screenPoint))
        {
            target = _dragTargetForm;
            return true;
        }

        var best = int.MaxValue;

        foreach (Form form in Application.OpenForms)
        {
            if (form is not LeashForm leash || !leash.Visible)
            {
                continue;
            }

            var area = leash.WorkingArea;
            var zone = Metrics.Px(Metrics.MagnetSnapZone, leash.Dpi);
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

    /// <summary>光标是否仍落在该绳子的磁铁感应区内（含沿边范围判定）。</summary>
    private static bool IsInMagnetZone(LeashForm leash, Point screenPoint)
    {
        if (!leash.Visible)
        {
            return false;
        }

        var zone = Metrics.Px(Metrics.MagnetSnapZone, leash.Dpi);
        return EdgeGeometry.IsCursorInTriggerZone(leash.Edge, leash.WorkingArea, screenPoint, zone);
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

        var slots = EdgeLayout.Compose(Edge, ClientRectangle, Dpi, items, TextStyle, _scrollPx);
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

        // 鼠标离开窗口 = 卡片被拖出绳子。
        //
        // 这里**暂不**直接交给 OLE：从鼠标消息上下文发起系统拖放时，
        // 同进程的其他绳子是否还能被识别为落点，尚未验证（自动化的点击坐标有偏差，
        // 几次都没真点中卡片，测得不可靠）。而交接一旦发生，跨绳就会依赖那个未知项，
        // 等于把已验证可用的跨绳押上去。
        //
        // 现在的行为：跨绳走内部吸附（已验证），拖出走 PollDrag 里「停在外部窗口上
        // 超过 DragOutGraceMs」的兜底（同样已验证）。等手动拖一次确认 OLE 能认出目标绳，
        // 再把这里换成零延迟交接——那时"以绳子宽度为锚点"就能完全按用户设想工作。
        if (Metrics.ZeroLatencyDragOut)
        {
            DragDiag("MouseLeave edge=" + Edge + " pressed=" + _pressedIndex
                     + " internal=" + _internalDrag + " dragging=" + _dragging);
            TryHandOffToOle();
        }
    }

    /// <summary>
    /// 把正在进行的内部拖拽交给系统拖放。只在「确实拖着卡片」时生效，
    /// 且 <c>_dragging</c> 已经为真时不再重入。
    /// </summary>
    private void TryHandOffToOle()
    {
        if (_dragging || _pressedIndex < 0)
        {
            return;
        }

        // 不能要求 _internalDrag：OnMouseLeave 通常比 PollDrag（40ms 轮询）先到，
        // 那时标志还没置位，交接就永远不会发生。用系统拖拽阈值判断「真的在拖」，
        // 单击后随手移开不会误触发。
        var client = PointToClient(Cursor.Position);
        var dragSize = SystemInformation.DragSize;
        var moved = Math.Abs(client.X - _pressPoint.X) > dragSize.Width
                    || Math.Abs(client.Y - _pressPoint.Y) > dragSize.Height;
        DragDiag("TryHandOff 检查 edge=" + Edge + " pressed=" + _pressedIndex
                 + " moved=" + moved + " internal=" + _internalDrag);
        if (!moved)
        {
            return;
        }

        var items = _store.ForEdge(Edge);
        if (_pressedIndex >= items.Count)
        {
            _pressedIndex = -1;
            return;
        }

        var index = _pressedIndex;
        _internalDrag = false;
        _insertIndex = -1;
        SetDragTarget(null);
        _pressedIndex = -1;
        Invalidate();
        StartDragOut(index);
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
        _scrollPx = EdgeLayout.ClampScroll(Edge, ClientRectangle, Dpi, items, TextStyle, _scrollPx + (direction * step));
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

        var slots = EdgeLayout.Compose(Edge, ClientRectangle, Dpi, items, TextStyle, _scrollPx);
        return EdgeLayout.HitTest(slots, location);
    }

    /// <summary>该点是否应该接收鼠标（展开时整面板，收起时只有绳线附近）。</summary>
    private bool IsInteractiveAt(Point client)
    {
        if (!ClientRectangle.Contains(client))
        {
            return false;
        }

        // 展开时整个面板都可交互：卡片点击、滚动、右键菜单都在这一区。
        if (IsExpanded)
        {
            return true;
        }

        // 收起时只有绳线附近可点，其余交给桌面，避免挡住贴边操作。
        var inset = Metrics.PxFloat(Metrics.RopeEdgeInset, Dpi);
        var tolerance = Metrics.PxFloat(Metrics.RopeStrokeWidth, Dpi)
                        + Metrics.PxFloat(Metrics.RopeHitTolerance, Dpi);

        return Edge switch
        {
            EdgeKind.Left => Math.Abs(client.X - inset) <= tolerance,
            EdgeKind.Right => Math.Abs(client.X - (ClientRectangle.Width - inset)) <= tolerance,
            EdgeKind.Top => Math.Abs(client.Y - inset) <= tolerance,
            _ => Math.Abs(client.Y - (ClientRectangle.Height - inset)) <= tolerance,
        };
    }

    protected override void WndProc(ref Message m)
    {
        // 收起窗口为了接收拖放加宽到了 40dip，但**鼠标点击不该跟着变宽**。
        // 让"不在绳线上的空白区"返回 HTTRANSPARENT，鼠标事件就落到下面的桌面/图标上。
        //
        // OLE 拖放不受影响：它由 RegisterDragDrop 按窗口矩形投递，不查 NCHITTEST，
        // 所以加宽带来的"拖放更容易落上"依然有效，这两件事可以分开。
        if (m.Msg == InteropConstants.WmNcHitTest)
        {
            var packed = m.LParam.ToInt64();
            var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> InteropConstants.HitTestCoordBits) & 0xFFFF));

            if (!IsInteractiveAt(PointToClient(screen)))
            {
                m.Result = (IntPtr)InteropConstants.HtTransparent;
                return;
            }
        }

        base.WndProc(ref m);
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

        var slots = EdgeLayout.Compose(Edge, ClientRectangle, Dpi, items, TextStyle, _scrollPx);
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

        // 关键：DoDragDrop 是**同步模态循环**，一启动就阻塞调用它的那段代码，
        // 所以「拖拽期间四边不透明」必须在进入 OLE **之前**完成。
        //
        // 2026-11-15 探针（.rivet/scratch/probe-crossdrag）三组对照实证：
        // 目标窗保持 TransparencyKey 时收到 0 个 DragEnter；拖放前、以及拖放进行中
        // 切成不透明，DragEnter/DragDrop 全部命中。唯一变量就是透明键——
        // 它抠掉的像素连 OLE 命中一起穿透，与「是否同进程」无关。
        //
        // 只给**光标附近**的那条边取消透明。四条边全设就是用户看到的"四周一圈黑框"。
        // 之后靠 LeashHost.OnTick 每 40ms 按光标位置动态切换——拖到哪条边，哪条才亮。
        foreach (Form open in Application.OpenForms)
        {
            if (open is LeashForm leash
                && !ReferenceEquals(leash, this)
                && leash.IsCursorNearEdge(Cursor.Position))
            {
                leash.SetDropOpaque(true);
            }
        }

        // 源窗口也必须不透明。实测（2026-11-15 22:17）：只把目标边撑到 113px 且不透明时，
        // OLE 仍然一条 DragEnter 都不投递，DoDragDrop 返回 Copy 说明落点被别的进程窗口接走。
        // 源窗口是 TransparencyKey 窗口时，整个拖放会话的可命中区域判定会失效。
        SetDropOpaque(true);

        try
        {
            // 记录 DoDragDrop 的返回值：None 说明 OLE 拖放**根本没启动**（源窗口透明、
            // 鼠标键状态、或在非鼠标消息上下文调用都可能），而不是「启动了但目标没命中」。
            var effect = DragOut.Start(this, item, _paths, _thumbnails, _settings);
            DragDiag("StartDragOut 结束 effect=" + effect);
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

            // 接收期结束：把其他绳子的「撑宽 + 不透明」收回去。
            // 不等 OnTick 兜底——DoDragDrop 期间消息循环是否泵、泵多少帧都不该决定观感。
            foreach (Form open in Application.OpenForms)
            {
                if (open is LeashForm leash && !ReferenceEquals(leash, this))
                {
                    leash.SetDropReceive(false);
                }
            }

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
            DragDiag("DragEnter isDragging=" + DragOut.IsDragging + " edge=" + Edge
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
        drgevent.Effect = DragIn.EffectsFor(drgevent.Data);

        if (DragOut.IsDragging)
        {
            // 自己的卡片跨边拖动：只置落点标记，**不展开**。
            // 展开会让不透明的窗口变成一大块黑框（用户反馈“黑框很宽”）；
            // 保持收起厚度就是一条细带，OLE 照样能命中、松手照样走 MoveAsync。
            _dropTarget = true;
            SchedulePreview(-1);
            Invalidate();
            return;
        }

        // 外部东西（桌面图标、别的程序的文字/文件）：只把绳线点亮当提示，**不展开**。
        // 展开会在你拖图标排列时凭空冒出一整块深蓝挡住操作。
        // 接收不依赖这个：OnDragDrop 只看 drgevent.Data，松手照样能挂上。
        _dropHighlight = true;
        Invalidate();
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);

        // 必须和源端 DoDragDrop 允许的效果**完全一致**：
        // 源只允许 Copy（DragOut.cs:126 的 DoDragDrop(data, Copy)），
        // 这里若返回 Move，OLE 判定不匹配 → 光标变成禁止符号、松手也落不下去。
        drgevent.Effect = DragOut.IsDragging
            ? DragDropEffects.Copy
            : DragIn.EffectsFor(drgevent.Data);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);

        _dropTarget = false;
        _dropHighlight = false;
        SetState(LeashVisualState.Idle);
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);

        // 拖放已经落下：窗口不再处于「外部东西正拖在上面」的状态。
        // 必须在这里复位 —— OLE 在窗口内松手时只发 DragDrop、**不发 DragLeave**，
        // 漏掉这一处 _dropHighlight 会永远为 true：EvaluateCursor 里
        // 「外部拖入中不改状态」的早退每帧生效，表现为
        // 「拖进去过一次之后整条绳子怎么碰都不展开」（用户反馈）。
        _dropHighlight = false;

        DragDiag("DragDrop isDragging=" + DragOut.IsDragging + " edge=" + Edge
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
