using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenLeash.Tests;

/// <summary>一处裸像素/裸颜色字面量违规。</summary>
/// <param name="Line">1 起始行号。</param>
/// <param name="Column">1 起始列号（在剥离注释/字符串后的等长文本上）。</param>
/// <param name="Literal">原始字面量文本（如 <c>8</c>、<c>8.5f</c>、<c>0.93f</c>）。</param>
/// <param name="SourceLine">原始行内容（裁剪到 120 字符，便于报错定位）。</param>
public readonly record struct RawPixelLiteral(int Line, int Column, string Literal, string SourceLine);

/// <summary>一次扫描的结果。</summary>
public sealed class RawPixelScanResult
{
    public RawPixelScanResult(
        string path,
        IReadOnlyList<RawPixelLiteral> violations,
        IReadOnlyList<int> exemptLines)
    {
        Path = path;
        Violations = violations;
        ExemptLines = exemptLines;
    }

    /// <summary>被扫描文件路径。</summary>
    public string Path { get; }

    /// <summary>违规字面量列表（按出现顺序）。</summary>
    public IReadOnlyList<RawPixelLiteral> Violations { get; }

    /// <summary>带 <c>// metrics-ok: &lt;理由&gt;</c> 豁免标记的行号。</summary>
    public IReadOnlyList<int> ExemptLines { get; }

    /// <summary>违规数量。</summary>
    public int Count => Violations.Count;

