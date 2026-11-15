using System;

namespace ScreenLeash.Core;

/// <summary>
/// 视觉度量单一来源。全部常量以 DIP（96 DPI 基准下的 1/96 英寸）命名，
/// 设备像素一律通过 <see cref="Px"/> / <see cref="PxFloat"/> 换算，绘制代码不得自带数字。
/// 本文件是数字的唯一合法归属地；门禁扫描器只约束 UI/ 与 Interop/，
/// 因此这里的常量值既不被判定为违规，也不可被复制到别处。
/// </summary>
public static class Metrics
{
    /// <summary>DIP 基准 DPI。Windows 的 100% 缩放。</summary>
    public const int DpiBaseline = 96;

    // ── 绳子（窗口本体）────────────────────────────────────────────
    /// <summary>收起时贴边的细线厚度（DIP）。</summary>
    /// <summary>收起态的窗口厚度。视觉上仍只画一条 2.5dip 的细线（RopeStrokeWidth），
    /// 但窗口本身留出这个宽度作为拖放接收区：OLE 只把 DragEnter 发给**光标所在的那个窗口**，
    /// 窗口只有 6dip 时用户必须精确落到线上才能挂。
    /// 这条加宽是否真的有效，取决于 TransparencyKey 的透明像素会不会一起把拖放穿透——
    /// 若无效（拖上去仍无反应），说明必须改用 layered window（每像素 alpha，透明也接收）。</summary>
    public const int CollapsedThickness = 40;

    /// <summary>展开时向屏内伸出的厚度（DIP）。</summary>
    public const int ExpandedThickness = 140;

    /// <summary>光标进入距边多少 DIP 内视为「拖近/靠近」触发弹出。</summary>
    public const int EdgeTriggerZone = 40;

    /// <summary>拖动**起点**距边多少 DIP 内冻结展开响应（防止在边缘附近选中/拖图标时绳子弹出来挡路）。
    /// 实测：140 太小（左边仍弹）、280 左边明显好转、400 过头（把正常操作也冻住），当前取 280。</summary>
    public const int DragFreezeWidth = 280;

    /// <summary>绳子沿边方向占工作区的比例（0..1）。0.8 = 上下（左右边则为上下）各留 10%，
    /// 不贴满整条边——用户要求「左右的上下不要打满」。</summary>
    public const float EdgeSpanRatio = 0.8f;

    /// <summary>拖放诊断日志采样次数（只打前几次 Move，够定位又不刷屏）。</summary>
    public const int DiagMoveSamples = 6;

    /// <summary>拖动排序时插入指示线的宽度（DIP）。</summary>
    public const float IndicatorWidth = 3f;

    /// <summary>拖拽靠近时的感应区（DIP）：比普通悬停的 40 更宽，
    /// 让用户拖东西时有足够空间提前看到「能放这里」的底框，而不是必须贴到边上。</summary>
    /// <summary>拖东西靠近时判定"能挂"的范围（用户定：40dip）。
    /// 与悬停用的 EdgeTriggerZone 分开命名，是为了以后调其中一个不会牵动另一个。</summary>
    public const int DragApproachZone = 40;

    /// <summary>跨边互拖时的磁铁吸附半径（DIP）。比 DragApproachZone 宽，因为拖卡片时希望更容易"够到"对侧。</summary>
    public const int MagnetSnapZone = 90;

    /// <summary>
    /// 拖出到外部应用的宽限（毫秒）：光标停在**别的程序**窗口上并保持不动这么久，
    /// 才判定为「拖出来」并交给 OLE。
    ///
    /// <para>为什么不是按修饰键，也不是「离开绳子就算」：跨边拖拽必然经过屏幕中部那片空白，
    /// 一离开就交出去会把卡片丢给路过的桌面/应用。用「停住」当判据，
    /// 跨边时是一口气划过去（移动就重置计时），拖出时用户会对准目标停住。</para>
    /// </summary>
    public const int DragOutGraceMs = 400;

