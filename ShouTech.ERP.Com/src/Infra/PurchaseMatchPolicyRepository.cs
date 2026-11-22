using System.Data;
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

    public async Task<IReadOnlyList<PurchaseMatchEvaluationSnapshot>> GetRecentEvaluationsAsync(
        string companyCode,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        RequireCompany(companyCode);
        if (take is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(take), "The result limit must be between 1 and 200.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT e.evaluation_id, e.invoice_doc_id, e.evaluation_version, e.policy_version,
                   e.decision, e.reason_codes_json, e.policy_snapshot_json,
                   e.result_snapshot_json, e.evaluated_by, e.evaluated_at
            FROM purchase_match_evaluations e
            WHERE e.company_code = @company
              AND e.evaluation_version = (
                  SELECT MAX(latest.evaluation_version)
                  FROM purchase_match_evaluations latest
                  WHERE latest.company_code = e.company_code
                    AND latest.invoice_doc_id = e.invoice_doc_id
              )
            ORDER BY e.evaluated_at DESC, e.invoice_doc_id DESC
            LIMIT @take
            """);
        Add(command, "@company", companyCode.Trim());
        Add(command, "@take", take);

        var result = new List<PurchaseMatchEvaluationSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var idText = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(idText, out var evaluationId))
                throw new InvalidOperationException("A stored purchase match evaluation has an invalid identifier.");

            result.Add(new PurchaseMatchEvaluationSnapshot(
                evaluationId,
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty,
                ReadJsonStringArray(reader.GetValue(5)),
                ReadJsonText(reader.GetValue(6)),
                ReadJsonText(reader.GetValue(7)),
                reader.IsDBNull(8) ? null : Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture),
                Convert.ToDateTime(reader.GetValue(9), CultureInfo.InvariantCulture)));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> ReadJsonStringArray(object value)
    {
        var json = ReadJsonText(value);
        return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
    }

    private static string ReadJsonText(object value) => value switch
    {
        string text => text,
        System.Text.Json.JsonDocument document => document.RootElement.GetRawText(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "{}"
    };

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

    public async Task<PurchaseMatchEvaluationSnapshot> ReevaluateLatestAsync(
        string companyCode,
        long invoiceDocId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireCompany(companyCode);
        if (invoiceDocId <= 0)
            throw new ArgumentOutOfRangeException(nameof(invoiceDocId));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        PurchaseMatchPolicy policy;
        var policySql = """
            SELECT is_enabled, require_receipt_match, price_warning_percent, price_block_percent,
                   tax_warning_percent, tax_block_percent, base_variance_warning_amount,
                   base_variance_block_amount, policy_version
            FROM purchase_match_policies
            WHERE company_code = @company
            """ + (_provider == "PostgreSQL" ? " FOR SHARE" : string.Empty);
        await using (var policyCommand = CreateCommand(connection, transaction, policySql))
        {
            Add(policyCommand, "@company", companyCode.Trim());
            await using var reader = await policyCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("No purchase match policy is configured for this company.");
            policy = new PurchaseMatchPolicy(
                Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture));
        }
        policy.Validate();
        if (!policy.IsEnabled)
            throw new InvalidOperationException("Purchase match policy is disabled; re-evaluation is not required.");

        string? previousResult = null;
        var previousPolicyVersion = 0;
        var previousEvaluationVersion = 0;
        await using (var latest = CreateCommand(connection, transaction, """
            SELECT result_snapshot_json, decision, policy_version, evaluation_version
            FROM purchase_match_evaluations
            WHERE company_code = @company AND invoice_doc_id = @invoice
            ORDER BY evaluation_version DESC
            LIMIT 1
            """ + (_provider == "PostgreSQL" ? " FOR UPDATE" : string.Empty)))
        {
            Add(latest, "@company", companyCode.Trim());
            Add(latest, "@invoice", invoiceDocId);
            await using var reader = await latest.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The invoice has no prior match evaluation and cannot be re-evaluated from a missing source snapshot.");
            previousResult = ReadJsonText(reader.GetValue(0));
            previousPolicyVersion = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            previousEvaluationVersion = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
        }

        if (previousPolicyVersion == policy.PolicyVersion)
            throw new InvalidOperationException("The latest invoice evaluation already uses the current policy version.");

        using var previousDocument = System.Text.Json.JsonDocument.Parse(previousResult ?? "{}");
        var root = previousDocument.RootElement;
        if (!root.TryGetProperty("priceVariancePercent", out var priceValue) ||
            !root.TryGetProperty("taxVariancePercent", out var taxValue) ||
            !root.TryGetProperty("absoluteBaseVariance", out var baseValue))
            throw new InvalidOperationException("The prior evaluation snapshot does not contain all required variance measurements.");

        var priceVariance = priceValue.GetDecimal();
        var taxVariance = taxValue.GetDecimal();
        var baseVariance = baseValue.GetDecimal();
        var hasReceiptMatch = root.TryGetProperty("matchDetails", out var matchDetails) &&
                              matchDetails.ValueKind == System.Text.Json.JsonValueKind.Array &&
                              matchDetails.GetArrayLength() > 0;
        if (!hasReceiptMatch)
        {
            await using var matchCount = CreateCommand(connection, transaction, """
                SELECT COUNT(*)
                FROM purchase_receipt_invoice_matches m
                JOIN purchase_documents d ON d.doc_id = m.invoice_doc_id AND d.company_id = m.company_id
                JOIN companies c ON CAST(c.company_id AS TEXT) = d.company_id
                WHERE c.company_code = @company AND m.invoice_doc_id = @invoice
                """);
            Add(matchCount, "@company", companyCode.Trim());
            Add(matchCount, "@invoice", invoiceDocId);
            hasReceiptMatch = Convert.ToInt64(
                await matchCount.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) > 0;
        }
        var evaluation = PurchaseMatchPolicyEvaluator.Evaluate(
            policy,
            new PurchaseMatchVariance(priceVariance, taxVariance, baseVariance, hasReceiptMatch));

        var evaluationId = Guid.NewGuid();
        var evaluationVersion = previousEvaluationVersion + 1;
        var policySnapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            policyEnabled = policy.IsEnabled,
            requireReceiptMatch = policy.RequireReceiptMatch,
            priceWarning = policy.PriceWarningPercent,
            priceBlock = policy.PriceBlockPercent,
            taxWarning = policy.TaxWarningPercent,
            taxBlock = policy.TaxBlockPercent,
            baseWarning = policy.BaseVarianceWarningAmount,
            baseBlock = policy.BaseVarianceBlockAmount,
            policyVersion = policy.PolicyVersion
        });
        var snapshotValues = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var property in root.EnumerateObject())
            snapshotValues[property.Name] = property.Value.Clone();
        snapshotValues["decision"] = System.Text.Json.JsonSerializer.SerializeToElement(evaluation.Decision);
        snapshotValues["reasonCodes"] = System.Text.Json.JsonSerializer.SerializeToElement(evaluation.ReasonCodes);
        var resultSnapshot = System.Text.Json.JsonSerializer.Serialize(snapshotValues);
        var reasonsJson = System.Text.Json.JsonSerializer.Serialize(evaluation.ReasonCodes);
        var reasonsValue = _provider == "PostgreSQL" ? "CAST(@reasons AS jsonb)" : "@reasons";
        var policyValue = _provider == "PostgreSQL" ? "CAST(@policySnapshot AS jsonb)" : "@policySnapshot";
        var resultValue = _provider == "PostgreSQL" ? "CAST(@resultSnapshot AS jsonb)" : "@resultSnapshot";
        await using (var insert = CreateCommand(connection, transaction, $"""
            INSERT INTO purchase_match_evaluations
                (evaluation_id, company_code, invoice_doc_id, evaluation_version, policy_version,
                 decision, reason_codes_json, policy_snapshot_json, result_snapshot_json, evaluated_by)
            VALUES
                (@id, @company, @invoice, @version, @policyVersion,
                 @decision, {reasonsValue}, {policyValue}, {resultValue}, @actor)
            """))
        {
            Add(insert, "@id", _provider == "PostgreSQL" ? (object)evaluationId : evaluationId.ToString("D"));
            Add(insert, "@company", companyCode.Trim());
            Add(insert, "@invoice", invoiceDocId);
            Add(insert, "@version", evaluationVersion);
            Add(insert, "@policyVersion", policy.PolicyVersion);
            Add(insert, "@decision", evaluation.Decision);
            Add(insert, "@reasons", reasonsJson);
            Add(insert, "@policySnapshot", policySnapshot);
            Add(insert, "@resultSnapshot", resultSnapshot);
            Add(insert, "@actor", actor.Trim());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PurchaseMatchEvaluationSnapshot(
            evaluationId, invoiceDocId, evaluationVersion, policy.PolicyVersion,
            evaluation.Decision, evaluation.ReasonCodes, policySnapshot, resultSnapshot,
            actor.Trim(), DateTime.UtcNow);
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
        string? evaluatedBy = null;
        await using (var latest = CreateCommand(connection, transaction, """
            SELECT evaluation_id, decision, evaluated_by
            FROM purchase_match_evaluations
            WHERE company_code = @company AND invoice_doc_id = @invoice
            ORDER BY evaluation_version DESC
            LIMIT 1
            """ + (_provider == "PostgreSQL" ? " FOR UPDATE" : string.Empty)))
        {
            Add(latest, "@company", companyCode.Trim());
            Add(latest, "@invoice", invoiceDocId);
            await using var reader = await latest.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                latestEvaluationId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                latestDecision = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                evaluatedBy = reader.IsDBNull(2)
                    ? null
                    : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
            }
        }

        if (!string.Equals(latestDecision, "Blocked", StringComparison.Ordinal) ||
            !Guid.TryParse(latestEvaluationId, out var latestId) ||
            latestId != evaluationId)
            throw new InvalidOperationException(
                "Only the latest blocked purchase match evaluation can be released.");

        if (!string.IsNullOrWhiteSpace(evaluatedBy) &&
            string.Equals(evaluatedBy.Trim(), actor.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The user who evaluated and posted this supplier invoice cannot release its blocked match.");

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
