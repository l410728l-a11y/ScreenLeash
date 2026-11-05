using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLeash.Core;

/// <summary>
/// 卡片集合的唯一拥有者：内存列表 + items.json 持久化 + blobs 配额/淘汰 + 孤儿清理。
///
/// <para><b>线程模型（读写对称）</b>：所有对 <c>_items</c> 的读写都在 <see cref="_gate"/> 内完成；
/// 所有写操作经 <see cref="RunOnUiAsync{T}"/> <c>Post</c> 回捕获的 UI 线程再落锁执行（UI 线程自身调用则直接执行），
/// 避免 UI 在遍历快照时被后台线程改集合。读路径返回**克隆快照**（<see cref="Snapshot"/> / <see cref="ForEdge"/>），
/// 调用方拿到后不再持锁，也不共享可变对象。
/// 大块 IO（拷贝原文件、生成缩略图、删孤儿）在调用线程或 <see cref="Task.Run(Action)"/> 上做，
/// 落锁内只做元数据与 items.json 写入——UI 不会被 200MB 的文件拷贝卡住。</para>
///
/// <para><b>持久化</b>：写临时文件 → <c>File.Replace</c> 覆盖 items.json 并保留 .bak；首次写用 Move。
/// 读：items.json 解析失败或 version 越界 → 回退 .bak → 仍失败则空集（绝不因坏文件阻塞启动）。
/// 坏条目（Kind 映射不变量不成立、BlobName 含路径）在读盘时丢弃，不进入 UI。</para>
///
/// <para><b>配额</b>：blobs 总量上限 <see cref="DefaultQuotaBytes"/>（256MB）。超限按
/// <see cref="Item.EffectiveLastAccessTicks"/> 从最冷开始淘汰（刚加入的那张不淘汰，否则「刚挂上去就消失」），
/// 淘汰事件通过 <see cref="ItemsEvicted"/> 抛给托盘提示。</para>
///
/// <para><b>独占</b>：blobs/ 与 thumbs/ 归 ItemStore 独占。启动时先构造 ItemStore 并调用
/// <see cref="CleanupOrphansAsync"/>，此后其它组件只经它读写数据目录。</para>
/// </summary>
public sealed class ItemStore : IDisposable
{
    /// <summary>items.json 的结构版本；读到更高的版本视为不兼容（可能被新版写过），回退 .bak。</summary>
    public const int SchemaVersion = 1;

    /// <summary>blobs 总量上限：256MB。</summary>
    public const long DefaultQuotaBytes = 256L * 1024 * 1024;

    private const string OrphanTempPattern = "*" + AppPaths.TempSuffix;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppPaths _paths;
    private readonly Thumbnailer? _thumbnails;
    private readonly SynchronizationContext? _uiContext;
    private readonly object _gate = new();
    private readonly List<Item> _items = new();

    private bool _disposed;
    private bool _dirty;
    private long _quotaBytes;

    /// <param name="paths">数据目录布局（测试传临时目录）。</param>
    /// <param name="thumbnails">缩略图生成器；为空时 Image/File 卡片不产缩略图（仍需调用方自行传 ThumbName）。</param>
    /// <param name="uiContext">写操作回投的 UI 线程上下文；为空则取 <see cref="SynchronizationContext.Current"/>（通常就是 UI 线程）。</param>
    /// <param name="quotaBytes">blobs 配额；测试可调小。</param>
    public ItemStore(
        AppPaths? paths = null,
        Thumbnailer? thumbnails = null,
        SynchronizationContext? uiContext = null,
        long quotaBytes = DefaultQuotaBytes)
    {
        _paths = paths ?? AppPaths.Default;
        _thumbnails = thumbnails;
        _uiContext = uiContext ?? SynchronizationContext.Current;
        _quotaBytes = quotaBytes > 0 ? quotaBytes : DefaultQuotaBytes;

        _paths.EnsureCreated();
        LoadCore();
    }

