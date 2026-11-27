# Navigation Database (Enterprise)

Commercial-grade catalog for **MainWindow menu bar** and **Ribbon** commands.

## Schema (`dba/sys/nav.sql`)

| Table | Purpose |
|-------|---------|
| `sys.MainMenuItems` | 17 top-level menu entries |
| `sys.RibbonTabs` | Ribbon tabs (linked to menu) |
| `sys.RibbonGroups` | Ribbon groups inside each tab |
| `sys.RibbonItems` | Executable ribbon commands |

### Enterprise features

- Multi-tenant: `TenantID`, `CompanyID`
- RBAC: `PermissionID` → `sec.Permissions`
- Soft delete + audit columns + `RowVersion`
- Filtered indexes for active rows
- Stored procedures:
  - `sys.sp_GetMainMenuItems`
  - `sys.sp_GetUserRibbonLayout`
- View: `sys.vw_NavigationCatalog`

## Seed data (`dba/sds/sds_nav.sql`)

Auto-generated from `MainWindow.xaml` + `Resources/Lang/*.json`:

```powershell
powershell -File tools/generate_nav_seed.ps1
```

Current catalog: **17 menus · 17 tabs · 55 groups · 215 items · 209 permissions**

## Deployment

**Full install (TMPL):** runs `nav.sql` + `sds_nav.sql` automatically.

**Incremental migration:**

```bash
sqlcmd -i dba/mig/003_nav_ribbon.sql
```

## Application layer

- Domain: `src/ShouTech.Domain/Entities/Navigation/`
- Repository interface: `INavigationRepository`
- Dapper implementation: `cod/cor/navrep.cs`

```csharp
var repo = new NavigationRepository(connection);
var menu = await repo.GetMainMenuItemsAsync(userId);
var layout = await repo.GetRibbonLayoutAsync(userId, tabCode: "TabInventory");
var tabs = NavigationTreeBuilder.BuildTabs(layout);
```

## Regeneration workflow

When adding ribbon buttons in XAML:

1. Update `Resources/Lang/Ar.json` + `En.json`
2. Run `tools/generate_nav_seed.ps1`
3. Deploy `dba/mig/003_nav_ribbon.sql` or full TMPL seed step
