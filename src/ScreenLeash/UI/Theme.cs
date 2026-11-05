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
    /// <summary>透明键色：Windows 会把窗口里**完全等于这个颜色**的像素抠成全透明
    /// （连鼠标命中也一起穿透）。选洋红是因为它不会被画到别处。
    /// 副作用：抗锯齿边缘会与洋红混合出花边——所以绳线用不带抗锯齿的绘制。</summary>
    public const uint TransparencyKeyArgb = 0xFFFF00FF;

    /// <summary>绳子本体底色：**几乎全透明**，只给鼠标一个极弱的区域暗示。
    /// 原先 alpha 242 的深灰面板在纯黑桌面上就是一大块"阴影"，挡住后面的内容。</summary>
    public const uint SurfaceArgb = 0x141C1E24;

    /// <summary>窗口 BackColor（WinForms 不接受带 alpha 的颜色）。取近黑，
    /// 在黑色壁纸上几乎看不出窗口边界；真正可见的元素只有卡片和绳线。</summary>
    public const uint SurfaceOpaqueArgb = 0xFF0B0C10;

    /// <summary>拖入经过时的提示底色：**不透明**深蓝。
    /// 半透明填充会与 TransparencyKey 的洋红混合出花边，所以这里必须给实色。</summary>
    public const uint SurfaceHoverArgb = 0xFF1E3A5F;

    /// <summary>**拖拽靠近**（还没进入窗口）时的落点底框：不透明灰蓝，
    /// 告诉用户"松手就放这里"。悬停查看时不画，避免挡视线。</summary>
    public const uint DragApproachArgb = 0xFF2A3040;

    /// <summary>绳线颜色：亮天蓝，在黑色背景上清晰可见。</summary>
    public const uint EdgeHighlightArgb = 0xCC4FC3F7;

    /// <summary>面板边缘的暗色分隔。</summary>
    public const uint EdgeShadowArgb = 0x40202028;

    // ── 卡片 ─────────────────────────────────────────────────────
    /// <summary>卡片底色：明显亮于纯黑背景，让卡片本身有轮廓。</summary>
    public const uint CardBackArgb = 0xFF2A2F3A;

    /// <summary>卡片悬停底色。</summary>
    public const uint CardHoverArgb = 0xFF3A4152;

    /// <summary>卡片边框：比原先更亮，黑底上能看清边界。</summary>
    public const uint CardBorderArgb = 0x66FFFFFF;

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

    public static Color TransparencyKey => FromArgb(TransparencyKeyArgb);

    public static Color SurfaceOpaque => FromArgb(SurfaceOpaqueArgb);

    public static Color SurfaceHover => FromArgb(SurfaceHoverArgb);

    public static Color DragApproach => FromArgb(DragApproachArgb);

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
