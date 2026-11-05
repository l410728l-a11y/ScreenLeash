using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ScreenLeash.Core;

/// <summary>
/// 缩略图生成与缓存。两层缓存：
/// <list type="number">
/// <item><b>磁盘</b>：thumbs/&lt;guid&gt;.png。目标文件比原图新即视为命中，不重复解码大图（截图拖入最贵的是解码）。</item>
/// <item><b>内存</b>：<see cref="MemoryCacheCapacity"/> 张的 LRU，淘汰/清空时 <c>Dispose</c> 掉 Bitmap——
/// 32 张 88dip 缩略图约 2～3MB，不会把常驻内存推上去。</item>
/// </list>
/// <para><b>所有权约定（重要）</b>：<see cref="GetOrLoad"/> / <see cref="GetOrLoadFromBlob"/> 返回的 Bitmap
/// <b>归缓存所有，调用方不得 Dispose</b>（Div 绘制直接用它）；需要独占副本（后台线程、拖拽数据对象）时用
/// <see cref="CreateCopy"/>，用完自行 Dispose。</para>
/// <para>所有尺寸经 <see cref="Metrics.Px"/> 从 DIP 换算，本文件不出现像素常量。</para>
/// </summary>
public sealed class Thumbnailer : IDisposable
{
    /// <summary>内存中最多保留的缩略图张数。</summary>
    public const int MemoryCacheCapacity = 32;

    /// <summary>GDI+ 的基准 DPI，仅作为「调用方没有提供 DPI 来源」时的兜底。</summary>
    private const int FallbackDpi = 96;

    private readonly Func<int> _dpiProvider;
    private readonly object _gate = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <param name="dpiProvider">当前屏幕 DPI（WinForms 侧传 <c>() =&gt; form.DeviceDpi</c>）；为空时按 96 处理。</param>
    public Thumbnailer(Func<int>? dpiProvider = null)
    {
        _dpiProvider = dpiProvider ?? (() => FallbackDpi);
    }

    /// <summary>缩略图边长的 DIP 值（单一出口在 Metrics）。</summary>
    public static int ThumbSizeDip => Metrics.ThumbSize;

    /// <summary>缩略图边长的实际像素值。</summary>
    public int ThumbSizePx => Metrics.Px(Metrics.ThumbSize, CurrentDpi);

    public int CurrentDpi => _dpiProvider();

