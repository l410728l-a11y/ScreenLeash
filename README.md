# ScreenLeash —— 屏幕边缘的绳子挂件

挂在屏幕四边的小绳子：把截图、图片或一句话拖进去 / 粘贴进去，需要时再拖出来用。
鼠标不拖东西时收起成细线，有东西拖近或悬停时弹出。

> 当前状态：**项目骨架**（bootstrap）。可构建、可运行托盘宿主、单实例与崩溃日志已就位；绳子窗口与数据层由后续条目接入。

## 环境

- Windows 10 / 11（本机为 Windows 11 26200）
- .NET 8 SDK。本机系统 `dotnet` 只有主机没有 SDK，私有 SDK 位于：
  `C:\Users\Administrator\dotnet-sdk-8`（含 WindowsDesktop.App 8.0.33）
- `build.ps1` 会自动把 `DOTNET_ROOT` 指向该目录，无需手动设置。

## 构建与验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1              # build（默认）
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 verify-metrics
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 test
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 publish
```

| 目标 | 行为 |
| --- | --- |
| `build` | Release 构建 `src\ScreenLeash\ScreenLeash.csproj`，要求 0 警告 0 错误 |
| `test` | 运行 `tests\ScreenLeash.Tests` 全部测试 |
| `publish` | 默认 framework-dependent（复用 Desktop Runtime 8.0.33）→ `publish\framework-dependent` |
| `publish -SelfContained` | 自包含单文件 → `publish\self-contained`（由 verify 实测后定稿） |
| `verify-metrics` | `dotnet test --filter FullyQualifiedName~NoRawPixel`，失败即 exit 1 |

## 运行方式

构建后直接运行：

```powershell
dotnet src\ScreenLeash\bin\Release\net8.0-windows\ScreenLeash.exe
```

- 启动后常驻托盘；右键托盘图标 → 退出。
- 单实例：重复启动会通过 `RegisterWindowMessage("ScreenLeash.Activate")` +
  `PostMessage(HWND_BROADCAST)` 唤醒已运行实例，新进程立即退出。
- 崩溃日志：`%LOCALAPPDATA%\ScreenLeash\logs\crash-<yyyyMMdd>.log`，
  未处理异常（进程级与 UI 线程级）都会落盘，并用托盘气泡提示。

## 裸像素门禁（verify-metrics）

`tests\ScreenLeash.Tests` 里的 `NoRawPixelScanner` 扫描 `src\ScreenLeash\UI` 与
`src\ScreenLeash\Interop` 下的 C# 源码（这两个目录在后续条目才出现，当前为空集）：

- 任何非 `0` / `1` / `0.0` / `1.0` 的数字字面量 = 违规；
- `Color.xxx` 命名颜色 = 违规（颜色必须走 `Theme`，`UI\Theme.cs` 自身除外）；
- 唯一豁免：行尾 `// metrics-ok: <理由>`；全项目豁免总数上限 5。
- 双夹具自证：`Fixtures\RawPixelViolation.cs.fixture` 必须报违规，
  `Fixtures\RawPixelClean.cs.fixture` 必须报 0 违规（夹具含真实绘制代码：
  `Metrics.Px` 换算、`Pen` 线宽、`Font` 字号、`FillRectangle` 偏移、`Math.Max`）。

扫描器是正则实现（非 Roslyn analyzer）：插值字符串里的数字可能误报，用
`metrics-ok` 豁免；已知限制记录在此，后续如需更强保证再引入 analyzer。

## 杀软白名单

本程序会常驻托盘、注册全局剪贴板/拖放相关能力，部分安全软件可能误报。
若被拦截，把以下目录加入白名单再运行：

- 程序目录：`<仓库>\publish\framework-dependent\`（或你的安装目录）
- 数据目录：`%LOCALAPPDATA%\ScreenLeash\`
- 临时拖出目录：`%TEMP%\ScreenLeash\`

Windows Defender：设置 → 隐私和安全性 → Windows 安全中心 → 病毒和威胁防护 →
管理设置 → 排除项 → 添加文件夹。未签名的 exe 首次运行会被 SmartScreen 拦截，
选择「更多信息 → 仍要运行」即可。

## 门禁实现补充（metrics-dpi 条目的实测口径）

上面那节是摘要，权威说明见 `docs\metrics-gate.md`。三处与实现口径不一致，以本节为准：

- 扫描器类名是 `RawPixelScanner`（测试类 `NoRawPixelTests`；`verify-metrics` 的
  `FullyQualifiedName~NoRawPixel` filter 依赖的是后者）。
- `src\ScreenLeash\UI` 现已非空（`Theme.cs`、`ScreenTopology.cs`），实测 **0 违规、0 豁免**。
- 除数字字面量外，**命名颜色**（`Color.White`）同样判违规；`Color.From*` 工厂方法与
  `Color.Empty` / `Color.Transparent` 属中性值放行。`Theme.cs` 是唯一颜色归属地。
- 插值字符串里的数字实际是**漏报**（整串按字符串剥离），不是误报；需要检测就拆成局部变量。

隔离复跑（产品项目被其它条目未完成文件阻塞时）：

```powershell
$env:DOTNET_ROOT='C:\Users\Administrator\dotnet-sdk-8'
dotnet test .rivet\scratch\metrics-verify\Verify.csproj
```
