# ScreenLeash 第一版交接说明

> **版本标记**：`v1`（git tag），对应提交 `449c0b0`
> **定版日期**：2026-11-06
> **用途**：这是用户认可的第一版。后续改动属于第二版，需用户明确要求才更新。
> **恢复方式**：`git checkout v1`，或 `git reset --hard v1`（会丢后续提交，谨慎）

---

## 一、这一版能做什么（用户实测确认过的）

| 功能 | 状态 | 备注 |
|---|---|---|
| 右侧一条绳子（左侧已关） | ✅ | `AppSettings.EnableLeft` 默认 false，v2→v3 迁移强制关左侧 |
| 拖入文件 / 图片 / 文字 | ✅ | 图片走 `Kind.Image` + 缩略图 |
| 拖出到桌面 / 微信 | ✅ | 文件名保持拖入时的原名（`Item.DisplayName`） |
| 文字拖到微信 / 天枢 | ✅ | 同时给 `UnicodeText` + `Text` 两种格式 |
| 上下拖动排序 | ✅ | `EdgeLayout.ComputeInsertIndex` + `ItemStore.MoveToAsync` |
| 右键删除卡片 | ✅ | 连预览一起收 |
| 退出重启后内容保留 | ✅ | `%LOCALAPPDATA%\ScreenLeash\items.json` 原子写 |
| 全局 Ctrl+V 兜底 | ✅ | 后台 STA 线程 + `PostMessage` 投递 |
| 悬停弹出卡片 | ✅ | 40dip 内展开到 140dip |
| 拖东西靠近能挂上 | ✅ | 见下方「关键设计」第 1、2 条 |

## 二、关键设计决策（改代码前必读）

这几条都是**实测得出的**，不是文档推断——推翻它们之前请先复测。

### 1. 收起态窗口宽度是 40dip，不是 6dip

`Metrics.cs` `CollapsedThickness = 40`。

**为什么**：OLE 拖放只把 `DragEnter` 发给"光标所在的窗口"。窗口 6dip 时用户必须**精确落在线上**才能挂——用户原话"只有完全到绳子上才能拖进去"。

**代价**：40dip 的窗口会让鼠标点击范围变宽，所以配了第 3 条。

### 2. `TransparencyKey` 的透明像素**不会**穿透 OLE 拖放

这是本会话实测推翻的一条假设（我此前按 Windows 文档推断"会穿透"，据此走了很多弯路）。

**证据**：把 `CollapsedThickness` 从 6 改成 40 后，用户实测"效果达到了"（不落在线上也能挂）。

**但**：透明像素**会不会**穿透**鼠标点击**，未单独验证——所以有了第 3 条。

### 3. 鼠标点击穿透用 `WM_NCHITTEST` + `HTTRANSPARENT`

`LeashForm.WndProc`（约 854 行）、`IsInteractiveAt`（约 827 行）。

**机制**：
- 鼠标命中走 `WM_NCHITTEST` → 返回 `HTTRANSPARENT` 就穿透到下面的桌面
- OLE 拖放走 `RegisterDragDrop`，**不看** `WM_NCHITTEST` → 不受影响

**所以**：收起态只有绳线附近（`RopeStrokeWidth + RopeHitTolerance` = 5.5dip）可点，其余交给桌面。

### 4. `SetDropOpaque` 的条件不是"左键按下"

`LeashHost.cs:379`：

```csharp
form.SetDropOpaque(leftDown && (form.IsDropHighlighted || !form.IsExpanded));
```

**历史坑**：
- 最初是 `SetDropOpaque(leftDown)` → 单击图标就整块变黑挡操作（用户反复反馈）
- 改成 `leftDown && !IsExpanded` → 展开后恢复透明 → 透明像素穿透 → OLE 判定光标离开 → `OnDragLeave` → 收起 → 又展开 → **"闪一下缩回去"**
- 现在：**外部拖入期间（`IsDropHighlighted`）一直保持不透明**，拖放链路不会自己打断自己

### 5. layered window 方案试过，已回滚

`c198bd8` 试过用 `WS_EX_LAYERED` + `UpdateLayeredWindow` 替代 `TransparencyKey`，被 `e3f253a` 回滚。

