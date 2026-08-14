<#
.SYNOPSIS
    Verifies the Revit Spatial Analyzer repository, build, tests and packaging
    hygiene.

.DESCRIPTION
    One command that answers "is this repository in a state worth committing?".

    The rules enforced here are the ones that are cheap to state and expensive
    to discover late: that Core cannot see the Revit API, that Revit's own
    assemblies never reach our build output, and that nothing prohibited has
    been committed.

    A note on how the checks are written. Text-searching source files for
    strings like "RevitAPI" produces false positives the moment a file is named
    RevitApiReference.cs, or a comment explains which references are forbidden;
    that happened twice while this project was being set up. So the checks below
    ask MSBuild for resolved reference paths, ask git for its tracked file list,
    and look at real files on disk. None of them grep source text.

    Every check reports PASS or FAIL individually and the script exits non-zero
    if any failed, so it is usable both by a human and as a CI gate.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER RevitApiDir
    Overrides the Revit 2025 installation directory. See tools/build.ps1.

.PARAMETER SkipBuild
    Runs only the repository, layering and output checks, without rebuilding or
    testing.

.EXAMPLE
    .\tools\verify.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [string] $RevitApiDir,

    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$results = New-Object System.Collections.ArrayList

<#
    Runs an external command and returns its exit code and stdout.

    This exists because of a specific Windows PowerShell 5.1 behaviour. When a
    native command's stderr is redirected, PowerShell wraps each stderr line in
    an ErrorRecord; combined with $ErrorActionPreference = 'Stop' that becomes a
    terminating error. git writes routine notices to stderr - the "LF will be
    replaced by CRLF" normalisation warning among them - so a plain redirection
    kills this script on a harmless message. Dropping the preference to
    'Continue' for the duration of the call is what makes external tooling safe
    to invoke here.
#>
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [string[]] $Arguments = @()
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $stdout = & $FilePath @Arguments 2>$null
        return [PSCustomObject]@{
            ExitCode = $LASTEXITCODE
            Output   = ($stdout | Out-String)
            Lines    = @($stdout)
        }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-Git {
    param([string[]] $Arguments)
    return Invoke-Native -FilePath 'git' -Arguments (@('-C', $repoRoot) + $Arguments)
}

