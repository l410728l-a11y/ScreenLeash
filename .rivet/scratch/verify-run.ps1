$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = 'C:\Users\Administrator\dotnet-sdk-8'

# ASCII-only on purpose: Windows PowerShell 5.1 reads BOM-less scripts with the ANSI code page,
# so non-ASCII paths inside this file would be mangled. cwd is set by the caller (repo root).
$exe = Join-Path (Get-Location) 'src\ScreenLeash\bin\Release\net8.0-windows\ScreenLeash.exe'
if (-not (Test-Path $exe)) { Write-Host "EXE_NOT_FOUND $exe"; exit 2 }

$logDir = Join-Path $env:LOCALAPPDATA 'ScreenLeash\logs'
$before = 0
if (Test-Path $logDir) { $before = (Get-ChildItem $logDir -File -ErrorAction SilentlyContinue | Measure-Object).Count }

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4

$p.Refresh()
if ($p.HasExited) {
    Write-Host "PROCESS_EXITED code=$($p.ExitCode)"
    exit 1
}

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinEnum {
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static List<string> ForPid(uint target) {
    var list = new List<string>();
    EnumWindows((h, lp) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r);
        var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
        var txt = new StringBuilder(256); GetWindowTextW(h, txt, 256);
        int w = r.R - r.L, hh = r.B - r.T;
        if (w > 0 && hh > 0) {
          list.Add(cls.ToString() + " | '" + txt.ToString() + "' | " + w + "x" + hh + " @" + r.L + "," + r.T);
        }
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$wins = [WinEnum]::ForPid([uint32]$p.Id)
Write-Host "visible_windows=$($wins.Count)"
foreach ($w in $wins) { Write-Host "  $w" }
Write-Host ("ws_mb={0} handles={1} threads={2}" -f [math]::Round($p.WorkingSet64/1MB,1), $p.HandleCount, $p.Threads.Count)

Start-Sleep -Seconds 3
$p.Refresh()
Write-Host ("ws_after3s_mb={0}" -f [math]::Round($p.WorkingSet64/1MB,1))

$after = 0
if (Test-Path $logDir) { $after = (Get-ChildItem $logDir -File -ErrorAction SilentlyContinue | Measure-Object).Count }
Write-Host "crash_logs_before=$before after=$after"

Stop-Process -Id $p.Id -Force
Write-Host 'VERIFY_DONE'
