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
                       ShowTotalsRow, ShowHeaderInfo, FreezeFirstColumn, IsFavorite, IsSystem
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
                       ShowTotalsRow, ShowHeaderInfo, FreezeFirstColumn, IsFavorite, IsSystem
                FROM CustomReportDefinitions
                WHERE IsDeleted = 0 AND ReportDefID = @reportDefId
                  AND (OwnerUserID = @userId OR Visibility <> 'PRIVATE')
                """, new { reportDefId, userId }, cancellationToken: ct));

            if (head is null)
                return null;

            var def = MapDefinition(head);

            var cols = await db.QueryAsync<ReportColRow>(new CommandDefinition("""
                SELECT FieldKey, CaptionAR, CaptionEN, DisplayOrder, ColumnWidth,
                       IsVisible, IsLocked, ColumnType, FormulaText, DataFormat,
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
                             IsFavorite, IsSystem, CreatedBy)
                        VALUES
                            (@CompanyID, @ReportKey, @ReportName, @Description, @LanguageCode,
                             @OwnerUserID, @Visibility, @DataSourceKey, @Orientation,
                             @ShowTotalsRow, @ShowHeaderInfo, @FreezeFirstColumn,
                             @IsFavorite, 0, @CreatedBy)
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
                         FormulaText, DataFormat, DecimalPlaces, AggregateFunc,
                         NegativeInRed, CreatedBy)
                    VALUES
                        (@CompanyID, @ReportDefID, @FieldKey, @CaptionAR, @CaptionEN,
                         @DisplayOrder, @ColumnWidth, @IsVisible, @IsLocked, @ColumnType,
                         @FormulaText, @DataFormat, @DecimalPlaces, @AggregateFunc,
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
            IsSystem = r.IsSystem
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
}
