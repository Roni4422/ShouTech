using System;
using System.Collections.Generic;
using System.Linq;

namespace ShouTech.Application.Services;

public sealed record ErpFeat(
    string Code,
    string Title,
    string Description,
    int Weight,
    bool IsCore,
    bool IsAutomation);

/// <summary>
/// Product-level planning helpers to keep ShouTech simpler, faster,
/// more powerful, and more modern than legacy ERP products.
/// </summary>
public sealed class ErpPlan
{
    private static readonly IReadOnlyList<ErpFeat> Feats = new[]
    {
        new ErpFeat("ACC", "Accounting", "Fast ledger, journals, and financial control", 100, true, false),
        new ErpFeat("INV", "Inventory", "Live stock, movements, and valuation", 98, true, false),
        new ErpFeat("SALES", "Sales", "Quick customer invoicing and order flow", 96, true, false),
        new ErpFeat("PURCH", "Purchases", "Vendor control and supplier purchasing", 94, true, false),
        new ErpFeat("DASH", "Dashboard", "Single-screen business overview for managers", 92, true, true),
        new ErpFeat("RPT", "Reporting", "Profit, cash flow, and KPI visibility", 90, true, true),
        new ErpFeat("POS", "POS", "Fast retail checkout and sales speed", 85, false, true),
        new ErpFeat("AI", "AI Insights", "Low-stock alerts and predictive insight", 75, false, true)
    };

    public IReadOnlyList<ErpFeat> Pri()
    {
        return Feats
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> ModSeq()
    {
        return Pri()
            .Select(x => x.Code)
            .ToList();
    }

    public IReadOnlyList<string> FastFlow()
    {
        return new[]
        {
            "Quick customer and item lookup",
            "One-click sales invoice workflow",
            "Low-stock and cash-alert warnings",
            "Fast posting with approval and audit trail",
            "Excel export in a single action"
        };
    }
}
