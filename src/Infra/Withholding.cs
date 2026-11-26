using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>
/// Withholding tax / TDS engine (W1, migration 114) — a category of the
/// existing tax engine, NOT a parallel tax system. One shared evaluator
/// serves BOTH supplier-invoice posting paths (the manual path in
/// InvRepo and the chain path in OrderDocumentRepository) so a withheld
/// invoice posts identically wherever it was created.
///
/// Determination order (first hit wins):
///   1. the invoice-level withholding code persisted on the document
///      (purchase_documents.withholding_tax_code — document-type
///      applicability is invoice-level selection);
///   2. the supplier's default code (parties.withholding_tax_code);
///   3. the company's default withholding rule
///      (company_tax_rules.is_withholding = 1 AND is_default = 1).
/// A code named by (1) or (2) that has no active withholding rule fails
/// loudly with PUR-1016 — a configured withholding must never silently
/// post without withholding.
///
/// Base definition: the consideration for the supply EXCLUDING VAT —
/// the manual path passes (grand total − tax amount), the chain path
/// passes the lines subtotal; both are the same figure for the same
/// invoice. Thresholds: 'PerDocument' compares this invoice's base;
/// 'Cumulative' compares the supplier's calendar-year withholding
/// bases (stored on posted purchase invoices) including this invoice
/// and withholds only on the excess over the threshold.
///
/// GL shape of a withheld invoice (paid-at-posting = 0):
///   Dr Purchases / GRNI clearing        base (+ non-recoverable VAT)
///   Dr Input VAT (recoverable part)     VAT
///   Cr AP control 2201                  GRAND TOTAL (gross)
///   Dr AP control 2201                  withheld   (deduction leg)
///   Cr Withholding payable (rule acct)  withheld
/// Net AP balance = gross − withheld = the supplier's party-ledger
/// credit and the document's allocatable open amount.
/// </summary>
public static class WithholdingTax
{
    public const string RegisterTaxType = "WITHHOLDING";
    public const string RegisterTreatment = "Withholding";

    /// <summary>The evaluated withholding for one invoice. Evaluated =
    /// a withholding rule applied to the document (Amount may be 0 when
    /// the threshold was not reached — the evaluation itself is what the
    /// register row records).</summary>
    public sealed record Outcome(
        bool Evaluated, string? TaxCode, decimal Rate, decimal Base,
        decimal TaxableBase, decimal Amount, string? AccountCode, string CountryCode);

    public static readonly Outcome None = new(false, null, 0m, 0m, 0m, 0m, null, string.Empty);

    public static Outcome Evaluate(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyCode, Guid companyGuid, long? partyId, DateOnly docDate,
        decimal baseAmount, string? explicitCode, long excludeDocId = 0)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        if (!PartyLedger.TableExists(connection, transaction, prov, "company_tax_rules")
            || !PartyLedger.ColumnExists(connection, transaction, prov, "company_tax_rules", "is_withholding"))
            return None;
        var isPg = prov == "PostgreSQL";
        var trueVal = isPg ? "TRUE" : "1";

