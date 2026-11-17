using System.Data.Common;
using System.Globalization;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>
/// Credit-limit enforcement for invoice posting (Phase A2, posting
/// completeness). Called inside the posting transaction of every path that
/// turns an invoice into a party-ledger balance:
/// - InvRepo.Post (InvoiceWin),
/// - CommercialDocumentRepository.PostSalesAsync (document chain),
/// - OrderDocumentRepository.ConvertToInvoiceAsync (OrdWin; the converted
///   invoice is created already posted).
///
/// Semantics (all amounts in the currencies the ledger itself records):
/// - Only a POSITIVE configured limit is enforced. A limit of 0 (the
///   schema default on every party card) means "no limit configured" and
///   never blocks; this matches how the existing data is populated.
/// - The limit is read from the party card the user maintains: the 031
///   parties row matched by (company, kind, code) wins; when it carries no
///   positive limit, the legacy customers/suppliers row identified by the
///   ledger party id is consulted. The first positive limit found applies.
/// - Outstanding balance is the party_ledger position for the exact
///   (company, kind, party id) the posting path writes: debit − credit
///   for customers, credit − debit for suppliers.
/// - Violation is a HARD BLOCK (ACC-5006). No override is honored: the
///   permission seeds contain no credit-override permission
///   (SALES.PRICE.OVERRIDE / SALES.DISCOUNT.OVERRIDE govern price and
///   discount, not credit exposure). If a SALES.CREDIT.OVERRIDE
///   permission is seeded in the future, the override belongs here, with
///   an audit entry, not at the call sites.
/// </summary>
public static class PartyCreditGuard
{
    /// <summary>
    /// Synchronous wrapper for the app's synchronous posting paths
    /// (InvRepo.Post). Safe to block on: every await inside the guard uses
    /// ConfigureAwait(false), so no continuation needs the caller's
    /// synchronization context.
    /// </summary>
    public static void EnsureWithinLimit(
        DbConnection connection,
        DbTransaction transaction,
        string provider,
        string companyKey,
        string partyKind,
        long partyId,
        string? partyCode,
        decimal newDocumentTotal,
        string paramPrefix)
        => EnsureWithinLimitAsync(connection, transaction, provider, companyKey, partyKind, partyId, partyCode, newDocumentTotal, paramPrefix)
            .GetAwaiter().GetResult();

    public static async Task EnsureWithinLimitAsync(
        DbConnection connection,
        DbTransaction transaction,
        string provider,
        string companyKey,
        string partyKind,
        long partyId,
        string? partyCode,
        decimal newDocumentTotal,
        string paramPrefix,
        CancellationToken cancellationToken = default)
    {
        if (partyId <= 0 || newDocumentTotal <= 0)
            return;
        var kind = partyKind?.Trim().ToUpperInvariant();
        if (kind is not ("C" or "S"))
            return;
        if (string.IsNullOrWhiteSpace(companyKey))
            return;

        var isPostgres = provider.Trim().ToLowerInvariant() is "postgres" or "postgresql" or "npgsql";
        var notDeleted = isPostgres ? "is_deleted = false" : "is_deleted = 0";
        var p = string.IsNullOrWhiteSpace(paramPrefix) ? "@" : paramPrefix.Trim();

        DbCommand Create(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command;
        }

        void Add(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        async Task<decimal?> ReadLimitAsync(string sql, Action<DbCommand> bind)
        {
            await using var command = Create(sql);
            bind(command);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull
                ? null
                : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        decimal? limit = null;

        // 1) The 031 parties card (the one the master-card windows edit),
        //    matched by company + kind + party code.
        if (!string.IsNullOrWhiteSpace(partyCode))
        {
            limit = await ReadLimitAsync(
                $"SELECT credit_limit FROM parties WHERE company_id = {p}company AND kind = {p}kind AND code = {p}code AND {notDeleted}",
                command =>
                {
                    Add(command, $"{p}company", companyKey);
                    Add(command, $"{p}kind", kind);
                    Add(command, $"{p}code", partyCode.Trim());
                }).ConfigureAwait(false);
        }

        // 2) The legacy customers/suppliers card identified by the ledger
        //    party id (party_ledger keys parties by this numeric id).
        if (limit is null or <= 0)
        {
            var table = kind == "C" ? "customers" : "suppliers";
            var idColumn = kind == "C" ? "customer_id" : "supplier_id";
            limit = await ReadLimitAsync(
                $"SELECT credit_limit FROM {table} WHERE {idColumn} = {p}party AND {notDeleted}",
                command => Add(command, $"{p}party", partyId)).ConfigureAwait(false);
        }

        if (limit is null or <= 0)
            return; // No limit configured for this party: posting proceeds.

        decimal balance;
        await using (var ledger = Create(
            $"SELECT COALESCE(SUM(debit), 0), COALESCE(SUM(credit), 0) FROM party_ledger WHERE company_id = {p}company AND party_kind = {p}kind AND party_id = {p}party"))
        {
            Add(ledger, $"{p}company", companyKey);
            Add(ledger, $"{p}kind", kind);
            Add(ledger, $"{p}party", partyId);
            await using var reader = await ledger.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return;
            var totalDebit = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
            var totalCredit = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            balance = kind == "C" ? totalDebit - totalCredit : totalCredit - totalDebit;
        }

        if (balance + newDocumentTotal > limit.Value)
            throw new AppError(
                ErrorCodes.CreditLimitExceeded,
                $"Posting this document would exceed the party credit limit. Limit: {limit.Value}; current balance: {balance}; document total: {newDocumentTotal}.",
                limit.Value, balance, newDocumentTotal);
    }
}
