# ASCII-only: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.
# 复现用户场景：记事本在前台 + 鼠标停在已展开的绳子上 + Ctrl+V
#   旧实现：钩子先判前台有粘贴目标 -> 放行 -> 内容粘进记事本，items 不变（RED）
#   修复后：先判光标在绳子上 -> 无条件接管 -> items +1（GREEN）
$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = 'C:\Users\Administrator\dotnet-sdk-8'

$exe = Join-Path (Get-Location) 'src\ScreenLeash\bin\Release\net8.0-windows\ScreenLeash.exe'
if (-not (Test-Path $exe)) { Write-Host "EXE_NOT_FOUND"; exit 2 }
$itemsPath = Join-Path $env:LOCALAPPDATA 'ScreenLeash\items.json'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class E2E {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
}
"@

function Get-ItemCount {
    if (-not (Test-Path $itemsPath)) { return 0 }
    try {
        $j = Get-Content $itemsPath -Raw | ConvertFrom-Json
        return @($j.Items).Count
    } catch { return -1 }
}

$marker = 'leash-e2e-' + (Get-Date -Format 'HHmmss')
Set-Clipboard -Value $marker
$before = Get-ItemCount
Write-Host "items_before=$before marker=$marker"

# 前台放任何普通窗口都行：FocusProbe 只在「桌面/无焦点」时返回 true，
# 别的窗口（含终端）都会让它走"放行"分支，正好复现用户场景。
# 不再启动 notepad —— Win11 的 notepad.exe 会重定向到 Store 版，-PassThru 拿到的进程立即退出。

$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4
if ($app.HasExited) { Write-Host "APP_EXITED code=$($app.ExitCode)"; exit 1 }

# 鼠标移到右边缘内侧（屏幕宽 2048，x=2045 落在细线 + 40px 触发带里），让绳子展开
[E2E]::SetCursorPos(2045, 500) | Out-Null
Start-Sleep -Milliseconds 300
[E2E]::SetCursorPos(2045, 500) | Out-Null
Start-Sleep -Milliseconds 900

# 确认光标真的停在边缘——上次设 2040 实际落到 1898，导致绳子没展开
$probe = New-Object E2E+POINT
[E2E]::GetCursorPos([ref]$probe) | Out-Null
Write-Host "cursor_now=$($probe.X),$($probe.Y)"

# 发送 Ctrl+V
[E2E]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
[E2E]::keybd_event(0x56, 0, 0, [UIntPtr]::Zero)
[E2E]::keybd_event(0x56, 0, 2, [UIntPtr]::Zero)
[E2E]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)

Start-Sleep -Seconds 2

$after = Get-ItemCount
Write-Host "items_after=$after"

if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }

if ($after -gt $before) {
    Write-Host "E2E_PASS (pasted into leash)"
} else {
    Write-Host "E2E_FAIL (not captured by leash)"
}
