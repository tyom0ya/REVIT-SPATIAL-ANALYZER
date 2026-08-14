<#
.SYNOPSIS
    Deploys the add-in to the current user's Revit 2025 installation for
    development.

.DESCRIPTION
    Verifies, builds, copies our own assemblies to a per-user staging directory,
    and writes the .addin manifest into the per-user Revit 2025 Addins folder.

    Two properties this script is careful about:

      * It never needs administrator rights. Everything it writes lives under
        the current user's LOCALAPPDATA and APPDATA.
      * It never copies Autodesk's assemblies. Revit loads RevitAPI.dll and
        RevitAPIUI.dll from its own installation; shipping copies risks binding
        the add-in against assemblies that disagree with the host.

    Revit reads manifests once at startup, so Revit must be restarted for a
    newly deployed or changed add-in to take effect.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER RevitApiDir
    Overrides the Revit 2025 installation directory used for building.

.PARAMETER SkipVerify
    Skips tools/verify.ps1 and builds directly. Faster, but deploys code that
    has not been checked.

.EXAMPLE
    .\tools\deploy-dev.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [string] $RevitApiDir,

    [switch] $SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

$revitVersion  = '2025'
$stagingDir    = Join-Path $env:LOCALAPPDATA "RevitSpatialAnalyzer\dev\$revitVersion"
$addinsDir     = Join-Path $env:APPDATA     "Autodesk\Revit\Addins\$revitVersion"
$manifestName  = 'SpatialAnalyzer.addin'
$templatePath  = Join-Path $repoRoot 'manifests\SpatialAnalyzer.addin.template'
$buildOutput   = Join-Path $repoRoot "src\SpatialAnalyzer.Revit\bin\$Configuration\net8.0-windows"

function Stop-WithFailure {
    param([string] $Message)
    Write-Host ""
    Write-Host "DEPLOY FAILED: $Message" -ForegroundColor Red
    exit 1
}

function Write-Step {
    param([string] $Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

Write-Host "Revit Spatial Analyzer - deploy (development)" -ForegroundColor White
Write-Host "  configuration : $Configuration"
Write-Host "  Revit version : $revitVersion"

# Revit holds a lock on loaded add-in assemblies. Copying over them while Revit
# is running fails with a confusing file-in-use error, so say so plainly first.
$revitProcesses = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if ($revitProcesses.Count -gt 0) {
    Stop-WithFailure "Revit is running (PID $($revitProcesses[0].Id)). Close Revit before deploying: it locks the add-in assemblies, and it only reads manifests at startup."
}

# ---------------------------------------------------------------------------
if ($SkipVerify) {
    Write-Step "Building (verification skipped)"
    $script = Join-Path $PSScriptRoot 'build.ps1'
}
else {
    Write-Step "Verifying and building"
    $script = Join-Path $PSScriptRoot 'verify.ps1'
}

$scriptArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Configuration', $Configuration)
if ($RevitApiDir) { $scriptArgs += @('-RevitApiDir', $RevitApiDir) }

& powershell @scriptArgs
if ($LASTEXITCODE -ne 0) {
    Stop-WithFailure "$(Split-Path $script -Leaf) failed. Nothing was deployed."
}

# ---------------------------------------------------------------------------
Write-Step "Staging assemblies"

if (-not (Test-Path $buildOutput)) {
    Stop-WithFailure "Build output not found at $buildOutput"
}

# Deploy only what we own. Enumerating our assemblies explicitly, rather than
# copying the output folder wholesale, is what guarantees an Autodesk assembly
# can never be dragged along by accident.
$ourAssemblies = @('SpatialAnalyzer.Revit.dll', 'SpatialAnalyzer.Core.dll')
$optionalFiles = @('SpatialAnalyzer.Revit.pdb', 'SpatialAnalyzer.Core.pdb', 'SpatialAnalyzer.Revit.deps.json')

foreach ($name in $ourAssemblies) {
    if (-not (Test-Path (Join-Path $buildOutput $name))) {
        Stop-WithFailure "Expected assembly missing from build output: $name"
    }
}

if (Test-Path $stagingDir) {
    Remove-Item (Join-Path $stagingDir '*') -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
}

foreach ($name in ($ourAssemblies + $optionalFiles)) {
    $source = Join-Path $buildOutput $name
    if (Test-Path $source) {
        Copy-Item $source -Destination $stagingDir -Force
    }
}

# Belt and braces: prove no Autodesk assembly reached the staging directory.
$leaked = @(Get-ChildItem $stagingDir -Recurse -File |
    Where-Object { $_.Name -match '^(RevitAPI.*|AdWindows|UIFramework.*)\.dll$' })
if ($leaked.Count -gt 0) {
    Stop-WithFailure "Revit API assemblies reached the staging directory: $(($leaked | ForEach-Object { $_.Name }) -join ', ')"
}

$deployedDll = Join-Path $stagingDir 'SpatialAnalyzer.Revit.dll'

# ---------------------------------------------------------------------------
Write-Step "Installing manifest"

if (-not (Test-Path $templatePath)) {
    Stop-WithFailure "Manifest template not found at $templatePath"
}

if (-not (Test-Path $addinsDir)) {
    New-Item -ItemType Directory -Path $addinsDir -Force | Out-Null
}

$template = Get-Content $templatePath -Raw

# Strip the template's XML comments before substituting.
#
# Order matters here. The comments explain the template to developers and have
# no place in a deployed artifact; worse, they mention {ASSEMBLY_PATH} by name,
# so substituting first would rewrite the prose into a sentence claiming the
# deployed file "is not a usable manifest". Removing comments first keeps the
# deployed manifest to exactly what Revit needs to read.
$manifest = [regex]::Replace($template, '(?s)<!--.*?-->', '')
$manifest = [regex]::Replace($manifest, '(?m)^\s*$\r?\n', '')
$manifest = $manifest.Replace('{ASSEMBLY_PATH}', $deployedDll)

if ($manifest -match '\{ASSEMBLY_PATH\}') {
    Stop-WithFailure "Manifest template substitution failed; placeholder still present."
}
if ($manifest -match '<!--') {
    Stop-WithFailure "Comment stripping failed; deployed manifest would carry template documentation."
}

$manifestPath = Join-Path $addinsDir $manifestName
# UTF-8 without BOM: the manifest declares encoding="utf-8" and Revit's parser
# is happier without a byte order mark in front of the declaration.
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

# Confirm what was written is well-formed rather than assuming the substitution
# produced valid XML.
try {
    [xml] $parsed = Get-Content $manifestPath -Raw
}
catch {
    Stop-WithFailure "Deployed manifest is not well-formed XML: $_"
}

$declaredAssembly = $parsed.RevitAddIns.AddIn.Assembly
if (-not (Test-Path $declaredAssembly)) {
    Stop-WithFailure "Manifest points at an assembly that does not exist: $declaredAssembly"
}

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "DEPLOY OK" -ForegroundColor Green
Write-Host ""
Write-Host "  manifest    : $manifestPath"
Write-Host "  assembly    : $declaredAssembly"
Write-Host "  class       : $($parsed.RevitAddIns.AddIn.FullClassName)"
Write-Host "  add-in id   : $($parsed.RevitAddIns.AddIn.AddInId)"
Write-Host ""
Write-Host "  Start Revit $revitVersion and run the command from" -ForegroundColor Yellow
Write-Host "  Add-Ins > External Tools > $($parsed.RevitAddIns.AddIn.Text)" -ForegroundColor Yellow
exit 0
