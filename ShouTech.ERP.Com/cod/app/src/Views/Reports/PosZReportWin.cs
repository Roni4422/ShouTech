using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>
/// POS session Z report (Build POS 066): one session's real figures —
/// sales / returns counts and totals, tenders by type, refunds, tax,
/// discounts, and the drawer expectation against the counted close
/// (variance = counted − expected). Data comes from PosRepository's
/// session aggregation over pos_sale_tenders + the session's own
/// documents — never a company-wide day guess. The filter is the
/// session number; blank = the most recent session.
/// </summary>
public sealed class PosZReportWin : RptBase
{
    protected override string? ScheduleTintName => "Peach";

    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "POS.ZReport",
        TitleTextKey = "POS.ZReportTitle",
        Columns = new RptCol[]
        {
            new() { Key = "Item", HeaderTextKey = "POS.ZCol.Item", Type = RptColType.Text, Width = 260 },
            new() { Key = "Count", HeaderTextKey = "POS.ZCol.Count", Type = RptColType.WholeNumber, Width = 100 },
            new() { Key = "Amount", HeaderTextKey = "POS.ZCol.Amount", Type = RptColType.Money, Width = 150 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Session", LabelTextKey = "POS.SessionNo", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var service = AppHost.Pos;
        if (service is not null)
        {
            var wanted = filters.GetString("Session");
            var sessions = await service.GetSessionsAsync(CurrentCompanyCode, ct);
            var session = string.IsNullOrWhiteSpace(wanted)
                ? (sessions.Count > 0 ? sessions[0] : null)
                : sessions.FirstOrDefault(s => string.Equals(s.SessionNo, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
            if (session is not null)
            {
                var z = await service.GetZReportAsync(CurrentCompanyCode, session.Id, ct);
                void Row(string key, int? count, decimal amount) => rows.Add(new Dictionary<string, object?>
                {
                    ["Item"] = T(key), ["Count"] = count, ["Amount"] = amount
                });
                Row("POS.ZSales", z.SalesCount, z.SalesTotal);
                Row("POS.ZReturns", z.ReturnsCount, -z.ReturnsTotal);
                Row("POS.ZNetSales", z.SalesCount - z.ReturnsCount, z.NetSales);
                Row("POS.ZCash", null, z.CashTendered);
                Row("POS.ZCard", null, z.CardTendered);
                Row("POS.ZOnAccount", null, z.OnAccountTendered);
                Row("POS.ZCashRefunded", null, -z.CashRefunded);
                Row("POS.ZCardRefunded", null, -z.CardRefunded);
                Row("POS.ZPaidOuts", z.PaidOutsCount, -z.PaidOutsTotal);
                Row("POS.ZTax", null, z.TaxTotal);
                Row("POS.ZDiscount", null, z.DiscountTotal);
                Row("POS.ZOpening", null, z.OpeningCash);
                Row("POS.ZExpected", null, z.ExpectedCash);
                if (z.CountedCash is decimal counted) Row("POS.ZCounted", null, counted);
                if (z.Variance is decimal variance) Row("POS.ZVariance", null, variance);
            }
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = null
        };
    }
}
