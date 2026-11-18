// ========================================================================
// FILE: Services/Accounting.cs
// PROJECT: SHOUTECH ERP — Accounting (COA + journal service)
// Merged: IAccServ + AccServ + AccRepo
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;

namespace ShouTech.App.Services
{

    // ========== IAccServ.cs ==========
/// <summary>
    /// Enterprise Accounting service interface (Journal entries).
    /// </summary>
    public interface IAccServ
    {
        Task<Guid> CreateJournalAsync(DateOnly entryDate, string description, string userId, CancellationToken ct = default);
        Task AddJournalLineAsync(
            Guid journalId, Guid accountId, decimal debit, decimal credit,
            string? costCenter, string? note, string userId, CancellationToken ct = default);
        Task PostJournalAsync(Guid journalId, string userId, CancellationToken ct = default);
        Task SoftDeleteDraftJournalAsync(Guid journalId, string userId, CancellationToken ct = default);
    }

    // ========== AccServ.cs ==========

    internal sealed class JournalLine
    {
        public Guid AccountId { get; init; }
        public decimal Debit { get; init; }
        public decimal Credit { get; init; }
        public string? CostCenter { get; init; }
        public string? Note { get; init; }
    }

    internal enum JournalStatus { Draft, Posted }

    internal sealed class JournalEntry
    {
        public Guid Id { get; init; }
        public DateOnly EntryDate { get; init; }
        public string Description { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; init; }
        public JournalStatus Status { get; set; } = JournalStatus.Draft;
        public string? PostedBy { get; set; }
        public DateTimeOffset? PostedAtUtc { get; set; }
        public bool IsDeleted { get; set; }
        public string? DeletedBy { get; set; }
        public List<JournalLine> Lines { get; } = new();

        public decimal TotalDebit => Lines.Sum(l => l.Debit);
        public decimal TotalCredit => Lines.Sum(l => l.Credit);
    }

/// <summary>
    /// Enterprise Accounting service (Journal entries).
    /// Protected by License + Permissions.
    /// </summary>
    public sealed class AccServ : IAccServ
    {
        private readonly ILicServ _lic;
        private readonly IPermServ _perm;

        private readonly Dictionary<Guid, JournalEntry> _journals = new();
        private readonly object _sync = new();

        public AccServ(ILicServ lic, IPermServ perm)
        {
            _lic = lic ?? throw new ArgumentNullException(nameof(lic));
            _perm = perm ?? throw new ArgumentNullException(nameof(perm));
        }

        public async Task<Guid> CreateJournalAsync(DateOnly entryDate, string description, string userId, CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Acc, ct);

            if (!_perm.Has(PermKey.AccCreate))
                throw new UnauthorizedAccessException("You do not have permission to create journal entries.");

            if (string.IsNullOrWhiteSpace(description))
                throw new ArgumentException("Description is required.");

            var journal = new JournalEntry
            {
                Id = Guid.NewGuid(),
                EntryDate = entryDate,
                Description = description.Trim(),
                CreatedBy = userId,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            lock (_sync)
                _journals[journal.Id] = journal;

            return journal.Id;
        }

        public async Task AddJournalLineAsync(
            Guid journalId, Guid accountId, decimal debit, decimal credit,
            string? costCenter, string? note, string userId, CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Acc, ct);

            if (!_perm.Has(PermKey.AccCreate))
                throw new UnauthorizedAccessException("You do not have permission to modify journal entries.");

            if (debit < 0 || credit < 0)
                throw new ArgumentException("Debit and Credit cannot be negative.");
            if (debit > 0 && credit > 0)
                throw new ArgumentException("A line cannot have both debit and credit.");
            if (debit == 0 && credit == 0)
                throw new ArgumentException("Either debit or credit must have a value.");

            lock (_sync)
            {
                var journal = GetOpenJournal(journalId);
                journal.Lines.Add(new JournalLine
                {
                    AccountId = accountId,
                    Debit = decimal.Round(debit, 4),
                    Credit = decimal.Round(credit, 4),
                    CostCenter = costCenter,
                    Note = note
                });
            }
        }

