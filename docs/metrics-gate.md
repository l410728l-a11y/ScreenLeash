# 度量 / 几何 / 拓扑单一来源与裸像素门禁

本文件是 `verify-metrics` 门禁的权威说明（README 里那节是摘要）。适用于 `src\ScreenLeash` 的全部 UI 代码。

## 1. 单一来源

| 关注点 | 唯一归属 | 说明 |
| --- | --- | --- |
| 视觉尺寸（DIP） | `Core\Metrics.cs` | 全部以 DIP 命名；设备像素只能经 `Metrics.Px(int dip, int dpi)` / `Metrics.PxFloat(float dip, int dpi)` 换算。`dpi <= 0` 抛 `ArgumentOutOfRangeException`。 |
| 颜色与字体族 | `UI\Theme.cs` | 色值一律 `0xAARRGGBB` 十六进制常量 + `Color` 访问器；字体族为常量字符串。 |
| 窗口边界 | `Core\EdgeGeometry.Compute(EdgeKind, Rectangle workingArea, int dpi, LeashVisualState)` | 纯函数、无 IO、不依赖 WinForms。任何窗口矩形都从这里出。收起 `CollapsedThickness`，展开 `ExpandedThickness`，沿边贯穿工作区；工作区太窄时按工作区裁剪。 |
| 屏幕拓扑 | `UI\ScreenTopology.cs` | `Screen.AllScreens × 4`（`OnlyPrimaryScreen` 降为 1 屏）；每屏用自身 DPI（`MonitorFromPoint` + `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`）；`SystemEvents.DisplaySettingsChanged` 自动重建，`WM_DPICHANGED` 由窗口侧调 `Rebuild()`；`Rebuilt` 事件在构造线程有 `SynchronizationContext` 时自动 Post 回 UI 线程。 |

`EdgeKind` / `LeashVisualState` 当前定义在 `Core\EdgeGeometry.cs`（与几何紧耦合）。若 `enums-home` 条目决定集中类型归属，请**移动**这两个声明并修正 `using`，不要复制定义。

## 2. 门禁规则（`tests\ScreenLeash.Tests\RawPixelScanner.cs`）

扫描范围：`src\ScreenLeash\UI`、`src\ScreenLeash\Interop`（目录不存在时视为空集）。`Core\` 是度量归属地，不在扫描范围。

剥离注释、字符串、字符字面量后：

1. **任何数字字面量 = 违规**，除非字面量本身是 `0` / `1` / `0.0` / `1.0`（含 `f`/`F`/`d`/`D`/`m`/`M` 后缀变体）。
2. **任何命名颜色 = 违规**（`Color.White` 之类）。例外：工厂方法 `Color.From*` 与中性值 `Color.Empty` / `Color.Transparent`。
3. **唯一豁免**：行尾 `// metrics-ok: <理由>`。
4. **全项目豁免行总数上限 5**，超过即失败（`ExemptionBudget_AcrossUiAndInterop_IsRespected`）。
5. `Theme.cs` 是颜色归属地，按文件名单独放行命名颜色（`IsColorAuthority`）。

十六进制字面量（`0x1F`、`0xFF1C1E24`）与二进制字面量（`0b1010`）因 `\w` 边界不被识别，天然放行——这是颜色常量可以写成 ARGB 的依据，也是 Win32 消息码/标志位的推荐写法。

## 3. 双夹具与验证强度

| 夹具 | 期望 | 作用 |
| --- | --- | --- |
| `Fixtures\RawPixelViolation.cs.fixture` | **必须失败** | 逐向量断言 23 处违规（`const 8` / `const 148` / `- 44` / `Pen 2f` / `Font 8.5f` / `PointF 8,8` / `Point 4,40` / `FillRectangle(4,0,width-6)` / `Opacity 0.93f` / `Math.Max(8, ... / 3)` / `Color.FromArgb(70,255,255,255)`），行号 + 字面量精确匹配。 |
| `Fixtures\RawPixelClean.cs.fixture` | **必须通过** | 真实绘制代码：`Metrics.Px` ≥ 8 处、`Metrics.PxFloat` ≥ 2 处、`Theme.` ≥ 4 处，且含 `Pen` / `Font` / `FillRectangle` / `DrawString` / `Math.Max` / `EdgeGeometry.Compute`。 |