function Add-Result {
    param(
        [string] $Name,
        [bool]   $Passed,
        [string] $Detail = ''
    )
    [void] $results.Add([PSCustomObject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    if ($Passed) {
        Write-Host ("  [PASS] " + $Name) -ForegroundColor Green
    }
    else {
        Write-Host ("  [FAIL] " + $Name) -ForegroundColor Red
        if ($Detail) {
            foreach ($line in ($Detail -split "`n")) {
                if ($line.Trim()) { Write-Host ("         " + $line.Trim()) -ForegroundColor Red }
            }
        }
    }
}

function Write-Section {
    param([string] $Title)
    Write-Host ""
    Write-Host ("== " + $Title) -ForegroundColor Cyan
}

<#
    Asks MSBuild for the reference paths a project actually resolves.

    This runs the ResolveAssemblyReferences target on purpose. Plain evaluation
    leaves ReferencePath empty, which would make every reference check below
    silently vacuous - passing because it found nothing rather than because
    nothing is wrong.
#>
function Get-ResolvedReferencePath {
    param([string] $ProjectPath)

    $msbuildArgs = @($ProjectPath, '-t:ResolveAssemblyReferences', '-getItem:ReferencePath', '-nologo')
    if ($RevitApiDir) { $msbuildArgs += "-p:RevitApiDir=$RevitApiDir" }

    $result = Invoke-Native -FilePath 'dotnet' -Arguments (@('msbuild') + $msbuildArgs)
    if ($result.ExitCode -ne 0) { return $null }

    try {
        $parsed = $result.Output | ConvertFrom-Json
    }
    catch {
        return $null
    }
    if (-not $parsed.Items) { return @() }
    return @($parsed.Items.ReferencePath)
}

Write-Host "Revit Spatial Analyzer - verify" -ForegroundColor White
Write-Host "  repository    : $repoRoot"
Write-Host "  configuration : $Configuration"

# ---------------------------------------------------------------------------
Write-Section "Repository"
# ---------------------------------------------------------------------------

$isRepo = (Invoke-Git @('rev-parse', '--git-dir')).ExitCode -eq 0
Add-Result "Is a git repository" $isRepo

if ($isRepo) {
    # What matters is what git TRACKS, not what happens to sit in the folder.
    # Build output and sample models are expected on disk; they must never be
    # committed.
    $tracked = @((Invoke-Git @('ls-files')).Lines | Where-Object { $_ })

    $prohibited = @($tracked | Where-Object {
        $_ -match '(^|/)(bin|obj)/'            -or `
        $_ -match '(^|/)\.vs/'                 -or `
        $_ -match '(^|/)\.claude/'             -or `
        $_ -match '(^|/)CLAUDE(\.local)?\.md$' -or `
        $_ -match '\.(rvt|rfa|rte|rft)$'       -or `
        $_ -cmatch 'RevitAPI[^/]*\.dll$'       -or `
        $_ -match '\.(pfx|snk)$'
    })
    Add-Result "No prohibited files tracked by git" ($prohibited.Count -eq 0) ($prohibited -join "`n")

    # The mirror of the check above, and the one that was missing.
    #
    # Guarding only against unwanted files being present says nothing about
    # wanted files being absent. An over-broad ignore rule excludes real source
    # silently: the build keeps working because the files are on disk, and the
    # loss only appears when someone clones. That happened here - a rule reading
    # "diagnostics/" matched src/SpatialAnalyzer.Core/Diagnostics, because git
    # is configured case-insensitively on Windows.
    $sourceOnDisk = @(Get-ChildItem (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests') -Recurse -File -Filter '*.cs' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object { $_.FullName.Replace($repoRoot + '\', '').Replace('\', '/') })

    # The test is whether git IGNORES a source file, not whether it is tracked.
    # A file that has simply not been added yet is ordinary work in progress; a
    # file git refuses to see is the defect. Conflating the two would make this
    # check fail throughout normal development, and a check that cries wolf
    # gets ignored precisely when it matters.
    if ($sourceOnDisk.Count -gt 0) {
        $ignoredSource = @((Invoke-Git (@('check-ignore') + $sourceOnDisk)).Lines | Where-Object { $_ })
        Add-Result "No source file is excluded by ignore rules" ($ignoredSource.Count -eq 0) `
            (($ignoredSource | ForEach-Object { "$_  (run: git check-ignore -v `"$_`")" }) -join "`n")
    }

    # Whitespace errors against HEAD, covering staged and unstaged alike.
    Add-Result "No whitespace errors (git diff --check)" ((Invoke-Git @('diff', 'HEAD', '--check')).ExitCode -eq 0)

    $status = @((Invoke-Git @('status', '--porcelain')).Lines | Where-Object { $_ })
    if ($status.Count -gt 0) {
        Write-Host ("  [info] working tree has " + $status.Count + " uncommitted change(s)") -ForegroundColor Yellow
    }
}

# UTF-8 BOMs in C# sources create diff noise and creep in whenever a tool writes
# a file with the wrong encoding. .sln is excluded on purpose: the .NET SDK and
# Visual Studio both write solution files with a BOM by design.
$csFiles = @(Get-ChildItem $repoRoot -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' })
$withBom = @($csFiles | Where-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
} | ForEach-Object { $_.FullName.Replace($repoRoot + '\', '') })
Add-Result "No UTF-8 BOM in C# sources" ($withBom.Count -eq 0) ($withBom -join "`n")

# ---------------------------------------------------------------------------
Write-Section "Toolchain"
# ---------------------------------------------------------------------------

$hasDotnet = [bool] (Get-Command dotnet -ErrorAction SilentlyContinue)
Add-Result "dotnet is available" $hasDotnet

if ($hasDotnet) {
    $sdkResult = Invoke-Native -FilePath 'dotnet' -Arguments @('--list-sdks')
    $has8 = [bool] ($sdkResult.Lines | Where-Object { $_ -match '^8\.0\.' })
    Add-Result ".NET 8 SDK installed" $has8 ("installed: " + (($sdkResult.Lines) -join '; '))
}

# ---------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Section "Build and tests"

    $buildScript = Join-Path $PSScriptRoot 'build.ps1'
    $buildArgs = @($buildScript, '-Configuration', $Configuration)
    if ($RevitApiDir) { $buildArgs += @('-RevitApiDir', $RevitApiDir) }

    $buildResult = Invoke-Native -FilePath 'powershell' -Arguments (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File') + $buildArgs)
    Add-Result "Solution builds" ($buildResult.ExitCode -eq 0) "run tools\build.ps1 directly to see the compiler output"

    # TreatWarningsAsErrors covers the compiler, but not MSBuild's own warnings
    # - MSB3277 reference conflicts being the one this project actually hit.
    # Asserting a clean build keeps that fixed at the source rather than
    # someone quietly reintroducing a suppression.
    $warningLine = @($buildResult.Lines | Where-Object { $_ -match '\d+\s+Warning\(s\)' }) | Select-Object -Last 1
    if ($null -eq $warningLine) {
        Add-Result "Build produces no warnings" $false "could not read a warning count from the build output"
    }
    else {
        $warningCount = [int] ([regex]::Match($warningLine, '(\d+)\s+Warning').Groups[1].Value)
        Add-Result "Build produces no warnings" ($warningCount -eq 0) "build reported $warningCount warning(s)"
    }

    $solution = Join-Path $repoRoot 'SpatialAnalyzer.sln'
    $testArgs = @('test', $solution, '-c', $Configuration, '--nologo', '--no-build')
    if ($RevitApiDir) { $testArgs += "-p:RevitApiDir=$RevitApiDir" }

    $testResult = Invoke-Native -FilePath 'dotnet' -Arguments $testArgs
    $summary = (@($testResult.Lines | Where-Object { $_ -match 'Passed!|Failed!|error' }) | Select-Object -First 3) -join "`n"
    Add-Result "Core tests pass" ($testResult.ExitCode -eq 0) $summary
}

# ---------------------------------------------------------------------------
Write-Section "Layering"
# ---------------------------------------------------------------------------

$coreProject  = Join-Path $repoRoot 'src\SpatialAnalyzer.Core\SpatialAnalyzer.Core.csproj'
$revitProject = Join-Path $repoRoot 'src\SpatialAnalyzer.Revit\SpatialAnalyzer.Revit.csproj'

# Core targets net8.0 rather than net8.0-windows so it is structurally unable to
# reference the Revit API. If that ever changes the guarantee is gone, even
# before anyone adds an actual Revit reference.
$coreTfm = (Invoke-Native -FilePath 'dotnet' -Arguments @('msbuild', $coreProject, '-getProperty:TargetFramework', '-nologo')).Output.Trim()
Add-Result "Core targets net8.0 (not -windows)" ($coreTfm -eq 'net8.0') "actual: $coreTfm"

$coreRefs = Get-ResolvedReferencePath $coreProject
if ($null -eq $coreRefs) {
    Add-Result "Core resolves no Revit assemblies" $false "could not resolve Core references"
}
else {
    $coreRevitRefs = @($coreRefs | Where-Object { $_.Identity -match '\\Autodesk\\' -or $_.Identity -cmatch 'RevitAPI' })
    Add-Result "Core resolves no Revit assemblies" ($coreRevitRefs.Count -eq 0) ((@($coreRevitRefs | ForEach-Object { $_.Identity })) -join "`n")
}

# Positive assertion about the Revit project's references. This deliberately
# replaces the MSB3277 warning suppressed in SpatialAnalyzer.Revit.csproj:
# rather than trusting that MSBuild resolved the version conflict correctly,
# state what the answer has to be. Exactly two assemblies may come out of the
# Revit installation directory, and neither may be copied locally.
$revitRefs = Get-ResolvedReferencePath $revitProject
if ($null -eq $revitRefs) {
    Add-Result "Revit project resolves exactly RevitAPI and RevitAPIUI" $false "could not resolve Revit project references"
}
else {
    $fromRevitDir = @($revitRefs | Where-Object { $_.Identity -match '\\Autodesk\\Revit ' })
    $names = @($fromRevitDir | ForEach-Object { Split-Path $_.Identity -Leaf } | Sort-Object)

    $expected = ($names.Count -eq 2 -and $names[0] -eq 'RevitAPI.dll' -and $names[1] -eq 'RevitAPIUI.dll')
    Add-Result "Revit project resolves exactly RevitAPI and RevitAPIUI" $expected ("resolved: " + ($names -join ', '))

    $copiedLocal = @($fromRevitDir | Where-Object { $_.Private -ne 'false' })
    Add-Result "Revit assemblies are marked Copy Local = false" ($copiedLocal.Count -eq 0) `
        ((@($copiedLocal | ForEach-Object { (Split-Path $_.Identity -Leaf) + " Private=" + $_.Private })) -join "`n")
}

# ---------------------------------------------------------------------------
Write-Section "Deployment hygiene"
# ---------------------------------------------------------------------------

# The distributable must never carry Autodesk's own assemblies. Revit loads them
# from its installation; shipping copies risks binding an add-in against
# assemblies that disagree with the host, and bloats the installer.
$binRoots = @(Get-ChildItem $repoRoot -Recurse -Directory -Filter 'bin' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\\.git\\' })

$leaked = @()
foreach ($bin in $binRoots) {
    $leaked += @(Get-ChildItem $bin.FullName -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(RevitAPI.*|AdWindows|UIFramework.*)\.dll$' } |
        ForEach-Object { $_.FullName.Replace($repoRoot + '\', '') })
}
Add-Result "No Revit API assemblies in build output" ($leaked.Count -eq 0) ($leaked -join "`n")

# deps.json drives runtime assembly resolution. A Revit entry here would mean
# the add-in expects to load Autodesk assemblies as its own dependencies.
$depsFiles = @(Get-ChildItem $repoRoot -Recurse -File -Filter '*.deps.json' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\\.git\\' })
$badDeps = @()
foreach ($deps in $depsFiles) {
    if ((Get-Content $deps.FullName -Raw) -cmatch 'RevitAPI') {
        $badDeps += $deps.FullName.Replace($repoRoot + '\', '')
    }
}
Add-Result "No Revit API entries in deps.json" ($badDeps.Count -eq 0) ($badDeps -join "`n")

# ---------------------------------------------------------------------------
Write-Host ""
Write-Host ("-" * 60)

$failed = @($results | Where-Object { -not $_.Passed })
$passedCount = ($results.Count - $failed.Count)

if ($failed.Count -eq 0) {
    Write-Host ("VERIFY OK - " + $passedCount + "/" + $results.Count + " checks passed") -ForegroundColor Green
    exit 0
}

Write-Host ("VERIFY FAILED - " + $failed.Count + " of " + $results.Count + " checks failed:") -ForegroundColor Red
foreach ($f in $failed) {
    Write-Host ("  - " + $f.Name) -ForegroundColor Red
}
exit 1
