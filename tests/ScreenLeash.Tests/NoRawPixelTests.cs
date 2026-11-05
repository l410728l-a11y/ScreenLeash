using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 「合规必绿」门禁：UI/ 与 Interop/ 下不得出现裸像素或裸颜色字面量。
/// 双夹具保证扫描器本身既不是恒绿（会漏），也不是恒红（会误报）：
///   RawPixelViolation.cs.fixture 必须 fail —— 覆盖 const / 算术 / Pen / Font / PointF / Point / FillRectangle / Opacity / Math.Max 全部向量；
///   RawPixelClean.cs.fixture 必须 pass —— 但它必须是**真实绘制代码**（Px 换算、线宽、字号、偏移、缓动步长），
///   否则「合规必绿」只是平凡通过。本类用 <see cref="CleanFixture_ContainsRealDrawingCode"/> 强制这一点。
/// </summary>
public class NoRawPixelTests
{
    private const string ViolationFixtureName = "RawPixelViolation.cs.fixture";
    private const string CleanFixtureName = "RawPixelClean.cs.fixture";

    [Fact]
    public void ViolationFixture_MustFail_AndCoverEveryKnownVector()
    {
        var result = RawPixelScanner.ScanFile(TestPaths.Fixture(ViolationFixtureName));

        Assert.True(result.Count > 0, $"违规夹具必须被拒绝，实际 0 违规：{result}");
        Assert.True(
            result.ExemptLines.Count == 0,
            "违规夹具不得使用 metrics-ok 豁免（那会让夹具自我阉割）。");

        // 逐向量断言：行号 + 字面量精确匹配，任何一条抓不到都说明扫描器退化了。
        var expected = new (int Line, string Literal)[]
        {
            (9, "8"),        // const CollapsedWidth = 8
            (10, "148"),     // const ExpandedWidth = 148
            (11, "8"),       // 字段初始值 = 8
            (13, "0.93f"),   // Opacity = 0.93f
            (17, "44"),      // workingArea.Right - 44
            (19, "8"),       // Math.Max(8, ...)
            (19, "3"),       // ... / 3
            (27, "70"),      // Color.FromArgb(70, ...)
            (27, "255"),     // Color.FromArgb(..., 255, ...)
            (27, "255"),
            (27, "255"),
            (27, "2f"),      // new Pen(..., 2f)
            (29, "60"),      // width > 60
            (31, "24"),      // Color.FromArgb(24, 26, 32)
            (31, "26"),
            (31, "32"),
            (32, "4"),       // FillRectangle(brush, 4, 0, width - 6, height)
            (32, "6"),
            (33, "8.5f"),    // new Font("Segoe UI", 8.5f)
            (34, "8"),       // new PointF(8, 8)
            (34, "8"),
            (35, "4"),       // new Point(4, 40)
            (35, "40"),
        };

        var actual = result.Violations.Select(v => (v.Line, v.Literal)).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CleanFixture_MustPass()
    {
        var result = RawPixelScanner.ScanFile(TestPaths.Fixture(CleanFixtureName));

        Assert.True(
            result.Count == 0,
            $"合规夹具必须 0 违规（它代表走 Metrics/Theme 的真实绘制代码），实际：{result}");
        Assert.True(
            result.ExemptLines.Count == 0,
            "合规夹具应靠 Metrics/Theme 天然合规，不需要 metrics-ok 豁免。");
    }

    /// <summary>
    /// 反平凡通过：合规夹具若被写成空壳或只含 0/1 的平凡文件，上面的 MustPass 就没有信息量。
    /// 这里强制它真的在画东西，且尺寸/颜色确实来自单一来源。
    /// </summary>
    [Fact]
    public void CleanFixture_ContainsRealDrawingCode()
    {
        var path = TestPaths.Fixture(CleanFixtureName);
        var text = File.ReadAllText(path);

        Assert.True(CountOccurrences(text, "Metrics.Px(") >= 8, "合规夹具必须有真实的 DIP→px 换算（至少 8 处 Metrics.Px 调用）。");
        Assert.True(CountOccurrences(text, "Metrics.PxFloat(") >= 2, "线宽/字号必须走 Metrics.PxFloat。");
        Assert.True(CountOccurrences(text, "Theme.") >= 4, "颜色必须全部走 Theme。");
        Assert.True(text.Contains("new Pen(", StringComparison.Ordinal), "必须包含描边绘制。");
        Assert.True(text.Contains("new Font(", StringComparison.Ordinal), "必须包含字体绘制。");
        Assert.True(text.Contains("FillRectangle(", StringComparison.Ordinal), "必须包含填充绘制（含偏移）。");
        Assert.True(text.Contains("DrawString(", StringComparison.Ordinal), "必须包含文本绘制。");
        Assert.True(text.Contains("Math.Max(", StringComparison.Ordinal), "必须包含以 Metrics 为上限的算术（Math.Max）。");
        Assert.True(text.Contains("EdgeGeometry.Compute(", StringComparison.Ordinal), "窗口矩形必须来自 EdgeGeometry.Compute。");
    }

    [Fact]
    public void ProductionUiAndInterop_HaveNoRawPixelLiterals()
    {
        var problems = new List<string>();

        foreach (var sub in new[] { "UI", "Interop" })
        {
            var dir = TestPaths.SourceDir(sub);
            if (!Directory.Exists(dir))
            {
                // Interop/ 在后续任务才出现；此时不视为失败，但要在报告里可见。
                continue;
            }

            var result = RawPixelScanner.ScanDirectory(dir);
            if (result.Count > 0)
            {
                problems.Add(result.ToString());
            }
        }

        Assert.True(problems.Count == 0, "UI/ 或 Interop/ 下存在裸像素/裸颜色字面量：\n" + string.Join("\n", problems));
    }

    [Fact]
    public void ExemptionBudget_AcrossUiAndInterop_IsRespected()
    {
        int total = 0;
        foreach (var sub in new[] { "UI", "Interop" })
        {
            var dir = TestPaths.SourceDir(sub);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var result = RawPixelScanner.ScanDirectory(dir);
            total += result.ExemptLines.Count;
        }

        Assert.True(
            total <= RawPixelScanner.MaxExemptions,
            $"metrics-ok 豁免行总数 {total} 超过上限 {RawPixelScanner.MaxExemptions}。豁免是逃生舱，不是常态。");
    }

    [Theory]
    [InlineData("private const int Pad = 8;", "8")]
    [InlineData("int rest = area.Right - 44;", "44")]
    [InlineData("using var p = new Pen(Theme.Border, 2f);", "2f")]
    [InlineData("using var f = new Font(Theme.UiFontFamily, 8.5f);", "8.5f")]
    [InlineData("g.DrawString(t, f, b, new PointF(8, 8));", "8")]
    [InlineData("var at = new Point(4, 40);", "40")]
    [InlineData("g.FillRectangle(b, 4, 0, w - 6, h);", "6")]
    [InlineData("int step = Math.Max(8, delta / 3);", "3")]
    [InlineData("Opacity = 0.93f;", "0.93f")]
    [InlineData("var c = Color.FromArgb(70, 255, 255, 255);", "70")]
    public void Scanner_DetectsInlineVector(string source, string expectedLiteral)
    {
        var result = RawPixelScanner.ScanText(source);

        Assert.Contains(result.Violations, v => v.Literal == expectedLiteral);
    }

    [Theory]
    [InlineData("// private const int Pad = 8;", "注释里的数字不是代码")]
    [InlineData("var s = \"padding 8px\";", "字符串里的数字不是代码")]
    [InlineData("var s = $\"padding {8}px\";", "插值字符串整体剥离（已知漏报）")]
    [InlineData("int zero = 0;\nint one = 1;", "0/1 豁免")]
    [InlineData("uint argb = 0xFF1C1E24;", "十六进制颜色常量放行")]
    [InlineData("float f = 1f;", "1f 豁免")]
    public void Scanner_IgnoresNoiseAndExemptLiterals(string source, string why)
    {
        var result = RawPixelScanner.ScanText(source);

        Assert.True(result.Count == 0, $"{why}，应 0 违规，实际：{result}");
    }

    [Fact]
    public void Scanner_HonorsMetricsOkExemption()
    {
        const string source = "var tricky = 42; // metrics-ok: 采样编号，非视觉度量";

        var result = RawPixelScanner.ScanText(source);

        Assert.Empty(result.Violations);
        Assert.Single(result.ExemptLines);
    }

    [Fact]
    public void Scanner_RejectsNamedColors()
    {
        var result = RawPixelScanner.ScanText("var c = Color.White;");

        var violation = Assert.Single(result.Violations);
        Assert.Equal("Color.White", violation.Literal);
    }

    [Fact]
    public void Scanner_AllowsColorFactoryAndNeutralMembers()
    {
        var result = RawPixelScanner.ScanText("var c = Color.FromArgb(alpha, red, green, blue); var d = Color.Transparent; var e = Color.Empty;");

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void ThemeSource_IsTheOnlyColorAuthority()
    {
        var result = RawPixelScanner.ScanFile(Path.Combine(TestPaths.SourceDir("UI"), "Theme.cs"));

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Scanner_ReportsLineAndColumn()
    {
        const string source = "var a = 1;\nvar b = 2;";

        var result = RawPixelScanner.ScanText(source);

        var violation = Assert.Single(result.Violations);
        Assert.Equal(2, violation.Line);
        Assert.Equal(9, violation.Column);
        Assert.Equal("2", violation.Literal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
