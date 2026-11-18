# WPF Window and View Inventory

**Branch:** `work/full-app-stabilization-2026-11-18`  
**Inventory snapshot:** 2026-11-18  
**Purpose:** Establish evidence before moving WPF windows. This is a source-file inventory, not a claim that every type is an independently navigable window.

## Inventory summary

- View type/file basenames: 182
- XAML + code-behind pairs: 0
- C#-only view/source basenames: 159
- XAML files without a same-basename C# code-behind: 23
- Report view basenames: 63
- Files under the existing Modules view folder are retained in place during the first migration wave.

A C#-only file may be a programmatic WPF window, helper, base class, view model, or other support type. It must be classified by inspecting its declared types and callers before relocation.

## Main views and non-report views

| Basename | C# | XAML |
|---|---:|---:|
| `AccCard` | No | Yes |
| `AccCard.xaml` | Yes | No |
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
| `BinWin` | No | Yes |
| `BinWin.xaml` | Yes | No |
| `BkpWin` | No | Yes |
| `BkpWin.xaml` | Yes | No |
| `BnkCard` | Yes | No |
| `BomWin` | Yes | No |
| `BudgetWin` | Yes | No |
| `CalculatorWin` | Yes | No |
| `CashDrawer` | Yes | No |
| `CatWin` | No | Yes |
| `CatWin.xaml` | Yes | No |
| `CcCard` | Yes | No |
| `CcListWin` | Yes | No |
| `ChangePasswordWin` | Yes | No |
| `ChqListWin` | Yes | No |
| `ChqWin` | Yes | No |
| `CloseCashWin` | Yes | No |
| `CoaWin` | No | Yes |
| `CoaWin.xaml` | Yes | No |
| `ColChsWin` | No | Yes |
| `ColChsWin.xaml` | Yes | No |
| `ComWin` | No | Yes |
| `ComWin.xaml` | Yes | No |
| `CompressInvoicesWin` | Yes | No |
| `CrmActivitiesWin` | Yes | No |
| `CrmLeadWin` | Yes | No |
| `CrmOppWin` | Yes | No |
| `CrmPipelineWin` | Yes | No |
| `CshWin` | Yes | No |
| `CstCard` | Yes | No |
| `CurCard` | Yes | No |
| `CustomReportsWin` | Yes | No |
| `DatWin` | No | Yes |
| `DatWin.xaml` | Yes | No |
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
| `Lang` | No | Yes |
| `Lang.xaml` | Yes | No |
| `LicenseWin` | Yes | No |
| `LogWin` | No | Yes |
| `LogWin.xaml` | Yes | No |
| `LoyaltyWin` | Yes | No |
| `MWin` | No | Yes |
| `MWin.xaml` | Yes | No |
| `MaintenanceWin` | Yes | No |
| `MatCard` | No | Yes |
| `MatCard.xaml` | Yes | No |
| `MatDirWin` | No | Yes |
| `MatDirWin.xaml` | Yes | No |
| `MatGroupWin` | No | Yes |
| `MatGroupWin.xaml` | Yes | No |
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
| `PrmWin` | No | Yes |
| `PrmWin.xaml` | Yes | No |
| `ProjWin` | Yes | No |
| `QcAlertWin` | Yes | No |
| `QcInspectWin` | Yes | No |
| `QcTemplateWin` | Yes | No |
| `RepCard` | Yes | No |
| `RepostWin` | Yes | No |
| `RestoreCoWin` | No | Yes |
| `RestoreCoWin.xaml` | Yes | No |
| `RolloverWin` | Yes | No |
| `RptBase` | Yes | No |
| `SerWin` | No | Yes |
| `SerWin.xaml` | Yes | No |
| `SplWin` | No | Yes |
| `SplWin.xaml` | Yes | No |
| `StpWiz` | No | Yes |
| `StpWiz.xaml` | Yes | No |
| `SupportWin` | Yes | No |
| `UpdatesWin` | Yes | No |
| `WhsWin` | No | Yes |
| `WhsWin.xaml` | Yes | No |

## Report views

| Basename | C# | XAML |
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

| Basename | C# | XAML |
|---|---:|---:|
| `Modules/DashVw` | No | Yes |
| `Modules/DashVw.xaml` | Yes | No |
| `Modules/LangVM` | Yes | No |
| `Modules/ModuleWorkspaceView` | No | Yes |
| `Modules/ModuleWorkspaceView.xaml` | Yes | No |
| `Modules/ReportGridVw` | No | Yes |
| `Modules/ReportGridVw.xaml` | Yes | No |

## Mandatory checks before moving a view

1. Inspect its declared window/view type, namespace, constructor dependencies and public entry points.
2. Find every construction, Show/ShowDialog, navigation route, Ribbon action key, menu click handler, reflection lookup and DI registration.
3. Map its service/repository dependencies and the tables/queries it reads or writes.
4. Check localization keys, language dictionaries, styles, merged resource dictionaries, icons, templates and dynamic resource references.
5. Check XAML x:Class, StartupUri, ResourceDictionary Source, relative paths, pack URIs, component resource references and any code that uses file paths.
6. Check MainApp.csproj item rules and all build/publish scripts. The current WPF project disables SDK default compile/page discovery and uses recursive **\*.cs and **\*.xaml includes. A move can change resource URIs even if compilation still succeeds.
7. Move each existing XAML/code-behind pair together. Do not split large code-behind files merely to reduce line count.
8. Preserve public types, namespaces and runtime behavior unless a separately reviewed compatibility change is required.
9. Build Debug and Release after each bounded move; run focused tests and launch/navigation smoke tests before proceeding.
10. Update this inventory with old path, new path, route, dependencies, migration commit and verification evidence.

## Proposed destination layout

Keep the existing cohesive files intact. Organize by business capability, not by file size:

- Views/Platform/: main shell, company, users, roles, permissions, settings, backup, restore, licensing and diagnostics.
- Views/Inventory/: item/material cards, categories, warehouses, barcode, transfers, counts, adjustments, batch/serial and inventory operations.
- Views/Sales/: quotations, sales orders, invoices, returns, delivery and customer-facing sales workflows.
- Views/Purchasing/: purchase orders, receipts, supplier invoices and purchase returns.
- Views/Accounting/: chart of accounts, journals, vouchers, receipts, payments, cash, banks and reconciliation.
- Views/Manufacturing/: BOM, production orders, planning, routing and quality.
- Views/HR/: employees, organization, attendance, leave and payroll.
- Views/CRM/: leads, opportunities, pipeline and activities.
- Views/Reports/<module>/: reports grouped by their owning module, preserving each report class/file intact.

These are target folders, not permission to move all files in one batch. The first actual move must be a small, fully traced slice with all XAML/resource references checked.

## Migration status

- [x] Source inventory generated from the branch tree.
- [ ] Type/caller/route/dependency mapping completed.
- [ ] First bounded move selected and reviewed.
- [ ] First move built and smoke-tested.
- [ ] Remaining modules migrated in bounded waves.
- [ ] All routes and windows covered by automated or documented manual smoke tests.
- [ ] Final Debug/Release, database, and end-to-end regression matrix passed.
