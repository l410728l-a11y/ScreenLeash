using System;
using System.Collections.Generic;
using System.Drawing;
using ScreenLeash.Core;

namespace ScreenLeash.UI;

/// <summary>一张卡片在窗口客户区内的位置（索引 + 矩形）。</summary>
/// <param name="Index">卡片在边内序列中的下标。</param>
/// <param name="Bounds">窗口客户区坐标系下的矩形。</param>
public readonly record struct CardSlot(int Index, Rectangle Bounds);

/// <summary>文字卡的排版参数：字号与最大行数。由 <see cref="AppSettings"/> 的
/// <c>TextFontSize</c> / <c>TextMaxLines</c> 换算而来，布局层不直接依赖设置对象。</summary>
/// <param name="FontSize">字号（DIP）。</param>
/// <param name="MaxLines">最大行数，决定沿边长度的上限。</param>
public readonly record struct TextCardStyle(int FontSize, int MaxLines)
{
    /// <summary>与改动前等价的默认值（字号 11、最多 6 行）。</summary>
    public static TextCardStyle Default { get; } = new(Metrics.FontSizeBaseline, Metrics.TextMaxLinesDefault);
}

/// <summary>
/// 卡片沿边排列的纯函数：只依赖 <see cref="Metrics"/> 的 DIP 常量，经 <see cref="Metrics.Px"/> 换算，
/// 可对 4 边 × 任意 DPI × 任意数量做单测。窗口只负责把结果画出来。
///
/// <para>上下边沿 X 轴排（卡片宽 <see cref="Metrics.CardWidth"/>、高占满面板）；
/// 左右边沿 Y 轴排（卡片高随文字量变化、宽占满面板）。
/// 内容超出面板长度时按 <c>scrollPx</c> 平移，<see cref="MaxScrollPx"/> 给出上界。</para>
///
/// <para><b>变长卡片</b>：左右边的文字卡按行数决定高度（见 <see cref="SlotExtentDip(EdgeKind, Item, TextCardStyle)"/>），
/// 其余卡片仍用固定长度。因此所有长度计算都按 <c>items</c> 逐张累加，不能再按 <c>count × 固定值</c> 估算。</para>
/// </summary>
public static class EdgeLayout
{
    /// <summary>该边是否横向排列（上下边）。</summary>
    public static bool IsHorizontal(EdgeKind edge) => edge is EdgeKind.Top or EdgeKind.Bottom;

    /// <summary>沿边方向单张卡片的标称长度（DIP）——不含文字卡的行数变化。</summary>
    public static int SlotExtentDip(EdgeKind edge) => IsHorizontal(edge) ? Metrics.CardWidth : Metrics.CardHeight;

    /// <summary>
    /// 单张卡沿边方向的长度（DIP）。文字卡在左右边按行数变高；其余情况沿用标称长度。
    /// </summary>
    /// <param name="edge">所在边。</param>
    /// <param name="item">卡片数据。</param>
    /// <param name="style">文字卡排版参数。</param>
    public static int SlotExtentDip(EdgeKind edge, Item? item, TextCardStyle style)
    {
        // 只在左右边变长：左右边的沿边方向是高度，变高符合直觉；
        // 上下边的沿边方向是宽度，变宽观感差且要重算面板厚度，本版保持不变。
        if (item is not null && item.Kind == Kind.Text && !IsHorizontal(edge))
        {
            var fontSize = Math.Clamp(style.FontSize, Metrics.TextFontSizeMin, Metrics.TextFontSizeMax);
            var maxLines = Math.Clamp(style.MaxLines, Metrics.TextMaxLinesMin, Metrics.TextMaxLinesMax);

            var lines = TextMeasure.EstimateTextLines(item.Text, fontSize, maxLines);
            var lineHeightDip = (Metrics.LineHeight * fontSize) / Metrics.FontSizeBaseline;
            var extent = Metrics.TextCardVerticalPadding + (lines * lineHeightDip);

            // 不低于标称高度：短文字卡不会缩成一条，保持沿边视觉节奏一致。
            return Math.Max(extent, Metrics.CardHeight);
        }

        return SlotExtentDip(edge);
    }

    /// <summary>垂直于边方向单张卡片的长度（DIP）。</summary>
    public static int CrossExtentDip(EdgeKind edge) => IsHorizontal(edge) ? Metrics.CardHeight : Metrics.CardWidth;

    /// <summary>内容总长度（px），逐张累加并含卡片间距；0 张时为 0。</summary>
    public static int ContentLengthPx(EdgeKind edge, IReadOnlyList<Item> items, TextCardStyle style, int dpi)
    {
        if (items is null || items.Count == 0)
        {
            return 0;
        }

        var gap = Metrics.Px(Metrics.CardGap, dpi);
        var total = 0;
        for (var i = 0; i < items.Count; i++)
        {
            total += Metrics.Px(SlotExtentDip(edge, items[i], style), dpi);
        }

        return total + ((items.Count - 1) * gap);
    }

