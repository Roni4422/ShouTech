using System;
using System.Windows;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Views;
using ShouTech.App.Views.Reports;

using ShouTech.Domain;
namespace ShouTech.App.Services;

/// <summary>
/// Central launcher for standard reports and commercial documents.
/// Maps ribbon/menu localization keys to concrete windows.
/// </summary>
public static class RptNav
{
    public static bool TryHandle(string? actionKey, Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
            return false;

        switch (actionKey.Trim())
        {
            case "Purchasing.MatchPolicy":
                OpenPurchaseMatchPolicy(owner); return true;
            case "Purchasing.GRNI":
            case "Reports.GoodsReceivedNotInvoiced":
                OpenGoodsReceivedNotInvoiced(owner); return true;
            case "Reports.SupplierStatement":
                OpenSupplierStatement(owner); return true;
            case "Accounts.TrialBalance":
            case "Reports.TrialBalance":
                OpenTrialBalance(owner); return true;
            case "Accounts.AccountBalance":
                OpenAccountBalance(owner); return true;
            case "Accounts.BalanceSheet":
                OpenBalanceSheet(owner); return true;
            case "Accounts.IncomeStatement":
                OpenIncomeStatement(owner); return true;
            case "Accounts.Ledger":
            case "Accounts.CustomerStatement":
            case "Reports.AccountStatement":
                OpenAccountStatement(owner); return true;
            case "Accounts.GeneralJournal":
            case "Reports.JournalList":
                OpenJournalList(owner); return true;
            case "Inventory.WarehouseInventory":
            case "Inventory.StockBalances":
            case "Reports.StockBalances":
            case "Reports.MaterialOverview":
                OpenStockBalances(owner); return true;
            case "Inventory.MaterialMovement":
            case "Reports.MaterialMoves":
            case "Accounts.CostCenterMaterialMovement":
                OpenMaterialMoves(owner); return true;
            case "Inventory.StockAdjustmentReport":
            case "Reports.StockAdjustment":
                OpenStockAdjustment(owner); return true;
            case "Reports.ItemProfitability":
            case "Inventory.ItemProfitability":
                OpenItemProfitability(owner); return true;
            case "Customers.CustomerBalance":
            case "Reports.CustomerBalances":
            case "Reports.CustomerOverview":
                OpenCustomerBalances(owner); return true;
            case "Customers.CustomerAging":
            case "Reports.CustomerAging":
                OpenCustomerAging(owner); return true;
            case "CRM.FunnelReport":
            case "Reports.CrmFunnel":
                OpenCrmFunnel(owner); return true;
            case "Reports.SalesChainExceptions":
                OpenSalesChainExceptions(owner); return true;
            case "Suppliers.SupplierBalance":
            case "Reports.SupplierBalances":
                OpenSupplierBalances(owner); return true;
            case "Suppliers.SupplierAging":
            case "Reports.SupplierAging":
                OpenSupplierAging(owner); return true;
            case "Invoices.CashSales":
            case "POS.OpenCashier":
                Show(new PosWin(), owner); return true;
            case "Invoices.SalesInvoice":
            case "Invoice.SalesTitle":
                OpenSalesInvoice(owner); return true;
            case "Invoices.SalesReturn":
            case "Invoices.ReturnSalesInvoice":
                OpenSalesReturn(owner); return true;
            case "Invoices.PurchaseInvoice":
            case "Invoice.PurchaseTitle":
                OpenPurchaseInvoice(owner); return true;
            case "Invoices.PurchaseReturn":
            case "Invoices.ReturnPurchaseInvoice":
                OpenPurchaseReturn(owner); return true;
            case "Purchasing.ReturnGoodsReceipt":
                OpenPurchaseReceiptReturn(owner); return true;
            case "Invoices.SalesInvoiceList":
            case "Reports.SalesInvoiceList":
                OpenSalesInvoiceList(owner); return true;
            case "Invoices.PurchaseInvoiceList":
            case "Reports.PurchaseInvoiceList":
                OpenPurchaseInvoiceList(owner); return true;
            case "Invoices.Transfer":
            case "Inventory.StockTransfer":
                OpenStockTransfer(owner); return true;
            case "Invoices.OpeningStock":
            case "Inventory.OpeningStock":
                OpenOpeningStock(owner); return true;
            case "Invoices.MaterialEntry":
            case "Inventory.StockIn":
                OpenStockIn(owner); return true;
            case "Invoices.MaterialExit":
            case "Inventory.StockOut":
                OpenStockOut(owner); return true;
            case "Invoices.InventoryCount":
            case "Inventory.StockCountDoc":
                OpenInventoryCount(owner); return true;
            case "Invoices.Quotation":
            case "Sales.Quotation":
                OpenQuotation(owner); return true;
            case "Bonds.JournalVoucher":
            case "Accounts.JournalVoucher":
            case "Vouchers.JournalTitle":
                OpenJournalVoucher(owner); return true;
            case "Bonds.ReceiptVoucher":
            case "Accounts.ReceiptVoucher":
            case "Vouchers.ReceiptTitle":
                OpenReceiptVoucher(owner); return true;
            case "Bonds.PaymentVoucher":
            case "Accounts.PaymentVoucher":
            case "Vouchers.PaymentTitle":
                OpenPaymentVoucher(owner); return true;
            // Step 8 (CHQ-01): incoming and outgoing cheques are
            // DIFFERENT documents with inverse GL. The outgoing key
            // must open the outgoing window — before this fix both
            // keys opened an incoming window and issuing a cheque
            // posted Dr Box / Cr Party (the exact inverse).
            case "Bonds.ReceiptCheque":
                OpenIncomingChequeVoucher(owner); return true;
            case "Bonds.PaymentCheque":
                OpenOutgoingChequeVoucher(owner); return true;
            case "Accounts.ChequeVoucher":
            case "Vouchers.ChequeTitle":
                OpenChequeVoucher(owner); return true;
            case "Customers.CustomerCard":
            case "Suppliers.SupplierCard":
            case "CustCard.Title":
                OpenCustCard(owner); return true;
            case "Accounts.CostCenterCard":
            case "CostCenter.Title":
                OpenCostCenterCard(owner); return true;
            case "Accounts.DefineCurrency":
            case "Accounts.CurrencyRates":
            case "CurrencyCard.Title":
                OpenCurrencyCard(owner); return true;
            case "Banks.DefineBanks":
            case "BankCard.Title":
                OpenBankCard(owner); return true;
            case "SalesRep.SalesRepCard":
            case "SalesRepCard.Title":
                OpenSalesRepCard(owner); return true;
            case "Accounts.PaymentReconciliation":
                OpenPaymentReconciliation(owner); return true;
            case "Projects.Projects":
                OpenProjects(owner); return true;
            case "Assets.Assets":
                OpenAssets(owner); return true;
            case "Manufacturing.ManufacturingModels":
                // MFG-6 FIX: the ribbon's BOM entry opens the BOM master
                // (BomWin), not a blank production order.
                OpenBom(owner); return true;
            case "Manufacturing.Planning":
                OpenPlanning(owner); return true;
            case "Manufacturing.ManufacturingProcess":
            case "MfgOrder.Title":
                OpenMfgOrder(owner); return true;
            case "Orders.SalesOrder":
            case "OrderDoc.TitleSales":
                OpenSalesOrder(owner); return true;
            case "Orders.PurchaseOrder":
            case "OrderDoc.TitlePurchase":
                OpenPurchaseOrder(owner); return true;
            case "Orders.RequestForQuotation":
            case "Purchasing.RequestForQuotation":
                OpenRequestForQuotation(owner); return true;
            // Phase 7B - three DISTINCT barcode windows:
            //   Create   = single material, N copies      (BarWin)
            //   Generate = auto-assign barcodes for ALL materials (BcdGenWin)
            //   Design  = label designer with style toolbar (BcdDesWin)
            case "Inventory.CreateBarcode":
            case "Invoices.PrintBarcode":
            case "BarcodeTool.Title":
                OpenBarcodeTool(owner); return true;
            case "Inventory.GenerateBarcode":
                OpenBarcodeGenerator(owner); return true;
            case "Inventory.DesignBarcode":
                OpenBarcodeDesigner(owner); return true;
            case "Inventory.MaterialList":
            case "Inventory.SearchMaterial":
                OpenMaterialList(owner); return true;
            case "Inventory.PriceList":
                OpenPriceList(owner); return true;
            case "Inventory.ShortageList":
                OpenShortageList(owner); return true;
            case "Reports.Statistics":
                OpenTrialBalance(owner); return true;
            default:
                return false;
        }
    }

