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
/// - The limit is read from the ONE party card the user maintains
///   (migration 061): the parties row matched by ledger id
///   (party_ledger.party_id = parties.ledger_id) or, when a code is
///   supplied, by (company, kind, code). EVERY lookup is company-scoped —
///   the pre-unification legacy fallback read customers/suppliers by id
///   with NO company filter and could return another company's limit
///   (sweep G-1); that fallback is gone with the legacy tables.
/// - Outstanding balance is the party_ledger position for the exact
///   (company, kind, party id) the posting path writes: debit − credit
///   for customers, credit − debit for suppliers. The company predicate
///   accepts the company's own two keys (code, and the Guid form some
///   repositories still pass) — both resolve through the companies
///   registry to the same company, never to another one.
/// - Step 7 (J-5): the balance and the incoming document total are
///   compared in BASE currency — the guard sums base_debit/base_credit
///   (migration 062; legacy rows fall back to their transaction amounts
///   via COALESCE) and every caller passes the document's BASE total
///   (stored total x effective rate). A foreign-currency document can
///   no longer slip under a base-currency limit at face value.
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

        // The company's own keys: the code party_ledger rows carry after
        // migration 061, plus the key the caller passed (a Guid for the
        // chain/order repositories). Both are THIS company — the party
        // and balance predicates below accept either form and no other.
        var companyCode = await PartyLedger.ResolveCompanyCodeAsync(
            connection, transaction, provider, companyKey, cancellationToken).ConfigureAwait(false);
        var companyKeys = new List<string> { companyCode };
        if (!companyKeys.Contains(companyKey.Trim()))
            companyKeys.Add(companyKey.Trim());
        var companyIn = string.Join(", ", companyKeys.Select((_, i) => $"{p}co{i}"));

        void AddCompanies(DbCommand command)
        {
            for (var i = 0; i < companyKeys.Count; i++)
                Add(command, $"{p}co{i}", companyKeys[i]);
        }

        decimal? limit = null;

        // 1) The canonical party by its ledger id — the exact identity the
        //    posting path is about to write for, in this company + kind.
        limit = await ReadLimitAsync(
            $"SELECT credit_limit FROM parties WHERE company_id IN ({companyIn}) AND kind = {p}kind AND ledger_id = {p}party AND {notDeleted}",
            command =>
            {
                AddCompanies(command);
                Add(command, $"{p}kind", kind);
                Add(command, $"{p}party", partyId);
            }).ConfigureAwait(false);

        // 2) The same canonical party by (company, kind, code), when the
        //    caller knows the code and the id lookup found no live party.
        if ((limit is null or <= 0) && !string.IsNullOrWhiteSpace(partyCode))
        {
            limit = await ReadLimitAsync(
                $"SELECT credit_limit FROM parties WHERE company_id IN ({companyIn}) AND kind = {p}kind AND code = {p}code AND {notDeleted}",
                command =>
                {
                    AddCompanies(command);
                    Add(command, $"{p}kind", kind);
                    Add(command, $"{p}code", partyCode.Trim());
                }).ConfigureAwait(false);
        }

        if (limit is null or <= 0)
            return; // No limit configured for this party: posting proceeds.

        decimal balance;
        // Base-currency sums where migration 062 has landed; the
        // COALESCE keeps pre-062 rows (and hand-built fixtures without
        // the columns are handled by the column probe) honest.
        var hasBase = PartyLedger.ColumnExists(connection, transaction, provider, "party_ledger", "base_debit");
        var debitExpr = hasBase ? "COALESCE(base_debit, debit)" : "debit";
        var creditExpr = hasBase ? "COALESCE(base_credit, credit)" : "credit";
        await using (var ledger = Create(
            $"SELECT COALESCE(SUM({debitExpr}), 0), COALESCE(SUM({creditExpr}), 0) FROM party_ledger WHERE company_id IN ({companyIn}) AND party_kind = {p}kind AND party_id = {p}party"))
        {
            AddCompanies(ledger);
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