    /// <summary>新增卡片后触发（UI 重绘 / 托盘 tooltip 更新）。始终在 UI 线程上抛。</summary>
    public event EventHandler? Changed;

    /// <summary>配额淘汰发生后触发；参数里给出被淘汰的卡片与当前占用，供托盘提示。</summary>
    public event EventHandler<ItemsEvictedEventArgs>? ItemsEvicted;

    public long QuotaBytes => _quotaBytes;

    /// <summary>当前 blobs 占用字节数（Text 卡片不计入）。</summary>
    public long TotalBlobBytes
    {
        get
        {
            lock (_gate)
            {
                return SumBlobBytesCore();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>本次启动是否是从 .bak 回退恢复的（供日志/提示）。</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>全量快照，按 (Edge, Order, Id) 排序，返回克隆——调用方随意遍历，不再持锁。</summary>
    public IReadOnlyList<Item> Snapshot()
    {
        lock (_gate)
        {
            return SortedCloneCore(null);
        }
    }

    /// <summary>某一条边的快照（UI 每屏每边取一次）。</summary>
    public IReadOnlyList<Item> ForEdge(EdgeKind edge)
    {
        lock (_gate)
        {
            return SortedCloneCore(edge);
        }
    }

    public Item? Find(Guid id)
    {
        lock (_gate)
        {
            return FindCore(id)?.Clone();
        }
    }

    /// <summary>文本卡片：内容内联，不落 blob。</summary>
    public Task<Item> AddTextAsync(string text, EdgeKind edge)
    {
        var item = Item.CreateText(text, edge);
        return AddAsync(item);
    }

    /// <summary>
    /// 图片卡片：调用线程先落 blob（PNG 副本），UI 线程提交元数据并生成缩略图 png。
    /// </summary>
    public async Task<Item> AddImageAsync(byte[] imageBytes, string? extension, EdgeKind edge, CancellationToken cancellationToken = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
        {
            throw new ArgumentException("图片字节为空。", nameof(imageBytes));
        }

        _paths.EnsureCreated();
        var normalized = AppPaths.NormalizeExtension(extension);
        var blobName = AppPaths.NewBlobName(normalized.Length > 0 ? normalized : AppPaths.ThumbExtension);
        var blobFull = _paths.BlobPath(blobName) ?? throw new InvalidOperationException("blob 文件名非法。");

        await WriteBlobAsync(blobFull, imageBytes, cancellationToken).ConfigureAwait(false);

        try
        {
            var byteSize = imageBytes.Length;
            return await RunOnUiAsync(() =>
            {
                var thumbName = BuildThumbnailCore(blobFull);
                var item = Item.CreateImage(blobName, thumbName, byteSize, edge);
                return CommitAddCore(item);
            }).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(blobFull);
            throw;
        }
    }

    /// <summary>
    /// 文件卡片：调用线程把原文件复制进 blobs（保留扩展名），UI 线程提交元数据。
    /// </summary>
    /// <summary>会被当作图片处理的扩展名（大小写不敏感）。</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff",
    };

    /// <summary>该路径看起来是图片吗（按扩展名判断，实际解码交给缩略图生成器）。</summary>
    public static bool IsImageFile(string? path) =>
        !string.IsNullOrEmpty(path) && ImageExtensions.Contains(Path.GetExtension(path));

    public async Task<Item> AddFileAsync(string sourcePath, EdgeKind edge, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("源路径为空。", nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("源文件不存在。", sourcePath);
        }

        _paths.EnsureCreated();
        var blobName = AppPaths.NewBlobName(AppPaths.NormalizeExtension(Path.GetExtension(sourcePath)));
        var blobFull = _paths.BlobPath(blobName) ?? throw new InvalidOperationException("blob 文件名非法。");

        await Task.Run(() => File.Copy(sourcePath, blobFull, overwrite: true), cancellationToken).ConfigureAwait(false);
        var byteSize = new FileInfo(blobFull).Length;

        var isImage = IsImageFile(sourcePath);
        try
        {
            return await RunOnUiAsync(() =>
            {
                var displayName = Path.GetFileName(sourcePath);

                if (isImage)
                {
                    // 图片文件必须走 Image 卡片：生成缩略图，卡片区才会显示预览而不是文件名。
                    var thumbName = BuildThumbnailCore(blobFull);
                    return CommitAddCore(Item.CreateImage(blobName, thumbName, byteSize, edge, displayName));
                }

                return CommitAddCore(Item.CreateFile(blobName, thumbName: null, byteSize, edge, displayName));
            }).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(blobFull);
            throw;
        }
    }

    /// <summary>
    /// 提交一张已由调用方备好 blob 的卡片（IngestService 等已落盘的场景）。
    /// 不在 UI 线程时回投 UI 线程提交。
    /// </summary>
    public Task<Item> AddAsync(Item item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var copy = item.Clone();
        return RunOnUiAsync(() => CommitAddCore(copy));
    }

    /// <summary>删除卡片：同时删 blob 与 thumb（删卡即删文件，不留半残）。</summary>
    public Task<bool> RemoveAsync(Guid id)
        => RunOnUiAsync(() => RemoveCore(id));

    /// <summary>跳边移动：目标边 Order = Max+1，源边随后重排为连续整数。</summary>
    public Task<bool> MoveAsync(Guid id, EdgeKind targetEdge)
        => RunOnUiAsync(() => MoveCore(id, targetEdge));

    /// <summary>记录一次真实使用（拖出/复制/选中时调），驱动 LRU。不立即落盘，由下次写入或 Dispose 刷。</summary>
    public Task TouchAsync(Guid id)
        => RunOnUiAsync(() =>
        {
            var item = FindCore(id);
            if (item is null)
            {
                return false;
            }

            item.Touch(DateTimeOffset.UtcNow);
            _dirty = true;
            return true;
        });

    /// <summary>清空全部卡片，并删除所有 blob/thumb 文件。</summary>
    public Task ClearAsync()
        => RunOnUiAsync(() =>
        {
            var removed = _items.Count;
            foreach (var item in _items)
            {
                DeleteItemFilesCore(item);
            }

            _items.Clear();
            _dirty = true;
            SaveCore();
            RaiseChanged();
            return removed;
        });

    /// <summary>落盘（有未保存的脏数据时）。Dispose 会自动调用。</summary>
    public Task FlushAsync()
        => RunOnUiAsync(() =>
        {
            SaveCore();
            return true;
        });

    /// <summary>
    /// 启动时的孤儿清理：blobs/ 与 thumbs/ 中无任何 Item 引用的文件、以及上次崩溃残留的 *.tmp 全部删除。
    /// 返回删除的文件数。
    /// </summary>
    /// <summary>
    /// 一次性兼容升级：旧版本把图片文件一律存成了 <see cref="Kind.File"/>（thumbName 为 null），
    /// 卡片区只画文件名。这里把它们原地转成 <see cref="Kind.Image"/> 并补缩略图——
    /// blob 内容不变，只是让预览能显示出来。返回被升级的卡片数。
    /// </summary>
    public Task<int> UpgradeImageFileItemsAsync(CancellationToken cancellationToken = default)
        => RunOnUiAsync(() =>
        {
            var upgraded = 0;
            foreach (var item in _items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item.Kind != Kind.File || !item.HasBlob || !IsImageFile(item.BlobName))
                {
                    continue;
                }

                var blobFull = _paths.BlobPath(item.BlobName);
                if (blobFull is null || !File.Exists(blobFull))
                {
                    continue;
                }

                item.Kind = Kind.Image;
                item.ThumbName = BuildThumbnailCore(blobFull);
                _dirty = true;
                upgraded++;
            }

            if (upgraded > 0)
            {
                SaveCore();
                RaiseChanged();
            }

            return upgraded;
        });

    public async Task<int> CleanupOrphansAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        HashSet<string> referencedBlobs;
        HashSet<string> referencedThumbs;
        lock (_gate)
        {
            referencedBlobs = new HashSet<string>(
                _items.Where(i => !string.IsNullOrEmpty(i.BlobName)).Select(i => i.BlobName!),
                StringComparer.OrdinalIgnoreCase);
            referencedThumbs = new HashSet<string>(
                _items.Where(i => !string.IsNullOrEmpty(i.ThumbName)).Select(i => i.ThumbName!),
                StringComparer.OrdinalIgnoreCase);
        }

        var removed = await Task.Run(
            () => SweepDirectory(_paths.BlobDirectory, referencedBlobs) + SweepDirectory(_paths.ThumbDirectory, referencedThumbs),
            cancellationToken).ConfigureAwait(false);

        // 文件被动过：下次写入时让 items.json 跟上（字节数仍以 Item.ByteSize 为准，
        // 孤儿文件从不计入配额，所以这里不需要重算）。
        lock (_gate)
        {
            _dirty = true;
        }

        return removed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                SaveCore();
            }
            catch (Exception ex)
            {
                Log("Dispose 落盘失败：" + ex.Message);
            }

