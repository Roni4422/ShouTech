# Ribbon completion audit — all RibModMap-driven entries (265 at audit; 266 after P3)

Addendum (POS family, 2026-11-24): **POS.SessionSales** added
(266th entry) → PosSalesWin, class (a) on arrival (dedicated,
persisted read model, real print/return paths — docs/
POS_FAMILY.md P3). **POS.DefinePOS** moved from the (b)
DefManagerWin class to (a): it now opens the dedicated
PosTermWin (P1). The (b) count drops 36 → 34 by these two.

Owner directives 2026-11-24 14:38 + 14:43: audit EVERY entry,
classify (a) complete / (b) partial / (c) dead, commit this audit
FIRST, then complete every (b) and (c) full-stack in gated batches.
Each entry also carries the 14:43 standard columns: four-system
comparison state, shell-context conformance, status bar.

Method: the 265 ribbon buttons of MWin.xaml (all carry LocProps
keys; the same keys drive the Explorer nav pane) were traced
through the real dispatch chain — MWin key/button special cases →
RibbonReportRouter → RibbonNav (170 cases) → RptNav (113 cases) →
MainShVM system actions / module-workspace fallback — to their
target window classes, and each target was measured in the tree
(defining file, size, base class, status-bar presence). Window
context conformance reuses tools/audit_window_context.py (111
window classes, all central). Classification is evidence-based;
"thin" means the target's XAML+code totals under 150 lines, which
cannot carry a complete-pillars surface.

**F1 help topic-key column (owner decisions 2026-11-24 15:36 +
15:38):** every window's topic key is its class name by
convention — `BaseWin.HelpTopicKey` (virtual, default
`GetType().Name`) for BaseWin windows, the same class-name rule
applied by the App-level F1 class handler for non-BaseWin
windows. So the topic key of every entry in this audit IS the
target window class named in its row; a window without a key
cannot exist under the convention, and any future override is a
code-visible exception. F1 routing landed in batch H1 (gates
ALL-GREEN): BaseWin F1 → `HelpWin(topicKey)`; non-BaseWin F1 →
the App class handler → `HelpWin(className)`; an unwritten topic
shows the graceful topic-pending note over the manual — never an
error, never a dead key. The per-window INDEX (index entry +
keywords per topic, per language) is maintained with the help
content in the commercial phase; index coverage (windows
indexed / total windows) is reported there.

## Verdict summary

- **(a) complete: 216** — dedicated, substantive window or report.
- **(b) partial: 36** — listed below with the reason for each.
- **(c) dead: 4** — `Tools.Arabic`, `Tools.English`, `Tools.French`,
  `Tools.Turkish`. No handler exists anywhere for these keys: the
  click falls through to the generic module workspace and the
  language never switches. Fix: route them to the real language
  path (App.SetApplicationLanguage) — batch R1.
- **Working shell commands (not windows): 9** — Menu.Open,
  Menu.New, Menu.Exit, Exit.ExitAndShutdown, Exit.ScreenSaver,
  Exit.Title, View.InfoWindow, Help.About, Tools.Keyboard.

## The (b) list

