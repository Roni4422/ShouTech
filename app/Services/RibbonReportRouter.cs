using System;
using System.Windows;

namespace ShouTech.App.Services;

/// <summary>
/// Routes Ribbon report commands to their report windows. R3
/// (2026-11-24) resolved the former "closest existing report"
/// approximations one by one: where the mapped report IS the
/// requested report in substance, the mapping is now a formal
/// adoption (recorded per case below and in docs/RIBBON_AUDIT.md);
/// the one genuinely distinct report — material GROUP movement —
/// got its dedicated window (GroupMoveWin) and is routed there.
/// </summary>
public static class RibbonReportRouter
{
    public static bool TryHandle(string? actionKey, Window? owner = null)
    {
        switch (actionKey?.Trim())
        {
            // ADOPTED (R3): material profits ARE the item
            // profitability report — same concept, same data.
            case "Inventory.MaterialProfits":
                RptNav.OpenItemProfitability(owner);
                return true;

            // Dedicated window (R3): group-aggregated movement
            // is not the per-material movement report.
            case "Inventory.MaterialGroupMovementReport":
                RptNav.OpenGroupMoves(owner);
                return true;

            // ADOPTED (R3): the posted sales document register
            // is the inventory sales report (every posted sale
            // with totals); deeper sales analytics live in the
            // profitability reports.
            case "Inventory.SalesReport":
                RptNav.OpenSalesInvoiceList(owner);
                return true;

            // ADOPTED (R3): receivables aging IS customer aging.
            case "Accounts.AgingReceivables":
                RptNav.OpenCustomerAging(owner);
                return true;

            // ADOPTED (R3): the daily report is the journal
            // register over the day — the register's date
            // filters scope it; same ledger, same rows.
            case "Accounts.DailyReport":
                RptNav.OpenJournalList(owner);
                return true;

            default:
                return false;
        }
    }
}