        private JournalEntry GetOpenJournal(Guid journalId)
        {
            if (!_journals.TryGetValue(journalId, out var journal) || journal.IsDeleted)
                throw new KeyNotFoundException("Journal entry not found.");
            if (journal.Status == JournalStatus.Posted)
                throw new InvalidOperationException(
                    "Posted journal entries are immutable. Correct via a reversing entry instead.");
            return journal;
        }

        public async Task PostJournalAsync(Guid journalId, string userId, CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Acc, ct);

            if (!_perm.Has(PermKey.AccPost))
                throw new UnauthorizedAccessException("You do not have permission to post journal entries.");

            lock (_sync)
            {
                var journal = GetOpenJournal(journalId);

                if (journal.Lines.Count < 2)
                    throw new InvalidOperationException(
                        "A journal entry must contain at least two lines before posting.");

                var totalDebit = decimal.Round(journal.TotalDebit, 4);
                var totalCredit = decimal.Round(journal.TotalCredit, 4);

                if (totalDebit <= 0 || totalCredit <= 0)
                    throw new InvalidOperationException(
                        "A journal entry with zero totals cannot be posted.");

                if (totalDebit != totalCredit)
                    throw new InvalidOperationException(
                        $"Unbalanced journal entry: total debit ({totalDebit:N2}) " +
                        $"does not equal total credit ({totalCredit:N2}). " +
                        $"Difference: {Math.Abs(totalDebit - totalCredit):N2}.");

                journal.Status = JournalStatus.Posted;
                journal.PostedBy = userId;
                journal.PostedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public async Task SoftDeleteDraftJournalAsync(Guid journalId, string userId, CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Acc, ct);

            if (!_perm.Has(PermKey.AccDelete))
                throw new UnauthorizedAccessException("You do not have permission to delete journal entries.");

            lock (_sync)
            {
                if (!_journals.TryGetValue(journalId, out var journal) || journal.IsDeleted)
                    throw new KeyNotFoundException("Journal entry not found.");

                if (journal.Status == JournalStatus.Posted)
                    throw new InvalidOperationException(
                        "Posted journal entries cannot be deleted. " +
                        "Issue a reversing entry to correct posted transactions.");

                journal.IsDeleted = true;
                journal.DeletedBy = userId;
            }
        }
    }

    // ========== AccRepo.cs ==========
/// <summary>
/// Persistent Chart of Accounts repository shared by the Account Card and COA window.
///
/// CANONICAL STORE (Fix Phase Step 2, migration 059): the one and only chart
/// table is <c>chart_of_accounts</c> — the same table JournalRepository, the
/// posting triggers, ChequeRepository and MasterCardRepository use, and the
/// table <c>journal_entry_lines.account_id</c> references. The legacy
/// <c>accounts</c> table (integer id, company CODE key, cached balances) was
/// merged into it by migration 059 and dropped; nothing reads or writes it.
///
/// Consequences of the unified model:
///   * Company scoping: chart rows are keyed by the company Guid
///     (companies.company_id). The shell carries the company CODE
///     (AppState.CurrentCompanyCode), so every entry point resolves
///     code -&gt; Guid through the companies row first (the same resolution
///     JournalRepository.ResolveCompanyIdAsync performs for JrnWin).
///   * Balances are NEVER stored: AccRec.Balance/Debit/Credit are computed
///     at load from POSTED journal_entry_lines (status = 1). The old cached
///     accounts.balance/debit/credit columns are gone (sweep defect C-2).
///   * Parent linkage is by parent_account_id (Guid FK), not by code text:
///     renaming a parent code can no longer orphan its children (C-6), and
///     renumbering only rewrites descendant CODES.
///   * The lifecycle guard (C-3) checks journal_entry_lines by the account
///     Guid — the reference that was structurally invisible when journal
///     lines held Guids and the guard compared integer ids.
/// Provider-specific SQL is isolated here so the UI remains database-neutral.
/// </summary>
internal static class AccRepo
{
    internal sealed record AccRec(
        string Id,
        string Code,
        string? ParentCode,
        string Name,
        string NameEn,
        string PostingMode,
        string Status,
        string Risk,
        string CostCenter,
        bool Reconcile,
        bool Taxable,
        bool ForeignCurrency,
        bool CostRequired,
        bool Budget,
        bool IsDefault,
        decimal TaxRate,
        string Description,
        decimal Balance,
        decimal Debit,
        decimal Credit);

