using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 持久化：模拟「退出程序 → 重新进」（换一个 ItemStore 实例读同一个目录），
/// 验证卡片、顺序、显示名都还在。重启电脑不影响——数据在 %LOCALAPPDATA%，不是进程内存。
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public PersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "leash-persist-" + Guid.NewGuid().ToString("N"));
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
    public async Task 重新打开_卡片和所属边都还在()
    {
        Guid textId;

        using (var store = new ItemStore(_paths))
        {
            var a = await store.AddTextAsync("第一句话", EdgeKind.Right);
            await store.AddTextAsync("第二句话", EdgeKind.Left);
            textId = a.Id;
        }

        // 模拟"退出再进"
        using (var reopened = new ItemStore(_paths))
        {
            var right = reopened.ForEdge(EdgeKind.Right);
            Assert.Single(right);
            Assert.Equal("第一句话", right[0].Text);
            Assert.Equal(textId, right[0].Id);

            var left = reopened.ForEdge(EdgeKind.Left);
            Assert.Single(left);
            Assert.Equal("第二句话", left[0].Text);
        }
    }

    [Fact]
    public async Task 重新打开_调整过的顺序仍然保持()
    {
        Guid first;

        using (var store = new ItemStore(_paths))
        {
            var a = await store.AddTextAsync("A", EdgeKind.Right);
            await store.AddTextAsync("B", EdgeKind.Right);
            await store.AddTextAsync("C", EdgeKind.Right);
            first = a.Id;

            // 拖到末尾
            await store.MoveToAsync(first, EdgeKind.Right, int.MaxValue);
            Assert.Equal(new[] { "B", "C", "A" }, store.ForEdge(EdgeKind.Right).Select(i => i.Text));
        }

        using (var reopened = new ItemStore(_paths))
        {
            Assert.Equal(new[] { "B", "C", "A" }, reopened.ForEdge(EdgeKind.Right).Select(i => i.Text));
        }
    }

    [Fact]
    public async Task 重新打开_删除过的卡片不会复活()
    {
        Guid removed;

        using (var store = new ItemStore(_paths))
        {
            await store.AddTextAsync("保留", EdgeKind.Right);
            var gone = await store.AddTextAsync("删掉", EdgeKind.Right);
            removed = gone.Id;
            await store.RemoveAsync(removed);
        }

        using (var reopened = new ItemStore(_paths))
        {
            Assert.Single(reopened.ForEdge(EdgeKind.Right));
            Assert.Equal("保留", reopened.ForEdge(EdgeKind.Right)[0].Text);
            Assert.DoesNotContain(reopened.ForEdge(EdgeKind.Right), i => i.Id == removed);
        }
    }

    [Fact]
    public async Task 重新打开_显示名仍然保留()
    {
        using (var store = new ItemStore(_paths))
        {
            var item = await store.AddFileAsync(CreateTempFile("季度报告.pdf"), EdgeKind.Right);
            Assert.Equal("季度报告.pdf", item.DisplayName);
        }

        // 注意：不能直接断言 items.json 里含中文——JsonSerializer 默认把非 ASCII 转义成 \uXXXX。

        using (var reopened = new ItemStore(_paths))
        {
            var item = Assert.Single(reopened.ForEdge(EdgeKind.Right));
            Assert.Equal("季度报告.pdf", item.DisplayName);
        }
    }

    private string CreateTempFile(string name)
    {
        // 放在子目录里，文件名保持原样——否则 Path.GetFileName 会带上 "src-" 前缀，
        // 断言"原名保留"就对不上了。
        var dir = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, name);
        File.WriteAllText(source, "x");
        return source;
    }
}
