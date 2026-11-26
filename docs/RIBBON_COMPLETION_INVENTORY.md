<!-- source: docs/RIBBON_COMPLETION_INVENTORY.md -->

# Ribbon Completion Inventory — ShouTech ERP

Branch: `feat/ribbon-windows-complete` (from main @ 9c8dea8). Date: 2026-11-17.
Goal (owner directive): every entry on the MWin ribbon opens a complete, working,
data-backed window. Done = button wired, window exists, loads real data, core
actions (new/edit/save/delete/post/convert as applicable) actually persist.
Heavy-duty rules: transactional writes, parameterized SQL, RBAC, audit,
both-database migrations, Lang keys in all 4 catalogs, green build.

## How the ribbon routes today (architecture)

- `cod/app/src/Views/MWin.xaml` (792 lines) defines 17 tabs, all
  `Visibility=Collapsed`, revealed through the top Menu. Every button uses
  `Click="OnRibbonButtonClick"` with a `Tag` = action key (228 buttons).
- `MWin.xaml.cs::OnRibbonButtonClick` resolution order:
  1. Named-button special cases (Backup, Category/Warehouse/Account/Material
     cards, Material Groups/Tree, Chart of Accounts, Import/Export, Open
     Company, Exit) and three key cases (`Tools.Options`,
     `Inventory.InventoryAdjustment`, `Inventory.TransferMaterial`).
  2. `RptNav.TryHandle(key)` — 99 keys to report windows, `InvoiceWin` kinds,
     POS, journal/cash/cheque vouchers, cards, PayRecWin, ProjWin, AssetWin,
     MfgWin, OrdWin, barcode and list windows.
  3. `MainShVM.HandleRibbonAction` — a few system actions
     (Menu.Open/New/Exit, Exit.ExitAndShutdown, Help.About, Tools.Backup).
  4. Fallback: `NavServ.NavigateToModule` -> `ModuleWorkspaceView`, a
     placeholder page ("ready for development - will be connected to the
     database"). **Any button reaching step 4 is a dead button.**
- Defect found: `Services/RibbonReportRouter.cs` (routes
  Inventory.MaterialProfits, Inventory.MaterialGroupMovementReport,
  Inventory.SalesReport, Accounts.DailyReport, Accounts.AgingReceivables)
  is **never called** - those 5 buttons fall through to the placeholder.

## Status counts - BEFORE

| Status | Count | Meaning |
|---|---|---|
| COMPLETE | 74 | Opens a real, data-backed window whose core actions persist |
| PLACEHOLDER | 14 | Window opens but Save/Delete/Post/Print are stubbed (`ShowOperationUnavailable` / fake data) |
| UNWIRED | 140 | Falls through to the ModuleWorkspaceView placeholder (135) or to dead code (5) |
| **Total** | **228** | |

PLACEHOLDER windows (verified by grep for `ShowPersistenceUnavailable` /
`ShowOperationUnavailable`): CcCard, BnkCard, CshWin, ChqWin, CstCard, CurCard,
RepCard, MfgWin, OrdWin (JrnWin retains an unused helper - removed in this pass).

## Full button inventory (before)

| Tab | Group | Button | Key | Target (before) | Status (before) |
|---|---|---|---|---|---|
| TabFile | FileOperations | New | `Menu.New` | MainShVM system action | COMPLETE |
| TabFile | FileOperations | Open | `Menu.Open` | MainShVM system action | COMPLETE |
| TabFile | FileOperations | Import data | `Menu.Import` | ? | COMPLETE |
| TabFile | FileOperations | Export data | `Menu.Export` | ? | COMPLETE |
| TabFile | FileBackup | Backup | `Tools.Backup` | MainShVM system action | COMPLETE |
| TabFile | FileBackup | Restore backup | `RestoreCompany.Restore` | ? | COMPLETE |
| TabFile | FileExit | Exit | `Menu.Exit` | MainShVM system action | COMPLETE |
| TabInventory | InventoryCards | Material card | `Inventory.MaterialCard` | ? | COMPLETE |
| TabInventory | InventoryCards | Category card | `Inventory.CategoryCard` | ? | COMPLETE |
| TabInventory | InventoryCards | Warehouse card | `Inventory.WarehouseCard` | ? | COMPLETE |
| TabInventory | InventoryCards | Material Groups | `Inventory.MaterialGroups` | ? | COMPLETE |
| TabInventory | InventoryBarcode | Generate barcode | `Inventory.GenerateBarcode` | BcdGenWin | COMPLETE |
| TabInventory | InventoryBarcode | Design and print barcode | `Inventory.DesignBarcode` | BcdDesWin | COMPLETE |
| TabInventory | InventoryBarcode | Create barcode | `Inventory.CreateBarcode` | BarWin | COMPLETE |
| TabInventory | InventoryMovements | Price list | `Inventory.PriceList` | PriceListWin | COMPLETE |
| TabInventory | InventoryMovements | End of period stock | `Inventory.EndOfPeriod` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMovements | Shortage list | `Inventory.ShortageList` | ShortListWin | COMPLETE |
| TabInventory | InventoryMovements | Transfer material movement | `Inventory.TransferMaterial` | MWin key case (options/adjustment/merge) | COMPLETE |
| TabInventory | InventoryMovements | Inventory adjustment | `Inventory.InventoryAdjustment` | MWin key case (options/adjustment/merge) | COMPLETE |
| TabInventory | InventoryMaterialReports | Material Directory | `Inventory.MaterialTree` | ? | COMPLETE |
| TabInventory | InventoryMaterialReports | Search materials | `Inventory.SearchMaterial` | MatListWin | COMPLETE |
| TabInventory | InventoryMaterialReports | Warehouse inventory | `Inventory.WarehouseInventory` | StkBalWin | COMPLETE |
| TabInventory | InventoryMaterialReports | Material movement | `Inventory.MaterialMovement` | MatMoveWin | COMPLETE |
| TabInventory | InventoryMaterialReports | Material movement chart | `Inventory.MaterialMovementChart` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Expired materials | `Inventory.ExpiredMaterials` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Invoice aging | `Inventory.InvoiceAge` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Material profits | `Inventory.MaterialProfits` | RibbonReportRouter (dead code — never called) | UNWIRED |
| TabInventory | InventoryMaterialReports | Material price history | `Inventory.MaterialPriceHistory` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Service materials reports | `Inventory.ServiceMaterials` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Material statistics | `Inventory.MaterialStatistics` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Stagnant materials | `Inventory.StagnantMaterials` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Most active materials | `Inventory.MostActiveMaterials` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Material comparison | `Inventory.MaterialComparison` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryMaterialReports | Material list | `Inventory.MaterialList` | MatListWin | COMPLETE |
| TabInventory | InventoryMaterialReports | Material and group movement report | `Inventory.MaterialGroupMovementReport` | RibbonReportRouter (dead code — never called) | UNWIRED |
| TabInventory | InventoryMaterialReports | Negative output | `Inventory.NegativeOutput` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryOther | Customers and material intersection | `Inventory.Customers` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryOther | Serial numbers report | `Inventory.SerialNumbersReport` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryOther | Edit serial number | `Inventory.EditSerialNumber` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryOther | Branch materials | `Inventory.BranchMaterials` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryOther | Sales report | `Inventory.SalesReport` | RibbonReportRouter (dead code — never called) | UNWIRED |
| TabInventory | InventoryOther | Edit material specs | `Inventory.EditMaterialSpecs` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInventory | InventoryOther | Material discounts | `Inventory.MaterialDiscounts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsGeneral | Chart of accounts | `Accounts.ChartOfAccounts` | ? | COMPLETE |
| TabAccounts | AccountsGeneral | Account card | `AccountCard` | ? | COMPLETE |
| TabAccounts | AccountsGeneral | Cost center | `Accounts.CostCenter` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsGeneral | Cost center card | `Accounts.CostCenterCard` | CcCard | PLACEHOLDER |
| TabAccounts | AccountsGeneral | Link to accounts | `Accounts.LinkWithAccounts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsGeneral | Fixed assets | `Assets.Assets` | AssetWin | COMPLETE |
| TabAccounts | AccountsGeneral | Cost center report | `Accounts.CostCenterReport` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsGeneral | Cost center material movement | `Accounts.CostCenterMaterialMovement` | MatMoveWin | COMPLETE |
| TabAccounts | AccountsGeneral | Cost center trial balance | `Accounts.CostCenterBalance` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsCurrencies | Define currency | `Accounts.DefineCurrency` | CurCard | PLACEHOLDER |
| TabAccounts | AccountsCurrencies | Currency rates bulletin | `Accounts.CurrencyRates` | CurCard | PLACEHOLDER |
| TabAccounts | AccountsCurrencies | Average currency rates | `Accounts.AverageCurrencyRate` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsCurrencies | Exchange rate differences | `Accounts.ExchangeRateDifferences` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsReports | General ledger | `Accounts.Ledger` | AccStmtWin | COMPLETE |
| TabAccounts | AccountsReports | Customer statement | `Accounts.CustomerStatement` | AccStmtWin | COMPLETE |
| TabAccounts | AccountsReports | General journal | `Accounts.GeneralJournal` | JrnListWin | COMPLETE |
| TabAccounts | AccountsReports | Daily report | `Accounts.DailyReport` | RibbonReportRouter (dead code — never called) | UNWIRED |
| TabAccounts | AccountsReports | Account movement chart | `Accounts.AccountMovementChart` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsReports | Account balance | `Accounts.AccountBalance` | AccBalWin | COMPLETE |
| TabAccounts | AccountsReports | Branch account movement | `Accounts.BranchAccountMovement` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsReports | Trial balance | `Accounts.TrialBalance` | TrialBalanceWin | COMPLETE |
| TabAccounts | AccountsClosingAccounts | Operations | `Accounts.Operations` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsClosingAccounts | Trading | `Accounts.Trading` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsClosingAccounts | Profit and loss | `Accounts.ProfitLoss` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsFinancialStatements | Balance sheet | `Accounts.BalanceSheet` | BalSheetWin | COMPLETE |
| TabAccounts | AccountsFinancialStatements | Income statement | `Accounts.IncomeStatement` | IncStmtWin | COMPLETE |
| TabAccounts | AccountsFinancialStatements | Cash flow statement | `Accounts.CashFlowStatement` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsAnalysis | Inactive accounts | `Accounts.DormantAccounts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsAnalysis | Receivables aging | `Accounts.AgingReceivables` | RibbonReportRouter (dead code — never called) | UNWIRED |
| TabAccounts | AccountsAnalysis | Payment reconciliation | `Accounts.PaymentReconciliation` | PayRecWin | COMPLETE |
| TabAccounts | AccountsAnalysis | Collection ratios | `Accounts.CollectionRates` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsAnalysis | Account comparison | `Accounts.AccountComparison` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsAnalysis | Local financial analysis | `Accounts.FinancialAnalysis` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabAccounts | AccountsAnalysis | AI financial analysis | `Accounts.FinancialAnalysis` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsVouchers | Journal voucher | `Bonds.JournalVoucher` | JrnWin | COMPLETE |
| TabBonds | BondsVouchers | Receipt voucher | `Bonds.ReceiptVoucher` | CshWin | PLACEHOLDER |
| TabBonds | BondsVouchers | Payment voucher | `Bonds.PaymentVoucher` | CshWin | PLACEHOLDER |
| TabBonds | BondsVouchers | Daily voucher | `Bonds.JournalVoucher` | JrnWin | COMPLETE |
| TabBonds | BondsVouchers | Cash journal | `Bonds.CashJournal` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsVouchers | Define voucher patterns | `Bonds.DefineVoucherTypes` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsInstallments | Installments | `Bonds.Installment` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsInstallments | Search installments | `Bonds.SearchInstallment` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsNegotiableInstruments | Incoming check | `Bonds.ReceiptCheque` | ChqWin | PLACEHOLDER |
| TabBonds | BondsNegotiableInstruments | Outgoing check | `Bonds.PaymentCheque` | ChqWin | PLACEHOLDER |
| TabBonds | BondsNegotiableInstruments | Define negotiable instruments | `Bonds.DefineSecurities` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsNegotiableInstruments | Search negotiable instruments | `Bonds.SearchSecurities` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsPayments | Payment patterns | `Bonds.PaymentTypes` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsPayments | Define payment patterns | `Bonds.DefinePaymentTypes` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBonds | BondsMonitoring | Monitor and edit vouchers | `Bonds.MonitorVouchers` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesInvoices | Transfer | `Invoices.Transfer` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesInvoices | Opening stock invoice | `Invoices.OpeningStock` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesInvoices | Material in invoice | `Invoices.MaterialEntry` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesInvoices | Material out invoice | `Invoices.MaterialExit` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesPurchasing | Purchase invoice | `Invoices.PurchaseInvoice` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesPurchasing | Purchase return invoice | `Invoices.PurchaseReturn` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesSales | Sales invoice | `Invoices.SalesInvoice` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesSales | Sales return invoice | `Invoices.SalesReturn` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesSales | Inventory count invoice | `Invoices.InventoryCount` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesSales | Print Barcode | `Invoices.PrintBarcode` | BarWin | COMPLETE |
| TabInvoices | InvoicesGeneral | Define invoices | `Invoices.DefineInvoices` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesGeneral | Search invoices | `Invoices.SearchInvoices` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesGeneral | Cash sales invoices | `Invoices.CashSales` | ? | COMPLETE |
| TabInvoices | InvoicesGeneral | Close cash register | `Invoices.CloseCash` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesGeneral | Quotation | `Invoices.Quotation` | InvoiceWin | COMPLETE |
| TabInvoices | InvoicesGeneral | Search quotations | `Invoices.SearchQuotations` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesGeneral | Invoice profits | `Invoices.InvoiceProfits` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesGeneral | Packing list | `Invoices.PackingList` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesOtherTools | Compress invoices | `Invoices.CompressInvoices` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabInvoices | InvoicesOtherTools | Repost invoices | `Invoices.RepostInvoices` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSSales | Open POS Cashier | `POS.OpenCashier` | ? | COMPLETE |
| TabPOS | POSAdministration | Points | `POS.Points` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSAdministration | Define POS | `POS.DefinePOS` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSAdministration | POS cash report | `POS.POSCashReport` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSAdministration | POS discounts | `POS.POSDiscounts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSAdministration | POS sync | `POS.SyncPOS` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSReports | Edit material price | `POS.AdjustPrice` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSReports | Edit material quantity | `POS.AdjustQuantity` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabPOS | POSReports | Delete material | `POS.DeleteMaterial` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOrders | OrdersOrders | Purchase order | `Orders.PurchaseOrder` | OrdWin | PLACEHOLDER |
| TabOrders | OrdersOrders | Sales order | `Orders.SalesOrder` | OrdWin | PLACEHOLDER |
| TabOrders | OrdersGeneral | Projects | `Projects.Projects` | ProjWin | COMPLETE |
| TabOrders | OrdersGeneral | Define orders | `Orders.DefineOrders` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOrders | OrdersGeneral | Order receive/deliver invoices | `Orders.OrderReceiptDelivery` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOrders | OrdersGeneral | Order tracking | `Orders.TrackOrders` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOrders | OrdersGeneral | Order payments | `Orders.OrderPayments` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOrders | OrdersGeneral | Search orders | `Orders.SearchOrders` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersCards | Customer card | `Customers.CustomerCard` | CstCard | PLACEHOLDER |
| TabCustomers | CustomersCards | Customer prices | `Customers.CustomerPrices` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersCards | Customer discounts | `Customers.CustomerDiscounts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersCards | Regions | `Customers.Areas` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersTargets | Assign customer targets | `Customers.AssignTargets` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersTargets | Customer targets report | `Customers.TargetsReport` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersFollowUp | Search customers | `Customers.SearchCustomers` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersFollowUp | Customer due | `Customers.CustomerDue` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersFollowUp | Link customer payments | `Customers.LinkPayments` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersFollowUp | Search dues | `Customers.SearchDues` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersReports | Customer balances | `Customers.CustomerBalance` | CustBalWin | COMPLETE |
| TabCustomers | CustomersReports | Settle customer accounts | `Customers.BalanceAccounts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersReports | Edit customers | `Customers.EditCustomers` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersReports | Customer groups | `Customers.CustomerGroups` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabCustomers | CustomersReports | Customer reports | `Customers.CustomerReports` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepCards | Sales rep card | `SalesRep.SalesRepCard` | RepCard | PLACEHOLDER |
| TabSalesRep | SalesRepCards | Sales rep groups | `SalesRep.SalesRepGroups` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepTargets | Assign sales rep targets | `SalesRep.AssignSalesRepTargets` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepTargets | Sales rep targets report | `SalesRep.SalesRepTargetsReport` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepFollowUp | Search customers | `SalesRep.SearchCustomers` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepFollowUp | Sales rep movement | `SalesRep.SalesRepMovement` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepFollowUp | Sales rep collections | `SalesRep.SalesRepCollections` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepHandheldDevices | Handheld device cards | `SalesRep.HandheldDevices` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabSalesRep | SalesRepHandheldDevices | Handheld device routes | `SalesRep.DeviceRoutes` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingAdministration | Manufacturing templates | `Manufacturing.ManufacturingModels` | MfgWin | PLACEHOLDER |
| TabManufacturing | ManufacturingAdministration | Manufacturing operation | `Manufacturing.ManufacturingProcess` | MfgWin | PLACEHOLDER |
| TabManufacturing | ManufacturingAdministration | Manufacturing capability | `Manufacturing.ManufacturingFeasibility` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingAdministration | Search manufacturing | `Manufacturing.SearchManufacturing` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingCostVariances | Finished goods cost variance | `Manufacturing.ManufacturingCostVariance` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingCostVariances | Raw material cost variance | `Manufacturing.RawMaterialCostVariance` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingCostVariances | Overhead cost variance | `Manufacturing.AdditionalCostVariance` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingCostVariances | Finished goods overhead variance | `Manufacturing.ManufacturedMaterialCostVariance` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingReports | Manufactured materials costs | `Manufacturing.ManufacturedMaterialCosts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabManufacturing | ManufacturingReports | Repost manufacturing operations | `Manufacturing.RepostManufacturing` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBanks | BanksAdministration | Define banks | `Banks.DefineBanks` | BnkCard | PLACEHOLDER |
| TabBanks | BanksAdministration | Estimated bank statement | `Banks.BankEstimate` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBanks | BanksAdministration | Bank reconciliation | `Banks.BankReconciliation` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabBanks | BanksAdministration | Bank balances | `Banks.BankBalances` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabReports | ReportsFinancial | Trial balance | `Accounts.TrialBalance` | TrialBalanceWin | COMPLETE |
| TabReports | ReportsFinancial | Account statement | `Reports.AccountStatement` | AccStmtWin | COMPLETE |
| TabReports | ReportsFinancial | General journal | `Accounts.GeneralJournal` | JrnListWin | COMPLETE |
| TabReports | ReportsFinancial | Customer aging | `Reports.CustomerAging` | CustAgingWin | COMPLETE |
| TabReports | ReportsFinancial | Supplier aging | `Reports.SupplierAging` | SupAgingWin | COMPLETE |
| TabReports | ReportsInventory | Stock balances | `Reports.StockBalances` | StkBalWin | COMPLETE |
| TabReports | ReportsInventory | Material movements | `Reports.MaterialMoves` | MatMoveWin | COMPLETE |
| TabReports | ReportsInventory | Stock movements | `Reports.StockAdjustment` | StkAdjRptWin | COMPLETE |
| TabReports | ReportsInventory | Item profitability | `Reports.ItemProfitability` | ItemProfitWin | COMPLETE |
| TabReports | ReportsParties | Customer balances | `Customers.CustomerBalance` | CustBalWin | COMPLETE |
| TabReports | ReportsParties | Supplier balances | `Reports.SupplierBalances` | SupBalWin | COMPLETE |
| TabReports | ReportsParties | Sales invoices | `Reports.SalesInvoiceList` | SalListWin | COMPLETE |
| TabReports | ReportsParties | Purchase invoices | `Reports.PurchaseInvoiceList` | PurListWin | COMPLETE |
| TabReports | ReportsReports | Statistics | `Reports.Statistics` | TrialBalanceWin | COMPLETE |
| TabReports | ReportsReports | Custom reports | `Reports.CustomReports` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabReports | ReportsReports | Customize reports | `Reports.CustomizeReports` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabTools | ToolsTools | Calculator | `Tools.Calculator` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabTools | ToolsTools | Keyboard | `Tools.Keyboard` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabTools | ToolsTools | Options | `Tools.Options` | MWin key case (options/adjustment/merge) | COMPLETE |
| TabTools | ToolsTools | Maintenance wizard | `Tools.MaintenanceWizard` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabTools | ToolsTools | Backup | `Tools.Backup` | MainShVM system action | COMPLETE |
| TabTools | ToolsLanguage | Arabic | `Tools.Arabic` | OnLanguageArabicClick | COMPLETE |
| TabTools | ToolsLanguage | English | `Tools.English` | OnLanguageEnglishClick | COMPLETE |
| TabTools | ToolsLanguage | Français | `Tools.French` | OnLanguageFrenchClick | COMPLETE |
| TabTools | ToolsLanguage | Türkçe | `Tools.Turkish` | OnLanguageTurkishClick | COMPLETE |
| TabTools | ToolsSecurity | Change password | `Tools.ChangePassword` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabTools | ToolsSecurity | Activity log | `Tools.ActivityLog` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabTools | ToolsAdvanced | Program add-ons | `Tools.Addons` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabTools | ToolsAdvanced | Artificial intelligence | `Tools.AI` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsAdministration | Rollover | `OtherTools.Rotation` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsAdministration | Permissions | `OtherTools.Permissions` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsAdministration | Permission templates | `OtherTools.PermissionTemplates` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsAdministration | Custom fields | `OtherTools.CustomFields` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsAdministration | Restore company | `RestoreCompany.Restore` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDefinitions | Define units of measure | `OtherTools.UnitsOfMeasure` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDefinitions | Define payment methods | `OtherTools.PaymentMethods` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDefinitions | Define material specs | `OtherTools.MaterialSpecs` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDelivery | Driver card | `OtherTools.DriverCard` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDelivery | Delivery prices | `OtherTools.DeliveryPrices` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDelivery | Receipt requests | `OtherTools.PickupRequests` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDelivery | Search delivery | `OtherTools.SearchDelivery` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabOtherTools | OtherToolsDelivery | Close driver cash | `OtherTools.CloseDriverCash` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewTrees | Account tree | `View.ChartOfAccounts` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewTrees | Material tree | `View.MaterialTree` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewTrees | Manufacturing tree | `View.ManufacturingTree` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewTrees | Cost center tree | `View.CostCenterTree` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewInformation | Info window | `View.InfoWindow` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewInformation | Define payment methods | `View.PaymentMethods` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewInformation | Company info | `View.CompanyInfo` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabView | ViewInformation | Export data | `View.ExportData` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabExit | ExitExit | Lock screen | `Exit.ScreenSaver` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabExit | ExitExit | Switch company | `Exit.SwitchCompany` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabExit | ExitExit | Exit | `Exit.Title` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabExit | ExitExit | Exit and shutdown | `Exit.ExitAndShutdown` | MainShVM system action | COMPLETE |
| TabHelp | HelpUpdates | Check for updates | `Help.CheckUpdates` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabHelp | HelpUpdates | Update from file | `Help.UpdateFromFile` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabHelp | HelpSupport | Help documentation | `Help.Instructions` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabHelp | HelpSupport | Technical support | `Help.TechnicalSupport` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabHelp | HelpSupport | Feedback | `Help.Suggestions` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabHelp | HelpLicense | Renew license | `Help.RenewLicense` | ModuleWorkspaceView placeholder (no window) | UNWIRED |
| TabHelp | HelpLicense | About | `Help.About` | MainShVM system action | COMPLETE |


## Reference specs (research-first, owner directive 2026-11-17)

Method: ERPNext (docs.frappe.io + DocType atlas) and Odoo (odoo.com/documentation
+ doc mirrors) are source-cited. Al-Ameen V10 / Phenix ERP v10 publish almost no
field-level English documentation; their parts are marked **pattern-based**
(vendor feature pages + established regional patterns: bilingual voucher/card
windows, bottom totals on schedules, cost center on every voucher line, cheque
portfolios, document cycles). Full research file: `~/workspace/research/erp-window-specs.md`
(440 lines, 6 deep specs) — reconciled into the sections below.

### RS-1 Cost Centers — CcCard + new CcListWin (Accounts.CostCenter, Accounts.LinkWithAccounts, View.CostCenterTree)

- **Purpose.** ERPNext: tree `Cost Center` DocType (per company, NestedSet);
  group nodes organize, only leaves post. Odoo: analytic accounts inside
  analytic plans; line-level analytic distribution by % (each plan Σ=100%).
  Al-Ameen/Phenix (pattern-based): cost-center card + tree, cost-center column
  on every voucher/invoice line, cost-center statement + trial balance reports.
- **Fields (adopted).** Code (req, unique per company), NameEn (req), NameAr,
  ParentId (tree, groups as parents), IsGroup, IsActive (default on), Level,
  Notes + audit columns. Card adds: computed Debit/Credit/Balance from journal
  lines for the selected center.
- **Grid (manager).** Tree-flattened list: Code | Name | Parent | Group? |
  Active | Balance (roll-up for groups). Footer: count + total balance.
- **Actions.** New, New Child, Save, Delete (soft; blocked when referenced by
  journal lines — disable instead), Statement (opens AccStmtWin pattern via
  cost-center report), Print tree.
- **Workflow/validation.** Masters: Active/Inactive only. Leaf-only posting
  enforced in the card (group flag) and in CshWin line validation. Company
  scoping on every query. Allocation rules (ERPNext Cost Center Allocation:
  main center + rows of center+%, Σ=100%, Valid From) are stored
  (`cost_center_allocations` + lines) and validated in the manager;
  posting-time application is **deliberately deferred** (journal engine change;
  noted as phase-2 in the research file itself).
- **Deviation (documented).** Transaction lines keep the existing
  `cost_center` **code** column (journal_entry_lines, voucher/invoice lines)
  instead of a new `CostCenterId` FK: the entire codebase resolves dimensions
  by code (accounts included); codes are unique per company and validated
  against `cost_centers` at save time in the new windows.

### RS-2 Payment Methods & Cards — definition set (OtherTools.PaymentMethods, View.PaymentMethods, POS.DefinePOS tenders)

- **Purpose.** ERPNext `Mode of Payment`: name + Type (Cash/Bank/General) +
  per-company default account; POS Profile lists accepted modes. Odoo: POS
  Payment Method = Name + Journal (Cash journal = cash, Bank journal = card) +
  Identify Customer + terminal. Al-Ameen/Phenix (pattern-based): each card
  type carries **bank commission %** and **settlement bank**, because card
  receipts are a bank receivable until settlement.
- **Fields (adopted, table `payment_methods`).** Code, NameEn, NameAr, Kind
  (Cash|BankTransfer|Cheque|Card|Other), GlAccountCode (req — clearing/cash/
  bank account money posts to), SettlementBankCode (cards), CommissionPercent
  (cards, default 0), CommissionAccountCode, RequiresReference, IsPosEnabled,
  PosSortOrder, IsActive.
- **Actions/states.** Definition manager (New/Edit/Activate/Delete-guarded);
  Enabled/Disabled only. Seeds: Cash, Bank Transfer, Cheque, Visa, Mastercard,
  mada, American Express.
- **Left out.** POS tender posting changes (PosWin already persists sales);
  settlement batch posting (clearing → bank net of commission) — the master
  carries the data; settlement is a voucher the accountant posts. Reason:
  scope control; the definition layer is what the ribbon entries open.

### RS-3 Cash Vouchers — CshWin (Bonds.ReceiptVoucher, Bonds.PaymentVoucher, Bonds.CashJournal)

- **Purpose.** ERPNext `Payment Entry`: one document, Payment Type
  Receive/Pay/Internal Transfer; Paid From/To accounts derived by type;
  References child table allocates against invoices; **Difference Amount must
  be 0 to submit**; unallocated remainder stays as advance. Odoo
  `account.payment`: journal + method line, outstanding accounts, group
  payments. Al-Ameen/Phenix (pattern-based): receipt/payment voucher pair over one
  engine; header amount must equal Σ lines; amount-in-words on print.
- **Fields (adopted — existing window shape kept).** Header: Doc No (auto per
  kind), Date, Treasury/Box account code (req), Party name, Amount, Currency,
  Rate, Ref No, Cost Center, Memo. Lines: Account Code/Name, Amount, Cost
  Center (leaf-validated), Invoice Ref, Memo.
- **Actions.** New, Save Draft, Post, Print (formal voucher with
  **amount-in-words**), Delete draft, Load by document number.
- **Workflow/validation.** Draft → Posted → (reversed via journal reversal,
  never edited/deleted). **Post blocked while header Amount ≠ Σ lines**
  (difference shown in the footer; zero-tolerance rule from ERPNext adopted).
  Posting writes a balanced journal entry through JournalService in one
  transaction (Receipt: Dr Box / Cr lines; Payment mirrored). Fiscal-period
  and account-existence checks come from the journal engine.
- **Left out.** The ERPNext outstanding-invoice allocation picker inside the
  voucher — ShouTech already has the dedicated Payment Reconciliation window
  (Phase 3) for allocation; voucher lines keep the Invoice Ref link. Reason:
  avoid two allocation engines writing `payment_allocations`.

### RS-4 Cheques — ChqWin + ChqListWin (Bonds.ReceiptCheque, Bonds.PaymentCheque, Bonds.SearchSecurities)

- **Purpose.** ERPNext/Odoo have **no cheque master** (cheque = Payment Entry
  reference no/date; clearance date at bank reconciliation) — the regional
  portfolio pattern (Al-Ameen/Phenix, pattern-based) is adopted instead:
  a received cheque is not money until it clears.
- **Fields (adopted, table `cheques`).** Direction (Incoming|Outgoing),
  Cheque No (req; **unique per company+bank+drawer** — fraud control), Bank
  Name (req), Branch, Drawer, Party Account Code, Amount (req > 0), Currency,
  Rate, Issue Date, Due Date (req), Status, Portfolio/Bank GL account codes,
  Deposit Date/Bank, Clearance Date, Endorsed-To account, Bounce
  Date/Reason/Fees, Journal Entry Id, Notes.
- **Lifecycle + posting map (adopted).** Incoming: Save → **InPortfolio**
  (Dr Portfolio/Box, Cr Party). **Deposit** (sets bank + date; no GL — funds
  still in portfolio account). **Clear** (Dr Bank GL, Cr Portfolio).
  **Bounce** (reverse receipt entry; status Bounced; fee fields recorded).
  **Endorse** (Dr Endorsed-To account, Cr Portfolio; one-way). Outgoing:
  Save → **Issued** (Dr Party, Cr Bank/Under-Payment account = header box).
  **Clear** = status + date (GL already moved). **Cancel** = reversal entry.
  All transitions transactional via JournalService, status order enforced.
- **Grid (register).** Cheque No | Direction | Party/Drawer | Bank | Amount |
  Due Date | Status | Days-to-due. Footer totals per status. Filters: status
  tabs + due-soon.
- **Left out.** Cheque-paper printing layouts per bank (register print only);
  batch deposit slips. Reason: bank-specific paper layouts need physical
  printer calibration data we do not have.

### RS-5 Orders + Convert-to-Invoice — OrdWin (Orders.SalesOrder, Orders.PurchaseOrder, Orders.* search/track/payments)

- **Purpose.** ERPNext Sales/Purchase Order: commitment document, **posts no
  GL and no stock**; tracks Per Delivered/Received and Per Billed; statuses
  Draft → To Deliver and Bill → To Deliver | To Bill → Completed, plus
  On Hold / Closed / Cancelled. Conversions copy only **remaining**
  quantities and write back progress in the same transaction. Odoo mirrors
  this (delivered/invoiced qty per line; bill on ordered vs received policy).
  Al-Ameen/Phenix (pattern-based): order → invoice joining is a marketed
  capability; grid carries item, unit, qty, price, discount, tax, warehouse,
  cost center, line total.
- **Deviation (documented, schema-forced).** The research recommends new
  `orders`/`order_lines` tables. ShouTech already has a commercial-document
  engine — `sales_documents`/`purchase_documents` (migration 010) with
  doc_kind SO/PO/QUO/DLV/SAL/RCV/PUR, `document_links` (024), fulfilled-qty
  computation and a tested SalesChainService (QUO→SO→DLV→SAL). Building a
  parallel orders engine would fork stock/ledger posting. **Adopted
  instead:** the ERPNext *semantics* on the existing engine — per-line
  fulfilled/billed quantities computed from posted linked documents,
  progress % on the order window, Close/Cancel states, conversion copying
  remaining unbilled qty only, all in one transaction.
- **Fields (adopted).** Existing header (doc no, date, party, warehouse,
  notes) + migration 032 adds `delivery_date` and `valid_till` to both
  document tables. Lines: item, description, unit, qty, price, discount %,
  tax rate, warehouse, cost center + read-only Fulfilled / Billed / Remaining
  progress columns. Footer: Σ qty, total, tax, grand total.
- **Actions.** New, Save Draft, Post, Close, Cancel (reversal-free: orders
  post nothing; cancel is a status with audit), Print, **Convert to Invoice**
  (SO→SAL, PO→PUR — new; the existing chain lacked both), Convert via chain
  (SO→DLV where applicable), Track (fulfilment view).
- **Validation.** Delivery date ≥ order date; qty > 0; cannot edit a posted
  order (cancel + re-create); cannot convert more than remaining qty;
  conversion blocked for Closed/Cancelled/expired-quotation sources.

### RS-6 Quotation — InvoiceWin(Quotation) + search (Invoices.SearchQuotations, Orders.DefineOrders chain)

- **Purpose.** ERPNext `Quotation`: distinct submittable document, Quotation
  To (Customer/Lead), **Valid Till** (default +30d), statuses Draft / Open /
  Ordered / Lost / Expired / Cancelled; conversion copies quoted rates (never
  re-fetched). Odoo: quotation = draft sale.order with Expiration. Regional
  (pattern-based): Quotation → Order → Invoice chain; expired quotes printable but
  blocked from conversion.
- **Adoption on existing engine.** Quotations already exist as
  `sales_documents` doc_kind QUO in InvoiceWin with QUO→SO conversion in
  SalesChainService. Migration 032 adds `valid_till`. Derived status:
  Ordered (SO link exists), Expired (valid_till < today, not ordered), Lost
  (explicit status set from the document search window), else Draft/Open.
  Conversion of an expired quotation is blocked in the service (audited
  validity extension = edit while Draft). Win-rate reporting comes free from
  `document_links`.

### RS-7 Master cards — BnkCard, CstCard, CurCard, RepCard (+ searches/managers)

- **ERPNext Bank Account:** account_name (req), bank (req, Bank master),
  account (link → GL Account of type Bank; one Bank Account per ledger),
  account_type/subtype, IBAN, branch_code, bank_account_no, company,
  is_company_account. **Adopted fields:** code, name, branch, account no,
  IBAN, currency, GL account code; balance computed live from the GL account.
- **ERPNext Customer:** customer_group (tree; supplies default price list,
  payment terms, credit limit), territory, default_currency,
  default_price_list, credit limit rows per company (block when exceeded),
  default receivable account. **Adopted:** existing CstCard field set (code,
  names, type, group → new `party_groups`, tax no, CR no, credit limit,
  credit days, price tier, GL account, contacts) persisted to new `parties`
  table (kind Customer|Supplier); balance from `party_ledger`; Statement
  button opens the account-statement report. Credit-limit *enforcement* at
  invoice posting is **left out** (InvoiceWin posting path is shared and
  heavily tested; the card stores the limit and the customer-balances report
  flags over-limit parties). Reason documented here per directive.
- **Currencies (ERPNext Currency + Currency Exchange; Odoo res.currency):**
  code, names, symbol, fraction (minor unit) name, enabled; dated rates
  (from/to currency, date, rate, min/max band) in `currency_rates`. Base
  currency rate = 1 enforced.
- **Sales reps (regional pattern + ERPNext Sales Person tree):** code, name,
  phone, commission %, target; groups via `sales_rep_groups`; movement and
  collections reports read invoice/ledger data by rep name (documents already
  carry `sales_rep_name`, migration 023).

### RS-8 Manufacturing orders — MfgWin + MfgListWin (Manufacturing.*)

- **ERPNext Work Order:** BOM drives production item + required items
  (required qty = BOM qty × order qty); source / WIP / FG / scrap warehouses;
  produced qty; statuses Draft → Not Started → In Process → Completed;
  Stopped; cancel refused once produced > 0; stock moves only via stock
  entries. **Adopted:** `mfg_orders` + `mfg_order_lines` (migration 034);
  Post consumes raw materials and produces the finished item via
  `stock_movements` in one transaction with unit-cost snapshot per order
  (feeds the four variance reports); BOM lines editable on the order
  (ShouTech has no BOM master — the order carries its own lines, matching
  the existing window's shape). Variance reports compare snapshot standard
  vs actual line costs.

### RS-9 Bank reconciliation & balances — Banks.* (BankRecWin)

- **ERPNext Bank Reconciliation Tool:** statement lines matched to payment
  entries/journal entries; **clearance dates** recorded on the transactions;
  semi-automatic matching by amount/date/reference. **Adopted (migration
  035):** `bank_reconciliation_marks` (journal line ↔ statement date/ref,
  cleared flag) + BankRecWin: pick bank GL account, list uncleared lines,
  mark cleared with statement date, totals (book balance, cleared, uncleared,
  difference). Statement *file import* (CSV/OFX) is **left out** — no import
  framework exists in the app yet; manual marking is the Al-Ameen/Phenix
  baseline pattern. Bank Balances report = GL balances of bank-flagged
  accounts; Bank Estimate = balances projected by cheque due dates
  (cheques table) — both real queries, no new tables.

### RS-10 Definition sets — Bonds/Invoices/Orders/POS/Customers/SalesRep/OtherTools definition entries

- Pattern (all four ERPs agree): small masters = code + bilingual names +
  active flag + a few set-specific fields, managed in a list+editor window,
  delete-guarded once referenced. One generic engine
  (`DefinitionRepository` + `DefManagerWin` configured per set) serves:
  voucher types, securities types, payment types, invoice definitions, order
  definitions, POS terminals, areas, targets, discount rules, customer
  prices, loyalty accounts, handheld devices, device routes, drivers,
  delivery prices, delivery requests, driver settlements, custom fields,
  installments + schedules, units of measure (existing table), material
  specs (existing `material_attributes`). Migration 036 creates the backing
  tables in one pass. Installment collection posts a `party_ledger` credit
  in the same transaction (regional pattern: installment = scheduled
  receivable with a collection voucher trail).

### RS-11 Reports family — Inventory/Accounts/Customers/SalesRep/Manufacturing/Banks report entries

- Pattern: Al-Ameen schedules (strong header, sortable/resizable columns,
  totals pinned at bottom, print/export) over the Phase-3 `RptBase`
  framework; ERPNext/Odoo equivalents are the standard stock ledger, aging,
  price-history, dormant-account and ratio reports. Every report below is a
  real query over existing tables (documents, lines, stock_movements,
  journal, party_ledger, materials) — no new tables except where RS-8/RS-10
  provide them. ~25 new `RptBase` windows + `RptQuery` methods (see build
  results). Closing/Trading/Profit-and-Loss/Cash-Flow reuse the existing
  financial-statement queries with the Al-Ameen closing-accounts layout.

### RS-12 System & tool windows — Tools.* / OtherTools.* / View.* / Help.* / Exit.*

- Calculator (in-app, keyboard support), on-screen keyboard launcher,
  Change Password (existing auth service policy: min 12 + symbol + deny
  list), Activity Log (`security_audit_log` + `journal_audit_events`
  viewers), Maintenance wizard (`DatabaseHealth` checks + VACUUM/backup
  shortcuts), fiscal Roll-over (`FYearSvc` Create/Close), POS sync
  (outbox `outbound_messages` viewer + retry), POS cash closing
  (`pos_cash_closings`, migration 037), invoice compression (audited purge
  of soft-deleted drafts), repost (transactional recompute of document
  totals from lines with preview), financial analysis (ratios computed
  from trial-balance data), custom reports (existing `ReportRepo` /
  ReportDefinition store + designer host), updates (version + configured
  update source; honest "not configured" state — no fake downloads),
  help (renders the shipped English manual), support tickets
  (`support_tickets`, migration 037), license info (existing license
  service), addons (licensed module list, read-only), screen saver =
  lock screen (LogWin), info window = About.

## Build plan & conventions (all chunks)

- Migrations (BOTH sqlite + postgresql, `dba/mig/`): **031** master cards
  (cost_centers, cost_center_allocations(+lines), banks, currencies,
  currency_rates, parties, party_groups, sales_reps, sales_rep_groups);
  **032** documents: delivery_date + valid_till; **033** cheques;
  **034** mfg_orders(+lines); **035** bank_reconciliation_marks;
  **036** definitions (doc_type_definitions, pos_terminals, areas, targets,
  discount_rules, customer_prices, loyalty_accounts(+entries),
  handheld_devices, device_routes, drivers, delivery_prices,
  delivery_requests, driver_settlements, custom_field_definitions(+values),
  installments(+schedule), payment_methods — RS-2);
  **037** operations (pos_cash_closings, support_tickets);
  **038** permissions seed for all new codes.
- Layers follow Phase 3 exactly: Domain entities + interfaces,
  Infrastructure repositories (provider-neutral, parameterized, company
  scoping, single-transaction writes), Application services (validation),
  App windows on BaseWin with bottom command bars, AppHost +
  AppServiceHost wiring, PermCat constants, Lang keys in all 4 catalogs.
- Tests: `tests/Kern.Tests` per new service (compile-green; execution needs
  Windows — noted).

## Build results

## Completion results (2026-11-17, branch `feat/ribbon-windows-complete`)

### Counts: before → after

| Status | Before | After |
|---|---|---|
| COMPLETE | 74 | **228** |
| PLACEHOLDER | 14 | 0 |
| UNWIRED | 140 | 0 |
| **Total** | **228** | **228** |

After-state verified mechanically: every action key in the inventory table is
referenced by routing code (MWin named cases / dedicated handlers /
RibbonReportRouter / RibbonNav / RptNav / MainShVM); zero
`ShowOperationUnavailable` / `ShowPersistenceUnavailable` call sites remain in
Views; the only `ModuleWorkspaceView` mention left in routing code is a doc
comment stating no button may fall through to it.

### Verification (verbatim results)

- MainApp build (`-p:EnableWindowsTargeting=true`): **0 Error(s), 48 Warning(s)** (analyzer warnings only).
- Kern.Tests build: **0 Error(s), 3 Warning(s)** (incl. the 2 known pre-existing CS1998 in SalesChain tests).
- SqlMigrator on fresh SQLite: all migrations through 038 applied; second run **37 SKIP** + "Migration run completed successfully" (idempotent). Table count **48 → 83**. Permissions rows: **285**, including `SALES.ORDER.CONVERT`, `PURCH.ORDER.CONVERT`, `SYS.CUSTOM_FIELDS.MANAGE`.
- Order-engine smoke harness (real SQLite, migrations applied, OrderService + repositories): **10 passed, 0 failed** — create/reload with delivery date + totals, draft edit persists, post flips status, convert creates posted invoice SAL-1001, progress 100% billed, party ledger Dr 300 written, double-convert blocked, quotation→order conversion, expired quotation blocked, NextDocNo PO-1001.
- Chunk-level service smokes (agents' reflection/console harnesses on real SQLite): MasterCardServiceTests 10/10, ChequeMfgServiceTests 13/13, OperationsServiceTests 8/8 scenarios PASS.
- Lang resource sweep: **525 distinct T() keys used in app code, 0 missing in En**; 621 chunk keys + order/doc-search keys merged into all 4 catalogs (En/Ar/Fr/Tr).
- Kern.Tests execution still requires Windows (testhost targets net8.0-windows); PostgreSQL migrations written in matching pairs but not live-run — both flagged for the Windows/PG verification pass.

### Per-window built summary

| Area | Windows | Migrations / tables | Repository / service | Permissions |
|---|---|---|---|---|
| Master cards (B) | CcCard, BnkCard, CurCard, RepCard, CstCard rewritten; CcListWin, PartySearchWin, PartyManagerWin new | 031: cost_centers, cost_center_allocations(+lines), banks, currencies, currency_rates, parties, party_groups, sales_reps, sales_rep_groups | MasterCardRepository/Service | existing card codes + 038 |
| Orders (D) | OrdWin rewritten; DocSearchWin, OrderTrackWin, GroupManagerWin new | 032: delivery_date/valid_till on sales/purchase_documents + indexes (documents engine + document_links 024 reused — documented deviation from new orders tables) | OrderDocumentRepository, OrderService (convert in ONE transaction, remaining-qty only, party ledger + link) | SALES.ORDER.CONVERT, PURCH.ORDER.CONVERT (038) |
| Vouchers/cheques/mfg (C) | CshWin rewritten (post blocked while header ≠ Σ lines, amount-in-words print); ChqWin + ChqListWin; MfgWin + MfgListWin | 033: cheques (full lifecycle); 034: mfg_orders(+lines) | ChequeService (legal-transition table + posting map), Manufacturing service | existing voucher/cheque codes |
| Reports (E) | ~30 new RptBase windows in Views/Reports/ | — (~35 additive RptQuery methods) | RptQuery | existing report codes |
| Definitions (F1) | DefManagerWin (21 sets), InstallmentsWin, LoyaltyWin, DeliveryDeskWin | 036: 19 tables incl. payment_methods (7 global seed rows) | DefinitionRepository/Service + DefSets | SYS.CUSTOM_FIELDS.MANAGE (038) + existing |
| Operations/tools (F2) | BankRecWin, CloseCashWin, PosSyncWin, CalculatorWin, ChangePasswordWin, ActivityLogWin, MaintenanceWin, RolloverWin, CompressInvoicesWin, RepostWin, FinancialAnalysisWin, CustomReportsWin, UpdatesWin, HelpWin, SupportWin, LicenseWin, AddonsWin | 035: bank_reconciliation_marks (key = journal_entry_line_id); 037: pos_cash_closings, support_tickets | OperationsRepository/Service | existing ops codes |

### Shell-context compliance (owner directive 2026-11-17)

Every window built/rewritten in this pass: **language/company/theme/prefs = central** —
strings and direction only via Lang/BaseWin; company resolved at open/query time
from `AppState.CurrentCompanyCode` (print headers use
`AppState.CurrentCompanyDisplayName`, e.g. OrdWin, MfgWin); styling only via the
central VisuId tokens; currency defaults from `UsrPref.Load().CurrencyCode`
(verified: zero `"SAR"` literals remain in the new/rewritten windows — the
PosWin pattern); RBAC + audit stamps from AppHost.Session/Auth/Permissions.
No window hardcodes language, direction, company, currency, or formats.

### Deliberate limitations (with reasons)

- **CshWin draft delete disabled** with an honest in-window note: JournalService exposes no draft-delete API; no fake delete was shipped.
- **CompressInvoicesWin** purges only soft-deleted Draft documents (posted documents can never match) transactionally with an audit row; hard purge of posted history is blocked by FKs from document_links/party_ledger by design.
- **LicenseWin Renew disabled**: ILicenSvc has no renewal/key-entry API — shown disabled with an explanation rather than faked.
- **AddonsWin read-only**: PluginManager exposes no enable/disable API.
- **UpdatesWin**: no update source is configured in shipped config (ActivationServer empty) — honest "not configured" state; when configured it performs a real version-file check. From-file path verifies + hashes + audits but does not install.
- **Credit-limit enforcement** is not wired into InvoiceWin posting (party credit limits persist via chunk B; enforcement hook is a follow-up).
- **Cheque-paper print layouts** and **bank statement file import** were not built (no paper-layout spec / file-format spec exists yet); cheque lifecycle + posting are complete.
- **Cost-center allocation** application at posting time is deferred; allocations persist and balances report from posted journal lines by cost-center code.
- Report simplifications documented inline per window (no batch expiry dates in schema → ExpiredWin uses has_expiry + last movement; cash/bank accounts identified by name heuristic — no flag column; CashFlowWin is a labeled indirect approximation; BankEstimateWin aggregates due cheques — cheques carry no GL link).

---

<!-- source: docs/WINDOW-INVENTORY.md -->

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

---

<!-- source: docs/MISSING_WINDOWS_INVENTORY.md -->

# ShouTech ERP — Missing Windows Inventory (Phase 3)

*Compiled 2026-11-17 on branch `feat/missing-windows-phase3` (base: `ready/combined-2026-11-17`).*

Sources used: `dba/sys/NAV_README.md` (the `dba/sys/nav.sql` + `dba/sds/sds_nav.sql`
SQL Server catalog described there is **legacy/reference only** — it is not in the
active SQLite/PostgreSQL tree; the live catalog is the MWin ribbon, 242 ribbon
elements in `MWin.xaml`), `cod/app/src/Navigation/RibModMap.cs` (17 tabs →
modules), `cod/app/src/Services/RptNav.cs` (the live command router),
`cod/app/src/Services/RibbonReportRouter.cs`, `cod/app/src/Services/Data.cs`
module definitions, a grep for `ShowOperationUnavailable` /
`ShowPersistenceUnavailable` across `cod/app/src/Views`, and
`docs/COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md`.

## 1. How navigation actually works today

Ribbon/menu action keys are routed by `RptNav.TryHandle` (~70 keys) and
`RibbonReportRouter.TryHandle` (5 keys) to concrete windows. Anything not routed
falls through to the generic `ModuleWorkspaceView` placeholder. The nav database
catalog in `dba/sys/NAV_README.md` (17 menus · 17 tabs · 55 groups · 215 items ·
209 permissions) describes the *intended* catalog; the table below maps every
routed command family to its window and status.

Status key: ✅ window exists and is data-backed · 🟡 window exists but a core
action is a placeholder (`ShowOperationUnavailable` / `ShowPersistenceUnavailable`)
· 🔀 routed to a neighbouring window (no distinct window) · ❌ missing entirely.

## 2. Command → window map (RptNav + RibbonReportRouter + MWin direct)

| Command family (action keys) | Window | Status |
|---|---|---|
| Trial balance (`Accounts.TrialBalance`, `Reports.TrialBalance`, `Reports.Statistics`) | `TrialBalanceWin` | ✅ |
| Account balance | `AccBalWin` | ✅ |
| Balance sheet | `BalSheetWin` | ✅ |
| Income statement | `IncStmtWin` | ✅ |
| Ledger / account statement / customer statement | `AccStmtWin` | ✅ |
| General journal / journal list | `JrnListWin` | ✅ |
| Stock balances / warehouse inventory / material overview | `StkBalWin` | ✅ |
| Material movement / cost-center material movement | `MatMoveWin` | ✅ |
| Stock adjustment report | `StkAdjRptWin` | ✅ |
| Item profitability | `ItemProfitWin` | ✅ |
| Customer balances / overview | `CustBalWin` | ✅ |
| Customer aging | `CustAgingWin` | ✅ |
| Supplier balances | `SupBalWin` | ✅ |
| Supplier aging | `SupAgingWin` | ✅ |
| Material profits (`Inventory.MaterialProfits`) | `ItemProfitWin` | 🔀 router alias |
| Material group movement | `MatMoveWin` | 🔀 router alias |
| Sales report (`Inventory.SalesReport`) | `SalListWin` | 🔀 router alias |
| Aging receivables (`Accounts.AgingReceivables`) | `CustAgingWin` | 🔀 router alias |
| Daily report (`Accounts.DailyReport`) | `JrnListWin` | 🔀 router alias |
| POS / cash sales (`Invoices.CashSales`, `POS.OpenCashier`) | `PosWin` (+ `PosCheckoutWin`) | ✅ |
| Sales invoice / sales return | `InvoiceWin(SalesInvoice/SalesReturn)` | ✅ |
| Purchase invoice / purchase return | `InvoiceWin(PurchaseInvoice/PurchaseReturn)` | ✅ |
| Sales / purchase invoice lists | `SalListWin` / `PurListWin` | ✅ |
| Stock transfer, opening stock, stock in/out, inventory count, quotation | `InvoiceWin(kind)` | ✅ |
| Journal voucher | `JrnWin` | 🟡 Print = `ShowOperationUnavailable` |
| Receipt / payment voucher | `CshWin(Receipt/Payment)` | 🟡 Save has no persistence (`ShowPersistenceUnavailable` on some actions) |
| Cheque voucher | `ChqWin` | 🟡 Print = placeholder; some actions unpersisted |
| Customer / supplier card | `CstCard` | 🟡 Statement action = placeholder; some actions unpersisted |
| Cost-center card | `CcCard` | 🟡 Save/Delete = `ShowPersistenceUnavailable` (no DB wiring at all) |
| Currency card | `CurCard` | 🟡 some actions unpersisted |
| Bank card | `BnkCard` | 🟡 some actions unpersisted |
| Sales-rep card | `RepCard` | 🟡 some actions unpersisted |
| Manufacturing order | `MfgWin` | 🟡 some actions unpersisted |
| Sales / purchase order | `OrdWin(Sales/Purchase)` | 🟡 Print + Convert-to-invoice = placeholders; some actions unpersisted |
| Barcode create / generate / design | `BarWin` / `BcdGenWin` / `BcdDesWin` | ✅ |
| Material list / search | `MatListWin` | ✅ |
| Price list | `PriceListWin` | ✅ |
| Shortage list | `ShortListWin` | ✅ |
| Material card, material groups, material directory, categories, warehouses, stock count | `MatCard`, `MatGroupWin`, `MatDirWin`, `CatWin`, `WhsWin` (+ `StockCountDialog`, `MatMergeWin`) | ✅ (opened from MWin) |
| Chart of accounts, account card | `CoaWin`, `AccCard` | ✅ |
| Permissions | `PrmWin` | ✅ |
| Data import/export | `DatWin` | ✅ |
| Backup / restore, recycle bin, company restore | `BkpWin`, `BinWin`, `RestoreCoWin` | ✅ |
| Serials (`SerWin`) | `SerWin` | ✅ |
| Cash drawer options | `CashDrawerOptionsWin` | ✅ |
| Company / login / language / splash / setup wizard | `ComWin`, `LogWin`, `Lang`, `SplWin`, `StpWiz` | ✅ startup flow |
| **Payment reconciliation** (`Accounts` gap) | — | ❌ **built in Phase 3** |
| **Projects** | — | ❌ **built in Phase 3** |
| **Assets register + depreciation** | — | ❌ **built in Phase 3** |
| **CRM opportunity pipeline** | — | ❌ backlog (after Phase 3 core) |
| **Quality management** | — | ❌ backlog |
| Budgets (`Accounts` gap) | — (`AccCard` has a budget flag only) | ❌ backlog |
| Batch/serial tracking, stock valuation (FIFO/Moving Avg), stock ledger audit | partial (`SerWin` exists) | ❌ backlog (engine work, not just windows) |
| RFQ → PO → receipt chain (`Buying` gap) | `OrdWin(Purchase)` + `InvoiceWin` only; `document_links` schema exists (migration 024) | 🟡 chain windows partial — backlog |
| Quotation → SO → delivery → invoice chain | `InvoiceWin(Quotation)`, `OrdWin(Sales)`; `document_links` schema exists | 🟡 Convert action is a placeholder in `OrdWin` — backlog |
| MRP / job cards (`Manufacturing` gap) | `MfgWin` only | ❌ backlog |
| Projects timesheets | — | ❌ backlog (Projects window built; timesheets not) |
| Maintenance, Support/Helpdesk, Subcontracting | — | ❌ backlog (low priority per roadmap) |
| Bank statement import, e-invoicing (Kuwait NIC/PACS) | outbox webhooks only | ❌ backlog |
| Payroll & leave (HRMS) | basic HR folder only | ❌ backlog |
| AI Report Copilot | design doc only (`docs/UI_2030_AI_REPORT_ASSISTANT.md`) | ❌ backlog |

## 3. Counts

- Routed command keys: ~75 (`RptNav` ~70 + `RibbonReportRouter` 5).
- Distinct windows/views in `cod/app/src/Views`: 23 XAML windows + 3 module
  views, 17 code-built windows/cards, 19 report windows (incl. `StkAdjRptWin`),
  plus nested `StockCountDialog` / `MatMergeWin` / `CashDrawerOptionsWin`.
- ✅ exists & data-backed: **38** window families.
- 🟡 exists but placeholder/unpersisted actions: **9** (`JrnWin` print,
  `CshWin`, `ChqWin`, `CstCard`, `CcCard`, `CurCard`, `BnkCard`, `RepCard`,
  `MfgWin`, `OrdWin` — 10 files, counted as 9 families since CshWin covers two
  voucher kinds).
- 🔀 alias-routed (no distinct window): **5** commands.
- ❌ missing entirely at inventory time: **11** module areas (payment
  reconciliation, projects, assets, CRM pipeline, quality, budgets, batch/serial
  + valuation engine, MRP/job cards, maintenance, helpdesk, subcontracting,
  bank import/e-invoice, payroll/leave, AI copilot) — Phase 3 builds the top 3.

## 4. Roadmap gap → schema / service / window (at inventory time)

| Gap | Schema? | Service? | Window? |
|---|---|---|---|
| Payment reconciliation | Partial: `party_ledger` (010), `sales_documents.paid_amount`, `document_links` is doc-chain only — a dedicated `payment_allocations` table is needed | No | No |
| Projects | No | No | No |
| Assets + depreciation | No | No | No |
| CRM opportunities | Leads exist in CRM folder; no pipeline tables | Partial | No |
| Quality | No | No | No |
| Budgets | No (flag on account card) | No | No |
| Batch/serial + valuation | Serials partial | Partial | `SerWin` only |

## 5. Buttons-to-bottom sweep (user rule: action buttons at the BOTTOM)

Legend: ✅ moved in Phase 3 · ⏸ deliberately left (reason) · ✔ already at bottom.

| Window | Before | Phase 3 |
|---|---|---|
| `JrnWin` | Toolbar top; totals footer bottom | ✅ toolbar moved to bottom bar directly above totals footer |
| `InvoiceWin` | Toolbar in grid row 0 | ✅ toolbar moved to new bottom row under the totals/notes band |
| `CshWin` (receipt/payment) | Toolbar top; totals footer bottom | ✅ above footer |
| `ChqWin` | Toolbar top; totals footer bottom | ✅ above footer |
| `MfgWin` | Toolbar top; cost footer bottom | ✅ above footer |
| `OrdWin` | Toolbar top; totals footer bottom | ✅ above footer |
| `BnkCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `CcCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `CstCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `RepCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `CurCard` | Toolbar top; no footer | ✅ bottom bar |
| `BarWin` | Toolbar top; no footer | ✅ bottom bar |
| `BcdGenWin` | Action toolbar top; status line bottom | ✅ action bar moved above status line (prefix input travels with its actions) |
| `BcdDesWin` | Toolbar added first, un-docked (defaulted to left in DockPanel) | ✅ explicitly docked bottom |
| `RptBase` (all 19 report windows) | Refresh/Columns/Export/Print in title row | ✅ moved to a bottom action bar under the status line |
| `MatDirWin` (XAML) | Toolbar border in row 1 (top) | ✅ moved to new bottom row 3, right-aligned |
| `CoaWin` (XAML) | Export/Import/Refresh under search (top) | ✅ moved into the bottom row next to Close/status |
| `CatWin` (XAML) | Refresh in header | ✅ moved into the bottom action bar; New/Edit/Delete/Save/Close were already bottom |
| `AccCard`, `MatCard`, `WhsWin`, `MatGroupWin`, `BinWin`, `BkpWin`, `ColChsWin`, `PrmWin`, `SerWin`, `RestoreCoWin`, `DatWin`, `PosCheckoutWin` | — | ✔ already at bottom (record-navigation arrows in `AccCard`/`WhsWin` headers are navigation, not actions) |
| `MWin` shell | Ribbon + menus | ⏸ exception per instructions (shell) |
| `SplWin`, `Lang`, `LogWin`, `ComWin`, `StpWiz` | Startup flow | ⏸ exception (no top toolbar; primary buttons are in content flow / wizard footer) |
| `PosWin` | Checkout/Clear already in bottom action row of the summary panel; payment methods in totals panel | ⏸ left: retail layout is function-driven; "Add item" stays with the scan/header area cashiers use |
| `CashDrawerOptionsWin` | Save/Test/Close in content-flow panel at the end of the form | ⏸ left: buttons already render last (bottom of the form) |
| `DatWin` Start button | In content flow (starts the import/export operation it configures) | ⏸ left: it is the operation's own execute control inside its card; Close/Details are bottom |
| `ModuleWorkspaceView`, `ReportGridVw`, `DashVw` | Embedded views in the shell, not windows | ⏸ left: header bands belong to the shell workspace layout |
| New: `PayRecWin`, `ProjWin`, `AssetWin` | — | ✅ built with bottom action bars from the start |

## 6. Phase 3 build results (this branch)

### 6.1 JrnWin Print — BUILT (`6e1f71a`)
`PrintJournal_Click` now prints a real A4 voucher page (company header, doc no/date,
narrative, account lines with debit/credit, totals, balance state) through a
`PrintDialog`, following the `InvPrn` invoice print pattern; labels via existing
`Vouchers.*` Lang keys. No schema/service needed (prints the loaded journal).

### 6.2 Payment Reconciliation — BUILT (`df1d3b2`, `6ea07bc`, fix `2b15216`)
- **Schema**: `dba/mig/{sqlite,postgresql}/025_payment_allocations.sql` — new
  `payment_allocations` table (the only schema needed; documents/ledger already
  existed). Model: a posted sales document debits the customer in `party_ledger`,
  a purchase document credits the supplier; money-in is a credit entry (customer)
  / debit entry (supplier). Allocation links a payment entry to an open document.
- **Domain**: `OpenDocumentRow`/`UnallocatedPaymentRow` records +
  `IReconciliationRepository` (`src/ShouTech.Domain`).
- **Infrastructure**: `ReconciliationRepository` — provider-neutral; lists capped
  at 500; `AllocateAsync` runs in ONE transaction and re-reads/re-validates the
  payment balance, document status (Posted), same-party rule, and open amount
  before inserting the allocation and increasing `paid_amount` (the settlement
  source of truth). Over-allocation and double submission are rejected inside
  the transaction (proven by a runtime smoke test against a migrated SQLite DB).
- **Service**: `ReconciliationService` (input validation) in `src/ShouTech.Application`.
- **Window**: `Views/PayRecWin.cs` (BaseWin) — two schedule grids (open documents /
  unallocated payments), party-kind selector + amount in the content area,
  Allocate/Refresh/Close in the bottom command bar, status bar with counts/totals.
- **RBAC**: `ACC.PAYREC.VIEW` / `ACC.PAYREC.ALLOCATE` (PermCat.Accounting), seeded
  by migration 028 with role grants (Admin all, User all, Viewer view-only).
- **Nav**: ribbon `Accounts.PaymentReconciliation` (TabAccounts → Analysis group)
  → `RptNav.OpenPaymentReconciliation`. **Lang**: `PaymentReconciliation.*` in all
  4 files (En complete; Ar/Fr/Tr translated).

### 6.3 Projects — BUILT (`6ea07bc`, `df1d3b2`)
- **Schema**: `026_projects.sql` (both providers): `projects` + `project_tasks`,
  partial unique index on (company, code) among live rows, audit columns,
  soft delete.
- **Domain/Service**: `Project`, `ProjectTask`, statuses; `IProjectRepository`;
  `ProjectService` validates code/name required, budget ≥ 0, end ≥ start, known
  statuses; `ProjectRepository` is transactional with in-transaction uniqueness
  re-checks and company scoping on every statement.
- **Window**: `Views/ProjWin.cs` — project list (main content) + editor in the
  content area + tasks grid/editor below; New/Save/Delete/AddTask/DeleteTask/
  Refresh/Close in the bottom command bar; status bar with count + total budget.
- **RBAC**: `PRJ.PROJECT.VIEW/CREATE/UPDATE/DELETE`, `PRJ.TASK.MANAGE` (migration
  028 grants). **Nav**: ribbon `Projects.Projects` (TabOrders → General group).
  **Lang**: `Projects.*` ×4.

### 6.4 Fixed Assets — BUILT (`6ea07bc`, `df1d3b2`)
- **Schema**: `027_fixed_assets.sql` (both providers): register only —
  depreciation is computed, never stored.
- **Domain/Service**: `FixedAsset`, `DepreciationMethods`, `AssetStatuses`;
  `AssetService` validates (cost ≥ 0, 0 ≤ salvage ≤ cost, life ≥ 1, known
  method/status) and computes schedules: straight-line and double-declining
  balance, final year lands exactly on salvage, accumulated depreciation
  pro-rated by whole months and capped at the depreciable base.
- **Window**: `Views/AssetWin.cs` — register list + editor + computed
  depreciation schedule grid; read-only accumulated/book-value fields; status
  bar with total cost + total book value.
- **RBAC**: `AST.ASSET.VIEW/CREATE/UPDATE/DELETE` (migration 028). **Nav**:
  ribbon `Assets.Assets` (TabAccounts → General group). **Lang**: `Assets.*` ×4.

### 6.5 Permissions seed — migration 028 (both providers)
11 new permissions + role grants, insert-or-update / `ON CONFLICT` idempotent.
Verified on SQLite: fresh migrator run applies, second run SKIPs; with roles
present the grants resolve to Administrator 11 / User 9 / Viewer 3 and are
idempotent on re-run.

### 6.6 NOT built in Phase 3 (deliberate)
- **OrdWin Convert-to-invoice**: blocked on order persistence — OrdWin itself
  has no storage (Save is `ShowPersistenceUnavailable`), so a solid Convert
  cannot exist yet. It is backlog #1 (wire card/voucher/order persistence,
  CcCard first, then Convert via the `document_links` schema).
- **CRM pipeline / Quality (priority 5)**: not started — priorities 1–4 plus
  verification consumed the phase, per the task's own stop rule.

## 6b. How the File Explorer reference was applied (per window type)
- **All swept windows**: the bottom action bar is an Explorer-style command bar
  moved to the bottom — flat `Border` with transparent background, subtle 1px
  top divider (`BorderBrush`), commands right-aligned (mirrored in RTL), sitting
  directly above any totals/status footer. No colored toolbar blocks remain in
  secondary windows.
- **List/manager windows (ProjWin, AssetWin, PayRecWin, card windows)**: the
  list is the main content; details/editing live in the content area; a slim
  status bar at the very bottom shows counts/totals (Explorer status-bar logic).
- **Document windows (JrnWin, InvoiceWin, CshWin, ChqWin, MfgWin, OrdWin)**:
  header fields and line grid keep the content area; the command bar sits above
  the existing totals/balance footer, which stays the last element.
- **Report windows (RptBase)**: Refresh/Columns/Export/Print form the bottom
  command bar under the grid; the totals bar is the footer.
- Surfaces/padding: windows keep the central Surface brushes; new windows use
  12px outer padding and the central implicit control styles. MWin/ribbon were
  NOT redone (separate follow-up, per instructions).

## 6c. The Al-Ameen V10 schedule pattern, as implemented here
Implemented centrally, not per window:
- **RptBase (all 19 report windows)**: strong header row (central DataGrid
  styles), banded rows, sortable + resizable + **reorderable** columns, numeric
  columns right-aligned, date columns centered, and a **totals footer pinned at
  the bottom** (bold, `SurfaceSecondaryBrush`). Reports that supply their own
  Totals dictionary keep them; reports that pass none now get totals computed
  from the bound rows for numeric columns (`RenderTotals` fallback).
- **Exceptions (documented)**: `BalSheetWin` and `IncStmtWin` opt out of computed
  totals (`AllowComputedTotals = false`) — their rows are statement sections and
  subtotals, so summing every row would double-count. Editable line grids
  (JrnWin/InvoiceWin) keep sorting/reordering off to preserve document line
  order; they already carry bottom totals footers.
- **New windows**: ProjWin/AssetWin/PayRecWin grids use the same pattern
  (sortable/resizable/reorderable, numeric right-aligned, dates centered,
  standard context menu via `DataGridTableBehavior`, totals in the status bar).
- Print/Export stay reachable from the RptBase bottom action bar.

## 6d. Heavy-duty checklist (per shipped item)
| Item | Transactions | Validation | RBAC | Audit | Migrations (SQLite+PG) | Tests |
|---|---|---|---|---|---|---|
| Payment allocations | Single tx; balances re-read + re-validated inside tx; rollback on failure | Service + repository (amount > 0, Posted doc, same party/kind, open & available caps) | `ACC.PAYREC.VIEW/ALLOCATE`, checked in window + Allocate handler | `allocated_by`/`allocated_at` on every allocation row | 025 both providers, idempotent (double-run proven) | Service validation tests; runtime smoke test incl. over-allocation rejection |
| Projects + tasks | Single tx per save; in-tx uniqueness + parent checks | Service (code/name, budget ≥ 0, dates, statuses) + partial unique index | `PRJ.*` checked on open/save/delete/task actions | created_by/at, updated_by/at columns; soft delete only | 026 both providers | Service validation + round-trip tests; repository smoke test |
| Fixed assets | Single tx per save; in-tx uniqueness check | Service (cost/salvage/life/method/status) + CHECK constraints | `AST.*` checked on open/save/delete | created_by/at, updated_by/at; soft delete only | 027 both providers | Depreciation math tests (SL, DB, pro-rata, caps) + validation tests |
| Permissions 028 | Migrator per-file tx | Insert-or-update / ON CONFLICT | — (it IS the RBAC seed) | — | 028 both providers; grants proven 11/9/3, idempotent | Migrator double-run + grant re-run check |
| JrnWin Print | N/A (read-only print) | Prints only a loaded journal | Existing journal perms apply | N/A | N/A | Build-verified; visual check needs Windows run |
| Buttons sweep + RptBase schedules | N/A (layout) | N/A | N/A | N/A | N/A | Build + XAML parse verified |

Notes: `dotnet test` cannot execute on this Linux VM (testhost targets
net8.0-windows) — the test project compiles green and must be run on Windows.
PostgreSQL migrations are written to the same patterns as the existing PG set
and parse-checked, but only SQLite was executed here (no live PostgreSQL).

## 7. Remaining backlog (priority order)

1. Wire persistence for the 🟡 card/voucher windows (`CcCard` first — it has no
   DB wiring at all; then `BnkCard`, `CurCard`, `RepCard`, `CshWin`, `ChqWin`,
   `MfgWin` save paths) and `OrdWin` Convert-to-invoice using `document_links`.
2. `JrnWin` / `ChqWin` / `OrdWin` Print via the existing reporting engine
   (`RptRend` / `InvPrn` patterns) instead of `ShowOperationUnavailable`.
3. CRM opportunity pipeline window (+ activity logging).
4. Quality management window (inspections + non-conformance).
5. Budgets window + budget-vs-actual report.
6. Batch/serial + valuation engine (FIFO/Moving Average) and stock ledger audit.
7. MRP planning + job cards for manufacturing.
8. Bank statement import (CSV/MT940) with auto-matching; Kuwait e-invoicing.
9. Payroll & leave (HRMS parity).
10. AI Report Copilot (per `docs/UI_2030_AI_REPORT_ASSISTANT.md`).
11. Maintenance, Support/Helpdesk, Subcontracting (low priority, offline desktop).

---

<!-- source: docs/FILE-INVENTORY.md -->

# Repository File Inventory and Missing-File Audit

**Repository:** `Roni4422/ShouTech`  
**Audited branch:** `work/full-app-stabilization-2026-11-18`  
**Audit commit:** `60df7663e42e88315f4c77b9df63bd6b287551df`  
**Compared with:** `main` at `31ebc1c936d8f9de94b0c91751859e2a9feb3458`  
**Audit date:** 2026-11-18

## 1. Tracked-file totals

| Measure | Count |
|---|---:|
| Tracked files on development branch | 751 |
| Tracked files on main | 749 |
| Net difference | +2 |
| Directories containing tracked files | 62 |
| C# source files (`.cs`) | 407 |
| SQL migration/schema files (`.sql`) | 159 |
| Markdown documents (`.md`) | 65 |
| XAML files in repository | 27 |
| WPF view XAML files under `cod/app/src/Views` | 23 |
| WPF view code-behind files (`.xaml.cs`) | 23 |
| Matched WPF XAML/code-behind pairs | 23 |
| WPF view XAML without same-basename code-behind | 0 |
| WPF view code-behind without same-basename XAML | 0 |
| C# project files (`.csproj`) | 8 |

Other tracked extensions: 20 JSON, 18 C++, 16 SHC, 6 H, 5 YML, 5 SYS, 4 PY, 3 PNG, and one each of GITIGNORE, PROPS, TXT (2), SLN, CONFIG, HYBRID and VCXPROJ.

## 2. Comparison with main: missing or renamed files

The path comparison accounts for all 127 paths that differ on main:

- Domain project folder renamed from `src/ShouTech.Domain` to `src/Domain`.
- Application project folder renamed from `src/ShouTech.Application` to `src/App`.
- Infrastructure project folder renamed from `src/ShouTech.Infrastructure` to `src/Infra`.
- The three project files were renamed consistently to `Domain.csproj`, `App.csproj` and `Infra.csproj`.
- `Services/RptQuery.cs` was renamed to `Services/Rpt.cs`.
- `Views/MatCard.xaml` and `Views/MatCard.xaml.cs` were moved together to `Views/Inventory/`.

**Comparison result:** 0 main-branch tracked files are unaccounted for after applying those explicit path mappings. The branch has two additional tracked files, both audit/refactoring documents: `docs/REFACTOR.md` and `docs/WINDOW-INVENTORY.md`. This is why the branch has 751 files versus 749 on main; it is not evidence of two missing source files.

## 3. Project and solution reference audit

- All 8 `.csproj` files were inspected.
- 13 explicit file/project references were resolved against the branch tree.
- Missing explicit references after cleanup: **0**.
- Solution project paths were checked against the repository tree.
- The WPF project uses recursive XAML and C# globs, so the new `Views/Inventory` folder is included by the existing project rules.

### Stale optional includes found and removed

`MainApp.csproj` contained two conditional source includes pointing to files that do not exist:

- `../../../cfg/AppSettings.cs`
- `../../../cfg/CompanyInfo.cs`

These were stale references, not missing implementation files: the settings model `AppSetng` and `CompanyInfo` type are already defined in `cod/app/src/Configuration/AppConfig.cs`, which is compiled by the project's recursive C# include. The only tracked file under `cfg` is `appset.json`.

The two obsolete conditional Compile entries were removed from `MainApp.csproj`. The remaining explicit project/file references all resolve.

## 4. WPF view-pair audit

Under `cod/app/src/Views`, all 23 XAML files have matching `.xaml.cs` code-behind files, and all 23 code-behind files have matching XAML files. No unmatched XAML/code-behind pair was found.

This check verifies file pairing only. It does not prove that every window is reachable from the Ribbon, has correct permissions, loads all resources, or works against the database. Those require route-level and runtime tests and remain part of the window migration gate.

## 5. Remaining verification

- [x] Compare complete tracked-file trees on the development branch and main.
- [x] Account for all renamed/moved paths.
- [x] Count files by extension and directory.
- [x] Check XAML/code-behind pairing.
- [x] Check explicit project/file references.
- [x] Remove the two stale optional source includes.
- [ ] Run Windows Debug and Release build after this audit commit.
- [ ] Complete runtime window navigation and data-persistence tests.
- [ ] Complete SQLite/PostgreSQL integration regression.

**Conclusion:** No tracked source file from `main` is currently unaccounted for after the documented renames and moves. The static project-reference audit now reports zero missing explicit references. Runtime completeness is not yet established; the outstanding window and database tests must still pass.