    internal sealed record AccountGuard(bool CanDelete, bool CanDisable, IReadOnlyList<string> Reasons);

    /// <summary>
    /// Account type derived from the code root, matching the seed chart's
    /// numbering (1 Assets, 2 Liabilities, 3 Equity, 5 Revenue; 4 Purchases
    /// and 6 Expenses are EXPENSE-nature). Used on insert and whenever a code
    /// is renumbered, so account_type can never disagree with the code root
    /// for UI-created accounts. Migration 059 applied the same rule.
    /// </summary>
    internal static string AccountTypeForCode(string code)
    {
        var root = string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim()[..1];
        return root switch
        {
            "1" => "ASSET",
            "2" => "LIABILITY",
            "3" => "EQUITY",
            "5" => "REVENUE",
            _ => "EXPENSE"
        };
    }

    private static bool IsPg => DatabaseRuntime.IsPostgreSql();
    private static string FalseLiteral => IsPg ? "FALSE" : "0";
    private static string TrueLiteral => IsPg ? "TRUE" : "1";

    /// <summary>Binds a company Guid in the provider's native shape (uuid on PostgreSQL, text on SQLite).</summary>
    private static object CompanyValue(string companyGuid)
        => IsPg ? Guid.Parse(companyGuid) : companyGuid;

    /// <summary>Binds an account Guid in the provider's native shape.</summary>
    private static object AccountValue(string accountGuid)
        => IsPg ? Guid.Parse(accountGuid) : accountGuid;

    public static void EnsureSchema()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("No connection to the current company database.");
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = IsPg
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'chart_of_accounts';"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'chart_of_accounts';";
        if (Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Chart of accounts schema is missing. Apply the provider migrations under dba/mig.");
    }

    /// <summary>
    /// Resolves the shell's company CODE to the canonical company Guid used
    /// by chart_of_accounts / journal tables. Falls back to the code itself
    /// when it already is a Guid, and to the code when the database carries
    /// no companies registry (single-tenant layout, where tenant tables key
    /// by the code itself).
    /// </summary>
    private static string ResolveCompanyGuid(DbConnection connection, DbTransaction? transaction)
    {
        var code = AppState.CurrentCompanyCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("There is no active company in the current session.");

        var hasCompanies = false;
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = IsPg
                ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'companies';"
                : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'companies';";
            hasCompanies = Convert.ToInt32(probe.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) == 1;
        }

