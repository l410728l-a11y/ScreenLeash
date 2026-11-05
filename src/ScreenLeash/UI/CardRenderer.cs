using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using ScreenLeash.Core;

namespace ScreenLeash.UI;

/// <summary>
/// 单张卡片的绘制：只负责「给定矩形和卡片，把它画出来」，不持有状态、不读磁盘布局以外的东西。
/// 尺寸全部经 <see cref="Metrics.Px"/> 换算，颜色全部来自 <see cref="Theme"/>——
/// 因此本文件在裸像素门禁下天然合规（有违规会被 NoRawPixelTests 直接拦住）。
/// </summary>
public static class CardRenderer
{
    /// <summary>绘制一张卡片。</summary>
    /// <param name="g">目标 Graphics（调用方负责双缓冲）。</param>
    /// <param name="bounds">卡片矩形（窗口客户区坐标）。</param>
    /// <param name="item">卡片数据。</param>
    /// <param name="dpi">该屏 DPI。</param>
    /// <param name="hovered">是否悬停高亮。</param>
    /// <param name="thumbnails">缩略图缓存（图片卡片用）。</param>
    /// <param name="paths">数据目录（解析缩略图文件名）。</param>
    public static void Draw(
        Graphics g,
        Rectangle bounds,
        Item item,
        int dpi,
        bool hovered,
        Thumbnailer? thumbnails,
        AppPaths paths)
    {
        if (g is null || item is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var previous = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int radius = Metrics.Px(Metrics.CardRadius, dpi);
            using var path = RoundedRect(bounds, radius);

            using (var fill = new SolidBrush(hovered ? Theme.CardHover : Theme.CardBack))
            {
                g.FillPath(fill, path);
            }

            using (var border = new Pen(
                       hovered ? Theme.CardBorderActive : Theme.CardBorder,
                       Metrics.PxFloat(Metrics.StrokeWidth, dpi)))
            {
                g.DrawPath(border, path);
            }

            int padding = Metrics.Px(Metrics.CardPadding, dpi);
            var inner = Rectangle.Inflate(bounds, -padding, -padding);
            if (inner.Width <= 0 || inner.Height <= 0)
            {
                return;
            }

            switch (item.Kind)
            {
                case Kind.Image:
                    DrawImageCard(g, inner, item, dpi, thumbnails, paths);
                    break;
                case Kind.Text:
                    DrawTextCard(g, inner, item, dpi);
                    break;
                default:
                    DrawFileCard(g, inner, item, dpi);
                    break;
            }
        }
        finally
        {
            g.Restore(previous);
        }
    }

    /// <summary>卡片摘要文本（供提示/无障碍与单元测试复用）。</summary>
    public static string Describe(Item item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        return item.Kind switch
        {
            Kind.Text => item.Text ?? string.Empty,
            _ => item.DisplayName ?? Path.GetFileName(item.BlobName ?? string.Empty),
        };
    }

    private static void DrawImageCard(
        Graphics g,
        Rectangle inner,
        Item item,
        int dpi,
        Thumbnailer? thumbnails,
        AppPaths paths)
    {
        var bitmap = thumbnails?.GetOrLoad(paths.ThumbPath(item.ThumbName));
        if (bitmap is not null)
        {
            var target = Fit(bitmap.Width, bitmap.Height, inner);
            g.DrawImage(bitmap, target);
            return;
        }

        // 缩略图缺失（生成失败/尚未就绪）：画一个占位边框，不留黑洞。
        using var pen = new Pen(Theme.CardBorder, Metrics.PxFloat(Metrics.HairlineWidth, dpi));
        g.DrawRectangle(pen, inner);
    }

    private static void DrawTextCard(Graphics g, Rectangle inner, Item item, int dpi)
    {
        var text = (item.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        using var font = new Font(Theme.UiFontFamily, Metrics.PxFloat(Metrics.FontSize, dpi), FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Theme.TextPrimary);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.LineLimit,
        };

        var area = Rectangle.Inflate(inner, -Metrics.Px(Metrics.TextInset, dpi), -Metrics.Px(Metrics.TextInset, dpi));
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        g.DrawString(text, font, brush, area, format);
    }

    private static void DrawFileCard(Graphics g, Rectangle inner, Item item, int dpi)
    {
        var fileName = item.DisplayName ?? Path.GetFileName(item.BlobName ?? string.Empty);
        var extension = Path.GetExtension(fileName);
        var displayName = string.IsNullOrEmpty(fileName) ? "文件" : fileName;

        using var extensionFont = new Font(Theme.MonoFontFamily, Metrics.PxFloat(Metrics.FontSizeTitle, dpi), FontStyle.Bold, GraphicsUnit.Pixel);
        using var nameFont = new Font(Theme.UiFontFamily, Metrics.PxFloat(Metrics.FontSize, dpi), FontStyle.Regular, GraphicsUnit.Pixel);
        using var extensionBrush = new SolidBrush(Theme.TextSecondary);
        using var nameBrush = new SolidBrush(Theme.TextPrimary);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisPath,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        var badge = string.IsNullOrEmpty(extension) ? "FILE" : extension.TrimStart('.').ToUpperInvariant();
        var badgeHeight = Metrics.Px(Metrics.LineHeight, dpi);
        var badgeRect = new Rectangle(inner.Left, inner.Top, inner.Width, badgeHeight);
        g.DrawString(badge, extensionFont, extensionBrush, badgeRect, format);

        var nameTop = badgeRect.Bottom + Metrics.Px(Metrics.ThumbGap, dpi);
        if (nameTop < inner.Bottom)
        {
            var nameRect = new Rectangle(inner.Left, nameTop, inner.Width, inner.Bottom - nameTop);
            g.DrawString(displayName, nameFont, nameBrush, nameRect, format);
        }
    }

    /// <summary>把图片等比缩放到容器内（不放大，居中）。</summary>
    private static Rectangle Fit(int width, int height, Rectangle container)
    {
        if (width <= 0 || height <= 0)
        {
            return container;
        }

        var scale = Math.Min((double)container.Width / width, (double)container.Height / height);
        if (scale > 1)
        {
            scale = 1;
        }

        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        var x = container.Left + ((container.Width - w) >> 1);
        var y = container.Top + ((container.Height - h) >> 1);
        return new Rectangle(x, y, w, h);
    }

    /// <summary>圆角矩形路径（半径过大时自动退化为椭圆，不抛）。</summary>
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = Math.Min(radius << 1, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, Metrics.ArcHalf, Metrics.ArcQuarter);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, Metrics.ArcThreeQuarter, Metrics.ArcQuarter);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, Metrics.ArcQuarter);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, Metrics.ArcQuarter, Metrics.ArcQuarter);
        path.CloseFigure();
        return path;
    }
}
