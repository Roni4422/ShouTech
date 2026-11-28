using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ShouTech.App.Reporting;

namespace ShouTech.App.Views.Reports;

/// <summary>Shared row-mapping helpers for Chunk E report windows.</summary>
internal static class RptWinUtil
{
    public static decimal Dec(object? v)
        => v is null ? 0m : Convert.ToDecimal(v, CultureInfo.InvariantCulture);

    public static bool IsNum(RptColType t)
        => t is RptColType.Money or RptColType.FractionalNumber or RptColType.WholeNumber or RptColType.Percent;

    /// <summary>Copies raw query rows into grid rows, converting numeric columns to decimal.</summary>
    public static List<IReadOnlyDictionary<string, object?>> Map(
        IEnumerable<IReadOnlyDictionary<string, object?>> raw, IReadOnlyList<RptCol> cols)
    {
        var list = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var r in raw)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var c in cols)
            {
                var v = r.GetValueOrDefault(c.Key);
                d[c.Key] = IsNum(c.Type) ? Dec(v) : v;
            }
            // Keep non-visible journal navigation keys in the row model.
            // They are not rendered as columns, but RptBase uses them to
            // open the exact posted journal entry on a double-click.
            foreach (var key in new[] { "JournalDocNo", "JournalDate" })
                if (r.TryGetValue(key, out var metadata))
                    d[key] = metadata;
            list.Add(d);
        }
        return list;
    }

    /// <summary>Builds the bottom totals line: label in <paramref name="labelKey"/>, sums of numeric columns except excluded ones.</summary>
    public static Dictionary<string, object?>? SumTotals(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<RptCol> cols,
        string label, string labelKey, params string[] exclude)
    {
        if (rows.Count == 0) return null;
        var t = new Dictionary<string, object?>(StringComparer.Ordinal) { [labelKey] = label };
        foreach (var c in cols)
        {
            if (!IsNum(c.Type) || c.Type == RptColType.Percent || exclude.Contains(c.Key)) continue;
            t[c.Key] = rows.Sum(r => Dec(r.GetValueOrDefault(c.Key)));
        }
        return t.Count > 1 ? t : null;
    }

    public static int ParseInt(string? s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static DateTime? ParseDate(object? v)
    {
        if (v is DateTime d) return d;
        var s = Convert.ToString(v, CultureInfo.InvariantCulture);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p : null;
    }
}
