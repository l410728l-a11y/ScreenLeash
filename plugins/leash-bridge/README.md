# leash-bridge —— 屏幕绳子桥

天枢插件。连接 [ScreenLeash](../../)（屏幕边缘挂件）：**读**挂在屏幕四边的卡片，也能**挂上、删除**，或让挂件**粘贴当前剪贴板内容**。

## 它读什么、写什么

数据根：`%LOCALAPPDATA%\ScreenLeash\`

| 文件 | 说明 | 本插件 |
|---|---|---|
| `items.json` | 卡片清单，`{ Version: 1, Items[] }` | 只读 |
| `blobs\` | 图片/文件卡的原文件副本 | 只读（只取绝对路径） |
| `thumbs\` | png 缩略图 | 只读 |
| `inbox\*.json` | **写入通道**：插件投递，挂件轮询落卡 | 只写 |
| `inbox\done\` / `inbox\bad\` | 挂件处理后的归档 | 不碰 |

枚举映射（从 ScreenLeash 源码逐字核对）：

- `Kind`：`0=Image, 1=Text, 2=File`（`src/ScreenLeash/Core/Item.cs:8-13`）
- `EdgeKind`：`0=Left, 1=Top, 2=Right, 3=Bottom`（`src/ScreenLeash/Core/EdgeGeometry.cs:7-13`）

## 工具

| 工具 | 作用 | 审批 |
|---|---|---|
| `leash_info` | 数据目录、卡片总数、各边分布、`items.json` 最后写入时间、收件箱在途状态（`inbox.consumerState`） | 否 |
| `leash_list` | 卡片列表（文本预览 / 文件名），可按 `edge`、`kind` 筛选，`limit` 默认 50 | 否 |
| `leash_read` | 单张卡完整内容：文本卡返回原文；图片/文件卡返回 blob 绝对路径 | 否 |
| `leash_put` | 挂一条文本或本地文件（`path` 是顶层参数，走宿主路径守卫） | 否 |
| `leash_remove` | 删掉一张卡（**不可恢复**） | **是** |
| `leash_move` | 把一张卡片移到另一条边（`left` / `top` / `right` / `bottom`） | 否 |
| `leash_paste` | 让挂件粘贴系统剪贴板当前内容（插件不读剪贴板） | 否 |

`leash_read` / `leash_remove` / `leash_move` 的 `id` 都支持 ≥4 位前缀；歧义时返回候选而不猜。
前缀展开在插件侧完成，**落盘端只接受完整 Guid**（防手抖前缀删错卡）。

收件箱线格式是 v2，一个文件一条命令，`action ∈ {add, remove, move, paste}`（缺省 `add`）。
字段级定义见 `src/ScreenLeash/Core/InboxIngest.cs` 顶部注释与本插件 `impl.js` 的 `submitToInbox` 调用点。

`leash_read` 的 `id` 支持 ≥4 位前缀；命中多条时返回候选而不猜。
`leash_put` 一次一条；`path` 是顶层参数，因此在宿主的 `pathParams` 守卫覆盖范围内（声明为 `read`）。

> **工作区外的文件**：`path` 不在当前会话的工作区内时，宿主会在调用到达插件之前就拦下，并要求先授权该路径；系统目录（如 `C:\Windows`）直接拒绝授权。用户自己把文件拖到绳子上不经过宿主，不受这个限制。

## 为什么写入要走收件箱

ScreenLeash 的 `ItemStore` 是卡片集合的**唯一拥有者**：内存持有列表，写盘是 temp → `File.Replace`。
外部直接改 `items.json` 会被它下一次保存整个覆盖 —— 用户看到的是「挂上去的东西过一会儿自己消失」，且无任何报错。

所以插件只写收件箱，落盘交给挂件自己：

```
插件                                ScreenLeash
  │                                     │
  ├─ 写 inbox\x.json.tmp                │
  ├─ rename → inbox\x.json  ──────────► │  Timer 每 1.5s 扫一次
  │   （同卷 rename 原子）               │  → InboxIngest.PumpAsync
  │                                     │  → DragIn.IngestParsedAsync（既有管线：配额/上限/缩略图/原名）
  │                                     ├─ 移入 inbox\done\