- ~~`Inventory.MaterialProfits` → ItemProfitWin~~ **R3 DONE — formally ADOPTED**: material profits ARE the item profitability report (same concept, same data); router comment now records the adoption.
- ~~`Inventory.MaterialGroupMovementReport` → MatMoveWin~~ **R3 DONE — dedicated window BUILT**: GroupMoveWin (RptBase, ReportKey Inv.GroupMoves) over RptQuery.GroupMovesAsync — movement aggregated per material group + Ungrouped bucket; router now routes here.
- ~~`Inventory.SalesReport` → SalListWin~~ **R3 DONE — formally ADOPTED**: the posted sales document register is the inventory sales report; deeper analytics live in the profitability reports.
- `Inventory.EditMaterialSpecs` → DefManagerWin: generic definition editor (set: MaterialSpecs) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `Inventory.MaterialDiscounts` → DefManagerWin: generic definition editor (set: MaterialDiscounts) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- ~~`Accounts.DailyReport` → JrnListWin~~ **R3 DONE — formally ADOPTED**: the daily report is the journal register scoped by its date filters (same ledger, same rows).
- ~~`Accounts.AgingReceivables` → CustAgingWin~~ **R3 DONE — formally ADOPTED**: receivables aging IS customer aging.
- `Bonds.DefineVoucherTypes` → DefManagerWin: generic definition editor (set: VoucherTypes) — window is real and persists; per-set field completeness not yet audited
- `Bonds.DefineSecurities` → DefManagerWin: generic definition editor (set: SecurityTypes) — window is real and persists; per-set field completeness not yet audited
- `Bonds.PaymentTypes` → DefManagerWin: generic definition editor (set: PaymentTypes) — window is real and persists; per-set field completeness not yet audited
- `Bonds.DefinePaymentTypes` → DefManagerWin: generic definition editor (set: PaymentTypes) — window is real and persists; per-set field completeness not yet audited
- `Invoices.DefineInvoices` → DefManagerWin: generic definition editor (set: InvoiceDefinitions) — window is real and persists; per-set field completeness not yet audited
- `Invoices.CloseCash` → CloseCashWin: legacy window (standing flag: retirement is the owner's call)
- `POS.DefinePOS` → DefManagerWin: generic definition editor (set: PosTerminals) — window is real and persists; per-set field completeness not yet audited
- `POS.POSDiscounts` → DefManagerWin: generic definition editor (set: PosDiscounts) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- ~~`POS.SyncPOS` → PosSyncWin~~ **R5 DONE → (a)**: status filter (All/Queued/Sent/Failed), numbered rows (central F2Ui), cataloged error reporting on refresh + retry; outbox viewer + FAILED→QUEUED retry verified real.
- `Orders.DefineOrders` → DefManagerWin: generic definition editor (set: OrderDefinitions) — window is real and persists; per-set field completeness not yet audited
- `Customers.CustomerPrices` → DefManagerWin: generic definition editor (set: CustomerPrices) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `Customers.CustomerDiscounts` → DefManagerWin: generic definition editor (set: CustomerDiscounts) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `Customers.Areas` → DefManagerWin: generic definition editor (set: Areas) — window is real and persists; per-set field completeness not yet audited
- `Customers.AssignTargets` → DefManagerWin: generic definition editor (set: PartyTargets) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `SalesRep.AssignSalesRepTargets` → DefManagerWin: generic definition editor (set: RepTargets) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `SalesRep.HandheldDevices` → DefManagerWin: generic definition editor (set: Devices) — window is real and persists; per-set field completeness not yet audited
- `SalesRep.DeviceRoutes` → DefManagerWin: generic definition editor (set: DeviceRoutes) — window is real and persists; per-set field completeness not yet audited
- ~~`Tools.ActivityLog` → ActivityLogWin~~ **R5 DONE → (a)**: text filter over both audit sources (user/event/details), numbered rows, cataloged error reporting, selected tab persisted per user (WinPref via the BaseWin state hooks).
- `Tools.Addons` → AddonsWin: thin window (120 lines) — limited surface, completeness not proven
- ~~`OtherTools.Permissions` → PrmWin~~ **R5 DONE → (a)**: migrated from Window to BaseWin (shell context, whole-window RTL mirroring, geometry/state persistence, F1 topic), localized via the existing Permissions Lang section, own schedule tint (Hickory). The editor itself was already real (per-user permission exceptions via PermAdminSvc).
- ~~`OtherTools.PermissionTemplates` → PrmWin~~ **R5 DONE → (a)**: same window, same completion as Permissions above.
- `OtherTools.CustomFields` → DefManagerWin: generic definition editor (set: CustomFields) — window is real and persists; per-set field completeness not yet audited
- `OtherTools.UnitsOfMeasure` → DefManagerWin: generic definition editor (set: UnitsOfMeasure) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `OtherTools.PaymentMethods` → DefManagerWin: generic definition editor (set: PaymentMethods) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `OtherTools.MaterialSpecs` → DefManagerWin: generic definition editor (set: MaterialSpecs) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `OtherTools.DriverCard` → DefManagerWin: generic definition editor (set: Drivers) — window is real and persists; per-set field completeness not yet audited **R4: field-complete per docs/R4_STUDY.md → (a).**
- `OtherTools.DeliveryPrices` → DefManagerWin: generic definition editor (set: DeliveryPrices) — window is real and persists; per-set field completeness not yet audited
- `View.PaymentMethods` → DefManagerWin: generic definition editor (set: PaymentMethods) — window is real and persists; per-set field completeness not yet audited
- `Help.RenewLicense` → LicenseWin: thin window (148 lines) — limited surface, completeness not proven

## Inverse gaps (windows with no ribbon entry)

- **RestoreCoWin** — RESOLVED on re-inspection (2026-11-24):
  the window IS wired — TabOtherTools → Administration carries
  a "Restore company" button whose key routes through the
  generic path to RibbonNav case
  `OtherTools.RestoreCompany.Restore` → RestoreCoWin (restore a
  DELETED COMPANY; it is not the backup-restore window). The
  original audit line conflated it with backup restore, which
  lives in BkpWin and is reached from the File tab's
  reference-handled Backup / Restore Backup buttons
  (OnRibbonButtonClick → OpenBackupWindow). One wart fixed in
  the same pass: the File tab's Restore Backup button took its
  localized label from RestoreCompany.Restore ("Restore"); it
  now uses the dedicated Menu.RestoreBackup key (×4).
- The remaining untargeted window classes are sub-dialogs opened
  from parent windows (BatchSerialPickerWin, PosCheckoutWin,
  PosReturnWin, PurchaseAttachmentsWin, QcInspectWin/QcAlertWin/
  QcTemplateWin, SupplierQuotationEntryWin/CompareWin, BinWin,
  ColChsWin, CashDrawerOptionsWin via OptWin), startup surfaces
  (Lang, LogWin, SplWin), or the module-workspace view itself —
  correct by design.

## Completion plan (gated batches)

- **R1 — the (c) four**: wire Tools.{Arabic,English,French,
  Turkish} to App.SetApplicationLanguage in RibbonNav.
  **DONE (2026-11-24), gates ALL-GREEN**: the four cases now call
  RibbonNav.SwitchLanguage — UsrPref persistence (the OptWin
  pattern) + App.SetApplicationLanguage live switch (re-mirrors and
  re-localizes all open windows). The (c) class is empty.
- **R2 — grid/directory program** (owner spec 2026-11-24 15:07 +
  reference image 15:10 — Al-Ameen 2004, attribution corrected
  15:19; structure/content only, visuals modern per 15:20):
  invoice/voucher/report grid standard, MatDirWin tree, CoaWin
  tree + account card — these carry the four-system comparisons
  for their families (docs/GRID_PROGRAM.md). **R2a (invoice
  family) and R2b (voucher family) DONE (2026-11-24), gates
  ALL-GREEN**; R2c reports, R2d MatDirWin tree, R2e CoaWin +
  AccCard follow. After the grid program: the POS family
  workstream (owner, 15:31 — terminal definition, session
  opening, session sales, close/Z integration), then R3–R5.
- **R3 — router approximations (5)**: DONE (2026-11-24) — 4
  formally adopted (MaterialProfits, SalesReport, DailyReport,
  AgingReceivables — the mapped report is the requested report
  in substance), 1 dedicated window built (GroupMoveWin for
  MaterialGroupMovementReport). (b) count 34 → 29.
- **R4 — definition sets (23 entries)**: DONE (2026-11-24) per
  docs/R4_STUDY.md — registry matches schemas throughout; the
  money-driving sets are complete and consumed (12 entries →
  (a)); UnitsOfMeasure gained names (migration 121); 11 entries
  stay (b) with named missing consumers (document-type engine,
  delivery/handheld/platform consumers). (b) total 29 → 17.
- **R5 — thin windows (6)**: DONE (2026-11-24) per
  docs/R5_STUDY.md — PosSyncWin, ActivityLogWin, PrmWin (×2
  entries) completed → (a). AddonsWin and LicenseWin stay (b)
  BY DESIGN: they are the commercial licensing phase's
  foundation (honest read-only catalogue / real license info +
  online validation; no runtime toggle or renewal API exists —
  building one now would pre-decide D1/D2/D5). (b) total
  17 → 14 (11 definition entries awaiting module consumers +
  AddonsWin + LicenseWin + CloseCashWin).
- Comparisons for the remaining (a) windows are recorded
  progressively as their families are touched; a window's
  comparison column flips to "done" only with its table written.

## Full entry table

| # | Entry key | Opens | Via | Class | Shell context | Status bar | 4-system comparison | Note |
|---|---|---|---|---|---|---|---|---|
| 1 | Menu.New | StpWiz | MainShVM system action | cmd | App handler ✓ | — | pending | shell command, works by design (not a window) |
| 2 | Menu.Open | ComWin | MainShVM system action | cmd | App handler ✓ | — | pending | shell command, works by design (not a window) |
| 3 | Menu.Import | DatWin | MWin button special | a | central ✓ | — | pending |  |
| 4 | Menu.Export | DatWin | MWin button special | a | central ✓ | — | pending |  |
| 5 | Tools.Backup | BkpWin | MWin button special | a | App handler ✓ | — | pending |  |
| 6 | RestoreCompany.Restore | BkpWin | MWin button special | a | App handler ✓ | — | pending |  |
| 7 | Menu.Exit | — | MainShVM system action | cmd | central ✓ | n/a | pending | shell command, works by design (not a window) |
| 8 | Inventory.MaterialCard | MatCard | MWin button special | a | central ✓ | ✓ | pending |  |
| 9 | Inventory.CategoryCard | CatWin | MWin button special | a | central ✓ | ✓ | pending |  |
| 10 | Inventory.WarehouseCard | WhsWin | MWin button special | a | central ✓ | ✓ | pending |  |
| 11 | Inventory.MaterialGroups | MatGroupWin | MWin button special | a | central ✓ | — | pending |  |
| 12 | Inventory.GenerateBarcode | BcdGenWin | RptNav | a | central ✓ | — | pending |  |
| 13 | Inventory.DesignBarcode | BcdDesWin | RptNav | a | central ✓ | — | done (services B2–B4) |  |
| 14 | Inventory.CreateBarcode | BarWin | RptNav | a | central ✓ | — | pending |  |
| 15 | Inventory.PriceList | PriceListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 16 | Inventory.EndOfPeriod | EndOfPeriodWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 17 | Inventory.ShortageList | ShortListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 18 | Inventory.TransferMaterial | MatMergeWin | MWin key special | a | central ✓ | ✓ | pending |  |
| 19 | Inventory.InventoryAdjustment | WhsWin | MWin key special — stock-count/adjustment mode | a | central ✓ | ✓ | pending |  |
| 20 | Inventory.MaterialTree | MatDirWin | MWin button special | a | central ✓ | — | in grid/directory program |  |
| 21 | Inventory.SearchMaterial | MatListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 22 | Inventory.WarehouseInventory | StkBalWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 23 | Inventory.MaterialMovement | MatMoveWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 24 | Inventory.MaterialMovementChart | MatMoveChartWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 25 | Inventory.ExpiredMaterials | ExpiredWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 26 | Inventory.InvoiceAge | InvoiceAgeWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 27 | Inventory.MaterialProfits | ItemProfitWin | RibbonReportRouter (approximated) | a | App handler ✓ | ✓ | pending | R3: formally adopted (see the (b) list outcome) |
| 28 | Inventory.MaterialPriceHistory | PriceHistoryWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 29 | Inventory.ServiceMaterials | ServiceMatWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 30 | Inventory.MaterialStatistics | MatStatsWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 31 | Inventory.StagnantMaterials | StagnantWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 32 | Inventory.MostActiveMaterials | MostActiveWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 33 | Inventory.MaterialComparison | MatCompareWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 34 | Inventory.MaterialList | MatListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 35 | Inventory.MaterialGroupMovementReport | GroupMoveWin | RibbonReportRouter (approximated) | a | App handler ✓ | ✓ | pending | R3: dedicated GroupMoveWin built (see the (b) list outcome) |
| 36 | Inventory.NegativeOutput | NegativeStockWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 37 | Inventory.Customers | InvCustomersWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 38 | Inventory.SerialNumbersReport | SerWin | RibbonNav | a | central ✓ | — | pending |  |
| 39 | Inventory.EditSerialNumber | SerWin | RibbonNav | a | central ✓ | — | pending |  |
| 40 | Inventory.BranchMaterials | BranchMatWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 41 | Inventory.SalesReport | SalListWin | RibbonReportRouter (approximated) | a | App handler ✓ | ✓ | pending | R3: formally adopted (see the (b) list outcome) |
| 42 | Inventory.EditMaterialSpecs | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: MaterialSpecs) — window is real and persists; per-set field completeness not yet audited |
| 43 | Inventory.MaterialDiscounts | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: MaterialDiscounts) — window is real and persists; per-set field completeness not yet audited |
| 44 | Accounts.ChartOfAccounts | CoaWin | MWin button special | a | central ✓ | — | in grid/directory program |  |
| 45 | AccountCard | AccCard | MWin button special | a | central ✓ | — | in grid/directory program |  |
| 46 | Accounts.CostCenter | CcListWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 47 | Accounts.CostCenterCard | CcCard | RptNav | a | central ✓ | — | pending |  |
| 48 | Accounts.LinkWithAccounts | CcListWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 49 | Assets.Assets | AssetWin | RptNav | a | central ✓ | ✓ | pending |  |
| 50 | Accounts.CostCenterReport | CostCenterRptWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 51 | Accounts.CostCenterMaterialMovement | MatMoveWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 52 | Accounts.CostCenterBalance | CostCenterTrialWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 53 | Accounts.TaxConfiguration | TaxSetupWin | RibbonNav | a | central ✓ | — | pending |  |
| 54 | Accounts.TaxReport | TaxReportWin | RibbonNav | a | central ✓ | — | pending |  |
| 55 | Accounts.DefineCurrency | CurCard | RptNav | a | central ✓ | — | pending |  |
| 56 | Accounts.CurrencyRates | CurCard | RptNav | a | central ✓ | — | pending |  |
| 57 | Accounts.AverageCurrencyRate | AvgRateWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 58 | Accounts.ExchangeRateDifferences | ExchangeDiffWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 59 | Accounts.Ledger | AccStmtWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 60 | Accounts.CustomerStatement | CustStmtWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 61 | Accounts.GeneralJournal | JrnListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 62 | Accounts.DailyReport | JrnListWin | RibbonReportRouter (approximated) | a | App handler ✓ | ✓ | pending | R3: formally adopted (see the (b) list outcome) |
| 63 | Accounts.AccountMovementChart | AccMoveChartWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 64 | Accounts.AccountBalance | AccBalWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 65 | Accounts.BranchAccountMovement | BranchAccMoveWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 66 | Accounts.TrialBalance | TrialBalanceWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 67 | Accounts.Operations | ClosingAccountsWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 68 | Accounts.Trading | ClosingAccountsWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 69 | Accounts.ProfitLoss | IncStmtWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 70 | Accounts.BalanceSheet | BalSheetWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 71 | Accounts.IncomeStatement | IncStmtWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 72 | Accounts.CashFlowStatement | CashFlowWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 73 | Accounts.DormantAccounts | DormantWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 74 | Accounts.AgingReceivables | CustAgingWin | RibbonReportRouter (approximated) | a | App handler ✓ | ✓ | pending | R3: formally adopted (see the (b) list outcome) |
| 75 | Accounts.PaymentReconciliation | PayRecWin | RptNav | a | central ✓ | ✓ | in grid/directory program |  |
| 76 | Accounts.CollectionRates | CollectionRatesWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 77 | Accounts.AccountComparison | AccCompareWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 78 | Accounts.FinancialAnalysis | FinancialAnalysisWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 79 | Accounts.FinancialAnalysis | FinancialAnalysisWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 80 | Bonds.JournalVoucher | JrnWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 81 | Bonds.ReceiptVoucher | CshWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 82 | Bonds.PaymentVoucher | CshWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 83 | Bonds.JournalVoucher | JrnWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 84 | Bonds.CashJournal | CashJournalWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 85 | Bonds.DefineVoucherTypes | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: VoucherTypes) — window is real and persists; per-set field completeness not yet audited |
| 86 | Bonds.Installment | InstallmentsWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 87 | Bonds.SearchInstallment | InstallmentsWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 88 | Bonds.ReceiptCheque | ChqWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 89 | Bonds.PaymentCheque | ChqWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 90 | Bonds.DefineSecurities | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: SecurityTypes) — window is real and persists; per-set field completeness not yet audited |
| 91 | Bonds.SearchSecurities | ChqListWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 92 | Bonds.PaymentTypes | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: PaymentTypes) — window is real and persists; per-set field completeness not yet audited |
| 93 | Bonds.DefinePaymentTypes | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: PaymentTypes) — window is real and persists; per-set field completeness not yet audited |
| 94 | Bonds.MonitorVouchers | JrnListWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 95 | Invoices.Transfer | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 96 | Invoices.OpeningStock | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 97 | Invoices.MaterialEntry | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 98 | Invoices.MaterialExit | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 99 | Invoices.PurchaseInvoice | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 100 | Invoices.PurchaseReturn | PurchaseReturnWin | RptNav | a | central ✓ | ✓ | pending |  |
| 101 | Purchasing.ReturnGoodsReceipt | PurchaseReceiptReturnWin | RptNav | a | central ✓ | — | pending |  |
| 102 | Purchasing.MatchPolicy | PurchaseMatchPolicyWin | RptNav | a | central ✓ | — | pending |  |
| 103 | Invoices.SalesInvoice | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 104 | Invoices.SalesReturn | SalesReturnWin | RptNav | a | central ✓ | ✓ | pending |  |
| 105 | Invoices.RmaRequests | RmaWin | RibbonNav | a | central ✓ | — | pending |  |
| 106 | Invoices.Warranty | WarrantyWin | RibbonNav | a | central ✓ | — | pending |  |
| 107 | Invoices.InventoryCount | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 108 | Invoices.PrintBarcode | BarWin | RptNav | a | central ✓ | — | pending |  |
| 109 | Invoices.DefineInvoices | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: InvoiceDefinitions) — window is real and persists; per-set field completeness not yet audited |
| 110 | Invoices.SearchInvoices | DocSearchWin | RibbonNav | a | central ✓ | — | pending |  |
| 111 | Invoices.CashSales | PosWin | RptNav | a | central ✓ | — | pending |  |
| 112 | Invoices.CloseCash | CloseCashWin | RibbonNav | b | central ✓ | ✓ | pending | legacy window (standing flag: retirement is the owner's call) |
| 113 | Invoices.Quotation | InvoiceWin | RptNav | a | central ✓ | — | in grid/directory program |  |
| 114 | Invoices.SearchQuotations | DocSearchWin | RibbonNav | a | central ✓ | — | pending |  |
| 115 | Invoices.InvoiceProfits | InvoiceProfitsWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 116 | Invoices.PackingList | PackingListWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 117 | Invoices.InvoiceDesigner | InvDesWin | RibbonNav | a | central ✓ | ✓ | done (services B2–B4) |  |
| 118 | Invoices.CompressInvoices | CompressInvoicesWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 119 | Invoices.RepostInvoices | RepostWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 120 | POS.OpenCashier | PosWin | RptNav | a | central ✓ | — | pending |  |
| 121 | POS.Sessions | PosSessionWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 122 | POS.ZReport | PosZReportWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 123 | POS.Points | LoyaltyWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 124 | POS.DefinePOS | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: PosTerminals) — window is real and persists; per-set field completeness not yet audited |
| 125 | POS.POSCashReport | PosCashRptWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 126 | POS.Performance | PosPerfRptWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 127 | POS.POSDiscounts | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: PosDiscounts) — window is real and persists; per-set field completeness not yet audited |
| 128 | POS.SyncPOS | PosSyncWin | RibbonNav | a | central ✓ | ✓ | pending | R5: status filter, numbered rows, cataloged error reporting |
| 129 | POS.AdjustPrice | PriceListWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 130 | POS.AdjustQuantity | WhsWin | RibbonNav — stock-count/adjustment mode | a | central ✓ | ✓ | pending |  |
| 131 | POS.DeleteMaterial | MatDirWin | RibbonNav | a | central ✓ | — | in grid/directory program |  |
| 132 | Orders.PurchaseOrder | OrdWin | RptNav | a | central ✓ | — | pending |  |
| 133 | Orders.PurchaseRequisitions | PurchaseRequisitionWin | RibbonNav | a | central ✓ | — | pending |  |
| 134 | Orders.LandedCosts | PurchaseLandedCostWin | RibbonNav | a | central ✓ | — | pending |  |
| 135 | Orders.RequestForQuotation | OrdWin | RptNav | a | central ✓ | — | pending |  |
| 136 | Orders.SalesOrder | OrdWin | RptNav | a | central ✓ | — | pending |  |
| 137 | Projects.Projects | ProjWin | RptNav | a | central ✓ | ✓ | pending |  |
| 138 | Orders.DefineOrders | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: OrderDefinitions) — window is real and persists; per-set field completeness not yet audited |
| 139 | Orders.OrderReceiptDelivery | SalesDeliveryWin | RibbonNav | a | central ✓ | — | pending |  |
| 140 | Orders.TrackOrders | OrderTrackWin | RibbonNav | a | central ✓ | — | pending |  |
| 141 | Orders.OrderPayments | OrderPaymentsWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 142 | Orders.SearchOrders | DocSearchWin | RibbonNav | a | central ✓ | — | pending |  |
| 143 | Orders.RecurringSchedules | RecurWin | RibbonNav | a | central ✓ | — | pending |  |
| 144 | Customers.CustomerCard | CstCard | RptNav | a | central ✓ | — | pending |  |
| 145 | Customers.CustomerPrices | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: CustomerPrices) — window is real and persists; per-set field completeness not yet audited |
| 146 | Customers.CustomerDiscounts | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: CustomerDiscounts) — window is real and persists; per-set field completeness not yet audited |
| 147 | Customers.Areas | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: Areas) — window is real and persists; per-set field completeness not yet audited |
| 148 | Customers.AssignTargets | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: PartyTargets) — window is real and persists; per-set field completeness not yet audited |
| 149 | Customers.TargetsReport | TargetsRptWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 150 | Customers.SearchCustomers | PartySearchWin | RibbonNav | a | central ✓ | — | pending |  |
| 151 | Customers.CustomerDue | CustomerDueWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 152 | Customers.LinkPayments | PayRecWin | RibbonNav | a | central ✓ | ✓ | in grid/directory program |  |
| 153 | Customers.SearchDues | CustomerDueWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 154 | CRM.Leads | CrmLeadWin | RibbonNav | a | central ✓ | — | pending |  |
| 155 | CRM.Pipeline | CrmPipelineWin | RibbonNav | a | central ✓ | — | pending |  |
| 156 | CRM.Opportunities | CrmOppWin | RibbonNav | a | central ✓ | — | pending |  |
| 157 | CRM.Activities | CrmActivitiesWin | RibbonNav | a | central ✓ | — | pending |  |
| 158 | CRM.Campaigns | CrmCampaignWin | RibbonNav | a | central ✓ | — | pending |  |
| 159 | Reports.CrmFunnel | CrmFunnelWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 160 | Customers.CustomerBalance | CustBalWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 161 | Customers.BalanceAccounts | PayRecWin | RibbonNav | a | central ✓ | ✓ | in grid/directory program |  |
| 162 | Customers.EditCustomers | PartyManagerWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 163 | Customers.CustomerGroups | GroupManagerWin | RibbonNav | a | central ✓ | — | pending |  |
| 164 | Customers.CustomerReports | CustBalWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 165 | SalesRep.SalesRepCard | RepCard | RptNav | a | central ✓ | — | pending |  |
| 166 | SalesRep.SalesRepGroups | GroupManagerWin | RibbonNav | a | central ✓ | — | pending |  |
| 167 | SalesRep.AssignSalesRepTargets | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: RepTargets) — window is real and persists; per-set field completeness not yet audited |
| 168 | SalesRep.SalesRepTargetsReport | TargetsRptWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 169 | SalesRep.Commissions | CommWin | RibbonNav | a | central ✓ | — | pending |  |
| 170 | SalesRep.Contracts | ContractWin | RibbonNav | a | central ✓ | — | pending |  |
| 171 | SalesRep.SearchCustomers | PartySearchWin | RibbonNav | a | central ✓ | — | pending |  |
| 172 | SalesRep.SalesRepMovement | RepMovementWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 173 | SalesRep.SalesRepCollections | RepCollectionsWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 174 | SalesRep.HandheldDevices | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: Devices) — window is real and persists; per-set field completeness not yet audited |
| 175 | SalesRep.DeviceRoutes | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: DeviceRoutes) — window is real and persists; per-set field completeness not yet audited |
| 176 | Manufacturing.ManufacturingModels | BomWin | RptNav | a | central ✓ | — | pending |  |
| 177 | Manufacturing.ManufacturingProcess | MfgWin | RptNav | a | central ✓ | — | pending |  |
| 178 | Manufacturing.Planning | MfgPlanWin | RptNav | a | central ✓ | — | pending |  |
| 179 | Manufacturing.ManufacturingFeasibility | MfgFeasibilityWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 180 | Manufacturing.SearchManufacturing | MfgListWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 181 | Manufacturing.ManufacturingCostVariance | MfgVarianceWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 182 | Manufacturing.RawMaterialCostVariance | MfgVarianceWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 183 | Manufacturing.AdditionalCostVariance | MfgVarianceWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 184 | Manufacturing.ManufacturedMaterialCostVariance | MfgVarianceWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 185 | Manufacturing.ManufacturedMaterialCosts | MfgCostsWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 186 | Manufacturing.RepostManufacturing | RepostWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 187 | HR.Employees | HrEmployeeWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 188 | HR.Departments | HrDeptWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 189 | HR.Designations | HrDesigWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 190 | HR.Attendance | HrAttendanceWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 191 | HR.Leave | HrLeaveWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 192 | HR.Structures | HrStructureWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 193 | HR.Payroll | HrPayrollWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 194 | HR.PayrollSummary | HrPayrollSummaryWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 195 | HR.PayslipReport | HrPayslipWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 196 | HR.AttendanceSummary | HrAttendanceSummaryWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 197 | HR.LeaveBalances | HrLeaveBalanceWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 198 | Banks.DefineBanks | BnkCard | RptNav | a | central ✓ | — | pending |  |
| 199 | Banks.BankEstimate | BankEstimateWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 200 | Banks.BankReconciliation | BankRecWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 201 | Banks.BankBalances | BankBalancesWin | RibbonNav | a | App handler ✓ | ✓ | pending |  |
| 202 | Accounts.TrialBalance | TrialBalanceWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 203 | Reports.AccountStatement | AccStmtWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 204 | Accounts.GeneralJournal | JrnListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 205 | Reports.CustomerAging | CustAgingWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 206 | Reports.SupplierAging | SupAgingWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 207 | Reports.StockBalances | StkBalWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 208 | Reports.MaterialMoves | MatMoveWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 209 | Reports.StockAdjustment | StkAdjRptWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 210 | Reports.ItemProfitability | ItemProfitWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 211 | Customers.CustomerBalance | CustBalWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 212 | Reports.SupplierBalances | SupBalWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 213 | Reports.SalesInvoiceList | SalListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 214 | Reports.PurchaseInvoiceList | PurListWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 215 | Reports.GoodsReceivedNotInvoiced | GrniWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 216 | Reports.SalesChainExceptions | SalExceptWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 217 | Reports.SupplierStatement | SupStatementWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 218 | Reports.Statistics | TrialBalanceWin | RptNav | a | App handler ✓ | ✓ | pending |  |
| 219 | Reports.CustomReports | CustomReportsWin | RibbonNav | a | central ✓ | ✓ | done (services B2–B4) |  |
| 220 | Reports.CustomizeReports | CustomReportsWin | RibbonNav | a | central ✓ | ✓ | done (services B2–B4) |  |
| 221 | Tools.Calculator | CalculatorWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 222 | Tools.Keyboard | — | command — on-screen keyboard | cmd | central ✓ | n/a | pending | shell command, works by design (not a window) |
| 223 | Tools.Options | OptWin | MWin key special | a | central ✓ | ✓ | pending |  |
| 224 | Tools.MaintenanceWizard | MaintenanceWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 225 | Tools.Backup | BkpWin | MWin button special | a | App handler ✓ | — | pending |  |
| 226 | Tools.Arabic | — | UNRESOLVED | c | central ✓ | n/a | pending | no handler anywhere — falls into the generic module workspace; the named action (language switch) never happens |
| 227 | Tools.English | — | UNRESOLVED | c | central ✓ | n/a | pending | no handler anywhere — falls into the generic module workspace; the named action (language switch) never happens |
| 228 | Tools.French | — | UNRESOLVED | c | central ✓ | n/a | pending | no handler anywhere — falls into the generic module workspace; the named action (language switch) never happens |
| 229 | Tools.Turkish | — | UNRESOLVED | c | central ✓ | n/a | pending | no handler anywhere — falls into the generic module workspace; the named action (language switch) never happens |
| 230 | Tools.ChangePassword | ChangePasswordWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 231 | Tools.ActivityLog | ActivityLogWin | RibbonNav | a | central ✓ | ✓ | pending | R5: text filter, numbered rows, cataloged errors, tab persisted |
| 232 | Tools.Addons | AddonsWin | RibbonNav | b | central ✓ | ✓ | pending | thin window (120 lines) — limited surface, completeness not proven |
| 233 | Tools.AI | FinancialAnalysisWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 234 | OtherTools.Rotation | RolloverWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 235 | OtherTools.Permissions | PrmWin | RibbonNav | a | central ✓ | ✓ | pending | R5: BaseWin migration (context/RTL/persistence/F1), Hickory tint |
| 236 | OtherTools.PermissionTemplates | PrmWin | RibbonNav | a | central ✓ | ✓ | pending | R5: BaseWin migration (context/RTL/persistence/F1), Hickory tint |
| 237 | OtherTools.CustomFields | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: CustomFields) — window is real and persists; per-set field completeness not yet audited |
| 238 | RestoreCompany.Restore | BkpWin | MWin button special | a | App handler ✓ | — | pending |  |
| 239 | OtherTools.UnitsOfMeasure | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: UnitsOfMeasure) — window is real and persists; per-set field completeness not yet audited |
| 240 | OtherTools.PaymentMethods | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: PaymentMethods) — window is real and persists; per-set field completeness not yet audited |
| 241 | OtherTools.MaterialSpecs | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: MaterialSpecs) — window is real and persists; per-set field completeness not yet audited |
| 242 | OtherTools.DriverCard | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: Drivers) — window is real and persists; per-set field completeness not yet audited |
| 243 | OtherTools.DeliveryPrices | DefManagerWin | RibbonNav | b | central ✓ | ✓ | pending | generic definition editor (set: DeliveryPrices) — window is real and persists; per-set field completeness not yet audited |
| 244 | OtherTools.PickupRequests | DeliveryDeskWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 245 | OtherTools.SearchDelivery | DeliveryDeskWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 246 | OtherTools.CloseDriverCash | DeliveryDeskWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 247 | View.ChartOfAccounts | CoaWin | RibbonNav | a | central ✓ | — | in grid/directory program |  |
| 248 | View.MaterialTree | MatDirWin | RibbonNav | a | central ✓ | — | in grid/directory program |  |
| 249 | View.ManufacturingTree | MfgListWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 250 | View.CostCenterTree | CcListWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 251 | View.InfoWindow | — | command — about dialog | cmd | central ✓ | n/a | pending | shell command, works by design (not a window) |
| 252 | View.PaymentMethods | DefManagerWin | RibbonNav | a | central ✓ | ✓ | pending | generic definition editor (set: PaymentMethods) — window is real and persists; per-set field completeness not yet audited |
| 253 | View.CompanyInfo | ComWin | RibbonNav | a | App handler ✓ | — | pending |  |
| 254 | View.ExportData | DatWin | RibbonNav | a | central ✓ | — | pending |  |
| 255 | Exit.ScreenSaver | — | command — lock workstation | cmd | central ✓ | n/a | pending | shell command, works by design (not a window) |
| 256 | Exit.SwitchCompany | ComWin | RibbonNav | a | App handler ✓ | — | pending |  |
| 257 | Exit.Title | — | command — confirm exit | cmd | central ✓ | n/a | pending | shell command, works by design (not a window) |
| 258 | Exit.ExitAndShutdown | — | MainShVM system action | cmd | central ✓ | n/a | pending | shell command, works by design (not a window) |
| 259 | Help.CheckUpdates | UpdatesWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 260 | Help.UpdateFromFile | UpdatesWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 261 | Help.Instructions | HelpWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 262 | Help.TechnicalSupport | SupportWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 263 | Help.Suggestions | SupportWin | RibbonNav | a | central ✓ | ✓ | pending |  |
| 264 | Help.RenewLicense | LicenseWin | RibbonNav | b | central ✓ | ✓ | pending | thin window (148 lines) — limited surface, completeness not proven |
| 265 | Help.About | — | MainShVM system action | cmd | central ✓ | n/a | pending | shell command, works by design (not a window) |