        string countryCode = string.Empty;
        if (PartyLedger.TableExists(connection, transaction, prov, "company_tax_settings"))
        {
            using var settings = Cmd(connection, transaction,
                $"SELECT country_code FROM company_tax_settings WHERE company_code = @company AND tax_enabled = {trueVal} LIMIT 1");
            Add(settings, "@company", companyCode);
            var value = settings.ExecuteScalar();
            countryCode = value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var code = string.IsNullOrWhiteSpace(explicitCode) ? null : explicitCode.Trim();
        var codeWasConfigured = code is not null;
        if (code is null && partyId is long supplierId
            && PartyLedger.ColumnExists(connection, transaction, prov, "parties", "withholding_tax_code"))
        {
            using var party = Cmd(connection, transaction,
                "SELECT withholding_tax_code FROM parties WHERE company_id = @company AND kind = 'S' AND ledger_id = @party LIMIT 1");
            Add(party, "@company", companyCode);
            Add(party, "@party", supplierId);
            var value = party.ExecuteScalar();
            var partyCode = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(partyCode))
            {
                code = partyCode.Trim();
                codeWasConfigured = true;
            }
        }
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            if (codeWasConfigured)
                throw new AppError(ErrorCodes.WithholdingRuleNotFound,
                    $"Withholding tax code '{code}' cannot be applied: the company's tax system is not enabled.", code ?? string.Empty);
            return None;
        }

        // Rule lookup: the named code, or the company's default
        // withholding rule when nothing named one.
        string taxType, dataStatus, accountCode, thresholdMode;
        decimal rate, threshold;
        string resolvedCode;
        using (var rule = Cmd(connection, transaction, code is not null
            ? $"""
               SELECT tax_code, rate, COALESCE(withholding_account_code, ''), withholding_threshold_amount,
                      COALESCE(withholding_threshold_mode, 'PerDocument'), COALESCE(data_status, 'USER_CONFIGURED'), tax_type
               FROM company_tax_rules
               WHERE company_code = @company AND country_code = @country AND tax_code = @code
                 AND is_active = {trueVal} AND is_withholding = {trueVal}
                 AND effective_from <= @date AND (effective_to IS NULL OR effective_to >= @date)
               ORDER BY effective_from DESC LIMIT 1
               """
            : $"""
               SELECT tax_code, rate, COALESCE(withholding_account_code, ''), withholding_threshold_amount,
                      COALESCE(withholding_threshold_mode, 'PerDocument'), COALESCE(data_status, 'USER_CONFIGURED'), tax_type
               FROM company_tax_rules
               WHERE company_code = @company AND country_code = @country
                 AND is_active = {trueVal} AND is_withholding = {trueVal} AND is_default = {trueVal}
                 AND effective_from <= @date AND (effective_to IS NULL OR effective_to >= @date)
               ORDER BY effective_from DESC LIMIT 1
               """))
        {
            Add(rule, "@company", companyCode);
            Add(rule, "@country", countryCode);
            if (code is not null) Add(rule, "@code", code);
            Add(rule, "@date", docDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = rule.ExecuteReader();
            if (!reader.Read())
            {
                if (codeWasConfigured)
                    throw new AppError(ErrorCodes.WithholdingRuleNotFound,
                        $"Withholding tax code '{code}' has no active withholding rule for {countryCode} on {docDate:yyyy-MM-dd}.", code ?? string.Empty);
                return None;
            }
            resolvedCode = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
            rate = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            accountCode = Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty;
            threshold = reader.IsDBNull(3) ? 0m : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
            thresholdMode = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "PerDocument";
            dataStatus = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? "USER_CONFIGURED";
            taxType = Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? string.Empty;
        }
        _ = taxType; // the register writes the WITHHOLDING vocabulary, not the rule's VAT type

        if (string.Equals(dataStatus, "BASELINE_REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase))
            throw new AppError(ErrorCodes.WithholdingConfigurationInvalid,
                $"Withholding rule '{resolvedCode}' is a starter rule and has not been reviewed.", resolvedCode);
        if (rate <= 0m || rate > 100m)
            throw new AppError(ErrorCodes.WithholdingConfigurationInvalid,
                $"Withholding rule '{resolvedCode}' carries an invalid rate {rate}.", resolvedCode);
        if (string.IsNullOrWhiteSpace(accountCode))
            accountCode = GlAccounts.WithholdingPayable;
        accountCode = accountCode.Trim();
        if (GlPosting.TryResolveAccount(connection, transaction, prov, companyGuid, accountCode) is null)
            throw new AppError(ErrorCodes.WithholdingConfigurationInvalid,
                $"Withholding rule '{resolvedCode}' points at account '{accountCode}', which is missing or not postable for this company.", resolvedCode);
        if (threshold < 0m)
            throw new AppError(ErrorCodes.WithholdingConfigurationInvalid,
                $"Withholding rule '{resolvedCode}' carries a negative threshold.", resolvedCode);

        var baseRounded = decimal.Round(baseAmount, 4, MidpointRounding.AwayFromZero);
        decimal taxableBase;
        if (threshold <= 0m)
        {
            taxableBase = baseRounded;
        }
        else if (string.Equals(thresholdMode, "Cumulative", StringComparison.OrdinalIgnoreCase))
        {
            var prior = PriorCumulativeBase(connection, transaction, prov, companyCode,
                companyGuid.ToString("D"), partyId, docDate, excludeDocId);
            var cumulative = prior + baseRounded;
            taxableBase = cumulative <= threshold
                ? 0m
                : cumulative - Math.Max(prior, threshold);
        }
        else
        {
            taxableBase = baseRounded < threshold ? 0m : baseRounded;
        }

        var amount = decimal.Round(taxableBase * rate / 100m, 2, MidpointRounding.AwayFromZero);
        return new Outcome(true, resolvedCode, rate, baseRounded, taxableBase, amount, accountCode, countryCode);
    }

    private static decimal PriorCumulativeBase(
        DbConnection connection, DbTransaction transaction, string prov,
        string companyCode, string companyGuid, long? partyId, DateOnly docDate, long excludeDocId)
    {
        if (partyId is not long supplierId) return 0m;
        if (!PartyLedger.ColumnExists(connection, transaction, prov, "purchase_documents", "withholding_base"))
            return 0m;
        var notDeleted = prov == "PostgreSQL" ? "is_deleted = FALSE" : "is_deleted = 0";
        var yearStart = new DateOnly(docDate.Year, 1, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yearEnd = new DateOnly(docDate.Year, 12, 31).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var cmd = Cmd(connection, transaction, $"""
            SELECT COALESCE(SUM(withholding_base), 0) FROM purchase_documents
            WHERE (CAST(company_id AS TEXT) = @company OR CAST(company_id AS TEXT) = @guid)
              AND party_id = @party AND doc_kind = 'PUR' AND status = 'Posted' AND {notDeleted}
              AND doc_date >= @from AND doc_date <= @to AND doc_id <> @exclude
            """);
        Add(cmd, "@company", companyCode);
        Add(cmd, "@guid", companyGuid);
        Add(cmd, "@party", supplierId);
        Add(cmd, "@from", yearStart);
        Add(cmd, "@to", yearEnd);
        Add(cmd, "@exclude", excludeDocId);
        return Convert.ToDecimal(cmd.ExecuteScalar() ?? 0m, CultureInfo.InvariantCulture);
    }

    /// <summary>Persists the evaluated outcome on the invoice header, in
    /// the caller's posting transaction (evaluated-below-threshold rows
    /// persist too: code + base with amount 0).</summary>
    public static void PersistOutcome(
        DbConnection connection, DbTransaction transaction, string provider, long docId, Outcome outcome)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        if (!PartyLedger.ColumnExists(connection, transaction, prov, "purchase_documents", "withholding_amount"))
            return;
        using var cmd = Cmd(connection, transaction, """
            UPDATE purchase_documents
            SET withholding_tax_code = @code, withholding_rate = @rate,
                withholding_base = @base, withholding_amount = @amount,
                withholding_account_code = @account
            WHERE doc_id = @doc
            """);
        Add(cmd, "@code", (object?)outcome.TaxCode ?? DBNull.Value);
        Add(cmd, "@rate", outcome.Rate);
        Add(cmd, "@base", outcome.Base);
        Add(cmd, "@amount", outcome.Amount);
        Add(cmd, "@account", (object?)outcome.AccountCode ?? DBNull.Value);
        Add(cmd, "@doc", docId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The withholding outcome stored on a posted invoice
    /// (amount 0 / null code when the invoice was never withheld).</summary>
    public static (decimal Amount, string? AccountCode, string? TaxCode, decimal Rate, decimal Base) ReadStored(
        DbConnection connection, DbTransaction transaction, string provider, long docId)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        if (!PartyLedger.ColumnExists(connection, transaction, prov, "purchase_documents", "withholding_amount"))
            return (0m, null, null, 0m, 0m);
        using var cmd = Cmd(connection, transaction, """
            SELECT COALESCE(withholding_amount, 0), withholding_account_code, withholding_tax_code,
                   COALESCE(withholding_rate, 0), COALESCE(withholding_base, 0)
            FROM purchase_documents WHERE doc_id = @doc
            """);
        Add(cmd, "@doc", docId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0m, null, null, 0m, 0m);
        return (Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Writes the register row for one withholding event into
    /// tax_transactions — the ONLY withholding store (reporting and
    /// certificates read it back). line_no 0 keeps it distinct from the
    /// per-line VAT rows; tax_type 'WITHHOLDING' + direction 'INPUT'
    /// distinguish it from input VAT in the register's vocabulary.
    /// Reversals write a NEGATIVE row under the reversal document's own
    /// kind/id (the register's existing return pattern); the original
    /// rows are immutable and remain.
    /// </summary>
    public static void WriteRegisterRow(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyCode, string countryCode, string docKind, long docId, string docNo,
        Outcome outcome, string currencyCode, decimal exchangeRate,
        string journalNo, string userId, DateOnly docDate, int sign = 1)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        if (!outcome.Evaluated || outcome.TaxCode is null) return;
        if (!PartyLedger.TableExists(connection, transaction, prov, "tax_transactions")) return;
        var taxable = outcome.Amount > 0m ? outcome.TaxableBase : outcome.Base;
        using var insert = Cmd(connection, transaction, """
            INSERT INTO tax_transactions
                (company_code, country_code, source_doc_kind, source_doc_id, source_doc_no,
                 line_no, tax_code, tax_type, tax_treatment, tax_rate, taxable_amount,
                 tax_amount, is_recoverable, tax_direction, tax_account_code, currency_code,
                 exchange_rate, base_tax_amount, journal_doc_no, created_by, document_date)
            VALUES
                (@company, @country, @kind, @docId, @docNo,
                 0, @taxCode, 'WITHHOLDING', 'Withholding', @rate, @taxable,
                 @tax, @recoverable, 'INPUT', @account, @currency,
                 @exchangeRate, @baseTax, @journal, @user, @docDate)
            ON CONFLICT (company_code, source_doc_kind, source_doc_id, line_no, tax_code) DO NOTHING
            """);
        Add(insert, "@company", companyCode);
        Add(insert, "@country", countryCode);
        Add(insert, "@kind", docKind);
        Add(insert, "@docId", docId);
        Add(insert, "@docNo", docNo);
        Add(insert, "@taxCode", outcome.TaxCode);
        Add(insert, "@rate", outcome.Rate);
        Add(insert, "@taxable", sign * taxable);
        Add(insert, "@tax", sign * outcome.Amount);
        Add(insert, "@recoverable", prov == "PostgreSQL" ? (object)false : 0);
        Add(insert, "@account", outcome.AccountCode ?? string.Empty);
        Add(insert, "@currency", currencyCode);
        Add(insert, "@exchangeRate", exchangeRate);
        Add(insert, "@baseTax", decimal.Round(sign * outcome.Amount * exchangeRate, 6, MidpointRounding.AwayFromZero));
        Add(insert, "@journal", journalNo);
        Add(insert, "@user", userId);
        Add(insert, "@docDate", docDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery();
    }

    private static DbCommand Cmd(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
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
}