            _disposed = true;
        }
    }

    // ---------- 内部：写核心（调用方已持有 _gate，且运行在 UI 线程） ----------

    private Item CommitAddCore(Item item)
    {
        if (!item.Validate(out var error))
        {
            throw new ArgumentException("卡片不合法：" + error, nameof(item));
        }

        if (FindCore(item.Id) is not null)
        {
            throw new InvalidOperationException("卡片 Id 已存在：" + item.Id);
        }

        if (item.LastAccessTicks == 0)
        {
            item.LastAccessTicks = item.CreatedAt.UtcTicks;
        }

        item.Order = MaxOrderCore(item.Edge) + 1;
        _items.Add(item);
        _dirty = true;

        EvictToQuotaCore(item.Id);
        SaveCore();
        RaiseChanged();
        return item.Clone();
    }

    private bool RemoveCore(Guid id)
    {
        var item = FindCore(id);
        if (item is null)
        {
            return false;
        }

        DeleteItemFilesCore(item);
        _items.Remove(item);
        _dirty = true;
        CompactOrdersCore(item.Edge);
        SaveCore();
        RaiseChanged();
        return true;
    }

    private bool MoveCore(Guid id, EdgeKind targetEdge)
    {
        var item = FindCore(id);
        if (item is null)
        {
            return false;
        }

        if (item.Edge.Equals(targetEdge))
        {
            return true;
        }

        var previousEdge = item.Edge;
        item.Edge = targetEdge;
        item.Order = MaxOrderCore(targetEdge) + 1;
        _dirty = true;

        CompactOrdersCore(previousEdge);
        CompactOrdersCore(targetEdge);
        SaveCore();
        RaiseChanged();
        return true;
    }

    private int MaxOrderCore(EdgeKind edge)
    {
        var max = -1;
        foreach (var item in _items)
        {
            if (item.Edge.Equals(edge) && item.Order > max)
            {
                max = item.Order;
            }
        }

        return max;
    }

    /// <summary>把一条边内的 Order 压成 0..n-1（保持既有相对顺序），避免长跑后 Order 无界增长。</summary>
    private void CompactOrdersCore(EdgeKind edge)
    {
        var ordered = _items.Where(i => i.Edge.Equals(edge))
            .OrderBy(i => i.Order)
            .ThenBy(i => i.Id)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Order != i)
            {
                ordered[i].Order = i;
                _dirty = true;
            }
        }
    }

    /// <summary>超配额时按 LRU 淘汰（最冷优先），保留 <paramref name="keepId"/>。</summary>
    private void EvictToQuotaCore(Guid keepId)
    {
        if (SumBlobBytesCore() <= _quotaBytes)
        {
            return;
        }

        var evicted = new List<Item>();
        while (SumBlobBytesCore() > _quotaBytes)
        {
            Item? victim = null;
            foreach (var item in _items)
            {
                if (!item.HasBlob || item.Id == keepId)
                {
                    continue;
                }

                if (victim is null || item.EffectiveLastAccessTicks < victim.EffectiveLastAccessTicks)
                {
                    victim = item;
                }
            }

            if (victim is null)
            {
                // 只剩刚加入的这一张且它本身就超配额：保留（入口处已按 Settings 的单文件上限拒绝），
                // 不能把它也删掉——用户刚挂上去的东西不能凭空消失。
                break;
            }

            DeleteItemFilesCore(victim);
            _items.Remove(victim);
            CompactOrdersCore(victim.Edge);
            evicted.Add(victim.Clone());
            _dirty = true;
        }

        if (evicted.Count > 0)
        {
            var total = SumBlobBytesCore();
            ItemsEvicted?.Invoke(this, new ItemsEvictedEventArgs(evicted, total, _quotaBytes));
        }
    }

    private void DeleteItemFilesCore(Item item)
    {
        var blob = _paths.BlobPath(item.BlobName);
        if (blob is not null)
        {
            TryDeleteFile(blob);
        }

        var thumb = _paths.ThumbPath(item.ThumbName);
        if (thumb is not null)
        {
            TryDeleteFile(thumb);
        }
    }

    private long SumBlobBytesCore()
    {
        long total = 0;
        foreach (var item in _items)
        {
            if (item.HasBlob)
            {
                total += Math.Max(0, item.ByteSize);
            }
        }

        return total;
    }

    private Item? FindCore(Guid id)
    {
        foreach (var item in _items)
        {
            if (item.Id == id)
            {
                return item;
            }
        }

        return null;
    }

    private IReadOnlyList<Item> SortedCloneCore(EdgeKind? edge)
    {
        var query = edge is null
            ? _items.AsEnumerable()
            : _items.Where(i => i.Edge.Equals(edge.Value));

        return query
            .OrderBy(i => i, ItemOrderComparer.Instance)
            .Select(i => i.Clone())
            .ToList();
    }

    /// <summary>为刚落盘的 blob 生成缩略图（在 UI 线程执行，只做一次性的 png 生成；失败返回 null）。</summary>
    private string? BuildThumbnailCore(string blobFullPath)
    {
        if (_thumbnails is null)
        {
            return null;
        }

        var thumbName = AppPaths.NewThumbName();
        var thumbFull = _paths.ThumbPath(thumbName);
        if (thumbFull is null)
        {
            return null;
        }

        try
        {
            return _thumbnails.EnsureThumbnailFile(blobFullPath, thumbFull) ? thumbName : null;
        }
        catch (Exception ex)
        {
            Log("缩略图生成失败：" + ex.Message);
            return null;
        }
    }

    // ---------- 内部：持久化 ----------

    private void LoadCore()
    {
        var document = TryReadDocument(_paths.ItemsFile);
        if (document is null && File.Exists(_paths.ItemsBackupFile))
        {
            document = TryReadDocument(_paths.ItemsBackupFile);
            if (document is not null)
            {
                RecoveredFromBackup = true;
                Log("items.json 不可用，已从 items.json.bak 恢复。");
            }
        }

        _items.Clear();
        if (document is null)
        {
            return;
        }

        var dropped = 0;
        foreach (var item in document.Items)
        {
            if (item is null || !item.Validate(out var error))
            {
                dropped++;
                continue;
            }

            // blob 丢失的卡片没有内容可挂：丢弃而不是留一张打不开的空卡。
            if (item.HasBlob)
            {
                var blobFull = _paths.BlobPath(item.BlobName);
                if (blobFull is null || !File.Exists(blobFull))
                {
                    dropped++;
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(item.ThumbName))
            {
                var thumbFull = _paths.ThumbPath(item.ThumbName);
                if (thumbFull is null || !File.Exists(thumbFull))
                {
                    item.ThumbName = null;
                }
            }

            _items.Add(item);
        }

        if (dropped > 0)
        {
            Log($"读盘丢弃 {dropped} 张不合法/失效卡片。");
            _dirty = true;
        }

        CompactOrdersCoreForAllEdgesCore();
    }

    private void CompactOrdersCoreForAllEdgesCore()
    {
        foreach (var edge in Enum.GetValues<EdgeKind>())
        {
            CompactOrdersCore(edge);
        }
    }

    private static ItemsDocument? TryReadDocument(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<ItemsDocument>(json, JsonOptions);
            if (document is null)
            {
                return null;
            }

            if (document.Version < 1 || document.Version > SchemaVersion)
            {
                return null;
            }

            document.Items ??= new List<Item>();
            return document;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>原子写：temp → File.Replace（保留 .bak）。任何异常只记日志，绝不因写盘失败崩 UI。</summary>
    private void SaveCore()
    {
        if (!_dirty || _disposed)
        {
            return;
        }

        string? temp = null;
        try
        {
            _paths.EnsureCreated();
            var document = new ItemsDocument
            {
                Version = SchemaVersion,
                Items = SortedCloneCore(null).ToList(),
            };

            var json = JsonSerializer.Serialize(document, JsonOptions);
            temp = _paths.ItemsTempFile;
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_paths.ItemsFile))
            {
                File.Replace(temp, _paths.ItemsFile, _paths.ItemsBackupFile, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _paths.ItemsFile);
            }

            temp = null;
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
        {
            Log("items.json 写入失败：" + ex.Message);
        }
        finally
        {
            if (temp is not null)
            {
                TryDeleteFile(temp);
            }
        }
    }

    // ---------- 内部：线程 ----------

    private Task<T> RunOnUiAsync<T>(Func<T> work)
    {
        var context = _uiContext;
        if (context is null || ReferenceEquals(SynchronizationContext.Current, context))
        {
            return Task.FromResult(InvokeLocked(work));
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(
            _ =>
            {
                try
                {
                    tcs.TrySetResult(InvokeLocked(work));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            null);

        return tcs.Task;
    }

    private T InvokeLocked<T>(Func<T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return work();
        }
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 内部：文件 ----------

    private static Task WriteBlobAsync(string fullPath, byte[] bytes, CancellationToken cancellationToken)
        => Task.Run(() => File.WriteAllBytes(fullPath, bytes), cancellationToken);

    private static int SweepDirectory(string directory, HashSet<string> referenced)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(directory);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var isTemp = name.EndsWith(AppPaths.TempSuffix, StringComparison.OrdinalIgnoreCase);
            if (!isTemp && referenced.Contains(name))
            {
                continue;
            }

            if (TryDeleteFile(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDeleteFile(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            File.Delete(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogDirectory);
            File.AppendAllText(
                Path.Combine(_paths.LogDirectory, "store.log"),
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 日志失败不影响数据路径。
        }
    }
}

/// <summary>items.json 的根对象。</summary>
public sealed class ItemsDocument
{
    public int Version { get; set; } = ItemStore.SchemaVersion;

    public List<Item> Items { get; set; } = new();
}

/// <summary>配额淘汰提示事件参数。</summary>
public sealed class ItemsEvictedEventArgs : EventArgs
{
    public ItemsEvictedEventArgs(IReadOnlyList<Item> items, long totalBlobBytes, long quotaBytes)
    {
        Items = items;
        TotalBlobBytes = totalBlobBytes;
        QuotaBytes = quotaBytes;
    }

    /// <summary>本次被淘汰的卡片（LRU 最冷优先）。</summary>
    public IReadOnlyList<Item> Items { get; }

    public long TotalBlobBytes { get; }

    public long QuotaBytes { get; }
}
