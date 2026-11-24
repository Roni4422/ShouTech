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
/// Sales-chain exception report (sales cycle upgrade B5, gap G22) —
/// one read-only RptBase window with two sections chosen in the
/// Section filter:
///   OrderedNotDelivered  — Posted sales orders whose remaining-to-
///                          fulfil quantity is &gt; 0, with the promised
///                          delivery date and an overdue flag when it
///                          is before today.
///   DeliveredNotInvoiced — Posted delivery notes with uninvoiced
///                          quantity remaining, valued at the delivery
///                          lines' own prices.
/// Quantities are computed from sales_documents + document_links with
/// the SAME allocation rule OrderDocumentRepository.GetProgressAsync
/// applies per document: linked Posted target quantities are keyed by
/// material (line number when a line has no material), consumed
/// against source lines in line order, and capped at each line's
/// quantity. The data layer (RptQuery.SalesOrderLinesAsync /
/// SalesDeliveryLinesAsync / SalesLinkedQuantitiesAsync) is set-based
/// over the whole company — no per-document repository loop.
/// Access is gated on SALES.ORDER.VIEW in
/// RptNav.OpenSalesChainExceptions, the key the sales order windows use.
/// </summary>
public sealed class SalExceptWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Sales.ChainExceptions",
        TitleTextKey = "Reports.SalesChainExceptions",
        Columns = new RptCol[]
        {
            new() { Key = "Section", HeaderTextKey = "Reports.Col.Section", Type = RptColType.Text, Width = 170 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 120 },
            new() { Key = "DocDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "PartyName", HeaderTextKey = "Reports.Col.PartyName", Type = RptColType.Text, Width = 200 },
            new() { Key = "OrderedQty", HeaderTextKey = "Reports.Col.OrderedQty", Type = RptColType.FractionalNumber, Width = 100, Format = "N3" },
            new() { Key = "DeliveredQty", HeaderTextKey = "Reports.Col.DeliveredQty", Type = RptColType.FractionalNumber, Width = 105, Format = "N3" },
            new() { Key = "InvoicedQty", HeaderTextKey = "Reports.Col.InvoicedQty", Type = RptColType.FractionalNumber, Width = 105, Format = "N3" },
            new() { Key = "RemainingQty", HeaderTextKey = "Reports.Col.RemainingQty", Type = RptColType.FractionalNumber, Width = 105, Format = "N3" },
            new() { Key = "UninvoicedValue", HeaderTextKey = "Reports.Col.UninvoicedValue", Type = RptColType.Money, Width = 125, Format = "N2" },
            new() { Key = "DeliveryDate", HeaderTextKey = "Reports.Col.DeliveryDate", Type = RptColType.Date, Width = 105 },
            new() { Key = "Overdue", HeaderTextKey = "Reports.Col.Overdue", Type = RptColType.Text, Width = 80 },
        },
        Filters = new RptFlt[]
        {
            new()
            {
                Key = "Section", LabelTextKey = "Reports.Filter.Section", Kind = RptFltKind.Choice,
                Choices = new[]
                {
                    new KeyValuePair<string, string>("OrderedNotDelivered", "Reports.SectionOrderedNotDelivered"),
                    new KeyValuePair<string, string>("DeliveredNotInvoiced", "Reports.SectionDeliveredNotInvoiced")
                },
                DefaultValue = "OrderedNotDelivered"
            },
            new() { Key = "Search", LabelTextKey = "Reports.Filter.Search", Kind = RptFltKind.Text },
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var delivered = string.Equals(filters.GetString("Section"), "DeliveredNotInvoiced", StringComparison.Ordinal);
        var rows = delivered
            ? await LoadDeliveredNotInvoicedAsync(ct)
            : await LoadOrderedNotDeliveredAsync(ct);

        var search = filters.GetString("Search")?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            rows = rows.Where(row =>
                Convert.ToString(row.GetValueOrDefault("DocNo"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                Convert.ToString(row.GetValueOrDefault("PartyName"), CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true).ToList();

        return new RptData
        {
            Columns = BuildDefinition().Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>
            {
                ["DocNo"] = T("Reports.Totals"),
                ["OrderedQty"] = rows.Sum(x => RptWinUtil.Dec(x.GetValueOrDefault("OrderedQty"))),
                ["DeliveredQty"] = rows.Sum(x => RptWinUtil.Dec(x.GetValueOrDefault("DeliveredQty"))),
                ["InvoicedQty"] = rows.Sum(x => RptWinUtil.Dec(x.GetValueOrDefault("InvoicedQty"))),
                ["RemainingQty"] = rows.Sum(x => RptWinUtil.Dec(x.GetValueOrDefault("RemainingQty"))),
                ["UninvoicedValue"] = rows.Sum(x => RptWinUtil.Dec(x.GetValueOrDefault("UninvoicedValue"))),
            }
        };
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> LoadOrderedNotDeliveredAsync(CancellationToken ct)
    {
        var lines = await RptQuery.SalesOrderLinesAsync(ct);
        var delivered = await RptQuery.SalesLinkedQuantitiesAsync("SO", "DLV", ct);
        var available = BuildAvailable(delivered);
        var today = DateTime.Today;
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var doc in GroupLines(lines))
        {
            decimal ordered = 0m, fulfilled = 0m;
            foreach (var line in doc.Lines)
            {
                var qty = RptWinUtil.Dec(line.GetValueOrDefault("Qty"));
                ordered += qty;
                fulfilled += Consume(available, doc.DocId, line, qty);
            }
            var remaining = ordered - fulfilled;
            if (remaining <= 0.0001m)
                continue;
            var first = doc.Lines[0];
            var deliveryDate = RptWinUtil.ParseDate(first.GetValueOrDefault("DeliveryDate"))?.Date;
            rows.Add(new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.SectionOrderedNotDelivered"),
                ["DocNo"] = Text(first, "DocNo"),
                ["DocDate"] = RptWinUtil.ParseDate(first.GetValueOrDefault("DocDate"))?.Date,
                ["PartyName"] = Text(first, "PartyName"),
                ["OrderedQty"] = ordered,
                ["DeliveredQty"] = fulfilled,
                ["RemainingQty"] = remaining,
                ["DeliveryDate"] = deliveryDate,
                ["Overdue"] = deliveryDate is not null && deliveryDate.Value < today
                    ? T("Reports.Col.Overdue")
                    : string.Empty,
            });
        }
        return rows;
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> LoadDeliveredNotInvoicedAsync(CancellationToken ct)
    {
        var lines = await RptQuery.SalesDeliveryLinesAsync(ct);
        var invoiced = await RptQuery.SalesLinkedQuantitiesAsync("DLV", "SAL", ct);
        var available = BuildAvailable(invoiced);
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var doc in GroupLines(lines))
        {
            decimal deliveredQty = 0m, invoicedQty = 0m, uninvoicedValue = 0m;
            foreach (var line in doc.Lines)
            {
                var qty = RptWinUtil.Dec(line.GetValueOrDefault("Qty"));
                deliveredQty += qty;
                var billed = Consume(available, doc.DocId, line, qty);
                invoicedQty += billed;
                var remainingLine = qty - billed;
                if (remainingLine > 0m && qty > 0m)
                    uninvoicedValue += remainingLine * RptWinUtil.Dec(line.GetValueOrDefault("LineTotal")) / qty;
            }
            var remaining = deliveredQty - invoicedQty;
            if (remaining <= 0.0001m)
                continue;
            var first = doc.Lines[0];
            rows.Add(new Dictionary<string, object?>
            {
                ["Section"] = T("Reports.SectionDeliveredNotInvoiced"),
                ["DocNo"] = Text(first, "DocNo"),
                ["DocDate"] = RptWinUtil.ParseDate(first.GetValueOrDefault("DocDate"))?.Date,
                ["PartyName"] = Text(first, "PartyName"),
                ["DeliveredQty"] = deliveredQty,
                ["InvoicedQty"] = invoicedQty,
                ["RemainingQty"] = remaining,
                ["UninvoicedValue"] = uninvoicedValue,
            });
        }
        return rows;
    }

    // ------------------------------------------------------------------
    // Allocation helpers — mirror OrderDocumentRepository.GetProgressAsync
    // (sales branch): linked quantities are keyed by material_id, or by
    // -line_no for lines without a material, and consumed against the
    // source document's lines in line order, capped per line.
    // ------------------------------------------------------------------

    private sealed class DocLines
    {
        public required long DocId { get; init; }
        public List<IReadOnlyDictionary<string, object?>> Lines { get; } = new();
    }

    private static List<DocLines> GroupLines(IReadOnlyList<IReadOnlyDictionary<string, object?>> lines)
    {
        var docs = new List<DocLines>();
        var byId = new Dictionary<long, DocLines>();
        foreach (var line in lines)
        {
            var docId = ToLong(line.GetValueOrDefault("DocId"));
            if (!byId.TryGetValue(docId, out var doc))
            {
                doc = new DocLines { DocId = docId };
                byId[docId] = doc;
                docs.Add(doc);
            }
            doc.Lines.Add(line);
        }
        return docs;
    }

    /// <summary>Available linked quantity per (source doc, line key).</summary>
    private static Dictionary<(long DocId, long Key), decimal> BuildAvailable(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> linkedRows)
    {
        var map = new Dictionary<(long, long), decimal>();
        foreach (var row in linkedRows)
        {
            var key = (ToLong(row.GetValueOrDefault("SourceId")), LineKey(row));
            map[key] = (map.TryGetValue(key, out var existing) ? existing : 0m)
                + RptWinUtil.Dec(row.GetValueOrDefault("Qty"));
        }
        return map;
    }

    /// <summary>Consumes up to the line's quantity from the available
    /// linked quantity for the line's key; returns the fulfilled part.</summary>
    private static decimal Consume(
        Dictionary<(long DocId, long Key), decimal> available,
        long docId,
        IReadOnlyDictionary<string, object?> line,
        decimal lineQty)
    {
        var key = (docId, LineKey(line));
        var avail = available.GetValueOrDefault(key);
        var fulfilled = Math.Min(lineQty, avail);
        if (fulfilled > 0m)
            available[key] = avail - fulfilled;
        return fulfilled;
    }

    private static long LineKey(IReadOnlyDictionary<string, object?> row)
        => ToLongOrNull(row.GetValueOrDefault("MaterialId")) ?? -ToLong(row.GetValueOrDefault("LineNo"));

    private static string Text(IReadOnlyDictionary<string, object?> row, string key)
        => Convert.ToString(row.GetValueOrDefault(key), CultureInfo.InvariantCulture) ?? string.Empty;

    private static long ToLong(object? v)
        => v is null ? 0L : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    private static long? ToLongOrNull(object? v)
        => v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
}
