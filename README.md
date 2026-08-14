# Revit Spatial Analyzer

An Autodesk Revit 2025 add-in that performs granular spatial room analysis on a
floor plan: it discovers spatial regions from the model's own boundary elements,
resolves the room containing a selected element (or both rooms adjacent to a
selected door), visualizes the result in the active view, and exports the
findings as JSON.

## Status

Under active development. See the phase branches for work in progress.

## Requirements

- Autodesk Revit 2025
- .NET 8 SDK
- Windows

## Building

```powershell
dotnet build
```

Full build and deployment instructions are documented as the project's build
scripts are established.
