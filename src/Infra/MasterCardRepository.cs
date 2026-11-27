using System.Data.Common;
using ShouTech.Domain;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infra;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral MasterCard persistence (SQLite + PostgreSQL), following the
/// ProjectRepository patterns: parameterized SQL only, company scoping on every
/// statement, soft deletes, and single-transaction writes with an
/// in-transaction uniqueness re-check and rollback on failure.
///
/// Ledger bridges (balances / delete guards):
/// - Cost centers are referenced by code in journal_entry_lines.cost_center;
///   only posted journal entries (status = Posted) count toward balances.
/// - Bank balances come from the bank's GL account code, resolved through
///   chart_of_accounts to journal_entry_lines.account_id (posted only).
/// - party_ledger.party_id is THIS party's ledger_id (migration 061): a
///   party resolves to exactly one ledger id through (company, kind, code)
///   against the canonical parties store — never through the code parsed
///   as a number and never through a second party master (sweep C-1/C-2).
/// </summary>
public sealed class MasterCardRepository : IMasterCardRepository
{
    private const int ListLimit = 1000;

    private readonly string _provider;
    private readonly string _connectionString;

    public MasterCardRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string DeletedValue => _provider == "PostgreSQL" ? "true" : "1";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";

    // ------------------------------------------------------------ Cost centers