```

### 收件箱线格式（别的程序也可以照这个投递）

```json
{
  "source": "tianshu",
  "items": [
    { "kind": "text", "text": "要挂的文字", "edge": "right" },
    { "kind": "file", "path": "D:\\a\\b.docx", "edge": "bottom" }
  ]
}
```

- `kind` 只能是 `text` / `file`；缺省时按 `text` / `path` 推断（推断规则在插件侧，挂件侧要求显式）。
- `edge` 可省略，缺省 `right`（挂件侧回退到最近活跃边）。
- 单文件最多 20 条，一轮最多处理 8 个文件。

### 写文件的三个要求

1. **先写 `.json.tmp` 再 rename 成 `.json`**。挂件只认 `.json`，`.tmp` 永不扫描。
2. **文件必须安静 2 秒以上**才被处理。守卫的是「写入方不守规矩直接写 `.json`」的情况，避免读到半个文件。
3. **文件名不要复用**。处理完会移进 `done\`；同名冲突时归档会加时间戳。

## 测试

插件侧：

```bash
node --test ./test/*.test.mjs   # 67 tests
node tools/check-manifest.mjs   # 清单校验
```

挂件侧（新增 `tests\ScreenLeash.Tests\InboxIngestTests.cs`，23 条）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 test
```

插件侧覆盖点：

- 枚举映射与源码一致
- 真实 `items.json` 形状能读出卡片
- `EffectiveLastAccessTicks` / `HasBlob` 两个 C# 只读派生属性被忽略
- 坏条目丢弃（Text 空 / Text 带 blob / Image 无 blob / 路径穿越 / 枚举越界 / 无 Id）
- `items.json` 坏 → 回退 `.bak`；两份都坏 → 空集不抛错；版本越界视为不兼容
- `leash_read` 前缀匹配、歧义不猜、图片卡返回 blob 绝对路径、blob 缺失标记
- 只读工具调用前后 `items.json` 字节不变
- `leash_put` 写 `.tmp` → rename 不留残留；数据目录不存在时拒绝且不造目录；六种非法参数逐项拒绝；连续两次不覆盖
- `inbox.consumerState` 三态判定（空 / 在途 / 停摆）：只数 inbox 顶层 `.json`，`.tmp` 与 `done/` 不计；`leash_put` 的文案随积压状态切换，停摆时不再承诺 1.5 秒落卡
- `turnStart` 注入：仅在数据目录不存在时注入一次（同会话不重复）、块根标签以插件 id 前缀开头（宿主校验规则）、体积上限、`setBlock` 抛错不外抛、宿主未提供 `promptContext` 时静默返回

挂件侧覆盖点：线格式解析（大小写不敏感、缺 edge 回退、坏 JSON、空 items、超条数截断）、
`.tmp` 不处理、安静期生效、坏文件进 `bad\`、成功归档进 `done\`、缺失源文件计 skipped、
一轮最多 `MaxFilesPerPump` 个、无 inbox 目录不抛错。

## 安装

**前置条件：本机需先安装 ScreenLeash 本体。**

下载：[ScreenLeash Releases](https://github.com/l410728l-a11y/ScreenLeash/releases/latest) → 取最新的 `ScreenLeash-*-win-x64.zip` → 解压 → 双击 `ScreenLeash.exe`（不需要安装 .NET）。

> 不写死版本号：本体发行产物名随版本变（源码里 `ScreenLeash.csproj` 与服务端 Release 的编号不一定相同），写死必过时。以 Releases 页当次最新的 `win-x64` 目录版为准。

插件不包含本体，也代替不了它：天枢插件跑在宿主进程里，画不出桌面窗口，那根绳子必须由 ScreenLeash 自己画。没装本体时插件不会报错，所有工具都会返回"未检测到数据目录"，且不创建任何文件。

从 0.5.2 起还有一条被动提示：插件声明了 `promptContext` 能力与 `turnStart` 生命周期，**每个会话的首轮对话**会检查一次数据目录；目录不存在时，向这一轮注入一段短文本（本机路径 + 下载地址），同一会话最多注入一次，目录正常时一个字节都不注入。所以装完插件、重启天枢、随便说一句话，就能在回答里看到「要先装本体」，不必先猜到说什么话才会触发工具。

**本体在跑但看不到绳子**：本体默认只在屏幕最右侧显示一条细线（实际线宽 2.5dip，其余像素透明），鼠标贴到屏幕右边缘 40dip 内才展开；左/上/下三条边默认关闭，要自己在本体设置里开。插件不解析本体的 `settings.json`——该文件不存在即全默认，且本体加载时会按 `Version` 迁移改写取值，外部照着文件读会得出与本体实际行为不同的结论，所以这类判断只能看本体自己的设置界面。

**装了本体但没启动时**是另一种静默：只读工具仍能读到历史卡片，但 `leash_put` 投递的命令会一直躺在 `inbox\` 里没人消费。用 `leash_info` 的 `inbox.consumerState` 自查：

| 值 | 含义 |
|---|---|
| `idle` | 收件箱空，无从判断本体是否在跑（不是「在跑」的证据） |
| `draining` | 有在途命令但都未超时，消费端看起来正常 |
| `stalled-or-absent` | 有命令超过 10 秒没被取走——本体没在跑，或进程在但卡住了 |

判据只用「收件箱里的 `.json` 有多久没被移走」这一条可观测事实，不做进程探测（插件没有 shell 权限，本体也没留下 pid 文件）。所以它能否定「在跑」，不能肯定「在跑」。

桌面端：把整个目录复制到「天枢数据目录」下的 `.rivet\plugins\leash-bridge\`（该目录由启动器配置的 `rivetHome` 决定，形如 `<rivetHome>\TianshuData\.rivet\plugins\leash-bridge\`），
托盘右键退出后重新打开（关窗口只是最小化，不会重新扫描）。

注意：新插件会改变会话的工具表，宿主重启后旧会话可能因连续性校验（工具表哈希不一致）无法续接 —— 这不是插件的问题，是宿主的前缀缓存机制。重启前先收尾手头工作。

## 未做的事

- `image` 类型的 `add`（位图字节）：挂件侧没有对应入口，插件也不读剪贴板。要挂截图用 `leash_paste` 或用户自己 Ctrl+V。
- **不做 `items.json` 缓存**（已知限制，有意为之）：`info` / `list` / `read` 每次调用都全量读取并解析 `items.json`。实测 p50：10 张卡 0.10ms、1,000 张卡（578 KB）3.99ms、10,000 张卡（5.8 MB）32ms。加缓存要引入失效判定，而 `items.json` 的唯一写者是挂件（它能被外部改写的假设不成立但无法从进程外可靠观测），缓存与挂件的下次保存之间会有窗口期——风险大于 4ms 的收益。若未来卡片数量级变化，重新评估。
- **不做工具级重试**：写操作失败直接返回 `write-failed`，由调用方决定是否重试，避免插件内部重复投递造成重复落卡。
