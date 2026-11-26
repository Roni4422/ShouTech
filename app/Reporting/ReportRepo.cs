// ========================================================================
// FILE: Reporting/ReportRepo.cs
// PROJECT: SHOUTECH ERP — Report definition persistence
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Services;

namespace ShouTech.App.Reporting
{
    public static class ReportRepo
    {
        private static bool IsPg => DatabaseRuntime.IsPostgreSql();

        private static DbConnection Open()
        {
            var cs = DatabaseRuntime.GetConnectionString()
                ?? throw new InvalidOperationException("Database connection is not configured.");
            return IsPg
                ? new NpgsqlConnection(cs)
                : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(cs));
        }

        private static string Now => IsPg ? "NOW()" : "CURRENT_TIMESTAMP";
        private static string BoolTrue => IsPg ? "TRUE" : "1";

        // ═══════════════════════════════════════════════════════════════
        // LOAD
        // ═══════════════════════════════════════════════════════════════

        public static async Task<IReadOnlyList<ReportDefinition>> ListReportsAsync(
            string reportKey, int userId, CancellationToken ct = default)
        {
            await using var db = Open();
            await db.OpenAsync(ct);

            var rows = await db.QueryAsync<ReportDefRow>(new CommandDefinition($"""
                SELECT ReportDefID, ReportKey, ReportName, Description, LanguageCode,
                       OwnerUserID, Visibility, DataSourceKey, PageOrientation,
                       ShowTotalsRow, ShowHeaderInfo, FreezeFirstColumn, IsFavorite, IsSystem,
                       TitleFontFamily, TitleFontSize, TitleBold, HeaderFontFamily,
                       HeaderFontSize, HeaderBold, HeaderBackColor, HeaderTextColor,
                       BodyFontFamily, BodyFontSize, TotalsFontFamily, TotalsFontSize,
                       TotalsBold, BandingColor, ShowGridLines, PageSize, MarginMm,
                       ShowCompanyAddress
                FROM CustomReportDefinitions
                WHERE IsDeleted = 0 AND ReportKey = @reportKey
                  AND (OwnerUserID = @userId OR Visibility <> 'PRIVATE')
                ORDER BY IsSystem DESC, IsFavorite DESC, ReportName
                """, new { reportKey, userId }, cancellationToken: ct));

            return rows.Select(MapDefinition).ToList();
        }

