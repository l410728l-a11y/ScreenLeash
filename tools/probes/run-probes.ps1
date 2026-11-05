# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less scripts as ANSI).
# Rebuilds and runs the probe suite behind docs\PROBES.md. Exit code != 0 means a phase failed.
$ErrorActionPreference = 'Stop'

$dotnetRoot = 'C:\Users\Administrator\dotnet-sdk-8'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path $dotnetExe)) {
    Write-Error "Cannot find .NET 8 SDK at $dotnetExe"
    exit 1
}

$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$probeRoot = Join-Path $PSScriptRoot '.'
$hookProject = Join-Path $probeRoot 'hook-probe\hookprobe.csproj'
$costProject = Join-Path $probeRoot 'cost-probe\costprobe.csproj'

function Build-Probe {
    param([string]$Project)
    & $dotnetExe build $Project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Probe {
    param([string]$Exe, [string]$LogPath, [string[]]$Arguments, [string]$ExpectMarker)

    if (Test-Path $LogPath) { Remove-Item $LogPath -Force }

    $startArgs = @{ FilePath = $Exe; PassThru = $true }
    if ($Arguments -and $Arguments.Count -gt 0) { $startArgs['ArgumentList'] = ($Arguments -join ' ') }

    $p = Start-Process @startArgs
    if (-not $p.WaitForExit(60000)) {
        $p.Kill()
        Write-Host "TIMEOUT: $Exe"
        exit 1
    }

    if (-not (Test-Path $LogPath)) {
        Write-Host "NO LOG: $LogPath"
        exit 1
    }

    Get-Content $LogPath | ForEach-Object { Write-Host "  $_" }

    $found = Select-String -Path $LogPath -Pattern $ExpectMarker -SimpleMatch -Quiet
    if (-not $found) {
        Write-Host "FAIL: marker '$ExpectMarker' not found in $LogPath"
        exit 1
    }

    Write-Host "OK: $ExpectMarker"
}

Write-Host '=== build probes ==='
Build-Probe $hookProject
Build-Probe $costProject

$hookExe = Join-Path $probeRoot 'hook-probe\bin\Release\net8.0-windows\hookprobe.exe'
$costExe = Join-Path $probeRoot 'cost-probe\bin\Release\net8.0-windows\costprobe.exe'
$hookLog = Join-Path $probeRoot 'hook-probe\bin\Release\net8.0-windows\hook-probe.log'
$costLog = Join-Path $probeRoot 'cost-probe\bin\Release\net8.0-windows\cost-probe.log'

# P1 (passthrough / swallow) needs the probe window to hold foreground focus, which is
# unreliable when launched from a script. Run those two manually:
#   hookprobe.exe            -> PROBE_PASS with windowHits=2 (key passes through)
#   hookprobe.exe --swallow  -> PROBE_PASS with windowHits=0 (key is swallowed)

Write-Host ''
Write-Host '=== P2: end-to-end (hook thread -> PostMessage -> UI window) ==='
Run-Probe -Exe $hookExe -LogPath $hookLog -Arguments @('--e2e') -ExpectMarker 'E2E_PASS'

Write-Host ''
Write-Host '=== P3: SynchronizationContext before Application.Run ==='
Run-Probe -Exe $hookExe -LogPath $hookLog -Arguments @('--scc') -ExpectMarker 'SCC_DONE'

Write-Host ''
Write-Host '=== P4: callback cost ==='
Run-Probe -Exe $costExe -LogPath $costLog -Arguments @() -ExpectMarker 'COST_PASS'

Write-Host ''
Write-Host 'ALL PROBES PASSED'