    /// <summary>
    /// 判定「光标停住」的抖动容忍（像素）：一帧内位移不超过这个值就算没在移动。
    /// 拖出宽限计时只在「没在移动」时累加，避免慢速划过屏幕中部被误判成拖出来。
    /// </summary>
    public const int DragIdleJitterPx = 3;

    /// <summary>
    /// 鼠标一离开绳子就把卡片交给系统拖放（零延迟拖出）。
    ///
    /// <para><b>默认开启</b>：拖到微信/桌面/别的程序时和原来手感一致，一离开绳子就能看到
    /// 系统的拖放指针。环境变量 <c>SCREENLEASH_ZERO_LATENCY_DRAGOUT=0</c> 可关掉，
    /// 退回「跨绳内部吸附 + 停在外部窗口 DragOutGraceMs 才拖出」的保守版本。</para>
    ///
    /// <para><b>开启后的已知风险</b>：跨绳能否成立取决于「系统拖放是否认得本进程自己的
    /// 另一条绳子」。跨进程（微信/桌面）是可靠的；同进程从定时器回调发起时实测四次都
    /// 收不到 DragEnter，从鼠标消息上下文（这里）发起还没测出来。</para>
    /// </summary>
    public static readonly bool ZeroLatencyDragOut = !string.Equals(
        Environment.GetEnvironmentVariable("SCREENLEASH_ZERO_LATENCY_DRAGOUT"),
        "0",
        StringComparison.Ordinal);

    // 注：曾加过 DragOutGraceMs / DragOutDistance 两个「何时把卡片交给系统拖放」的判据，
    // 2026-11-15 证实真因是「透明键把 OLE 拖放命中一起穿透」——目标边根本收不到 DragEnter。
    // 修法在 LeashHost.OnTick：拖自己卡片时四条边强制不透明。判据本身不再需要，已移除。

    // ── 卡片 ─────────────────────────────────────────────────────
    /// <summary>卡片圆角半径（DIP）。</summary>
    public const int CardRadius = 10;

    /// <summary>卡片高度（DIP）。</summary>
    public const int CardHeight = 112;

    /// <summary>卡片宽度（DIP）；图片/文件/文本卡片同宽，保证沿边排列整齐。</summary>
    public const int CardWidth = 196;

    /// <summary>缩略图边长（DIP）。不能超过卡片内区高度（112 - 2×10 = 92），否则会缩小显示。</summary>
    public const int ThumbSize = 92;

    /// <summary>缩略图最小边长（DIP），低于此值不绘制缩略图只画图标。</summary>
    public const int ThumbMinSize = 24;

    /// <summary>卡片内边距（DIP）。</summary>
    public const int CardPadding = 10;

    /// <summary>卡片之间的间距（DIP）。</summary>
    public const int CardGap = 6;

    /// <summary>缩略图与文字之间的间距（DIP）。</summary>
    public const int ThumbGap = 8;

    /// <summary>文本摘要最多显示行数。</summary>
    public const int TextLines = 3;

    // ── 几何（GDI+ 路径用）─────────────────────────────────────────
    // ── 绳子外观 ─────────────────────────────────────────────────
    /// <summary>绳子弧线的下垂量（DIP）：沿边中段向屏内偏移多少，形成"绳"的弧度。</summary>
    public const float RopeSagDip = 6f;

    /// <summary>绳子描边宽度（DIP）。</summary>
    public const float RopeStrokeWidth = 2.5f;

    /// <summary>收起态下绳线周围多少 DIP 内仍算"可点"（超出就穿透给桌面）。
    /// 收起窗口为了接收拖放加宽到了 CollapsedThickness，但点击不该跟着变宽——
    /// 否则会挡住贴边的桌面操作（用户反馈"收回时也跟着变宽了"）。</summary>
    public const float RopeHitTolerance = 3f;

    /// <summary>绳子离窗口边的内缩（DIP），避免线压在窗口边界上被裁掉一半。</summary>
    public const float RopeEdgeInset = 2f;

    /// <summary>贝塞尔控制点靠近起点的位置比例（0..1）。</summary>
    public const float RopeCuspRatioNear = 0.33f;

