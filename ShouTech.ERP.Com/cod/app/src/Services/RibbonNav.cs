using System;
using System.Diagnostics;
using System.Windows;
using ShouTech.App.Configuration;
using ShouTech.App.Views;
using ShouTech.App.Views.Reports;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Services;

/// <summary>
/// Second-chance ribbon router (ribbon completion pass). MWin consults this
/// after its named-button cases and before RptNav / the module fallback:
/// every key listed here opens a real, data-backed window, so no ribbon
/// button can fall through to the ModuleWorkspaceView placeholder.
/// Keys already handled by RptNav are deliberately not repeated here.
/// </summary>
public static class RibbonNav
{
    public static bool TryHandle(string? actionKey, Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
            return false;

        switch (actionKey.Trim())
        {
            // ------------------------------------------------ Inventory reports
            case "Inventory.EndOfPeriod": Show(new EndOfPeriodWin(), owner); return true;
            case "Inventory.MaterialMovementChart": Show(new MatMoveChartWin(), owner); return true;
            case "Inventory.ExpiredMaterials": Show(new ExpiredWin(), owner); return true;
            case "Inventory.BatchBalances": Show(new BatchBalWin(), owner); return true;
            case "Inventory.InvoiceAge": Show(new InvoiceAgeWin(), owner); return true;
            case "Inventory.MaterialPriceHistory": Show(new PriceHistoryWin(), owner); return true;
            case "Inventory.ServiceMaterials": Show(new ServiceMatWin(), owner); return true;
            case "Inventory.MaterialStatistics": Show(new MatStatsWin(), owner); return true;
            case "Inventory.StagnantMaterials": Show(new StagnantWin(), owner); return true;
            case "Inventory.MostActiveMaterials": Show(new MostActiveWin(), owner); return true;
            case "Inventory.MaterialComparison": Show(new MatCompareWin(), owner); return true;
            case "Inventory.NegativeOutput": Show(new NegativeStockWin(), owner); return true;
            case "Inventory.Customers": Show(new InvCustomersWin(), owner); return true;
            case "Inventory.BranchMaterials": Show(new BranchMatWin(), owner); return true;
            case "Inventory.MaterialDiscounts": ShowDef(DefSets.MaterialDiscounts, owner); return true;
            case "Inventory.EditMaterialSpecs": ShowDef(DefSets.MaterialSpecs, owner); return true;
            case "Inventory.SerialNumbersReport":
            case "Inventory.EditSerialNumber": Show(new SerWin(), owner); return true;
            case "Inventory.QualityInspections": Show(new QcInspectWin(), owner); return true;
            case "Inventory.QualityTemplates": Show(new QcTemplateWin(), owner); return true;
            case "Inventory.QualityAlerts": Show(new QcAlertWin(), owner); return true;
            case "Inventory.QualitySummary": Show(new QcSummaryWin(), owner); return true;

            // ------------------------------------------------ CRM (Phase B 057; CRM.* prefix per design section 4.2 — RBAC only, entitlement gating postponed)
            case "CRM.Leads": Show(new CrmLeadWin(), owner); return true;
            case "CRM.Pipeline": Show(new CrmPipelineWin(), owner); return true;
            case "CRM.Opportunities": Show(new CrmOppWin(), owner); return true;
            case "CRM.Activities": Show(new CrmActivitiesWin(), owner); return true;
            case "CRM.Campaigns": Show(new CrmCampaignWin(), owner); return true;

            // ------------------------------------------------ After-sales (W10 Level 3, migration 105; CommWin pattern)
            case "Invoices.Warranty": Show(new WarrantyWin(), owner); return true;
            case "Invoices.RmaRequests": Show(new RmaWin(), owner); return true;

            // ------------------------------------------------ HR / Payroll (Build HR 065; HR.* keys, HR.* RBAC)
            case "HR.Employees": Show(new HrEmployeeWin(), owner); return true;
            case "HR.Departments": Show(new HrDeptWin(), owner); return true;
            case "HR.Designations": Show(new HrDesigWin(), owner); return true;
            case "HR.Attendance": Show(new HrAttendanceWin(), owner); return true;
            case "HR.Leave": Show(new HrLeaveWin(), owner); return true;
            case "HR.Structures": Show(new HrStructureWin(), owner); return true;
            case "HR.Payroll": Show(new HrPayrollWin(), owner); return true;
            case "HR.PayrollSummary": Show(new HrPayrollSummaryWin(), owner); return true;
            case "HR.PayslipReport": Show(new HrPayslipWin(), owner); return true;
            case "HR.AttendanceSummary": Show(new HrAttendanceSummaryWin(), owner); return true;
            case "HR.LeaveBalances": Show(new HrLeaveBalanceWin(), owner); return true;

            // ------------------------------------------------ Accounts reports & cards
            case "Accounts.CostCenter":
            case "Accounts.LinkWithAccounts": Show(new CcListWin(), owner); return true;
            case "Accounts.Budgets": Show(new BudgetWin(), owner); return true;
            case "Accounts.TaxConfiguration": Show(new TaxSetupWin(), owner); return true;
            case "Accounts.TaxReport": Show(new TaxReportWin(), owner); return true;
            case "Accounts.BudgetVariance": Show(new BudgetVarWin(), owner); return true;
            case "Accounts.CostCenterReport": Show(new CostCenterRptWin(), owner); return true;
            case "Accounts.CostCenterBalance": Show(new CostCenterTrialWin(), owner); return true;
            case "Accounts.AverageCurrencyRate": Show(new AvgRateWin(), owner); return true;
            case "Accounts.ExchangeRateDifferences": Show(new ExchangeDiffWin(), owner); return true;
            case "Accounts.AccountMovementChart": Show(new AccMoveChartWin(), owner); return true;
            case "Accounts.BranchAccountMovement": Show(new BranchAccMoveWin(), owner); return true;
            case "Accounts.Operations": Show(new ClosingAccountsWin("Operations"), owner); return true;
            case "Accounts.Trading": Show(new ClosingAccountsWin("Trading"), owner); return true;
            case "Accounts.ProfitLoss": RptNav.OpenIncomeStatement(owner); return true;
            case "Accounts.CashFlowStatement": Show(new CashFlowWin(), owner); return true;
            case "Accounts.DormantAccounts": Show(new DormantWin(), owner); return true;
            case "Accounts.CollectionRates": Show(new CollectionRatesWin(), owner); return true;
            case "Accounts.AccountComparison": Show(new AccCompareWin(), owner); return true;
            case "Accounts.FinancialAnalysis": Show(new FinancialAnalysisWin(), owner); return true;

            // ------------------------------------------------ Bonds / vouchers
            case "Bonds.CashJournal": Show(new CashJournalWin(), owner); return true;
            case "Bonds.DefineVoucherTypes": ShowDef(DefSets.VoucherTypes, owner); return true;
            case "Bonds.DefineSecurities": ShowDef(DefSets.SecurityTypes, owner); return true;
            case "Bonds.SearchSecurities": Show(new ChqListWin(), owner); return true;
            case "Bonds.PaymentTypes":
            case "Bonds.DefinePaymentTypes": ShowDef(DefSets.PaymentTypes, owner); return true;
            case "Bonds.Installment":
            case "Bonds.SearchInstallment": Show(new InstallmentsWin(), owner); return true;
            case "Bonds.MonitorVouchers": RptNav.OpenJournalList(owner); return true;

            // ------------------------------------------------ Invoices
            case "Invoices.DefineInvoices": ShowDef(DefSets.InvoiceDefinitions, owner); return true;
            case "Invoices.SearchInvoices": Show(new DocSearchWin(DocSearchWin.DocSearchMode.Invoices), owner); return true;
            case "Invoices.SearchQuotations": Show(new DocSearchWin(DocSearchWin.DocSearchMode.Quotations), owner); return true;
            case "Invoices.CloseCash": Show(new CloseCashWin(), owner); return true;
            case "Invoices.InvoiceProfits": Show(new InvoiceProfitsWin(), owner); return true;
            case "Invoices.PackingList": Show(new PackingListWin(), owner); return true;
            case "Invoices.CompressInvoices": Show(new CompressInvoicesWin(), owner); return true;
            case "Invoices.RepostInvoices": Show(new RepostWin(), owner); return true;
            case "Invoices.InvoiceDesigner": Show(new InvDesWin(), owner); return true;

            // ------------------------------------------------ POS
            case "POS.Points": Show(new LoyaltyWin(), owner); return true;
            case "POS.DefinePOS": Show(new PosTermWin(), owner); return true;
            case "POS.POSCashReport": Show(new PosCashRptWin(), owner); return true;
            case "POS.Sessions": Show(new PosSessionWin(), owner); return true;
            case "POS.SessionSales": Show(new PosSalesWin(), owner); return true;
            case "POS.ZReport": Show(new PosZReportWin(), owner); return true;
            case "POS.Performance": Show(new PosPerfRptWin(), owner); return true;
            case "POS.POSDiscounts": ShowDef(DefSets.PosDiscounts, owner); return true;
            case "POS.SyncPOS": Show(new PosSyncWin(), owner); return true;
            case "POS.AdjustPrice": RptNav.OpenPriceList(owner); return true;
            case "POS.AdjustQuantity":
                WhsWin.OpenStockCount(owner, isAdjustment: true);
                return true;
            case "POS.DeleteMaterial": Show(new MatDirWin(), owner); return true;

            // ------------------------------------------------ Orders
            case "Orders.PurchaseRequisitions": Show(new PurchaseRequisitionWin(), owner); return true;
            case "Orders.LandedCosts": Show(new PurchaseLandedCostWin(), owner); return true;
            case "Orders.DefineOrders": ShowDef(DefSets.OrderDefinitions, owner); return true;
            case "Orders.SearchOrders": Show(new DocSearchWin(DocSearchWin.DocSearchMode.Orders), owner); return true;
            case "Orders.OrderReceiptDelivery": Show(new SalesDeliveryWin(), owner); return true;
            case "Orders.TrackOrders": Show(new OrderTrackWin(), owner); return true;
            case "Orders.RecurringSchedules": Show(new RecurWin(), owner); return true;
            case "Orders.OrderPayments": Show(new OrderPaymentsWin(), owner); return true;

            // ------------------------------------------------ Customers
            case "Customers.CustomerPrices": ShowDef(DefSets.CustomerPrices, owner); return true;
            case "Customers.CustomerDiscounts": ShowDef(DefSets.CustomerDiscounts, owner); return true;
            case "Customers.Areas": ShowDef(DefSets.Areas, owner); return true;
            case "Customers.AssignTargets": ShowDef(DefSets.PartyTargets, owner); return true;
            case "Customers.TargetsReport": Show(new TargetsRptWin(), owner); return true;
            case "Customers.SearchCustomers": Show(new PartySearchWin(), owner); return true;
            case "Customers.CustomerDue":
            case "Customers.SearchDues": Show(new CustomerDueWin(), owner); return true;
            case "Customers.LinkPayments":
            case "Customers.BalanceAccounts": RptNav.OpenPaymentReconciliation(owner); return true;
            case "Customers.EditCustomers": Show(new PartyManagerWin(), owner); return true;
            case "Customers.CustomerGroups": Show(new GroupManagerWin(GroupManagerWin.GroupKind.Party), owner); return true;
            case "Customers.CustomerReports": RptNav.OpenCustomerBalances(owner); return true;

            // ------------------------------------------------ Sales representatives
            case "SalesRep.SalesRepGroups": Show(new GroupManagerWin(GroupManagerWin.GroupKind.SalesRep), owner); return true;
            case "SalesRep.AssignSalesRepTargets": ShowDef(DefSets.RepTargets, owner); return true;
            case "SalesRep.SalesRepTargetsReport": Show(new TargetsRptWin(), owner); return true;
            case "SalesRep.SearchCustomers": Show(new PartySearchWin(), owner); return true;
            case "SalesRep.Commissions": Show(new CommWin(), owner); return true;
            case "SalesRep.Contracts": Show(new ContractWin(), owner); return true;
            case "SalesRep.SalesRepMovement": Show(new RepMovementWin(), owner); return true;
            case "SalesRep.SalesRepCollections": Show(new RepCollectionsWin(), owner); return true;
            case "SalesRep.HandheldDevices": ShowDef(DefSets.Devices, owner); return true;
            case "SalesRep.DeviceRoutes": ShowDef(DefSets.DeviceRoutes, owner); return true;

            // ------------------------------------------------ Manufacturing
            case "Manufacturing.SearchManufacturing": Show(new MfgListWin(), owner); return true;
            case "Manufacturing.ManufacturingCostVariance": Show(new MfgVarianceWin("Total"), owner); return true;
            case "Manufacturing.RawMaterialCostVariance": Show(new MfgVarianceWin("Raw"), owner); return true;
            case "Manufacturing.AdditionalCostVariance": Show(new MfgVarianceWin("Overhead"), owner); return true;
            case "Manufacturing.ManufacturedMaterialCostVariance": Show(new MfgVarianceWin("Finished"), owner); return true;
            case "Manufacturing.ManufacturedMaterialCosts": Show(new MfgCostsWin(), owner); return true;
            case "Manufacturing.ManufacturingFeasibility": Show(new MfgFeasibilityWin(), owner); return true;
            case "Manufacturing.RepostManufacturing": Show(new RepostWin(), owner); return true;

            // ------------------------------------------------ Banks
            case "Banks.BankReconciliation": Show(new BankRecWin(), owner); return true;
            case "Banks.BankBalances": Show(new BankBalancesWin(), owner); return true;
            case "Banks.BankEstimate": Show(new BankEstimateWin(), owner); return true;

            // ------------------------------------------------ Reports center
            case "Reports.CustomReports":
            case "Reports.CustomizeReports": Show(new CustomReportsWin(), owner); return true;

            // ------------------------------------------------ Tools
            case "Tools.Calculator": Show(new CalculatorWin(), owner); return true;
            case "Tools.Keyboard": LaunchOnScreenKeyboard(owner); return true;
            // Ribbon completion R1: the four language entries had NO
            // handler anywhere (audit docs/RIBBON_AUDIT.md class (c)) —
            // the click fell into the generic module workspace and the
            // language never switched. They now run the real switch:
            // persist the choice in UsrPref (the OptWin pattern) and
            // apply it live to every open window.
            case "Tools.Arabic": SwitchLanguage("ar"); return true;
            case "Tools.English": SwitchLanguage("en"); return true;
            case "Tools.French": SwitchLanguage("fr"); return true;
            case "Tools.Turkish": SwitchLanguage("tr"); return true;
            case "Tools.MaintenanceWizard": Show(new MaintenanceWin(), owner); return true;
            case "Tools.ChangePassword": Show(new ChangePasswordWin(), owner); return true;
            case "Tools.ActivityLog": Show(new ActivityLogWin(), owner); return true;
            case "Tools.Addons": Show(new AddonsWin(), owner); return true;
            case "Tools.AI": Show(new FinancialAnalysisWin(), owner); return true;

            // ------------------------------------------------ Other tools
            case "OtherTools.Rotation": Show(new RolloverWin(), owner); return true;
            case "OtherTools.PeriodClose": Show(new PeriodCloseWin(), owner); return true;
            case "OtherTools.Permissions":
            case "OtherTools.PermissionTemplates":
                Show(new PrmWin(AppState.CurrentUser?.Username ?? string.Empty), owner);
                return true;
            case "OtherTools.CustomFields": ShowDef(DefSets.CustomFields, owner); return true;
            case "OtherTools.RestoreCompany.Restore": Show(new RestoreCoWin(), owner); return true;
            case "OtherTools.UnitsOfMeasure": ShowDef(DefSets.UnitsOfMeasure, owner); return true;
            case "OtherTools.PaymentMethods": ShowDef(DefSets.PaymentMethods, owner); return true;
            case "OtherTools.MaterialSpecs": ShowDef(DefSets.MaterialSpecs, owner); return true;
            case "OtherTools.DriverCard": ShowDef(DefSets.Drivers, owner); return true;
            case "OtherTools.DeliveryPrices": ShowDef(DefSets.DeliveryPrices, owner); return true;
            case "OtherTools.PickupRequests":
            case "OtherTools.SearchDelivery":
            case "OtherTools.CloseDriverCash": Show(new DeliveryDeskWin(), owner); return true;

            // ------------------------------------------------ View
            case "View.ChartOfAccounts": Show(new CoaWin(), owner); return true;
            case "View.MaterialTree": Show(new MatDirWin(), owner); return true;
            case "View.ManufacturingTree": Show(new MfgListWin(), owner); return true;
            case "View.CostCenterTree": Show(new CcListWin(), owner); return true;
            case "View.PaymentMethods": ShowDef(DefSets.PaymentMethods, owner); return true;
            case "View.CompanyInfo": Show(new ComWin(ComWinMode.Management), owner); return true;
            case "View.ExportData": Show(new DatWin(true), owner); return true;
            case "View.InfoWindow": ShowAbout(owner); return true;

            // ------------------------------------------------ Exit / session
            case "Exit.SwitchCompany": Show(new ComWin(ComWinMode.Startup), owner); return true;
            case "Exit.ScreenSaver": LockWorkStation(); return true;
            case "Exit.Title": ConfirmExit(owner); return true;

            // ------------------------------------------------ Help
            case "Help.CheckUpdates": Show(new UpdatesWin(), owner); return true;
            case "Help.UpdateFromFile": Show(new UpdatesWin(), owner); return true;
            case "Help.Instructions": Show(new HelpWin(), owner); return true;
            case "Help.TechnicalSupport": Show(new SupportWin(SupportWinMode.Support), owner); return true;
            case "Help.Suggestions": Show(new SupportWin(SupportWinMode.Suggestion), owner); return true;
            case "Help.RenewLicense": Show(new LicenseWin(), owner); return true;

            default:
                return false;
        }
    }

