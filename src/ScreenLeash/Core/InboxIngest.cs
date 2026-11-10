using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ScreenLeash.Interop;

namespace ScreenLeash.Core;

/// <summary>
/// 收件箱摄入：扫描 <c>%LOCALAPPDATA%\ScreenLeash\inbox\*.json</c>，把里面的条目交给
/// <see cref="DragIn.IngestParsedAsync"/> 的既有管线落卡。
///
/// <para><b>为什么要有这条通道</b>：<see cref="ItemStore"/> 是卡片集合的唯一拥有者（内存持有列表、
/// 写盘走 temp → <c>File.Replace</c>）。外部程序直接改 <c>items.json</c> 会被下一次 SaveCore 整个覆盖，
/// 用户看到的只是「挂上去的东西自己消失」。所以外部写入必须经本程序的内存路径，落盘由 ItemStore 自己完成。</para>
///
/// <para><b>线格式（写入方照这个写）</b>：</para>
/// <code>
/// {
///   "source": "tianshu",
///   "items": [
///     { "kind": "text", "text": "要挂的文字", "edge": "right" },
///     { "kind": "file", "path": "D:\\a\\b.docx", "edge": "bottom" }
///   ]
/// }
/// </code>
///
/// <para><b>写文件的两个硬要求</b>：</para>
/// <list type="number">
/// <item>先写 <c>xxx.json.tmp</c>，再改名为 <c>xxx.json</c>——本类只扫 <c>.json</c>，<c>.tmp</c> 一律不碰。</item>
/// <item>文件名任意，但不要复用（处理完会被移进 <c>done\</c>；同名冲突时归档会加时间戳）。</item>
/// </list>
///
/// <para><b>结算语义</b>：整批成功后文件移入 <c>done\</c>；解析失败或空条目移入 <c>bad\</c> 并记 <c>logs\inbox.log</c>。
/// 移动失败只记日志——那意味着下一轮可能重复落卡，这比静默丢数据可接受。</para>
///
/// <para><b>安静期</b>：只处理「最后写入距今超过 <see cref="SettleTime"/>」的文件。写入方不守规矩直接写
/// <c>.json</c> 时，这条兜底能避免读到半个文件。</para>
/// </summary>
public static class InboxIngest
{
    /// <summary>一轮最多处理几个文件：防止一次塞几千个把 UI 线程拖住。</summary>
    public const int MaxFilesPerPump = 8;

    /// <summary>单个文件最多摄入几条：多余的忽略（不报错，只计入 skipped）。</summary>
    public const int MaxItemsPerFile = 20;

    /// <summary>正式收件后缀（本类只认它）。</summary>
    public const string WireSuffix = ".json";

    /// <summary>建议写入方使用的临时后缀（本类永不扫描）。</summary>
    public const string TempSuffix = ".json.tmp";

    /// <summary>文件最后写入后至少安静这么久才处理。</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>一条待摄入条目（已解析，未落卡）。</summary>
    public sealed record InboxItemSpec(string Kind, string? Text, string? FilePath, EdgeKind Edge);

    /// <summary>一轮扫描的结算结果。</summary>
    public readonly record struct InboxPumpReport(
        int FilesSeen,
        int FilesProcessed,
        int FilesRejected,
        int Added,
        int Removed,
        int Moved,
        int Pasted,
        int Skipped,
        string? LastError)
    {
        public static readonly InboxPumpReport None = new(0, 0, 0, 0, 0, 0, 0, 0, null);

        /// <summary>本轮是否真的动了数据（托盘据此刷新提示）。</summary>
        public bool Changed => Added > 0 || Removed > 0 || Moved > 0 || Pasted > 0 || FilesRejected > 0;

        /// <summary>本轮实际生效的动作总数（不含拒收与跳过）。</summary>
        public int Affected => Added + Removed + Moved + Pasted;
    }

    /// <summary>收件文档解析结果：一个文件 = 一条命令。</summary>
    /// <param name="Action">add / remove / move / paste（缺省 add）。</param>
    /// <param name="Items">仅 add 用。</param>
    /// <param name="Id">仅 remove / move 用，卡片 Id（完整 Guid 或 ≥4 位前缀由挂件侧不支持，必须是完整 Guid）。</param>
    /// <param name="Edge">目标边（已回退到 fallbackEdge）。</param>
    public sealed record InboxCommand(
        string Action,
        IReadOnlyList<InboxItemSpec> Items,
        string? Id,
        EdgeKind Edge,
        string? Error);

