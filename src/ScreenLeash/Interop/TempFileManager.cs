using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ScreenLeash.Core;

namespace ScreenLeash.Interop;

/// <summary>
/// 拖出用的临时文件生命周期。
///
/// <para>为什么需要它：拖到微信/QQ 这类目标时，<c>DoDragDrop</c> 返回后目标可能仍在异步读取
/// 文件路径；立刻删除会让对方拿到「文件不存在」。所以按 <see cref="AppSettings.DragOutRetentionSeconds"/>
/// 延迟删除，并在下次启动时清掉上次异常退出留下的残留。</para>
///
/// <para>文件统一放在 <c>%TEMP%\ScreenLeash\</c> 下、统一前缀 <see cref="FilePrefix"/>，
/// 与用户其它临时文件隔离，清理时可以放心按前缀扫。</para>
/// </summary>
public static class TempFileManager
{
    /// <summary>拖出临时文件前缀：清理时只认这个前缀。</summary>
    public const string FilePrefix = "leash-out-";

    private static readonly object Gate = new();

    /// <summary>延迟删除的定时器引用：必须持有，否则被 GC 回收后回调不再触发。</summary>
    private static readonly List<System.Threading.Timer> Pending = new();

    /// <summary>临时目录：<c>%TEMP%\ScreenLeash</c>。</summary>
    public static string RootDirectory => Path.Combine(Path.GetTempPath(), AppPaths.AppFolderName);

    /// <summary>把文本写成临时 .txt，供不支持 UnicodeText 的目标通过 FileDrop 拿到内容。</summary>
    public static string CreateForText(string text, string? extension = null)
    {
        var name = FilePrefix + Guid.NewGuid().ToString("N") + NormalizeExtension(extension);
        return WriteFile(name, Encoding.UTF8.GetBytes(text ?? string.Empty));
    }

    /// <summary>把图片字节写成临时文件，保留目标可识别的扩展名（默认 .png）。</summary>
    public static string CreateForImage(byte[] bytes, string? extension = null)
    {
        var name = FilePrefix + Guid.NewGuid().ToString("N") + NormalizeExtension(extension);
        return WriteFile(name, bytes ?? Array.Empty<byte>());
    }

    /// <summary>
    /// 延迟删除：<paramref name="delaySeconds"/> 秒后尝试删除，失败则留待下次启动清理。
    /// 不抛异常——拖出成功与否不该被清理逻辑影响。
    /// </summary>
    public static void ScheduleDelete(string? path, int delaySeconds)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        var due = TimeSpan.FromSeconds(Math.Max(AppSettings.MinDragOutRetentionSeconds, delaySeconds));
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(
            _ =>
            {
                TryDelete(path);
                lock (Gate)
                {
                    Pending.Remove(timer!);
                }

                timer?.Dispose();
            },
            null,
            due,
            Timeout.InfiniteTimeSpan);

        lock (Gate)
        {
            Pending.Add(timer);
        }
    }

    /// <summary>立即尝试删除（内部用，UI 线程不要调用）。</summary>
    public static bool TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>清理上次运行残留的拖出临时文件（只删本程序前缀，不碰其它临时文件）。返回删除数量。</summary>
    public static int CleanupStale()
    {
        var root = RootDirectory;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(root, FilePrefix + "*");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var file in files)
        {
            if (TryDelete(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private static string WriteFile(string fileName, byte[] bytes)
    {
        var root = RootDirectory;
        Directory.CreateDirectory(root);
        var full = Path.Combine(root, fileName);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private static string NormalizeExtension(string? extension)
    {
        var normalized = AppPaths.NormalizeExtension(extension);
        return normalized.Length > 0 ? normalized : AppPaths.ThumbExtension;
    }
}