    /// <summary>贝塞尔控制点靠近终点的位置比例（0..1）。</summary>
    public const float RopeCuspRatioFar = 0.67f;

    /// <summary>渐变方向：水平（从左到右，0°）。</summary>
    public const float GradientAngleRightward = 0f;

    /// <summary>渐变方向：垂直（从上到下，90°）。</summary>
    public const float GradientAngleDownward = 90f;

    // ── 悬停放大预览 ─────────────────────────────────────────────
    /// <summary>鼠标停在一张卡片上多久后弹出放大预览（毫秒）。太短会在划过时乱闪。</summary>
    public const int PreviewDelayMs = 420;

    /// <summary>预览图相对工作区高度的比例上限（0..1）。</summary>
    public const float PreviewMaxHeightRatio = 0.6f;

    /// <summary>预览窗与绳子/屏幕边的间距（DIP）。</summary>
    public const int PreviewMarginDip = 10;

    /// <summary>预览窗圆角（DIP）。</summary>
    public const int PreviewRadiusDip = 12;

    /// <summary>四分之一圆弧角（度），圆角矩形用。</summary>
    public const float ArcQuarter = 90f;

    /// <summary>半圆弧角（度）。</summary>
    public const float ArcHalf = 180f;

    /// <summary>四分之三圆弧角（度）。</summary>
    public const float ArcThreeQuarter = 270f;

    /// <summary>单行文本高度（DIP）。</summary>
    public const int LineHeight = 19;

    /// <summary>文件图标边长（DIP）。</summary>
    public const int IconSize = 32;

    /// <summary>小圆角（悬停按钮等，DIP）。</summary>
    public const int CornerRadiusSmall = 4;

    // ── 描边与字体 ────────────────────────────────────────────────
    /// <summary>主描边宽度（DIP）。</summary>
    public const float StrokeWidth = 2f;

    /// <summary>发丝线宽度（DIP），用于贴边细线描边。</summary>
    public const float HairlineWidth = 1f;

    /// <summary>正文字号（DIP）。用户反馈卡片里文字偏小，9.5 → 11。</summary>
    public const float FontSize = 11f;

    /// <summary>字号基准（DIP）。行高按字号线性折算时的分母：
    /// 行高 DIP = LineHeight × 字号 / FontSizeBaseline。</summary>
    public const int FontSizeBaseline = 11;

    /// <summary>文字卡字号下限（DIP）。</summary>
    public const int TextFontSizeMin = 9;

    /// <summary>文字卡字号上限（DIP）。</summary>
    public const int TextFontSizeMax = 20;

    /// <summary>文字卡显示行数下限。</summary>
    public const int TextMaxLinesMin = 1;

    /// <summary>文字卡显示行数上限（决定文字卡最大沿边长度）。</summary>
    public const int TextMaxLinesMax = 12;

    /// <summary>文字卡显示行数默认值（未设置时的初始上限）。</summary>
    public const int TextMaxLinesDefault = 6;

    /// <summary>文字卡垂直内边距合计（上下各一份，DIP）。文字卡高度 = 本值 + 行数 × 行高。</summary>
    public const int TextCardVerticalPadding = CardPadding * 2;

    /// <summary>文件卡扩展名徽标比正文字号小多少（DIP）。保持层级，但不低于字号下限。</summary>
    public const int FileBadgeSizeDelta = 2;

    /// <summary>
    /// 卡片开始绘制的最小展开比例（0..1）。
    ///
    /// <para>取 0.35：收起 40dip / 展开 140dip ≈ 0.286，0.35 略高于它，
    /// 等价于「展开动画一开始就参与」——卡片随面板块撑开而逐渐显形，这才是丝滑的来源。</para>
    ///
    /// <para>曾取 0.98：卡片只在最后 1-2 帧出现，等于「啪」地跳出，丝滑感全无（用户实测）。</para>
    /// <para>注意：贴边黑线的成因尚未定位（与卡片压扁无关，已在固定宽度版本上复现），
    /// 本值只负责动画观感，不承担那个问题。</para>
    /// </summary>
    public const float CardRevealRatio = 0.35f;

