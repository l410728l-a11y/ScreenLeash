using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ScreenLeash.Core;

namespace ScreenLeash.Interop;

/// <summary>
/// 把一张卡片拖出去。<b>只允许 Copy</b>：DataObject 不声明 Move，
/// 这样即使目标应用想「移动」，也不可能把我们 blobs/ 里的原文件搬走或删掉。
///
/// <para>同时提供多种格式，让不同目标各取所需：</para>
/// <list type="bullet">
/// <item><b>FileDrop</b>：Explorer/桌面 → 复制文件；微信/QQ 的文件框 → 发文件。</item>
/// <item><b>Bitmap</b>：聊天窗口的图片粘贴区 → 直接发图（不依赖文件路径）。</item>
/// <item><b>UnicodeText</b>：编辑器/地址栏 → 粘贴文本或文件路径。</item>
/// </list>
///
/// <para>文本卡片没有 blob，FileDrop 用 <see cref="TempFileManager"/> 生成的临时 .txt 承载，
/// 并在拖放结束后按 Settings 延迟删除——立即删会让异步读取的目标拿到空文件。</para>
/// </summary>
public static class DragOut
{
    /// <summary>
    /// 发起拖出。必须在 UI 线程、由鼠标按下+移动的事件里调用（<c>DoDragDrop</c> 是同步模态消息循环）。
    /// </summary>
    /// <param name="source">拖放源控件（通常是卡片所在的绳子窗口）。</param>
    /// <param name="item">要拖出的卡片。</param>
    /// <param name="paths">数据目录（用于取 blob 的真实路径）。</param>
    /// <param name="thumbnails">缩略图缓存（图片卡片用它取位图副本）。</param>
    /// <param name="settings">设置（读取延迟删除秒数）。</param>
    /// <returns>目标实际接受的拖放效果。</returns>
    public static DragDropEffects Start(
        Control source,
        Item item,
        AppPaths paths,
        Thumbnailer? thumbnails,
        AppSettings settings)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        paths ??= AppPaths.Default;
        settings ??= new AppSettings();

        var data = new DataObject();
        var tempFiles = new List<string>();
        Image? ownedBitmap = null;

        try
        {
            // ── FileDrop ──
            var filePath = ResolveFilePath(item, paths, tempFiles);
            if (filePath is not null)
            {
                data.SetData(DataFormats.FileDrop, new[] { filePath });
            }

            // ── UnicodeText ──
            var text = ResolveText(item);
            if (!string.IsNullOrEmpty(text))
            {
                data.SetData(DataFormats.UnicodeText, text);
            }

            // ── Bitmap ──
            if (item.Kind == Kind.Image && thumbnails is not null)
            {
                ownedBitmap = TryLoadBitmap(paths.ThumbPath(item.ThumbName), thumbnails);
                if (ownedBitmap is not null)
                {
                    data.SetData(DataFormats.Bitmap, ownedBitmap);
                }
            }

            if (data.GetFormats().Length == 0)
            {
                return DragDropEffects.None;
            }

            var effect = source.DoDragDrop(data, DragDropEffects.Copy);

            foreach (var temp in tempFiles)
            {
                TempFileManager.ScheduleDelete(temp, settings.DragOutRetentionSeconds);
            }

            return effect;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
                                   || ex is InvalidOperationException
                                   || ex is ArgumentException)
        {
            // 拖放失败（目标拒绝/COM 异常）不该影响绳子本身。
            foreach (var temp in tempFiles)
            {
                TempFileManager.ScheduleDelete(temp, settings.DragOutRetentionSeconds);
            }

            return DragDropEffects.None;
        }
        finally
        {
            ownedBitmap?.Dispose();
        }
    }

    /// <summary>卡片对应的可拖出文件路径：blob 真实路径优先，文本卡片落到临时 .txt。</summary>
    private static string? ResolveFilePath(Item item, AppPaths paths, List<string> tempFiles)
    {
        if (item.HasBlob)
        {
            var full = paths.BlobPath(item.BlobName);
            return full is not null && System.IO.File.Exists(full) ? full : null;
        }

        if (item.Kind == Kind.Text && !string.IsNullOrEmpty(item.Text))
        {
            var temp = TempFileManager.CreateForText(item.Text);
            tempFiles.Add(temp);
            return temp;
        }

        return null;
    }

    /// <summary>UnicodeText 格式的内容：文本卡片给正文，其它给文件名（拖到输入框即得路径/名称）。</summary>
    private static string? ResolveText(Item item)
    {
        if (item.Kind == Kind.Text)
        {
            return item.Text;
        }

        if (string.IsNullOrEmpty(item.BlobName))
        {
            return null;
        }

        return System.IO.Path.GetFileName(item.BlobName);
    }

    private static Bitmap? TryLoadBitmap(string? thumbFullPath, Thumbnailer thumbnails)
    {
        try
        {
            var copy = thumbnails.CreateCopy(thumbFullPath);
            return copy;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is OutOfMemoryException)
        {
            return null;
        }
    }
}
