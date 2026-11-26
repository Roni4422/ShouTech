using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

// ---- merged from cod/app/src/Views/Reports/CollectionRatesWin.cs ----
/// <summary>Per customer: invoiced vs collected from the party ledger, with collection percentage.</summary>
public sealed class CollectionRatesWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Acc.CollectionRates",
        TitleTextKey = "Reports.CollectionRates",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 100 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Invoiced", HeaderTextKey = "Reports.Col.Invoiced", Type = RptColType.Money, Width = 130 },
            new() { Key = "Collected", HeaderTextKey = "Reports.Col.Collected", Type = RptColType.Money, Width = 130 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 120 },
            new() { Key = "CollectionPct", HeaderTextKey = "Reports.Col.CollectionPct", Type = RptColType.Percent, Width = 110 }
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.CollectionRatesAsync(ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/CustAgingWin.cs ----
public sealed class CustAgingWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AR.CustomerAging",
        TitleTextKey = "Reports.CustomerAging",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.PartyCode", Width = 100 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.PartyName", Width = 220 },
            new() { Key = "B0", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B30", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B60", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B90", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.PartyAgingAsync(customers: true, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = r.GetValueOrDefault("Name"),
            ["B0"] = Dec(r.GetValueOrDefault("Bucket0_30")),
            ["B30"] = Dec(r.GetValueOrDefault("Bucket31_60")),
            ["B60"] = Dec(r.GetValueOrDefault("Bucket61_90")),
            ["B90"] = Dec(r.GetValueOrDefault("Bucket90Plus")),
            ["Balance"] = Dec(r.GetValueOrDefault("Balance")),
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["B0"] = rows.Sum(x => Dec(x["B0"])),
                ["B30"] = rows.Sum(x => Dec(x["B30"])),
                ["B60"] = rows.Sum(x => Dec(x["B60"])),
                ["B90"] = rows.Sum(x => Dec(x["B90"])),
                ["Balance"] = rows.Sum(x => Dec(x["Balance"])),
            }
        };
    }

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/CustBalWin.cs ----
public sealed class CustBalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AR.CustomerBalances",
        TitleTextKey = "Reports.CustomerBalances",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 260 },
            new() { Key = "Phone", HeaderTextKey = "Reports.Col.Phone", Type = RptColType.Text, Width = 120 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 130, Format = "N2" },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var lang = App.CurrentLanguageCode ?? "ar";
        var raw = await RptQuery.CustomerBalancesAsync(ct);
        var rows = raw.Select(r =>
        {
            var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
                : Convert.ToString(r.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name))
                name = Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture);
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = r.GetValueOrDefault("Code"),
                ["Name"] = name,
                ["Phone"] = r.GetValueOrDefault("Phone"),
                ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
            };
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["Balance"] = rows.Sum(x => ToDec(x["Balance"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/CustStmtWin.cs ----
/// <summary>
/// Customer ledger statement with base-currency opening and running
/// balances (W1) — the customer mirror of <see cref="SupStatementWin"/>,
/// over <see cref="RptQuery.CustomerStatementAsync"/> (party_ledger,
/// debit − credit orientation). <see cref="InitialSearch"/> pre-filters
/// the statement (CstCard's Statement button passes the party code).
/// </summary>
public sealed class CustStmtWin : RptBase
{
    /// <summary>Optional pre-filter (a party code) applied ahead of the
    /// filter bar's Search value.</summary>
    public string? InitialSearch { get; set; }

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AR.CustomerStatement",
        TitleTextKey = "Reports.CustomerStatement",
        Columns = new RptCol[]
        {
            new() { Key = "CustomerCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 95 },
            new() { Key = "CustomerName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 180 },
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 95 },
            new() { Key = "DocumentNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 110 },
            new() { Key = "DocumentKind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 75 },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 180 },
            new() { Key = "CurrencyCode", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 75 },
            new() { Key = "DebitBase", HeaderTextKey = "Reports.Col.DebitBase", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "CreditBase", HeaderTextKey = "Reports.Col.CreditBase", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "OpeningBalanceBase", HeaderTextKey = "Reports.Col.OpeningBalance", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "RunningBalanceBase", HeaderTextKey = "Reports.Col.RunningBalance", Type = RptColType.Money, Width = 125, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        if (from.Date > to.Date)
            throw new ArgumentException("The start date must not be after the end date.");

        var search = string.IsNullOrWhiteSpace(InitialSearch) ? filters.GetString("Search") : InitialSearch;
        var raw = await RptQuery.CustomerStatementAsync(from, to, search, ct);
        var rows = RptWinUtil.Map(raw, BuildDefinition().Columns);
        var openingByCustomer = rows
            .GroupBy(row => Convert.ToString(row.GetValueOrDefault("CustomerCode"), CultureInfo.InvariantCulture) ?? string.Empty)
            .ToDictionary(group => group.Key, group => RptWinUtil.Dec(group.First().GetValueOrDefault("OpeningBalanceBase")), StringComparer.Ordinal);
        var totals = new Dictionary<string, object?>
        {
            ["Note"] = T("Reports.Totals"),
            ["DebitBase"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("DebitBase"))),
            ["CreditBase"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("CreditBase"))),
        };
        if (openingByCustomer.Count > 0)
            totals["OpeningBalanceBase"] = openingByCustomer.Values.Sum();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = rows.Count == 0 ? null : totals
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/CustomerDueWin.cs ----
/// <summary>
/// Customers with an outstanding balance, sorted by oldest ledger date, with
/// the age in days of the oldest open entry. Serves both ribbon keys
/// Customers.CustomerDue and Customers.SearchDues. The query runs on
/// party_ledger + the canonical parties master (migration 061).
/// </summary>
public sealed class CustomerDueWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Customers.CustomerDue",
        TitleTextKey = "Reports.CustomerDue",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Phone", HeaderTextKey = "Reports.Col.Phone", Type = RptColType.Text, Width = 120 },
            new() { Key = "Invoiced", HeaderTextKey = "Reports.Col.Invoiced", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Collected", HeaderTextKey = "Reports.Col.Collected", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "OldestDate", HeaderTextKey = "Reports.Col.OldestDue", Type = RptColType.Date, Width = 110 },
            new() { Key = "AgeDays", HeaderTextKey = "Reports.Col.AgeDays", Type = RptColType.WholeNumber, Width = 100 },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.CustomerDueAsync(ct);
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var oldest = RptWinUtil.ParseDate(r.GetValueOrDefault("OldestDate"));
            rows.Add(new Dictionary<string, object?>
            {
                ["PartyCode"] = r.GetValueOrDefault("PartyCode"),
                ["PartyName"] = r.GetValueOrDefault("PartyName"),
                ["Phone"] = r.GetValueOrDefault("Phone"),
                ["Invoiced"] = RptWinUtil.Dec(r.GetValueOrDefault("Invoiced")),
                ["Collected"] = RptWinUtil.Dec(r.GetValueOrDefault("Collected")),
                ["Balance"] = RptWinUtil.Dec(r.GetValueOrDefault("Balance")),
                ["OldestDate"] = r.GetValueOrDefault("OldestDate"),
                ["AgeDays"] = oldest is null ? 0 : Math.Max(0, (DateTime.Today - oldest.Value.Date).Days),
            });
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName", "AgeDays")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/InvCustomersWin.cs ----
/// <summary>Materials bought per customer, from posted sales lines.</summary>
public sealed class InvCustomersWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.Customers",
        TitleTextKey = "Reports.InventoryCustomers",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 100 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 200 },
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Qty", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Value", HeaderTextKey = "Reports.Col.Value", Type = RptColType.Money, Width = 120 },
            new() { Key = "InvoiceCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 100 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.InventoryCustomersAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/InvoiceAgeWin.cs ----
/// <summary>
/// Invoice age schedule: open amounts of posted sales invoices bucketed by
/// age (0-30 / 31-60 / 61-90 / 90+ days from the invoice date) per customer.
/// Bucketing is done here from the real per-invoice open amounts so the
/// report works identically on SQLite and PostgreSQL.
/// </summary>
public sealed class InvoiceAgeWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Inv.InvoiceAge",
        TitleTextKey = "Reports.InvoiceAge",
        Columns = new RptCol[]
        {
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 240 },
            new() { Key = "Bucket0_30", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Bucket31_60", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Bucket61_90", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Bucket90Plus", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "InvoiceCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 100 },
        },
        Filters = Array.Empty<RptFlt>()
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var raw = await RptQuery.InvoiceAgeInvoicesAsync(ct);
        var byParty = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var r in raw)
        {
            var code = Convert.ToString(r.GetValueOrDefault("PartyCode"), CultureInfo.InvariantCulture) ?? "";
            var name = Convert.ToString(r.GetValueOrDefault("PartyName"), CultureInfo.InvariantCulture) ?? "";
            var key = code + "|" + name;
            if (!byParty.TryGetValue(key, out var row))
            {
                row = new Dictionary<string, object?>
                {
                    ["PartyCode"] = code, ["PartyName"] = name,
                    ["Bucket0_30"] = 0m, ["Bucket31_60"] = 0m, ["Bucket61_90"] = 0m, ["Bucket90Plus"] = 0m,
                    ["Total"] = 0m, ["InvoiceCount"] = 0
                };
                byParty[key] = row;
                order.Add(key);
            }
            var open = RptWinUtil.Dec(r.GetValueOrDefault("OpenAmount"));
            var date = RptWinUtil.ParseDate(r.GetValueOrDefault("DocDate")) ?? DateTime.Today;
            var age = Math.Max(0, (DateTime.Today - date.Date).Days);
            var bucket = age <= 30 ? "Bucket0_30" : age <= 60 ? "Bucket31_60" : age <= 90 ? "Bucket61_90" : "Bucket90Plus";
            row[bucket] = RptWinUtil.Dec(row[bucket]) + open;
            row["Total"] = RptWinUtil.Dec(row["Total"]) + open;
            row["InvoiceCount"] = (int)row["InvoiceCount"]! + 1;
        }
        var rows = order.Select(k => (IReadOnlyDictionary<string, object?>)byParty[k]).ToList();
        var def = BuildDefinition();
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/InvoiceProfitsWin.cs ----
/// <summary>Per posted sales invoice: revenue, cost at material average cost, profit and margin.</summary>
public sealed class InvoiceProfitsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Invoices.InvoiceProfits",
        TitleTextKey = "Reports.InvoiceProfits",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Revenue", HeaderTextKey = "Reports.Col.Revenue", Type = RptColType.Money, Width = 120 },
            new() { Key = "Cost", HeaderTextKey = "Reports.Col.Cost", Type = RptColType.Money, Width = 120 },
            new() { Key = "Profit", HeaderTextKey = "Reports.Col.Profit", Type = RptColType.Money, Width = 120 },
            new() { Key = "MarginPct", HeaderTextKey = "Reports.Col.MarginPct", Type = RptColType.Percent, Width = 90 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.InvoiceProfitsAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/ItemProfitWin.cs ----
public sealed class ItemProfitWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Sales.ItemProfitability",
        TitleTextKey = "Reports.ItemProfitability",
        Columns = new RptCol[]
        {
            new() { Key = "MaterialCode", HeaderTextKey = "Reports.Col.MaterialCode", Width = 110 },
            new() { Key = "MaterialName", HeaderTextKey = "Reports.Col.MaterialName", Width = 220 },
            new() { Key = "QtySold", HeaderTextKey = "Reports.Col.Qty", Type = RptColType.FractionalNumber, Width = 90, Format = "N3" },
            new() { Key = "Revenue", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 90, Format = "N2" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 110, Format = "N2" },
            // Step 6: the profitability columns this report's name
            // promises — cost from the valuation engine's movements.
            new() { Key = "Cost", HeaderTextKey = "Reports.Col.Cost", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Profit", HeaderTextKey = "Reports.Col.Profit", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "MarginPct", HeaderTextKey = "Reports.Col.MarginPct", Type = RptColType.Percent, Width = 90, Format = "P1" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.ItemProfitabilityAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["MaterialCode"] = r.GetValueOrDefault("MaterialCode"),
            ["MaterialName"] = r.GetValueOrDefault("MaterialName"),
            ["QtySold"] = Dec(r.GetValueOrDefault("QtySold")),
            ["Revenue"] = Dec(r.GetValueOrDefault("Revenue")),
            ["Tax"] = Dec(r.GetValueOrDefault("Tax")),
            ["Total"] = Dec(r.GetValueOrDefault("Total")),
            ["Cost"] = Dec(r.GetValueOrDefault("Cost")),
            ["Profit"] = Dec(r.GetValueOrDefault("Profit")),
            ["MarginPct"] = Dec(r.GetValueOrDefault("MarginPct")),
        }).ToList();
        var totalRevenue = rows.Sum(x => Dec(x["Revenue"]));
        var totalProfit = rows.Sum(x => Dec(x["Profit"]));
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["MaterialName"] = T("Reports.Totals"),
                ["QtySold"] = rows.Sum(x => Dec(x["QtySold"])),
                ["Revenue"] = totalRevenue,
                ["Tax"] = rows.Sum(x => Dec(x["Tax"])),
                ["Total"] = rows.Sum(x => Dec(x["Total"])),
                ["Cost"] = rows.Sum(x => Dec(x["Cost"])),
                ["Profit"] = totalProfit,
                ["MarginPct"] = totalRevenue == 0 ? 0m : totalProfit / totalRevenue,
            }
        };
    }

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/OrderPaymentsWin.cs ----
/// <summary>Payments against sales/purchase orders: allocated amounts (payment_allocations) or the document paid_amount, with the remaining balance.</summary>
public sealed class OrderPaymentsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Orders.OrderPayments",
        TitleTextKey = "Reports.OrderPayments",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Kind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 70 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 220 },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 120 },
            new() { Key = "Paid", HeaderTextKey = "Reports.Col.Paid", Type = RptColType.Money, Width = 120 },
            new() { Key = "Remaining", HeaderTextKey = "Reports.Col.Remaining", Type = RptColType.Money, Width = 120 },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.OrderPaymentsAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Convert.ToString(row.GetValueOrDefault("DocNo"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("PartyName"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToList();
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "PartyName")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/PurListWin.cs ----
public sealed class PurListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Purch.InvoiceList",
        TitleTextKey = "Reports.PurchaseInvoiceList",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Kind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 70 },
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 100 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Subtotal", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 },
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 70 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.PurchaseInvoiceListAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["DocDate"] = r.GetValueOrDefault("DocDate"),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["Kind"] = r.GetValueOrDefault("Kind"),
            ["PartyCode"] = r.GetValueOrDefault("PartyCode"),
            ["PartyName"] = r.GetValueOrDefault("PartyName"),
            ["Subtotal"] = ToDec(r.GetValueOrDefault("Subtotal")),
            ["Tax"] = ToDec(r.GetValueOrDefault("Tax")),
            ["Total"] = ToDec(r.GetValueOrDefault("Total")),
            ["Status"] = r.GetValueOrDefault("Status"),
            ["Currency"] = r.GetValueOrDefault("Currency"),
        }).ToList();
        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Convert.ToString(row["DocNo"], CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row["PartyCode"], CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row["PartyName"], CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["PartyName"] = T("Reports.Totals"),
                ["Subtotal"] = rows.Sum(x => ToDec(x["Subtotal"])),
                ["Tax"] = rows.Sum(x => ToDec(x["Tax"])),
                ["Total"] = rows.Sum(x => ToDec(x["Total"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/RepCollectionsWin.cs ----
/// <summary>Collections per sales rep: payment allocations settled against that rep's invoices (party_ledger carries no rep column).</summary>
public sealed class RepCollectionsWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "SalesRep.SalesRepCollections",
        TitleTextKey = "Reports.SalesRepCollections",
        Columns = new RptCol[]
        {
            new() { Key = "Rep", HeaderTextKey = "Reports.Col.Rep", Type = RptColType.Text, Width = 200 },
            new() { Key = "AllocationCount", HeaderTextKey = "Reports.Col.Count", Type = RptColType.WholeNumber, Width = 110 },
            new() { Key = "Collected", HeaderTextKey = "Reports.Col.Collected", Type = RptColType.Money, Width = 140 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.SalesRepCollectionsAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Rep")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/RepMovementWin.cs ----
/// <summary>Posted sales documents grouped by sales rep (sales_rep_name on the document header).</summary>
public sealed class RepMovementWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "SalesRep.SalesRepMovement",
        TitleTextKey = "Reports.SalesRepMovement",
        Columns = new RptCol[]
        {
            new() { Key = "Rep", HeaderTextKey = "Reports.Col.Rep", Type = RptColType.Text, Width = 200 },
            new() { Key = "DocCount", HeaderTextKey = "Reports.Col.DocCount", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "Subtotal", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 120 },
            new() { Key = "Discount", HeaderTextKey = "Reports.Col.Discount", Type = RptColType.Money, Width = 110 },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 110 },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 130 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.SalesRepMovementAsync(from, to, ct);
        var def = BuildDefinition();
        var rows = RptWinUtil.Map(raw, def.Columns);
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Rep")
        };
    }
}

// ---- merged from cod/app/src/Views/Reports/SalListWin.cs ----
public sealed class SalListWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Sales.InvoiceList",
        TitleTextKey = "Reports.SalesInvoiceList",
        Columns = new RptCol[]
        {
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "Kind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 70 },
            new() { Key = "PartyCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 100 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 200 },
            new() { Key = "Subtotal", HeaderTextKey = "Reports.Col.Subtotal", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Tax", HeaderTextKey = "Reports.Col.Tax", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "Total", HeaderTextKey = "Reports.Col.Total", Type = RptColType.Money, Width = 110, Format = "N2" },
            new() { Key = "Status", HeaderTextKey = "Reports.Col.Status", Type = RptColType.Text, Width = 90 },
            new() { Key = "Currency", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 70 },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        var raw = await RptQuery.SalesInvoiceListAsync(from, to, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["DocDate"] = r.GetValueOrDefault("DocDate"),
            ["DocNo"] = r.GetValueOrDefault("DocNo"),
            ["Kind"] = r.GetValueOrDefault("Kind"),
            ["PartyCode"] = r.GetValueOrDefault("PartyCode"),
            ["PartyName"] = r.GetValueOrDefault("PartyName"),
            ["Subtotal"] = ToDec(r.GetValueOrDefault("Subtotal")),
            ["Tax"] = ToDec(r.GetValueOrDefault("Tax")),
            ["Total"] = ToDec(r.GetValueOrDefault("Total")),
            ["Status"] = r.GetValueOrDefault("Status"),
            ["Currency"] = r.GetValueOrDefault("Currency"),
        }).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["PartyName"] = T("Reports.Totals"),
                ["Subtotal"] = rows.Sum(x => ToDec(x["Subtotal"])),
                ["Tax"] = rows.Sum(x => ToDec(x["Tax"])),
                ["Total"] = rows.Sum(x => ToDec(x["Total"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

/// <summary>
/// Supplier payable aging. Two bases share this window (and its
/// AP.SupplierAging report permission):
///  - Entry date (the historical view): supplier-level buckets from
///    party_ledger entry dates, now measured against a user-chosen
///    as-of date instead of always "today" (Y3).
///  - Due date (Y3): one row per OPEN purchase invoice, aged by its
///    due_date (migration 109; doc date fallback for older invoices)
///    into the same four buckets, base-currency amounts.
/// </summary>
public sealed class SupAgingWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AP.SupplierAging",
        TitleTextKey = "Reports.SupplierAging",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.PartyCode", Width = 100 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.PartyName", Width = 220 },
            new() { Key = "InvoiceNo", HeaderTextKey = "Reports.Col.InvoiceNo", Type = RptColType.Text, Width = 110, VisibleByDefault = false },
            new() { Key = "InvoiceDate", HeaderTextKey = "Reports.Col.InvoiceDate", Type = RptColType.Date, Width = 95, VisibleByDefault = false },
            new() { Key = "DueDate", HeaderTextKey = "Reports.Col.DueDate", Type = RptColType.Date, Width = 95, VisibleByDefault = false },
            new() { Key = "DaysOverdue", HeaderTextKey = "Reports.Col.DaysOverdue", Type = RptColType.WholeNumber, Width = 95, VisibleByDefault = false },
            new() { Key = "OpenAmount", HeaderTextKey = "Reports.Col.OpenAmount", Type = RptColType.Money, Width = 110, Format = "N2", VisibleByDefault = false },
            new() { Key = "B0", HeaderTextKey = "Reports.Col.Aging0_30", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B30", HeaderTextKey = "Reports.Col.Aging31_60", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B60", HeaderTextKey = "Reports.Col.Aging61_90", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "B90", HeaderTextKey = "Reports.Col.Aging90Plus", Type = RptColType.Money, Width = 100, Format = "N2" },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 110, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new()
            {
                Key = "Mode", LabelTextKey = "Reports.Filter.AgingBasis", Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("EntryDate", "Reports.AgingByEntryDate"),
                    new KeyValuePair<string, string>("DueDate", "Reports.AgingByDueDate")
                },
                DefaultValue = "EntryDate"
            },
            new() { Key = "AsOf", LabelTextKey = "Reports.Filter.AsOf", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var asOf = filters.GetDateOr("AsOf", DateTime.Today).Date;
        var byDueDate = string.Equals(filters.GetString("Mode"), "DueDate", StringComparison.OrdinalIgnoreCase);
        var rows = byDueDate
            ? await LoadDueDateRowsAsync(asOf, ct)
            : await LoadEntryDateRowsAsync(asOf, ct);

        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Contains(row, "Code", search) ||
                Contains(row, "Name", search) ||
                Contains(row, "InvoiceNo", search)).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["OpenAmount"] = rows.Sum(x => Dec(x.GetValueOrDefault("OpenAmount"))),
                ["B0"] = rows.Sum(x => Dec(x.GetValueOrDefault("B0"))),
                ["B30"] = rows.Sum(x => Dec(x.GetValueOrDefault("B30"))),
                ["B60"] = rows.Sum(x => Dec(x.GetValueOrDefault("B60"))),
                ["B90"] = rows.Sum(x => Dec(x.GetValueOrDefault("B90"))),
                ["Balance"] = rows.Sum(x => Dec(x.GetValueOrDefault("Balance"))),
            }
        };
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> LoadEntryDateRowsAsync(DateTime asOf, CancellationToken ct)
    {
        var raw = await RptQuery.PartyAgingAsync(customers: false, asOf, ct);
        return raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Code"] = r.GetValueOrDefault("Code"),
            ["Name"] = r.GetValueOrDefault("Name"),
            ["B0"] = Dec(r.GetValueOrDefault("Bucket0_30")),
            ["B30"] = Dec(r.GetValueOrDefault("Bucket31_60")),
            ["B60"] = Dec(r.GetValueOrDefault("Bucket61_90")),
            ["B90"] = Dec(r.GetValueOrDefault("Bucket90Plus")),
            ["Balance"] = Dec(r.GetValueOrDefault("Balance")),
        }).ToList();
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> LoadDueDateRowsAsync(DateTime asOf, CancellationToken ct)
    {
        var raw = await RptQuery.PayableDueDateAgingAsync(asOf, ct);
        return raw.Select(r =>
        {
            // Buckets carry the BASE open amount (the entry-date mode's
            // basis); OpenAmount keeps the document-currency figure.
            var openBase = Dec(r.GetValueOrDefault("OpenBaseAmount"));
            var bucket = Convert.ToString(r.GetValueOrDefault("Bucket"), CultureInfo.InvariantCulture);
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = r.GetValueOrDefault("SupplierCode"),
                ["Name"] = r.GetValueOrDefault("SupplierName"),
                ["InvoiceNo"] = r.GetValueOrDefault("InvoiceNo"),
                ["InvoiceDate"] = r.GetValueOrDefault("InvoiceDate"),
                ["DueDate"] = r.GetValueOrDefault("DueDate"),
                ["DaysOverdue"] = Convert.ToInt64(r.GetValueOrDefault("DaysOverdue") ?? 0L, CultureInfo.InvariantCulture),
                ["OpenAmount"] = Dec(r.GetValueOrDefault("OpenAmount")),
                ["B0"] = bucket == "Bucket0_30" ? openBase : 0m,
                ["B30"] = bucket == "Bucket31_60" ? openBase : 0m,
                ["B60"] = bucket == "Bucket61_90" ? openBase : 0m,
                ["B90"] = bucket == "Bucket90Plus" ? openBase : 0m,
                ["Balance"] = openBase,
            };
        }).ToList();
    }

    private static bool Contains(IReadOnlyDictionary<string, object?> row, string key, string search)
        => Convert.ToString(row.GetValueOrDefault(key), CultureInfo.InvariantCulture)
            ?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private static decimal Dec(object? v) => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/SupBalWin.cs ----
public sealed class SupBalWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AP.SupplierBalances",
        TitleTextKey = "Reports.SupplierBalances",
        Columns = new RptCol[]
        {
            new() { Key = "Code", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 110 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 260 },
            new() { Key = "Phone", HeaderTextKey = "Reports.Col.Phone", Type = RptColType.Text, Width = 120 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 130, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var lang = App.CurrentLanguageCode ?? "ar";
        var raw = await RptQuery.SupplierBalancesAsync(ct);
        var rows = raw.Select(r =>
        {
            var name = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture)
                : Convert.ToString(r.GetValueOrDefault("NameEn"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name))
                name = Convert.ToString(r.GetValueOrDefault("Name"), CultureInfo.InvariantCulture);
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Code"] = r.GetValueOrDefault("Code"),
                ["Name"] = name,
                ["Phone"] = r.GetValueOrDefault("Phone"),
                ["Balance"] = ToDec(r.GetValueOrDefault("Balance")),
            };
        }).ToList();
        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Convert.ToString(row["Code"], CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row["Name"], CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row["Phone"], CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Name"] = T("Reports.Totals"),
                ["Balance"] = rows.Sum(x => ToDec(x["Balance"])),
            }
        };
    }

    private static decimal ToDec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
}

