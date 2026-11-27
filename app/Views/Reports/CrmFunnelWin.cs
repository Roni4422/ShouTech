using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// CRM funnel report (sales cycle upgrade B5, gap G22) — one read-only
/// RptBase window with four sections chosen in the Section filter:
///   Pipeline  — open opportunities per stage: count, raw expected
///               total and probability-weighted total
///               (RptQuery.CrmPipelineByStageAsync).
///   WinRate   — won / (won + lost) per sales rep over the From-To
///               period (RptQuery.CrmWinRateByRepAsync).
///   Stale     — open opportunities with no activity for at least the
///               selected number of days; last activity is the newest
///               crm_activities timestamp, falling back to the
///               opportunity's own updated_at (RptQuery.CrmOpenOpportunitiesAsync).
///   LostQuotes— QUO documents with status Lost/Expired plus Posted
///               quotations whose valid_till is in the past
///               (RptQuery.CrmLostExpiredQuotationsAsync).
///   Forecast  — open opportunities by expected close month and owner
///               rep, weighted by STAGE probability (W10 Level 3;
///               RptQuery.CrmForecastByMonthAsync).
/// Access is gated on CRM.OPP.VIEW in RptNav.OpenCrmFunnel, the same
/// key the CRM pipeline/opportunity windows require.
/// </summary>
public sealed class CrmFunnelWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "CRM.Funnel",
        TitleTextKey = "Reports.CrmFunnel",
        Columns = new RptCol[]
        {
            new() { Key = "Section", HeaderTextKey = "Reports.Col.Section", Type = RptColType.Text, Width = 170 },
            new() { Key = "Name", HeaderTextKey = "Reports.Col.Name", Type = RptColType.Text, Width = 230 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 180 },
            new() { Key = "Count", HeaderTextKey = "Reports.Col.Count", Type = RptColType.WholeNumber, Width = 80 },
            new() { Key = "Won", HeaderTextKey = "Reports.Col.Won", Type = RptColType.WholeNumber, Width = 70 },
            new() { Key = "Lost", HeaderTextKey = "Reports.Col.Lost", Type = RptColType.WholeNumber, Width = 70 },
            new() { Key = "WinRate", HeaderTextKey = "Reports.Col.WinRate", Type = RptColType.Percent, Width = 90 },
            new() { Key = "Amount", HeaderTextKey = "Reports.Col.Amount", Type = RptColType.Money, Width = 120, Format = "N2" },
            new() { Key = "WeightedAmount", HeaderTextKey = "Reports.Col.WeightedAmount", Type = RptColType.Money, Width = 130, Format = "N2" },
            new() { Key = "DaysIdle", HeaderTextKey = "Reports.Col.DaysIdle", Type = RptColType.WholeNumber, Width = 85 },
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "LastActivity", HeaderTextKey = "Reports.Col.LastActivity", Type = RptColType.Date, Width = 110 },
            new() { Key = "ExpectedClose", HeaderTextKey = "Reports.Col.ExpectedClose", Type = RptColType.Date, Width = 110 },
            new() { Key = "ValidTill", HeaderTextKey = "Reports.Col.ValidTill", Type = RptColType.Date, Width = 100 },
        },
        Filters = new RptFlt[]
        {
            new()
            {
                Key = "Section", LabelTextKey = "Reports.Filter.Section", Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("Pipeline", "Reports.SectionPipeline"),
                    new KeyValuePair<string, string>("WinRate", "Reports.SectionWinRate"),
                    new KeyValuePair<string, string>("Stale", "Reports.SectionStale"),
                    new KeyValuePair<string, string>("LostQuotes", "Reports.SectionLostQuotes"),
                    new KeyValuePair<string, string>("Forecast", "Reports.SectionForecast")
                },
                DefaultValue = "Pipeline"
            },
            new() { Key = "From", LabelTextKey = "Reports.Filter.DateFrom", Kind = RptFltKind.DateFrom, DefaultValue = new DateTime(DateTime.Today.Year, 1, 1) },
            new() { Key = "To", LabelTextKey = "Reports.Filter.DateTo", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today },
            new() { Key = "StaleDays", LabelTextKey = "Reports.Filter.StaleDays", Kind = RptFltKind.Text, DefaultValue = "30" },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var section = filters.GetString("Section") ?? "Pipeline";
        return section switch
        {
            "WinRate" => await LoadWinRateAsync(filters, ct),
            "Stale" => await LoadStaleAsync(filters, ct),
            "LostQuotes" => await LoadLostQuotesAsync(filters, ct),
            "Forecast" => await LoadForecastAsync(ct),
            _ => await LoadPipelineAsync(ct)
        };
    }

    private async Task<RptData> LoadForecastAsync(CancellationToken ct)
    {
        var raw = await RptQuery.CrmForecastByMonthAsync(ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Section"] = T("Reports.SectionForecast"),
            ["Name"] = Convert.ToString(r.GetValueOrDefault("CloseMonth"), CultureInfo.InvariantCulture) ?? string.Empty,
            ["PartyName"] = Convert.ToString(r.GetValueOrDefault("Rep"), CultureInfo.InvariantCulture) is { Length: > 0 } rep ? rep : "—",
            ["Count"] = RptWinUtil.Dec(r.GetValueOrDefault("OppCount")),
            ["Amount"] = RptWinUtil.Dec(r.GetValueOrDefault("RawTotal")),
            ["WeightedAmount"] = RptWinUtil.Dec(r.GetValueOrDefault("WeightedTotal")),
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.Totals"),
                ["Count"] = rows.Sum(x => RptWinUtil.Dec(x["Count"])),
                ["Amount"] = rows.Sum(x => RptWinUtil.Dec(x["Amount"])),
                ["WeightedAmount"] = rows.Sum(x => RptWinUtil.Dec(x["WeightedAmount"])),
            }
        };
    }

    private async Task<RptData> LoadPipelineAsync(CancellationToken ct)
    {
        var raw = await RptQuery.CrmPipelineByStageAsync(ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Section"] = T("Reports.SectionPipeline"),
            ["Name"] = LocalizedName(r, "Stage", "StageAr"),
            ["Count"] = RptWinUtil.Dec(r.GetValueOrDefault("OppCount")),
            ["Amount"] = RptWinUtil.Dec(r.GetValueOrDefault("RawTotal")),
            ["WeightedAmount"] = RptWinUtil.Dec(r.GetValueOrDefault("WeightedTotal")),
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.Totals"),
                ["Count"] = rows.Sum(x => RptWinUtil.Dec(x["Count"])),
                ["Amount"] = rows.Sum(x => RptWinUtil.Dec(x["Amount"])),
                ["WeightedAmount"] = rows.Sum(x => RptWinUtil.Dec(x["WeightedAmount"])),
            }
        };
    }

    private async Task<RptData> LoadWinRateAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1)).Date;
        var to = filters.GetDateOr("To", DateTime.Today).Date;
        var raw = await RptQuery.CrmWinRateByRepAsync(from, to, ct);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var won = RptWinUtil.Dec(r.GetValueOrDefault("Won"));
            var lost = RptWinUtil.Dec(r.GetValueOrDefault("Lost"));
            var closed = won + lost;
            var rep = Convert.ToString(r.GetValueOrDefault("Rep"), CultureInfo.InvariantCulture);
            rows.Add(new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.SectionWinRate"),
                ["Name"] = string.IsNullOrWhiteSpace(rep) ? "—" : rep,
                ["Count"] = closed,
                ["Won"] = won,
                ["Lost"] = lost,
                ["WinRate"] = closed > 0m ? won / closed : 0m,
                ["Amount"] = RptWinUtil.Dec(r.GetValueOrDefault("ClosedAmount")),
            });
        }
        var totalWon = rows.Sum(x => RptWinUtil.Dec(x["Won"]));
        var totalLost = rows.Sum(x => RptWinUtil.Dec(x["Lost"]));
        var totalClosed = totalWon + totalLost;
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.Totals"),
                ["Count"] = totalClosed,
                ["Won"] = totalWon,
                ["Lost"] = totalLost,
                ["WinRate"] = totalClosed > 0m ? totalWon / totalClosed : 0m,
                ["Amount"] = rows.Sum(x => RptWinUtil.Dec(x["Amount"])),
            }
        };
    }

    private async Task<RptData> LoadStaleAsync(RptFltValues filters, CancellationToken ct)
    {
        var staleDays = Math.Max(0, RptWinUtil.ParseInt(filters.GetString("StaleDays"), 30));
        var today = DateTime.Today;
        var raw = await RptQuery.CrmOpenOpportunitiesAsync(ct);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            // Last touch: newest activity, else the opportunity's own
            // last update. No parseable timestamp at all means the row
            // cannot be proven stale — skip it rather than guess.
            var lastTouch = RptWinUtil.ParseDate(r.GetValueOrDefault("LastActivity"))
                ?? RptWinUtil.ParseDate(r.GetValueOrDefault("UpdatedAt"));
            if (lastTouch is null)
                continue;
            var daysIdle = (today - lastTouch.Value.Date).Days;
            if (daysIdle < staleDays)
                continue;
            rows.Add(new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.SectionStale"),
                ["Name"] = Convert.ToString(r.GetValueOrDefault("Title"), CultureInfo.InvariantCulture) ?? string.Empty,
                ["PartyName"] = LocalizedName(r, "PartyName", "PartyNameAr"),
                ["Count"] = 1m,
                ["Amount"] = RptWinUtil.Dec(r.GetValueOrDefault("Amount")),
                ["DaysIdle"] = (decimal)daysIdle,
                ["LastActivity"] = lastTouch.Value.Date,
                ["ExpectedClose"] = RptWinUtil.ParseDate(r.GetValueOrDefault("ExpectedClose"))?.Date,
            });
        }
        rows = rows.OrderByDescending(x => RptWinUtil.Dec(x["DaysIdle"])).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.Totals"),
                ["Count"] = (decimal)rows.Count,
                ["Amount"] = rows.Sum(x => RptWinUtil.Dec(x["Amount"])),
            }
        };
    }

    private async Task<RptData> LoadLostQuotesAsync(RptFltValues filters, CancellationToken ct)
    {
        var from = filters.GetDateOr("From", new DateTime(DateTime.Today.Year, 1, 1)).Date;
        var to = filters.GetDateOr("To", DateTime.Today).Date;
        var raw = await RptQuery.CrmLostExpiredQuotationsAsync(from, to, DateTime.Today, ct);
        var rows = raw.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Section"] = string.Equals(
                Convert.ToString(r.GetValueOrDefault("Status"), CultureInfo.InvariantCulture),
                "Lost", StringComparison.OrdinalIgnoreCase)
                ? T("Reports.StatusLost")
                : T("Reports.StatusExpired"),
            ["Name"] = Convert.ToString(r.GetValueOrDefault("DocNo"), CultureInfo.InvariantCulture) ?? string.Empty,
            ["PartyName"] = Convert.ToString(r.GetValueOrDefault("PartyName"), CultureInfo.InvariantCulture) ?? string.Empty,
            ["Count"] = 1m,
            ["Amount"] = RptWinUtil.Dec(r.GetValueOrDefault("Total")),
            ["DocDate"] = RptWinUtil.ParseDate(r.GetValueOrDefault("DocDate"))?.Date,
            ["ValidTill"] = RptWinUtil.ParseDate(r.GetValueOrDefault("ValidTill"))?.Date,
        }).ToList();
        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.Totals"),
                ["Count"] = (decimal)rows.Count,
                ["Amount"] = rows.Sum(x => RptWinUtil.Dec(x["Amount"])),
            }
        };
    }

    /// <summary>Stage/party names follow the shell language: the Arabic
    /// name in Arabic UI when one is stored, else the query's
    /// English-first display name (the RptQuery convention).</summary>
    private static string LocalizedName(IReadOnlyDictionary<string, object?> row, string nameKey, string arKey)
    {
        var ar = Convert.ToString(row.GetValueOrDefault(arKey), CultureInfo.InvariantCulture);
        if (string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(ar))
            return ar;
        return Convert.ToString(row.GetValueOrDefault(nameKey), CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
