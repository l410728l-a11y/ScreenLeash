<#
.SYNOPSIS
    ScreenLeash reproducible build pipeline.

.DESCRIPTION
    Targets: build | test | publish | verify-metrics.

    - build          : Release build of the main project (expects 0 warnings, 0 errors).
    - test           : Run all unit tests.
    - publish        : Publish to publish\framework-dependent (default, reuses Desktop Runtime 8.0.33).
                       Pass -SelfContained for a single-file self-contained build in publish\self-contained.
    - verify-metrics : Raw-pixel gate - dotnet test --filter FullyQualifiedName~NoRawPixel, exit 1 on failure.

    Every target points DOTNET_ROOT at the private SDK on this machine
    (the system dotnet host has no SDK installed).

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

    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$dotnetRoot = 'C:\Users\Administrator\dotnet-sdk-8'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path $dotnetExe)) {
    Write-Error "Cannot find .NET 8 SDK at $dotnetExe . Install SDK 8 there, or edit the dotnetRoot constant in build.ps1."
    exit 1
}

$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
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
            $out = Join-Path $publishRoot 'self-contained'
            Invoke-Dotnet -Arguments @(
                'publish', $project, '-c', 'Release', '-r', 'win-x64',
                '--self-contained', 'true', '-p:PublishSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $out)
        }
        else {
            $out = Join-Path $publishRoot 'framework-dependent'
            Invoke-Dotnet -Arguments @(
                'publish', $project, '-c', 'Release', '-r', 'win-x64',
                '--self-contained', 'false', '-p:PublishSingleFile=false', '-o', $out)
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
