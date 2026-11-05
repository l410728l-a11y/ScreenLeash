# 探针与实测记录（可复核）

本文件记录 ScreenLeash 关键机制的**实测结果**。每个数字都对应 `tools/probes/` 下可重跑的探针源码——
探针本身在版本库内（不是 `.rivet/scratch`），任何人在干净检出后都能复核，不是"只在某次会话里跑过"。

复现：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probes\run-probes.ps1
```

环境：Windows 11 26200 / .NET 8.0.427（私有 SDK `C:\Users\Administrator\dotnet-sdk-8`）/ 单屏 2048×1152 / `GetDpiForSystem=96`。

---

## P1. 低级键盘钩子：可安装 / 可捕获 / 可吞键

探针：`tools/probes/hook-probe`（`hookprobe.exe`，加 `--swallow` 切换是否吞键）

**对照组（不吞，默认）** 原始输出：

```
[17:28:03.242] start swallow=False
[17:28:03.299] hook installed=True lastError=0
[17:28:04.209] sending synthetic Ctrl+V
[17:28:04.211] hook hit #1 swallow=False
[17:28:04.215] window KeyDown V #1
[17:28:05.122] sending synthetic Ctrl+V (2nd)
[17:28:05.123] hook hit #2 swallow=False
[17:28:05.124] window KeyDown V #2
[17:28:06.034] RESULT hookHits=2 windowHits=2 swallow=False
[17:28:06.035] PROBE_PASS
```

**吞键组（`--swallow`）** 原始输出：

```
[17:28:06.158] start swallow=True
[17:28:06.213] hook installed=True lastError=0
[17:28:07.120] sending synthetic Ctrl+V
[17:28:07.122] hook hit #1 swallow=True
[17:28:08.031] sending synthetic Ctrl+V (2nd)
[17:28:08.032] hook hit #2 swallow=True
[17:28:08.934] RESULT hookHits=2 windowHits=0 swallow=True
[17:28:08.935] PROBE_PASS
```

结论：钩子可安装（`lastError=0`）；两组都捕获到 2 次 Ctrl+V；返回 `(IntPtr)1` 后目标窗口 **windowHits 从 2 降到 0**，吞键确实生效。

---

## P2. 端到端：钩子线程 → PostMessage → UI 线程窗口

探针：`hookprobe.exe --e2e`（钩子装在后台 STA 线程，主线程建隐藏窗口接收）

原始输出：

```
[18:18:01.894] e2e: sink hwnd=0x250728
[18:18:01.906] e2e: hook installed=True lastError=0 thread=4
[18:18:02.800] e2e: main thread=1, sending Ctrl+V
[18:18:02.802] hook hit #1 thread=4 swallow=False
[18:18:02.802] post to 0x250728 ok=True
[18:18:02.803] sink: paste request #1 on thread=1
[18:18:03.722] hook hit #2 thread=4 swallow=False
[18:18:03.722] post to 0x250728 ok=True
[18:18:03.724] sink: paste request #2 on thread=1
[18:18:04.615] e2e: RESULT hookHits=2 pasteRequests=2 windowKeyDowns=0
[18:18:04.615] E2E_PASS
```

结论：钩子运行在**后台线程**（thread=4），`PostMessage` 返回 `True`，消息由**主线程**（thread=1）收到（`paste request #1/#2`），且同时按键被吞（`windowKeyDowns=0`）。整条「捕获 → 投递 → UI 处理」链路成立，不只是"能吞键"。

---

## P3. SynchronizationContext 在 `Application.Run` 之前的真实状态

探针：`hookprobe.exe --scc`

原始输出：

```
[18:18:01.766] scc: before any control = NULL
[18:18:01.787] scc: after new Form = System.Windows.Forms.WindowsFormsSynchronizationContext
[18:18:01.794] scc: after Handle created = System.Windows.Forms.WindowsFormsSynchronizationContext
[18:18:01.800] scc: after Show = System.Windows.Forms.WindowsFormsSynchronizationContext
[18:18:01.808] scc: (no-show path) current = System.Windows.Forms.WindowsFormsSynchronizationContext
[18:18:01.808] SCC_DONE
```

结论：**创建任何 Control 之前 `SynchronizationContext.Current` 是 NULL**，`new Form()` 之后才被安装。
因此如果四条边全关（不创建任何窗口），它永远是 NULL——**钩子的消息投递不能依赖 SynchronizationContext**，必须用 `PostMessage` 到宿主窗口。

---

## P4. 钩子回调耗时（含一次完整焦点判定）

探针：`tools/probes/cost-probe`（`costprobe.exe`），回调内执行与 `FocusProbe.ShouldTakeOverPaste()` 等价的工作：
`GetForegroundWindow` → `GetWindowThreadProcessId` → `GetGUIThreadInfo` → `GetClassNameW`（含一次 `new StringBuilder(256)` 分配）。

原始输出：

```
[18:25:46.423] hook installed=True lastError=0
[18:25:46.838] sample #1: 913.2 us (noPasteTarget=False)
[18:25:46.841] sample #2: 31.7 us (noPasteTarget=False)
[18:25:46.842] sample #3: 29.7 us (noPasteTarget=False)
[18:25:49.274] count=60 min=9.4us avg=54.2us p99=913.2us max=913.2us
[18:25:49.274] COST_PASS
```

结论：稳态 **9–32μs**，`avg=54.2μs`，`p99=913.2μs`（首个样本含 JIT 编译）。
`LowLevelHooksTimeout` 是**秒级**（默认 5000ms = 5,000,000μs），相差 5 个数量级。
因此：回调内允许调用 `FocusProbe`；正确的性能口径是**零阻塞（每次 < 100μs）**，不是"零分配"。

---

## 未验证项

以下**没有**实测数据，不得当作已验证：

- **物理按键**：P1–P4 全部用 `keybd_event` 合成输入。低级钩子对合成与物理输入一视同仁（`KBDLLHOOKSTRUCT.flags` 有 `LLKHF_INJECTED` 标记可区分，本方案未区分），但未做人工物理按键对照。
- **剪贴板被死锁时的行为**：未构造 `OpenClipboard` 被长期占用的场景；`ClipboardParser` 的 STA 隔离设计基于 Win32 语义，未在本机复现。
- **杀软拦截**：探针只短时运行未被拦截，长期常驻是否被 Defender/第三方安全软件关注未知。
- **多显示器 / 高 DPI**：本机单屏 96 DPI，混合 DPI 场景未测。

---

## 复现脚本

`tools/probes/run-probes.ps1` 会依次构建并运行上述探针，把原始输出打到控制台。
脚本退出码非 0 表示某阶段失败（`PROBE_FAIL` / `E2E_FAIL` / `COST_FAIL`）。
