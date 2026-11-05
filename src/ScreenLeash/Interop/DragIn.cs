using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScreenLeash.Core;

namespace ScreenLeash.Interop;

/// <summary>一次摄入的结果，供 UI 给出准确提示（而不是笼统的「成功/失败」）。</summary>
/// <param name="Added">成功挂到绳子上的卡片数。</param>
/// <param name="Skipped">被跳过的条目数（文件夹、超大文件、不存在路径）。</param>
/// <param name="LastError">最后一个可读的失败原因；没有失败时为 null。</param>
public readonly record struct IngestResult(int Added, int Skipped, string? LastError)
{
    public static readonly IngestResult None = new(0, 0, null);

    public bool AnyAdded => Added > 0;
}

/// <summary>
/// 拖入接收：把 <see cref="IDataObject"/> 里的数据落成卡片。
///
/// <para>解析全部委托给 <see cref="ClipboardParser"/>（唯一 owner），本类只负责
/// 「按 Settings 过滤 → 调 ItemStore 写盘 → 汇总结果」。因此拖入与粘贴走的是同一条落盘路径，
/// 行为不会分叉。</para>
///
/// <para>失败语义：单条失败不影响其它条目（拖 5 个文件坏 1 个，其余 4 个照常挂上），
/// 只有全部失败才把最后一条错误抛给调用方做提示。</para>
/// </summary>
public static class DragIn
{
    /// <summary>拖入时的目标效果：只接受复制（源文件不该被移动或删除）。</summary>
    public const DragDropEffects AllowedEffect = DragDropEffects.Copy;

    /// <summary>根据拖入数据给出鼠标指针效果：有可挂载内容就显示 Copy。</summary>
    public static DragDropEffects EffectsFor(IDataObject? data)
        => ClipboardParser.ParseDataObject(data).HasContent ? AllowedEffect : DragDropEffects.None;

    /// <summary>
    /// 摄入拖入数据。<paramref name="edge"/> 决定挂到哪条边。
    /// 该方法可从 UI 线程 await；内部对每个条目分别 await，不阻塞消息循环。
    /// </summary>
    public static async Task<IngestResult> IngestAsync(
        IDataObject? data,
        ItemStore store,
        AppSettings settings,
        EdgeKind edge,
        CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        settings ??= new AppSettings();
        var parsed = ClipboardParser.ParseDataObject(data);
        return await IngestParsedAsync(parsed, store, settings, edge, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>摄入一份已解析的内容（粘贴路径直接复用这里）。</summary>
    public static async Task<IngestResult> IngestParsedAsync(
        ParsedContent parsed,
        ItemStore store,
        AppSettings settings,
        EdgeKind edge,
        CancellationToken cancellationToken = default)
    {
        if (parsed is null || !parsed.HasContent)
        {
            return IngestResult.None;
        }

        switch (parsed.Kind)
        {
            case ParsedKind.Text:
                return await IngestTextAsync(parsed.Text, store, settings, edge, cancellationToken).ConfigureAwait(true);

            case ParsedKind.Image:
                return await IngestImageAsync(parsed.ImageBytes, parsed.ImageExtension, store, settings, edge, cancellationToken).ConfigureAwait(true);

            case ParsedKind.Files:
                return await IngestFilesAsync(parsed.Files, store, settings, edge, cancellationToken).ConfigureAwait(true);

            default:
                return IngestResult.None;
        }
    }

    private static async Task<IngestResult> IngestTextAsync(
        string? text,
        ItemStore store,
        AppSettings settings,
        EdgeKind edge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            return IngestResult.None;
        }

        var trimmed = text.Length > settings.TextPreviewChars
            ? text.Substring(0, settings.TextPreviewChars)
            : text;

        await store.AddTextAsync(trimmed, edge).ConfigureAwait(true);
        return new IngestResult(1, 0, null);
    }

    private static async Task<IngestResult> IngestImageAsync(
        byte[]? bytes,
        string? extension,
        ItemStore store,
        AppSettings settings,
        EdgeKind edge,
        CancellationToken cancellationToken)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return IngestResult.None;
        }

        if (bytes.Length > settings.MaxSingleFileBytes)
        {
            return new IngestResult(0, 1, "图片超过单文件上限：" + FormatMegabytes(settings.MaxSingleFileBytes));
        }

        await store.AddImageAsync(bytes, extension ?? AppPaths.ThumbExtension, edge, cancellationToken).ConfigureAwait(true);
        return new IngestResult(1, 0, null);
    }

    private static async Task<IngestResult> IngestFilesAsync(
        IReadOnlyList<string> files,
        ItemStore store,
        AppSettings settings,
        EdgeKind edge,
        CancellationToken cancellationToken)
    {
        int added = 0;
        int skipped = 0;
        string? lastError = null;

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(path))
                {
                    // 文件夹暂不摄入：卡片预览与拖出语义都需要具体文件。
                    skipped++;
                    lastError = "暂不支持把文件夹挂上去：" + Path.GetFileName(path);
                    continue;
                }

                if (!File.Exists(path))
                {
                    skipped++;
                    lastError = "文件不存在：" + Path.GetFileName(path);
                    continue;
                }

                var size = new FileInfo(path).Length;
                if (size > settings.MaxSingleFileBytes)
                {
                    skipped++;
                    lastError = "超过单文件上限：" + Path.GetFileName(path) + "（" + FormatMegabytes(size) + "）";
                    continue;
                }

                await store.AddFileAsync(path, edge, cancellationToken).ConfigureAwait(true);
                added++;
            }
            catch (Exception ex) when (ex is IOException
                                       || ex is UnauthorizedAccessException
                                       || ex is NotSupportedException
                                       || ex is ArgumentException)
            {
                skipped++;
                lastError = Path.GetFileName(path) + "：" + ex.Message;
            }
        }

        return new IngestResult(added, skipped, lastError);
    }

    private static string FormatMegabytes(long bytes)
        => (bytes / InteropConstants.BytesPerMegabyteDouble).ToString("0.0") + " MB";
}
