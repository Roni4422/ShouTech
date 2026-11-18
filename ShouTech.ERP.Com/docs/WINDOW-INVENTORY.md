# WPF Window and View Inventory

**Branch:** `work/full-app-stabilization-2026-11-18`  
**Inventory snapshot:** 2026-11-18

This is a source-file inventory, not a claim that every type is an independently navigable window. XAML code-behind files are detected using the `.xaml.cs` suffix.

## Inventory summary

- Unique view/source basenames: 182
- XAML + code-behind pairs: 0
- C#-only basenames: 159
- XAML without same-basename code-behind: 23
- Report basenames: 63
- Existing Modules basenames: 7

A C#-only file may be a programmatic WPF window, helper, base class, view model, or support type. Inspect declared types and callers before treating it as a window or moving it.

## Main views and non-report views

| Basename | C# / code-behind | XAML |
|---|---:|---:|
| `AccCar` | Yes | No |
| `AccCard` | No | Yes |
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
| `BinWi` | Yes | No |
| `BinWin` | No | Yes |
| `BkpWi` | Yes | No |
| `BkpWin` | No | Yes |
| `BnkCard` | Yes | No |
| `BomWin` | Yes | No |
| `BudgetWin` | Yes | No |
| `CalculatorWin` | Yes | No |
| `CashDrawer` | Yes | No |
| `CatWi` | Yes | No |
| `CatWin` | No | Yes |
| `CcCard` | Yes | No |
| `CcListWin` | Yes | No |
| `ChangePasswordWin` | Yes | No |
| `ChqListWin` | Yes | No |
| `ChqWin` | Yes | No |
| `CloseCashWin` | Yes | No |
| `CoaWi` | Yes | No |
| `CoaWin` | No | Yes |
| `ColChsWi` | Yes | No |
| `ColChsWin` | No | Yes |
| `ComWi` | Yes | No |
| `ComWin` | No | Yes |
| `CompressInvoicesWin` | Yes | No |
| `CrmActivitiesWin` | Yes | No |
| `CrmLeadWin` | Yes | No |
| `CrmOppWin` | Yes | No |
| `CrmPipelineWin` | Yes | No |
| `CshWin` | Yes | No |
| `CstCard` | Yes | No |
| `CurCard` | Yes | No |
| `CustomReportsWin` | Yes | No |
| `DatWi` | Yes | No |
| `DatWin` | No | Yes |
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
| `Lan` | Yes | No |
| `Lang` | No | Yes |
| `LicenseWin` | Yes | No |
| `LogWi` | Yes | No |
| `LogWin` | No | Yes |
| `LoyaltyWin` | Yes | No |
| `MWi` | Yes | No |
| `MWin` | No | Yes |
| `MaintenanceWin` | Yes | No |
| `MatCar` | Yes | No |
| `MatCard` | No | Yes |
| `MatDirWi` | Yes | No |
| `MatDirWin` | No | Yes |
| `MatGroupWi` | Yes | No |
| `MatGroupWin` | No | Yes |
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
| `PrmWi` | Yes | No |
| `PrmWin` | No | Yes |
| `ProjWin` | Yes | No |
| `QcAlertWin` | Yes | No |
| `QcInspectWin` | Yes | No |
| `QcTemplateWin` | Yes | No |
| `RepCard` | Yes | No |
| `RepostWin` | Yes | No |
| `RestoreCoWi` | Yes | No |
| `RestoreCoWin` | No | Yes |
| `RolloverWin` | Yes | No |
| `RptBase` | Yes | No |
| `SerWi` | Yes | No |
| `SerWin` | No | Yes |
| `SplWi` | Yes | No |
| `SplWin` | No | Yes |
| `StpWi` | Yes | No |
| `StpWiz` | No | Yes |
| `SupportWin` | Yes | No |
| `UpdatesWin` | Yes | No |
| `WhsWi` | Yes | No |
| `WhsWin` | No | Yes |

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
| `Modules/DashV` | Yes | No |
| `Modules/DashVw` | No | Yes |
| `Modules/LangVM` | Yes | No |
| `Modules/ModuleWorkspaceVie` | Yes | No |
| `Modules/ModuleWorkspaceView` | No | Yes |
| `Modules/ReportGridV` | Yes | No |
| `Modules/ReportGridVw` | No | Yes |

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
