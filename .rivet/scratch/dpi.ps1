Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class DpiProbe {
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
"@
"GetDpiForSystem        = " + [DpiProbe]::GetDpiForSystem()
"GetDpiForDesktopWindow = " + [DpiProbe]::GetDpiForWindow([DpiProbe]::GetDesktopWindow())
$reg = Get-ItemProperty "HKCU:\Control Panel\Desktop" -Name LogPixels -ErrorAction SilentlyContinue
"LogPixels(reg)         = " + $reg.LogPixels
"SM_CXSCREEN            = " + [DpiProbe]::GetSystemMetrics(0)
"SM_CYSCREEN            = " + [DpiProbe]::GetSystemMetrics(1)
Add-Type -AssemblyName System.Windows.Forms
$s = [System.Windows.Forms.Screen]::PrimaryScreen
"Screen.Bounds          = " + $s.Bounds.ToString()
"Screen.WorkingArea     = " + $s.WorkingArea.ToString()
