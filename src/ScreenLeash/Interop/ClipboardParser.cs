using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ScreenLeash.Core;

namespace ScreenLeash.Interop;

/// <summary>剪贴板/拖放数据的权威形态。</summary>
public enum ParsedKind
{
    /// <summary>没有可挂载的内容。</summary>
    None,

    /// <summary>一个或多个真实文件路径（含资源管理器拖入、复制的文件）。</summary>
    Files,

    /// <summary>纯文本（UnicodeText 或从 HTML 片段提取的文本）。</summary>
    Text,

    /// <summary>位图（截图、浏览器复制的图片）。</summary>
    Image,
}

/// <summary>一次解析的结果；不可变，可直接跨线程传递。</summary>
public sealed class ParsedContent
{
    /// <summary>空结果（无内容可挂）。</summary>
    public static readonly ParsedContent Empty =
        new(ParsedKind.None, null, Array.Empty<string>(), null, null);

    private ParsedContent(
        ParsedKind kind,
        string? text,
        IReadOnlyList<string> files,
        byte[]? imageBytes,
        string? imageExtension)
    {
        Kind = kind;
        Text = text;
        Files = files;
        ImageBytes = imageBytes;
        ImageExtension = imageExtension;
    }

    public ParsedKind Kind { get; }

    /// <summary>Kind=Text 时的文本内容（已 Trim、已按 Settings 截断由调用方负责）。</summary>
    public string? Text { get; }

    /// <summary>Kind=Files 时的文件路径列表（已过滤不存在项）。</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Kind=Image 时的 PNG 字节。</summary>
    public byte[]? ImageBytes { get; }

    /// <summary>Kind=Image 时的扩展名（统一 png）。</summary>
    public string? ImageExtension { get; }

    public bool HasContent => Kind != ParsedKind.None;

    public static ParsedContent FromFiles(IReadOnlyList<string> files) =>
        files.Count == 0
            ? Empty
            : new ParsedContent(ParsedKind.Files, null, files, null, null);

    public static ParsedContent FromText(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? Empty
            : new ParsedContent(ParsedKind.Text, text.Trim(), Array.Empty<string>(), null, null);

    public static ParsedContent FromImage(byte[] pngBytes) =>
        pngBytes.Length == 0
            ? Empty
            : new ParsedContent(ParsedKind.Image, null, Array.Empty<string>(), pngBytes, AppPaths.ThumbExtension);

    public override string ToString()
        => Kind switch
        {
            ParsedKind.Files => $"Files({Files.Count})",
            ParsedKind.Text => $"Text({Text?.Length ?? 0} chars)",
            ParsedKind.Image => $"Image({ImageBytes?.Length ?? 0} bytes)",
            _ => "None",
        };
}

/// <summary>
/// 剪贴板与拖放数据的**纯解析层**：只做「拿到什么 → 归成哪种卡片」的判断与解码，
/// 不写磁盘、不碰 UI、不起线程，因此可以脱开 WinForms 消息循环单测。
/// 优先级（同时存在多种格式时的取舍）：
/// <list type="number">
/// <item><b>FileDrop</b>：真实文件/文件夹路径，信息最完整，拖到别处还能还原类型。</item>
/// <item><b>UnicodeText</b>：Word/Excel/网页复制的正文——同时带 Bitmap 时不选图，选文字更符合意图。</item>
/// <item><b>Bitmap / CF_DIB</b>：截图工具（Win+Shift+S）只给位图，此路命中。</item>
/// <item><b>HTML Format</b>：仅当上面都没有时，退化为去标签后的纯文本。</item>
/// </list>
/// 本类为 <c>ClipboardParser.cs</c> 的唯一 owner，<c>DragIn</c> 与 <c>PasteHook</c> 只调用不修改。
/// </summary>
public static class ClipboardParser
{
    private static readonly Regex HtmlFragment = new(
        @"<!--\s*StartFragment\s*-->(?<body>.*?)<!--\s*EndFragment\s*-->",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTag = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    private static readonly Regex HtmlBreak = new(
        @"<\s*(br|/p|/div|/li)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlEntities = new(
        @"&(#x?[0-9a-fA-F]+|[a-zA-Z]+);",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KnownEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = " ",
    };

    /// <summary>
    /// 解析一个 <see cref="IDataObject"/>（拖放事件提供的数据对象，或剪贴板快照）。
    /// 任何格式的内部异常都被吞掉并退化为下一个候选，绝不把解析失败抛给 UI 线程。
    /// </summary>
    public static ParsedContent ParseDataObject(IDataObject? data)
    {
        if (data is null)
        {
            return ParsedContent.Empty;
        }

        // 1) 真实文件
        var files = TryGetFiles(data);
        if (files.Count > 0)
        {
            return ParsedContent.FromFiles(files);
        }

        // 2) 纯文本
        var text = TryGetUnicodeText(data);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return ParsedContent.FromText(text);
        }

        // 3) 位图
        var image = TryGetBitmapPng(data);
        if (image is not null)
        {
            return ParsedContent.FromImage(image);
        }

        // 4) HTML 片段里的文字
        var htmlText = TryGetHtmlText(data);
        if (!string.IsNullOrWhiteSpace(htmlText))
        {
            return ParsedContent.FromText(htmlText);
        }

        return ParsedContent.Empty;
    }

