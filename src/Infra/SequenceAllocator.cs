using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;

namespace ShouTech.Infra;

/// <summary>
/// The ONE number allocator (NUMBERING_STUDY §3.1): every document,
/// journal, session, order and card number in the system is drawn from
/// document_number_sequences (migration 075) through here. Allocation
/// is an UPSERT inside the CALLER's transaction — the counter row is
/// the lock, so two simultaneous users can never receive the same
/// value (Odoo's no_gap counter-row model). The counter only moves
/// forward: a number, once allocated, is never reissued — not while
/// its record sits in the Recycle Bin and not after a permanent purge
/// (study §3.3). Callers pass seedMax = the highest numeric suffix
/// already present in their family INCLUDING soft-deleted rows, so a
/// fresh counter row starts above all history, and startFloor from
/// number_series_config (migration 123) when configured.
/// </summary>
public static class SequenceAllocator
{
    public const long MaxValue = 99999999;

    /// <summary>Resolved configuration for one series (defaults applied).</summary>
    public sealed record SeriesConfig(
        string Prefix,
        int Padding,
        bool YearlySegment,
        long StartValue);

    public static SeriesConfig DefaultConfig(string seriesKey, bool yearlyByDefault = true) =>
        new(seriesKey, 4, yearlyByDefault, 1);

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool TableExists(DbConnection connection, DbTransaction transaction, bool isPg, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = isPg
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = @t"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t";
        Add(command, "@t", table);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Reads number_series_config for one series, falling back
    /// to the supplied defaults when no row exists (or the config table
    /// is not there yet). Invalid stored values raise NUM-8302.</summary>
    public static SeriesConfig ReadConfig(
        DbConnection connection, DbTransaction transaction, bool isPg,
        string companyKey, string seriesKey, SeriesConfig defaults)
    {
        if (!TableExists(connection, transaction, isPg, "number_series_config"))
            return defaults;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT prefix, padding, yearly_segment, start_value
            FROM number_series_config
            WHERE company_id = @company AND series_key = @series
            """;
        Add(command, "@company", companyKey.Trim());
        Add(command, "@series", seriesKey.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return defaults;
        var prefix = reader.IsDBNull(0) ? defaults.Prefix : reader.GetString(0).Trim();
        var padding = reader.IsDBNull(1) ? defaults.Padding : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        var yearly = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0;
        var start = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
        if (prefix.Length == 0 || prefix.Length > 12 || padding is < 1 or > 8 || start < 1 || start > MaxValue)
            throw new AppError(ErrorCodes.NumberingInvalidConfig,
                $"Series '{seriesKey}' has an invalid numbering configuration (prefix '{prefix}', padding {padding}, start {start}).");
        return new SeriesConfig(prefix, padding, yearly, start);
    }

    /// <summary>Allocates the next value of a series inside the caller's
    /// transaction. next = MAX(stored + 1, seedMax + 1, startFloor + 1);
    /// the first allocation inserts MAX(seedMax, startFloor) + 1.</summary>
    public static long Allocate(
        DbConnection connection, DbTransaction transaction, bool isPg,
        string companyKey, string seriesKey, int segmentYear, long seedMax, long startFloor = 0)
    {
        var floor = Math.Max(seedMax, startFloor);
        if (floor >= MaxValue)
            throw new AppError(ErrorCodes.NumberingLimitReached,
                $"The '{seriesKey}' number sequence has reached its supported limit.");
        var advanceExpression = isPg
            ? "GREATEST(document_number_sequences.last_value + 1, EXCLUDED.last_value)"
            : "MAX(document_number_sequences.last_value + 1, excluded.last_value)";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO document_number_sequences (company_id, doc_kind, doc_year, last_value)
            VALUES (@company, @series, @year, @next)
            ON CONFLICT (company_id, doc_kind, doc_year)
            DO UPDATE SET last_value = {advanceExpression}
            RETURNING last_value
            """;
        Add(command, "@company", companyKey.Trim());
        Add(command, "@series", seriesKey.Trim());
        Add(command, "@year", segmentYear);
        Add(command, "@next", floor + 1);
        var allocated = Convert.ToInt64(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        if (allocated <= 0 || allocated > MaxValue)
            throw new AppError(ErrorCodes.NumberingAllocationFailed,
                $"The '{seriesKey}' number sequence returned an invalid value.");
        return allocated;
    }

    /// <summary>Async twin of <see cref="Allocate"/> for the Infra
    /// repositories.</summary>
    public static async Task<long> AllocateAsync(
        DbConnection connection, DbTransaction transaction, bool isPg,
        string companyKey, string seriesKey, int segmentYear, long seedMax, long startFloor = 0,
        CancellationToken cancellationToken = default)
    {
        var floor = Math.Max(seedMax, startFloor);
        if (floor >= MaxValue)
            throw new AppError(ErrorCodes.NumberingLimitReached,
                $"The '{seriesKey}' number sequence has reached its supported limit.");
        var advanceExpression = isPg
            ? "GREATEST(document_number_sequences.last_value + 1, EXCLUDED.last_value)"
            : "MAX(document_number_sequences.last_value + 1, excluded.last_value)";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO document_number_sequences (company_id, doc_kind, doc_year, last_value)
            VALUES (@company, @series, @year, @next)
            ON CONFLICT (company_id, doc_kind, doc_year)
            DO UPDATE SET last_value = {advanceExpression}
            RETURNING last_value
            """;
        Add(command, "@company", companyKey.Trim());
        Add(command, "@series", seriesKey.Trim());
        Add(command, "@year", segmentYear);
        Add(command, "@next", floor + 1);
        var allocated = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0,
            CultureInfo.InvariantCulture);
        if (allocated <= 0 || allocated > MaxValue)
            throw new AppError(ErrorCodes.NumberingAllocationFailed,
                $"The '{seriesKey}' number sequence returned an invalid value.");
        return allocated;
    }

    /// <summary>Async config read for the Infra repositories.</summary>
    public static async Task<SeriesConfig> ReadConfigAsync(
        DbConnection connection, DbTransaction transaction, bool isPg,
        string companyKey, string seriesKey, SeriesConfig defaults,
        CancellationToken cancellationToken = default)
    {
        if (!TableExists(connection, transaction, isPg, "number_series_config"))
            return defaults;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT prefix, padding, yearly_segment, start_value
            FROM number_series_config
            WHERE company_id = @company AND series_key = @series
            """;
        Add(command, "@company", companyKey.Trim());
        Add(command, "@series", seriesKey.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return defaults;
        var prefix = reader.IsDBNull(0) ? defaults.Prefix : reader.GetString(0).Trim();
        var padding = reader.IsDBNull(1) ? defaults.Padding : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        var yearly = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0;
        var start = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
        if (prefix.Length == 0 || prefix.Length > 12 || padding is < 1 or > 8 || start < 1 || start > MaxValue)
            throw new AppError(ErrorCodes.NumberingInvalidConfig,
                $"Series '{seriesKey}' has an invalid numbering configuration (prefix '{prefix}', padding {padding}, start {start}).");
        return new SeriesConfig(prefix, padding, yearly, start);
    }

    /// <summary>The current counter of a series (0 when never used) —
    /// for the OptWin Numbering section's "current value" column.</summary>
    public static long Peek(
        DbConnection connection, DbTransaction transaction,
        string companyKey, string seriesKey, int segmentYear)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT last_value FROM document_number_sequences
            WHERE company_id = @company AND doc_kind = @series AND doc_year = @year
            """;
        Add(command, "@company", companyKey.Trim());
        Add(command, "@series", seriesKey.Trim());
        Add(command, "@year", segmentYear);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Allocates the single company-wide journal sequence. The counter is
    /// updated inside the caller's posting transaction, so a failed posting
    /// cannot leave a journal number detached from its journal entry.
    /// Journal numbers are continuous across fiscal/calendar years.
    /// </summary>
    public static string AllocateJournalNumber(
        DbConnection connection, DbTransaction transaction, bool isPg,
        Guid companyId, DateOnly entryDate)
    {
        using var company = connection.CreateCommand();
        company.Transaction = transaction;
        company.CommandText = "SELECT company_code FROM companies WHERE CAST(company_id AS TEXT) = @id";
        Add(company, "@id", companyId.ToString("D", CultureInfo.InvariantCulture));
        var companyCode = Convert.ToString(company.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new AppError(ErrorCodes.NumberingAllocationFailed,
                "The journal company could not be resolved for number allocation.");

        const string seriesKey = "JRN";
        var defaults = new SeriesConfig("JRN", 8, false, 1);
        var config = ReadConfig(connection, transaction, isPg, companyCode, seriesKey, defaults);
        var segment = config.YearlySegment ? entryDate.Year : 0;
        var prefix = config.YearlySegment
            ? $"{config.Prefix}-{entryDate.Year}-"
            : $"{config.Prefix}-";
        var start = prefix.Length + 1;
        var suffix = isPg ? "SUBSTRING(document_number FROM @start)" : "SUBSTR(document_number, @start)";
        var numeric = isPg
            ? $"{suffix} ~ '^[0-9]+$'"
            : $"{suffix} GLOB '[0-9]*' AND {suffix} NOT GLOB '*[^0-9]*'";

        using var max = connection.CreateCommand();
        max.Transaction = transaction;
        max.CommandText = $"""
            SELECT COALESCE(MAX(CAST({suffix} AS {(isPg ? "BIGINT" : "INTEGER")})), 0)
            FROM journal_entries
            WHERE company_id = @company AND document_number LIKE @pattern AND {numeric}
            """;
        Add(max, "@company", companyId.ToString("D", CultureInfo.InvariantCulture));
        Add(max, "@pattern", $"{prefix}%");
        Add(max, "@start", start);
        var seed = Convert.ToInt64(max.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        var allocated = Allocate(connection, transaction, isPg, companyCode, seriesKey,
            segment, seed, config.StartValue - 1);
        return Format(config, segment, allocated);
    }

    /// <summary>Formats a document-style number: PREFIX-YEAR-DDDD with a
    /// year segment, PREFIX-DDDD without one.</summary>
    public static string Format(SeriesConfig config, int segmentYear, long value) =>
        segmentYear > 0
            ? $"{config.Prefix}-{segmentYear}-{value.ToString($"D{config.Padding}", CultureInfo.InvariantCulture)}"
            : $"{config.Prefix}-{value.ToString($"D{config.Padding}", CultureInfo.InvariantCulture)}";
}
