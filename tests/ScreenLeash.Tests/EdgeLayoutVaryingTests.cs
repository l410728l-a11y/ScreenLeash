using System;
using System.Collections.Generic;
using System.Drawing;
using ScreenLeash.Core;
using ScreenLeash.UI;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 变长卡片布局的单元测试：文字卡按行数改变沿边长度后，
/// ContentLengthPx / Compose / MaxScrollPx / HitTest 必须仍然自洽。
/// </summary>
public sealed class EdgeLayoutVaryingTests
{
    private const int Dpi = Metrics.DpiBaseline;   // 96 DPI：DIP 与 px 1:1，便于断言

    private static readonly TextCardStyle Style = new(Metrics.FontSizeBaseline, Metrics.TextMaxLinesDefault);

    private static Item TextItem(string text) => new() { Kind = Kind.Text, Text = text };

    private static Item ImageItem() => new() { Kind = Kind.Image, BlobName = "a.png", DisplayName = "a.png" };

    /// <summary>长文字（会到行数上限）比图片卡更占沿边长度。</summary>
    [Fact]
    public void LongTextCard_IsLongerThanImageCard()
    {
        var longText = TextItem(new string('字', 300));
        var image = ImageItem();

        var textExtent = EdgeLayout.SlotExtentDip(EdgeKind.Right, longText, Style);
        var imageExtent = EdgeLayout.SlotExtentDip(EdgeKind.Right, image, Style);

        Assert.True(textExtent > imageExtent, $"长文字卡 {textExtent} 应长于图片卡 {imageExtent}");
    }

    /// <summary>短文字卡不退化成细条：不低于标称卡高。</summary>
    [Fact]
    public void ShortTextCard_KeepsNominalHeight()
    {
        var shortText = TextItem("你好");

        Assert.Equal(Metrics.CardHeight, EdgeLayout.SlotExtentDip(EdgeKind.Right, shortText, Style));
    }

    /// <summary>上下边的文字卡不参与变长（沿边方向是宽度，本版保持不变）。</summary>
    [Fact]
    public void HorizontalEdge_TextCard_KeepsNominalWidth()
    {
        var longText = TextItem(new string('字', 300));

        Assert.Equal(Metrics.CardWidth, EdgeLayout.SlotExtentDip(EdgeKind.Top, longText, Style));
        Assert.Equal(Metrics.CardWidth, EdgeLayout.SlotExtentDip(EdgeKind.Bottom, longText, Style));
    }

    /// <summary>内容总长 = 逐项长度之和 + 间距，不是 count × 固定值。</summary>
    [Fact]
    public void ContentLength_SumsPerItemExtents()
    {
        var items = new List<Item> { TextItem(new string('字', 300)), ImageItem(), TextItem("短") };

        var expected = 0;
        foreach (var item in items)
        {
            expected += Metrics.Px(EdgeLayout.SlotExtentDip(EdgeKind.Right, item, Style), Dpi);
        }

        expected += (items.Count - 1) * Metrics.Px(Metrics.CardGap, Dpi);

        Assert.Equal(expected, EdgeLayout.ContentLengthPx(EdgeKind.Right, items, Style, Dpi));
    }

    /// <summary>Compose 输出的矩形沿边不重叠、按 items 顺序排列，且高度与各自 extent 一致。</summary>
    [Fact]
    public void Compose_ProducesNonOverlappingOrderedSlots()
    {
        var items = new List<Item> { TextItem(new string('字', 300)), ImageItem(), TextItem("短") };
        var area = new Rectangle(0, 0, Metrics.CardWidth, 400);

        var slots = EdgeLayout.Compose(EdgeKind.Right, area, Dpi, items, Style, scrollPx: 0);

        Assert.Equal(items.Count, slots.Count);

        for (var i = 0; i < slots.Count; i++)
        {
            Assert.Equal(i, slots[i].Index);
            Assert.Equal(Metrics.Px(EdgeLayout.SlotExtentDip(EdgeKind.Right, items[i], Style), Dpi), slots[i].Bounds.Height);

            if (i > 0)
            {
                Assert.True(
                    slots[i].Bounds.Top >= slots[i - 1].Bounds.Bottom,
                    $"第 {i} 张卡起点 {slots[i].Bounds.Top} 应不早于前一张终点 {slots[i - 1].Bounds.Bottom}");
            }
        }
    }

    /// <summary>MaxScrollPx 随内容总长变化：内容越长，可滚范围越大。</summary>
    [Fact]
    public void MaxScroll_GrowsWithContentLength()
    {
        var area = new Rectangle(0, 0, Metrics.CardWidth, 200);

        var shortContent = new List<Item> { TextItem("短"), TextItem("短") };
        var longContent = new List<Item> { TextItem(new string('字', 300)), ImageItem(), TextItem(new string('字', 300)) };

        var shortScroll = EdgeLayout.MaxScrollPx(EdgeKind.Right, area, Dpi, shortContent, Style);
        var longScroll = EdgeLayout.MaxScrollPx(EdgeKind.Right, area, Dpi, longContent, Style);

        Assert.True(longScroll > shortScroll, $"长内容可滚 {longScroll} 应大于短内容 {shortScroll}");
    }

    /// <summary>HitTest 与 Compose 自洽：每张卡中心点命中自己。</summary>
    [Fact]
    public void HitTest_MatchesCompose()
    {
        var items = new List<Item> { TextItem(new string('字', 300)), ImageItem(), TextItem("短") };
        var area = new Rectangle(0, 0, Metrics.CardWidth, 600);

        var slots = EdgeLayout.Compose(EdgeKind.Right, area, Dpi, items, Style, scrollPx: 0);

        foreach (var slot in slots)
        {
            var center = new Point(slot.Bounds.Left + (slot.Bounds.Width / 2), slot.Bounds.Top + (slot.Bounds.Height / 2));
            Assert.Equal(slot.Index, EdgeLayout.HitTest(slots, center));
        }
    }
}
