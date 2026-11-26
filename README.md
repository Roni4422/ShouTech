# ShouTech ERP

The Windows desktop ERP application has one startup project in `app/` and one solution at the repository root.

## Main entry point

- Project: `app/ShouTech.ERP.csproj`
- WPF application definition: `App.xaml`
- Application bootstrap and lifecycle: `App.xaml.cs`
- Solution: `ShouTech.sln` (the only .NET solution in the repository root)

## Repository map

- [Architecture](docs/ARCHITECTURE.md) — technical layers and project boundaries.
- [Structure audit](docs/STRUCTURE_AUDIT.md) — canonical folder layout and ownership.
- [Complete file/folder index](docs/FILE_INDEX.md) — searchable paths for every repository file and directory.
- [Abbreviation dictionary](docs/FILE_ABBR.md) — shortened file and folder names with their full meanings.
- [Developer guide](DEV.md) — engineering workflow and build guidance.

## Build and run

Run commands from the repository root on Windows with the .NET 8 SDK. The root contains only the solution file, so the standard SDK command resolves it automatically:

```powershell
dotnet restore
dotnet build --configuration Release --no-restore
dotnet run --project app/ShouTech.ERP.csproj
dotnet test tests/Kern.Tests/Kern.Tests.csproj --configuration Release
```

The Windows background worker, SQL migration CLI, libraries, and tests remain separate supporting projects. They are not alternate startup points for the desktop ERP.

Developer and user documentation remains under `docs/`.
