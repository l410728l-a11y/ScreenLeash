using System;
using System.Drawing;
using ScreenLeash.Core;

namespace ScreenLeash.UI;

/// <summary>
/// 放大预览窗的位置计算：纯函数、不碰窗体和 GDI+，可直接单测。
/// 规则：预览**贴在绳子内侧**、沿边方向居中对齐卡片，最后整体夹回工作区，
/// 保证无论卡片在屏幕哪个位置、预览多大，都不会跑出屏幕。
/// </summary>
public static class PreviewGeometry
{
    /// <summary>
    /// 计算预览窗矩形。
    /// </summary>
    /// <param name="anchor">当前悬停卡片的屏幕矩形。</param>
    /// <param name="imageSize">图片原始像素尺寸（未缩放）。</param>
    /// <param name="workingArea">所在屏幕的工作区。</param>
    /// <param name="dpi">该屏 DPI。</param>
    /// <param name="edge">卡片所在的绳边，决定预览放在锚点的哪一侧。</param>
    public static Rectangle Compute(Rectangle anchor, Size imageSize, Rectangle workingArea, int dpi, EdgeKind edge)
    {
        var margin = Metrics.Px(Metrics.PreviewMarginDip, dpi);
        var maxWidth = Math.Max(1, workingArea.Width - (margin << 1));
        var maxHeight = Math.Max(1, workingArea.Height - (margin << 1));

        var scaled = FitInside(imageSize, maxWidth, maxHeight);
        if (scaled.Width <= 0 || scaled.Height <= 0)
        {
            return Rectangle.Empty;
        }

        int x;
        int y;
        switch (edge)
        {
            case EdgeKind.Right:
                x = anchor.Left - scaled.Width - margin;
                y = anchor.Top + ((anchor.Height - scaled.Height) >> 1);
                break;
            case EdgeKind.Left:
                x = anchor.Right + margin;
                y = anchor.Top + ((anchor.Height - scaled.Height) >> 1);
                break;
            case EdgeKind.Top:
                x = anchor.Left + ((anchor.Width - scaled.Width) >> 1);
                y = anchor.Bottom + margin;
                break;
            default:
                x = anchor.Left + ((anchor.Width - scaled.Width) >> 1);
                y = anchor.Top - scaled.Height - margin;
                break;
        }

        x = Math.Clamp(x, workingArea.Left + margin, Math.Max(workingArea.Left + margin, workingArea.Right - scaled.Width - margin));
        y = Math.Clamp(y, workingArea.Top + margin, Math.Max(workingArea.Top + margin, workingArea.Bottom - scaled.Height - margin));

        return new Rectangle(x, y, scaled.Width, scaled.Height);
    }

    /// <summary>等比缩放到不超过 (maxWidth, maxHeight)，且高度不超过工作区的
    /// <see cref="Metrics.PreviewMaxHeightRatio"/> 上限；不放大。</summary>
    public static Size FitInside(Size image, int maxWidth, int maxHeight)
    {
        if (image.Width <= 0 || image.Height <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return Size.Empty;
        }

        var heightCap = Math.Max(1, (int)Math.Round(maxHeight * Metrics.PreviewMaxHeightRatio));
        var limitWidth = Math.Min(maxWidth, image.Width);
        var limitHeight = Math.Min(maxHeight, heightCap);

        var scale = Math.Min((double)limitWidth / image.Width, (double)limitHeight / image.Height);
        if (scale <= 0)
        {
            return Size.Empty;
        }

        if (scale > 1)
        {
            scale = 1;
        }

        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return new Size(width, height);
    }
}
