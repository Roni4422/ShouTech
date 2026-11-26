# ShouTech ERP

The Windows desktop ERP application has one startup project at the repository root.

## Main entry point

- Project: `ShouTech.ERP.csproj`
- WPF application definition: `App.xaml`
- Application bootstrap and lifecycle: `App.xaml.cs`
- Solution: `ShouTech.sln`

## Build and run

Run these commands from the repository root on Windows with the .NET 8 SDK:

```powershell
dotnet restore ShouTech.sln
dotnet build ShouTech.sln --configuration Release --no-restore
dotnet run --project ShouTech.ERP.csproj
dotnet test ShouTech.ERP.Com/tests/Kern.Tests/Kern.Tests.csproj --configuration Release
```

The Windows background worker, SQL migration CLI, libraries, and tests remain separate supporting projects. They are not alternate startup points for the desktop ERP.

Developer and user documentation remains under `ShouTech.ERP.Com/docs/`.
