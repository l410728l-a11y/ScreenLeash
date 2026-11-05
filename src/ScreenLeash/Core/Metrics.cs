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
    public const int CollapsedThickness = 6;

    /// <summary>展开时向屏内伸出的厚度（DIP）。</summary>
    public const int ExpandedThickness = 140;

    /// <summary>光标进入距边多少 DIP 内视为「拖近/靠近」触发弹出。</summary>
    public const int EdgeTriggerZone = 40;

    /// <summary>绳子沿边方向占工作区的比例（0..1）。0.8 = 上下（左右边则为上下）各留 10%，
    /// 不贴满整条边——用户要求「左右的上下不要打满」。</summary>
    public const float EdgeSpanRatio = 0.8f;

    // ── 卡片 ─────────────────────────────────────────────────────
    /// <summary>卡片圆角半径（DIP）。</summary>
    public const int CardRadius = 10;

    /// <summary>卡片高度（DIP）。</summary>
    public const int CardHeight = 96;

    /// <summary>卡片宽度（DIP）；图片/文件/文本卡片同宽，保证沿边排列整齐。</summary>
    public const int CardWidth = 168;

    /// <summary>缩略图边长（DIP）。</summary>
    public const int ThumbSize = 88;

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
    public const int LineHeight = 16;

    /// <summary>文件图标边长（DIP）。</summary>
    public const int IconSize = 32;

    /// <summary>小圆角（悬停按钮等，DIP）。</summary>
    public const int CornerRadiusSmall = 4;

    // ── 描边与字体 ────────────────────────────────────────────────
    /// <summary>主描边宽度（DIP）。</summary>
    public const float StrokeWidth = 2f;

    /// <summary>发丝线宽度（DIP），用于贴边细线描边。</summary>
    public const float HairlineWidth = 1f;

    /// <summary>正文字号（DIP）。9.5 比原来的 8.5 更易读——用户反馈黑底上"显示不了什么字"。</summary>
    public const float FontSize = 9.5f;

    /// <summary>辅助字号（DIP）。</summary>
    public const float FontSizeSmall = 7.5f;

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