    /// <summary>把 <c>edge</c> 字段（大小写不敏感）解析成枚举；不认识返回 null（由调用方回退）。</summary>
    public static EdgeKind? ParseEdge(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "left" => EdgeKind.Left,
            "top" => EdgeKind.Top,
            "right" => EdgeKind.Right,
            "bottom" => EdgeKind.Bottom,
            _ => null,
        };
    }

    /// <summary>
    /// 解析卡片 Id。只接受完整 Guid —— 前缀匹配是插件侧的便利功能，落盘这一端不做模糊匹配，
    /// 否则一个手抖的前缀就可能删错卡片。
    /// </summary>
    public static (Guid? Id, string? Error) ParseCardId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, "缺少 id");
        }

        return Guid.TryParse(value, out var id)
            ? (id, null)
            : (null, "id 不是合法 Guid：" + value);
    }

    /// <summary>
    /// 解析收件 JSON。成功返回条目列表与 <c>null</c> 错误；失败返回空列表与原因。
    /// 缺 <c>kind</c> 或 <c>kind</c> 不认识的条目会被原样保留（由摄入阶段计入 skipped），
    /// 这样调用方能在日志里看到「文件里有什么被丢掉了」。
    /// </summary>
    public static (IReadOnlyList<InboxItemSpec> Items, string? Error) ParseDocument(string? json, EdgeKind fallbackEdge)
    {
        var command = ParseCommand(json, fallbackEdge);
        return (command.Items, command.Error);
    }

    /// <summary>
    /// 解析收件文档成一条命令。四种 action：
    /// <c>add</c>（缺省，需 items）、<c>remove</c>（需 id）、<c>move</c>（需 id + 显式 edge）、
    /// <c>paste</c>（把系统剪贴板当前内容挂到 edge，由调用方注入的处理器执行）。
    /// </summary>
    public static InboxCommand ParseCommand(string? json, EdgeKind fallbackEdge)
    {
        var empty = Array.Empty<InboxItemSpec>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return new InboxCommand("add", empty, null, fallbackEdge, "空文件");
        }

        WireDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<WireDocument>(json, WireOptions);
        }
        catch (JsonException ex)
        {
            return new InboxCommand("add", empty, null, fallbackEdge, "JSON 不合法：" + ex.Message);
        }

        if (document is null)
        {
            return new InboxCommand("add", empty, null, fallbackEdge, "文档为空");
        }

        var action = string.IsNullOrWhiteSpace(document.Action)
            ? "add"
            : document.Action.Trim().ToLowerInvariant();
        var edge = ParseEdge(document.Edge) ?? fallbackEdge;
        var id = string.IsNullOrWhiteSpace(document.Id) ? null : document.Id.Trim();

        switch (action)
        {
            case "add":
                if (document.Items is null)
                {
                    return new InboxCommand(action, empty, null, edge, "缺少 items 数组");
                }

                var items = new List<InboxItemSpec>();
                foreach (var raw in document.Items.Take(MaxItemsPerFile))
                {
                    if (raw is null)
                    {
                        continue;
                    }

                    var kind = (raw.Kind ?? string.Empty).Trim().ToLowerInvariant();
                    var itemEdge = ParseEdge(raw.Edge) ?? edge;
                    items.Add(new InboxItemSpec(kind, raw.Text, raw.FilePath, itemEdge));
                }

                return new InboxCommand(action, items, null, edge, null);

            case "remove":
                return id is null
                    ? new InboxCommand(action, empty, null, edge, "remove 需要 id")
                    : new InboxCommand(action, empty, id, edge, null);

            case "move":
                if (id is null)
                {
                    return new InboxCommand(action, empty, null, edge, "move 需要 id");
                }

                // 目标边必须显式给：拿 fallback 猜「移到哪」等于悄悄挪错地方。
                if (ParseEdge(document.Edge) is null)
                {
                    return new InboxCommand(action, empty, id, edge, "move 需要合法的 edge（left/top/right/bottom）");
                }

                return new InboxCommand(action, empty, id, edge, null);

            case "paste":
                return new InboxCommand(action, empty, null, edge, null);

            default:
                return new InboxCommand(action, empty, id, edge, "未知 action：" + action);
        }
    }

    /// <summary>
    /// 扫一轮收件箱。必须在 UI 线程上调用（WinForms Timer 的 Tick 即满足）：
    /// <see cref="ItemStore"/> 的写操作要回投到创建它的同步上下文。
    /// </summary>
    public static async Task<InboxPumpReport> PumpAsync(
        AppPaths paths,
        ItemStore store,
        AppSettings settings,
        EdgeKind fallbackEdge,
        Func<EdgeKind, Task<int>>? pasteHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (paths is null)
        {
            throw new ArgumentNullException(nameof(paths));
        }

        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        settings ??= new AppSettings();

        string[] files;
        try
        {
            if (!Directory.Exists(paths.InboxDirectory))
            {
                return InboxPumpReport.None;
            }

            files = Directory.GetFiles(paths.InboxDirectory, "*" + WireSuffix, SearchOption.TopDirectoryOnly);

            // .json.tmp 也匹配 "*" + ".json"？不会：GetFiles 的模式按整名匹配，且下面再挡一次。
            files = files
                .Where(f => f.EndsWith(WireSuffix, StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Log(paths, "扫描收件箱失败：" + ex.Message);
            return InboxPumpReport.None;
        }

        if (files.Length == 0)
        {
            return InboxPumpReport.None;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var seen = files.Length;
        var processed = 0;
        var rejected = 0;
        var added = 0;
        var removed = 0;
        var moved = 0;
        var pasted = 0;
        var skipped = 0;
        string? lastError = null;

        foreach (var file in files.Take(MaxFilesPerPump))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSettled(file))
            {
                continue;
            }

            string json;
            try
            {
                json = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log(paths, "读取失败：" + Path.GetFileName(file) + " / " + ex.Message);
                continue;
            }

            var command = ParseCommand(json, fallbackEdge);
            if (command.Error is not null)
            {
                Reject(paths, file, command.Error);
                processed++;
                rejected++;
                lastError = command.Error;
                continue;
            }

            switch (command.Action)
            {
                case "add":
                {
                    if (command.Items.Count == 0)
                    {
                        Reject(paths, file, "没有可摄入条目");
                        processed++;
                        rejected++;
                        lastError = "没有可摄入条目";
                        continue;
                    }

                    foreach (var spec in command.Items)
                    {
                        var content = ToContent(spec, paths);
                        if (!content.HasContent)
                        {
                            skipped++;
                            lastError = $"条目无法摄入（kind={spec.Kind}）";
                            continue;
                        }

                        var result = await DragIn
                            .IngestParsedAsync(content, store, settings, spec.Edge, cancellationToken)
                            .ConfigureAwait(true);

                        added += result.Added;
                        skipped += result.Skipped;

                        if (result.LastError is not null)
                        {
                            lastError = result.LastError;
                        }

                        if (result.Added == 0 && result.Skipped == 0)
                        {
                            // 既没加也没跳过：内容被判为空，计入 skipped 免得 silently 丢掉。
                            skipped++;
                        }
                    }

                    break;
                }

                case "remove":
                case "move":
                {
                    var (cardId, idError) = ParseCardId(command.Id);
                    if (cardId is null)
                    {
                        Reject(paths, file, idError ?? "id 不合法");
                        processed++;
                        rejected++;
                        lastError = idError;
                        continue;
                    }

                    var affected = command.Action == "remove"
                        ? await store.RemoveAsync(cardId.Value).ConfigureAwait(true)
                        : await store.MoveAsync(cardId.Value, command.Edge).ConfigureAwait(true);

                    if (!affected)
                    {
                        // 合法 Id 但卡片不在：重试也不会命中，按「已处理但没生效」结算。
                        skipped++;
                        lastError = "卡片不存在：" + command.Id;
                    }
                    else if (command.Action == "remove")
                    {
                        removed++;
                    }
                    else
                    {
                        moved++;
                    }

                    break;
                }

                case "paste":
                {
                    if (pasteHandler is null)
                    {
                        skipped++;
                        lastError = "当前上下文不支持剪贴板粘贴";
                        break;
                    }

                    var count = await pasteHandler(command.Edge).ConfigureAwait(true);
                    if (count > 0)
                    {
                        pasted += count;
                    }
                    else
                    {
                        skipped++;
                        lastError = "剪贴板没有可挂的内容";
                    }

                    break;
                }
            }

            Archive(paths, file, paths.InboxDoneDirectory);
            processed++;
        }

        var report = new InboxPumpReport(seen, processed, rejected, added, removed, moved, pasted, skipped, lastError);
        if (report.Changed)
        {
            Log(
                paths,
                $"收件箱结算：文件 {report.FilesProcessed}/{report.FilesSeen}，落卡 {report.Added}，"
                    + $"删除 {report.Removed}，移动 {report.Moved}，粘贴 {report.Pasted}，"
                    + $"跳过 {report.Skipped}，拒收 {report.FilesRejected}"
                    + (report.LastError is null ? string.Empty : "，末次错误：" + report.LastError));
        }

        return report;
    }

    private static bool IsSettled(string file)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= SettleTime;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>条目 → 既有摄入管线的输入。文件条目要求路径真实存在（否则记 skipped）。</summary>
    private static ParsedContent ToContent(InboxItemSpec spec, AppPaths paths)
    {
        switch (spec.Kind)
        {
            case "text":
                return string.IsNullOrWhiteSpace(spec.Text)
                    ? ParsedContent.Empty
                    : ParsedContent.FromText(spec.Text);

            case "file":
                if (string.IsNullOrWhiteSpace(spec.FilePath))
                {
                    return ParsedContent.Empty;
                }

                try
                {
                    return File.Exists(spec.FilePath)
                        ? ParsedContent.FromFiles(new[] { spec.FilePath })
                        : ParsedContent.Empty;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    Log(paths, "文件条目不可用：" + spec.FilePath + " / " + ex.Message);
                    return ParsedContent.Empty;
                }

            default:
                return ParsedContent.Empty;
        }
    }

    private static void Reject(AppPaths paths, string file, string reason)
    {
        Log(paths, "拒收 " + Path.GetFileName(file) + "：" + reason);
        Archive(paths, file, paths.InboxBadDirectory);
    }

    private static void Archive(AppPaths paths, string file, string directory)
    {
        try
        {
            paths.EnsureCreated();
            Directory.CreateDirectory(directory);

            var name = Path.GetFileName(file);
            var target = Path.Combine(directory, name);
            if (File.Exists(target))
            {
                target = Path.Combine(
                    directory,
                    Path.GetFileNameWithoutExtension(name)
                        + "-"
                        + DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture)
                        + WireSuffix);
            }

            File.Move(file, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 归档失败意味着下一轮可能重复落卡：记下来，不静默吞掉。
            Log(paths, "归档失败（可能重复落卡）：" + Path.GetFileName(file) + " / " + ex.Message);
        }
    }

    private static void Log(AppPaths paths, string message)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            File.AppendAllText(
                Path.Combine(paths.LogDirectory, "inbox.log"),
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 日志失败不影响摄入路径。
        }
    }

    private sealed class WireDocument
    {
        public string? Source { get; set; }

        /// <summary>add / remove / move / paste；缺省 add（兼容只有 items 的旧格式）。</summary>
        public string? Action { get; set; }

        /// <summary>remove / move 的目标卡片 Id（完整 Guid）。</summary>
        public string? Id { get; set; }

        /// <summary>文档级默认边（move 时必填；add 时作为条目缺省边）。</summary>
        public string? Edge { get; set; }

        public List<WireItem?>? Items { get; set; }
    }

    private sealed class WireItem
    {
        public string? Kind { get; set; }

        public string? Text { get; set; }

        /// <summary>线格式字段名是 <c>path</c>；属性名不能叫 Path（会与 System.IO.Path 撞名），所以显式映射。</summary>
        [JsonPropertyName("path")]
        public string? FilePath { get; set; }

        public string? Edge { get; set; }
    }
}
