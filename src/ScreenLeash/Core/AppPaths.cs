using System;
using System.IO;
using System.Text;

namespace ScreenLeash.Core;

/// <summary>
/// 磁盘布局的单一出口。数据根为 <c>%LOCALAPPDATA%\ScreenLeash\</c>（Local，非 Roaming——
/// 卡片是机器本地产物，漫游只会拖慢登录且可能与其它机器的 blobs 冲突）。
/// Item 只保存相对文件名（<see cref="Item.BlobName"/> / <see cref="Item.ThumbName"/>），
/// 任何绝对路径都必须经 <see cref="BlobPath"/> / <see cref="ThumbPath"/> 派生，杜绝把用户目录写进 items.json。
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "ScreenLeash";
    public const string BlobFolderName = "blobs";
    public const string ThumbFolderName = "thumbs";
    public const string LogFolderName = "logs";
    public const string InboxFolderName = "inbox";
    public const string ItemsFileName = "items.json";

    public const string SettingsFileName = "settings.json";
    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";
    public const string ThumbExtension = ".png";

    /// <summary>扩展名过滤后最长保留的字符数（超长扩展名一律截断，避免异常文件名拖垮路径长度）。</summary>
    private const int MaxExtensionLength = 16;

    private static readonly Lazy<AppPaths> LazyDefault = new(() => new AppPaths(DefaultRoot()));

    public AppPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("数据根不能为空。", nameof(root));
        }

        Root = Path.GetFullPath(root);
    }

    /// <summary>进程级默认数据根。测试/探针请自行 new 一个指向临时目录的实例。</summary>
    public static AppPaths Default => LazyDefault.Value;

    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

    public string Root { get; }

    public string BlobDirectory => Path.Combine(Root, BlobFolderName);

    public string ThumbDirectory => Path.Combine(Root, ThumbFolderName);

    public string LogDirectory => Path.Combine(Root, LogFolderName);

    /// <summary>
    /// 外部写入通道：别的程序（如天枢插件）把待挂内容写成 JSON 放这里，本程序扫描后落卡。
    /// 只接受 <c>.json</c>；写入方应先写 <c>.json.tmp</c> 再改名，否则可能被读到半个文件。
    /// </summary>
    public string InboxDirectory => Path.Combine(Root, InboxFolderName);

    /// <summary>已处理完的收件文件归档（移进来就不会再被扫描，防重复落卡）。</summary>
    public string InboxDoneDirectory => Path.Combine(InboxDirectory, "done");

    /// <summary>坏文件归档：解析失败或整体被拒时移进来，留档排查，不自动删除。</summary>
    public string InboxBadDirectory => Path.Combine(InboxDirectory, "bad");

    public string ItemsFile => Path.Combine(Root, ItemsFileName);

    /// <summary>用户设置文件（四边启用、钩子开关等）；与 items.json 同目录但不参与卡片事务。</summary>
    public string SettingsFile => Path.Combine(Root, SettingsFileName);

    /// <summary>上一次成功写入的版本；<c>File.Replace</c> 的 backup 参数落在这里。</summary>
    public string ItemsBackupFile => ItemsFile + BackupSuffix;

    /// <summary>原子写的临时文件（必须与 items.json 同卷同目录，File.Replace 才能保留 .bak）。</summary>
    public string ItemsTempFile => ItemsFile + TempSuffix;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BlobDirectory);
        Directory.CreateDirectory(ThumbDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(InboxDirectory);
        Directory.CreateDirectory(InboxDoneDirectory);
        Directory.CreateDirectory(InboxBadDirectory);
    }

    /// <summary>相对文件名 → blobs/ 下的绝对路径；名字非法（含分隔符/绝对路径/遍历）时返回 null。</summary>
    public string? BlobPath(string? fileName) => Resolve(BlobDirectory, fileName);

    /// <summary>相对文件名 → thumbs/ 下的绝对路径；名字非法时返回 null。</summary>
    public string? ThumbPath(string? fileName) => Resolve(ThumbDirectory, fileName);

    public static string? Resolve(string directory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // 只接受「裸文件名」：不允许目录分隔符、卷号、根路径或上跳。
        if (fileName.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0)
        {
            return null;
        }

        if (fileName == "." || fileName == "..")
        {
            return null;
        }

        return Path.Combine(directory, fileName);
    }

    /// <summary>为 blob 生成唯一相对文件名，保留扩展名（Kind=File/Image 拖出时要靠它还原真实类型）。</summary>
    public static string NewBlobName(string? extension)
        => Guid.NewGuid().ToString("N") + NormalizeExtension(extension);

    /// <summary>为缩略图生成唯一相对文件名（统一 png）。</summary>
    public static string NewThumbName()
        => Guid.NewGuid().ToString("N") + ThumbExtension;

    /// <summary>清洗扩展名：只留字母数字与点，长度受限；不合法时返回空串。</summary>
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(extension.Length + 1);
        foreach (var ch in extension.Trim())
        {
            if (ch == '.' || char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        var cleaned = sb.ToString();
        while (cleaned.StartsWith('.'))
        {
            cleaned = cleaned.Substring(1);
        }

        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        if (cleaned.Length > MaxExtensionLength)
        {
            cleaned = cleaned.Substring(0, MaxExtensionLength);
        }

        return "." + cleaned;
    }
}