    public async Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, parent_id, is_group, level, is_active, notes FROM cost_centers WHERE company_id = @company AND {NotDeleted} ORDER BY code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<CostCenter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapCostCenter(reader));
        return result;
    }

    public async Task<CostCenter?> GetCostCenterByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code))
            return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, parent_id, is_group, level, is_active, notes FROM cost_centers WHERE company_id = @company AND code = @code AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapCostCenter(reader) : null;
    }

    public async Task<CostCenter?> GetCostCenterByIdAsync(string companyId, Guid id, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, parent_id, is_group, level, is_active, notes FROM cost_centers WHERE company_id = @company AND id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapCostCenter(reader) : null;
    }

    public async Task SaveCostCenterAsync(string companyId, CostCenter costCenter, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(costCenter);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCodeUniqueAsync(connection, transaction, "cost_centers", companyId, costCenter.Code, null, costCenter.Id, "Cost center", cancellationToken).ConfigureAwait(false);
            var level = await ComputeCostCenterLevelAsync(connection, transaction, companyId, costCenter, cancellationToken).ConfigureAwait(false);
            costCenter.Level = level;

            var exists = await ExistsByIdAsync(connection, transaction, "cost_centers", companyId, costCenter.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO cost_centers (id, company_id, code, name_en, name_ar, parent_id, is_group, level, is_active, notes, created_by) VALUES (@id, @company, @code, @nameEn, @nameAr, @parent, @isGroup, @level, @isActive, @notes, @user)");
                BindCostCenter(insert, companyId, costCenter, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE cost_centers SET code = @code, name_en = @nameEn, name_ar = @nameAr, parent_id = @parent, is_group = @isGroup, level = @level, is_active = @isActive, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindCostCenter(update, companyId, costCenter, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Cost center could not be updated (missing or deleted).");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteCostCenterAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("cost_centers", "Cost center", companyId, id, userId, cancellationToken);

    public async Task<int> CountCostCenterJournalRefsAsync(string companyId, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code))
            return 0;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await CountCostCenterJournalRefsAsync(connection, null, companyId, code, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LedgerBalance> GetCostCenterBalanceAsync(string companyId, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var balance = new LedgerBalance();
        if (string.IsNullOrWhiteSpace(code))
            return balance;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id WHERE e.company_id = @company AND e.status = @posted AND l.cost_center = @code");
        Add(command, "@company", companyId.Trim());
        Add(command, "@posted", (int)JournalEntryStatus.Posted);
        Add(command, "@code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            balance.Debit = ReadDecimal(reader, 0);
            balance.Credit = ReadDecimal(reader, 1);
        }
        return balance;
    }

    public async Task<IReadOnlyDictionary<string, LedgerBalance>> GetCostCenterBalancesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var result = new Dictionary<string, LedgerBalance>(StringComparer.OrdinalIgnoreCase);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT l.cost_center, COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id WHERE e.company_id = @company AND e.status = @posted AND l.cost_center IS NOT NULL AND l.cost_center <> '' GROUP BY l.cost_center");
        Add(command, "@company", companyId.Trim());
        Add(command, "@posted", (int)JournalEntryStatus.Posted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var code = ReadString(reader, 0);
            if (string.IsNullOrWhiteSpace(code))
                continue;
            result[code] = new LedgerBalance { Debit = ReadDecimal(reader, 1), Credit = ReadDecimal(reader, 2) };
        }
        return result;
    }

    /// <summary>
    /// Validates the parent chain inside the write transaction (parent exists,
    /// belongs to the company, is a group, and creates no cycle) and derives
    /// the stored level: roots are level 1, each child is parent level + 1.
    /// </summary>
    private async Task<int> ComputeCostCenterLevelAsync(DbConnection connection, DbTransaction transaction, string companyId, CostCenter costCenter, CancellationToken cancellationToken)
    {
        if (!costCenter.ParentId.HasValue)
            return 1;
        if (costCenter.ParentId.Value == costCenter.Id)
            throw new InvalidOperationException("A cost center cannot be its own parent.");

        var level = 1;
        var current = costCenter.ParentId;
        var first = true;
        while (current.HasValue)
        {
            if (current.Value == costCenter.Id)
                throw new InvalidOperationException("The cost-center parent chain contains a cycle.");
            level++;
            if (level > 64)
                throw new InvalidOperationException("The cost-center tree is too deep.");

            Guid? parentOfCurrent;
            bool isGroup;
            await using (var cmd = CreateCommand(connection, transaction,
                $"SELECT parent_id, is_group FROM cost_centers WHERE company_id = @company AND id = @id AND {NotDeleted}"))
            {
                Add(cmd, "@company", companyId.Trim());
                Add(cmd, "@id", FormatGuid(current.Value));
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The parent cost center was not found for this company.");
                parentOfCurrent = ReadNullableGuid(reader, 0);
                isGroup = ReadBool(reader, 1);
            }
            if (first && !isGroup)
                throw new InvalidOperationException("Only a group cost center can be a parent.");
            first = false;
            current = parentOfCurrent;
        }
        return level;
    }

    private async Task<int> CountCostCenterJournalRefsAsync(DbConnection connection, DbTransaction? transaction, string companyId, string code, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT COUNT(*) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id WHERE e.company_id = @company AND l.cost_center = @code");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", code.Trim());
        return (int)Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private void BindCostCenter(DbCommand command, string companyId, CostCenter costCenter, string userId)
    {
        Add(command, "@id", FormatGuid(costCenter.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", costCenter.Code.Trim());
        Add(command, "@nameEn", costCenter.NameEn.Trim());
        Add(command, "@nameAr", (object?)NullIfBlank(costCenter.NameAr) ?? DBNull.Value);
        Add(command, "@parent", (object?)FormatNullableGuid(costCenter.ParentId) ?? DBNull.Value);
        Add(command, "@isGroup", DbBool(costCenter.IsGroup));
        Add(command, "@level", costCenter.Level);
        Add(command, "@isActive", DbBool(costCenter.IsActive));
        Add(command, "@notes", (object?)NullIfBlank(costCenter.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private static CostCenter MapCostCenter(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Code = ReadString(reader, 1) ?? string.Empty,
        NameEn = ReadString(reader, 2) ?? string.Empty,
        NameAr = ReadString(reader, 3),
        ParentId = ReadNullableGuid(reader, 4),
        IsGroup = ReadBool(reader, 5),
        Level = ReadInt(reader, 6),
        IsActive = ReadBool(reader, 7),
        Notes = ReadString(reader, 8)
    };

    // ------------------------------------------------- Cost center allocations

    public async Task<IReadOnlyList<CostCenterAllocation>> GetAllocationsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CostCenterAllocation>();
        await using (var command = CreateCommand(connection, null,
            $"SELECT id, main_cost_center_id, valid_from, is_active FROM cost_center_allocations WHERE company_id = @company AND {NotDeleted} ORDER BY valid_from DESC LIMIT {ListLimit}"))
        {
            Add(command, "@company", companyId.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new CostCenterAllocation
                {
                    Id = ReadGuid(reader, 0),
                    MainCostCenterId = ReadGuid(reader, 1),
                    ValidFrom = ReadDate(reader, 2) ?? DateOnly.FromDateTime(DateTime.Today),
                    IsActive = ReadBool(reader, 3)
                });
            }
        }
        foreach (var allocation in result)
        {
            await using var lines = CreateCommand(connection, null,
                "SELECT id, allocation_id, cost_center_id, percentage FROM cost_center_allocation_lines WHERE allocation_id = @allocation ORDER BY percentage DESC");
            Add(lines, "@allocation", FormatGuid(allocation.Id));
            await using var reader = await lines.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                allocation.Lines.Add(new CostCenterAllocationLine
                {
                    Id = ReadGuid(reader, 0),
                    AllocationId = ReadGuid(reader, 1),
                    CostCenterId = ReadGuid(reader, 2),
                    Percentage = ReadDecimal(reader, 3)
                });
            }
        }
        return result;
    }

    public async Task SaveAllocationAsync(string companyId, CostCenterAllocation allocation, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(allocation);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await ExistsByIdAsync(connection, transaction, "cost_centers", companyId, allocation.MainCostCenterId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The allocation's main cost center was not found for this company.");
            foreach (var line in allocation.Lines)
            {
                if (!await ExistsByIdAsync(connection, transaction, "cost_centers", companyId, line.CostCenterId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("An allocation line references a cost center that was not found for this company.");
            }

            var exists = await ExistsByIdAsync(connection, transaction, "cost_center_allocations", companyId, allocation.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO cost_center_allocations (id, company_id, main_cost_center_id, valid_from, is_active, created_by) VALUES (@id, @company, @main, @validFrom, @isActive, @user)");
                Add(insert, "@id", FormatGuid(allocation.Id));
                Add(insert, "@company", companyId.Trim());
                Add(insert, "@main", FormatGuid(allocation.MainCostCenterId));
                Add(insert, "@validFrom", FormatDate(allocation.ValidFrom));
                Add(insert, "@isActive", DbBool(allocation.IsActive));
                Add(insert, "@user", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE cost_center_allocations SET main_cost_center_id = @main, valid_from = @validFrom, is_active = @isActive, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                Add(update, "@id", FormatGuid(allocation.Id));
                Add(update, "@company", companyId.Trim());
                Add(update, "@main", FormatGuid(allocation.MainCostCenterId));
                Add(update, "@validFrom", FormatDate(allocation.ValidFrom));
                Add(update, "@isActive", DbBool(allocation.IsActive));
                Add(update, "@user", userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Allocation could not be updated (missing or deleted).");
            }

            // Lines are child rows without their own lifecycle: replace them
            // wholesale inside the same transaction.
            await using (var clear = CreateCommand(connection, transaction,
                "DELETE FROM cost_center_allocation_lines WHERE allocation_id = @allocation"))
            {
                Add(clear, "@allocation", FormatGuid(allocation.Id));
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var line in allocation.Lines)
            {
                if (line.Id == Guid.Empty)
                    line.Id = Guid.NewGuid();
                line.AllocationId = allocation.Id;
                await using var insertLine = CreateCommand(connection, transaction,
                    "INSERT INTO cost_center_allocation_lines (id, allocation_id, cost_center_id, percentage) VALUES (@id, @allocation, @center, @pct)");
                Add(insertLine, "@id", FormatGuid(line.Id));
                Add(insertLine, "@allocation", FormatGuid(allocation.Id));
                Add(insertLine, "@center", FormatGuid(line.CostCenterId));
                Add(insertLine, "@pct", line.Percentage);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteAllocationAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("cost_center_allocations", "Allocation", companyId, id, userId, cancellationToken);

    // ------------------------------------------------------------------- Banks

    public async Task<IReadOnlyList<Bank>> GetBanksAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, branch_name, account_no, iban, currency_code, gl_account_code, is_active, notes FROM banks WHERE company_id = @company AND {NotDeleted} ORDER BY code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<Bank>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapBank(reader));
        return result;
    }

    public async Task<Bank?> GetBankByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code))
            return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, branch_name, account_no, iban, currency_code, gl_account_code, is_active, notes FROM banks WHERE company_id = @company AND code = @code AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBank(reader) : null;
    }

    public async Task SaveBankAsync(string companyId, Bank bank, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(bank);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCodeUniqueAsync(connection, transaction, "banks", companyId, bank.Code, null, bank.Id, "Bank", cancellationToken).ConfigureAwait(false);
            var exists = await ExistsByIdAsync(connection, transaction, "banks", companyId, bank.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO banks (id, company_id, code, name_en, name_ar, branch_name, account_no, iban, currency_code, gl_account_code, is_active, notes, created_by) VALUES (@id, @company, @code, @nameEn, @nameAr, @branch, @accountNo, @iban, @currency, @gl, @isActive, @notes, @user)");
                BindBank(insert, companyId, bank, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE banks SET code = @code, name_en = @nameEn, name_ar = @nameAr, branch_name = @branch, account_no = @accountNo, iban = @iban, currency_code = @currency, gl_account_code = @gl, is_active = @isActive, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindBank(update, companyId, bank, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Bank could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteBankAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("banks", "Bank", companyId, id, userId, cancellationToken);

    public async Task<LedgerBalance> GetBankBalanceAsync(string companyId, string glAccountCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var balance = new LedgerBalance();
        if (string.IsNullOrWhiteSpace(glAccountCode))
            return balance;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT COALESCE(SUM(l.debit), 0), COALESCE(SUM(l.credit), 0) FROM journal_entry_lines l JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = e.company_id WHERE e.company_id = @company AND e.status = @posted AND a.account_code = @gl");
        Add(command, "@company", companyId.Trim());
        Add(command, "@posted", (int)JournalEntryStatus.Posted);
        Add(command, "@gl", glAccountCode.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            balance.Debit = ReadDecimal(reader, 0);
            balance.Credit = ReadDecimal(reader, 1);
        }
        return balance;
    }

    private void BindBank(DbCommand command, string companyId, Bank bank, string userId)
    {
        Add(command, "@id", FormatGuid(bank.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", bank.Code.Trim());
        Add(command, "@nameEn", bank.NameEn.Trim());
        Add(command, "@nameAr", (object?)NullIfBlank(bank.NameAr) ?? DBNull.Value);
        Add(command, "@branch", (object?)NullIfBlank(bank.BranchName) ?? DBNull.Value);
        Add(command, "@accountNo", (object?)NullIfBlank(bank.AccountNo) ?? DBNull.Value);
        Add(command, "@iban", (object?)NullIfBlank(bank.Iban) ?? DBNull.Value);
        Add(command, "@currency", string.IsNullOrWhiteSpace(bank.CurrencyCode) ? "SAR" : bank.CurrencyCode.Trim().ToUpperInvariant());
        Add(command, "@gl", (object?)NullIfBlank(bank.GlAccountCode) ?? DBNull.Value);
        Add(command, "@isActive", DbBool(bank.IsActive));
        Add(command, "@notes", (object?)NullIfBlank(bank.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private static Bank MapBank(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Code = ReadString(reader, 1) ?? string.Empty,
        NameEn = ReadString(reader, 2) ?? string.Empty,
        NameAr = ReadString(reader, 3),
        BranchName = ReadString(reader, 4),
        AccountNo = ReadString(reader, 5),
        Iban = ReadString(reader, 6),
        CurrencyCode = ReadString(reader, 7) ?? "SAR",
        GlAccountCode = ReadString(reader, 8),
        IsActive = ReadBool(reader, 9),
        Notes = ReadString(reader, 10)
    };

    // -------------------------------------------------------------- Currencies

    public async Task<IReadOnlyList<Currency>> GetCurrenciesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, symbol, fraction_name, is_active FROM currencies WHERE company_id = @company AND {NotDeleted} ORDER BY code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<Currency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapCurrency(reader));
        return result;
    }

    public async Task<Currency?> GetCurrencyByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code))
            return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, symbol, fraction_name, is_active FROM currencies WHERE company_id = @company AND code = @code AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", code.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapCurrency(reader) : null;
    }

    public async Task SaveCurrencyAsync(string companyId, Currency currency, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(currency);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCodeUniqueAsync(connection, transaction, "currencies", companyId, currency.Code.Trim().ToUpperInvariant(), null, currency.Id, "Currency", cancellationToken).ConfigureAwait(false);
            var exists = await ExistsByIdAsync(connection, transaction, "currencies", companyId, currency.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO currencies (id, company_id, code, name_en, name_ar, symbol, fraction_name, is_active, created_by) VALUES (@id, @company, @code, @nameEn, @nameAr, @symbol, @fraction, @isActive, @user)");
                BindCurrency(insert, companyId, currency, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE currencies SET code = @code, name_en = @nameEn, name_ar = @nameAr, symbol = @symbol, fraction_name = @fraction, is_active = @isActive, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindCurrency(update, companyId, currency, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Currency could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteCurrencyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("currencies", "Currency", companyId, id, userId, cancellationToken);

    public async Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(string companyId, string currencyCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var result = new List<CurrencyRate>();
        if (string.IsNullOrWhiteSpace(currencyCode))
            return result;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, currency_code, rate_date, rate, min_rate, max_rate FROM currency_rates WHERE company_id = @company AND currency_code = @code ORDER BY rate_date DESC LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", currencyCode.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CurrencyRate
            {
                Id = ReadGuid(reader, 0),
                CurrencyCode = ReadString(reader, 1) ?? string.Empty,
                RateDate = ReadDate(reader, 2) ?? DateOnly.FromDateTime(DateTime.Today),
                Rate = ReadDecimal(reader, 3),
                MinRate = ReadNullableDecimal(reader, 4),
                MaxRate = ReadNullableDecimal(reader, 5)
            });
        }
        return result;
    }

    public async Task SaveRateAsync(string companyId, CurrencyRate rate, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(rate);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The currency card must exist and be live for this company.
            await using (var check = CreateCommand(connection, transaction,
                $"SELECT COUNT(*) FROM currencies WHERE company_id = @company AND code = @code AND {NotDeleted}"))
            {
                Add(check, "@company", companyId.Trim());
                Add(check, "@code", rate.CurrencyCode.Trim().ToUpperInvariant());
                if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                    throw new InvalidOperationException($"Currency '{rate.CurrencyCode.Trim()}' was not found for this company.");
            }

            // One rate row per currency per day: re-saving a date updates it.
            Guid? existingId = null;
            await using (var find = CreateCommand(connection, transaction,
                "SELECT id FROM currency_rates WHERE company_id = @company AND currency_code = @code AND rate_date = @date"))
            {
                Add(find, "@company", companyId.Trim());
                Add(find, "@code", rate.CurrencyCode.Trim().ToUpperInvariant());
                Add(find, "@date", FormatDate(rate.RateDate));
                var value = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (value is not null and not DBNull)
                    existingId = value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
            }

            if (existingId.HasValue)
            {
                rate.Id = existingId.Value;
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE currency_rates SET rate = @rate, min_rate = @min, max_rate = @max, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id");
                Add(update, "@company", companyId.Trim());
                Add(update, "@id", FormatGuid(rate.Id));
                Add(update, "@rate", rate.Rate);
                Add(update, "@min", (object?)rate.MinRate ?? DBNull.Value);
                Add(update, "@max", (object?)rate.MaxRate ?? DBNull.Value);
                Add(update, "@user", userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (rate.Id == Guid.Empty)
                    rate.Id = Guid.NewGuid();
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO currency_rates (id, company_id, currency_code, rate_date, rate, min_rate, max_rate, created_by) VALUES (@id, @company, @code, @date, @rate, @min, @max, @user)");
                Add(insert, "@id", FormatGuid(rate.Id));
                Add(insert, "@company", companyId.Trim());
                Add(insert, "@code", rate.CurrencyCode.Trim().ToUpperInvariant());
                Add(insert, "@date", FormatDate(rate.RateDate));
                Add(insert, "@rate", rate.Rate);
                Add(insert, "@min", (object?)rate.MinRate ?? DBNull.Value);
                Add(insert, "@max", (object?)rate.MaxRate ?? DBNull.Value);
                Add(insert, "@user", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void BindCurrency(DbCommand command, string companyId, Currency currency, string userId)
    {
        Add(command, "@id", FormatGuid(currency.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", currency.Code.Trim().ToUpperInvariant());
        Add(command, "@nameEn", currency.NameEn.Trim());
        Add(command, "@nameAr", (object?)NullIfBlank(currency.NameAr) ?? DBNull.Value);
        Add(command, "@symbol", (object?)NullIfBlank(currency.Symbol) ?? DBNull.Value);
        Add(command, "@fraction", (object?)NullIfBlank(currency.FractionName) ?? DBNull.Value);
        Add(command, "@isActive", DbBool(currency.IsActive));
        Add(command, "@user", userId);
    }

    private static Currency MapCurrency(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Code = ReadString(reader, 1) ?? string.Empty,
        NameEn = ReadString(reader, 2) ?? string.Empty,
        NameAr = ReadString(reader, 3),
        Symbol = ReadString(reader, 4),
        FractionName = ReadString(reader, 5),
        IsActive = ReadBool(reader, 6)
    };

    // ----------------------------------------------------------------- Parties

    private const string PartyColumns = "id, kind, code, name_en, name_ar, party_group_id, tax_no, commercial_reg_no, credit_limit, credit_days, price_tier, gl_account_code, phone, mobile, email, address, is_active, notes, ledger_id, default_currency, risk_class, is_blacklisted, blacklist_reason, blacklisted_at, blacklisted_by, opening_balance_amount, opening_balance_date, opening_journal_id";

    public async Task<IReadOnlyList<Party>> GetPartiesAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"SELECT {PartyColumns} FROM parties WHERE company_id = @company AND {NotDeleted}";
        if (!string.IsNullOrWhiteSpace(kind))
            sql += " AND kind = @kind";
        sql += $" ORDER BY kind, code LIMIT {ListLimit}";
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(kind))
            Add(command, "@kind", kind.Trim().ToUpperInvariant());
        var result = new List<Party>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapParty(reader));
        return result;
    }

    public async Task<Party?> GetPartyByCodeAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(kind))
            return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT {PartyColumns} FROM parties WHERE company_id = @company AND kind = @kind AND code = @code AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", kind.Trim().ToUpperInvariant());
        Add(command, "@code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapParty(reader) : null;
    }

    public async Task<IReadOnlyList<Party>> SearchPartiesAsync(string companyId, string? kind, string query, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"SELECT {PartyColumns} FROM parties WHERE company_id = @company AND {NotDeleted}";
        if (!string.IsNullOrWhiteSpace(kind))
            sql += " AND kind = @kind";
        if (!string.IsNullOrWhiteSpace(query))
            sql += " AND (code LIKE @q ESCAPE '\\' OR name_en LIKE @q ESCAPE '\\' OR name_ar LIKE @q ESCAPE '\\' OR phone LIKE @q ESCAPE '\\' OR mobile LIKE @q ESCAPE '\\')";
        sql += $" ORDER BY kind, code LIMIT {ListLimit}";
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(kind))
            Add(command, "@kind", kind.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query))
            Add(command, "@q", "%" + EscapeLike(query.Trim()) + "%");
        var result = new List<Party>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapParty(reader));
        return result;
    }

    /// <summary>Previews the next party code without consuming it. The
    /// actual code is allocated inside SavePartyAsync's write transaction.</summary>
    public async Task<string> NextPartyCodeAsync(string companyId, string kind, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preview = await ResolvePartyCodeAsync(connection, transaction, companyId, kind,
                allocate: false, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return preview;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ResolvePartyCodeAsync(
        DbConnection connection, DbTransaction transaction, string companyId, string kind,
        bool allocate, CancellationToken cancellationToken)
    {
        var normalizedKind = kind.Trim().ToUpperInvariant();
        var series = normalizedKind == "S" ? "SUP" : "CUS";
        var isPg = _provider == "PostgreSQL";
        var config = await SequenceAllocator.ReadConfigAsync(connection, transaction, isPg,
            companyId.Trim(), series, SequenceAllocator.DefaultConfig(series, yearlyByDefault: false),
            cancellationToken).ConfigureAwait(false);
        var prefixes = normalizedKind == "S"
            ? new[] { config.Prefix, "SUP" }
            : new[] { config.Prefix, "CUS", "CUST" };
        long seed = 0;
        foreach (var prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var documentPrefix = prefix + "-";
            var suffixStart = documentPrefix.Length + 1;
            var substr = isPg ? "SUBSTRING(code FROM @start)" : "SUBSTR(code, @start)";
            var predicate = isPg
                ? $"{substr} ~ '^[0-9]+$'"
                : $"{substr} GLOB '[0-9]*' AND {substr} NOT GLOB '*[^0-9]*'";
            await using var maxCommand = CreateCommand(connection, transaction, $"""
                SELECT COALESCE(MAX(CAST({substr} AS {(isPg ? "BIGINT" : "INTEGER")})), 0)
                FROM parties
                WHERE company_id = @company AND kind = @kind AND code LIKE @pattern AND {predicate}
                """);
            Add(maxCommand, "@company", companyId.Trim());
            Add(maxCommand, "@kind", normalizedKind);
            Add(maxCommand, "@pattern", $"{documentPrefix}%");
            Add(maxCommand, "@start", suffixStart);
            var value = Convert.ToInt64(
                await maxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            seed = Math.Max(seed, value);
        }

        if (allocate)
        {
            var value = await SequenceAllocator.AllocateAsync(connection, transaction, isPg,
                companyId.Trim(), series, 0, seed, config.StartValue - 1, cancellationToken)
                .ConfigureAwait(false);
            return SequenceAllocator.Format(config, 0, value);
        }

        var current = SequenceAllocator.PeekMax(connection, transaction, companyId.Trim(), series);
        var next = checked(Math.Max(Math.Max(seed, current), config.StartValue - 1) + 1);
        if (next > SequenceAllocator.MaxValue)
            throw new AppError(ErrorCodes.NumberingLimitReached, $"The '{series}' party sequence has reached its supported limit.");
        return SequenceAllocator.Format(config, 0, next);
    }

    public async Task SavePartyAsync(string companyId, Party party, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(party);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsByIdAsync(connection, transaction, "parties", companyId, party.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                var preview = await ResolvePartyCodeAsync(connection, transaction, companyId, party.Kind,
                    allocate: false, cancellationToken).ConfigureAwait(false);
                if (string.Equals(party.Code, preview, StringComparison.OrdinalIgnoreCase))
                    party.Code = await ResolvePartyCodeAsync(connection, transaction, companyId, party.Kind,
                        allocate: true, cancellationToken).ConfigureAwait(false);
            }
            await EnsureCodeUniqueAsync(connection, transaction, "parties", companyId, party.Code, party.Kind, party.Id, "Party", cancellationToken).ConfigureAwait(false);
            if (party.PartyGroupId.HasValue)
            {
                await using var groupCheck = CreateCommand(connection, transaction,
                    $"SELECT kind FROM party_groups WHERE company_id = @company AND id = @id AND {NotDeleted}");
                Add(groupCheck, "@company", companyId.Trim());
                Add(groupCheck, "@id", FormatGuid(party.PartyGroupId.Value));
                var groupKind = await groupCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (groupKind is null or DBNull)
                    throw new InvalidOperationException("The party group was not found for this company.");
                if (!string.Equals(Convert.ToString(groupKind, CultureInfo.InvariantCulture), party.Kind, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The party group belongs to a different party kind.");
            }

            if (!exists)
            {
                // Step 4: every canonical party owns ONE stable ledger_id,
                // assigned here inside the save transaction above every
                // identity already in use (existing parties AND orphan
                // party_ledger rows), so a new party can never inherit a
                // stranger's balance.
                party.LedgerId = await NextLedgerIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO parties (id, company_id, kind, code, name_en, name_ar, party_group_id, tax_no, commercial_reg_no, credit_limit, credit_days, price_tier, gl_account_code, phone, mobile, email, address, is_active, notes, created_by, ledger_id, default_currency, risk_class, is_blacklisted, blacklist_reason, blacklisted_at, blacklisted_by) VALUES (@id, @company, @kind, @code, @nameEn, @nameAr, @group, @taxNo, @regNo, @creditLimit, @creditDays, @priceTier, @gl, @phone, @mobile, @email, @address, @isActive, @notes, @user, @ledgerId, @defaultCurrency, @riskClass, @isBlacklisted, @blacklistReason, @blacklistedAt, @blacklistedBy)");
                BindParty(insert, companyId, party, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE parties SET kind = @kind, code = @code, name_en = @nameEn, name_ar = @nameAr, party_group_id = @group, tax_no = @taxNo, commercial_reg_no = @regNo, credit_limit = @creditLimit, credit_days = @creditDays, price_tier = @priceTier, gl_account_code = @gl, phone = @phone, mobile = @mobile, email = @email, address = @address, is_active = @isActive, notes = @notes, default_currency = @defaultCurrency, risk_class = @riskClass, is_blacklisted = @isBlacklisted, blacklist_reason = @blacklistReason, blacklisted_at = @blacklistedAt, blacklisted_by = @blacklistedBy, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindParty(update, companyId, party, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Party could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeletePartyAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("parties", "Party", companyId, id, userId, cancellationToken);

    public async Task<int> CountPartyLedgerRefsAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var ledgerId = await ResolveOwnLedgerIdAsync(connection, null, companyId, kind, code, cancellationToken).ConfigureAwait(false);
        if (ledgerId is null)
            return 0;
        await using var command = CreateCommand(connection, null,
            "SELECT COUNT(*) FROM party_ledger WHERE company_id = @company AND party_kind = @kind AND party_id = @party");
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", kind.Trim().ToUpperInvariant());
        Add(command, "@party", ledgerId.Value);
        return (int)Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<LedgerBalance> GetPartyBalanceAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var balance = new LedgerBalance();
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(code))
            return balance;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var ledgerId = await ResolveOwnLedgerIdAsync(connection, null, companyId, kind, code, cancellationToken).ConfigureAwait(false);
        if (ledgerId is null)
            return balance;
        await using var command = CreateCommand(connection, null,
            "SELECT COALESCE(SUM(debit), 0), COALESCE(SUM(credit), 0) FROM party_ledger WHERE company_id = @company AND party_kind = @kind AND party_id = @party");
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", kind.Trim().ToUpperInvariant());
        Add(command, "@party", ledgerId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            balance.Debit = ReadDecimal(reader, 0);
            balance.Credit = ReadDecimal(reader, 1);
        }
        return balance;
    }

    // ------------------------------------------- Party structure (W1, mig 095)

    public async Task<IReadOnlyList<PartyContact>> GetPartyContactsAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, party_id, name, role, phone, mobile, email, is_primary, is_active, notes FROM party_contacts WHERE company_id = @company AND party_id = @party AND {NotDeleted} ORDER BY is_primary DESC, name");
        Add(command, "@company", companyId.Trim());
        Add(command, "@party", FormatGuid(partyId));
        var result = new List<PartyContact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new PartyContact
            {
                Id = ReadGuid(reader, 0), PartyId = ReadGuid(reader, 1),
                Name = ReadString(reader, 2) ?? string.Empty, Role = ReadString(reader, 3),
                Phone = ReadString(reader, 4), Mobile = ReadString(reader, 5), Email = ReadString(reader, 6),
                IsPrimary = ReadBool(reader, 7), IsActive = ReadBool(reader, 8), Notes = ReadString(reader, 9)
            });
        return result;
    }

    public async Task SavePartyContactAsync(string companyId, PartyContact contact, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(contact);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await ExistsByIdAsync(connection, transaction, "parties", companyId, contact.PartyId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The party was not found for this company.");
            var exists = await ExistsByIdAsync(connection, transaction, "party_contacts", companyId, contact.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO party_contacts (id, company_id, party_id, name, role, phone, mobile, email, is_primary, is_active, notes, created_by) VALUES (@id, @company, @party, @name, @role, @phone, @mobile, @email, @primary, @active, @notes, @user)");
                BindContact(insert, companyId, contact, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE party_contacts SET name = @name, role = @role, phone = @phone, mobile = @mobile, email = @email, is_primary = @primary, is_active = @active, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindContact(update, companyId, contact, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Party contact could not be updated (missing or deleted).");
            }
            if (contact.IsPrimary)
                await ClearOtherPrimariesAsync(connection, transaction, companyId, "party_contacts", "is_primary", contact.PartyId, contact.Id, cancellationToken).ConfigureAwait(false);
            await SyncPartySummaryAsync(connection, transaction, companyId, contact.PartyId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeletePartyContactAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        await SoftDeleteChildAsync("party_contacts", companyId, id, userId, cancellationToken).ConfigureAwait(false);
        await ResyncPartySummaryAsync(companyId, id, "party_contacts", cancellationToken).ConfigureAwait(false);
    }

    private void BindContact(DbCommand command, string companyId, PartyContact contact, string userId)
    {
        Add(command, "@id", FormatGuid(contact.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@party", FormatGuid(contact.PartyId));
        Add(command, "@name", contact.Name.Trim());
        Add(command, "@role", (object?)NullIfBlank(contact.Role) ?? DBNull.Value);
        Add(command, "@phone", (object?)NullIfBlank(contact.Phone) ?? DBNull.Value);
        Add(command, "@mobile", (object?)NullIfBlank(contact.Mobile) ?? DBNull.Value);
        Add(command, "@email", (object?)NullIfBlank(contact.Email) ?? DBNull.Value);
        Add(command, "@primary", DbBool(contact.IsPrimary));
        Add(command, "@active", DbBool(contact.IsActive));
        Add(command, "@notes", (object?)NullIfBlank(contact.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    public async Task<IReadOnlyList<PartyAddress>> GetPartyAddressesAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, party_id, kind, city, country, postal_code, address, is_default, is_active, notes FROM party_addresses WHERE company_id = @company AND party_id = @party AND {NotDeleted} ORDER BY is_default DESC, kind");
        Add(command, "@company", companyId.Trim());
        Add(command, "@party", FormatGuid(partyId));
        var result = new List<PartyAddress>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new PartyAddress
            {
                Id = ReadGuid(reader, 0), PartyId = ReadGuid(reader, 1),
                Kind = ReadString(reader, 2) ?? PartyAddressKinds.Delivery,
                City = ReadString(reader, 3), Country = ReadString(reader, 4), PostalCode = ReadString(reader, 5),
                Address = ReadString(reader, 6), IsDefault = ReadBool(reader, 7), IsActive = ReadBool(reader, 8),
                Notes = ReadString(reader, 9)
            });
        return result;
    }

    public async Task SavePartyAddressAsync(string companyId, PartyAddress address, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(address);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await ExistsByIdAsync(connection, transaction, "parties", companyId, address.PartyId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The party was not found for this company.");
            var exists = await ExistsByIdAsync(connection, transaction, "party_addresses", companyId, address.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO party_addresses (id, company_id, party_id, kind, city, country, postal_code, address, is_default, is_active, notes, created_by) VALUES (@id, @company, @party, @kind, @city, @country, @postal, @address, @isDefault, @active, @notes, @user)");
                BindAddress(insert, companyId, address, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE party_addresses SET kind = @kind, city = @city, country = @country, postal_code = @postal, address = @address, is_default = @isDefault, is_active = @active, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindAddress(update, companyId, address, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Party address could not be updated (missing or deleted).");
            }
            if (address.IsDefault)
                await ClearOtherPrimariesAsync(connection, transaction, companyId, "party_addresses", "is_default", address.PartyId, address.Id, cancellationToken).ConfigureAwait(false);
            await SyncPartySummaryAsync(connection, transaction, companyId, address.PartyId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeletePartyAddressAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
    {
        await SoftDeleteChildAsync("party_addresses", companyId, id, userId, cancellationToken).ConfigureAwait(false);
        await ResyncPartySummaryAsync(companyId, id, "party_addresses", cancellationToken).ConfigureAwait(false);
    }

    private void BindAddress(DbCommand command, string companyId, PartyAddress address, string userId)
    {
        Add(command, "@id", FormatGuid(address.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@party", FormatGuid(address.PartyId));
        Add(command, "@kind", address.Kind.Trim());
        Add(command, "@city", (object?)NullIfBlank(address.City) ?? DBNull.Value);
        Add(command, "@country", (object?)NullIfBlank(address.Country) ?? DBNull.Value);
        Add(command, "@postal", (object?)NullIfBlank(address.PostalCode) ?? DBNull.Value);
        Add(command, "@address", (object?)NullIfBlank(address.Address) ?? DBNull.Value);
        Add(command, "@isDefault", DbBool(address.IsDefault));
        Add(command, "@active", DbBool(address.IsActive));
        Add(command, "@notes", (object?)NullIfBlank(address.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    public async Task<IReadOnlyList<PartyBranch>> GetPartyBranchesAsync(string companyId, Guid partyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, party_id, name, address, phone, is_active, notes FROM party_branches WHERE company_id = @company AND party_id = @party AND {NotDeleted} ORDER BY name");
        Add(command, "@company", companyId.Trim());
        Add(command, "@party", FormatGuid(partyId));
        var result = new List<PartyBranch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new PartyBranch
            {
                Id = ReadGuid(reader, 0), PartyId = ReadGuid(reader, 1),
                Name = ReadString(reader, 2) ?? string.Empty, Address = ReadString(reader, 3),
                Phone = ReadString(reader, 4), IsActive = ReadBool(reader, 5), Notes = ReadString(reader, 6)
            });
        return result;
    }

    public async Task SavePartyBranchAsync(string companyId, PartyBranch branch, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(branch);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await ExistsByIdAsync(connection, transaction, "parties", companyId, branch.PartyId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The party was not found for this company.");
            var exists = await ExistsByIdAsync(connection, transaction, "party_branches", companyId, branch.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO party_branches (id, company_id, party_id, name, address, phone, is_active, notes, created_by) VALUES (@id, @company, @party, @name, @address, @phone, @active, @notes, @user)");
                BindBranch(insert, companyId, branch, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE party_branches SET name = @name, address = @address, phone = @phone, is_active = @active, notes = @notes, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindBranch(update, companyId, branch, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Party branch could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    public Task SoftDeletePartyBranchAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteChildAsync("party_branches", companyId, id, userId, cancellationToken);

    private void BindBranch(DbCommand command, string companyId, PartyBranch branch, string userId)
    {
        Add(command, "@id", FormatGuid(branch.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@party", FormatGuid(branch.PartyId));
        Add(command, "@name", branch.Name.Trim());
        Add(command, "@address", (object?)NullIfBlank(branch.Address) ?? DBNull.Value);
        Add(command, "@phone", (object?)NullIfBlank(branch.Phone) ?? DBNull.Value);
        Add(command, "@active", DbBool(branch.IsActive));
        Add(command, "@notes", (object?)NullIfBlank(branch.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private async Task SoftDeleteChildAsync(string table, string companyId, Guid id, string userId, CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        if (id == Guid.Empty)
            throw new ArgumentException("Record is required.", nameof(id));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"UPDATE {table} SET is_deleted = {DeletedValue}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(id));
        Add(command, "@user", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Record was not found or is already deleted.");
    }

    private async Task ClearOtherPrimariesAsync(DbConnection connection, DbTransaction transaction, string companyId, string table, string flagColumn, Guid partyId, Guid keepId, CancellationToken cancellationToken)
    {
        var falseValue = _provider == "PostgreSQL" ? "false" : "0";
        await using var command = CreateCommand(connection, transaction,
            $"UPDATE {table} SET {flagColumn} = {falseValue} WHERE company_id = @company AND party_id = @party AND id <> @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@party", FormatGuid(partyId));
        Add(command, "@id", FormatGuid(keepId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Keeps the party row's legacy single fields as the PRIMARY
    /// contact / DEFAULT address summary (W1 sync rule): the primary
    /// contact mirrors phone/mobile/email, the default address mirrors
    /// the address text. When no primary/default row exists the summary
    /// is left untouched (the card fields remain the summary of record).</summary>
    private async Task SyncPartySummaryAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid partyId, CancellationToken cancellationToken)
    {
        string? phone = null, mobile = null, email = null;
        var hasContact = false;
        await using (var find = CreateCommand(connection, transaction,
            $"SELECT phone, mobile, email FROM party_contacts WHERE company_id = @company AND party_id = @party AND {NotDeleted} AND is_primary = {( _provider == "PostgreSQL" ? "true" : "1")} LIMIT 1"))
        {
            Add(find, "@company", companyId.Trim());
            Add(find, "@party", FormatGuid(partyId));
            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hasContact = true;
                phone = ReadString(reader, 0); mobile = ReadString(reader, 1); email = ReadString(reader, 2);
            }
        }
        if (hasContact)
        {
            await using var update = CreateCommand(connection, transaction,
                $"UPDATE parties SET phone = @phone, mobile = @mobile, email = @email, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
            Add(update, "@company", companyId.Trim());
            Add(update, "@id", FormatGuid(partyId));
            Add(update, "@phone", (object?)phone ?? DBNull.Value);
            Add(update, "@mobile", (object?)mobile ?? DBNull.Value);
            Add(update, "@email", (object?)email ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string? address = null;
        var hasAddress = false;
        await using (var find = CreateCommand(connection, transaction,
            $"SELECT address FROM party_addresses WHERE company_id = @company AND party_id = @party AND {NotDeleted} AND is_default = {(_provider == "PostgreSQL" ? "true" : "1")} LIMIT 1"))
        {
            Add(find, "@company", companyId.Trim());
            Add(find, "@party", FormatGuid(partyId));
            await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hasAddress = true;
                address = ReadString(reader, 0);
            }
        }
        if (hasAddress)
        {
            await using var update = CreateCommand(connection, transaction,
                $"UPDATE parties SET address = @address, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
            Add(update, "@company", companyId.Trim());
            Add(update, "@id", FormatGuid(partyId));
            Add(update, "@address", (object?)address ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ResyncPartySummaryAsync(string companyId, Guid childId, string childTable, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        Guid partyId;
        await using (var find = CreateCommand(connection, null,
            $"SELECT party_id FROM {childTable} WHERE company_id = @company AND id = @id"))
        {
            Add(find, "@company", companyId.Trim());
            Add(find, "@id", FormatGuid(childId));
            var value = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull)
                return;
            partyId = ReadGuidValue(value);
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncPartySummaryAsync(connection, transaction, companyId, partyId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private static Guid ReadGuidValue(object value)
        => value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);

    /// <summary>
    /// Posts the party's opening balance in ONE transaction: a GL journal
    /// through GlPosting (Dr party control / Cr retained earnings 32 for
    /// a customer debit opening; mirrored for credits and for suppliers),
    /// the matching party_ledger row (doc_kind 'OPN'), and the marker on
    /// the party row. The marker is the one-opening guard: while it is
    /// set, a second opening is refused (corrections go through a normal
    /// journal, never a silent re-opening).
    /// </summary>
    public async Task<string> PostPartyOpeningBalanceAsync(string companyId, string kind, string code, decimal signedAmount, DateOnly openingDate, string currencyCode, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (signedAmount == 0m)
            throw new InvalidOperationException("Opening balance amount cannot be zero.");
        var partyKind = kind.Trim().ToUpperInvariant();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Party? party = null;
            await using (var find = CreateCommand(connection, transaction,
                $"SELECT {PartyColumns} FROM parties WHERE company_id = @company AND kind = @kind AND code = @code AND {NotDeleted}"))
            {
                Add(find, "@company", companyId.Trim());
                Add(find, "@kind", partyKind);
                Add(find, "@code", code.Trim());
                await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    party = MapParty(reader);
            }
            if (party is null)
                throw new InvalidOperationException($"Party '{code.Trim()}' was not found for this company.");
            if (party.OpeningBalanceAmount.HasValue || !string.IsNullOrWhiteSpace(party.OpeningJournalId))
                throw new InvalidOperationException($"Party '{code.Trim()}' already has an opening balance ({party.OpeningBalanceAmount}); a second opening is blocked. Post a correcting journal instead.");
            if (party.LedgerId <= 0)
                throw new InvalidOperationException("The party has no ledger identity; save the card first.");

            var companyGuid = await ResolveCompanyGuidAsync(connection, transaction, companyId, cancellationToken).ConfigureAwait(false);
            var controlAccount = !string.IsNullOrWhiteSpace(party.GlAccountCode)
                ? party.GlAccountCode!.Trim()
                : partyKind == PartyKinds.Customer ? GlAccounts.Receivable : GlAccounts.Payable;
            var amount = Math.Abs(signedAmount);
            // Orientation: a customer opening DEBIT (receivable) is
            // Dr control / Cr equity; a customer credit opening mirrors.
            // Suppliers mirror customers (a payable opening is a credit).
            var controlIsDebit = (partyKind == PartyKinds.Customer) == (signedAmount > 0);
            var lines = new[]
            {
                new GlLineRequest(controlAccount, controlIsDebit ? amount : 0m, controlIsDebit ? 0m : amount, null, $"Opening balance — {party.Code}"),
                new GlLineRequest(GlAccounts.OpeningBalanceEquity, controlIsDebit ? 0m : amount, controlIsDebit ? amount : 0m, null, $"Opening balance — {party.Code}")
            };
            var sourceNo = $"OPN-{partyKind}-{party.Code}";
            var result = GlPosting.PostInTransaction(connection, transaction, _provider,
                new GlDocumentRequest(companyGuid, openingDate,
                    $"Opening balance — {partyKind} {party.Code} {party.NameEn}",
                    "OPN", party.Id.ToString("D"), sourceNo,
                    string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim().ToUpperInvariant(), 0m, lines),
                userId);

            var companyCode = await PartyLedger.ResolveCompanyCodeAsync(connection, transaction, _provider, companyId, cancellationToken).ConfigureAwait(false);
            var ledgerDebit = partyKind == PartyKinds.Customer ? (signedAmount > 0 ? amount : 0m) : (signedAmount > 0 ? 0m : amount);
            var ledgerCredit = partyKind == PartyKinds.Customer ? (signedAmount > 0 ? 0m : amount) : (signedAmount > 0 ? amount : 0m);
            // Supplier signedAmount convention: positive = amount owed
            // (a credit balance), so the ledger row is a credit; the GL
            // above already mirrors through controlIsDebit.
            PartyLedger.WriteEntry(connection, transaction, _provider, companyCode, partyKind, party.LedgerId,
                "OPN", null, sourceNo, openingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ledgerDebit, ledgerCredit,
                string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim().ToUpperInvariant(),
                "Opening balance", result.JournalEntryId.ToString("D"));

            await using (var mark = CreateCommand(connection, transaction,
                $"UPDATE parties SET opening_balance_amount = @amount, opening_balance_date = @date, opening_journal_id = @journal, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}"))
            {
                Add(mark, "@amount", signedAmount);
                Add(mark, "@date", openingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(mark, "@journal", result.JournalEntryId.ToString("D"));
                Add(mark, "@user", userId);
                Add(mark, "@company", companyId.Trim());
                Add(mark, "@id", FormatGuid(party.Id));
                if (await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("The opening-balance marker could not be stored.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result.JournalEntryId.ToString("D");
        }
        catch { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private async Task<Guid> ResolveCompanyGuidAsync(DbConnection connection, DbTransaction transaction, string companyId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @code");
        Add(command, "@code", companyId.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull || !Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid))
            throw new InvalidOperationException($"Company '{companyId.Trim()}' has no Guid identity in the companies registry; the opening balance cannot post to GL.");
        return guid;
    }

    /// <summary>
    /// The party's OWN ledger id (migration 061): the single
    /// <c>parties.ledger_id</c> of the live party matched by
    /// (company, kind, code). This replaces the pre-unification resolution
    /// that parsed the code as a number and consulted a second party
    /// master — code "1" could inherit another party's balance (C-2).
    /// </summary>
    private async Task<long?> ResolveOwnLedgerIdAsync(DbConnection connection, DbTransaction? transaction, string companyId, string kind, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(code))
            return null;
        return await PartyLedger.ResolveLedgerIdAsync(connection, transaction, _provider, companyId, kind, code, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> NextLedgerIdAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        long maxParty = 0, maxLedger = 0;
        await using (var parties = CreateCommand(connection, transaction,
            "SELECT COALESCE(MAX(ledger_id), 0) FROM parties"))
        {
            maxParty = Convert.ToInt64(await parties.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        if (await TableExistsAsync(connection, transaction, "party_ledger", cancellationToken).ConfigureAwait(false))
        {
            await using var ledger = CreateCommand(connection, transaction,
                "SELECT COALESCE(MAX(party_id), 0) FROM party_ledger");
            maxLedger = Convert.ToInt64(await ledger.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        return Math.Max(maxParty, maxLedger) + 1;
    }

    private async Task<bool> TableExistsAsync(DbConnection connection, DbTransaction? transaction, string table, CancellationToken cancellationToken)
    {
        var sql = _provider == "PostgreSQL"
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, "@name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private void BindParty(DbCommand command, string companyId, Party party, string userId)
    {
        Add(command, "@id", FormatGuid(party.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", party.Kind.Trim().ToUpperInvariant());
        Add(command, "@code", party.Code.Trim());
        Add(command, "@nameEn", party.NameEn.Trim());
        Add(command, "@nameAr", (object?)NullIfBlank(party.NameAr) ?? DBNull.Value);
        Add(command, "@group", (object?)FormatNullableGuid(party.PartyGroupId) ?? DBNull.Value);
        Add(command, "@taxNo", (object?)NullIfBlank(party.TaxNo) ?? DBNull.Value);
        Add(command, "@regNo", (object?)NullIfBlank(party.CommercialRegNo) ?? DBNull.Value);
        Add(command, "@creditLimit", party.CreditLimit);
        Add(command, "@creditDays", party.CreditDays);
        Add(command, "@priceTier", (object?)NullIfBlank(party.PriceTier) ?? DBNull.Value);
        Add(command, "@gl", (object?)NullIfBlank(party.GlAccountCode) ?? DBNull.Value);
        Add(command, "@phone", (object?)NullIfBlank(party.Phone) ?? DBNull.Value);
        Add(command, "@mobile", (object?)NullIfBlank(party.Mobile) ?? DBNull.Value);
        Add(command, "@email", (object?)NullIfBlank(party.Email) ?? DBNull.Value);
        Add(command, "@address", (object?)NullIfBlank(party.Address) ?? DBNull.Value);
        Add(command, "@isActive", DbBool(party.IsActive));
        Add(command, "@notes", (object?)NullIfBlank(party.Notes) ?? DBNull.Value);
        Add(command, "@user", userId);
        Add(command, "@ledgerId", party.LedgerId);
        Add(command, "@defaultCurrency", (object?)NullIfBlank(party.DefaultCurrency) ?? DBNull.Value);
        Add(command, "@riskClass", (object?)NullIfBlank(party.RiskClass) ?? DBNull.Value);
        Add(command, "@isBlacklisted", DbBool(party.IsBlacklisted));
        Add(command, "@blacklistReason", (object?)NullIfBlank(party.BlacklistReason) ?? DBNull.Value);
        Add(command, "@blacklistedAt", (object?)NullIfBlank(party.BlacklistedAt) ?? DBNull.Value);
        Add(command, "@blacklistedBy", (object?)NullIfBlank(party.BlacklistedBy) ?? DBNull.Value);
    }

    private static Party MapParty(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Kind = ReadString(reader, 1) ?? PartyKinds.Customer,
        Code = ReadString(reader, 2) ?? string.Empty,
        NameEn = ReadString(reader, 3) ?? string.Empty,
        NameAr = ReadString(reader, 4),
        PartyGroupId = ReadNullableGuid(reader, 5),
        TaxNo = ReadString(reader, 6),
        CommercialRegNo = ReadString(reader, 7),
        CreditLimit = ReadDecimal(reader, 8),
        CreditDays = ReadInt(reader, 9),
        PriceTier = ReadString(reader, 10),
        GlAccountCode = ReadString(reader, 11),
        Phone = ReadString(reader, 12),
        Mobile = ReadString(reader, 13),
        Email = ReadString(reader, 14),
        Address = ReadString(reader, 15),
        IsActive = ReadBool(reader, 16),
        Notes = ReadString(reader, 17),
        LedgerId = reader.IsDBNull(18) ? 0 : Convert.ToInt64(reader.GetValue(18), CultureInfo.InvariantCulture),
        DefaultCurrency = ReadString(reader, 19),
        RiskClass = ReadString(reader, 20),
        IsBlacklisted = ReadBool(reader, 21),
        BlacklistReason = ReadString(reader, 22),
        BlacklistedAt = ReadString(reader, 23),
        BlacklistedBy = ReadString(reader, 24),
        OpeningBalanceAmount = reader.IsDBNull(25) ? null : ReadDecimal(reader, 25),
        OpeningBalanceDate = ReadString(reader, 26),
        OpeningJournalId = ReadString(reader, 27)
    };

    // ------------------------------------------------------------ Party groups

    public async Task<IReadOnlyList<PartyGroup>> GetPartyGroupsAsync(string companyId, string? kind = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = $"SELECT id, kind, code, name_en, name_ar, parent_id, is_active FROM party_groups WHERE company_id = @company AND {NotDeleted}";
        if (!string.IsNullOrWhiteSpace(kind))
            sql += " AND kind = @kind";
        sql += $" ORDER BY kind, code LIMIT {ListLimit}";
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(kind))
            Add(command, "@kind", kind.Trim().ToUpperInvariant());
        var result = new List<PartyGroup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapPartyGroup(reader));
        return result;
    }

    public async Task<PartyGroup?> GetPartyGroupByCodeAsync(string companyId, string kind, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(kind))
            return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, kind, code, name_en, name_ar, parent_id, is_active FROM party_groups WHERE company_id = @company AND kind = @kind AND code = @code AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", kind.Trim().ToUpperInvariant());
        Add(command, "@code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapPartyGroup(reader) : null;
    }

    public async Task SavePartyGroupAsync(string companyId, PartyGroup group, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(group);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCodeUniqueAsync(connection, transaction, "party_groups", companyId, group.Code, group.Kind, group.Id, "Party group", cancellationToken).ConfigureAwait(false);
            if (group.ParentId.HasValue)
            {
                if (group.ParentId.Value == group.Id)
                    throw new InvalidOperationException("A party group cannot be its own parent.");
                await using var parentCheck = CreateCommand(connection, transaction,
                    $"SELECT kind FROM party_groups WHERE company_id = @company AND id = @id AND {NotDeleted}");
                Add(parentCheck, "@company", companyId.Trim());
                Add(parentCheck, "@id", FormatGuid(group.ParentId.Value));
                var parentKind = await parentCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (parentKind is null or DBNull)
                    throw new InvalidOperationException("The parent party group was not found for this company.");
                if (!string.Equals(Convert.ToString(parentKind, CultureInfo.InvariantCulture), group.Kind, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The parent party group belongs to a different party kind.");
            }

            var exists = await ExistsByIdAsync(connection, transaction, "party_groups", companyId, group.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO party_groups (id, company_id, kind, code, name_en, name_ar, parent_id, is_active, created_by) VALUES (@id, @company, @kind, @code, @nameEn, @nameAr, @parent, @isActive, @user)");
                BindPartyGroup(insert, companyId, group, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE party_groups SET kind = @kind, code = @code, name_en = @nameEn, name_ar = @nameAr, parent_id = @parent, is_active = @isActive, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindPartyGroup(update, companyId, group, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Party group could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeletePartyGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("party_groups", "Party group", companyId, id, userId, cancellationToken);

    private void BindPartyGroup(DbCommand command, string companyId, PartyGroup group, string userId)
    {
        Add(command, "@id", FormatGuid(group.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@kind", group.Kind.Trim().ToUpperInvariant());
        Add(command, "@code", group.Code.Trim());
        Add(command, "@nameEn", group.NameEn.Trim());
        Add(command, "@nameAr", (object?)NullIfBlank(group.NameAr) ?? DBNull.Value);
        Add(command, "@parent", (object?)FormatNullableGuid(group.ParentId) ?? DBNull.Value);
        Add(command, "@isActive", DbBool(group.IsActive));
        Add(command, "@user", userId);
    }

    private static PartyGroup MapPartyGroup(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Kind = ReadString(reader, 1) ?? PartyKinds.Customer,
        Code = ReadString(reader, 2) ?? string.Empty,
        NameEn = ReadString(reader, 3) ?? string.Empty,
        NameAr = ReadString(reader, 4),
        ParentId = ReadNullableGuid(reader, 5),
        IsActive = ReadBool(reader, 6)
    };

    // -------------------------------------------------------------- Sales reps

    public async Task<IReadOnlyList<SalesRep>> GetSalesRepsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, phone, commission_pct, target_amount, group_id, is_active FROM sales_reps WHERE company_id = @company AND {NotDeleted} ORDER BY code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<SalesRep>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapSalesRep(reader));
        return result;
    }

    public async Task<SalesRep?> GetSalesRepByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code))
            return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, phone, commission_pct, target_amount, group_id, is_active FROM sales_reps WHERE company_id = @company AND code = @code AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapSalesRep(reader) : null;
    }

    public async Task SaveSalesRepAsync(string companyId, SalesRep rep, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(rep);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCodeUniqueAsync(connection, transaction, "sales_reps", companyId, rep.Code, null, rep.Id, "Sales rep", cancellationToken).ConfigureAwait(false);
            if (rep.GroupId.HasValue && !await ExistsByIdAsync(connection, transaction, "sales_rep_groups", companyId, rep.GroupId.Value, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The sales rep group was not found for this company.");

            var exists = await ExistsByIdAsync(connection, transaction, "sales_reps", companyId, rep.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO sales_reps (id, company_id, code, name_en, name_ar, phone, commission_pct, target_amount, group_id, is_active, created_by) VALUES (@id, @company, @code, @nameEn, @nameAr, @phone, @commission, @target, @group, @isActive, @user)");
                BindSalesRep(insert, companyId, rep, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE sales_reps SET code = @code, name_en = @nameEn, name_ar = @nameAr, phone = @phone, commission_pct = @commission, target_amount = @target, group_id = @group, is_active = @isActive, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindSalesRep(update, companyId, rep, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Sales rep could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteSalesRepAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("sales_reps", "Sales rep", companyId, id, userId, cancellationToken);

    private void BindSalesRep(DbCommand command, string companyId, SalesRep rep, string userId)
    {
        Add(command, "@id", FormatGuid(rep.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", rep.Code.Trim());
        Add(command, "@nameEn", rep.NameEn.Trim());
        Add(command, "@nameAr", (object?)NullIfBlank(rep.NameAr) ?? DBNull.Value);
        Add(command, "@phone", (object?)NullIfBlank(rep.Phone) ?? DBNull.Value);
        Add(command, "@commission", rep.CommissionPct);
        Add(command, "@target", rep.TargetAmount);
        Add(command, "@group", (object?)FormatNullableGuid(rep.GroupId) ?? DBNull.Value);
        Add(command, "@isActive", DbBool(rep.IsActive));
        Add(command, "@user", userId);
    }

    private static SalesRep MapSalesRep(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Code = ReadString(reader, 1) ?? string.Empty,
        NameEn = ReadString(reader, 2) ?? string.Empty,
        NameAr = ReadString(reader, 3),
        Phone = ReadString(reader, 4),
        CommissionPct = ReadDecimal(reader, 5),
        TargetAmount = ReadDecimal(reader, 6),
        GroupId = ReadNullableGuid(reader, 7),
        IsActive = ReadBool(reader, 8)
    };

    // -------------------------------------------------------- Sales rep groups

    public async Task<IReadOnlyList<SalesRepGroup>> GetSalesRepGroupsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, is_active FROM sales_rep_groups WHERE company_id = @company AND {NotDeleted} ORDER BY code LIMIT {ListLimit}");
        Add(command, "@company", companyId.Trim());
        var result = new List<SalesRepGroup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(MapSalesRepGroup(reader));
        return result;
    }

    public async Task<SalesRepGroup?> GetSalesRepGroupByCodeAsync(string companyId, string code, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(code))
            return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, is_active FROM sales_rep_groups WHERE company_id = @company AND code = @code AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapSalesRepGroup(reader) : null;
    }

    public async Task SaveSalesRepGroupAsync(string companyId, SalesRepGroup group, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(group);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCodeUniqueAsync(connection, transaction, "sales_rep_groups", companyId, group.Code, null, group.Id, "Sales rep group", cancellationToken).ConfigureAwait(false);
            var exists = await ExistsByIdAsync(connection, transaction, "sales_rep_groups", companyId, group.Id, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await using var insert = CreateCommand(connection, transaction,
                    "INSERT INTO sales_rep_groups (id, company_id, code, name_en, name_ar, is_active, created_by) VALUES (@id, @company, @code, @nameEn, @nameAr, @isActive, @user)");
                BindSalesRepGroup(insert, companyId, group, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction,
                    $"UPDATE sales_rep_groups SET code = @code, name_en = @nameEn, name_ar = @nameAr, is_active = @isActive, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
                BindSalesRepGroup(update, companyId, group, userId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Sales rep group could not be updated (missing or deleted).");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task SoftDeleteSalesRepGroupAsync(string companyId, Guid id, string userId, CancellationToken cancellationToken = default)
        => SoftDeleteAsync("sales_rep_groups", "Sales rep group", companyId, id, userId, cancellationToken);

    private void BindSalesRepGroup(DbCommand command, string companyId, SalesRepGroup group, string userId)
    {
        Add(command, "@id", FormatGuid(group.Id));
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", group.Code.Trim());
        Add(command, "@nameEn", group.NameEn.Trim());
        Add(command, "@nameAr", (object?)NullIfBlank(group.NameAr) ?? DBNull.Value);
        Add(command, "@isActive", DbBool(group.IsActive));
        Add(command, "@user", userId);
    }

    private static SalesRepGroup MapSalesRepGroup(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0),
        Code = ReadString(reader, 1) ?? string.Empty,
        NameEn = ReadString(reader, 2) ?? string.Empty,
        NameAr = ReadString(reader, 3),
        IsActive = ReadBool(reader, 4)
    };

    // ---------------------------------------------------------- Shared helpers
    // Table names below are internal constants only (never caller input), so
    // interpolating them is safe; every value is always a bound parameter.

    private async Task EnsureCodeUniqueAsync(DbConnection connection, DbTransaction transaction, string table, string companyId, string code, string? kind, Guid selfId, string entityName, CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(*) FROM {table} WHERE company_id = @company AND code = @code AND {NotDeleted} AND id <> @id";
        if (kind is not null)
            sql += " AND kind = @kind";
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, "@company", companyId.Trim());
        Add(command, "@code", code.Trim());
        Add(command, "@id", FormatGuid(selfId));
        if (kind is not null)
            Add(command, "@kind", kind.Trim().ToUpperInvariant());
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            throw new InvalidOperationException($"{entityName} code '{code.Trim()}' already exists for this company.");
    }

    private async Task<bool> ExistsByIdAsync(DbConnection connection, DbTransaction? transaction, string table, string companyId, Guid id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            $"SELECT COUNT(*) FROM {table} WHERE company_id = @company AND id = @id AND {NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(id));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private async Task SoftDeleteAsync(string table, string entityName, string companyId, Guid id, string userId, CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        if (id == Guid.Empty)
            throw new ArgumentException($"{entityName} is required.", nameof(id));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Delete guards re-checked inside the delete transaction so a
            // reference created concurrently cannot slip past the service.
            if (table == "cost_centers")
            {
                string? code;
                await using (var find = CreateCommand(connection, transaction,
                    $"SELECT code FROM cost_centers WHERE company_id = @company AND id = @id AND {NotDeleted}"))
                {
                    Add(find, "@company", companyId.Trim());
                    Add(find, "@id", FormatGuid(id));
                    var value = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    code = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                if (code is null)
                    throw new InvalidOperationException("Cost center was not found or is already deleted.");
                if (await CountCostCenterJournalRefsAsync(connection, transaction, companyId, code, cancellationToken).ConfigureAwait(false) > 0)
                    throw new InvalidOperationException($"Cost center '{code}' cannot be deleted because journal lines reference it. Deactivate it instead.");
                await using var children = CreateCommand(connection, transaction,
                    $"SELECT COUNT(*) FROM cost_centers WHERE company_id = @company AND parent_id = @id AND {NotDeleted}");
                Add(children, "@company", companyId.Trim());
                Add(children, "@id", FormatGuid(id));
                if (Convert.ToInt64(await children.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                    throw new InvalidOperationException("Cost center cannot be deleted while it has child cost centers.");
            }
            if (table == "parties")
            {
                string? kind;
                string? code;
                await using (var find = CreateCommand(connection, transaction,
                    $"SELECT kind, code FROM parties WHERE company_id = @company AND id = @id AND {NotDeleted}"))
                {
                    Add(find, "@company", companyId.Trim());
                    Add(find, "@id", FormatGuid(id));
                    await using var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("Party was not found or is already deleted.");
                    kind = ReadString(reader, 0);
                    code = ReadString(reader, 1);
                }
                var ownLedgerId = await ResolveOwnLedgerIdAsync(connection, transaction, companyId, kind!, code!, cancellationToken).ConfigureAwait(false);
                if (ownLedgerId is not null)
                {
                    await using var refs = CreateCommand(connection, transaction,
                        "SELECT COUNT(*) FROM party_ledger WHERE company_id = @company AND party_kind = @kind AND party_id = @party");
                    Add(refs, "@company", companyId.Trim());
                    Add(refs, "@kind", kind!.Trim().ToUpperInvariant());
                    Add(refs, "@party", ownLedgerId.Value);
                    if (Convert.ToInt64(await refs.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
                        throw new InvalidOperationException($"Party '{code}' cannot be deleted because party ledger entries reference it. Deactivate it instead.");
                }
            }

            await using var command = CreateCommand(connection, transaction,
                $"UPDATE {table} SET is_deleted = {DeletedValue}, updated_by = @user, updated_at = {NowExpr} WHERE company_id = @company AND id = @id AND {NotDeleted}");
            Add(command, "@company", companyId.Trim());
            Add(command, "@id", FormatGuid(id));
            Add(command, "@user", userId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException($"{entityName} was not found or is already deleted.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private object DbBool(bool value) => _provider == "PostgreSQL" ? value : (value ? 1 : 0);

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null)
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

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString();

    private object? FormatNullableGuid(Guid? value) => value.HasValue ? FormatGuid(value.Value) : null;

    private static Guid ReadGuid(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid g => g,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
        };
    }

    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool ReadBool(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return false;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            long l => l != 0,
            int i => i != 0,
            short s => s != 0,
            _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
        };
    }

    private static int ReadInt(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal? ReadNullableDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateOnly? ReadDate(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            string s when s.Length >= 10 && DateOnly.TryParse(s[..10], CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string NormalizeProvider(string provider) => (provider ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
