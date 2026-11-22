using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral persistence for opt-in purchase three-way-match controls.
/// Policy writes use optimistic version checks; evaluation releases are
/// append-only and can target only the invoice's latest blocked evaluation.
/// </summary>
public sealed class PurchaseMatchPolicyRepository : IPurchaseMatchPolicyRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public PurchaseMatchPolicyRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));

        _provider = provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" or "sqlite3" => "SQLite",
            "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
            _ => throw new NotSupportedException($"Unsupported database provider '{provider}'.")
        };
        _connectionString = connectionString;
    }

    public async Task<PurchaseMatchPolicy?> GetPolicyAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        RequireCompany(companyCode);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT is_enabled, require_receipt_match, price_warning_percent, price_block_percent,
                   tax_warning_percent, tax_block_percent, base_variance_warning_amount,
                   base_variance_block_amount, policy_version
            FROM purchase_match_policies
            WHERE company_code = @company
            """);
        Add(command, "@company", companyCode.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var policy = new PurchaseMatchPolicy(
            Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture));
        policy.Validate();
        return policy;
    }

    public async Task SavePolicyAsync(
        string companyCode,
        PurchaseMatchPolicy policy,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireCompany(companyCode);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (expectedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO purchase_match_policies
                (company_code, is_enabled, require_receipt_match,
                 price_warning_percent, price_block_percent,
                 tax_warning_percent, tax_block_percent,
                 base_variance_warning_amount, base_variance_block_amount,
                 policy_version, created_by, updated_by)
            VALUES
                (@company, @enabled, @requireReceipt,
                 @priceWarning, @priceBlock, @taxWarning, @taxBlock,
                 @baseWarning, @baseBlock, 1, @actor, @actor)
            ON CONFLICT(company_code) DO UPDATE SET
                is_enabled = excluded.is_enabled,
                require_receipt_match = excluded.require_receipt_match,
                price_warning_percent = excluded.price_warning_percent,
                price_block_percent = excluded.price_block_percent,
                tax_warning_percent = excluded.tax_warning_percent,
                tax_block_percent = excluded.tax_block_percent,
                base_variance_warning_amount = excluded.base_variance_warning_amount,
                base_variance_block_amount = excluded.base_variance_block_amount,
                policy_version = purchase_match_policies.policy_version + 1,
                updated_by = excluded.updated_by,
                updated_at = CURRENT_TIMESTAMP
            WHERE purchase_match_policies.policy_version = @expectedVersion
              AND @expectedVersion > 0
            """);
        Add(command, "@company", companyCode.Trim());
        Add(command, "@enabled", policy.IsEnabled);
        Add(command, "@requireReceipt", policy.RequireReceiptMatch);
        Add(command, "@priceWarning", policy.PriceWarningPercent);
        Add(command, "@priceBlock", policy.PriceBlockPercent);
        Add(command, "@taxWarning", policy.TaxWarningPercent);
        Add(command, "@taxBlock", policy.TaxBlockPercent);
        Add(command, "@baseWarning", policy.BaseVarianceWarningAmount);
        Add(command, "@baseBlock", policy.BaseVarianceBlockAmount);
        Add(command, "@actor", actor.Trim());
        Add(command, "@expectedVersion", expectedVersion);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            throw new DBConcurrencyException(
                "The purchase match policy was changed by another user or the expected policy version is invalid.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> RecordOverrideAsync(
        string companyCode,
        long invoiceDocId,
        Guid evaluationId,
        string actor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        RequireCompany(companyCode);
        if (invoiceDocId <= 0)
            throw new ArgumentOutOfRangeException(nameof(invoiceDocId));
        if (evaluationId == Guid.Empty)
            throw new ArgumentException("A match evaluation is required.", nameof(evaluationId));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new ArgumentException("A release reason of at least five characters is required.", nameof(reason));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string? latestEvaluationId = null;
        string? latestDecision = null;
        await using (var latest = CreateCommand(connection, transaction, """
            SELECT evaluation_id, decision
            FROM purchase_match_evaluations
            WHERE company_code = @company AND invoice_doc_id = @invoice
            ORDER BY evaluation_version DESC
            LIMIT 1
            """))
        {
            Add(latest, "@company", companyCode.Trim());
            Add(latest, "@invoice", invoiceDocId);
            await using var reader = await latest.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                latestEvaluationId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                latestDecision = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        if (!string.Equals(latestDecision, "Blocked", StringComparison.Ordinal) ||
            !Guid.TryParse(latestEvaluationId, out var latestId) ||
            latestId != evaluationId)
            throw new InvalidOperationException(
                "Only the latest blocked purchase match evaluation can be released.");

        var overrideId = Guid.NewGuid();
        await using (var insert = CreateCommand(connection, transaction, """
            INSERT INTO purchase_match_overrides
                (override_id, company_code, invoice_doc_id, evaluation_id,
                 previous_decision, released_by, release_reason)
            VALUES
                (@id, @company, @invoice, @evaluation, 'Blocked', @actor, @reason)
            """))
        {
            Add(insert, "@id", _provider == "PostgreSQL" ? overrideId : overrideId.ToString("D"));
            Add(insert, "@company", companyCode.Trim());
            Add(insert, "@invoice", invoiceDocId);
            Add(insert, "@evaluation", _provider == "PostgreSQL" ? evaluationId : evaluationId.ToString("D"));
            Add(insert, "@actor", actor.Trim());
            Add(insert, "@reason", reason.Trim());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return overrideId;
    }

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction is not null)
            command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void RequireCompany(string companyCode)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new ArgumentException("A company code is required.", nameof(companyCode));
    }
}
