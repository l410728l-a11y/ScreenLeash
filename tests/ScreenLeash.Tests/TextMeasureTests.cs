using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// <see cref="TextMeasure.EstimateTextLines"/> 的单元测试。
/// 该函数决定文字卡的沿边长度，是「卡片高度随文字量变化」的唯一判据。
/// </summary>
public sealed class TextMeasureTests
{
    [Fact]
    public void EmptyOrWhitespaceText_TakesOneLine()
    {
        Assert.Equal(1, TextMeasure.EstimateTextLines(null, 11, 6));
        Assert.Equal(1, TextMeasure.EstimateTextLines(string.Empty, 11, 6));
        Assert.Equal(1, TextMeasure.EstimateTextLines("   ", 11, 6));
    }

    [Fact]
    public void ShortText_TakesOneLine()
    {
        Assert.Equal(1, TextMeasure.EstimateTextLines("你好", 11, 6));
        Assert.Equal(1, TextMeasure.EstimateTextLines("hello world", 11, 6));
    }

    [Fact]
    public void LongText_IsClampedToMaxLines()
    {
        var text = new string('字', 300);
        Assert.Equal(6, TextMeasure.EstimateTextLines(text, 11, 6));
        Assert.Equal(12, TextMeasure.EstimateTextLines(text, 11, 12));
    }

    [Fact]
    public void MultiParagraph_CountsEachParagraph()
    {
        // 三个短段落：每段各占一行。
        Assert.Equal(3, TextMeasure.EstimateTextLines("a\nb\nc", 11, 6));

        // 含空行的段落也占一行（与 DrawString 的行为一致）。
        Assert.Equal(3, TextMeasure.EstimateTextLines("a\n\nb", 11, 6));
    }

    [Fact]
    public void LargerFont_TakesMoreOrEqualLines()
    {
        // 用不会触到上限的长度（maxLines 设到最大），比较字号 9 / 11 / 20。
        var text = new string('x', 40);

        var atNine = TextMeasure.EstimateTextLines(text, 9, Metrics.TextMaxLinesMax);
        var atEleven = TextMeasure.EstimateTextLines(text, 11, Metrics.TextMaxLinesMax);
        var atTwenty = TextMeasure.EstimateTextLines(text, 20, Metrics.TextMaxLinesMax);

        Assert.True(atNine <= atEleven, $"9dip={atNine} 应不大于 11dip={atEleven}");
        Assert.True(atEleven <= atTwenty, $"11dip={atEleven} 应不大于 20dip={atTwenty}");
        Assert.True(atTwenty > atNine, "字号 20 的行数应严格多于字号 9（40 字时）");
    }

    [Fact]
    public void MaxLinesBelowOne_IsClampedToOne()
    {
        Assert.Equal(1, TextMeasure.EstimateTextLines(new string('字', 300), 11, 0));
        Assert.Equal(1, TextMeasure.EstimateTextLines(new string('字', 300), 11, -5));
    }
}
