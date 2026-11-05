using System;
using System.Collections.Generic;
using System.Text;

namespace ScreenLeash.Core;

/// <summary>卡片内容的三种权威形态；语义见 <see cref="Item"/> 的类型注释。</summary>
public enum Kind
{
    Image,
    Text,
    File,
}

/// <summary>
/// 绳子上一张卡片的持久化模型。
///
/// <para><b>Kind 权威映射</b>（唯一解释点，其它任何文件都不得自行解释 Kind 的含义）：</para>
/// <list type="bullet">
/// <item><c>Image</c>：<see cref="BlobName"/> 指向 blobs/ 下的原图副本。拖出时优先给真实文件路径，
/// 目标不接受文件（如聊天窗口只收位图）时补 Bitmap 格式。</item>
/// <item><c>Text</c>：内容内联在 <see cref="Text"/>，<b>不落 blob</b>，<see cref="BlobName"/> 必须为 null。</item>
/// <item><c>File</c>：<see cref="BlobName"/> 是原文件副本，<b>保留原扩展名</b>（拖出后仍能被系统按类型打开）。</item>
/// </list>
///
/// <para><b>排序</b>：边内单调整数 <see cref="Order"/>，全局排序键为 (<see cref="Edge"/>, <see cref="Order"/>, <see cref="Id"/>)，
/// 由 <see cref="ItemOrderComparer"/> 保证唯一且稳定。跳边移动的 Order 语义由 <c>ItemStore.MoveAsync</c> 负责
/// （目标边 Order = Max+1，随后整边重排为连续整数）。</para>
///
/// <para><b>LRU</b>：<see cref="LastAccessTicks"/> 是 UTC ticks，0 表示从未被访问，此时以
/// <see cref="CreatedAt"/> 兜底（见 <see cref="EffectiveLastAccessTicks"/>），因此新建的卡片不会因为「没人碰过」
/// 而被优先淘汰。</para>
/// </summary>
public sealed class Item
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Kind Kind { get; set; }

    /// <summary>Kind=Text 的内联内容；其它 Kind 保持空串（不参与持久化语义）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>blobs/ 下的相对文件名；Kind=Text 时为 null。不得写入绝对路径。</summary>
    public string? BlobName { get; set; }

    /// <summary>thumbs/ 下的相对文件名；无缩略图（纯文本）时为 null。</summary>
    public string? ThumbName { get; set; }

    /// <summary>blob 字节数（Kind=Text 时为文本 UTF-8 字节数，仅作展示/统计，不计入 blob 配额）。</summary>
    public long ByteSize { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public EdgeKind Edge { get; set; }

    /// <summary>边内单调整数，初始值由 ItemStore 分配（Max+1）。</summary>
    public int Order { get; set; }

    /// <summary>最近一次真正使用（拖出/复制/选中）的 UTC ticks；0 = 从未访问。</summary>
    public long LastAccessTicks { get; set; }

    /// <summary>LRU 判据：从未访问时按创建时间算，避免新卡片被当成最冷数据。</summary>
    public long EffectiveLastAccessTicks
        => LastAccessTicks != 0 ? LastAccessTicks : CreatedAt.UtcTicks;

    /// <summary>是否占用 blob 配额。</summary>
    public bool HasBlob => !string.IsNullOrEmpty(BlobName);

    public void Touch(DateTimeOffset now)
        => LastAccessTicks = now.UtcTicks;

    public Item Clone() => (Item)MemberwiseClone();

    public static Item CreateText(string text, EdgeKind edge)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("文本内容不能为空。", nameof(text));
        }

        var item = new Item
        {
            Kind = Kind.Text,
            Text = text,
            ByteSize = Encoding.UTF8.GetByteCount(text),
            Edge = edge,
        };

        return item;
    }

    public static Item CreateImage(string blobName, string? thumbName, long byteSize, EdgeKind edge)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new ArgumentException("Image 必须带 BlobName。", nameof(blobName));
        }

        return new Item
        {
            Kind = Kind.Image,
            Text = string.Empty,
            BlobName = blobName,
            ThumbName = thumbName,
            ByteSize = Math.Max(0, byteSize),
            Edge = edge,
        };
    }

    public static Item CreateFile(string blobName, string? thumbName, long byteSize, EdgeKind edge)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new ArgumentException("File 必须带 BlobName。", nameof(blobName));
        }

        return new Item
        {
            Kind = Kind.File,
            Text = string.Empty,
            BlobName = blobName,
            ThumbName = thumbName,
            ByteSize = Math.Max(0, byteSize),
            Edge = edge,
        };
    }

    /// <summary>
    /// 校验 Kind 映射不变量。用于读盘（坏数据直接丢弃而不是半残地进入 UI）与写入口。
    /// </summary>
    public bool Validate(out string error)
    {
        if (Id == Guid.Empty)
        {
            error = "Id 为空。";
            return false;
        }

        if (Order < 0)
        {
            error = "Order 不能为负。";
            return false;
        }

        if (!Enum.IsDefined(typeof(Kind), Kind))
        {
            error = "Kind 取值非法。";
            return false;
        }

        if (!Enum.IsDefined(typeof(EdgeKind), Edge))
        {
            error = "Edge 取值非法。";
            return false;
        }

        if (AppPaths.Resolve("blobs", BlobName) is null && !string.IsNullOrEmpty(BlobName))
        {
            error = "BlobName 必须是裸文件名（不得含路径分隔符或绝对路径）。";
            return false;
        }

        if (AppPaths.Resolve("thumbs", ThumbName) is null && !string.IsNullOrEmpty(ThumbName))
        {
            error = "ThumbName 必须是裸文件名（不得含路径分隔符或绝对路径）。";
            return false;
        }

        switch (Kind)
        {
            case Kind.Text:
                if (string.IsNullOrEmpty(Text))
                {
                    error = "Text 类型的 Text 不能为空。";
                    return false;
                }

                if (!string.IsNullOrEmpty(BlobName))
                {
                    error = "Text 类型不落 blob，BlobName 必须为空。";
                    return false;
                }

                break;

            case Kind.Image:
            case Kind.File:
                if (string.IsNullOrEmpty(BlobName))
                {
                    error = "Image/File 类型必须有 BlobName。";
                    return false;
                }

                break;

            default:
                error = "Kind 取值非法。";
                return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>全局排序：边 → 边内 Order → Id（Guid 打破 Order 相同的并列，保证 (Edge, Order, Id) 唯一）。</summary>
public sealed class ItemOrderComparer : IComparer<Item>
{
    public static readonly ItemOrderComparer Instance = new();

    public int Compare(Item? x, Item? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byEdge = Comparer<EdgeKind>.Default.Compare(x.Edge, y.Edge);
        if (byEdge != 0)
        {
            return byEdge;
        }

        var byOrder = x.Order.CompareTo(y.Order);
        if (byOrder != 0)
        {
            return byOrder;
        }

        return x.Id.CompareTo(y.Id);
    }
}
