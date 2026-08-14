# Revit Spatial Analyzer

An Autodesk Revit 2025 add-in that performs granular spatial room analysis on a
floor plan. It discovers spatial regions from the model's own boundary elements,
resolves the room containing a selected element — or both rooms adjacent to a
selected door — visualizes the result in the active view, and exports the
findings as JSON.

## Status

Early development. The foundation phase is complete: the solution builds, the
test harness runs, and the Revit 2025 API is referenced correctly.

**The add-in does not load into Revit yet.** There is no `.addin` manifest and no
external command; those arrive in the next phase. Nothing in this repository
should be read as evidence of Revit runtime behaviour.

## Requirements

| | |
|---|---|
| Autodesk Revit | 2025 (tested against 25.0.2) |
| .NET SDK | 8.0.4xx — pinned by `global.json` |
| OS | Windows |

A .NET *runtime* is not sufficient. Revit 2025 is itself .NET 8 hosted, so a
machine can run Revit perfectly well and still be unable to compile anything for
it. If `dotnet --list-sdks` shows no `8.0.x` entry:

```powershell
winget install --id Microsoft.DotNet.SDK.8 -e
```

Revit 2025 must be installed, because the build references `RevitAPI.dll` and
`RevitAPIUI.dll` from it directly. Those assemblies are **not** redistributed
with this project and are not committed to the repository.

## Building

```powershell
.\tools\build.ps1
```

Options:

```powershell
.\tools\build.ps1 -Configuration Release
.\tools\build.ps1 -NoRestore
```

If Revit 2025 is installed somewhere other than
`C:\Program Files\Autodesk\Revit 2025`, point the build at it:

```powershell
.\tools\build.ps1 -RevitApiDir "D:\Autodesk\Revit 2025"
```

The same switch works on `verify.ps1`. The default lives in
`Directory.Build.props` as the `RevitApiDir` property, so no machine-specific
path is committed.

## Verifying

```powershell
.\tools\verify.ps1
```

Builds, runs the tests, and enforces the rules that are cheap to state and
expensive to discover late:

- `SpatialAnalyzer.Core` resolves no Revit assemblies, and targets `net8.0`
  rather than `net8.0-windows` so it *cannot*
- the Revit project resolves exactly `RevitAPI.dll` and `RevitAPIUI.dll`, both
  with Copy Local = false
- no Revit API assembly reaches build output or `deps.json`
- no build output, Revit model, or key file is tracked by git
- no whitespace errors and no UTF-8 BOMs in C# sources

Exits non-zero if any check fails, so it works as a CI gate as well as a
pre-commit habit.

## Repository layout

```
SpatialAnalyzer.sln
Directory.Build.props          shared build settings, RevitApiDir
global.json                    pins the .NET 8 SDK band

src/
  SpatialAnalyzer.Core/        plain .NET domain and spatial algorithms
  SpatialAnalyzer.Revit/       Autodesk adapter layer

tests/
  SpatialAnalyzer.Core.Tests/  unit tests, no Revit required

tools/
  build.ps1                    build entry point
  verify.ps1                   build + tests + repository checks
```

Directories are created when the functionality needs them rather than up front.

### Why Core is a separate project

`SpatialAnalyzer.Core` targets plain `net8.0`, not `net8.0-windows`. The Revit
API is a Windows-framework assembly, so this project is structurally incapable
of referencing it — the layering rule is enforced by the target framework rather
than by convention.

That matters for testing. Spatial rules, above all the requirement that a real
physical gap between two spaces is never programmatically closed, must be
provable in seconds on any machine, not by opening a model and looking at it.
Revit data is copied into plain owned types by `SpatialAnalyzer.Revit` before it
reaches Core.

## Development model

Work happens on `phase/*` branches and merges to `main` with `--no-ff` once the
phase passes its acceptance. Individual checkpoint commits are preserved rather
than squashed: each one is a known-good restoration point.

Anything whose correctness depends on Revit is verified by running it in Revit
before being committed. A successful compile is evidence of a successful
compile, and nothing more.
