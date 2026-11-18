# WPF Window and View Inventory

**Branch:** `work/full-app-stabilization-2026-11-18`  
**Inventory snapshot:** 2026-11-18

This is a source-file inventory, not a claim that every type is an independently navigable window. XAML code-behind files are detected using the `.xaml.cs` suffix.

## Inventory summary

- Unique view/source basenames: 159
- XAML + code-behind pairs: 23
- C#-only basenames: 136
- XAML without same-basename code-behind: 0
- Report basenames: 63
- Existing Modules basenames: 4

A C#-only file may be a programmatic WPF window, helper, base class, view model, or support type. Inspect declared types and callers before treating it as a window or moving it.

## Main views and non-report views

| Basename | C# / code-behind | XAML |
|---|---:|---:|
| `AccCard` | Yes | Yes |
| `ActivityLogWin` | Yes | No |
| `AddonsWin` | Yes | No |
| `AppState` | Yes | No |
| `AssetWin` | Yes | No |
| `BankRecWin` | Yes | No |
| `BarWin` | Yes | No |
| `BaseWin` | Yes | No |
| `BatchSerialPickerWin` | Yes | No |
| `BcdDesWin` | Yes | No |
| `BcdGenWin` | Yes | No |
| `BinWin` | Yes | Yes |
| `BkpWin` | Yes | Yes |
| `BnkCard` | Yes | No |
| `BomWin` | Yes | No |
| `BudgetWin` | Yes | No |
| `CalculatorWin` | Yes | No |
| `CashDrawer` | Yes | No |
| `CatWin` | Yes | Yes |
| `CcCard` | Yes | No |
| `CcListWin` | Yes | No |
| `ChangePasswordWin` | Yes | No |
| `ChqListWin` | Yes | No |
| `ChqWin` | Yes | No |
| `CloseCashWin` | Yes | No |
| `CoaWin` | Yes | Yes |
| `ColChsWin` | Yes | Yes |
| `ComWin` | Yes | Yes |
| `CompressInvoicesWin` | Yes | No |
| `CrmActivitiesWin` | Yes | No |
| `CrmLeadWin` | Yes | No |
| `CrmOppWin` | Yes | No |
| `CrmPipelineWin` | Yes | No |
| `CshWin` | Yes | No |
| `CstCard` | Yes | No |
| `CurCard` | Yes | No |
| `CustomReportsWin` | Yes | No |
| `DatWin` | Yes | Yes |
| `DefManagerWin` | Yes | No |
| `DeliveryDeskWin` | Yes | No |
| `DocSearchWin` | Yes | No |
| `F2WinUi` | Yes | No |
| `FinancialAnalysisWin` | Yes | No |
| `GroupManagerWin` | Yes | No |
| `HelpWin` | Yes | No |
| `HrAttendanceWin` | Yes | No |
| `HrEmployeeWin` | Yes | No |
| `HrLeaveWin` | Yes | No |
| `HrOrgWins` | Yes | No |
| `HrPayrollWin` | Yes | No |
| `HrStructureWin` | Yes | No |
| `InstallmentsWin` | Yes | No |
| `InvoiceWin` | Yes | No |
| `JrnWin` | Yes | No |
| `Lang` | Yes | Yes |
| `LicenseWin` | Yes | No |
| `LogWin` | Yes | Yes |
| `LoyaltyWin` | Yes | No |
| `MWin` | Yes | Yes |
| `MaintenanceWin` | Yes | No |
| `MatCard` | Yes | Yes |
| `MatDirWin` | Yes | Yes |
| `MatGroupWin` | Yes | Yes |
| `MfgListWin` | Yes | No |
| `MfgPlanWin` | Yes | No |
| `MfgWin` | Yes | No |
| `OrdWin` | Yes | No |
| `OrderTrackWin` | Yes | No |
| `PartyManagerWin` | Yes | No |
| `PartySearchWin` | Yes | No |
| `PayRecWin` | Yes | No |
| `PosCheckoutWin` | Yes | No |
| `PosReturnWin` | Yes | No |
| `PosSessionWin` | Yes | No |
| `PosSyncWin` | Yes | No |
| `PosWin` | Yes | No |
| `PrmWin` | Yes | Yes |
| `ProjWin` | Yes | No |
| `QcAlertWin` | Yes | No |
| `QcInspectWin` | Yes | No |
| `QcTemplateWin` | Yes | No |
| `RepCard` | Yes | No |
| `RepostWin` | Yes | No |
| `RestoreCoWin` | Yes | Yes |
| `RolloverWin` | Yes | No |
| `RptBase` | Yes | No |
| `SerWin` | Yes | Yes |
| `SplWin` | Yes | Yes |
| `StpWiz` | Yes | Yes |
| `SupportWin` | Yes | No |
| `UpdatesWin` | Yes | No |
| `WhsWin` | Yes | Yes |