    public override string ToString()
    {
        if (Count == 0)
        {
            return $"{Path}: 0 violations, {ExemptLines.Count} exemption(s)";
        }

        var sb = new StringBuilder();
        sb.Append(Path).Append(": ").Append(Count).Append(" violation(s), ")
            .Append(ExemptLines.Count).Append(" exemption(s)").AppendLine();
        foreach (var v in Violations)
        {
            sb.Append("  L").Append(v.Line).Append(':').Append(v.Column)
                .Append(" literal=").Append(v.Literal)
                .Append(" | ").Append(v.SourceLine).AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// 裸像素扫描器（正式实现，源头是已实证的原型 <c>.rivet/scratch/rawpixel-scan.mjs</c>）。
/// 规则：剥离注释、字符串、字符字面量后，任何数字字面量都算违规，
/// 除非它是 <see cref="ExemptLiterals"/> 之一，或所在行带 <c>// metrics-ok: &lt;理由&gt;</c>。
/// 十六进制字面量（<c>0xFF...</c>）不作为像素度量，按原型行为放行。
/// 已知限制（与原型一致）：
///   1. 插值字符串整体按字符串剥离，其中的数字不可见（漏报）；需要检测就拆成局部变量。
///   2. 不做语义分析（不引入 Roslyn analyzer，成本 3-5 倍），因此局部变量别名绕不过禁。
///   3. 十六进制/二进制字面量（0x1F、0b1010）因 <c>\w</c> 边界不被识别，天然放行——
///      这是颜色常量允许写成 0xAARRGGBB 的依据。
/// </summary>
public static class RawPixelScanner
{
    /// <summary>行尾豁免标记。</summary>
    public const string ExemptionMarker = "metrics-ok";

    /// <summary>全项目豁免行总数上限；超过即门禁失败。</summary>
    public const int MaxExemptions = 5;

    /// <summary>豁免字面量（无量纲的取值）。</summary>
    public static readonly IReadOnlySet<string> ExemptLiterals = new HashSet<string>(StringComparer.Ordinal)
    {
        "0",
        "1",
        "0.0",
        "1.0",
        "0f",
        "1f",
        "0F",
        "1F",
        "0d",
        "1d",
        "0D",
        "1D",
        "0m",
        "1m",
        "0M",
        "1M",
        "0.0f",
        "1.0f",
        "0.0F",
        "1.0F",
        "0.0d",
        "1.0d",
        "0.0D",
        "1.0D",
        "0.0m",
        "1.0m",
        "0.0M",
        "1.0M",
    };

    private static readonly Regex NumberLiteral = new(
        @"(?<![\w.$])(\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?)([fFdDmM])?(?![\w.])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExemptionLine = new(
        @"//\s*" + Regex.Escape(ExemptionMarker) + @"\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>命名颜色（<c>Color.White</c> 之类）—— 颜色必须走 Theme，不得就地写色名。</summary>
    private static readonly Regex NamedColor = new(
        @"\bColor\.([A-Z][A-Za-z]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>不是调色值的 <c>Color.*</c> 成员：工厂方法与中性值（Empty/Transparent）。</summary>
    private static readonly IReadOnlySet<string> NamedColorNeutral = new HashSet<string>(StringComparer.Ordinal)
    {
        "FromArgb",
        "FromKnownColor",
        "FromName",
        "FromHsv",
        "Empty",
        "Transparent",
    };

    /// <summary>
    /// 扫描一段源码文本。
    /// </summary>
    /// <param name="source">源码或夹具文本。</param>
    /// <param name="path">报告用路径。</param>
    /// <param name="allowNamedColors">
    /// 是否允许命名颜色。只有颜色归属地（<c>Theme.cs</c>）可以开，其它文件必须走 Theme。
    /// </param>
    public static RawPixelScanResult ScanText(string source, string path = "<text>", bool allowNamedColors = false)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var rawLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var stripped = StripNoise(source).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var violations = new List<RawPixelLiteral>();
        var exempt = new List<int>();

        for (int i = 0; i < stripped.Length; i++)
        {
            var rawLine = i < rawLines.Length ? rawLines[i] : string.Empty;
            if (ExemptionLine.IsMatch(rawLine))
            {
                exempt.Add(i + 1);
                continue;
            }

            var line = stripped[i];
            foreach (Match m in NumberLiteral.Matches(line))
            {
                var literal = m.Groups[1].Value + (m.Groups[2].Success ? m.Groups[2].Value : string.Empty);
                LiteralFlavor flavor = Classify(line, m);

                if (ExemptLiterals.Contains(literal))
                {
                    continue;
                }

                // 十六进制：C# 写法 0x1F / 0xFF1C1E24 —— 原型用「匹配位置前两字符是 0x」判放行。
                // 这里用预扫描的十六进制区间判定（更严格，且不依赖前一个字符的边界）。
                if (flavor == LiteralFlavor.Hex)
                {
                    continue;
                }

                violations.Add(new RawPixelLiteral(
                    i + 1,
                    m.Index + 1,
                    literal,
                    rawLine.Trim().Length > 120 ? rawLine.Trim().Substring(0, 120) : rawLine.Trim()));
            }

            if (!allowNamedColors)
            {
                foreach (Match m in NamedColor.Matches(line))
                {
                    if (NamedColorNeutral.Contains(m.Groups[1].Value))
                    {
                        continue;
                    }

                    violations.Add(new RawPixelLiteral(
                        i + 1,
                        m.Index + 1,
                        "Color." + m.Groups[1].Value,
                        rawLine.Trim().Length > 120 ? rawLine.Trim().Substring(0, 120) : rawLine.Trim()));
                }
            }
        }

        violations.Sort((left, right) => left.Line != right.Line
            ? left.Line.CompareTo(right.Line)
            : left.Column.CompareTo(right.Column));

        return new RawPixelScanResult(path, violations, exempt);
    }

    /// <summary>扫描单个文件。</summary>
    public static RawPixelScanResult ScanFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到待扫描文件。", path);
        }

        return ScanText(File.ReadAllText(path), path, allowNamedColors: IsColorAuthority(path));
    }

    /// <summary>颜色归属地（Theme.cs）允许命名颜色；其它文件一律不允许。</summary>
    private static bool IsColorAuthority(string path) =>
        string.Equals(Path.GetFileName(path), "Theme.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 扫描目录下所有 <c>*.cs</c> 文件并聚合结果（跳过 bin/obj）。
    /// </summary>
    public static RawPixelScanResult ScanDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return new RawPixelScanResult(root, Array.Empty<RawPixelLiteral>(), Array.Empty<int>());
        }

        var all = new List<RawPixelLiteral>();
        var exempt = new List<int>();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = ScanFile(file);
            all.AddRange(result.Violations);
            exempt.AddRange(result.ExemptLines);
        }

        return new RawPixelScanResult(root, all, exempt);
    }

