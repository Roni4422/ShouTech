using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>Where an effective exchange rate came from (Step 7, J-5).
/// Recorded implicitly by the resolved rate stored on every money line
/// (journal_entry_lines.exchange_rate / party_ledger.exchange_rate,
/// migration 062) and explicitly by the resolution result.</summary>
public enum FxRateSource
{
    /// <summary>The line's currency IS the company base currency: rate 1.</summary>
    BaseCurrency,
    /// <summary>The document/entry header rate (explicit, stored).</summary>
    Document,
    /// <summary>The currency_rates master table (latest rate on or
    /// before the entry date; latest overall as fallback).</summary>
    Master,
}

/// <summary>An effective exchange rate plus its source and the base
/// currency the converted amounts are expressed in.</summary>
public sealed record FxResolution(decimal Rate, FxRateSource Source, string? BaseCurrency);

/// <summary>
/// The ONE exchange-rate resolution path (Fix Phase Step 7, sweep J-5).
/// Before this step the rate stored on a journal/document header was
/// never applied anywhere: lines carried no base-currency amounts and
/// every report summed foreign amounts as if 1 unit = 1 base unit.
///
/// Precedence (the rate source is explicit by construction):
///   1. transaction currency == company base currency
///      (companies.currency_code) -> rate 1, source BaseCurrency;
///   2. a document/entry rate &gt; 0 stored on the header -> that rate,
///      source Document;
///   3. the currency_rates master: latest rate on or before the entry
///      date for (company, currency), latest overall as fallback ->
///      source Master;
///   4. no rate anywhere -> AppError ACC-5015 (ExchangeRateMissing).
///      Posting NEVER silently falls back to 1.0 for a foreign currency.
///
/// Base amounts are transaction x rate, rounded to 4dp AwayFromZero —
/// the same rounding the migration-062 backfill uses, so live postings
/// and backfilled history agree.
///
/// Hand-built fixtures predating migration 062 / the companies currency
/// column degrade safely: a missing companies.currency_code column or a
/// missing currency_rates table simply means "base/master unknown", and
/// an explicit document rate (rule 2) still resolves.
/// </summary>
public static class FxRates
{
    public static decimal ToBase(decimal amount, decimal rate)
        => decimal.Round(amount * rate, 4, MidpointRounding.AwayFromZero);

    /// <summary>Converts a set of (debit, credit) transaction amounts to
    /// base amounts at one rate, absorbing the 4dp rounding residue on
    /// the largest line so a transaction-balanced set stays EXACTLY
    /// base-balanced (the mixed-rate/base-balance rule of Step 7: an
    /// entry validates in base currency, not only in transaction
    /// currency).</summary>
    public static List<(decimal BaseDebit, decimal BaseCredit)> ConvertBalanced(
        IReadOnlyList<(decimal Debit, decimal Credit)> lines, decimal rate)
    {
        var result = new List<(decimal BaseDebit, decimal BaseCredit)>(lines.Count);
        foreach (var (debit, credit) in lines)
            result.Add((ToBase(debit, rate), ToBase(credit, rate)));
        if (result.Count == 0 || rate == 1m)
            return result;
        var debitSum = result.Sum(r => r.BaseDebit);
        var creditSum = result.Sum(r => r.BaseCredit);
        var residue = debitSum - creditSum;
        if (residue == 0m)
            return result;
        // The residue can only come from rounding (the caller validated
        // transaction balance); put it on the largest line of the side
        // that is short... actually on the largest line overall of the
        // over-stated side, keeping every line non-negative.
        var idx = 0;
        var largest = -1m;
        for (var i = 0; i < result.Count; i++)
        {
            var magnitude = Math.Max(result[i].BaseDebit, result[i].BaseCredit);
            if (magnitude > largest) { largest = magnitude; idx = i; }
        }
        var line = result[idx];
        if (line.BaseDebit > 0 && residue > 0)
            result[idx] = (line.BaseDebit - residue, line.BaseCredit);
        else if (line.BaseCredit > 0 && residue < 0)
            result[idx] = (line.BaseDebit, line.BaseCredit + residue);
        else if (line.BaseDebit > 0)
            result[idx] = (line.BaseDebit - residue, line.BaseCredit);
        else
            result[idx] = (line.BaseDebit, line.BaseCredit + residue);
        return result;
    }

    /// <summary>The company's base currency (companies.currency_code),
    /// resolved by company Guid; null when the company row, the column,
    /// or the value is absent (hand-built fixtures).</summary>
    public static string? ResolveBaseCurrency(
        DbConnection connection, DbTransaction? transaction, string provider, Guid companyId)
    {
        if (!PartyLedger.TableExists(connection, transaction, provider, "companies"))
            return null;
        if (!PartyLedger.ColumnExists(connection, transaction, provider, "companies", "currency_code"))
            return null;
        using var command = GlPosting.CreateCommand(connection, transaction,
            "SELECT currency_code FROM companies WHERE CAST(company_id AS TEXT) = @company");
        GlPosting.Add(command, "@company", companyId.ToString("D"));
        var value = command.ExecuteScalar();
        var code = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrWhiteSpace(code) ? null : code.ToUpperInvariant();
    }

