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

/// <summary>
/// Optional print styling for a report page set (B2). A null style
/// on the render/export models means "the built-in look, unchanged"
/// — only designed (custom) reports carry one. Colors are CSS hex
/// strings ("#51708E"); empty font family means the app font.
/// </summary>
public sealed class RptStyle
{
    public string? TitleFontFamily { get; init; }
    public double TitleFontSize { get; init; } = 20;
    public bool TitleBold { get; init; } = true;
    public string? HeaderFontFamily { get; init; }
    public double HeaderFontSize { get; init; } = 10.5;
    public bool HeaderBold { get; init; } = true;
    public string? HeaderBackColor { get; init; }
    public string? HeaderTextColor { get; init; }
    public string? BodyFontFamily { get; init; }
    public double BodyFontSize { get; init; } = 10;
    public string? TotalsFontFamily { get; init; }
    public double TotalsFontSize { get; init; } = 10;
    public bool TotalsBold { get; init; } = true;
    public string? BandingColor { get; init; }
    public bool ShowGridLines { get; init; } = true;
    public string PageSize { get; init; } = "A4";
    public double MarginMm { get; init; } = 12;
    public bool IsLandscape { get; init; }
    public bool ShowCompanyAddress { get; init; }
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

        /// <summary>
        /// Cell-level formula for the totals row / group subtotals
        /// (B2): an expression over the column aggregates, referenced
        /// by field key (e.g. [Profit] / [Sales] * 100 where the
        /// bracketed keys resolve to the columns' aggregate values).
        /// Evaluated at render time; validated on save.
        /// </summary>
        public string? TotalsFormula { get; set; }

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
            TotalsFormula = TotalsFormula,
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

        // ── B2 styling + page setup (migration 115) ──
        public string? TitleFontFamily { get; set; }
        public double TitleFontSize { get; set; } = 20;
        public bool TitleBold { get; set; } = true;
        public string? HeaderFontFamily { get; set; }
        public double HeaderFontSize { get; set; } = 10.5;
        public bool HeaderBold { get; set; } = true;
        public string? HeaderBackColor { get; set; }
        public string? HeaderTextColor { get; set; }
        public string? BodyFontFamily { get; set; }
        public double BodyFontSize { get; set; } = 10;
        public string? TotalsFontFamily { get; set; }
        public double TotalsFontSize { get; set; } = 10;
        public bool TotalsBold { get; set; } = true;
        public string? BandingColor { get; set; }
        public bool ShowGridLines { get; set; } = true;
        public string PageSize { get; set; } = "A4";
        public double MarginMm { get; set; } = 12;
        public bool ShowCompanyAddress { get; set; }

        /// <summary>Projects the stored styling onto the renderer's
        /// style contract (RptRend / XptServ).</summary>
        public RptStyle ToStyle() => new()
        {
            TitleFontFamily = TitleFontFamily,
            TitleFontSize = TitleFontSize,
            TitleBold = TitleBold,
            HeaderFontFamily = HeaderFontFamily,
            HeaderFontSize = HeaderFontSize,
            HeaderBold = HeaderBold,
            HeaderBackColor = HeaderBackColor,
            HeaderTextColor = HeaderTextColor,
            BodyFontFamily = BodyFontFamily,
            BodyFontSize = BodyFontSize,
            TotalsFontFamily = TotalsFontFamily,
            TotalsFontSize = TotalsFontSize,
            TotalsBold = TotalsBold,
            BandingColor = BandingColor,
            ShowGridLines = ShowGridLines,
            PageSize = PageSize,
            MarginMm = MarginMm,
            IsLandscape = IsLandscape,
            ShowCompanyAddress = ShowCompanyAddress
        };

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
                IsSystem = false,
                TitleFontFamily = TitleFontFamily,
                TitleFontSize = TitleFontSize,
                TitleBold = TitleBold,
                HeaderFontFamily = HeaderFontFamily,
                HeaderFontSize = HeaderFontSize,
                HeaderBold = HeaderBold,
                HeaderBackColor = HeaderBackColor,
                HeaderTextColor = HeaderTextColor,
                BodyFontFamily = BodyFontFamily,
                BodyFontSize = BodyFontSize,
                TotalsFontFamily = TotalsFontFamily,
                TotalsFontSize = TotalsFontSize,
                TotalsBold = TotalsBold,
                BandingColor = BandingColor,
                ShowGridLines = ShowGridLines,
                PageSize = PageSize,
                MarginMm = MarginMm,
                ShowCompanyAddress = ShowCompanyAddress
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
