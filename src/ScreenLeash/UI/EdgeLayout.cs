using System;
using System.Collections.Generic;
using System.Drawing;
using ScreenLeash.Core;

namespace ScreenLeash.UI;

/// <summary>一张卡片在窗口客户区内的位置（索引 + 矩形）。</summary>
/// <param name="Index">卡片在边内序列中的下标。</param>
/// <param name="Bounds">窗口客户区坐标系下的矩形。</param>
public readonly record struct CardSlot(int Index, Rectangle Bounds);

/// <summary>
/// 卡片沿边排列的纯函数：只依赖 <see cref="Metrics"/> 的 DIP 常量，经 <see cref="Metrics.Px"/> 换算，
/// 可对 4 边 × 任意 DPI × 任意数量做单测。窗口只负责把结果画出来。
///
/// <para>上下边沿 X 轴排（卡片宽 <see cref="Metrics.CardWidth"/>、高占满面板）；
/// 左右边沿 Y 轴排（卡片高 <see cref="Metrics.CardHeight"/>、宽占满面板）。
/// 内容超出面板长度时按 <c>scrollPx</c> 平移，<see cref="MaxScrollPx"/> 给出上界。</para>
/// </summary>
public static class EdgeLayout
{
    /// <summary>该边是否横向排列（上下边）。</summary>
    public static bool IsHorizontal(EdgeKind edge) => edge is EdgeKind.Top or EdgeKind.Bottom;

    /// <summary>沿边方向单张卡片的长度（DIP）。</summary>
    public static int SlotExtentDip(EdgeKind edge) => IsHorizontal(edge) ? Metrics.CardWidth : Metrics.CardHeight;

    /// <summary>垂直于边方向单张卡片的长度（DIP）。</summary>
    public static int CrossExtentDip(EdgeKind edge) => IsHorizontal(edge) ? Metrics.CardHeight : Metrics.CardWidth;

    /// <summary>内容总长度（px），含卡片间距；0 张时为 0。</summary>
    public static int ContentLengthPx(EdgeKind edge, int count, int dpi)
    {
        if (count <= 0)
        {
            return 0;
        }

        int slot = Metrics.Px(SlotExtentDip(edge), dpi);
        int gap = Metrics.Px(Metrics.CardGap, dpi);
        return (count * slot) + ((count - 1) * gap);
    }

    /// <summary>面板可视长度（px）：横向边取宽度，纵向边取高度。</summary>
    public static int VisibleLengthPx(EdgeKind edge, Rectangle area) => IsHorizontal(edge) ? area.Width : area.Height;

    /// <summary>最大滚动量（px）；0 表示内容装得下。</summary>
    public static int MaxScrollPx(EdgeKind edge, Rectangle area, int dpi, int count)
    {
        int visible = VisibleLengthPx(edge, area);
        int content = ContentLengthPx(edge, count, dpi);
        return Math.Max(0, content - visible);
    }

    /// <summary>把滚动量夹进合法区间。</summary>
    public static int ClampScroll(EdgeKind edge, Rectangle area, int dpi, int count, int scrollPx)
        => Math.Clamp(scrollPx, 0, MaxScrollPx(edge, area, dpi, count));

    /// <summary>
    /// 计算某条边上所有卡片的矩形（窗口客户区坐标）。
    /// 卡片沿边居中于面板的垂直厚度内：上下边贴顶、左右边贴左，由窗口自身的内边距留白。
    /// </summary>
    /// <param name="edge">所在边。</param>
    /// <param name="clientArea">窗口客户区矩形（通常 0,0,Width,Height）。</param>
    /// <param name="dpi">该屏 DPI。</param>
    /// <param name="count">卡片数量。</param>
    /// <param name="scrollPx">当前滚动量（会被夹紧）。</param>
    public static IReadOnlyList<CardSlot> Compose(EdgeKind edge, Rectangle clientArea, int dpi, int count, int scrollPx)
    {
        if (count <= 0)
        {
            return Array.Empty<CardSlot>();
        }

        int slot = Metrics.Px(SlotExtentDip(edge), dpi);
        int gap = Metrics.Px(Metrics.CardGap, dpi);
        int padding = Metrics.Px(Metrics.CardPadding, dpi);
        int cross = Metrics.Px(CrossExtentDip(edge), dpi);
        int scroll = ClampScroll(edge, clientArea, dpi, count, scrollPx);

        // 跨边方向：面板厚度可能小于卡片标称尺寸（收起动画中间态），此时按可用厚度收缩。
        int panelThickness = IsHorizontal(edge) ? clientArea.Height : clientArea.Width;
        if (cross > panelThickness)
        {
            cross = panelThickness;
        }

        var result = new List<CardSlot>(count);
        for (int i = 0; i < count; i++)
        {
            int along = padding + (i * (slot + gap)) - scroll;
            var bounds = edge switch
            {
                EdgeKind.Top => new Rectangle(clientArea.Left + along, clientArea.Top, slot, cross),
                EdgeKind.Bottom => new Rectangle(clientArea.Left + along, clientArea.Top, slot, cross),
                EdgeKind.Left => new Rectangle(clientArea.Left, clientArea.Top + along, cross, slot),
                _ => new Rectangle(clientArea.Left, clientArea.Top + along, cross, slot),
            };
            result.Add(new CardSlot(i, bounds));
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