## Report views

| Basename | C# / code-behind | XAML |
|---|---:|---:|
| `Reports/AccBalWin` | Yes | No |
| `Reports/AccCompareWin` | Yes | No |
| `Reports/AccMoveChartWin` | Yes | No |
| `Reports/AccStmtWin` | Yes | No |
| `Reports/AvgRateWin` | Yes | No |
| `Reports/BalSheetWin` | Yes | No |
| `Reports/BankBalancesWin` | Yes | No |
| `Reports/BankEstimateWin` | Yes | No |
| `Reports/BatchBalWin` | Yes | No |
| `Reports/BranchAccMoveWin` | Yes | No |
| `Reports/BranchMatWin` | Yes | No |
| `Reports/BudgetVarWin` | Yes | No |
| `Reports/CashFlowWin` | Yes | No |
| `Reports/CashJournalWin` | Yes | No |
| `Reports/ClosingAccountsWin` | Yes | No |
| `Reports/CollectionRatesWin` | Yes | No |
| `Reports/CostCenterRptWin` | Yes | No |
| `Reports/CostCenterTrialWin` | Yes | No |
| `Reports/CustAgingWin` | Yes | No |
| `Reports/CustBalWin` | Yes | No |
| `Reports/CustomerDueWin` | Yes | No |
| `Reports/DormantWin` | Yes | No |
| `Reports/EndOfPeriodWin` | Yes | No |
| `Reports/ExchangeDiffWin` | Yes | No |
| `Reports/ExpiredWin` | Yes | No |
| `Reports/HrReports` | Yes | No |
| `Reports/IncStmtWin` | Yes | No |
| `Reports/InvCustomersWin` | Yes | No |
| `Reports/InvoiceAgeWin` | Yes | No |
| `Reports/InvoiceProfitsWin` | Yes | No |
| `Reports/ItemProfitWin` | Yes | No |
| `Reports/JrnListWin` | Yes | No |
| `Reports/MatCompareWin` | Yes | No |
| `Reports/MatListWin` | Yes | No |
| `Reports/MatMoveChartWin` | Yes | No |
| `Reports/MatMoveWin` | Yes | No |
| `Reports/MatStatsWin` | Yes | No |
| `Reports/MfgCostsWin` | Yes | No |
| `Reports/MfgFeasibilityWin` | Yes | No |
| `Reports/MfgVarianceWin` | Yes | No |
| `Reports/MostActiveWin` | Yes | No |
| `Reports/NegativeStockWin` | Yes | No |
| `Reports/OrderPaymentsWin` | Yes | No |
| `Reports/PackingListWin` | Yes | No |
| `Reports/PosCashRptWin` | Yes | No |
| `Reports/PosZReportWin` | Yes | No |
| `Reports/PriceHistoryWin` | Yes | No |
| `Reports/PriceListWin` | Yes | No |
| `Reports/PurListWin` | Yes | No |
| `Reports/QcSummaryWin` | Yes | No |
| `Reports/RepCollectionsWin` | Yes | No |
| `Reports/RepMovementWin` | Yes | No |
| `Reports/RptWinUtil` | Yes | No |
| `Reports/SalListWin` | Yes | No |
| `Reports/ServiceMatWin` | Yes | No |
| `Reports/ShortListWin` | Yes | No |
| `Reports/StagnantWin` | Yes | No |
| `Reports/StkAdjRptWin` | Yes | No |
| `Reports/StkBalWin` | Yes | No |
| `Reports/SupAgingWin` | Yes | No |
| `Reports/SupBalWin` | Yes | No |
| `Reports/TargetsRptWin` | Yes | No |
| `Reports/TrialBalanceWin` | Yes | No |

## Existing Modules views

| Basename | C# / code-behind | XAML |
|---|---:|---:|
| `Modules/DashVw` | Yes | Yes |
| `Modules/LangVM` | Yes | No |
| `Modules/ModuleWorkspaceView` | Yes | Yes |
| `Modules/ReportGridVw` | Yes | Yes |

## Mandatory checks before moving a view

1. Inspect declared window/view types, namespaces, constructors, dependencies and public entry points.
2. Find every construction, Show/ShowDialog call, navigation route, Ribbon action key, menu click handler, reflection lookup and DI registration.
3. Map service/repository dependencies and the tables/queries each view reads or writes.
4. Check localization keys, language dictionaries, styles, merged resource dictionaries, icons, templates and dynamic resource references.
5. Check x:Class, StartupUri, ResourceDictionary Source, relative paths, pack URIs, component resource references and runtime file paths.
6. Check MainApp.csproj and build/publish scripts. The WPF project disables SDK default compile/page discovery and uses recursive **\\*.cs and **\\*.xaml includes; resource URIs may change after a move even when compilation succeeds.
7. Move each existing XAML/code-behind pair together. Do not split large code-behind files merely to reduce line count.
8. Preserve public types, namespaces and runtime behavior unless a separately reviewed compatibility change is required.
9. Build Debug and Release after each bounded move; run focused tests and launch/navigation smoke tests before proceeding.
10. Record old path, new path, route, dependencies, migration commit and verification evidence here.