**回滚原因**：改完后两边都不显示线、拖进去没反应。

**推断根因**（**未证实**）：`Bitmap.GetHbitmap()` 返回的 GDI 位图不含 alpha 通道，`UpdateLayeredWindow` 按 `AC_SRC_ALPHA` 读到全 0 → 整窗全透明。正确做法应改用 `CreateDIBSection` + `Bitmap.LockBits` 自己建 32bpp DIB。

**探针结论**（`tools/` 或 `.rivet/scratch/layered-probe`，已随回滚删除源码）：layered window **确实能**同时半透明 + 接收 OLE 拖放（用户手动拖放实测 `DRAG_DROP => LAYERED_WINDOW_RECEIVES_DROP`）。

> 也就是说：layered 方向本身可行，只是渲染那一层写错了。第二版如果要根治"透明与拖放互斥"，可以从这里继续。

## 三、关键参数（都在 `src/ScreenLeash/Core/Metrics.cs`）

| 常量 | 值 | 作用 |
|---|---|---|
| `CollapsedThickness` | 40 | 收起态窗口厚度（拖放接收区） |
| `ExpandedThickness` | 140 | 展开态面板宽度 |
| `RopeStrokeWidth` | 2.5 | 绳线视觉宽度（**没变过**，看到的还是细线） |
| `RopeHitTolerance` | 3 | 收起态绳线周围可点余量 |
| `EdgeTriggerZone` | 40 | 悬停展开判定半径 |
| `DragApproachZone` | 40 | 拖东西靠近的判定半径 |
| `MagnetSnapZone` | 90 | 跨边互拖的磁铁吸附半径 |
| `DragFreezeWidth` | 280 | 拖动起点距边多远内"冻结"展开（防拖图标误弹） |
| `EdgeSpanRatio` | 0.8 | 绳子沿边只占 80%、居中 |

## 四、已知问题 / 未验证项

| 项 | 状态 |
|---|---|
| 右键菜单弹出变慢 | **已排除本程序**：CPU 0.013%、无鼠标钩子（只装了 `WH_KEYBOARD_LL`）、用户 A/B 开/关程序一样慢 |
| 跨边互拖 | 代码还在（`MagnetSnapZone=90`），但左侧已关，**实际不可用** |
| 诊断日志 `drag-diag.log` | 仍在 `%LOCALAPPDATA%\ScreenLeash\logs\`，会持续写入 |

> **定版时已全部验证**：`449c0b0` 的"闪"修复经用户实测确认——拖文件靠近时窗口稳定展开不闪、松手能挂上（2026-11-06）。

## 五、诊断手段

项目里保留了两组日志，排查时直接读：

| 文件 | 内容 |
|---|---|
| `%LOCALAPPDATA%\ScreenLeash\logs\drag-diag.log` | `justPressed edge=… d=… near=… frozen=…`（冻结判定）、`DrawSurface …`（是深蓝还是近黑） |
| `%LOCALAPPDATA%\ScreenLeash\logs\crash-*.log` | 未捕获异常 |

## 六、门禁（改代码会被拦的两条）

1. **`NoRawPixelTests`**：`UI/` 和 `Interop/` 里禁止出现非 0/1 的数字字面量（尺寸、颜色都要走 `Metrics` / `Theme`）。`Core/` 不扫描。
2. **豁免预算**：`// metrics-ok: <理由>` 全项目上限 5 处。

本会话我在这条门禁上栽过两次（`SourceConstantAlpha = 255`、`>> 16`），都被正确拦下——**拦得对，是我的问题**。

## 七、构建与发布

```bash
export DOTNET_ROOT="C:\Users\Administrator\dotnet-sdk-8"
"$DOTNET_ROOT/dotnet.exe" build src/ScreenLeash/ScreenLeash.csproj -c Release
"$DOTNET_ROOT/dotnet.exe" test tests/ScreenLeash.Tests/ScreenLeash.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 publish -SelfContained
```

产物：`publish\self-contained\ScreenLeash.exe`（约 145MB，自带 .NET 8 运行时，双击即用）。

> 注意：系统只装了 .NET 6，**不要发布 framework-dependent 版本**，否则双击闪退。