// ---- merged from cod/app/src/Views/Reports/SupStatementWin.cs ----
/// <summary>Supplier ledger statement with base-currency opening and running balances.</summary>
public sealed class SupStatementWin : RptBase
{
    /// <summary>Optional pre-filter (a party code) applied ahead of the
    /// filter bar's Search value (W1: CstCard's Statement button).</summary>
    public string? InitialSearch { get; set; }

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "AP.SupplierStatement",
        TitleTextKey = "Reports.SupplierStatement",
        Columns = new RptCol[]
        {
            new() { Key = "SupplierCode", HeaderTextKey = "Reports.Col.PartyCode", Type = RptColType.Text, Width = 95 },
            new() { Key = "SupplierName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 180 },
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 95 },
            new() { Key = "DocumentNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 110 },
            new() { Key = "DocumentKind", HeaderTextKey = "Reports.Col.DocKind", Type = RptColType.Text, Width = 75 },
            new() { Key = "Note", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 180 },
            new() { Key = "CurrencyCode", HeaderTextKey = "Reports.Col.Currency", Type = RptColType.Text, Width = 75 },
            new() { Key = "DebitBase", HeaderTextKey = "Reports.Col.DebitBase", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "CreditBase", HeaderTextKey = "Reports.Col.CreditBase", Type = RptColType.Money, Width = 105, Format = "N2" },
            new() { Key = "OpeningBalanceBase", HeaderTextKey = "Reports.Col.OpeningBalance", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "RunningBalanceBase", HeaderTextKey = "Reports.Col.RunningBalance", Type = RptColType.Money, Width = 125, Format = "N2" },
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1));
        var to = filters.GetDateOr("To", DateTime.Today);
        if (from.Date > to.Date)
            throw new ArgumentException("The start date must not be after the end date.");

        var search = string.IsNullOrWhiteSpace(InitialSearch) ? filters.GetString("Search") : InitialSearch;
        var raw = await RptQuery.SupplierStatementAsync(from, to, search, ct);
        var rows = RptWinUtil.Map(raw, BuildDefinition().Columns);
        var openingBySupplier = rows
            .GroupBy(row => Convert.ToString(row.GetValueOrDefault("SupplierCode"), CultureInfo.InvariantCulture) ?? string.Empty)
            .ToDictionary(group => group.Key, group => RptWinUtil.Dec(group.First().GetValueOrDefault("OpeningBalanceBase")), StringComparer.Ordinal);
        var totals = new Dictionary<string, object?>
        {
            ["Note"] = T("Reports.Totals"),
            ["DebitBase"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("DebitBase"))),
            ["CreditBase"] = rows.Sum(row => RptWinUtil.Dec(row.GetValueOrDefault("CreditBase"))),
        };
        if (openingBySupplier.Count > 0)
            totals["OpeningBalanceBase"] = openingBySupplier.Values.Sum();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = rows.Count == 0 ? null : totals
        };
    }
}
