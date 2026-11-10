<#
.SYNOPSIS
    ScreenLeash reproducible build pipeline.

.DESCRIPTION
    Targets: build | test | publish | verify-metrics.

    - build          : Release build of the main project (expects 0 warnings, 0 errors).
    - test           : Run all unit tests.
    - publish        : Publish to publish\framework-dependent (default, reuses Desktop Runtime 8.0.33).
                       Pass -SelfContained for a directory-form self-contained build in publish\standalone
                       (starts fast, no self-extract, no AV false positives).
                       Add -SingleFile to get the old single-file bundle in publish\self-contained instead.
    - verify-metrics : Raw-pixel gate - dotnet test --filter FullyQualifiedName~NoRawPixel, exit 1 on failure.

    Every target uses whatever .NET SDK is on PATH (8.0 or newer works).
    No fixed install path is required.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 verify-metrics

.NOTES
    This file is intentionally ASCII-only: Windows PowerShell 5.1 reads BOM-less
    scripts with the ANSI code page, and non-ASCII text breaks parsing.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'publish', 'verify-metrics')]
    [string]$Target = 'build',

    [switch]$SelfContained,

    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}

# Resolve dotnet: pick the first candidate that actually has an SDK installed.
# A host-only dotnet.exe (no SDK) returns nothing from --list-sdks, so it is skipped.
$candidates = @()
$privateSdk = Join-Path $env:USERPROFILE 'dotnet-sdk-8\dotnet.exe'
if (Test-Path $privateSdk) { $candidates += $privateSdk }
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $candidates += $onPath.Source }

$dotnetExe = $null
foreach ($candidate in $candidates) {
    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnetExe = $candidate; break }
}

if (-not $dotnetExe) {
    Write-Error 'dotnet SDK not found: install .NET SDK 8.0 or newer (dotnet --list-sdks must list a version).'
    exit 1
}

Write-Host "dotnet: $dotnetExe"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$project = Join-Path $root 'src\ScreenLeash\ScreenLeash.csproj'
$testProject = Join-Path $root 'tests\ScreenLeash.Tests\ScreenLeash.Tests.csproj'

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & $dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

function Assert-TestProject {
    if (-not (Test-Path $testProject)) {
        Write-Error "Test project not found: $testProject"
        exit 1
    }
}

switch ($Target) {
    'build' {
        Invoke-Dotnet -Arguments @('build', $project, '-c', 'Release', '--nologo', '-v', 'minimal')
    }

    'test' {
        Assert-TestProject
        Invoke-Dotnet -Arguments @('test', $testProject, '-c', 'Release', '--nologo')
    }

    'publish' {
        $publishRoot = Join-Path $root 'publish'
        if ($SelfContained) {
            if ($SingleFile) {
                $out = Join-Path $publishRoot 'self-contained'
                Invoke-Dotnet -Arguments @(
                    'publish', $project, '-c', 'Release', '-r', 'win-x64',
                    '--self-contained', 'true', '-p:PublishSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true',
                    '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $out)
            }
            else {
                # Directory form: no self-extract step, faster cold start, fewer AV false positives.
                $out = Join-Path $publishRoot 'standalone'
                Invoke-Dotnet -Arguments @(
                    'publish', $project, '-c', 'Release', '-r', 'win-x64',
                    '--self-contained', 'true', '-p:PublishSingleFile=false',
                    '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $out)
            }
        }
        else {
            $out = Join-Path $publishRoot 'framework-dependent'
            Invoke-Dotnet -Arguments @(
                'publish', $project, '-c', 'Release', '-r', 'win-x64',
                '--self-contained', 'false', '-p:PublishSingleFile=false',
                '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $out)
        }

        Write-Host "publish: output -> $out"
    }

    'verify-metrics' {
        Assert-TestProject

        & $dotnetExe test $testProject -c Release --nologo --filter "FullyQualifiedName~NoRawPixel"
        if ($LASTEXITCODE -ne 0) {
            Write-Error "verify-metrics FAILED: raw-pixel gate tests did not pass (exit $LASTEXITCODE)."
            exit 1
        }

        Write-Host 'verify-metrics: PASSED (raw-pixel gate is green)'
    }
}