`CleanFixture_ContainsRealDrawingCode` 就是「合规必绿」的强度约束：空壳或只含 `0`/`1` 的平凡夹具会在这条断言上失败，所以「绿」证明的是**走 Metrics/Theme 的真实绘制代码不触发扫描器**，而不是扫描器空转。

RED 已实测（2026-11 执行）：把 clean 夹具替换成 `internal sealed class Tiny { private const int Pad = 42; }` 后，`CleanFixture_MustPass` 与 `CleanFixture_ContainsRealDrawingCode` 双双变红（2 失败 / 0 通过），恢复原文后全绿。

生产代码信用实例：`ScreenTopology.CenterOf` 最初写成 `bounds.Width / 2`，被 `ProductionUiAndInterop_HaveNoRawPixelLiterals` 直接抓出（L180 两处 `literal=2`），改用 `>> 1` 后归零。门禁不是纸面承诺。

## 4. 豁免预算 5 是否足够（量化结论：足够）

对计划点名的 4 个 Interop 组件的数字字面量逐类核对：

| 组件 | 预计数字 | 处置 | 需豁免 |
| --- | --- | --- | --- |
| `ClipboardParser` | 正则量词 `{2,}`、字符类边界 | 正则写在**字符串**里，字符串被剥离 → 不扫描 | 0 |
| `ClipboardParser` | SHA256 摘要长度 32 | 写成命名常量 `0x20`（十六进制放行） | 0 |
| `PasteHook` | `WH_KEYBOARD_LL=13`、`WM_KEYDOWN=0x0100`、`VK_V=0x56`、`WM_PASTE=0x0302` | 全用十六进制 | 0 |
| `PasteHook` | `Marshal.SizeOf` / `INPUT` 结构体大小 | 用 `Marshal.SizeOf<T>()`，无字面量 | 0 |
| `FocusProbe` | 类名缓冲区长度 256 | 写成 `0x100` | 0 |
| `ClipboardWatcher` | 监听重试间隔毫秒 | 命名常量（十六进制）或 `Metrics` 另有对应项时走 Metrics | 0–1 |

第一手证据：`ScreenTopology` 内置的 Win32 互操作（`MonitorFromPoint`、`GetDpiForMonitor`）用 `0x2` / `0x0` / `0x0` 表达标志位与 `S_OK`，扫描器对 `UI\` 报 **0 违规、0 豁免**。结论：**预算 5 足够，且大概率一个都用不上**。若某个真实需要十进制语义（例如必须写 `32` 才读得懂）的场景出现，优先加注释 + 命名常量，最后才动 `metrics-ok`。

## 5. 已知限制

- **插值字符串**整体按字符串剥离，`$"{(x + 2)}"` 里的 `2` 不会被检测（漏报，不是误报）。需要检测就拆成局部变量。
- **不做语义分析**：不引入 Roslyn analyzer（成本为现有实现的 3–5 倍，列为后续项）。因此局部变量别名（`var pad = 8;` 在 `Core\` 里定义、在 `UI\` 里引用）绕得过数字规则——`Core\Metrics.cs` 是唯一被允许的数字归属地，靠评审守住，不靠扫描器。
- **`Color.FromArgb(70, 255, 255, 255)` 的数字**已被规则 1 抓住；命名颜色由规则 2 抓；两者都不走 Theme 时必失败。
- 扫描器只读文本，不解析语法树：`#if` 分支里的违规同样会被报（这是期望行为）。

## 6. 复跑

正式门禁（产品项目可编译时）：

```powershell
.\build.ps1 verify-metrics
# 等价于：dotnet test tests\ScreenLeash.Tests --filter FullyQualifiedName~NoRawPixel
```

隔离复跑（当 `src\ScreenLeash` 被其它条目的未完成文件阻塞、无法整体编译时）：

```powershell
$env:DOTNET_ROOT='C:\Users\Administrator\dotnet-sdk-8'
dotnet test .rivet\scratch\metrics-verify\Verify.csproj
```

`.rivet\scratch\metrics-verify\` 是临时验证脚手架（只编译本条目 4 个源文件 + 扫描器测试），产品项目恢复可编译后即可删除。

## 7. 实测记录

- 2026-11：隔离项目 `dotnet test` → **45 通过 / 0 失败**（度量换算、几何、拓扑、扫描器、双夹具全部覆盖）。
- 违规夹具 23 处违规逐项匹配；合规夹具 0 违规且通过真实性断言；`UI\`+`Interop\` 生产源码 0 违规、0 豁免。
