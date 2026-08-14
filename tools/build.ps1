<#
.SYNOPSIS
    Builds the Revit Spatial Analyzer solution.

.DESCRIPTION
    One deterministic entry point for building, so that a build on a developer
    machine and a build in CI are the same operation. Deliberately small: it
    restores and builds, and it exits non-zero when that fails. Running tests
    and checking repository hygiene belong to tools/verify.ps1.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER RevitApiDir
    Overrides the location of the Revit 2025 installation supplying
    RevitAPI.dll and RevitAPIUI.dll. Defaults to the value in
    Directory.Build.props. Use this when Revit is not installed under
    C:\Program Files\Autodesk\Revit 2025.

.PARAMETER NoRestore
    Skips the restore step for a faster incremental build.

.EXAMPLE
    .\tools\build.ps1

.EXAMPLE
    .\tools\build.ps1 -Configuration Release -RevitApiDir "D:\Autodesk\Revit 2025"
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [string] $RevitApiDir,

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'SpatialAnalyzer.sln'

function Write-Step {
    param([string] $Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Stop-WithFailure {
    param([string] $Message)
    Write-Host ""
    Write-Host "BUILD FAILED: $Message" -ForegroundColor Red
    exit 1
}

Write-Host "Revit Spatial Analyzer - build" -ForegroundColor White
Write-Host "  configuration : $Configuration"
Write-Host "  solution      : $solution"

# The .NET SDK is not implied by the presence of a .NET runtime. A machine can
# happily run Revit 2025 and still be unable to compile anything for it, so this
# check exists to fail with an actionable message rather than a missing command.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Stop-WithFailure "The 'dotnet' command was not found. Install the .NET 8 SDK (winget install --id Microsoft.DotNet.SDK.8 -e)."
}

$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure "'dotnet --list-sdks' failed."
}
if (-not ($sdks | Where-Object { $_ -match '^8\.0\.' })) {
    Write-Host ""
    Write-Host "Installed SDKs:" -ForegroundColor Yellow
    $sdks | ForEach-Object { Write-Host "  $_" }
    Stop-WithFailure "No .NET 8 SDK found. global.json pins the 8.0.4xx band; install it with 'winget install --id Microsoft.DotNet.SDK.8 -e'."
}

if (-not (Test-Path $solution)) {
    Stop-WithFailure "Solution not found at $solution"
}

$commonArgs = @('--nologo')
if ($RevitApiDir) {
    if (-not (Test-Path $RevitApiDir)) {
        Stop-WithFailure "RevitApiDir does not exist: $RevitApiDir"
    }
    $commonArgs += "-p:RevitApiDir=$RevitApiDir"
}

if (-not $NoRestore) {
    Write-Step "Restoring"
    & dotnet restore $solution @commonArgs
    if ($LASTEXITCODE -ne 0) { Stop-WithFailure "restore failed." }
}

Write-Step "Building ($Configuration)"
$buildArgs = @($solution, '-c', $Configuration) + $commonArgs
if ($NoRestore) { $buildArgs += '--no-restore' }

& dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) { Stop-WithFailure "compilation failed." }

Write-Host ""
Write-Host "BUILD OK ($Configuration)" -ForegroundColor Green
exit 0