    /// <summary>
    /// 读取系统剪贴板并解析。剪贴板被其它进程占用（OpenClipboard 失败）时返回空结果，
    /// 由调用方决定是否提示；绝不重试阻塞 UI。
    /// </summary>
    public static ParsedContent ParseClipboard()
    {
        try
        {
            if (!Clipboard.ContainsData(DataFormats.FileDrop)
                && !Clipboard.ContainsText()
                && !Clipboard.ContainsImage())
            {
                return ParsedContent.Empty;
            }

            var data = Clipboard.GetDataObject();
            return ParseDataObject(data);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
                                   || ex is System.Threading.ThreadStateException)
        {
            // 剪贴板被占用：返回空，让用户再试一次即可。
            return ParsedContent.Empty;
        }
    }

    /// <summary>把 <see cref="Image"/> 编码成 PNG 字节；失败返回 null。</summary>
    public static byte[]? EncodePng(Image? image)
    {
        if (image is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ExternalException || ex is IOException || ex is ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> TryGetFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
            {
                return Array.Empty<string>();
            }

            if (data.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                return Array.Empty<string>();
            }

            var existing = new List<string>(paths.Length);
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path)
                    && (File.Exists(path) || Directory.Exists(path)))
                {
                    existing.Add(path);
                }
            }

            return existing;
        }
        catch (Exception ex) when (ex is ExternalException || ex is InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? TryGetUnicodeText(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true))
            {
                return data.GetData(DataFormats.UnicodeText, autoConvert: true) as string;
            }

            if (data.GetDataPresent(DataFormats.Text))
            {
                return data.GetData(DataFormats.Text) as string;
            }
        }
        catch (Exception ex) when (ex is ExternalException || ex is InvalidOperationException)
        {
            // 退化到下一个候选。
        }

        return null;
    }

    private static byte[]? TryGetBitmapPng(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.Bitmap, autoConvert: true))
            {
                return null;
            }

            if (data.GetData(DataFormats.Bitmap, autoConvert: true) is not Image image)
            {
                return null;
            }

            // 剪贴板返回的对象归系统所有：复制一份再编码，避免后续被外部释放。
            using var copy = new Bitmap(image);
            return EncodePng(copy);
        }
        catch (Exception ex) when (ex is ExternalException || ex is InvalidOperationException || ex is ArgumentException)
        {
            return null;
        }
    }

    private static string? TryGetHtmlText(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(InteropConstants.HtmlFormat))
            {
                return null;
            }

            if (data.GetData(InteropConstants.HtmlFormat) is not string html || string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            return ExtractPlainText(html);
        }
        catch (Exception ex) when (ex is ExternalException || ex is InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 CF_HTML 片段提取可读文本：优先 StartFragment/EndFragment 之间的正文，
    /// 把 &lt;br&gt;/&lt;/p&gt; 之变换成换行，去标签，解实体，压缩空白。
    /// </summary>
    public static string ExtractPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var body = html;
        var fragment = HtmlFragment.Match(html);
        if (fragment.Success)
        {
            body = fragment.Groups["body"].Value;
        }

        body = HtmlBreak.Replace(body, Environment.NewLine);
        body = HtmlTag.Replace(body, string.Empty);
        body = HtmlEntities.Replace(body, DecodeEntity);

        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder(body.Length);
        foreach (var rawLine in lines)
        {
            var line = CollapseWhitespace(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(Environment.NewLine);
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private static string DecodeEntity(Match match)
    {
        var token = match.Groups[1].Value;
        if (KnownEntities.TryGetValue(token, out var known))
        {
            return known;
        }

        if (token.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(token.AsSpan("#x".Length), System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            return char.ConvertFromUtf32(hex);
        }

        if (token.StartsWith("#", StringComparison.Ordinal)
            && int.TryParse(token.AsSpan("#".Length), out var dec))
        {
            return char.ConvertFromUtf32(dec);
        }

        return match.Value;
    }

    private static string CollapseWhitespace(string line)
    {
        var builder = new StringBuilder(line.Length);
        var previousWasSpace = false;
        foreach (var ch in line)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace)
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            builder.Append(ch);
            previousWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}