    /// <summary>面板可视长度（px）：横向边取宽度，纵向边取高度。</summary>
    public static int VisibleLengthPx(EdgeKind edge, Rectangle area) => IsHorizontal(edge) ? area.Width : area.Height;

    /// <summary>最大滚动量（px）；0 表示内容装得下。</summary>
    public static int MaxScrollPx(EdgeKind edge, Rectangle area, int dpi, IReadOnlyList<Item> items, TextCardStyle style)
    {
        var visible = VisibleLengthPx(edge, area);
        var content = ContentLengthPx(edge, items, style, dpi);
        return Math.Max(0, content - visible);
    }

    /// <summary>把滚动量夹进合法区间。</summary>
    public static int ClampScroll(EdgeKind edge, Rectangle area, int dpi, IReadOnlyList<Item> items, TextCardStyle style, int scrollPx)
        => Math.Clamp(scrollPx, 0, MaxScrollPx(edge, area, dpi, items, style));

    /// <summary>
    /// 计算某条边上所有卡片的矩形（窗口客户区坐标）。
    /// 卡片沿边居中于面板的垂直厚度内：上下边贴顶、左右边贴左，由窗口自身的内边距留白。
    /// </summary>
    /// <param name="edge">所在边。</param>
    /// <param name="clientArea">窗口客户区矩形（通常 0,0,Width,Height）。</param>
    /// <param name="dpi">该屏 DPI。</param>
    /// <param name="items">卡片列表（顺序即沿边顺序）。</param>
    /// <param name="style">文字卡排版参数。</param>
    /// <param name="scrollPx">当前滚动量（会被夹紧）。</param>
    public static IReadOnlyList<CardSlot> Compose(
        EdgeKind edge,
        Rectangle clientArea,
        int dpi,
        IReadOnlyList<Item> items,
        TextCardStyle style,
        int scrollPx)
    {
        if (items is null || items.Count == 0)
        {
            return Array.Empty<CardSlot>();
        }

        var gap = Metrics.Px(Metrics.CardGap, dpi);
        var padding = Metrics.Px(Metrics.CardPadding, dpi);
        var cross = Metrics.Px(CrossExtentDip(edge), dpi);
        var scroll = ClampScroll(edge, clientArea, dpi, items, style, scrollPx);

        // 跨边方向：面板厚度可能小于卡片标称尺寸（收起动画中间态），此时按可用厚度收缩。
        var panelThickness = IsHorizontal(edge) ? clientArea.Height : clientArea.Width;
        if (cross > panelThickness)
        {
            cross = panelThickness;
        }

        var result = new List<CardSlot>(items.Count);
        var cursor = padding - scroll;

        for (var i = 0; i < items.Count; i++)
        {
            var extent = Metrics.Px(SlotExtentDip(edge, items[i], style), dpi);
            var bounds = edge switch
            {
                EdgeKind.Top => new Rectangle(clientArea.Left + cursor, clientArea.Top, extent, cross),
                EdgeKind.Bottom => new Rectangle(clientArea.Left + cursor, clientArea.Top, extent, cross),
                EdgeKind.Left => new Rectangle(clientArea.Left, clientArea.Top + cursor, cross, extent),
                _ => new Rectangle(clientArea.Left, clientArea.Top + cursor, cross, extent),
            };
            result.Add(new CardSlot(i, bounds));
            cursor += extent + gap;
        }

        return result;
    }

    /// <summary>
    /// 拖动排序时算「插入到第几个位置」（0..count）。纯函数、可单测。
    /// 规则：从左/上往右/下扫，光标落在某张卡片**中心之前**就插在它前面；都不满足则插到末尾。
    /// </summary>
    /// <param name="slots">当前布局（<see cref="Compose"/> 的结果）。</param>
    /// <param name="point">光标位置（窗口客户区坐标）。</param>
    /// <param name="horizontal">true = 横向排列（上下边）。</param>
    public static int ComputeInsertIndex(IReadOnlyList<CardSlot> slots, Point point, bool horizontal)
    {
        if (slots is null || slots.Count == 0)
        {
            return 0;
        }

        for (var i = 0; i < slots.Count; i++)
        {
            var bounds = slots[i].Bounds;
            var center = horizontal
                ? bounds.Left + (bounds.Width >> 1)
                : bounds.Top + (bounds.Height >> 1);
            var cursor = horizontal ? point.X : point.Y;

            if (cursor < center)
            {
                return i;
            }
        }

        return slots.Count;
    }

    /// <summary>命中的卡片下标；未命中返回 -1。</summary>
    public static int HitTest(IReadOnlyList<CardSlot> slots, Point point)
    {
        if (slots is null)
        {
            return -1;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Bounds.Contains(point))
            {
                return slots[i].Index;
            }
        }

        return -1;
    }
}
