using System;

namespace ScreenLeash.Core;

/// <summary>
/// 文字卡行数估算：纯函数、不依赖 Graphics、不读磁盘。
///
/// <para>布局层（UI.EdgeLayout）用它决定文字卡的沿边长度——卡片高度随文字量变化。
/// 估算规则偏保守（宁可多算一点），实际绘制仍由 <c>EllipsisCharacter</c> 兜底：
/// 允许「卡比文字略高」，不允许「文字溢出卡片」。</para>
///
/// <para>为什么不用 <c>Graphics.MeasureString</c>：布局是纯函数且被单元测试直接调用，
/// 引入 Graphics 会让它依赖设备上下文，也无法在无窗口环境测试。</para>
/// </summary>
public static class TextMeasure
{
    /// <summary>
    /// 估算文字卡需要的行数。
    /// </summary>
    /// <param name="text">卡片文本（可为 null/空）。</param>
    /// <param name="fontDip">字号（DIP）。</param>
    /// <param name="maxLines">上限行数；小于 1 时按 1 处理。</param>
    /// <returns>1..maxLines 之间的行数。</returns>
    public static int EstimateTextLines(string? text, int fontDip, int maxLines)
    {
        if (maxLines < 1)
        {
            maxLines = 1;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return 1;
        }

        // 可用宽度（DIP）：卡片宽 - 两侧卡片内边距 - 两侧文本内缩。
        var usableWidth = Metrics.CardWidth - (2 * Metrics.CardPadding) - (2 * Metrics.TextInset);
        if (usableWidth <= 0)
        {
            return 1;
        }

        // 单字符宽度估算（DIP）：中文字宽约等于字号，西文约 0.55 倍。
        // 不逐字判宽，统一按 0.55 倍偏保守取值——英文卡会多算行（卡略高），中文卡恰好。
        var charWidth = Math.Max(1.0, fontDip * 0.55);

        // 每行可容纳字符数（至少 1，避免除零）。
        var perLine = Math.Max(1, (int)(usableWidth / charWidth));

        var lines = 0;
        foreach (var paragraph in text.Split('\n'))
        {
            var length = paragraph.TrimEnd('\r').Length;
            if (length == 0)
            {
                lines += 1;   // 空段落占一行（与 DrawString 的行为一致）
            }
            else
            {
                lines += (int)Math.Ceiling(length / (double)perLine);
            }

            if (lines >= maxLines)
            {
                return maxLines;
            }
        }

        return Math.Clamp(lines, 1, maxLines);
    }
}
