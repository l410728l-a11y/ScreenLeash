$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = 'C:\Users\Administrator\dotnet-sdk-8'

$exe = Join-Path (Get-Location) 'src\ScreenLeash\bin\Release\net8.0-windows\ScreenLeash.exe'
if (-not (Test-Path $exe)) { Write-Host "EXE_NOT_FOUND"; exit 2 }

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class HoverProbe {
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static List<string> Sizes(uint target) {
    var list = new List<string>();
    EnumWindows((h, lp) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == target && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r);
        int w = r.R - r.L, hh = r.B - r.T;
        if (w > 0 && hh > 0) { list.Add(w + "x" + hh + "@" + r.L + "," + r.T); }
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3
$p.Refresh()
if ($p.HasExited) { Write-Host "EXITED code=$($p.ExitCode)"; exit 1 }

$saved = New-Object HoverProbe+POINT
[HoverProbe]::GetCursorPos([ref]$saved) | Out-Null

Write-Host "--- collapsed (cursor parked far away) ---"
[HoverProbe]::SetCursorPos(600, 600) | Out-Null
Start-Sleep -Milliseconds 1200
foreach ($s in [HoverProbe]::Sizes([uint32]$p.Id)) { Write-Host "  $s" }

Write-Host "--- cursor near right edge (should expand) ---"
[HoverProbe]::SetCursorPos(2040, 500) | Out-Null
Start-Sleep -Milliseconds 1500
foreach ($s in [HoverProbe]::Sizes([uint32]$p.Id)) { Write-Host "  $s" }

Write-Host "--- cursor parked far again (should collapse) ---"
[HoverProbe]::SetCursorPos(600, 600) | Out-Null
Start-Sleep -Milliseconds 1500
foreach ($s in [HoverProbe]::Sizes([uint32]$p.Id)) { Write-Host "  $s" }

[HoverProbe]::SetCursorPos($saved.X, $saved.Y) | Out-Null
$p.Refresh()
Write-Host ("ws_mb={0}" -f [math]::Round($p.WorkingSet64/1MB,1))
Stop-Process -Id $p.Id -Force
Write-Host 'HOVER_DONE'