## Proposed destination layout

Keep cohesive source files intact. Organize by business capability, not file size:

- Views/Platform/: main shell, company, users, roles, permissions, settings, backup, restore, licensing and diagnostics.
- Views/Inventory/: item/material cards, categories, warehouses, barcode, transfers, counts, adjustments, batch/serial and inventory operations.
- Views/Sales/: quotations, sales orders, invoices, returns, delivery and customer-facing sales workflows.
- Views/Purchasing/: purchase orders, receipts, supplier invoices and purchase returns.
- Views/Accounting/: chart of accounts, journals, vouchers, receipts, payments, cash, banks and reconciliation.
- Views/Manufacturing/: BOM, production orders, planning, routing and quality.
- Views/HR/: employees, organization, attendance, leave and payroll.
- Views/CRM/: leads, opportunities, pipeline and activities.
- Views/Reports/<module>/: reports grouped by owning module, preserving each report class/file intact.

These are target folders, not permission to move all files in one batch. The first actual move must be a small, fully traced slice with all XAML/resource references checked.

## Migration status

- [x] Source inventory generated from the branch tree.
- [ ] Type/caller/route/dependency mapping completed.
- [ ] First bounded move selected and reviewed.
- [ ] First move built and smoke-tested.
- [ ] Remaining modules migrated in bounded waves.
- [ ] All routes and windows covered by automated or documented manual smoke tests.
- [ ] Final Debug/Release, database, and end-to-end regression matrix passed.


## First bounded migration wave: Material Card

**Status:** Source relocation committed; runtime verification pending.

| Item | Result |
|---|---|
| Previous XAML | `cod/app/src/Views/MatCard.xaml` |
| New XAML | `cod/app/src/Views/Inventory/MatCard.xaml` |
| Previous code-behind | `cod/app/src/Views/MatCard.xaml.cs` |
| New code-behind | `cod/app/src/Views/Inventory/MatCard.xaml.cs` |
| Type and namespace | `ShouTech.App.Views.MatCard` preserved |
| XAML class | `ShouTech.App.Views.MatCard` preserved |
| Main-window route | `MWin.xaml.cs` -> `OpenMaterialCard()` -> `new MatCard` preserved |
| Ribbon entry | Material Card button in `MWin.xaml` preserved |
| Localization | Existing `Inventory.MaterialCard` and `MatCard.*` keys preserved |
| Project inclusion | Recursive Compile and Page globs include the new subdirectory |
| Source splitting | None; both original files moved intact |

**Checks still required:** Windows CI build and test results for the move commit; runtime XAML resource loading; open/close/reopen; create/edit/search/delete behavior; SQLite persistence; PostgreSQL parity if supported by this card workflow. No runtime pass is claimed yet.

## Current migration status

- [x] Source inventory generated.
- [x] First bounded XAML/code-behind pair moved together.
- [ ] First move passes Windows Debug/Release build and automated tests.
- [ ] Material Card runtime smoke test and persistence verification.
- [ ] Remaining windows mapped and moved in bounded waves.
- [ ] All Ribbon/menu routes verified.
- [ ] Final full regression matrix passed.


## Test baseline discovered before the first view move

The Windows CI run for the pre-move baseline commit `38b22a8e8ef28cbe77b3d6bb0b8b011330e9c053` completed with a successful restore and build, but the test step failed: **72 failed, 242 passed, 0 skipped (314 total)**. This run predates the Material Card relocation and therefore is not evidence that the relocation caused these failures.

The failure log shows recurring test-fixture/schema and isolation problems that must be triaged independently of the view moves, including:

- Fixture SQL expects `warehouses.name`, while the migrated SQLite schema does not expose that column.
- Fixture SQL expects `mfg_orders.bom_id`, which is absent in the schema used by the test.
- SQLite test databases intermittently fail to open.
- Several fixtures cannot resolve seeded companies, materials, warehouses, accounts, parties or cost centers.
- Some tests encounter duplicate account numbers during setup.
- COA tests contain SQL syntax failures near alias `a`.
- STEI import round-trip lacks the expected `DATA.IMPORT` permission in its test context.

These are observed failure signatures, not yet proven root causes. Fixes must align fixtures with authoritative migrations and isolate each test's database/session/company state; do not weaken production validation to make tests pass.

The Windows CI run for the Material Card move commit `cc9eec898508f7620246ddc94720c26296f7c059` is running separately. Its final restore/build/test result must be recorded before this first relocation is marked verified.
