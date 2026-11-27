# ShouTech ERP

The Windows desktop ERP application has one startup project at the repository root. The full developer solution is kept under `build/`.

## Main entry point

- Project: `ShouTech.ERP.csproj`
- WPF application definition: `App.xaml`
- Application bootstrap and lifecycle: `App.xaml.cs`
- Solution: `build/ShouTech.sln` (developer/IDE solution; not required for the two daily commands)

## Repository map

- [Architecture](docs/ARCHITECTURE.md) — technical layers and project boundaries.
- [Structure audit](docs/STRUCTURE_AUDIT.md) — canonical folder layout and ownership.
- [Complete file/folder index](docs/FILE_INDEX.md) — searchable paths for every repository file and directory.
- [Abbreviation dictionary](docs/FILE_ABBR.md) — shortened file and folder names with their full meanings.
- [Developer guide](DEV.md) — engineering workflow and build guidance.

## Build and run

Install the .NET 10 SDK on Windows. The repository CI currently builds with the .NET 10 SDK, while the application and supporting projects still target .NET 8; the target-framework migration is a separate, not-yet-completed step. The root project is the sole default entry point, so both commands work without a project or solution name:

```powershell
dotnet build
dotnet run

# Full solution validation (optional)
dotnet build build/ShouTech.sln
dotnet test tests/Kern.Tests/Kern.Tests.csproj --configuration Release
```

The Windows background worker, SQL migration CLI, libraries, and tests remain separate supporting projects. They are not alternate startup points for the desktop ERP.

Developer and user documentation remains under `docs/`.