        public static async Task<ReportDefinition?> LoadReportAsync(
            int reportDefId, int userId, CancellationToken ct = default)
        {
            await using var db = Open();
            await db.OpenAsync(ct);

            var head = await db.QuerySingleOrDefaultAsync<ReportDefRow>(new CommandDefinition("""
                SELECT ReportDefID, ReportKey, ReportName, Description, LanguageCode,
                       OwnerUserID, Visibility, DataSourceKey, PageOrientation,
                       ShowTotalsRow, ShowHeaderInfo, FreezeFirstColumn, IsFavorite, IsSystem,
                       TitleFontFamily, TitleFontSize, TitleBold, HeaderFontFamily,
                       HeaderFontSize, HeaderBold, HeaderBackColor, HeaderTextColor,
                       BodyFontFamily, BodyFontSize, TotalsFontFamily, TotalsFontSize,
                       TotalsBold, BandingColor, ShowGridLines, PageSize, MarginMm,
                       ShowCompanyAddress
                FROM CustomReportDefinitions
                WHERE IsDeleted = 0 AND ReportDefID = @reportDefId
                  AND (OwnerUserID = @userId OR Visibility <> 'PRIVATE')
                """, new { reportDefId, userId }, cancellationToken: ct));

            if (head is null)
                return null;

            var def = MapDefinition(head);

            var cols = await db.QueryAsync<ReportColRow>(new CommandDefinition("""
                SELECT FieldKey, CaptionAR, CaptionEN, DisplayOrder, ColumnWidth,
                       IsVisible, IsLocked, ColumnType, FormulaText, TotalsFormula, DataFormat,
                       DecimalPlaces, AggregateFunc, NegativeInRed
                FROM CustomReportColumns
                WHERE IsDeleted = 0 AND ReportDefID = @reportDefId
                ORDER BY DisplayOrder
                """, new { reportDefId }, cancellationToken: ct));

            foreach (var c in cols)
            {
                def.Columns.Add(new ReportColumnDef
                {
                    FieldKey = c.FieldKey,
                    CaptionAR = c.CaptionAR,
                    CaptionEN = c.CaptionEN,
                    DisplayOrder = c.DisplayOrder,
                    ColumnWidth = (double)c.ColumnWidth,
                    IsVisible = c.IsVisible,
                    IsLocked = c.IsLocked,
                    ColumnType = ParseColumnType(c.ColumnType),
                    FormulaText = c.FormulaText,
                    TotalsFormula = c.TotalsFormula,
                    DataFormat = ParseFormat(c.DataFormat),
                    DecimalPlaces = c.DecimalPlaces,
                    AggregateFunc = c.AggregateFunc,
                    NegativeInRed = c.NegativeInRed
                });
            }

            var grps = await db.QueryAsync<ReportGrpRow>(new CommandDefinition("""
                SELECT FieldKey, GroupLevel, ShowSubTotals, StartOnNewPage, SortDirection
                FROM CustomReportGroupings
                WHERE IsDeleted = 0 AND ReportDefID = @reportDefId
                ORDER BY GroupLevel
                """, new { reportDefId }, cancellationToken: ct));

            def.Groupings.AddRange(grps.Select(g => new ReportGroupingDef
            {
                FieldKey = g.FieldKey,
                GroupLevel = g.GroupLevel,
                ShowSubTotals = g.ShowSubTotals,
                StartOnNewPage = g.StartOnNewPage,
                SortDirection = g.SortDirection
            }));

            var flts = await db.QueryAsync<ReportFltRow>(new CommandDefinition("""
                SELECT FilterKey, FilterLabelAR, Operator, ValueFrom, ValueTo,
                       IsPrompted, IsRequired
                FROM CustomReportFilters
                WHERE IsDeleted = 0 AND ReportDefID = @reportDefId
                ORDER BY ReportFilterID
                """, new { reportDefId }, cancellationToken: ct));

            def.Filters.AddRange(flts.Select(f => new ReportFilterDef
            {
                FilterKey = f.FilterKey,
                FilterLabelAR = f.FilterLabelAR,
                Operator = f.Operator,
                ValueFrom = f.ValueFrom,
                ValueTo = f.ValueTo,
                IsPrompted = f.IsPrompted,
                IsRequired = f.IsRequired
            }));

            return def;
        }

        // ═══════════════════════════════════════════════════════════════
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// </summary>
        public static async Task<int> SaveReportAsync(
            ReportDefinition def, Guid companyId, int userId, CancellationToken ct = default)
        {
            if (def.Columns.Count == 0)
                throw new InvalidOperationException("A report must contain at least one column.");

            foreach (var col in def.Columns.Where(c => c.ColumnType == ReportColumnType.Formula))
            {
                if (!CompiledFormula.TryCompile(col.FormulaText ?? string.Empty, out var err))
                    throw new InvalidOperationException(
                        $"Invalid formula in column '{col.CaptionAR}': {err}");
            }

            foreach (var col in def.Columns.Where(c => !string.IsNullOrWhiteSpace(c.TotalsFormula)))
            {
                if (!CompiledFormula.TryCompile(col.TotalsFormula!, out var err))
                    throw new InvalidOperationException(
                        $"Invalid totals formula in column '{col.CaptionAR}': {err}");
            }

            await using var db = Open();
            await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);

