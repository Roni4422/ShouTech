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
/// Provider-specific SQL is isolated here so the UI remains database-neutral.
/// </summary>
internal static class AccRepo
{
    internal sealed record AccRec(
        long Id,
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


    public static void EnsureSchema()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("No connection to the current company database.");
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'accounts';"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'accounts';";
        if (Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("accounts schema is missing. Apply the provider migrations under dba/mig.");
    }


    public static List<AccRec> LoadAll()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return new();
        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id, account_code, parent_code, name_ar, name_en,
                   posting_mode, status, risk, cost_center, allow_reconcile,
                   taxable, foreign_currency, cost_required, budget, is_default,
                   tax_rate, description, balance, debit, credit
            FROM accounts
            WHERE company_id = $company AND is_deleted = 0
            ORDER BY account_code;
            """;
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        using var reader = command.ExecuteReader();
        var result = new List<AccRec>();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    public static AccRec? FindById(long id)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id, account_code, parent_code, name_ar, name_en,
                   posting_mode, status, risk, cost_center, allow_reconcile,
                   taxable, foreign_currency, cost_required, budget, is_default,
                   tax_rate, description, balance, debit, credit
            FROM accounts
            WHERE company_id = $company AND account_id = $id AND is_deleted = 0;
            """;
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$id", id);
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

    public static bool CodeExists(string code, long? excludeId = null)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return false;
        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM accounts WHERE company_id=$company AND account_code=$code AND is_deleted=0" +
                              (excludeId.HasValue ? " AND account_id<>$id" : "") + ";";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$code", RequireCode(code));
        if (excludeId.HasValue) Add(command, "$id", excludeId.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public static long Save(AccRec? current, AccRec value, bool renumberDescendants)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("لا يوجد اتصال بقاعدة بيانات الشركة الحالية.");
        EnsureSchema();
        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        try
        {
            if (current is not null
                && !string.Equals(current.Status, value.Status, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Status, "معطل", StringComparison.OrdinalIgnoreCase))
            {
                var guard = CheckLifecycleOnConnection(connection, transaction, current);
                if (!guard.CanDisable)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, guard.Reasons));
            }

            if (current is null)
            {
                if (CodeExistsOnConnection(connection, transaction, value.Code, null))
                    throw new InvalidOperationException("رقم الحساب مستخدم مسبقًا.");
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = InsertSql();
                Bind(insert, value, null);
                insert.ExecuteNonQuery();
                var id = GetId(connection, transaction, value.Code);
                transaction.Commit();
                return id;
            }

            if (!string.Equals(current.Code, value.Code, StringComparison.OrdinalIgnoreCase)
                && CodeExistsOnConnection(connection, transaction, value.Code, current.Id))
                throw new InvalidOperationException("رقم الحساب الجديد مستخدم مسبقًا.");

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = UpdateSql();
            Bind(update, value, current.Id);
            update.ExecuteNonQuery();

            if (!string.Equals(current.Code, value.Code, StringComparison.OrdinalIgnoreCase) && HasChildrenOnConnection(connection, transaction, current.Code))
            {
                if (renumberDescendants)
                    RenumberDescendants(connection, transaction, current.Code, value.Code);
                else
                    ReparentDescendants(connection, transaction, current.Code, value.Code);
            }

            transaction.Commit();
            return current.Id;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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
            throw new InvalidOperationException("لا يوجد اتصال بقاعدة بيانات الشركة الحالية.");

        EnsureSchema();
        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        return CheckLifecycleOnConnection(connection, transaction, account);
    }

