using System;
using System.Windows;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Views;
using ShouTech.App.Views.Reports;

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
            case "Bonds.ReceiptCheque":
            case "Bonds.PaymentCheque":
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
            case "Manufacturing.ManufacturingModels":
            case "Manufacturing.ManufacturingProcess":
            case "MfgOrder.Title":
                OpenMfgOrder(owner); return true;
            case "Orders.SalesOrder":
            case "OrderDoc.TitleSales":
                OpenSalesOrder(owner); return true;
            case "Orders.PurchaseOrder":
            case "OrderDoc.TitlePurchase":
                OpenPurchaseOrder(owner); return true;
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
    public static void OpenSalesReturn(Window? owner = null) => Show(new InvoiceWin(InvDocKind.SalesReturn), owner);
    public static void OpenPurchaseInvoice(Window? owner = null) => Show(new InvoiceWin(InvDocKind.PurchaseInvoice), owner);
    public static void OpenPurchaseReturn(Window? owner = null) => Show(new InvoiceWin(InvDocKind.PurchaseReturn), owner);
    public static void OpenStockTransfer(Window? owner = null) => Show(new InvoiceWin(InvDocKind.StockTransfer), owner);
    public static void OpenOpeningStock(Window? owner = null) => Show(new InvoiceWin(InvDocKind.OpeningStock), owner);
    public static void OpenStockIn(Window? owner = null) => Show(new InvoiceWin(InvDocKind.StockIn), owner);
    public static void OpenStockOut(Window? owner = null) => Show(new InvoiceWin(InvDocKind.StockOut), owner);
    public static void OpenInventoryCount(Window? owner = null) => Show(new InvoiceWin(InvDocKind.InventoryCount), owner);
    public static void OpenQuotation(Window? owner = null) => Show(new InvoiceWin(InvDocKind.Quotation), owner);

    public static void OpenJournalVoucher(Window? owner = null) => Show(new JrnWin(), owner);
    public static void OpenReceiptVoucher(Window? owner = null) => Show(new CshWin(CshKind.Receipt), owner);
    public static void OpenPaymentVoucher(Window? owner = null) => Show(new CshWin(CshKind.Payment), owner);
    public static void OpenChequeVoucher(Window? owner = null) => Show(new ChqWin(), owner);

    public static void OpenCustCard(Window? owner = null) => Show(new CstCard(), owner);
    public static void OpenCostCenterCard(Window? owner = null) => Show(new CcCard(), owner);
    public static void OpenCurrencyCard(Window? owner = null) => Show(new CurCard(), owner);
    public static void OpenBankCard(Window? owner = null) => Show(new BnkCard(), owner);
    public static void OpenSalesRepCard(Window? owner = null) => Show(new RepCard(), owner);

    public static void OpenMfgOrder(Window? owner = null) => Show(new MfgWin(), owner);
    public static void OpenSalesOrder(Window? owner = null) => Show(new OrdWin(OrdKind.Sales), owner);
    public static void OpenPurchaseOrder(Window? owner = null) => Show(new OrdWin(OrdKind.Purchase), owner);
    public static void OpenBarcodeTool(Window? owner = null) => Show(new BarWin(), owner);
    public static void OpenBarcodeGenerator(Window? owner = null) => Show(new BcdGenWin(), owner);
    public static void OpenBarcodeDesigner(Window? owner = null) => Show(new BcdDesWin(), owner);
    public static void OpenMaterialList(Window? owner = null) => Show(new MatListWin(), owner);
    public static void OpenPriceList(Window? owner = null) => Show(new PriceListWin(), owner);
    public static void OpenShortageList(Window? owner = null) => Show(new ShortListWin(), owner);
    public static void OpenCustomerAging(Window? owner = null) => Show(new CustAgingWin(), owner);
    public static void OpenSupplierAging(Window? owner = null) => Show(new SupAgingWin(), owner);
    public static void OpenItemProfitability(Window? owner = null) => Show(new ItemProfitWin(), owner);
    public static void OpenStockAdjustment(Window? owner = null) => Show(new StkAdjRptWin(), owner);

    private static void Show(Window win, Window? owner)
    {
        try
        {
            foreach (Window w in System.Windows.Application.Current.Windows)
            {
                if (w.GetType() == win.GetType() && w.IsVisible)
                {
                    w.Activate();
                    if (w.WindowState == WindowState.Minimized)
                        w.WindowState = WindowState.Normal;
                    return;
                }
            }
            win.Owner = owner ?? System.Windows.Application.Current?.MainWindow;
        }
        catch { }
        win.Show();
    }
}