            try
            {
                int reportDefId;

                if (def.ReportDefID.HasValue && def.ReportDefID.Value > 0)
                {
                    var updated = await db.ExecuteAsync(new CommandDefinition($"""
                        UPDATE CustomReportDefinitions
                        SET ReportName = @ReportName, Description = @Description,
                            LanguageCode = @LanguageCode, Visibility = @Visibility,
                            PageOrientation = @Orientation,
                            ShowTotalsRow = @ShowTotalsRow, ShowHeaderInfo = @ShowHeaderInfo,
                            FreezeFirstColumn = @FreezeFirstColumn,
                            IsFavorite = @IsFavorite,
                            TitleFontFamily = @TitleFontFamily, TitleFontSize = @TitleFontSize,
                            TitleBold = @TitleBold, HeaderFontFamily = @HeaderFontFamily,
                            HeaderFontSize = @HeaderFontSize, HeaderBold = @HeaderBold,
                            HeaderBackColor = @HeaderBackColor, HeaderTextColor = @HeaderTextColor,
                            BodyFontFamily = @BodyFontFamily, BodyFontSize = @BodyFontSize,
                            TotalsFontFamily = @TotalsFontFamily, TotalsFontSize = @TotalsFontSize,
                            TotalsBold = @TotalsBold, BandingColor = @BandingColor,
                            ShowGridLines = @ShowGridLines, PageSize = @PageSize,
                            MarginMm = @MarginMm, ShowCompanyAddress = @ShowCompanyAddress,
                            UpdatedBy = @UpdatedBy, UpdatedAt = {Now}
                        WHERE ReportDefID = @ReportDefID
                          AND OwnerUserID = @OwnerUserID AND IsDeleted = 0 AND IsSystem = 0
                        """,
                        HeadParams(def, companyId, userId), tx, cancellationToken: ct));

                    if (updated == 0)
                        throw new UnauthorizedAccessException(
                            "You cannot modify this report (not owner or system report). Use Save As.");

                    reportDefId = def.ReportDefID.Value;

                    await SoftDeleteChildrenAsync(db, tx, reportDefId, userId, ct);
                }
                else
                {
                    reportDefId = await db.ExecuteScalarAsync<int>(new CommandDefinition($"""
                        INSERT INTO CustomReportDefinitions
                            (CompanyID, ReportKey, ReportName, Description, LanguageCode,
                             OwnerUserID, Visibility, DataSourceKey, PageOrientation,
                             ShowTotalsRow, ShowHeaderInfo, FreezeFirstColumn,
                             IsFavorite, IsSystem,
                             TitleFontFamily, TitleFontSize, TitleBold, HeaderFontFamily,
                             HeaderFontSize, HeaderBold, HeaderBackColor, HeaderTextColor,
                             BodyFontFamily, BodyFontSize, TotalsFontFamily, TotalsFontSize,
                             TotalsBold, BandingColor, ShowGridLines, PageSize, MarginMm,
                             ShowCompanyAddress, CreatedBy)
                        VALUES
                            (@CompanyID, @ReportKey, @ReportName, @Description, @LanguageCode,
                             @OwnerUserID, @Visibility, @DataSourceKey, @Orientation,
                             @ShowTotalsRow, @ShowHeaderInfo, @FreezeFirstColumn,
                             @IsFavorite, 0,
                             @TitleFontFamily, @TitleFontSize, @TitleBold, @HeaderFontFamily,
                             @HeaderFontSize, @HeaderBold, @HeaderBackColor, @HeaderTextColor,
                             @BodyFontFamily, @BodyFontSize, @TotalsFontFamily, @TotalsFontSize,
                             @TotalsBold, @BandingColor, @ShowGridLines, @PageSize, @MarginMm,
                             @ShowCompanyAddress, @CreatedBy)
                        {(IsPg ? "RETURNING ReportDefID" : "; SELECT last_insert_rowid()")}
                        """,
                        HeadParams(def, companyId, userId), tx, cancellationToken: ct));
                }

                await InsertColumnsAsync(db, tx, reportDefId, companyId, userId, def, ct);
                await InsertGroupingsAsync(db, tx, reportDefId, companyId, userId, def, ct);
                await InsertFiltersAsync(db, tx, reportDefId, companyId, userId, def, ct);

                await tx.CommitAsync(ct);
                return reportDefId;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public static async Task DeleteReportAsync(
            int reportDefId, int userId, CancellationToken ct = default)
        {
            await using var db = Open();
            await db.OpenAsync(ct);

            var deleted = await db.ExecuteAsync(new CommandDefinition($"""
                UPDATE CustomReportDefinitions
                SET IsDeleted = 1, DeletedAt = {Now}, DeletedBy = @UserGuid
                WHERE ReportDefID = @reportDefId AND OwnerUserID = @userId
                  AND IsDeleted = 0 AND IsSystem = 0
                """,
                new { reportDefId, userId, UserGuid = Guid.Empty }, cancellationToken: ct));

            if (deleted == 0)
                throw new UnauthorizedAccessException(
                    "You cannot delete this report (not owner or system report).");
        }

        // ═══════════════════════════════════════════════════════════════
        // Internals
        // ═══════════════════════════════════════════════════════════════

        private static object HeadParams(ReportDefinition def, Guid companyId, int userId) => new
        {
            ReportDefID = def.ReportDefID,
            CompanyID = companyId,
            def.ReportKey,
            def.ReportName,
            def.Description,
            def.LanguageCode,
            OwnerUserID = def.OwnerUserID,
            Visibility = def.Visibility.ToString().ToUpperInvariant(),
            def.DataSourceKey,
            Orientation = def.IsLandscape ? "LANDSCAPE" : "PORTRAIT",
            def.ShowTotalsRow,
            def.ShowHeaderInfo,
            def.FreezeFirstColumn,
            def.IsFavorite,
            def.TitleFontFamily,
            def.TitleFontSize,
            def.TitleBold,
            def.HeaderFontFamily,
            def.HeaderFontSize,
            def.HeaderBold,
            def.HeaderBackColor,
            def.HeaderTextColor,
            def.BodyFontFamily,
            def.BodyFontSize,
            def.TotalsFontFamily,
            def.TotalsFontSize,
            def.TotalsBold,
            def.BandingColor,
            def.ShowGridLines,
            def.PageSize,
            def.MarginMm,
            def.ShowCompanyAddress,
            CreatedBy = companyId,
            UpdatedBy = companyId
        };

        private static async Task SoftDeleteChildrenAsync(
            DbConnection db, DbTransaction tx, int reportDefId, int userId, CancellationToken ct)
        {
            foreach (var table in new[]
                     { "CustomReportColumns", "CustomReportGroupings", "CustomReportFilters" })
            {
                await db.ExecuteAsync(new CommandDefinition($"""
                    UPDATE {table}
                    SET IsDeleted = 1, DeletedAt = {Now}, DeletedBy = @UserGuid
                    WHERE ReportDefID = @reportDefId AND IsDeleted = 0
                    """,
                    new { reportDefId, UserGuid = Guid.Empty }, tx, cancellationToken: ct));
            }
        }

        private static async Task InsertColumnsAsync(
            DbConnection db, DbTransaction tx, int reportDefId,
            Guid companyId, int userId, ReportDefinition def, CancellationToken ct)
        {
            var order = 0;
            foreach (var col in def.Columns)
            {
                await db.ExecuteAsync(new CommandDefinition($"""
                    INSERT INTO CustomReportColumns
                        (CompanyID, ReportDefID, FieldKey, CaptionAR, CaptionEN,
                         DisplayOrder, ColumnWidth, IsVisible, IsLocked, ColumnType,
                         FormulaText, TotalsFormula, DataFormat, DecimalPlaces, AggregateFunc,
                         NegativeInRed, CreatedBy)
                    VALUES
                        (@CompanyID, @ReportDefID, @FieldKey, @CaptionAR, @CaptionEN,
                         @DisplayOrder, @ColumnWidth, @IsVisible, @IsLocked, @ColumnType,
                         @FormulaText, @TotalsFormula, @DataFormat, @DecimalPlaces, @AggregateFunc,
                         @NegativeInRed, @CreatedBy)
                    """,
                    new
                    {
                        CompanyID = companyId,
                        ReportDefID = reportDefId,
                        col.FieldKey,
                        col.CaptionAR,
                        col.CaptionEN,
                        DisplayOrder = order++,
                        ColumnWidth = (decimal)col.ColumnWidth,
                        col.IsVisible,
                        col.IsLocked,
                        ColumnType = col.ColumnType switch
                        {
                            ReportColumnType.Formula => "FORMULA",
                            ReportColumnType.RunningTotal => "RUNNING_TOTAL",
                            ReportColumnType.RowNumber => "ROW_NUMBER",
                            _ => "DATA"
                        },
                        col.FormulaText,
                        col.TotalsFormula,
                        DataFormat = col.DataFormat switch
                        {
                            ReportDataFormat.Money => "MONEY",
                            ReportDataFormat.Qty => "QTY",
                            ReportDataFormat.Percent => "PERCENT",
                            ReportDataFormat.Date => "DATE",
                            ReportDataFormat.DateTime => "DATETIME",
                            _ => "TEXT"
                        },
                        col.DecimalPlaces,
                        col.AggregateFunc,
                        col.NegativeInRed,
                        CreatedBy = companyId
                    },
                    tx, cancellationToken: ct));
            }
        }

        private static async Task InsertGroupingsAsync(
            DbConnection db, DbTransaction tx, int reportDefId,
            Guid companyId, int userId, ReportDefinition def, CancellationToken ct)
        {
            foreach (var g in def.Groupings)
            {
                await db.ExecuteAsync(new CommandDefinition($"""
                    INSERT INTO CustomReportGroupings
                        (CompanyID, ReportDefID, FieldKey, GroupLevel,
                         ShowSubTotals, StartOnNewPage, SortDirection, CreatedBy)
                    VALUES
                        (@CompanyID, @ReportDefID, @FieldKey, @GroupLevel,
                         @ShowSubTotals, @StartOnNewPage, @SortDirection, @CreatedBy)
                    """,
                    new
                    {
                        CompanyID = companyId, ReportDefID = reportDefId,
                        g.FieldKey, g.GroupLevel, g.ShowSubTotals,
                        g.StartOnNewPage, g.SortDirection, CreatedBy = companyId
                    },
                    tx, cancellationToken: ct));
            }
        }

        private static async Task InsertFiltersAsync(
            DbConnection db, DbTransaction tx, int reportDefId,
            Guid companyId, int userId, ReportDefinition def, CancellationToken ct)
        {
            foreach (var f in def.Filters)
            {
                await db.ExecuteAsync(new CommandDefinition($"""
                    INSERT INTO CustomReportFilters
                        (CompanyID, ReportDefID, FilterKey, FilterLabelAR, Operator,
                         ValueFrom, ValueTo, IsPrompted, IsRequired, CreatedBy)
                    VALUES
                        (@CompanyID, @ReportDefID, @FilterKey, @FilterLabelAR, @Operator,
                         @ValueFrom, @ValueTo, @IsPrompted, @IsRequired, @CreatedBy)
                    """,
                    new
                    {
                        CompanyID = companyId, ReportDefID = reportDefId,
                        f.FilterKey, f.FilterLabelAR, f.Operator,
                        f.ValueFrom, f.ValueTo, f.IsPrompted, f.IsRequired,
                        CreatedBy = companyId
                    },
                    tx, cancellationToken: ct));
            }
        }

        private static ReportDefinition MapDefinition(ReportDefRow r) => new()
        {
            ReportDefID = r.ReportDefID,
            ReportKey = r.ReportKey,
            ReportName = r.ReportName,
            Description = r.Description,
            LanguageCode = r.LanguageCode,
            OwnerUserID = r.OwnerUserID,
            Visibility = r.Visibility switch
            {
                "SHARED" => ReportVisibility.Shared,
                "SYSTEM" => ReportVisibility.System,
                _ => ReportVisibility.Private
            },
            DataSourceKey = r.DataSourceKey,
            IsLandscape = r.PageOrientation == "LANDSCAPE",
            ShowTotalsRow = r.ShowTotalsRow,
            ShowHeaderInfo = r.ShowHeaderInfo,
            FreezeFirstColumn = r.FreezeFirstColumn,
            IsFavorite = r.IsFavorite,
            IsSystem = r.IsSystem,
            TitleFontFamily = r.TitleFontFamily,
            TitleFontSize = r.TitleFontSize,
            TitleBold = r.TitleBold,
            HeaderFontFamily = r.HeaderFontFamily,
            HeaderFontSize = r.HeaderFontSize,
            HeaderBold = r.HeaderBold,
            HeaderBackColor = r.HeaderBackColor,
            HeaderTextColor = r.HeaderTextColor,
            BodyFontFamily = r.BodyFontFamily,
            BodyFontSize = r.BodyFontSize,
            TotalsFontFamily = r.TotalsFontFamily,
            TotalsFontSize = r.TotalsFontSize,
            TotalsBold = r.TotalsBold,
            BandingColor = r.BandingColor,
            ShowGridLines = r.ShowGridLines,
            PageSize = string.IsNullOrWhiteSpace(r.PageSize) ? "A4" : r.PageSize,
            MarginMm = r.MarginMm,
            ShowCompanyAddress = r.ShowCompanyAddress
        };

        private static ReportColumnType ParseColumnType(string? v) => v switch
        {
            "FORMULA" => ReportColumnType.Formula,
            "RUNNING_TOTAL" => ReportColumnType.RunningTotal,
            "ROW_NUMBER" => ReportColumnType.RowNumber,
            _ => ReportColumnType.Data
        };

        private static ReportDataFormat ParseFormat(string? v) => v switch
        {
            "MONEY" => ReportDataFormat.Money,
            "QTY" => ReportDataFormat.Qty,
            "PERCENT" => ReportDataFormat.Percent,
            "DATE" => ReportDataFormat.Date,
            "DATETIME" => ReportDataFormat.DateTime,
            _ => ReportDataFormat.Text
        };

        // ── Dapper row shapes ──
        private sealed class ReportDefRow
        {
            public int ReportDefID { get; set; }
            public string ReportKey { get; set; } = string.Empty;
            public string ReportName { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string LanguageCode { get; set; } = "ar";
            public int OwnerUserID { get; set; }
            public string Visibility { get; set; } = "PRIVATE";
            public string DataSourceKey { get; set; } = string.Empty;
            public string PageOrientation { get; set; } = "PORTRAIT";
            public bool ShowTotalsRow { get; set; }
            public bool ShowHeaderInfo { get; set; }
            public bool FreezeFirstColumn { get; set; }
            public bool IsFavorite { get; set; }
            public bool IsSystem { get; set; }
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
            public string? PageSize { get; set; }
            public double MarginMm { get; set; } = 12;
            public bool ShowCompanyAddress { get; set; }
        }

        private sealed class ReportColRow
        {
            public string FieldKey { get; set; } = string.Empty;
            public string CaptionAR { get; set; } = string.Empty;
            public string? CaptionEN { get; set; }
            public int DisplayOrder { get; set; }
            public decimal ColumnWidth { get; set; }
            public bool IsVisible { get; set; }
            public bool IsLocked { get; set; }
            public string? ColumnType { get; set; }
            public string? FormulaText { get; set; }
            public string? TotalsFormula { get; set; }
            public string? DataFormat { get; set; }
            public int DecimalPlaces { get; set; }
            public string? AggregateFunc { get; set; }
            public bool NegativeInRed { get; set; }
        }

        private sealed class ReportGrpRow
        {
            public string FieldKey { get; set; } = string.Empty;
            public int GroupLevel { get; set; }
            public bool ShowSubTotals { get; set; }
            public bool StartOnNewPage { get; set; }
            public string SortDirection { get; set; } = "ASC";
        }

        private sealed class ReportFltRow
        {
            public string FilterKey { get; set; } = string.Empty;
            public string FilterLabelAR { get; set; } = string.Empty;
            public string Operator { get; set; } = "EQ";
            public string? ValueFrom { get; set; }
            public string? ValueTo { get; set; }
            public bool IsPrompted { get; set; }
            public bool IsRequired { get; set; }
        }
    }

    /// <summary>
    /// JSON transfer shape for report designs — the payload carried
    /// inside the encrypted .strd files of DesignFileSvc (B0/B2).
    /// Versioned independently of the DB schema; unknown versions are
    /// rejected by the importer (the caller surfaces DSG-8103).
    /// </summary>
    public static class ReportDesignJson
    {
        public const int CurrentVersion = 1;

        public sealed class DesignDto
        {
            public int Version { get; set; } = CurrentVersion;
            public string ReportKey { get; set; } = string.Empty;
            public string ReportName { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string LanguageCode { get; set; } = "en";
            public string DataSourceKey { get; set; } = string.Empty;
            public bool IsLandscape { get; set; }
            public bool ShowTotalsRow { get; set; } = true;
            public bool ShowHeaderInfo { get; set; } = true;
            public bool FreezeFirstColumn { get; set; }
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
            public List<ColumnDto> Columns { get; set; } = new();
            public List<GroupingDto> Groupings { get; set; } = new();
            public List<FilterDto> Filters { get; set; } = new();
        }

        public sealed class ColumnDto
        {
            public string FieldKey { get; set; } = string.Empty;
            public string CaptionAR { get; set; } = string.Empty;
            public string? CaptionEN { get; set; }
            public int DisplayOrder { get; set; }
            public double ColumnWidth { get; set; } = 120;
            public bool IsVisible { get; set; } = true;
            public bool IsLocked { get; set; }
            public string ColumnType { get; set; } = "DATA";
            public string? FormulaText { get; set; }
            public string? TotalsFormula { get; set; }
            public string DataFormat { get; set; } = "TEXT";
            public int DecimalPlaces { get; set; } = 2;
            public string? AggregateFunc { get; set; }
            public bool NegativeInRed { get; set; } = true;
        }

        public sealed class GroupingDto
        {
            public string FieldKey { get; set; } = string.Empty;
            public int GroupLevel { get; set; }
            public bool ShowSubTotals { get; set; } = true;
            public bool StartOnNewPage { get; set; }
            public string SortDirection { get; set; } = "ASC";
        }

        public sealed class FilterDto
        {
            public string FilterKey { get; set; } = string.Empty;
            public string FilterLabelAR { get; set; } = string.Empty;
            public string Operator { get; set; } = "EQ";
            public string? ValueFrom { get; set; }
            public string? ValueTo { get; set; }
            public bool IsPrompted { get; set; }
            public bool IsRequired { get; set; }
        }

        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false
        };

        public static string Serialize(ReportDefinition def)
        {
            var dto = new DesignDto
            {
                ReportKey = def.ReportKey,
                ReportName = def.ReportName,
                Description = def.Description,
                LanguageCode = def.LanguageCode,
                DataSourceKey = def.DataSourceKey,
                IsLandscape = def.IsLandscape,
                ShowTotalsRow = def.ShowTotalsRow,
                ShowHeaderInfo = def.ShowHeaderInfo,
                FreezeFirstColumn = def.FreezeFirstColumn,
                TitleFontFamily = def.TitleFontFamily,
                TitleFontSize = def.TitleFontSize,
                TitleBold = def.TitleBold,
                HeaderFontFamily = def.HeaderFontFamily,
                HeaderFontSize = def.HeaderFontSize,
                HeaderBold = def.HeaderBold,
                HeaderBackColor = def.HeaderBackColor,
                HeaderTextColor = def.HeaderTextColor,
                BodyFontFamily = def.BodyFontFamily,
                BodyFontSize = def.BodyFontSize,
                TotalsFontFamily = def.TotalsFontFamily,
                TotalsFontSize = def.TotalsFontSize,
                TotalsBold = def.TotalsBold,
                BandingColor = def.BandingColor,
                ShowGridLines = def.ShowGridLines,
                PageSize = def.PageSize,
                MarginMm = def.MarginMm,
                ShowCompanyAddress = def.ShowCompanyAddress,
                Columns = def.Columns.OrderBy(c => c.DisplayOrder).Select(c => new ColumnDto
                {
                    FieldKey = c.FieldKey,
                    CaptionAR = c.CaptionAR,
                    CaptionEN = c.CaptionEN,
                    DisplayOrder = c.DisplayOrder,
                    ColumnWidth = c.ColumnWidth,
                    IsVisible = c.IsVisible,
                    IsLocked = c.IsLocked,
                    ColumnType = c.ColumnType switch
                    {
                        ReportColumnType.Formula => "FORMULA",
                        ReportColumnType.RunningTotal => "RUNNING_TOTAL",
                        ReportColumnType.RowNumber => "ROW_NUMBER",
                        _ => "DATA"
                    },
                    FormulaText = c.FormulaText,
                    TotalsFormula = c.TotalsFormula,
                    DataFormat = c.DataFormat switch
                    {
                        ReportDataFormat.Money => "MONEY",
                        ReportDataFormat.Qty => "QTY",
                        ReportDataFormat.Percent => "PERCENT",
                        ReportDataFormat.Date => "DATE",
                        ReportDataFormat.DateTime => "DATETIME",
                        _ => "TEXT"
                    },
                    DecimalPlaces = c.DecimalPlaces,
                    AggregateFunc = c.AggregateFunc,
                    NegativeInRed = c.NegativeInRed
                }).ToList(),
                Groupings = def.Groupings.Select(g => new GroupingDto
                {
                    FieldKey = g.FieldKey,
                    GroupLevel = g.GroupLevel,
                    ShowSubTotals = g.ShowSubTotals,
                    StartOnNewPage = g.StartOnNewPage,
                    SortDirection = g.SortDirection
                }).ToList(),
                Filters = def.Filters.Select(f => new FilterDto
                {
                    FilterKey = f.FilterKey,
                    FilterLabelAR = f.FilterLabelAR,
                    Operator = f.Operator,
                    ValueFrom = f.ValueFrom,
                    ValueTo = f.ValueTo,
                    IsPrompted = f.IsPrompted,
                    IsRequired = f.IsRequired
                }).ToList()
            };
            return System.Text.Json.JsonSerializer.Serialize(dto, JsonOptions);
        }

        /// <summary>Parses a design payload. Returns null when the
        /// payload is not a supported design (caller reports
        /// DSG-8103). The result is always a NEW unsaved definition;
        /// ownership/visibility are the caller's to assign.</summary>
        public static ReportDefinition? Deserialize(string payloadJson, int ownerUserId)
        {
            DesignDto? dto;
            try
            {
                dto = System.Text.Json.JsonSerializer.Deserialize<DesignDto>(payloadJson, JsonOptions);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }

            if (dto is null || dto.Version != CurrentVersion ||
                string.IsNullOrWhiteSpace(dto.ReportKey) || dto.Columns.Count == 0)
                return null;

            var def = new ReportDefinition
            {
                ReportDefID = null,
                ReportKey = dto.ReportKey,
                ReportName = dto.ReportName,
                Description = dto.Description,
                LanguageCode = dto.LanguageCode,
                OwnerUserID = ownerUserId,
                Visibility = ReportVisibility.Private,
                DataSourceKey = dto.DataSourceKey,
                IsLandscape = dto.IsLandscape,
                ShowTotalsRow = dto.ShowTotalsRow,
                ShowHeaderInfo = dto.ShowHeaderInfo,
                FreezeFirstColumn = dto.FreezeFirstColumn,
                IsFavorite = false,
                IsSystem = false,
                TitleFontFamily = dto.TitleFontFamily,
                TitleFontSize = dto.TitleFontSize,
                TitleBold = dto.TitleBold,
                HeaderFontFamily = dto.HeaderFontFamily,
                HeaderFontSize = dto.HeaderFontSize,
                HeaderBold = dto.HeaderBold,
                HeaderBackColor = dto.HeaderBackColor,
                HeaderTextColor = dto.HeaderTextColor,
                BodyFontFamily = dto.BodyFontFamily,
                BodyFontSize = dto.BodyFontSize,
                TotalsFontFamily = dto.TotalsFontFamily,
                TotalsFontSize = dto.TotalsFontSize,
                TotalsBold = dto.TotalsBold,
                BandingColor = dto.BandingColor,
                ShowGridLines = dto.ShowGridLines,
                PageSize = string.IsNullOrWhiteSpace(dto.PageSize) ? "A4" : dto.PageSize,
                MarginMm = dto.MarginMm,
                ShowCompanyAddress = dto.ShowCompanyAddress
            };

            foreach (var c in dto.Columns)
            {
                def.Columns.Add(new ReportColumnDef
                {
                    FieldKey = c.FieldKey,
                    CaptionAR = c.CaptionAR,
                    CaptionEN = c.CaptionEN,
                    DisplayOrder = c.DisplayOrder,
                    ColumnWidth = c.ColumnWidth,
                    IsVisible = c.IsVisible,
                    IsLocked = c.IsLocked,
                    ColumnType = c.ColumnType switch
                    {
                        "FORMULA" => ReportColumnType.Formula,
                        "RUNNING_TOTAL" => ReportColumnType.RunningTotal,
                        "ROW_NUMBER" => ReportColumnType.RowNumber,
                        _ => ReportColumnType.Data
                    },
                    FormulaText = c.FormulaText,
                    TotalsFormula = c.TotalsFormula,
                    DataFormat = c.DataFormat switch
                    {
                        "MONEY" => ReportDataFormat.Money,
                        "QTY" => ReportDataFormat.Qty,
                        "PERCENT" => ReportDataFormat.Percent,
                        "DATE" => ReportDataFormat.Date,
                        "DATETIME" => ReportDataFormat.DateTime,
                        _ => ReportDataFormat.Text
                    },
                    DecimalPlaces = c.DecimalPlaces,
                    AggregateFunc = c.AggregateFunc,
                    NegativeInRed = c.NegativeInRed
                });
            }

            def.Groupings.AddRange(dto.Groupings.Select(g => new ReportGroupingDef
            {
                FieldKey = g.FieldKey,
                GroupLevel = g.GroupLevel,
                ShowSubTotals = g.ShowSubTotals,
                StartOnNewPage = g.StartOnNewPage,
                SortDirection = g.SortDirection
            }));

            def.Filters.AddRange(dto.Filters.Select(f => new ReportFilterDef
            {
                FilterKey = f.FilterKey,
                FilterLabelAR = f.FilterLabelAR,
                Operator = f.Operator,
                ValueFrom = f.ValueFrom,
                ValueTo = f.ValueTo,
                IsPrompted = f.IsPrompted,
                IsRequired = f.IsRequired
            }));

            return def;
        }
    }
}
