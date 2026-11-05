using System.Drawing;

namespace ScreenLeash.UI;

/// <summary>
/// 颜色与字体族的唯一归属地。这里只放「是什么颜色/什么字」，不放尺寸——
/// 尺寸一律在 <see cref="Core.Metrics"/>。所有色值以 0xAARRGGBB 十六进制常量表达，
/// 绘制代码只能引用这里暴露的 <see cref="Color"/> / 字体族名，不得自带颜色字面量。
/// </summary>
public static class Theme
{
    // ── 字体族 ───────────────────────────────────────────────────
    /// <summary>界面主字体族（优先）。</summary>
    public const string UiFontFamily = "Segoe UI";

    /// <summary>中文回退字体族。</summary>
    public const string FallbackFontFamily = "Microsoft YaHei UI";

    /// <summary>等宽字体族（文本类卡片摘要）。</summary>
    public const string MonoFontFamily = "Consolas";

    // ── 窗口/绳子 ─────────────────────────────────────────────────
    /// <summary>绳子本体底色（半透明深色，用于自绘填充）。</summary>
    public const uint SurfaceArgb = 0xF21C1E24;

    /// <summary>绳子本体不透明底色：WinForms 的 Control.BackColor 不接受带 alpha 的颜色，
    /// 窗口透明感由 Form.Opacity 与自绘填充叠加得到。</summary>
    public const uint SurfaceOpaqueArgb = 0xFF1C1E24;

    /// <summary>绳子本体悬停态底色。</summary>
    public const uint SurfaceHoverArgb = 0xF224262E;

    /// <summary>贴边细线的亮色描边。</summary>
    public const uint EdgeHighlightArgb = 0x46FFFFFF;

    /// <summary>面板边缘的暗色分隔。</summary>
    public const uint EdgeShadowArgb = 0x40202028;

    // ── 卡片 ─────────────────────────────────────────────────────
    /// <summary>卡片底色。</summary>
    public const uint CardBackArgb = 0xFF181A20;

    /// <summary>卡片悬停底色。</summary>
    public const uint CardHoverArgb = 0xFF2A2D36;

    /// <summary>卡片边框。</summary>
    public const uint CardBorderArgb = 0x33FFFFFF;

    /// <summary>卡片选中态边框。</summary>
    public const uint CardBorderActiveArgb = 0xCC6EA8FF;

    // ── 文本 ─────────────────────────────────────────────────────
    /// <summary>主文本色。</summary>
    public const uint TextPrimaryArgb = 0xFFE6E6E6;

    /// <summary>次要文本色（文件名、时间）。</summary>
    public const uint TextSecondaryArgb = 0xFF9AA0AC;

    /// <summary>占位/空态文本色。</summary>
    public const uint TextMutedArgb = 0xFF6B7280;

    // ── 状态色 ───────────────────────────────────────────────────
    /// <summary>可放置高亮（拖入经过时）。</summary>
    public const uint DropHighlightArgb = 0xCC6EA8FF;

    /// <summary>危险操作（删除按钮）。</summary>
    public const uint DangerArgb = 0xCCFF6B6B;

    /// <summary>成功/已复制提示。</summary>
    public const uint SuccessArgb = 0xCC6BD98A;

    // ── 颜色访问器 ────────────────────────────────────────────────
    public static Color Surface => FromArgb(SurfaceArgb);

    public static Color SurfaceOpaque => FromArgb(SurfaceOpaqueArgb);

    public static Color SurfaceHover => FromArgb(SurfaceHoverArgb);

    public static Color EdgeHighlight => FromArgb(EdgeHighlightArgb);

    public static Color EdgeShadow => FromArgb(EdgeShadowArgb);

    public static Color CardBack => FromArgb(CardBackArgb);

    public static Color CardHover => FromArgb(CardHoverArgb);

    public static Color CardBorder => FromArgb(CardBorderArgb);

    public static Color CardBorderActive => FromArgb(CardBorderActiveArgb);

    public static Color TextPrimary => FromArgb(TextPrimaryArgb);

    public static Color TextSecondary => FromArgb(TextSecondaryArgb);

    public static Color TextMuted => FromArgb(TextMutedArgb);

    public static Color DropHighlight => FromArgb(DropHighlightArgb);

    public static Color Danger => FromArgb(DangerArgb);

    public static Color Success => FromArgb(SuccessArgb);

    private static Color FromArgb(uint argb) => Color.FromArgb(unchecked((int)argb));
}