    public int CachedCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>
    /// 生成/复用缩略图 png。返回 false 表示源不可读（调用方保留卡片但不显示预览）。
    /// </summary>
    public bool EnsureThumbnailFile(string sourceFullPath, string thumbFullPath, int maxPx = 0)
    {
        if (string.IsNullOrEmpty(sourceFullPath) || string.IsNullOrEmpty(thumbFullPath))
        {
            return false;
        }

        if (!File.Exists(sourceFullPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(thumbFullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 磁盘缓存命中：缩略图不旧于原图就直接复用。
        if (File.Exists(thumbFullPath)
            && File.GetLastWriteTimeUtc(thumbFullPath) >= File.GetLastWriteTimeUtc(sourceFullPath))
        {
            return true;
        }

        var target = maxPx > 0 ? maxPx : ThumbSizePx;

        using var stream = new MemoryStream(File.ReadAllBytes(sourceFullPath), writable: false);
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);

        var size = FitInside(source.Width, source.Height, target);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return false;
        }

        using var thumb = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(thumb))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height));
        }

        var temp = thumbFullPath + AppPaths.TempSuffix;
        thumb.Save(temp, ImageFormat.Png);
        if (File.Exists(thumbFullPath))
        {
            File.Delete(thumbFullPath);
        }

        File.Move(temp, thumbFullPath);
        return true;
    }

    /// <summary>取缩略图（内存 LRU → 磁盘 png）。返回对象归缓存所有，调用方不得 Dispose。</summary>
    public Bitmap? GetOrLoad(string? thumbFullPath)
    {
        if (string.IsNullOrEmpty(thumbFullPath))
        {
            return null;
        }

        var key = thumbFullPath;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_index.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                return hit.Value.Bitmap;
            }
        }

        var loaded = LoadBitmapFile(key);
        if (loaded is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                loaded.Dispose();
                return null;
            }

            if (_index.TryGetValue(key, out var raced))
            {
                // 并发加载同一张：保留先到的，丢弃自己这份。
                _lru.Remove(raced);
                _lru.AddFirst(raced);
                loaded.Dispose();
                return raced.Value.Bitmap;
            }

            return AddLocked(key, loaded);
        }
    }

    /// <summary>
    /// 没有现成缩略图时，从 blob 现算一张（缩略图生成失败的兜底，只在内存缓存里）。
    /// </summary>
    public Bitmap? GetOrLoadFromBlob(string? blobFullPath, string cacheKey)
    {
        if (string.IsNullOrEmpty(blobFullPath) || !File.Exists(blobFullPath))
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_index.TryGetValue(cacheKey, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                return hit.Value.Bitmap;
            }
        }

        var built = LoadScaled(blobFullPath, ThumbSizePx);
        if (built is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                built.Dispose();
                return null;
            }

            if (_index.TryGetValue(cacheKey, out var raced))
            {
                _lru.Remove(raced);
                _lru.AddFirst(raced);
                built.Dispose();
                return raced.Value.Bitmap;
            }

            return AddLocked(cacheKey, built);
        }
    }

    /// <summary>给调用方一份独占副本，用完由调用方 Dispose。缓存未命中或已释放时返回 null。</summary>
    public Bitmap? CreateCopy(string? thumbFullPath)
    {
        var cached = GetOrLoad(thumbFullPath);
        if (cached is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            try
            {
                return new Bitmap(cached);
            }
            catch (ArgumentException)
            {
                // 缓存项刚好被并发淘汰并 Dispose。
                return null;
            }
        }
    }

    /// <summary>主动收缩内存缓存（例如 DPI 变更后缩略图尺寸全部失效时）。</summary>
    public void ClearMemory()
    {
        lock (_gate)
        {
            DisposeCacheLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeCacheLocked();
        }
    }

    private Bitmap? AddLocked(string key, Bitmap bitmap)
    {
        var node = _lru.AddFirst(new CacheEntry(key, bitmap));
        _index[key] = node;
        TrimLocked();
        // Trim 可能淘汰刚插入的这张（容量配置异常时），此时返回 null 由调用方兜底。
        return _index.ContainsKey(key) ? bitmap : null;
    }

    private void TrimLocked()
    {
        while (_index.Count > MemoryCacheCapacity)
        {
            var last = _lru.Last;
            if (last is null)
            {
                return;
            }

            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
            last.Value.Bitmap.Dispose();
        }
    }

    private void DisposeCacheLocked()
    {
        foreach (var entry in _lru)
        {
            entry.Bitmap.Dispose();
        }

        _lru.Clear();
        _index.Clear();
    }

    private static Bitmap? LoadBitmapFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return null;
        }
    }

    private Bitmap? LoadScaled(string path, int maxPx)
    {
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            var size = FitInside(source.Width, source.Height, maxPx);
            if (size.Width <= 0 || size.Height <= 0)
            {
                return null;
            }

            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size.Width, size.Height));
            }

            return bitmap;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return null;
        }
    }

    /// <summary>等比缩放到不超过 <paramref name="maxPx"/>（不放大），保证 1:1 与极端长宽比都不失真。</summary>
    private static Size FitInside(int width, int height, int maxPx)
    {
        if (width <= 0 || height <= 0 || maxPx <= 0)
        {
            return Size.Empty;
        }

        if (width <= maxPx && height <= maxPx)
        {
            return new Size(width, height);
        }

        var scale = Math.Min((double)maxPx / width, (double)maxPx / height);
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        return new Size(w, h);
    }

    private readonly struct CacheEntry
    {
        public CacheEntry(string key, Bitmap bitmap)
        {
            Key = key;
            Bitmap = bitmap;
        }

        public string Key { get; }

        public Bitmap Bitmap { get; }
    }
}