    private static void ShowDef(string setKey, Window? owner)
    {
        // DefManagerWin's ModeKey IS its SetKey, so the central policy
        // gives this the mode-aware matching ShowDef always did by hand.
        Show(DefManagerWin.For(setKey), owner);
    }

    /// <summary>Step 11: all activation goes through the ONE central
    /// policy (WindowActivator — same type AND same mode reactivates;
    /// a different mode opens a second instance). The type-only dedupe
    /// that used to live here was the router mode-stickiness defect
    /// (sweep S-2), and its empty catch silently swallowed activation
    /// failures (sweep S-7) — WindowActivator reports them centrally.</summary>
    private static void Show(Window win, Window? owner) => WindowActivator.Show(win, owner);

    /// <summary>
    /// Ribbon language entries (R1): persists the choice in UsrPref
    /// and applies it live — App.SetApplicationLanguage re-mirrors
    /// and re-localizes every open window and sets the culture.
    /// </summary>
    private static void SwitchLanguage(string code)
    {
        try
        {
            var pref = UsrPref.Load();
            pref.Lang = code;
            pref.Save();
            App.SetApplicationLanguage(code);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, ex, "RibbonNav.SwitchLanguage");
        }
    }

    private static void LaunchOnScreenKeyboard(Window? owner)
    {
        try
        {
            Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void LockWorkStation()
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Step 11 (sweep S-7): was a silent catch — lock failures now
            // report through the central mechanism like every other
            // shell operation.
            AppHost.Errors.Report(ErrorCodes.ShellOperationFailed, ex, "RibbonNav.LockWorkStation");
        }
    }

    private static void ShowAbout(Window? owner)
    {
        var version = typeof(RibbonNav).Assembly.GetName().Version?.ToString() ?? "";
        var text = AppHost.Localization.GetString("Messages.AboutText", $"ShouTech ERP\nVersion {version}");
        MessageBox.Show(owner, text, "ShouTech ERP", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void ConfirmExit(Window? owner) => RequestExit(owner);

    /// <summary>
    /// The one application-exit path (OPTWIN_SECTIONS §3.8): the
    /// Options "Confirm on exit" preference governs whether the
    /// confirmation prompt appears at all. Previously the prompt
    /// was unconditional here and absent in the shell view-model —
    /// the stored preference was read by nothing. Every exit entry
    /// point (ribbon Exit, shell Exit command, shell menu) routes
    /// through this method.
    /// </summary>
    public static void RequestExit(Window? owner)
    {
        var confirmOnExit = true;
        try { confirmOnExit = UsrPref.Load().ConfirmOnExit; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "RibbonNav.RequestExit.Prefs");
        }

        if (!confirmOnExit)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        var confirm = MessageBox.Show(
            owner,
            AppHost.Localization.GetString("Messages.ConfirmExit", "Exit ShouTech ERP?"),
            "ShouTech ERP",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
            System.Windows.Application.Current.Shutdown();
    }
}
