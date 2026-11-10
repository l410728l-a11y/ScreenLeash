# ScreenLeash —— 屏幕边缘的绳子挂件

挂在屏幕四边的小绳子：把截图、图片或一句话拖进去 / 粘贴进去，需要时再拖出来用。
鼠标不拖东西时收起成细线，有东西拖近或悬停时弹出。

> 当前状态：**功能完整可运行**。`src` 下 27 个 `.cs` / 8,258 行，`tests` 193 项全部通过；
> 四边绳子、卡片数据层、收件箱通道（外部程序投递）、全局 Ctrl+V 粘贴、托盘菜单均已接入。
>
> **已知未做**：exe 只做了**自签名**（证书 `CN=ScreenLeash Self-Signed`，不在系统信任根内，Windows 会显示「未知发布者」，SmartScreen 仍可能拦截）；无自动更新；多显示器下的绳条位置记忆未验证。

## 环境

- Windows 10 / 11（本机为 Windows 11 26200）
- .NET SDK 8.0 或更高（目标框架 `net8.0-windows`）。已在 .NET SDK **9.0.316** 上验证：还原、Release 构建、193 项测试全部通过；不需要预装 WindowsDesktop Runtime 8.0.33。
- 若本机装了多个 SDK，用 `dotnet --list-sdks` 确认版本；`build.ps1` 不依赖任何固定安装路径。
- 以自包含方式发布（`publish -SelfContained`）时，目标机器无需安装 .NET 运行时。

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

### 托盘菜单与全局键盘钩子

托盘右键菜单含「开机自启」开关（`src\ScreenLeash\Program.cs:174` → `ToggleStartup()`）：
勾选后写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，取消时删除该项；菜单勾选态
始终回读注册表真实状态（`AppSettings.IsStartupRegistered()`），不会显示假状态。

程序会安装一个**全局低级键盘钩子**（`WH_KEYBOARD_LL`，`src\ScreenLeash\Interop\PasteHook.cs:234`），
用于识别 Ctrl+V：仅当前台没有可粘贴目标时，把这次粘贴转成「挂到绳子上」。钩子只判断按键是否
构成 Ctrl+V，不记录、不缓存、不发送按键内容；钩子装不上时降级为托盘菜单手动粘贴，并在启动
气泡里点名降级路径。

## 裸像素门禁（verify-metrics）

`tests\ScreenLeash.Tests` 里的 `NoRawPixelScanner` 扫描 `src\ScreenLeash\UI` 与
`src\ScreenLeash\Interop` 下的 C# 源码：

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
管理设置 → 排除项 → 添加文件夹。

### SmartScreen 拦截怎么过

发布包里的 `ScreenLeash.exe` 已用自签名证书签过（`CN=ScreenLeash Self-Signed`），
但**自签名不在系统信任根内**，所以 Windows 会把它当成「未知发布者」——`signtool` 验证会报
「已处理证书链，但是在不受信任提供程序信任的根证书中终止」。这三种办法都能过，任选一：

1. **用 7-Zip 解压**（推荐）：7-Zip 不像资源管理器那样给解压出的文件写「来自互联网」标记，
   没有该标记就不会触发 SmartScreen。
2. **解压后右键 `ScreenLeash.exe` → 属性 → 勾选底部「解除锁定」→ 应用**。
3. **PowerShell**：`Unblock-File .\ScreenLeash.exe`

都不要的话，在拦截弹窗里点「更多信息 → 仍要运行」也能启动。

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
dotnet test .rivet\scratch\metrics-verify\Verify.csproj
```
