using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using ShouTech.Domain;
using ShouTech.Infra;
using ShouTech.Infrastructure;

namespace ShouTech.App.Services;

/// <summary>
/// Numbering configuration surface (NUMBERING_STUDY §3.4): reads and
/// writes number_series_config for the OptWin Numbering section. The
/// counters themselves live in document_number_sequences and are only
/// ever advanced by SequenceAllocator — saving configuration here can
/// change future formats and raise start floors, but it can never
/// renumber history or move a counter backward (the allocator takes
/// MAX(stored, seed, start − 1) + 1).
/// </summary>
public static class SeqSvc
{
    /// <summary>One series in the catalog: key, Lang name key, and the
    /// built-in defaults used when no configuration row exists.</summary>
    public sealed record SeriesDef(string Key, string NameKey, string DefaultPrefix, bool DefaultYearly);

    /// <summary>A catalog series with its effective configuration and
    /// the current counter value (0 when never allocated).</summary>
    public sealed record SeriesRow(
        string Key, string NameKey, string Prefix, int Padding,
        bool Yearly, long StartValue, long CurrentValue);

    public static IReadOnlyList<SeriesDef> Catalog { get; } = new[]
    {
        new SeriesDef("SAL", "Numbering.Series.SAL", "SAL", false),
        new SeriesDef("SRT", "Numbering.Series.SRT", "SRT", false),
        new SeriesDef("POS", "Numbering.Series.POS", "POS", false),
        new SeriesDef("PUR", "Numbering.Series.PUR", "PUR", false),
        new SeriesDef("PRT", "Numbering.Series.PRT", "PRT", false),
        new SeriesDef("ENT", "Numbering.Series.ENT", "ENT", false),
        new SeriesDef("EXT", "Numbering.Series.EXT", "EXT", false),
        new SeriesDef("TRN", "Numbering.Series.TRN", "TRN", false),
        new SeriesDef("OPN", "Numbering.Series.OPN", "OPN", false),
        new SeriesDef("CNT", "Numbering.Series.CNT", "CNT", false),
        new SeriesDef("QUO", "Numbering.Series.QUO", "QUO", false),
        new SeriesDef("SO", "Numbering.Series.SO", "SO", false),
        new SeriesDef("PO", "Numbering.Series.PO", "PO", false),
        new SeriesDef("RFQ", "Numbering.Series.RFQ", "RFQ", false),
        new SeriesDef("SQ", "Numbering.Series.SQ", "SQ", false),
        new SeriesDef("PRQ", "Numbering.Series.PRQ", "PRQ", false),
        new SeriesDef("DLV", "Numbering.Series.DLV", "DLV", false),
        new SeriesDef("RCR", "Numbering.Series.RCR", "RCR", false),
        // RCV is ONE series shared by chain purchase receipts and cash
        // receipt vouchers (both print RCV- numbers; sharing the
        // counter keeps the printed numbers unique across families).
        new SeriesDef("JV", "Numbering.Series.JV", "JV", false),
        new SeriesDef("RCV", "Numbering.Series.RCV", "RCV", false),
        new SeriesDef("PAY", "Numbering.Series.PAY", "PAY", false),
        new SeriesDef("SES", "Numbering.Series.SES", "SES", false),
        new SeriesDef("MFG", "Numbering.Series.MFG", "MFG", false),
        new SeriesDef("CUS", "Numbering.Series.CUS", "CUS", false),
        new SeriesDef("SUP", "Numbering.Series.SUP", "SUP", false),
        new SeriesDef("EMP", "Numbering.Series.EMP", "EMP", false),
    };

    private static string Company => AppState.CurrentCompanyCode ?? string.Empty;

    private static DbConnection Open(string connectionString) =>
        DatabaseRuntime.IsPostgreSql()
            ? new Npgsql.NpgsqlConnection(connectionString)
            : new SqliteConnection(connectionString);

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Loads every catalog series with its effective
    /// configuration and current counter for the open company.</summary>
    public static List<SeriesRow> LoadSeries()
    {
        var rows = new List<SeriesRow>();
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(Company))
            return rows;
        var isPg = DatabaseRuntime.IsPostgreSql();
        using var connection = Open(cs);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var def in Catalog)
        {
            var config = SequenceAllocator.ReadConfig(connection, transaction, isPg, Company, def.Key,
                SequenceAllocator.DefaultConfig(def.DefaultPrefix, def.DefaultYearly));
            const int segment = 0;
            var current = SequenceAllocator.Peek(connection, transaction, Company, def.Key, segment);
            rows.Add(new SeriesRow(def.Key, def.NameKey, config.Prefix, 1,
                false, config.StartValue, current));
        }
        transaction.Commit();
        return rows;
    }

    /// <summary>Persists configuration rows (one UPSERT per series).
    /// Validation failures raise NUM-8302 before anything is written.</summary>
    public static void SaveSeries(IEnumerable<SeriesRow> rows, string actor)
    {
        var list = rows.ToList();
        foreach (var row in list)
        {
            if (string.IsNullOrWhiteSpace(row.Prefix) || row.Prefix.Trim().Length > 12
                || row.StartValue < 1 || row.StartValue > SequenceAllocator.MaxValue)
                throw new AppError(ErrorCodes.NumberingInvalidConfig,
                    $"Series '{row.Key}' has an invalid numbering configuration.");
        }
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs) || string.IsNullOrWhiteSpace(Company))
            throw new AppError(ErrorCodes.NumberingAllocationFailed,
                "A company database must be connected before saving numbering configuration.");
        using var connection = Open(cs);
        connection.Open();
        using var transaction = connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: false)
            : connection.BeginTransaction();
        try
        {
            foreach (var row in list)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO number_series_config
                        (company_id, series_key, prefix, padding, yearly_segment, start_value, updated_by, updated_at)
                    VALUES (@company, @series, @prefix, @padding, @yearly, @start, @actor, @at)
                    ON CONFLICT (company_id, series_key)
                    DO UPDATE SET prefix = EXCLUDED.prefix, padding = EXCLUDED.padding,
                                  yearly_segment = EXCLUDED.yearly_segment, start_value = EXCLUDED.start_value,
                                  updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
                    """;
                Add(command, "@company", Company);
                Add(command, "@series", row.Key);
                Add(command, "@prefix", row.Prefix.Trim());
                Add(command, "@padding", 1);
                Add(command, "@yearly", 0);
                Add(command, "@start", row.StartValue);
                Add(command, "@actor", actor);
                Add(command, "@at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
