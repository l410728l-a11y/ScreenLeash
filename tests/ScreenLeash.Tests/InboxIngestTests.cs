using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ScreenLeash.Core;
using Xunit;

namespace ScreenLeash.Tests;

/// <summary>
/// 收件箱摄入的契约：线格式解析、边界（安静期、.tmp、坏 JSON）、以及真正落卡的效果。
/// 这里不碰计时器与托盘——只锁「外部写一个 JSON 进来，卡片会不会按预期出现在 Store 里」。
/// </summary>
public sealed class InboxIngestTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly ItemStore _store;

    public InboxIngestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "screenleash-inbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        // thumbnails 传 null：纯文本/文件卡不需要缩略图；图片路径另有测试覆盖。
        _store = new ItemStore(_paths, thumbnails: null);
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言结果。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>写一个收件文件；settle=true 时把最后写入时间往前推，跳过安静期判定。</summary>
    private string WriteInbox(string name, string json, bool settle = true)
    {
        string file = Path.Combine(_paths.InboxDirectory, name);
        File.WriteAllText(file, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (settle)
        {
            File.SetLastWriteTimeUtc(
                file,
                DateTime.UtcNow - InboxIngest.SettleTime - TimeSpan.FromSeconds(1));
        }

        return file;
    }

    // ---------- 线格式解析 ----------

    [Fact]
    public void ParseDocument_ReadsTextAndFileItems_AndFallsBackOnMissingEdge()
    {
        const string json = """
        {
          "source": "tianshu",
          "items": [
            { "kind": "text", "text": "你好", "edge": "bottom" },
            { "kind": "file", "path": "D:\\a\\b.docx" }
          ]
        }
        """;

        var (items, error) = InboxIngest.ParseDocument(json, EdgeKind.Right);

        Assert.Null(error);
        Assert.Equal(2, items.Count);
        Assert.Equal("text", items[0].Kind);
        Assert.Equal("你好", items[0].Text);
        Assert.Equal(EdgeKind.Bottom, items[0].Edge);

        Assert.Equal("file", items[1].Kind);
        Assert.Equal("D:\\a\\b.docx", items[1].FilePath);
        Assert.Equal(EdgeKind.Right, items[1].Edge); // 缺 edge → 回退
    }

    [Fact]
    public void ParseDocument_IsCaseInsensitiveOnFieldNamesAndEdgeNames()
    {
        const string json = """{ "Items": [ { "Kind": "TEXT", "Text": "x", "Edge": "LEFT" } ] }""";

        var (items, error) = InboxIngest.ParseDocument(json, EdgeKind.Right);

        Assert.Null(error);
        Assert.Single(items);
        Assert.Equal("text", items[0].Kind);
        Assert.Equal(EdgeKind.Left, items[0].Edge);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDocument_EmptyBody_ReportsError(string json)
    {
        var (items, error) = InboxIngest.ParseDocument(json, EdgeKind.Right);
        Assert.Empty(items);
        Assert.Equal("空文件", error);
    }

    [Fact]
    public void ParseDocument_MalformedJson_ReportsErrorInsteadOfThrowing()
    {
        var (items, error) = InboxIngest.ParseDocument("{ this is not json", EdgeKind.Right);
        Assert.Empty(items);
        Assert.NotNull(error);
        Assert.StartsWith("JSON 不合法", error);
    }

    [Fact]
    public void ParseDocument_MissingItemsArray_ReportsError()
    {
        var (items, error) = InboxIngest.ParseDocument("""{ "source": "tianshu" }""", EdgeKind.Right);
        Assert.Empty(items);
        Assert.Equal("缺少 items 数组", error);
    }

    [Fact]
    public void ParseDocument_TruncatesAtMaxItemsPerFile()
    {
        string entries = string.Join(",", Enumerable.Range(0, InboxIngest.MaxItemsPerFile + 5)
            .Select(i => $$"""{ "kind": "text", "text": "item-{{i}}" }"""));

        var (items, error) = InboxIngest.ParseDocument($"{{ \"items\": [{entries}] }}", EdgeKind.Right);

        Assert.Null(error);
        Assert.Equal(InboxIngest.MaxItemsPerFile, items.Count);
    }

    [Theory]
    [InlineData("left", EdgeKind.Left)]
    [InlineData("TOP", EdgeKind.Top)]
    [InlineData("Right", EdgeKind.Right)]
    [InlineData(" bottom ", EdgeKind.Bottom)]
    public void ParseEdge_AcceptsKnownNames(string raw, EdgeKind expected)
    {
        Assert.Equal(expected, InboxIngest.ParseEdge(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("middle")]
    public void ParseEdge_UnknownOrEmpty_ReturnsNull(string? raw)
    {
        Assert.Null(InboxIngest.ParseEdge(raw));
    }

    // ---------- 真正落卡 ----------

    [Fact]
    public async Task PumpAsync_TextItem_AddsCardAndArchivesFile()
    {
        string file = WriteInbox(
            "a.json",
            """{ "items": [ { "kind": "text", "text": "来自天枢", "edge": "left" } ] }""");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.FilesSeen);
        Assert.Equal(1, report.FilesProcessed);
        Assert.Equal(0, report.FilesRejected);
        Assert.Equal(1, report.Added);
        Assert.True(report.Changed);

        Assert.False(File.Exists(file), "处理完必须移出 inbox，否则会重复落卡");
        Assert.True(File.Exists(Path.Combine(_paths.InboxDoneDirectory, "a.json")));

        var items = _store.Snapshot();
        Assert.Single(items);
        Assert.Equal(Kind.Text, items[0].Kind);
        Assert.Equal("来自天枢", items[0].Text);
        Assert.Equal(EdgeKind.Left, items[0].Edge);
    }

    [Fact]
    public async Task PumpAsync_FileItem_CopiesIntoBlobsAndKeepsDisplayName()
    {
        string source = Path.Combine(_root, "报告.txt");
        File.WriteAllText(source, "hello", new UTF8Encoding(false));

        WriteInbox(
            "b.json",
            $$"""{ "items": [ { "kind": "file", "path": {{System.Text.Json.JsonSerializer.Serialize(source)}}, "edge": "top" } ] }""");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.Added);
        var item = Assert.Single(_store.Snapshot());
        Assert.Equal(Kind.File, item.Kind);
        Assert.Equal("报告.txt", item.DisplayName);
        Assert.Equal(EdgeKind.Top, item.Edge);
        Assert.True(item.HasBlob);
    }

    [Fact]
    public async Task PumpAsync_MissingSourceFile_SkipsWithoutAdding()
    {
        WriteInbox(
            "c.json",
            """{ "items": [ { "kind": "file", "path": "D:\\definitely\\missing.pdf" } ] }""");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(0, report.Added);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(_store.Snapshot());
        // 整批处理完毕（不是解析失败），文件仍归档到 done，避免每轮重试
        Assert.True(File.Exists(Path.Combine(_paths.InboxDoneDirectory, "c.json")));
    }

    [Fact]
    public async Task PumpAsync_MalformedFile_MovesToBadWithoutAdding()
    {
        WriteInbox("bad.json", "{ oops");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.FilesRejected);
        Assert.Equal(0, report.Added);
        Assert.Empty(_store.Snapshot());
        Assert.True(File.Exists(Path.Combine(_paths.InboxBadDirectory, "bad.json")));
        Assert.False(File.Exists(Path.Combine(_paths.InboxDirectory, "bad.json")));
    }

    [Fact]
    public async Task PumpAsync_IgnoresTmpFiles()
    {
        string tmp = Path.Combine(_paths.InboxDirectory, "d.json.tmp");
        File.WriteAllText(tmp, """{ "items": [ { "kind": "text", "text": "半个" } ] }""", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow - TimeSpan.FromMinutes(1));

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(InboxIngest.InboxPumpReport.None, report);
        Assert.True(File.Exists(tmp), ".tmp 是写入方的临时态，绝不能动");
        Assert.Empty(_store.Snapshot());
    }

    [Fact]
    public async Task PumpAsync_RespectsSettleTime()
    {
        // 不 settle：刚写完的文件这一轮不应被处理
        string file = WriteInbox(
            "fresh.json",
            """{ "items": [ { "kind": "text", "text": "刚写的" } ] }""",
            settle: false);

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.FilesSeen);
        Assert.Equal(0, report.FilesProcessed);
        Assert.Empty(_store.Snapshot());
        Assert.True(File.Exists(file), "安静期内的文件必须留在原地等下一轮");
    }

    [Fact]
    public async Task PumpAsync_EmptyItemsArray_MovesToBad()
    {
        WriteInbox("empty.json", """{ "items": [] }""");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.FilesRejected);
        Assert.True(File.Exists(Path.Combine(_paths.InboxBadDirectory, "empty.json")));
    }

    [Fact]
    public async Task PumpAsync_NoInboxDirectory_ReturnsNothingInsteadOfThrowing()
    {
        var otherRoot = Path.Combine(_root, "no-inbox-here");
        var otherPaths = new AppPaths(otherRoot);
        Directory.CreateDirectory(otherRoot);

        using var store = new ItemStore(otherPaths, thumbnails: null);
        var report = await InboxIngest.PumpAsync(otherPaths, store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(InboxIngest.InboxPumpReport.None, report);
    }

    [Fact]
    public async Task PumpAsync_ProcessesAtMostMaxFilesPerPump()
    {
        for (var i = 0; i < InboxIngest.MaxFilesPerPump + 3; i++)
        {
            WriteInbox(
                $"bulk-{i:D2}.json",
                $$"""{ "items": [ { "kind": "text", "text": "批量-{{i}}" } ] }""");
        }

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(InboxIngest.MaxFilesPerPump + 3, report.FilesSeen);
        Assert.Equal(InboxIngest.MaxFilesPerPump, report.FilesProcessed);
        Assert.Equal(InboxIngest.MaxFilesPerPump, _store.Count);
    }

    // ---------- action 协议（remove / move / paste） ----------

    [Fact]
    public void ParseCommand_MissingActionDefaultsToAdd()
    {
        var command = InboxIngest.ParseCommand("""{ "items": [ { "kind": "text", "text": "老格式" } ] }""", EdgeKind.Right);

        Assert.Equal("add", command.Action);
        Assert.Null(command.Error);
        Assert.Single(command.Items);
    }

    [Theory]
    [InlineData("""{ "action": "remove" }""", "remove 需要 id")]
    [InlineData("""{ "action": "move", "id": "0c31a690-90a5-454d-bf5c-dc10c83aa9a8" }""", "move 需要合法的 edge（left/top/right/bottom）")]
    [InlineData("""{ "action": "explode", "id": "x" }""", "未知 action：explode")]
    public void ParseCommand_RejectsMalformedCommands(string json, string expectedError)
    {
        var command = InboxIngest.ParseCommand(json, EdgeKind.Right);
        Assert.Equal(expectedError, command.Error);
    }

    [Fact]
    public void ParseCardId_AcceptsOnlyFullGuid()
    {
        var ok = InboxIngest.ParseCardId("0c31a690-90a5-454d-bf5c-dc10c83aa9a8");
        Assert.NotNull(ok.Id);
        Assert.Null(ok.Error);

        var prefix = InboxIngest.ParseCardId("0c31a690");
        Assert.Null(prefix.Id);
        Assert.StartsWith("id 不是合法 Guid", prefix.Error);
    }

    [Fact]
    public async Task PumpAsync_RemoveAction_DeletesCardAndArchivesFile()
    {
        var item = await _store.AddTextAsync("待删除", EdgeKind.Right);
        Assert.Equal(1, _store.Count);

        WriteInbox("rm.json", "{ \"action\": \"remove\", \"id\": \"" + item.Id + "\" }");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.Removed);
        Assert.Equal(0, report.FilesRejected);
        Assert.Equal(1, report.Affected);
        Assert.Equal(0, _store.Count);
        Assert.True(File.Exists(Path.Combine(_paths.InboxDoneDirectory, "rm.json")));
    }

    [Fact]
    public async Task PumpAsync_MoveAction_ChangesEdge()
    {
        var item = await _store.AddTextAsync("换边", EdgeKind.Right);

        WriteInbox("mv.json", "{ \"action\": \"move\", \"id\": \"" + item.Id + "\", \"edge\": \"bottom\" }");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.Moved);
        var only = Assert.Single(_store.Snapshot());
        Assert.Equal(EdgeKind.Bottom, only.Edge);
    }

    [Fact]
    public async Task PumpAsync_MoveWithoutEdge_RejectsFileWithoutMoving()
    {
        var item = await _store.AddTextAsync("别动", EdgeKind.Right);

        WriteInbox("mv-bad.json", "{ \"action\": \"move\", \"id\": \"" + item.Id + "\" }");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.FilesRejected);
        Assert.Equal(0, report.Moved);
        Assert.Equal(EdgeKind.Right, _store.Snapshot()[0].Edge);
        Assert.True(File.Exists(Path.Combine(_paths.InboxBadDirectory, "mv-bad.json")));
    }

    [Fact]
    public async Task PumpAsync_RemoveWithBadId_RejectsFile()
    {
        WriteInbox("rm-bad.json", """{ "action": "remove", "id": "not-a-guid" }""");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(1, report.FilesRejected);
        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(Path.Combine(_paths.InboxBadDirectory, "rm-bad.json")));
    }

    [Fact]
    public async Task PumpAsync_RemoveMissingCard_CountsSkippedButArchives()
    {
        WriteInbox("rm-missing.json", """{ "action": "remove", "id": "00000000-0000-0000-0000-000000000001" }""");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(0, report.Removed);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.FilesRejected);
        Assert.True(File.Exists(Path.Combine(_paths.InboxDoneDirectory, "rm-missing.json")));
    }

    [Fact]
    public async Task PumpAsync_PasteAction_UsesInjectedHandlerWithRequestedEdge()
    {
        EdgeKind? seen = null;
        WriteInbox("paste.json", """{ "action": "paste", "edge": "top" }""");

        var report = await InboxIngest.PumpAsync(
            _paths,
            _store,
            new AppSettings(),
            EdgeKind.Right,
            edge =>
            {
                seen = edge;
                return Task.FromResult(1);
            });

        Assert.Equal(1, report.Pasted);
        Assert.Equal(EdgeKind.Top, seen);
    }

    [Fact]
    public async Task PumpAsync_PasteWithoutHandler_CountsSkipped()
    {
        WriteInbox("paste2.json", """{ "action": "paste" }""");

        var report = await InboxIngest.PumpAsync(_paths, _store, new AppSettings(), EdgeKind.Right);

        Assert.Equal(0, report.Pasted);
        Assert.Equal(1, report.Skipped);
    }

    [Fact]
    public async Task PumpAsync_PasteWithEmptyClipboard_CountsSkipped()
    {
        WriteInbox("paste3.json", """{ "action": "paste" }""");

        var report = await InboxIngest.PumpAsync(
            _paths,
            _store,
            new AppSettings(),
            EdgeKind.Right,
            _ => Task.FromResult(0));

        Assert.Equal(0, report.Pasted);
        Assert.Equal(1, report.Skipped);
    }
}