    /// <summary>Base currency resolved by company CODE (the app layer's
    /// key); null when unknown.</summary>
    public static string? ResolveBaseCurrencyByCode(
        DbConnection connection, DbTransaction? transaction, string provider, string companyCode)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            return null;
        if (!PartyLedger.TableExists(connection, transaction, provider, "companies"))
            return null;
        if (!PartyLedger.ColumnExists(connection, transaction, provider, "companies", "currency_code"))
            return null;
        using var command = GlPosting.CreateCommand(connection, transaction,
            "SELECT currency_code FROM companies WHERE company_code = @code");
        GlPosting.Add(command, "@code", companyCode.Trim());
        var value = command.ExecuteScalar();
        var code = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrWhiteSpace(code) ? null : code.ToUpperInvariant();
    }

    /// <summary>Resolves the effective rate for a posting keyed by
    /// company Guid (the journal/document repositories' world). The
    /// currency_rates master is keyed by company CODE (the namespace
    /// CurCard/MasterCardRepository maintain), so both of the company's
    /// own keys are consulted — never another company's.</summary>
    public static FxResolution Resolve(
        DbConnection connection, DbTransaction? transaction, string provider,
        Guid companyId, string currencyCode, DateOnly entryDate, decimal documentRate)
    {
        var currency = NormalizeCurrency(currencyCode);
        var baseCurrency = ResolveBaseCurrency(connection, transaction, provider, companyId);
        if (baseCurrency is not null && currency == baseCurrency)
            return new FxResolution(1m, FxRateSource.BaseCurrency, baseCurrency);
        if (documentRate > 0)
            return new FxResolution(documentRate, FxRateSource.Document, baseCurrency);

        var keys = new List<string>();
        var companyCode = ResolveCompanyCode(connection, transaction, provider, companyId);
        if (!string.IsNullOrWhiteSpace(companyCode)) keys.Add(companyCode);
        keys.Add(companyId.ToString("D"));
        var master = LookupMasterRate(connection, transaction, provider, keys, currency, entryDate);
        if (master is decimal rate)
            return new FxResolution(rate, FxRateSource.Master, baseCurrency);
        throw Missing(currency, entryDate);
    }

    /// <summary>Resolves the effective rate for a posting keyed by
    /// company CODE (the app layer's InvRepo world).</summary>
    public static FxResolution ResolveByCompanyCode(
        DbConnection connection, DbTransaction? transaction, string provider,
        string companyCode, string currencyCode, DateOnly entryDate, decimal documentRate)
    {
        var currency = NormalizeCurrency(currencyCode);
        var baseCurrency = ResolveBaseCurrencyByCode(connection, transaction, provider, companyCode);
        if (baseCurrency is not null && currency == baseCurrency)
            return new FxResolution(1m, FxRateSource.BaseCurrency, baseCurrency);
        if (documentRate > 0)
            return new FxResolution(documentRate, FxRateSource.Document, baseCurrency);
        var master = LookupMasterRate(connection, transaction, provider,
            new[] { companyCode.Trim() }, currency, entryDate);
        if (master is decimal rate)
            return new FxResolution(rate, FxRateSource.Master, baseCurrency);
        throw Missing(currency, entryDate);
    }

    /// <summary>Non-throwing master lookup used by party-ledger writers
    /// whose amount is already denominated in the document currency and
    /// whose caller supplied the document rate: returns the master rate
    /// or null. Company keys are the caller's own (code and/or Guid).</summary>
    public static decimal? LookupMasterRate(
        DbConnection connection, DbTransaction? transaction, string provider,
        IReadOnlyList<string> companyKeys, string currencyCode, DateOnly entryDate)
    {
        if (!PartyLedger.TableExists(connection, transaction, provider, "currency_rates"))
            return null;
        if (!PartyLedger.ColumnExists(connection, transaction, provider, "currency_rates", "rate"))
            return null;
        var currency = NormalizeCurrency(currencyCode);
        var keys = companyKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct().ToList();
        if (keys.Count == 0)
            return null;
        var inList = string.Join(", ", keys.Select((_, i) => $"@k{i}"));
        foreach (var dated in new[] { true, false })
        {
            var sql = dated
                ? $"SELECT rate FROM currency_rates WHERE company_id IN ({inList}) AND currency_code = @cur AND rate > 0 AND rate_date <= @date ORDER BY rate_date DESC LIMIT 1"
                : $"SELECT rate FROM currency_rates WHERE company_id IN ({inList}) AND currency_code = @cur AND rate > 0 ORDER BY rate_date DESC LIMIT 1";
            using var command = GlPosting.CreateCommand(connection, transaction, sql);
            for (var i = 0; i < keys.Count; i++)
                GlPosting.Add(command, $"@k{i}", keys[i]);
            GlPosting.Add(command, "@cur", currency);
            if (dated)
                GlPosting.Add(command, "@date", entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var value = command.ExecuteScalar();
            if (value is not null and not DBNull)
            {
                var rate = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (rate > 0) return rate;
            }
        }
        return null;
    }

    private static string? ResolveCompanyCode(
        DbConnection connection, DbTransaction? transaction, string provider, Guid companyId)
    {
        if (!PartyLedger.TableExists(connection, transaction, provider, "companies"))
            return null;
        using var command = GlPosting.CreateCommand(connection, transaction,
            "SELECT company_code FROM companies WHERE CAST(company_id AS TEXT) = @company");
        GlPosting.Add(command, "@company", companyId.ToString("D"));
        var value = command.ExecuteScalar();
        var code = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    private static string NormalizeCurrency(string? currencyCode)
        => string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim().ToUpperInvariant();

    private static AppError Missing(string currency, DateOnly entryDate)
        => new(ErrorCodes.ExchangeRateMissing,
            $"No exchange rate is available for currency '{currency}': the document carries no rate and no master rate exists on or before {entryDate:yyyy-MM-dd}.",
            currency, entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}
