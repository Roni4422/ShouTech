using System;
using System.Windows;

namespace ShouTech.App.Services;

/// <summary>
/// Routes Ribbon commands whose requested report is not yet a distinct report
/// window to the closest existing, data-backed report. This keeps these actions
/// inside the real reporting workflow rather than opening the generic placeholder
/// module workspace.
/// </summary>
public static class RibbonReportRouter
{
    public static bool TryHandle(string? actionKey, Window? owner = null)
    {
        switch (actionKey?.Trim())
        {
            // Material profit analysis is the existing item profitability report.
            case "Inventory.MaterialProfits":
                RptNav.OpenItemProfitability(owner);
                return true;

            // Group movement is supported by the material movement report, whose
            // filters allow the user to select a material and warehouse.
            case "Inventory.MaterialGroupMovementReport":
                RptNav.OpenMaterialMoves(owner);
                return true;

            // Sales activity is available through the posted sales document register.
            case "Inventory.SalesReport":
                RptNav.OpenSalesInvoiceList(owner);
                return true;

            // Receivables aging is the existing customer aging report.
            case "Accounts.AgingReceivables":
                RptNav.OpenCustomerAging(owner);
                return true;

            // Daily accounting activity is available in the journal register.
            case "Accounts.DailyReport":
                RptNav.OpenJournalList(owner);
                return true;

            default:
                return false;
        }
    }
}
