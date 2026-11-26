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
                   base_variance_block_amount, policy_version, receipt_over_tolerance_pct
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
            Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
            ReceiptOverTolerancePct: Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture));
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
                 receipt_over_tolerance_pct,
                 policy_version, created_by, updated_by)
            VALUES
                (@company, @enabled, @requireReceipt,
                 @priceWarning, @priceBlock, @taxWarning, @taxBlock,
                 @baseWarning, @baseBlock, @receiptTolerance, 1, @actor, @actor)
            ON CONFLICT(company_code) DO UPDATE SET
                is_enabled = excluded.is_enabled,
                require_receipt_match = excluded.require_receipt_match,
                price_warning_percent = excluded.price_warning_percent,
                price_block_percent = excluded.price_block_percent,
                tax_warning_percent = excluded.tax_warning_percent,
                tax_block_percent = excluded.tax_block_percent,
                base_variance_warning_amount = excluded.base_variance_warning_amount,
                base_variance_block_amount = excluded.base_variance_block_amount,
                receipt_over_tolerance_pct = excluded.receipt_over_tolerance_pct,
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
        Add(command, "@receiptTolerance", policy.ReceiptOverTolerancePct);
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
                   base_variance_block_amount, policy_version, receipt_over_tolerance_pct
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
                Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
                ReceiptOverTolerancePct: Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture));
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
            {
                // I3a: ON-DEMAND evaluation — a posted purchase invoice
                // with no prior evaluation (legacy / pre-policy / manual
                // path) is evaluated from the invoice document and its
                // mig-078 receipt matches, so it is no longer
                // permanently un-evaluable (and therefore permanently
                // un-allocatable when the policy blocks). The variance
                // snapshot is synthesized in the same shape the chain
                // posting writes, including per-line variance (I3b).
                // Tax variance is 0: a legacy invoice carries no PO tax
                // expectation to measure against.
                previousResult = await BuildOnDemandSnapshotAsync(
                    connection, transaction, companyCode.Trim(), invoiceDocId, cancellationToken).ConfigureAwait(false);
                previousPolicyVersion = -1; // never equal to the current version
                previousEvaluationVersion = 0;
            }
            else
            {
                previousResult = ReadJsonText(reader.GetValue(0));
                previousPolicyVersion = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
                previousEvaluationVersion = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            }
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
            receiptOverTolerancePct = policy.ReceiptOverTolerancePct,
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

    /// <summary>
    /// I3a/I3b: builds the variance snapshot for an on-demand (first)
    /// evaluation of a posted purchase invoice from the invoice header
    /// and its mig-078 matches. Throws when the invoice is not a posted
    /// purchase invoice of the company.
    /// </summary>
    private async Task<string> BuildOnDemandSnapshotAsync(
        DbConnection connection, DbTransaction transaction, string companyCode,
        long invoiceDocId, CancellationToken cancellationToken)
    {
        string companyGuid = companyCode;
        await using (var company = CreateCommand(connection, transaction,
            "SELECT CAST(company_id AS TEXT) FROM companies WHERE company_code = @company"))
        {
            Add(company, "@company", companyCode);
            var value = await company.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null and not DBNull)
                companyGuid = Convert.ToString(value, CultureInfo.InvariantCulture) ?? companyCode;
        }

        decimal subtotal, exchangeRate;
        await using (var invoice = CreateCommand(connection, transaction, """
            SELECT subtotal, exchange_rate FROM purchase_documents
            WHERE doc_id = @invoice AND (company_id = @guid OR company_id = @code)
              AND doc_kind = 'PUR' AND status = 'Posted'
              AND CAST(is_deleted AS TEXT) IN ('0', 'false')
            """))
        {
            Add(invoice, "@invoice", invoiceDocId);
            Add(invoice, "@guid", companyGuid);
            Add(invoice, "@code", companyCode);
            await using var reader = await invoice.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Only a posted purchase invoice of this company can be evaluated on demand.");
            subtotal = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
            exchangeRate = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        if (exchangeRate <= 0m) exchangeRate = 1m;

        var matchDetails = new List<object>();
        var lineVariance = new List<object>();
        decimal grniBaseAmount = 0m;
        await using (var matches = CreateCommand(connection, transaction, """
            SELECT match_id, purchase_order_id, receipt_doc_id, receipt_line_id,
                   invoice_line_id, material_id, matched_qty, receipt_unit_cost,
                   receipt_exchange_rate, invoice_unit_cost, invoice_exchange_rate,
                   receipt_base_amount, invoice_base_amount, variance_base_amount
            FROM purchase_receipt_invoice_matches
            WHERE invoice_doc_id = @invoice
            ORDER BY receipt_doc_id, receipt_line_id, invoice_line_id, match_id
            """))
        {
            Add(matches, "@invoice", invoiceDocId);
            await using var reader = await matches.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var receiptBase = Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture);
                var invoiceBase = Convert.ToDecimal(reader.GetValue(12), CultureInfo.InvariantCulture);
                grniBaseAmount += receiptBase;
                matchDetails.Add(new
                {
                    matchId = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture),
                    purchaseOrderId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    receiptDocId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    receiptLineId = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                    invoiceLineId = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                    materialId = Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                    matchedQty = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    receiptUnitCost = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    receiptExchangeRate = Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture),
                    invoiceUnitCost = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    invoiceExchangeRate = Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                    receiptBaseAmount = receiptBase,
                    invoiceBaseAmount = invoiceBase,
                    varianceBaseAmount = Convert.ToDecimal(reader.GetValue(13), CultureInfo.InvariantCulture)
                });
                lineVariance.Add(new
                {
                    invoiceLineId = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                    materialId = Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                    matchedQty = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    receiptRate = Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    invoiceRate = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                    rateVariance = Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture)
                        - Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    amountVariance = invoiceBase - receiptBase
                });
            }
        }

        var invoiceBaseAmount = decimal.Round(subtotal * exchangeRate, 6, MidpointRounding.AwayFromZero);
        var priceVariancePercent = grniBaseAmount == 0m
            ? (invoiceBaseAmount == 0m ? 0m : decimal.MaxValue)
            : decimal.Round(Math.Abs(invoiceBaseAmount - grniBaseAmount) / Math.Abs(grniBaseAmount) * 100m, 6, MidpointRounding.AwayFromZero);
        var absoluteBaseVariance = Math.Abs(invoiceBaseAmount - grniBaseAmount);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            priceVariancePercent,
            taxVariancePercent = 0m,
            absoluteBaseVariance,
            grniBaseAmount,
            invoiceBaseAmount,
            matchDetails,
            lineVariance,
            onDemand = true
        });
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