    /// <summary>辅助字号（DIP），文件名/次要信息用。</summary>
    public const float FontSizeSmall = 9f;

    /// <summary>标题字号（DIP）。</summary>
    public const float FontSizeTitle = 10f;

    // ── 绘制偏移 ─────────────────────────────────────────────────
    /// <summary>绘制内容相对窗口的外缩（DIP）。</summary>
    public const int Inset = 4;

    /// <summary>文本相对内容区的外缩（DIP）。</summary>
    public const int TextInset = 8;

    /// <summary>缩略图相对卡片的外缩（DIP）。</summary>
    public const int ThumbInset = 6;

    // ── 交互与动画 ────────────────────────────────────────────────
    /// <summary>UI 线程唯一的显隐/缓动 timer 间隔（毫秒）。</summary>
    public const int AnimIntervalMs = 150;

    // 说明：AnimIntervalMs 已无任何源码引用（Wave 0 实测 grep 只命中定义本身），
    // 保留是为了不打无关 diff。下面三个常量是现行动画时钟的真实参数。

    /// <summary>动画刷新间隔（毫秒）。16ms ≈ 60fps，只在过渡期运行；
    /// 与光标探测（<see cref="CursorProbeIntervalMs"/>）解耦，空闲时不启动。</summary>
    public const int AnimFrameIntervalMs = 16;

    /// <summary>展开/收起总时长（毫秒）。原实现 5 帧 × 40ms = 200ms，这里取 180ms 略快。</summary>
    public const int AnimDurationMs = 180;

    /// <summary>ease-out 幂次：3 = 三次方收尾（前快后慢）。</summary>
    public const int EasePower = 3;

    /// <summary>缓动除数：每 tick 移动距离 = max(MinEaseStep, |Δ| / EaseDivisor)。
    /// 2 比 3 收敛更快：134dip 的展开从约 8 帧降到 5 帧。</summary>
    public const int EaseDivisor = 2;

    /// <summary>单次缓动的最小步长（DIP）。</summary>
    public const int MinEaseStep = 8;

    /// <summary>溢出内容的滚轮单步滚动量（DIP）。</summary>
    public const int ScrollStep = 40;

    /// <summary>光标位置探测间隔（毫秒）。40ms 是「弹出跟手」与「空闲开销」的折中：
    /// 120ms 时展开动画要 8-9 帧、约 1 秒才到底，用户感知为卡顿。</summary>
    public const int CursorProbeIntervalMs = 40;

    // ── 透明度 ───────────────────────────────────────────────────
    /// <summary>收起态窗口不透明度。1.0 = 不做整窗透明：收起时只有一条亮色绳线，
    /// 再叠透明度反而把线也削淡了。</summary>
    public const float CollapsedOpacity = 1f;

    /// <summary>展开态窗口不透明度。1.0 = 不做整窗透明：卡片和文字必须保持原色，
    /// 黑底上再降 7% 会明显发灰（用户反馈"显示不了什么字"）。</summary>
    public const float ExpandedOpacity = 1f;

    /// <summary>悬停卡片不透明度。</summary>
    public const float CardHoverOpacity = 1f;

    // ── 换算 ─────────────────────────────────────────────────────
    /// <summary>把 DIP 换算为设备像素（四舍五入，远离零）。</summary>
    /// <param name="dip">DIP 值，可为负（用于反向偏移）。</param>
    /// <param name="dpi">目标屏幕的 DPI，必须为正。</param>
    public static int Px(int dip, int dpi)
    {
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "dpi 必须为正。");
        }

        return (int)Math.Round(dip * (dpi / (double)DpiBaseline), MidpointRounding.AwayFromZero);
    }

    /// <summary>把 DIP 换算为设备像素（浮点，用于 GDI+ 的线宽/字号）。</summary>
    public static float PxFloat(float dip, int dpi)
    {
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "dpi 必须为正。");
        }

        return (float)(dip * (dpi / (double)DpiBaseline));
    }
}
