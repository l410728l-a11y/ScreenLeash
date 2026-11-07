# ScreenLeash 第二版交接说明（v2 / v2.1）

> **版本标记**：`v2` = `e230a30`（用户实测认可）；`v2.1` = `4c6a5cb`（v2 + 点击不闪蓝）
> **定版日期**：2026-11-07
> **产物**：`publish\self-contained\ScreenLeash.exe`（自包含单文件，双击即用）
> **恢复方式**：`git checkout v2` / `git checkout v2.1`

---

## 一、v2 修了什么（v1 → v2 的六个缺陷）

| 提交 | 症状（用户原话/实测） | 根因 | 修复落点 |
|---|---|---|---|
| `8a95a8c` | 换卡片位置后「40 宽的残留一直」 | 内部拖动把「自己」设为磁铁目标，松手只置空引用、没关高亮，`DrawSurface` 每帧填满窗口 | 三条退出路径统一 `SetDragTarget(null)` |
| `3c5e708` | 点桌面/应用右边「闪一下黑框」 | 判据 `leftDown && !IsExpanded` 在收起态等价于「左键按下」，全屏任何点击都让 40dip 窗口变不透明 | 抽出 `Core\DropOpaquePolicy`：按住 + 移动过 + 光标在窗口内 |
| `2515865` | 拖东西到位「黑框出不来、挂不上」 | `EvaluateCursor` 在同一 tick 先把窗口设为展开，`expanded` 分支拒绝不透明 | 判据改用 `draggingCard`（是否在拖自己的卡片） |
| `4e8b683` | 一拖「悬浮卡连带着弹出来，整体框弹出一百多」 | 悬停判定把「按住拖动靠近」当「悬停」，进入 40dip 就展开 140dip | `EvaluateCursor(cursor, leftDown, mayBeDragging)`：按着并移动期间不因悬停展开 |
| `e230a30` | 拖进去一次后「整条绳子怎么碰都没用」 | OLE 在窗口内松手只发 `DragDrop`、**不发 `DragLeave`**，`_dropHighlight` 永久为 true，`EvaluateCursor` 每帧早退 | `OnDragDrop` 复位 `_dropHighlight` |
| `4c6a5cb` | 鼠标在面板上点一下「闪一下蓝线」 | 单击手抖超过系统拖拽阈值被判成拖动，同边吸附命中自己 → `SetMagnetHighlight(true)` → 整窗深蓝 | `Core\MagnetPolicy`：磁铁高亮只对「另一条绳子」，永不点亮自己 |

### 蓝线问题的证据链（可复核）

`%LOCALAPPDATA%\ScreenLeash\logs\drag-diag.log` 2026-11-07 15:16 段：

```
15:16:32.568 MouseDown loc={X=89,Y=395} hit=2 ...
15:16:32.676 磁铁目标 → Right
15:16:32.676 DrawSurface True edge=Right drop=False magnet=True count=3 state=Hover
15:16:33.143 磁铁目标 → 无
15:16:33.144 DrawSurface False ... magnet=False
```

每次单击卡片后约 100ms 出现 `magnet=True` 的整窗填充，0.3–0.7s 后消失——与「闪一下蓝线」逐帧对应。

---

## 二、改代码前必读的不变量（都是实测得出的）

1. **`_dropHighlight` 必须在两处复位**：`OnDragLeave` **和** `OnDragDrop`。OLE 在窗口内松手只发 `DragDrop`；漏一处就会让 `EvaluateCursor` 永久早退（v1 的 `_dropHighlight` 只影响配色，v2 起它还参与状态机）。
2. **磁铁高亮只用于跨边落点提示**（`MagnetPolicy.ShouldHighlight`）。同边排序命中自己，点亮自己既无语义、又会被单击手抖触发。
3. **收起态取消透明的判据**是 `DropOpaquePolicy`：不能只看「左键按下」（全屏点击都会闪），也不能看「是否展开」（拖近的同一 tick 窗口就会展开）。
4. **`EvaluateCursor` 在「按着并移动」期间不因悬停展开**：`mayBeDragging` 由宿主按「按下后移动超过系统拖拽阈值」提供；`_pressedIndex >= 0`（按在卡片上）例外。
5. **`_internalDrag` 的置位依赖 `_pressedIndex >= 0`**：外部拖入时永远为 -1，所以 `IsDraggingCard` 能区分「拖自己卡片」与「外部东西拖进来」。

---

## 三、已知问题 / 未验证项

| 项 | 状态 |
|---|---|
| 真实 OLE 拖放（桌面→绳子、绳子→微信） | 只有用户手动实测覆盖，无自动化 |
| 跨边互拖 | 代码在（`MagnetSnapZone=90`），但左侧默认关闭（`AppSettings.cs:32`），实际不可达 |
| `drag-diag.log` | 无开关无轮转，持续写入 `%LOCALAPPDATA%\ScreenLeash\logs\`（实测 110KB+） |
| 「透明与拖放互斥」 | 仍是补丁式方案（`SetDropOpaque` 切换 `TransparencyKey`）；根治需 layered window（`c198bd8` 试过、`e3f253a` 回滚，根因 `Bitmap.GetHbitmap()` 无 alpha，未证实） |
| 展开面板空白处按住拖动 | `_pressedIndex` 为 -1 时会短暂变不透明（缝隙 6dip，实际难触发） |
| 拖放源异常崩溃时 `_dropHighlight` | 理论上可残留（OLE 契约内不会发生），未加超时兜底 |

---

## 四、构建 / 测试 / 发布

```bash
export DOTNET_ROOT="C:/Users/Administrator/dotnet-sdk-8"   # 系统 dotnet 只有 Runtime
"$DOTNET_ROOT/dotnet.exe" build src/ScreenLeash/ScreenLeash.csproj -c Release
"$DOTNET_ROOT/dotnet.exe" test  tests/ScreenLeash.Tests/ScreenLeash.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 publish -SelfContained
```

v2.1 实测：构建 **0 警告 0 错误**，全量测试 **157 通过 / 0 失败**。

门禁不变：`NoRawPixelTests` 扫描 `UI\` 与 `Interop\`（禁裸数字字面量 / 命名颜色），豁免预算 5 处；权威说明见 `docs\metrics-gate.md`。

---

## 五、v2 新增的纯策略层（都有单测）

| 文件 | 职责 |
|---|---|
| `Core\DropOpaquePolicy.cs` | 收起态何时临时取消透明键 |
| `Core\MagnetPolicy.cs` | 磁铁吸附高亮是否点亮（只对另一条绳子） |
| `UI\LeashForm.EvaluateCursor(..., mayBeDragging)` | 悬停展开判据 |
