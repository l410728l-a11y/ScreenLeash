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
    /// 进程级「正在向外拖」标志。必须是静态的：从右边绳拖到左边绳时，
    /// 目标是**另一个** LeashForm 实例，实例字段看不到源窗口的状态，
    /// 就会把这次「自己拖自己」当成外部拖入，凭空多出一张卡片（用户报的重复文件）。
    /// </summary>
    public static bool IsDragging { get; private set; }
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

        IsDragging = true;
        try
        {
            // 按卡片类型**只给一种**格式：多格式并给等于把选择权交给目标程序，
            // 微信/QQ 会优先取 FileDrop 里的临时 .txt，把一段文字变成"打不开的文件"。
            // 用户要求：拖进去是什么，拖出来就是什么。
            switch (item.Kind)
            {
                case Kind.Text:
                    // 纯文字：给两种文本格式。
                    // UnicodeText 是首选；但 Electron/WebView2 外壳的输入框（如天枢）
                    // 只请求 ANSI 的 CF_TEXT，只给 UnicodeText 时它们收不到内容
                    // （用户实测：同一张文字卡片拖到微信可以、拖到天枢不行）。
                    // 两者都是文本格式，不会像 FileDrop 那样被目标误判成文件或图片。
                    if (!string.IsNullOrEmpty(item.Text))
                    {
                        data.SetData(DataFormats.UnicodeText, item.Text);
                        data.SetData(DataFormats.Text, item.Text);
                    }

                    break;

                case Kind.Image:
                    // 图片：FileDrop 给真实图片文件（拖到桌面=复制文件、拖到微信=发图），
                    // Bitmap 作为不接受文件目标的兜底。
                    var imagePath = ResolveFilePath(item, paths, tempFiles);
                    if (imagePath is not null)
                    {
                        data.SetData(DataFormats.FileDrop, new[] { imagePath });
                    }

                    if (thumbnails is not null)
                    {
                        ownedBitmap = TryLoadBitmap(paths.ThumbPath(item.ThumbName), thumbnails);
                        if (ownedBitmap is not null)
                        {
                            data.SetData(DataFormats.Bitmap, ownedBitmap);
                        }
                    }

                    break;

                default:
                    // 文件：只给 FileDrop，拖出去就是一个文件。
                    var filePath = ResolveFilePath(item, paths, tempFiles);
                    if (filePath is not null)
                    {
                        data.SetData(DataFormats.FileDrop, new[] { filePath });
                    }

                    break;
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
            IsDragging = false;
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

        return item.DisplayName ?? System.IO.Path.GetFileName(item.BlobName);
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
