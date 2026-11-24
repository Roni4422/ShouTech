// ========================================================================
// FILE: Reporting/ReportModels.cs
// PROJECT: SHOUTECH ERP — Report designer models
//              CustomReportDefinitions + CustomReportColumns +
//              CustomReportGroupings + CustomReportFilters.
// ========================================================================
#nullable enable

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System;

namespace ShouTech.App.Reporting
{
// ---- merged from cod/app/src/Reporting/RptDef.cs ----
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

    public enum ReportColumnType
    {
        Data,
        Formula,
        RunningTotal,
        RowNumber
    }

    public enum ReportDataFormat
    {
        Text,
        Money,
        Qty,
        Percent,
        Date,
        DateTime
    }

    public enum ReportVisibility
    {
        Private,
        Shared,
        System
    }

    public sealed class ReportColumnDef
    {
        public string FieldKey { get; set; } = string.Empty;
        public string CaptionAR { get; set; } = string.Empty;
        public string? CaptionEN { get; set; }
        public int DisplayOrder { get; set; }

        public double ColumnWidth { get; set; } = 120;

        public bool IsVisible { get; set; } = true;
        public bool IsLocked { get; set; }
        public ReportColumnType ColumnType { get; set; } = ReportColumnType.Data;

        public string? FormulaText { get; set; }

        public ReportDataFormat DataFormat { get; set; } = ReportDataFormat.Text;
        public int DecimalPlaces { get; set; } = 2;
        public string? AggregateFunc { get; set; }      // SUM | AVG | MIN | MAX | COUNT
        public bool NegativeInRed { get; set; } = true;

        [NonSerialized]
        private CompiledFormula? _compiled;

        public CompiledFormula? GetCompiled()
        {
            if (ColumnType != ReportColumnType.Formula || string.IsNullOrWhiteSpace(FormulaText))
                return null;
            return _compiled ??= CompiledFormula.Compile(FormulaText);
        }

        public ReportColumnDef Clone() => new()
        {
            FieldKey = FieldKey,
            CaptionAR = CaptionAR,
            CaptionEN = CaptionEN,
            DisplayOrder = DisplayOrder,
            ColumnWidth = ColumnWidth,
            IsVisible = IsVisible,
            IsLocked = IsLocked,
            ColumnType = ColumnType,
            FormulaText = FormulaText,
            DataFormat = DataFormat,
            DecimalPlaces = DecimalPlaces,
            AggregateFunc = AggregateFunc,
            NegativeInRed = NegativeInRed
        };
    }

    public sealed class ReportGroupingDef
    {
        public string FieldKey { get; set; } = string.Empty;
        public int GroupLevel { get; set; }
        public bool ShowSubTotals { get; set; } = true;
        public bool StartOnNewPage { get; set; }
        public string SortDirection { get; set; } = "ASC";
    }

    public sealed class ReportFilterDef
    {
        public string FilterKey { get; set; } = string.Empty;
        public string FilterLabelAR { get; set; } = string.Empty;
        public string Operator { get; set; } = "EQ";
        public string? ValueFrom { get; set; }
        public string? ValueTo { get; set; }
        public bool IsPrompted { get; set; }
        public bool IsRequired { get; set; }
    }

    /// <summary>
    /// </summary>
    public sealed class ReportDefinition
    {
        public int? ReportDefID { get; set; }           // NULL = not saved yet
        public string ReportKey { get; set; } = string.Empty;   // The base report (e.g. SALES_BY_ITEM)
        public string ReportName { get; set; } = string.Empty;  // The name chosen by the user
        public string? Description { get; set; }
        public string LanguageCode { get; set; } = "ar";
        public int OwnerUserID { get; set; }
        public ReportVisibility Visibility { get; set; } = ReportVisibility.Private;
        public string DataSourceKey { get; set; } = string.Empty;
        public bool IsLandscape { get; set; }
        public bool ShowTotalsRow { get; set; } = true;
        public bool ShowHeaderInfo { get; set; } = true;
        public bool FreezeFirstColumn { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsSystem { get; set; }

        public ObservableCollection<ReportColumnDef> Columns { get; } = new();
        public List<ReportGroupingDef> Groupings { get; } = new();
        public List<ReportFilterDef> Filters { get; } = new();

        public IEnumerable<ReportColumnDef> VisibleColumns =>
            Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayOrder);

        public ReportDefinition CloneForSaveAs(string newName, int ownerUserId)
        {
            var clone = new ReportDefinition
            {
                ReportDefID = null,                     // New copy - new identity on save
                ReportKey = ReportKey,
                ReportName = newName,
                Description = Description,
                LanguageCode = LanguageCode,
                OwnerUserID = ownerUserId,
                Visibility = ReportVisibility.Private,  // Copies always start as private
                DataSourceKey = DataSourceKey,
                IsLandscape = IsLandscape,
                ShowTotalsRow = ShowTotalsRow,
                ShowHeaderInfo = ShowHeaderInfo,
                FreezeFirstColumn = FreezeFirstColumn,
                IsFavorite = false,
                IsSystem = false
            };

            foreach (var col in Columns)
                clone.Columns.Add(col.Clone());

            clone.Groupings.AddRange(Groupings.Select(g => new ReportGroupingDef
            {
                FieldKey = g.FieldKey,
                GroupLevel = g.GroupLevel,
                ShowSubTotals = g.ShowSubTotals,
                StartOnNewPage = g.StartOnNewPage,
                SortDirection = g.SortDirection
            }));

            clone.Filters.AddRange(Filters.Select(f => new ReportFilterDef
            {
                FilterKey = f.FilterKey,
                FilterLabelAR = f.FilterLabelAR,
                Operator = f.Operator,
                ValueFrom = f.ValueFrom,
                ValueTo = f.ValueTo,
                IsPrompted = f.IsPrompted,
                IsRequired = f.IsRequired
            }));

            return clone;
        }
    }
}