    /// <summary>
    /// Permanently deletes an account only when it is completely unused and has no children.
    /// </summary>
    public static void Delete(AccRec account)
    {
        if (account.Id <= 0)
            throw new InvalidOperationException("الحساب غير صالح للحذف.");

        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("لا يوجد اتصال بقاعدة بيانات الشركة الحالية.");

        EnsureSchema();
        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        try
        {
            var guard = CheckLifecycleOnConnection(connection, transaction, account);
            if (!guard.CanDelete)
                throw new InvalidOperationException(string.Join(Environment.NewLine, guard.Reasons));

            if (HasChildrenOnConnection(connection, transaction, account.Code))
                throw new InvalidOperationException("لا يمكن حذف الحساب لأنه يحتوي على حسابات فرعية. احذف الحسابات الفرعية أولًا.");

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var deletedValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
            var activeValue = DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0";
            command.CommandText = $"""
                UPDATE accounts
                SET is_deleted={deletedValue}, deleted_at=CURRENT_TIMESTAMP, deleted_by=$deletedBy
                WHERE company_id=$company AND account_id=$id AND is_deleted={activeValue};
                """;
            Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
            Add(command, "$id", account.Id);
            Add(command, "$deletedBy", AppState.CurrentUser?.Username);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("تعذر نقل الحساب إلى سلة المحذوفات لأنه لم يعد موجودًا في الشركة الحالية.");

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

        if (account.Balance != 0 || account.Debit != 0 || account.Credit != 0)
            reasons.Add("الحساب يحتوي على رصيد أو حركة مالية.");

        if (HasChildrenOnConnection(connection, transaction, account.Code))
            reasons.Add("الحساب يحتوي على حسابات فرعية.");

        foreach (var reference in FindAccountReferences(connection, transaction, account))
        {
            if (!reasons.Any(x => string.Equals(x, reference, StringComparison.OrdinalIgnoreCase)))
                reasons.Add(reference);
        }

        var canDelete = reasons.Count == 0;
        return new AccountGuard(canDelete, canDelete, reasons);
    }

    private static List<string> FindAccountReferences(DbConnection connection, DbTransaction transaction, AccRec account)
    {
        var references = new List<string>();
        foreach (var table in GetReferenceTables(connection, transaction))
        {
            if (string.Equals(table.Name, "accounts", StringComparison.OrdinalIgnoreCase))
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
                    ? "الحساب مرتبط ببطاقة عميل. يجب إزالة ارتباط الحساب من بطاقة العميل أولًا."
                    : $"الحساب مرتبط ببيانات أو عمليات في {tableName} ({columnName}).");
            }
        }

