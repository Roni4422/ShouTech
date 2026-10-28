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
        public int? ReportDefID { get; set; }           // NULL = لم يُحفظ بعد
        public string ReportKey { get; set; } = string.Empty;   // التقرير الأساسي (مثل SALES_BY_ITEM)
        public string ReportName { get; set; } = string.Empty;  // الاسم الذي اختاره المستخدم
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
                ReportDefID = null,                     // نسخة جديدة — هوية جديدة عند الحفظ
                ReportKey = ReportKey,
                ReportName = newName,
                Description = Description,
                LanguageCode = LanguageCode,
                OwnerUserID = ownerUserId,
                Visibility = ReportVisibility.Private,  // النسخ تبدأ خاصة دائماً
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