    public static void OpenTrialBalance(Window? owner = null) => Show(new TrialBalanceWin(), owner);
    public static void OpenPaymentReconciliation(Window? owner = null) => Show(new PayRecWin(), owner);
    public static void OpenGoodsReceivedNotInvoiced(Window? owner = null) => Show(new GrniWin(), owner);
    public static void OpenSupplierStatement(Window? owner = null) => Show(new SupStatementWin(), owner);
    public static void OpenProjects(Window? owner = null) => Show(new ProjWin(), owner);
    public static void OpenAssets(Window? owner = null) => Show(new AssetWin(), owner);
    public static void OpenAccountBalance(Window? owner = null) => Show(new AccBalWin(), owner);
    public static void OpenBalanceSheet(Window? owner = null) => Show(new BalSheetWin(), owner);
    public static void OpenIncomeStatement(Window? owner = null) => Show(new IncStmtWin(), owner);
    public static void OpenAccountStatement(Window? owner = null) => Show(new AccStmtWin(), owner);
    public static void OpenJournalList(Window? owner = null) => Show(new JrnListWin(), owner);
    public static void OpenStockBalances(Window? owner = null) => Show(new StkBalWin(), owner);
    public static void OpenMaterialMoves(Window? owner = null) => Show(new MatMoveWin(), owner);
    public static void OpenCustomerBalances(Window? owner = null) => Show(new CustBalWin(), owner);
    public static void OpenSupplierBalances(Window? owner = null) => Show(new SupBalWin(), owner);
    public static void OpenSalesInvoiceList(Window? owner = null) => Show(new SalListWin(), owner);
    public static void OpenPurchaseInvoiceList(Window? owner = null) => Show(new PurListWin(), owner);
    public static void OpenSalesInvoice(Window? owner = null) => Show(new InvoiceWin(InvDocKind.SalesInvoice), owner);
    public static void OpenSalesReturn(Window? owner = null) => Show(new SalesReturnWin(), owner);
    public static void OpenPurchaseInvoice(Window? owner = null) => Show(new InvoiceWin(InvDocKind.PurchaseInvoice), owner);
    public static void OpenPurchaseReturn(Window? owner = null) => Show(new PurchaseReturnWin(), owner);
    public static void OpenPurchaseReceiptReturn(Window? owner = null) => Show(new PurchaseReceiptReturnWin(), owner);
    public static void OpenStockTransfer(Window? owner = null) => Show(new InvoiceWin(InvDocKind.StockTransfer), owner);
    public static void OpenOpeningStock(Window? owner = null) => Show(new InvoiceWin(InvDocKind.OpeningStock), owner);
    public static void OpenStockIn(Window? owner = null) => Show(new InvoiceWin(InvDocKind.StockIn), owner);
    public static void OpenStockOut(Window? owner = null) => Show(new InvoiceWin(InvDocKind.StockOut), owner);
    public static void OpenInventoryCount(Window? owner = null) => Show(new InvoiceWin(InvDocKind.InventoryCount), owner);
    public static void OpenQuotation(Window? owner = null) => Show(new InvoiceWin(InvDocKind.Quotation), owner);