        return references;
    }

    private sealed record ReferenceTable(string Name, bool HasCompanyId, IReadOnlyList<string> AccountColumns);

    private static List<ReferenceTable> GetReferenceTables(DbConnection connection, DbTransaction transaction)
    {
        var columns = new List<(string Table, string Column)>();
        if (DatabaseRuntime.IsPostgreSql())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT table_name, column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                  AND table_name <> 'accounts';
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
        var companyPredicate = table.HasCompanyId ? $" AND {QuoteIdentifier("company_id")}=$company" : string.Empty;

        var isId = column.EndsWith("_id", StringComparison.OrdinalIgnoreCase);
        command.CommandText = isId
            ? $"SELECT COUNT(1) FROM {tableSql} WHERE {columnSql} IS NOT NULL AND CAST({columnSql} AS TEXT)=CAST($value AS TEXT){companyPredicate};"
            : $"SELECT COUNT(1) FROM {tableSql} WHERE {columnSql}=$value{companyPredicate};";

        Add(command, "$value", isId ? account.Id : account.Code);
        if (table.HasCompanyId)
            Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static string QuoteIdentifier(string identifier) => $"\"{EscapeIdentifier(identifier)}\"";

    private static string EscapeIdentifier(string identifier) => identifier.Replace("\"", "\"\"", StringComparison.Ordinal);

    public static bool HasChildren(string code)
    {
        var normalized = RequireCode(code);
        return LoadAll().Any(x => string.Equals(x.ParentCode, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasChildrenOnConnection(DbConnection connection, DbTransaction transaction, string code)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM accounts WHERE company_id=$company AND parent_code=$parent AND is_deleted=0;";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$parent", code);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static void ReparentDescendants(DbConnection connection, DbTransaction transaction, string oldParent, string newParent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE accounts SET parent_code=$newparent, updated_at=CURRENT_TIMESTAMP WHERE company_id=$company AND parent_code=$oldparent AND is_deleted=0;";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$oldparent", oldParent);
        Add(command, "$newparent", newParent);
        command.ExecuteNonQuery();
    }

    private static void RenumberDescendants(DbConnection connection, DbTransaction transaction, string oldPrefix, string newPrefix)
    {
        var rows = new List<(long Id, string Code, string? ParentCode)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT account_id, account_code, parent_code FROM accounts WHERE company_id=$company AND is_deleted=0;";
            Add(select, "$company", AppState.CurrentCompanyCode ?? string.Empty);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var byParent = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.ParentCode))
            .GroupBy(x => x.ParentCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var descendants = new List<(long Id, string Code, string? ParentCode)>();
        var visited = new HashSet<long>();
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
                    throw new InvalidOperationException("تعذر إعادة ترقيم الحسابات بسبب وجود دورة في شجرة الحسابات.");

                if (!child.Code.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"الحساب التابع {child.Code} لا يتبع بنية الحساب الأب {oldPrefix}.");

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
                throw new InvalidOperationException("إعادة ترقيم الحسابات التابعة ستنتج أرقامًا مكررة.");
        }

        foreach (var item in mapping)
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(1) FROM accounts WHERE company_id=$company AND account_code=$code AND account_id<>$id AND is_deleted=0;";
            Add(check, "$company", AppState.CurrentCompanyCode ?? string.Empty);
            Add(check, "$code", item.New);
            Add(check, "$id", item.Id);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException($"لا يمكن إعادة الترقيم لأن الرقم {item.New} مستخدم بالفعل.");
        }

        foreach (var item in mapping)
        {
            using var temp = connection.CreateCommand();
            temp.Transaction = transaction;
            temp.CommandText = "UPDATE accounts SET account_code=$code, updated_at=CURRENT_TIMESTAMP WHERE company_id=$company AND account_id=$id;";
            Add(temp, "$company", AppState.CurrentCompanyCode ?? string.Empty);
            Add(temp, "$code", "__REN__" + item.Id.ToString(CultureInfo.InvariantCulture));
            Add(temp, "$id", item.Id);
            temp.ExecuteNonQuery();
        }

        var codeMap = mapping.ToDictionary(x => x.Old, x => x.New, StringComparer.OrdinalIgnoreCase);
        foreach (var item in mapping)
        {
            var newParent = item.ParentCode != null && codeMap.TryGetValue(item.ParentCode, out var mappedParent)
                ? mappedParent
                : newPrefix;

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE accounts SET account_code=$new, parent_code=$parent, updated_at=CURRENT_TIMESTAMP WHERE company_id=$company AND account_id=$id;";
            Add(update, "$company", AppState.CurrentCompanyCode ?? string.Empty);
            Add(update, "$new", item.New);
            Add(update, "$parent", newParent);
            Add(update, "$id", item.Id);
            update.ExecuteNonQuery();
        }
    }

    private static string InsertSql() => """
        INSERT INTO accounts(company_id,account_code,parent_code,name_ar,name_en,posting_mode,status,risk,cost_center,
            allow_reconcile,taxable,foreign_currency,cost_required,budget,is_default,tax_rate,description,balance,debit,credit)
        VALUES($company,$code,$parent,$ar,$en,$posting,$status,$risk,$cc,$reconcile,$taxable,$foreign,$costreq,$budget,$default,$tax,$desc,$balance,$debit,$credit);
        """;

    private static string UpdateSql() => """
        UPDATE accounts SET account_code=$code,parent_code=$parent,name_ar=$ar,name_en=$en,posting_mode=$posting,
            status=$status,risk=$risk,cost_center=$cc,allow_reconcile=$reconcile,taxable=$taxable,
            foreign_currency=$foreign,cost_required=$costreq,budget=$budget,is_default=$default,tax_rate=$tax,
            description=$desc,balance=$balance,debit=$debit,credit=$credit,updated_at=CURRENT_TIMESTAMP
        WHERE company_id=$company AND account_id=$id AND is_deleted=0;
        """;

    private static void Bind(DbCommand command, AccRec value, long? id)
    {
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$code", value.Code);
        Add(command, "$parent", (object?)value.ParentCode ?? DBNull.Value);
        Add(command, "$ar", value.Name);
        Add(command, "$en", value.NameEn);
        Add(command, "$posting", value.PostingMode);
        Add(command, "$status", value.Status);
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
        Add(command, "$balance", value.Balance);
        Add(command, "$debit", value.Debit);
        Add(command, "$credit", value.Credit);
        if (id.HasValue) Add(command, "$id", id.Value);
    }

    private static long GetId(DbConnection connection, DbTransaction transaction, string code)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT account_id FROM accounts WHERE company_id=$company AND account_code=$code LIMIT 1;";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$code", code);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool CodeExistsOnConnection(DbConnection connection, DbTransaction transaction, string code, long? excludeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(1) FROM accounts WHERE company_id=$company AND account_code=$code AND is_deleted=0" +
                              (excludeId.HasValue ? " AND account_id<>$id" : "") + ";";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$code", code);
        if (excludeId.HasValue) Add(command, "$id", excludeId.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static AccRec Read(DbDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
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
        Convert.ToDecimal(reader.GetValue(17), System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToDecimal(reader.GetValue(18), System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToDecimal(reader.GetValue(19), System.Globalization.CultureInfo.InvariantCulture));

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
            throw new ArgumentException("رقم الحساب يجب أن يتكون من أرقام فقط.", nameof(code));
        return value;
    }

    private static long ParsePositive(string value) => long.TryParse(value, out var number) ? number : 0;
}

}