        if (hasCompanies)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT company_id FROM companies WHERE company_code = $code;";
            Add(command, "$code", code.Trim());
            var value = command.ExecuteScalar();
            if (value is not null and not DBNull)
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        }

        if (Guid.TryParse(code.Trim(), out _))
            return code.Trim();

        if (!hasCompanies)
            return code.Trim();

        throw new InvalidOperationException(
            $"Active company '{code.Trim()}' could not be resolved to a database company ID.");
    }

    private static string SelectColumnsSql() => $"""
        SELECT CAST(a.account_id AS TEXT), a.account_code, p.account_code,
               COALESCE(a.name_ar, a.account_name), a.account_name,
               CASE WHEN a.is_postable THEN 'Postable' ELSE 'Summary' END,
               CASE WHEN a.is_active THEN 'Active' ELSE 'Disabled' END,
               a.risk, a.cost_center_mode, a.allow_reconcile,
               a.taxable, a.foreign_currency, a.cost_required, a.budget, a.is_default,
               a.tax_rate, a.description,
               COALESCE(bal.d, 0), COALESCE(bal.cr, 0)
        FROM chart_of_accounts a
        LEFT JOIN chart_of_accounts p ON p.account_id = a.parent_account_id
        LEFT JOIN (
            SELECT l.account_id AS aid, SUM(l.debit) AS d, SUM(l.credit) AS cr
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            WHERE j.status = 1
            GROUP BY l.account_id
        ) bal ON bal.aid = a.account_id
        """ + Environment.NewLine;

    public static List<AccRec> LoadAll()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return new();
        EnsureSchema();
        using var connection = Open(cs);
        var companyGuid = ResolveCompanyGuid(connection, null);
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumnsSql() + $"""
            WHERE a.company_id = $company AND a.is_deleted = {FalseLiteral}
            ORDER BY a.account_code;
            """;
        Add(command, "$company", CompanyValue(companyGuid));
        using var reader = command.ExecuteReader();
        var result = new List<AccRec>();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    public static AccRec? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        EnsureSchema();
        using var connection = Open(cs);
        var companyGuid = ResolveCompanyGuid(connection, null);
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumnsSql() + $"""
            WHERE a.company_id = $company AND a.account_id = $id AND a.is_deleted = {FalseLiteral};
            """;
        Add(command, "$company", CompanyValue(companyGuid));
        Add(command, "$id", AccountValue(id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static string NextChildCode(string parentCode)
    {
        var normalized = RequireCode(parentCode);
        var rows = LoadAll().Where(x => string.Equals(x.ParentCode, normalized, StringComparison.OrdinalIgnoreCase));
        var suffixes = rows
            .Select(x => x.Code.Length > normalized.Length ? x.Code[normalized.Length..] : string.Empty)
            .Where(s => s.Length > 0 && s.All(char.IsDigit))
            .Select(s => new { Value = ParsePositive(s), Width = s.Length })
            .Where(x => x.Value > 0)
            .ToList();

        if (suffixes.Count == 0)
            return normalized + "01";

        var next = suffixes.Max(x => x.Value) + 1;
        var width = Math.Max(2, suffixes.Max(x => x.Width));
        var formatted = next.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
        return normalized + formatted;
    }

    public static bool CodeExists(string code, string? excludeId = null)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return false;
        EnsureSchema();
        using var connection = Open(cs);
        var companyGuid = ResolveCompanyGuid(connection, null);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM chart_of_accounts WHERE company_id=$company AND account_code=$code AND is_deleted={FalseLiteral}" +
                              (!string.IsNullOrWhiteSpace(excludeId) ? " AND account_id<>$id" : "") + ";";
        Add(command, "$company", CompanyValue(companyGuid));
        Add(command, "$code", RequireCode(code));
        if (!string.IsNullOrWhiteSpace(excludeId)) Add(command, "$id", AccountValue(excludeId!));
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public static string Save(AccRec? current, AccRec value, bool renumberDescendants)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("There is no connection to the current company database.");
        EnsureSchema();
        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        try
        {
            var companyGuid = ResolveCompanyGuid(connection, transaction);

            if (current is not null
                && !string.Equals(current.Status, value.Status, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Status, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                var guard = CheckLifecycleOnConnection(connection, transaction, current);
                if (!guard.CanDisable)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, guard.Reasons));
            }

            var parentId = ResolveParentId(connection, transaction, companyGuid, value.ParentCode, current?.Id);

            if (current is null)
            {
                if (CodeExistsOnConnection(connection, transaction, companyGuid, value.Code, null))
                    throw new InvalidOperationException("The account number is already in use.");
                var newId = Guid.NewGuid();
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = InsertSql();
                Bind(insert, value, newId.ToString("D"), companyGuid, parentId);
                insert.ExecuteNonQuery();
                transaction.Commit();
                return newId.ToString("D");
            }

            if (!string.Equals(current.Code, value.Code, StringComparison.OrdinalIgnoreCase)
                && CodeExistsOnConnection(connection, transaction, companyGuid, value.Code, current.Id))
                throw new InvalidOperationException("The new account number is already in use.");

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = UpdateSql();
            Bind(update, value, current.Id, companyGuid, parentId);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The account could not be saved because it no longer exists in the current company.");

            if (!string.Equals(current.Code, value.Code, StringComparison.OrdinalIgnoreCase)
                && renumberDescendants
                && HasChildrenOnConnection(connection, transaction, current.Id))
            {
                RenumberDescendants(connection, transaction, companyGuid, current.Code, value.Code);
            }
            // When the code changes WITHOUT renumbering, children keep their
            // codes but stay linked: parentage is parent_account_id (this
            // row's Guid, unchanged), so there is nothing to re-point. The
            // legacy accounts model had to rewrite parent_code text here and
            // silently broke the code-prefix invariant (sweep defect C-6);
            // the unified model cannot desync the link itself.

            transaction.Commit();
            return current.Id;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string? ResolveParentId(DbConnection connection, DbTransaction transaction, string companyGuid, string? parentCode, string? selfId)
    {
        if (string.IsNullOrWhiteSpace(parentCode))
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT CAST(account_id AS TEXT) FROM chart_of_accounts WHERE company_id=$company AND account_code=$code AND is_deleted={FalseLiteral};";
        Add(command, "$company", CompanyValue(companyGuid));
        Add(command, "$code", parentCode.Trim());
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            throw new InvalidOperationException($"The parent account {parentCode.Trim()} could not be found in the current company.");
        var parentId = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (!string.IsNullOrWhiteSpace(selfId) && string.Equals(parentId, selfId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An account cannot be a parent of itself.");
        return parentId;
    }

    /// <summary>
    /// Performs a conservative, company-scoped lifecycle check. Any detected financial
    /// or master-data reference blocks delete/disable. The check is repeated inside the
    /// write transaction so the UI can never bypass the business rule through a race.
    /// </summary>
    public static AccountGuard CheckLifecycle(AccRec account)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("There is no connection to the current company database.");

        EnsureSchema();
        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        return CheckLifecycleOnConnection(connection, transaction, account);
    }

    /// <summary>
    /// Soft-deletes an account (recycle bin) only when it is completely unused and has no children.
    /// </summary>
    public static void Delete(AccRec account)
    {
        if (string.IsNullOrWhiteSpace(account.Id))
            throw new InvalidOperationException("The account is not eligible for deletion.");

        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("There is no connection to the current company database.");

        EnsureSchema();
        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        try
        {
            var companyGuid = ResolveCompanyGuid(connection, transaction);
            var guard = CheckLifecycleOnConnection(connection, transaction, account);
            if (!guard.CanDelete)
                throw new InvalidOperationException(string.Join(Environment.NewLine, guard.Reasons));

            if (HasChildrenOnConnection(connection, transaction, account.Id))
                throw new InvalidOperationException("The account cannot be deleted because it has child accounts. Delete the child accounts first.");

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE chart_of_accounts
                SET is_deleted={TrueLiteral}, deleted_at=CURRENT_TIMESTAMP, deleted_by=$deletedBy
                WHERE company_id=$company AND account_id=$id AND is_deleted={FalseLiteral};
                """;
            Add(command, "$company", CompanyValue(companyGuid));
            Add(command, "$id", AccountValue(account.Id));
            Add(command, "$deletedBy", AppState.CurrentUser?.Username);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The account could not be moved to the recycle bin because it no longer exists in the current company.");

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static AccountGuard CheckLifecycleOnConnection(DbConnection connection, DbTransaction transaction, AccRec account)
    {
        var reasons = new List<string>();

        // C-2/C-3 fix: the financial-activity test is computed LIVE from
        // posted journal lines for THIS account's Guid — never from stored
        // counters (the legacy accounts.balance/debit/credit columns were
        // never maintained, so the old guard saw 0 forever and let posted-to
        // accounts be deleted).
        var (postedDebit, postedCredit) = PostedTotalsOnConnection(connection, transaction, account.Id);
        if (postedDebit != 0 || postedCredit != 0 || account.Balance != 0 || account.Debit != 0 || account.Credit != 0)
            reasons.Add("The account has a balance or financial activity.");

        if (HasChildrenOnConnection(connection, transaction, account.Id))
            reasons.Add("The account has child accounts.");

        // Any journal reference at all (draft lines included) blocks
        // delete/disable: removing the account would orphan the entry.
        if (postedDebit == 0 && postedCredit == 0
            && JournalReferenceCountOnConnection(connection, transaction, account.Id) > 0)
            reasons.Add("The account is referenced by journal entries.");

        foreach (var reference in FindAccountReferences(connection, transaction, account))
        {
            if (!reasons.Any(x => string.Equals(x, reference, StringComparison.OrdinalIgnoreCase)))
                reasons.Add(reference);
        }

        var canDelete = reasons.Count == 0;
        return new AccountGuard(canDelete, canDelete, reasons);
    }

    private static (decimal Debit, decimal Credit) PostedTotalsOnConnection(DbConnection connection, DbTransaction transaction, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0)
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            WHERE l.account_id = $id AND j.status = 1;
            """;
        Add(command, "$id", AccountValue(accountId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (0m, 0m);
        return (Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    private static long JournalReferenceCountOnConnection(DbConnection connection, DbTransaction transaction, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM journal_entry_lines WHERE account_id = $id;";
        Add(command, "$id", AccountValue(accountId));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<string> FindAccountReferences(DbConnection connection, DbTransaction transaction, AccRec account)
    {
        var references = new List<string>();
        foreach (var table in GetReferenceTables(connection, transaction))
        {
            if (string.Equals(table.Name, "chart_of_accounts", StringComparison.OrdinalIgnoreCase))
                continue;
            // Journal lines are checked explicitly (and with a clearer
            // message) in CheckLifecycleOnConnection.
            if (string.Equals(table.Name, "journal_entry_lines", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var column in table.AccountColumns)
            {
                if (!ReferenceExists(connection, transaction, table, column, account))
                    continue;

                var tableName = table.Name.Replace('_', ' ');
                var columnName = column.Replace('_', ' ');
                var customerLike = table.Name.Contains("customer", StringComparison.OrdinalIgnoreCase)
                                   || table.Name.Contains("client", StringComparison.OrdinalIgnoreCase)
                                   || column.Contains("customer", StringComparison.OrdinalIgnoreCase)
                                   || column.Contains("client", StringComparison.OrdinalIgnoreCase);
                references.Add(customerLike
                    ? "The account is linked to a customer card. Remove the account link from the customer card first."
                    : $"The account is linked to data or operations in {tableName} ({columnName}).");
            }
        }

        return references;
    }

    private sealed record ReferenceTable(string Name, bool HasCompanyId, IReadOnlyList<string> AccountColumns);

    private static List<ReferenceTable> GetReferenceTables(DbConnection connection, DbTransaction transaction)
    {
        var columns = new List<(string Table, string Column)>();
        if (IsPg)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT table_name, column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name <> 'chart_of_accounts';
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add((reader.GetString(0), reader.GetString(1)));
        }
        else
        {
            var tableNames = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
                using var reader = command.ExecuteReader();
                while (reader.Read()) tableNames.Add(reader.GetString(0));
            }

            foreach (var table in tableNames)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA table_info(\"{EscapeIdentifier(table)}\");";
                using var reader = command.ExecuteReader();
                while (reader.Read()) columns.Add((table, reader.GetString(1)));
            }
        }

        return columns
            .GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.Select(x => x.Column).ToList();
                var customerLikeTable = g.Key.Contains("customer", StringComparison.OrdinalIgnoreCase)
                                        || g.Key.Contains("client", StringComparison.OrdinalIgnoreCase);
                var accountColumns = list.Where(x => IsAccountReferenceColumn(x)
                                                     || (customerLikeTable && x.Contains("account", StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new ReferenceTable(g.Key,
                    list.Any(x => string.Equals(x, "company_id", StringComparison.OrdinalIgnoreCase)),
                    accountColumns);
            })
            .Where(x => x.AccountColumns.Count > 0)
            .ToList();
    }

    private static bool IsAccountReferenceColumn(string column)
    {
        var name = column.Trim().ToLowerInvariant();
        if (name is "account_id" or "account_code") return true;
        if (name.EndsWith("_account_id", StringComparison.Ordinal)) return true;
        if (name.EndsWith("_account_code", StringComparison.Ordinal)) return true;
        if (name.Contains("customer_account", StringComparison.Ordinal)) return true;
        if (name.Contains("client_account", StringComparison.Ordinal)) return true;
        if (name.Contains("ledger_account", StringComparison.Ordinal)) return true;
        if (name.Contains("receivable_account", StringComparison.Ordinal)) return true;
        if (name.Contains("payable_account", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool ReferenceExists(DbConnection connection, DbTransaction transaction, ReferenceTable table, string column, AccRec account)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var tableSql = QuoteIdentifier(table.Name);
        var columnSql = QuoteIdentifier(column);
        // Company predicate compares as TEXT against BOTH the resolved Guid
        // and the shell code, so Guid-keyed tables (journal family) and
        // code-keyed tables (documents family) are both scoped correctly on
        // either provider — the pre-unification code bound the integer
        // accounts id / the code indiscriminately, which is exactly why the
        // journal reference was invisible (C-3).
        var companyPredicate = table.HasCompanyId
            ? $" AND CAST({QuoteIdentifier("company_id")} AS TEXT) IN ($companyGuid, $companyCode)"
            : string.Empty;

        var isId = column.EndsWith("_id", StringComparison.OrdinalIgnoreCase);
        command.CommandText = isId
            ? $"SELECT COUNT(1) FROM {tableSql} WHERE {columnSql} IS NOT NULL AND CAST({columnSql} AS TEXT)=CAST($value AS TEXT){companyPredicate};"
            : $"SELECT COUNT(1) FROM {tableSql} WHERE {columnSql}=$value{companyPredicate};";

        Add(command, "$value", isId ? account.Id : account.Code);
        if (table.HasCompanyId)
        {
            var companyGuid = ResolveCompanyGuid(connection, transaction);
            Add(command, "$companyGuid", companyGuid);
            Add(command, "$companyCode", AppState.CurrentCompanyCode ?? string.Empty);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static string QuoteIdentifier(string identifier) => $"\"{EscapeIdentifier(identifier)}\"";

    private static string EscapeIdentifier(string identifier) => identifier.Replace("\"", "\"\"", StringComparison.Ordinal);

    public static bool HasChildren(string code)
    {
        var normalized = RequireCode(code);
        return LoadAll().Any(x => string.Equals(x.ParentCode, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasChildrenOnConnection(DbConnection connection, DbTransaction transaction, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(1) FROM chart_of_accounts WHERE parent_account_id=$parent AND is_deleted={FalseLiteral};";
        Add(command, "$parent", AccountValue(accountId));
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static void RenumberDescendants(DbConnection connection, DbTransaction transaction, string companyGuid, string oldPrefix, string newPrefix)
    {
        var rows = new List<(string Id, string Code, string? ParentCode)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"""
                SELECT CAST(a.account_id AS TEXT), a.account_code, p.account_code
                FROM chart_of_accounts a
                LEFT JOIN chart_of_accounts p ON p.account_id = a.parent_account_id
                WHERE a.company_id=$company AND a.is_deleted={FalseLiteral};
                """;
            Add(select, "$company", CompanyValue(companyGuid));
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var byParent = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.ParentCode))
            .GroupBy(x => x.ParentCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var descendants = new List<(string Id, string Code, string? ParentCode)>();
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(oldPrefix);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!byParent.TryGetValue(parent, out var children))
                continue;

            foreach (var child in children)
            {
                if (!visited.Add(child.Id))
                    throw new InvalidOperationException("Accounts could not be renumbered because there is a cycle in the account tree.");

                if (!child.Code.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The child account {child.Code} does not follow the parent account structure {oldPrefix}.");

                descendants.Add(child);
                queue.Enqueue(child.Code);
            }
        }

        if (descendants.Count == 0)
            return;

        var mapping = descendants
            .Select(x => (x.Id, Old: x.Code, New: newPrefix + x.Code[oldPrefix.Length..], x.ParentCode))
            .ToList();

        var targetCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mapping)
        {
            if (!targetCodes.Add(item.New))
                throw new InvalidOperationException("Renumbering the child accounts would produce duplicate numbers.");
        }

        foreach (var item in mapping)
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = $"SELECT COUNT(1) FROM chart_of_accounts WHERE company_id=$company AND account_code=$code AND account_id<>$id AND is_deleted={FalseLiteral};";
            Add(check, "$company", CompanyValue(companyGuid));
            Add(check, "$code", item.New);
            Add(check, "$id", AccountValue(item.Id));
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException($"Cannot renumber because the number {item.New} is already in use.");
        }

        // Two-phase rename through temporary codes so intermediate states
        // can never collide with the UNIQUE(company_id, account_code) index.
        // Parent links are Guids and are deliberately untouched.
        foreach (var item in mapping)
        {
            using var temp = connection.CreateCommand();
            temp.Transaction = transaction;
            temp.CommandText = "UPDATE chart_of_accounts SET account_code=$code, updated_at=CURRENT_TIMESTAMP WHERE company_id=$company AND account_id=$id;";
            Add(temp, "$company", CompanyValue(companyGuid));
            Add(temp, "$code", "__REN__" + item.Id);
            Add(temp, "$id", AccountValue(item.Id));
            temp.ExecuteNonQuery();
        }

        foreach (var item in mapping)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE chart_of_accounts SET account_code=$new, account_type=$type, updated_at=CURRENT_TIMESTAMP WHERE company_id=$company AND account_id=$id;";
            Add(update, "$company", CompanyValue(companyGuid));
            Add(update, "$new", item.New);
            Add(update, "$type", AccountTypeForCode(item.New));
            Add(update, "$id", AccountValue(item.Id));
            update.ExecuteNonQuery();
        }
    }

    private static string InsertSql() => $"""
        INSERT INTO chart_of_accounts(account_id,company_id,account_code,account_name,name_ar,account_type,
            parent_account_id,is_postable,is_active,is_deleted,risk,cost_center_mode,
            allow_reconcile,taxable,foreign_currency,cost_required,budget,is_default,tax_rate,description)
        VALUES($id,$company,$code,$en,$ar,$type,$parent,$postable,$active,{FalseLiteral},$risk,$cc,$reconcile,$taxable,$foreign,$costreq,$budget,$default,$tax,$desc);
        """;

    private static string UpdateSql() => $"""
        UPDATE chart_of_accounts SET account_code=$code,account_name=$en,name_ar=$ar,account_type=$type,
            parent_account_id=$parent,is_postable=$postable,is_active=$active,risk=$risk,cost_center_mode=$cc,
            allow_reconcile=$reconcile,taxable=$taxable,foreign_currency=$foreign,cost_required=$costreq,
            budget=$budget,is_default=$default,tax_rate=$tax,description=$desc,updated_at=CURRENT_TIMESTAMP
        WHERE company_id=$company AND account_id=$id AND is_deleted={FalseLiteral};
        """;

    private static void Bind(DbCommand command, AccRec value, string id, string companyGuid, string? parentId)
    {
        Add(command, "$id", AccountValue(id));
        Add(command, "$company", CompanyValue(companyGuid));
        Add(command, "$code", value.Code);
        Add(command, "$en", string.IsNullOrWhiteSpace(value.NameEn) ? value.Name : value.NameEn);
        Add(command, "$ar", value.Name);
        Add(command, "$type", AccountTypeForCode(value.Code));
        Add(command, "$parent", parentId is null ? null : AccountValue(parentId));
        Add(command, "$postable", !string.Equals(value.PostingMode, "Summary", StringComparison.OrdinalIgnoreCase));
        Add(command, "$active", !string.Equals(value.Status, "Disabled", StringComparison.OrdinalIgnoreCase));
        Add(command, "$risk", value.Risk);
        Add(command, "$cc", value.CostCenter);
        Add(command, "$reconcile", value.Reconcile);
        Add(command, "$taxable", value.Taxable);
        Add(command, "$foreign", value.ForeignCurrency);
        Add(command, "$costreq", value.CostRequired);
        Add(command, "$budget", value.Budget);
        Add(command, "$default", value.IsDefault);
        Add(command, "$tax", value.TaxRate);
        Add(command, "$desc", value.Description);
    }

    private static bool CodeExistsOnConnection(DbConnection connection, DbTransaction transaction, string companyGuid, string code, string? excludeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(1) FROM chart_of_accounts WHERE company_id=$company AND account_code=$code AND is_deleted={FalseLiteral}" +
                              (!string.IsNullOrWhiteSpace(excludeId) ? " AND account_id<>$id" : "") + ";";
        Add(command, "$company", CompanyValue(companyGuid));
        Add(command, "$code", code);
        if (!string.IsNullOrWhiteSpace(excludeId)) Add(command, "$id", AccountValue(excludeId!));
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static AccRec Read(DbDataReader reader)
    {
        var debit = Convert.ToDecimal(reader.GetValue(17), System.Globalization.CultureInfo.InvariantCulture);
        var credit = Convert.ToDecimal(reader.GetValue(18), System.Globalization.CultureInfo.InvariantCulture);
        return new AccRec(
            Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            Convert.ToBoolean(reader.GetValue(9), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(10), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(11), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(12), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(13), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(14), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToDecimal(reader.GetValue(15), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
            debit - credit, debit, credit);
    }

    private static DbConnection Open(string connectionString)
    {
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private static string RequireCode(string code)
    {
        var value = code.Trim();
        if (value.Length == 0 || value.Any(c => !char.IsDigit(c)))
            throw new ArgumentException("The account number must consist of digits only.", nameof(code));
        return value;
    }

    private static long ParsePositive(string value) => long.TryParse(value, out var number) ? number : 0;
}

}