    public static void OpenJournalVoucher(Window? owner = null) => Show(new JrnWin(), owner);
    public static void OpenReceiptVoucher(Window? owner = null) => Show(new CshWin(CshKind.Receipt), owner);
    public static void OpenPaymentVoucher(Window? owner = null) => Show(new CshWin(CshKind.Payment), owner);
    public static void OpenChequeVoucher(Window? owner = null) => OpenIncomingChequeVoucher(owner);
    public static void OpenIncomingChequeVoucher(Window? owner = null) => Show(new ChqWin(ChqKind.Incoming), owner);
    public static void OpenOutgoingChequeVoucher(Window? owner = null) => Show(new ChqWin(ChqKind.Outgoing), owner);

    // Step 8's kind-aware ShowCheque dedupe is retired: Step 11
    // generalized its (type, kind) rule into WindowActivator, which
    // Show() below now applies to EVERY window this router opens —
    // cheques keep exactly the Step-8 behaviour through ChqWin.ModeKey.

    public static void OpenCustCard(Window? owner = null) => Show(new CstCard(), owner);
    public static void OpenCostCenterCard(Window? owner = null) => Show(new CcCard(), owner);
    public static void OpenCurrencyCard(Window? owner = null) => Show(new CurCard(), owner);
    public static void OpenBankCard(Window? owner = null) => Show(new BnkCard(), owner);
    public static void OpenSalesRepCard(Window? owner = null) => Show(new RepCard(), owner);

