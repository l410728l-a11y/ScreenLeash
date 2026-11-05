using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScreenLeash.Core;
using ScreenLeash.UI;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 拖动排序：数据层插入位置 + 布局层插入位置计算。
/// 这两条是"上下调序"与"跨边移动"的底座，先钉死行为再接 UI。
/// </summary>
public class ReorderTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public ReorderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "leash-reorder-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task MoveTo_同边_可以插到最前()
    {
        using var store = new ItemStore(_paths);
        await store.AddTextAsync("A", EdgeKind.Right);
        await store.AddTextAsync("B", EdgeKind.Right);
        var c = await store.AddTextAsync("C", EdgeKind.Right);

        Assert.Equal(new[] { "A", "B", "C" }, store.ForEdge(EdgeKind.Right).Select(i => i.Text));

        await store.MoveToAsync(c.Id, EdgeKind.Right, 0);

        Assert.Equal(new[] { "C", "A", "B" }, store.ForEdge(EdgeKind.Right).Select(i => i.Text));
    }

    [Fact]
    public async Task MoveTo_同边_可以插到中间()
    {
        using var store = new ItemStore(_paths);
        var a = await store.AddTextAsync("A", EdgeKind.Right);
        await store.AddTextAsync("B", EdgeKind.Right);
        await store.AddTextAsync("C", EdgeKind.Right);

        await store.MoveToAsync(a.Id, EdgeKind.Right, 1);

        Assert.Equal(new[] { "B", "A", "C" }, store.ForEdge(EdgeKind.Right).Select(i => i.Text));
    }

    [Fact]
    public async Task MoveTo_跨边_追加到对侧末尾()
    {
        using var store = new ItemStore(_paths);
        var a = await store.AddTextAsync("A", EdgeKind.Right);
        await store.AddTextAsync("B", EdgeKind.Left);

        await store.MoveToAsync(a.Id, EdgeKind.Left, int.MaxValue);

        Assert.Empty(store.ForEdge(EdgeKind.Right));
        Assert.Equal(new[] { "B", "A" }, store.ForEdge(EdgeKind.Left).Select(i => i.Text));
    }

    [Fact]
    public async Task MoveTo_索引越界_夹到末尾()
    {
        using var store = new ItemStore(_paths);
        var a = await store.AddTextAsync("A", EdgeKind.Right);
        await store.AddTextAsync("B", EdgeKind.Right);

        await store.MoveToAsync(a.Id, EdgeKind.Right, 999);

        Assert.Equal(new[] { "B", "A" }, store.ForEdge(EdgeKind.Right).Select(i => i.Text));
    }

    [Fact]
    public async Task MoveTo_不存在的Id_返回false()
    {
        using var store = new ItemStore(_paths);
        await store.AddTextAsync("A", EdgeKind.Right);

        Assert.False(await store.MoveToAsync(Guid.NewGuid(), EdgeKind.Right, 0));
    }

    [Fact]
    public void ComputeInsertIndex_横向_按卡片中心判断()
    {
        var slots = new[]
        {
            new CardSlot(0, new Rectangle(0, 0, 100, 50)),
            new CardSlot(1, new Rectangle(110, 0, 100, 50)),
        };

        // 第一张中心在 x=50
        Assert.Equal(0, EdgeLayout.ComputeInsertIndex(slots, new Point(10, 25), horizontal: true));
        Assert.Equal(1, EdgeLayout.ComputeInsertIndex(slots, new Point(60, 25), horizontal: true));
        Assert.Equal(2, EdgeLayout.ComputeInsertIndex(slots, new Point(300, 25), horizontal: true));
    }

    [Fact]
    public void ComputeInsertIndex_纵向_按卡片中心判断()
    {
        var slots = new[]
        {
            new CardSlot(0, new Rectangle(0, 0, 50, 100)),
            new CardSlot(1, new Rectangle(0, 110, 50, 100)),
        };

        Assert.Equal(0, EdgeLayout.ComputeInsertIndex(slots, new Point(25, 10), horizontal: false));
        Assert.Equal(1, EdgeLayout.ComputeInsertIndex(slots, new Point(25, 60), horizontal: false));
        Assert.Equal(2, EdgeLayout.ComputeInsertIndex(slots, new Point(25, 300), horizontal: false));
    }

    [Fact]
    public void ComputeInsertIndex_空布局_返回0()
    {
        Assert.Equal(0, EdgeLayout.ComputeInsertIndex(Array.Empty<CardSlot>(), new Point(0, 0), true));
    }
}