    private enum LiteralFlavor
    {
        Decimal,
        Hex,
    }

    /// <summary>
    /// 与原型 <c>stripNoise</c> 等价的逐字符状态机：注释/字符串/字符字面量替换为空格，
    /// 保留换行与列宽（便于行号、列号直接在原文对齐）。
    /// </summary>
    internal static string StripNoise(string source)
    {
        var output = new StringBuilder(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];

            // 行注释
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    output.Append(' ');
                    i++;
                }

                continue;
            }

            // 块注释（保留换行）
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                output.Append(' ').Append(' ');
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
                    {
                        output.Append(' ').Append(' ');
                        i += 2;
                        break;
                    }

                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            // 字符串起始：@"..." / $"..." / $@"..." / @$"..." / "..."
            int stringStart = StringStartLength(source, i);
            if (stringStart > 0)
            {
                bool verbatim = IsVerbatim(source, i);
                bool sawQuote = false;
                int j = i;
                while (j < source.Length)
                {
                    char ch = source[j];
                    if (sawQuote)
                    {
                        if (verbatim && ch == '"')
                        {
                            // @"a""b" 的转义引号
                            output.Append(' ');
                            j++;
                            if (j < source.Length && source[j] == '"')
                            {
                                output.Append(' ');
                                j++;
                                continue;
                            }

                            break;
                        }

                        if (!verbatim && ch == '\\')
                        {
                            output.Append(' ');
                            j++;
                            if (j < source.Length)
                            {
                                output.Append(source[j] == '\n' ? '\n' : ' ');
                                j++;
                            }

                            continue;
                        }

                        if (!verbatim && ch == '"')
                        {
                            output.Append(' ');
                            j++;
                            break;
                        }
                    }

                    if (ch == '"')
                    {
                        sawQuote = true;
                    }

                    output.Append(ch == '\n' ? '\n' : ' ');
                    j++;
                }

                i = j;
                continue;
            }

            // 字符字面量 'x' / '\n'
            if (c == '\'')
            {
                output.Append(' ');
                i++;
                while (i < source.Length)
                {
                    char ch = source[i];
                    if (ch == '\\')
                    {
                        output.Append(' ');
                        i++;
                        if (i < source.Length)
                        {
                            output.Append(source[i] == '\n' ? '\n' : ' ');
                            i++;
                        }

                        continue;
                    }

                    output.Append(ch == '\n' ? '\n' : ' ');
                    i++;
                    if (ch == '\'')
                    {
                        break;
                    }
                }

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    private static bool IsVerbatim(string source, int index)
    {
        char c = source[index];
        if (c == '@')
        {
            return true; // @"..." 与 @$"..."
        }

        // $@"..."
        return c == '$' && index + 1 < source.Length && source[index + 1] == '@';
    }

    private static int StringStartLength(string source, int index)
    {
        char c = source[index];
        if (c == '"')
        {
            return 1;
        }

        if ((c == '$' || c == '@') && index + 1 < source.Length)
        {
            char next = source[index + 1];
            if (next == '"')
            {
                return 2;
            }

            if (((c == '$' && next == '@') || (c == '@' && next == '$'))
                && index + 2 < source.Length
                && source[index + 2] == '"')
            {
                return 3;
            }
        }

        return 0;
    }

    private static LiteralFlavor Classify(string line, Match match)
    {
        // 匹配起点前若紧跟 x/X（形如 0x1F 的尾部），则属于十六进制字面量。
        int start = match.Index;
        if (start >= 1 && (line[start - 1] == 'x' || line[start - 1] == 'X'))
        {
            return LiteralFlavor.Hex;
        }

        return LiteralFlavor.Decimal;
    }
}