    public static void OpenMfgOrder(Window? owner = null) => Show(new MfgWin(), owner);
    public static void OpenBom(Window? owner = null) => Show(new BomWin(), owner);
    public static void OpenPlanning(Window? owner = null) => Show(new MfgPlanWin(), owner);
    public static void OpenHrEmployees(Window? owner = null) => Show(new HrEmployeeWin(), owner);
    public static void OpenHrDepartments(Window? owner = null) => Show(new HrDeptWin(), owner);
    public static void OpenHrDesignations(Window? owner = null) => Show(new HrDesigWin(), owner);
    public static void OpenHrAttendance(Window? owner = null) => Show(new HrAttendanceWin(), owner);
    public static void OpenHrLeave(Window? owner = null) => Show(new HrLeaveWin(), owner);
    public static void OpenHrStructures(Window? owner = null) => Show(new HrStructureWin(), owner);
    public static void OpenHrPayroll(Window? owner = null) => Show(new HrPayrollWin(), owner);
    public static void OpenSalesOrder(Window? owner = null) => Show(new OrdWin(OrdKind.Sales), owner);
    public static void OpenPurchaseOrder(Window? owner = null) => Show(new OrdWin(OrdKind.Purchase), owner);
    public static void OpenPurchaseMatchPolicy(Window? owner = null) => Show(new PurchaseMatchPolicyWin(), owner);
    public static void OpenRequestForQuotation(Window? owner = null) => Show(new OrdWin(OrdKind.RequestForQuotation), owner);
    public static void OpenBarcodeTool(Window? owner = null) => Show(new BarWin(), owner);
    public static void OpenBarcodeGenerator(Window? owner = null) => Show(new BcdGenWin(), owner);
    public static void OpenBarcodeDesigner(Window? owner = null) => Show(new BcdDesWin(), owner);
    public static void OpenMaterialList(Window? owner = null) => Show(new MatListWin(), owner);
    public static void OpenPriceList(Window? owner = null) => Show(new PriceListWin(), owner);
    public static void OpenShortageList(Window? owner = null) => Show(new ShortListWin(), owner);
    public static void OpenCustomerAging(Window? owner = null) => Show(new CustAgingWin(), owner);

    // Sales cycle B5 (G22): the two new reports gate on the same view
    // permissions as their neighbouring windows — CRM.OPP.VIEW (the
    // CrmPipelineWin/CrmOppWin check) for the funnel and
    // SALES.ORDER.VIEW (the sales order windows) for chain exceptions.
    // The check lives here so a denied user never sees the window flash
    // open before RptBase starts loading.
    public static void OpenCrmFunnel(Window? owner = null)
    {
        if (!AppHost.Permissions.Has(PermCat.Crm.OppView))
        {
            MessageBox.Show(owner,
                AppServiceHost.Localization.GetString("Crm.PermissionDenied") ?? "Crm.PermissionDenied",
                "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Show(new CrmFunnelWin(), owner);
    }

    public static void OpenSalesChainExceptions(Window? owner = null)
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderView))
        {
            MessageBox.Show(owner,
                AppServiceHost.Localization.GetString("OrderDoc.PermissionDenied") ?? "OrderDoc.PermissionDenied",
                "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Show(new SalExceptWin(), owner);
    }
    public static void OpenSupplierAging(Window? owner = null) => Show(new SupAgingWin(), owner);
    public static void OpenItemProfitability(Window? owner = null) => Show(new ItemProfitWin(), owner);
    public static void OpenStockAdjustment(Window? owner = null) => Show(new StkAdjRptWin(), owner);

    /// <summary>Step 11: all activation goes through the ONE central
    /// policy (WindowActivator — same type AND same mode reactivates;
    /// a different mode opens a second instance). The type-only dedupe
    /// that used to live here was the router mode-stickiness defect
    /// (sweep S-2).</summary>
    private static void Show(Window win, Window? owner) => WindowActivator.Show(win, owner);
}
