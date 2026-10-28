using System;
using System.Collections.Generic;

namespace ShouTech.App.Reporting;

/// <summary>Logical data type of a report column; drives formatting and alignment.</summary>
public enum RptColType
{
    Text,
    WholeNumber,
    FractionalNumber,
    Money,
    Date,
    DateTime,
    Percent,
    Boolean
}

/// <summary>Kind of filter control generated for a report parameter.</summary>
public enum RptFltKind
{
    DateFrom,
    DateTo,
    Text,
    Choice,
    Warehouse,
    MaterialGroup,
    Material
}

/// <summary>
/// Immutable definition of a single report column.
/// HeaderTextKey is a localization key; headers are never literals.
/// </summary>
public sealed class RptCol
{
    public required string Key { get; init; }
    public required string HeaderTextKey { get; init; }
    public RptColType Type { get; init; } = RptColType.Text;
    public double Width { get; set; } = 140;
    public bool VisibleByDefault { get; init; } = true;
    public bool AllowHide { get; init; } = true;

    /// <summary>Optional custom cell format (e.g. "N2"); null means type default.</summary>
    public string? Format { get; init; }
}

/// <summary>Definition of one filter shown in the report filter bar.</summary>
public sealed class RptFlt
{
    public required string Key { get; init; }
    public required string LabelTextKey { get; init; }
    public RptFltKind Kind { get; init; } = RptFltKind.Text;

    /// <summary>Options for Kind = Choice (value + localization key pairs).</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Choices { get; init; }

    /// <summary>Default value applied when the report opens.</summary>
    public object? DefaultValue { get; init; }
}

/// <summary>Filter values collected from the filter bar at run time.</summary>
public sealed class RptFltValues
{
    private readonly Dictionary<string, object?> _map = new(StringComparer.Ordinal);

    public void Set(string key, object? value) => _map[key] = value;

    public string? GetString(string key)
        => _map.TryGetValue(key, out var v) ? v as string : null;

    public DateTime? GetDate(string key)
        => _map.TryGetValue(key, out var v) && v is DateTime d ? d : null;

    public DateTime GetDateOr(string key, DateTime fallback)
        => GetDate(key) ?? fallback;
}

/// <summary>
/// One loaded report page: ordered rows aligned with <see cref="RptDef.Columns"/>.
/// Each row is a dictionary keyed by column key.
/// </summary>
public sealed class RptData
{
    public required IReadOnlyList<RptCol> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }

    /// <summary>Optional aggregate line rendered under the grid (totals row).</summary>
    public IReadOnlyDictionary<string, object?>? Totals { get; init; }
}

/// <summary>Static description of a report, produced by the concrete report window.</summary>
public sealed class RptDef
{
    /// <summary>Stable unique key, e.g. "Inv.StockStatus". Used for preferences.</summary>
    public required string ReportKey { get; init; }

    /// <summary>Localization key of the report title.</summary>
    public required string TitleTextKey { get; init; }

    public required IReadOnlyList<RptCol> Columns { get; init; }
    public required IReadOnlyList<RptFlt> Filters { get; init; }
}
