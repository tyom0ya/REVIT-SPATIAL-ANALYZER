<#
.SYNOPSIS
    Builds the Windows installer for the Revit 2025 add-in.

.DESCRIPTION
    Verifies, builds Release, stages only the assemblies this project owns,
    generates the manifest template the installer finishes at install time, and
    compiles the whole thing with Inno Setup.

    The staging step is the point of this script rather than a detail of it.
    Assemblies are enumerated by name rather than copied wholesale, and the
    staging directory is then checked for Autodesk assemblies before anything
    is compiled into an installer. An add-in that redistributes RevitAPI.dll
    risks binding against assemblies that disagree with the host, and it is not
    ours to redistribute in any case.

.PARAMETER Version
    Overrides the version. Defaults to the Version property in
    Directory.Build.props, which is where it should normally be changed.

.PARAMETER RevitApiDir
    Overrides the Revit 2025 installation directory used for building.

.PARAMETER SkipVerify
    Skips tools/verify.ps1. Faster, but packages code that has not been checked.

.EXAMPLE
    .\tools\build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Version,

    [string] $RevitApiDir,

    [switch] $SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$revitVersion = '2025'
$installerDir = Join-Path $repoRoot 'installer'
$stagingDir   = Join-Path $installerDir 'staging'
$outputDir    = Join-Path $installerDir 'Output'
$issPath      = Join-Path $installerDir 'SpatialAnalyzer.iss'
$templatePath = Join-Path $repoRoot 'manifests\SpatialAnalyzer.addin.template'
$buildOutput  = Join-Path $repoRoot "src\SpatialAnalyzer.Revit\bin\Release\net8.0-windows"

function Stop-WithFailure {
    param([string] $Message)
    Write-Host ""
    Write-Host "INSTALLER FAILED: $Message" -ForegroundColor Red
    exit 1
}

function Write-Step {
    param([string] $Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

Write-Host "Revit Spatial Analyzer - build installer" -ForegroundColor White

# ---------------------------------------------------------------------------
Write-Step "Locating Inno Setup"

$iscc = $null
# winget installs Inno Setup per-user by default, which puts it under
# LOCALAPPDATA rather than in either Program Files. All three are checked
# because which one it lands in depends on how it was installed rather than on
# anything this project controls.
$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

foreach ($candidate in $candidates) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}

if (-not $iscc) {
    $onPath = Get-Command iscc -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}

if (-not $iscc) {
    Stop-WithFailure "Inno Setup 6 was not found. Install it with: winget install --id JRSoftware.InnoSetup"
}

Write-Host "  compiler : $iscc"

# ---------------------------------------------------------------------------
Write-Step "Reading version"

if (-not $Version) {
    # Asked for by XPath rather than by property access. Directory.Build.props
    # has more than one PropertyGroup and only one of them carries a Version,
    # so walking the dotted path hands back an array under StrictMode and
    # fails on the group that does not have it.
    [xml] $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($node) { $Version = $node.InnerText.Trim() }
}

if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+$') {
    Stop-WithFailure "Version '$Version' is not three dot-separated numbers. Inno Setup records it as a file version and will reject anything else."
}

Write-Host "  version  : $Version"

# ---------------------------------------------------------------------------
if ($SkipVerify) {
    Write-Step "Building Release (verification skipped)"
    $script = Join-Path $PSScriptRoot 'build.ps1'
}
else {
    Write-Step "Verifying and building Release"
    $script = Join-Path $PSScriptRoot 'verify.ps1'
}

$scriptArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Configuration', 'Release')
if ($RevitApiDir) { $scriptArgs += @('-RevitApiDir', $RevitApiDir) }

& powershell @scriptArgs
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure "$(Split-Path $script -Leaf) failed. No installer was built."
}

# ---------------------------------------------------------------------------
Write-Step "Staging assemblies"

if (-not (Test-Path $buildOutput)) {
    Stop-WithFailure "Release build output not found at $buildOutput"
}

# Named explicitly, never copied wholesale. This enumeration is what guarantees
# an Autodesk assembly cannot be dragged into a redistributable by accident.
$ourAssemblies = @('SpatialAnalyzer.Revit.dll', 'SpatialAnalyzer.Core.dll')
$optionalFiles = @('SpatialAnalyzer.Revit.deps.json')

foreach ($name in $ourAssemblies) {
    if (-not (Test-Path (Join-Path $buildOutput $name))) {
        Stop-WithFailure "Expected assembly missing from Release output: $name"
    }
}

if (Test-Path $stagingDir) {
    Remove-Item $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

foreach ($name in ($ourAssemblies + $optionalFiles)) {
    $source = Join-Path $buildOutput $name
    if (Test-Path $source) {
        Copy-Item $source -Destination $stagingDir -Force
    }
}

# ---------------------------------------------------------------------------
Write-Step "Generating manifest template"

if (-not (Test-Path $templatePath)) {
    Stop-WithFailure "Manifest template not found at $templatePath"
}

# The developer commentary is stripped here rather than at install time. It
# explains the template to whoever maintains it and has no place in a shipped
# artifact; worse, it mentions the placeholder by name, so substituting first
# would rewrite the prose into a sentence claiming the installed file is not a
# usable manifest.
$manifest = [regex]::Replace((Get-Content $templatePath -Raw), '(?s)<!--.*?-->', '')
$manifest = [regex]::Replace($manifest, '(?m)^\s*$\r?\n', '')

if ($manifest -notmatch '\{ASSEMBLY_PATH\}') {
    Stop-WithFailure "The manifest template has no {ASSEMBLY_PATH} placeholder, so the installer would have nothing to substitute."
}
if ($manifest -match '<!--') {
    Stop-WithFailure "Comment stripping failed; the installed manifest would carry template documentation."
}

$generated = Join-Path $stagingDir 'SpatialAnalyzer.addin.in'
[System.IO.File]::WriteAllText($generated, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# ---------------------------------------------------------------------------
Write-Step "Checking nothing of Autodesk's is being packaged"

$leaked = @(Get-ChildItem $stagingDir -Recurse -File |
    Where-Object { $_.Name -match '^(RevitAPI.*|AdWindows|UIFramework.*)\.dll$' })

if ($leaked.Count -gt 0) {
    Stop-WithFailure "Revit API assemblies reached the staging directory: $(($leaked | ForEach-Object { $_.Name }) -join ', ')"
}

Write-Host "  staged   : $((Get-ChildItem $stagingDir -File | ForEach-Object { $_.Name }) -join ', ')"

# ---------------------------------------------------------------------------
Write-Step "Compiling installer"

if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

& $iscc "/DAppVersion=$Version" "/Qp" $issPath
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure "Inno Setup returned $LASTEXITCODE."
}

$installer = Join-Path $outputDir "RevitSpatialAnalyzer-$Version-Revit$revitVersion.exe"
if (-not (Test-Path $installer)) {
    Stop-WithFailure "Inno Setup reported success but produced no file at $installer"
}

$size = [math]::Round((Get-Item $installer).Length / 1MB, 2)

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "INSTALLER OK" -ForegroundColor Green
Write-Host ""
Write-Host "  file    : $installer"
Write-Host "  size    : $size MB"
Write-Host "  version : $Version"
Write-Host ""
Write-Host "  Installs per-user and asks for no administrator rights." -ForegroundColor Yellow
Write-Host "  Revit $revitVersion must be restarted after installing." -ForegroundColor Yellow
exit 0
