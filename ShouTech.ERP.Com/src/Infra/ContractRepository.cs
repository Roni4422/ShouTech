using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral sales-contract persistence (migration 101),
/// following the CommRepository patterns: parameterized SQL only,
/// company scoping on every statement, soft deletes, single-transaction
/// writes. Company keying is the party world's company CODE, exactly
/// like parties / sco_* (097); chain documents carry Guid-or-code keys,
/// resolved through the companies registry at the seams.
///
/// DRAW-DOWN MAINTENANCE: delivered_qty / invoiced_qty on
/// sct_contract_lines are STORED progress in the Part I
/// per_delivered / per_billed style — RefreshProgress recomputes them
/// from the POSTED chain documents linked to the contract's orders
/// (contract orders are sales_documents.contract_id = this contract;
/// their deliveries / invoices hang off document_links) and persists
/// the result. There is no parallel tracker: the chain documents are
/// the only source, the stored columns are a cache of that read.
/// </summary>
public sealed class ContractRepository : IContractRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public ContractRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = GlPosting.NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = false" : "is_deleted = 0";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";
    private object Truth(bool value) => IsPg ? value : (value ? 1 : 0);

    // ------------------------------------------------------------------
    // Read
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<SalesContract>> GetContractsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var contracts = await ReadContractsAsync(connection, null, company, null, status, cancellationToken).ConfigureAwait(false);
        foreach (var contract in contracts)
            contract.Lines = await ReadLinesAsync(connection, null, company, contract.Id, cancellationToken).ConfigureAwait(false);
        return contracts;
    }

    public async Task<SalesContract?> GetContractAsync(string companyId, Guid contractId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var found = await ReadContractsAsync(connection, null, company, contractId, null, cancellationToken).ConfigureAwait(false);
        var contract = found.Count > 0 ? found[0] : null;
        if (contract is not null)
            contract.Lines = await ReadLinesAsync(connection, null, company, contract.Id, cancellationToken).ConfigureAwait(false);
        return contract;
    }

    private async Task<List<SalesContract>> ReadContractsAsync(
        DbConnection connection, DbTransaction? transaction, string company,
        Guid? onlyId, string? status, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT contract_id, contract_no, party_id, party_code, party_name, title,
                   start_date, end_date, status, currency_code, payment_terms,
                   guarantee_text, guarantee_amount, renewal_of_id, notes
            FROM sct_contracts
            WHERE company_id = @c AND {NotDeleted}
            """;
        if (onlyId is not null) sql += " AND contract_id = @id";
        if (!string.IsNullOrWhiteSpace(status)) sql += " AND status = @status";
        sql += " ORDER BY contract_no";
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, "@c", company);
        if (onlyId is not null) Add(command, "@id", FormatGuid(onlyId.Value));
        if (!string.IsNullOrWhiteSpace(status)) Add(command, "@status", status.Trim());
        var result = new List<SalesContract>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SalesContract
            {
                Id = ParseGuid(ReadString(reader, 0) ?? string.Empty),
                ContractNo = ReadString(reader, 1) ?? string.Empty,
                PartyId = ReadLongOrNull(reader, 2),
                PartyCode = ReadString(reader, 3),
                PartyName = ReadString(reader, 4),
                Title = ReadString(reader, 5) ?? string.Empty,
                StartDate = ReadString(reader, 6) ?? string.Empty,
                EndDate = ReadString(reader, 7) ?? string.Empty,
                Status = ReadString(reader, 8) ?? ContractStatuses.Draft,
                CurrencyCode = ReadString(reader, 9) ?? "SAR",
                PaymentTerms = ReadString(reader, 10),
                GuaranteeText = ReadString(reader, 11),
                GuaranteeAmount = ReadDecimalOrNull(reader, 12) ?? 0m,
                RenewalOfId = ReadString(reader, 13) is { } renewal ? ParseGuid(renewal) : null,
                Notes = ReadString(reader, 14)
            });
        }
        return result;
    }

    private async Task<List<SalesContractLine>> ReadLinesAsync(
        DbConnection connection, DbTransaction? transaction, string company,
        Guid contractId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT line_id, line_no, material_id, material_code, description, unit,
                   contracted_qty, unit_price, delivered_qty, invoiced_qty,
                   next_delivery_date, interval_days, scheduled_qty
            FROM sct_contract_lines
            WHERE company_id = @c AND contract_id = @id AND {NotDeleted}
            ORDER BY line_no
            """);
        Add(command, "@c", company);
        Add(command, "@id", FormatGuid(contractId));
        var result = new List<SalesContractLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SalesContractLine
            {
                Id = ParseGuid(ReadString(reader, 0) ?? string.Empty),
                ContractId = contractId,
                LineNo = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                MaterialId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                MaterialCode = ReadString(reader, 3),
                Description = ReadString(reader, 4),
                Unit = ReadString(reader, 5),
                ContractedQty = ReadDecimalOrNull(reader, 6) ?? 0m,
                UnitPrice = ReadDecimalOrNull(reader, 7) ?? 0m,
                DeliveredQty = ReadDecimalOrNull(reader, 8) ?? 0m,
                InvoicedQty = ReadDecimalOrNull(reader, 9) ?? 0m,
                NextDeliveryDate = ReadString(reader, 10),
                IntervalDays = reader.IsDBNull(11) ? null : Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture),
                ScheduledQty = ReadDecimalOrNull(reader, 12) ?? 0m
            });
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Save / delete
    // ------------------------------------------------------------------
    public async Task SaveContractAsync(string companyId, SalesContract contract, string userId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(contract);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = await ExistsAsync(connection, transaction,
                $"SELECT COUNT(*) FROM sct_contracts WHERE company_id = @c AND contract_id = @id AND {NotDeleted}",
                company, FormatGuid(contract.Id), cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                if (string.IsNullOrWhiteSpace(contract.ContractNo))
                    contract.ContractNo = await NextContractNoAsync(connection, transaction, company, cancellationToken).ConfigureAwait(false);
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO sct_contracts
                        (contract_id, company_id, contract_no, party_id, party_code, party_name, title,
                         start_date, end_date, status, currency_code, payment_terms, guarantee_text,
                         guarantee_amount, renewal_of_id, notes, created_by)
                    VALUES (@id, @c, @no, @party, @partyCode, @partyName, @title,
                            @start, @end, @status, @currency, @terms, @guarantee,
                            @guaranteeAmount, @renewalOf, @notes, @user)
                    """);
                BindHeader(insert, company, contract, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await InsertEventAsync(connection, transaction, company, contract.Id, "CREATED", null, contract.Status, contract.ContractNo, userId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                string? currentStatus;
                await using (var read = CreateCommand(connection, transaction,
                    $"SELECT status FROM sct_contracts WHERE company_id = @c AND contract_id = @id AND {NotDeleted}"))
                {
                    Add(read, "@c", company);
                    Add(read, "@id", FormatGuid(contract.Id));
                    currentStatus = Convert.ToString(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                if (currentStatus != ContractStatuses.Draft && currentStatus != ContractStatuses.Suspended)
                    throw new InvalidOperationException(
                        $"Contract '{contract.ContractNo}' is '{currentStatus}'; only Draft or Suspended contracts can be edited.");
                await using var update = CreateCommand(connection, transaction, $"""
                    UPDATE sct_contracts SET party_id = @party, party_code = @partyCode, party_name = @partyName,
                        title = @title, start_date = @start, end_date = @end, currency_code = @currency,
                        payment_terms = @terms, guarantee_text = @guarantee, guarantee_amount = @guaranteeAmount,
                        notes = @notes, updated_by = @user, updated_at = {NowExpr}
                    WHERE company_id = @c AND contract_id = @id AND {NotDeleted}
                    """);
                BindHeader(update, company, contract, userId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await InsertEventAsync(connection, transaction, company, contract.Id, "UPDATED", currentStatus, currentStatus, contract.ContractNo, userId, cancellationToken).ConfigureAwait(false);
            }

            // Lines are replaced wholesale (the CommRepository tiers
            // pattern): the editor owns the full line list.
            await using (var wipe = CreateCommand(connection, transaction,
                "DELETE FROM sct_contract_lines WHERE company_id = @c AND contract_id = @id"))
            {
                Add(wipe, "@c", company);
                Add(wipe, "@id", FormatGuid(contract.Id));
                await wipe.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var lineNo = 0;
            foreach (var line in contract.Lines)
            {
                lineNo++;
                line.LineNo = lineNo;
                line.ContractId = contract.Id;
                if (line.Id == Guid.Empty) line.Id = Guid.NewGuid();
                await using var insertLine = CreateCommand(connection, transaction, """
                    INSERT INTO sct_contract_lines
                        (line_id, company_id, contract_id, line_no, material_id, material_code, description,
                         unit, contracted_qty, unit_price, next_delivery_date, interval_days, scheduled_qty, created_by)
                    VALUES (@id, @c, @contract, @lineNo, @material, @matCode, @desc,
                            @unit, @qty, @price, @next, @interval, @schedQty, @user)
                    """);
                Add(insertLine, "@id", FormatGuid(line.Id));
                Add(insertLine, "@c", company);
                Add(insertLine, "@contract", FormatGuid(contract.Id));
                Add(insertLine, "@lineNo", lineNo);
                Add(insertLine, "@material", line.MaterialId);
                Add(insertLine, "@matCode", (object?)line.MaterialCode ?? DBNull.Value);
                Add(insertLine, "@desc", (object?)line.Description ?? DBNull.Value);
                Add(insertLine, "@unit", (object?)line.Unit ?? DBNull.Value);
                Add(insertLine, "@qty", line.ContractedQty);
                Add(insertLine, "@price", line.UnitPrice);
                Add(insertLine, "@next", (object?)line.NextDeliveryDate ?? DBNull.Value);
                Add(insertLine, "@interval", (object?)line.IntervalDays ?? DBNull.Value);
                Add(insertLine, "@schedQty", line.ScheduledQty);
                Add(insertLine, "@user", userId);
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

    public async Task SoftDeleteContractAsync(string companyId, Guid contractId, string userId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contract = (await ReadContractsAsync(connection, transaction, company, contractId, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new InvalidOperationException("Contract was not found.");
            if (contract.Status != ContractStatuses.Draft)
                throw new InvalidOperationException("Only Draft contracts can be deleted.");
            await using var update = CreateCommand(connection, transaction, $"""
                UPDATE sct_contracts SET is_deleted = @t, updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @c AND contract_id = @id AND {NotDeleted}
                """);
            Add(update, "@t", Truth(true));
            Add(update, "@user", userId);
            Add(update, "@c", company);
            Add(update, "@id", FormatGuid(contractId));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, company, contractId, "DELETED", contract.Status, null, contract.ContractNo, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Status / renewal / expiry
    // ------------------------------------------------------------------
    public async Task SetStatusAsync(string companyId, Guid contractId, string toStatus, string userId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contract = (await ReadContractsAsync(connection, transaction, company, contractId, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new InvalidOperationException("Contract was not found.");
            if (!ContractTransitions.IsAllowed(contract.Status, toStatus))
                throw new InvalidOperationException($"Contract status cannot move from '{contract.Status}' to '{toStatus}'.");
            await UpdateStatusAsync(connection, transaction, company, contractId, toStatus, userId, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, company, contractId, "STATUS", contract.Status, toStatus, null, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SalesContract> RenewAsync(string companyId, Guid contractId, string userId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = (await ReadContractsAsync(connection, transaction, company, contractId, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new InvalidOperationException("Contract was not found.");
            if (source.Status is not (ContractStatuses.Active or ContractStatuses.Expired or ContractStatuses.Completed))
                throw new InvalidOperationException($"Only an Active, Expired or Completed contract can be renewed; this contract is '{source.Status}'.");
            source.Lines = await ReadLinesAsync(connection, transaction, company, contractId, cancellationToken).ConfigureAwait(false);

            // Successor window: starts the day after the old contract
            // ends and runs for the old contract's own span.
            var oldStart = DateOnly.Parse(source.StartDate, CultureInfo.InvariantCulture);
            var oldEnd = DateOnly.Parse(source.EndDate, CultureInfo.InvariantCulture);
            var span = oldEnd.DayNumber - oldStart.DayNumber;
            var newStart = oldEnd.AddDays(1);
            var successor = new SalesContract
            {
                Id = Guid.NewGuid(),
                ContractNo = await NextContractNoAsync(connection, transaction, company, cancellationToken).ConfigureAwait(false),
                PartyId = source.PartyId,
                PartyCode = source.PartyCode,
                PartyName = source.PartyName,
                Title = source.Title,
                StartDate = newStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                EndDate = newStart.AddDays(span).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Status = ContractStatuses.Draft,
                CurrencyCode = source.CurrencyCode,
                PaymentTerms = source.PaymentTerms,
                GuaranteeText = source.GuaranteeText,
                GuaranteeAmount = source.GuaranteeAmount,
                RenewalOfId = source.Id,
                Notes = source.Notes,
                Lines = source.Lines.Select(l => new SalesContractLine
                {
                    Id = Guid.NewGuid(),
                    MaterialId = l.MaterialId,
                    MaterialCode = l.MaterialCode,
                    Description = l.Description,
                    Unit = l.Unit,
                    ContractedQty = l.ContractedQty,
                    UnitPrice = l.UnitPrice,
                    NextDeliveryDate = l.NextDeliveryDate is null
                        ? null
                        : newStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    IntervalDays = l.IntervalDays,
                    ScheduledQty = l.ScheduledQty
                }).ToList()
            };

            await using (var insert = CreateCommand(connection, transaction, """
                INSERT INTO sct_contracts
                    (contract_id, company_id, contract_no, party_id, party_code, party_name, title,
                     start_date, end_date, status, currency_code, payment_terms, guarantee_text,
                     guarantee_amount, renewal_of_id, notes, created_by)
                VALUES (@id, @c, @no, @party, @partyCode, @partyName, @title,
                        @start, @end, @status, @currency, @terms, @guarantee,
                        @guaranteeAmount, @renewalOf, @notes, @user)
                """))
            {
                BindHeader(insert, company, successor, userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var lineNo = 0;
            foreach (var line in successor.Lines)
            {
                lineNo++;
                await using var insertLine = CreateCommand(connection, transaction, """
                    INSERT INTO sct_contract_lines
                        (line_id, company_id, contract_id, line_no, material_id, material_code, description,
                         unit, contracted_qty, unit_price, next_delivery_date, interval_days, scheduled_qty, created_by)
                    VALUES (@id, @c, @contract, @lineNo, @material, @matCode, @desc,
                            @unit, @qty, @price, @next, @interval, @schedQty, @user)
                    """);
                Add(insertLine, "@id", FormatGuid(line.Id));
                Add(insertLine, "@c", company);
                Add(insertLine, "@contract", FormatGuid(successor.Id));
                Add(insertLine, "@lineNo", lineNo);
                Add(insertLine, "@material", line.MaterialId);
                Add(insertLine, "@matCode", (object?)line.MaterialCode ?? DBNull.Value);
                Add(insertLine, "@desc", (object?)line.Description ?? DBNull.Value);
                Add(insertLine, "@unit", (object?)line.Unit ?? DBNull.Value);
                Add(insertLine, "@qty", line.ContractedQty);
                Add(insertLine, "@price", line.UnitPrice);
                Add(insertLine, "@next", (object?)line.NextDeliveryDate ?? DBNull.Value);
                Add(insertLine, "@interval", (object?)line.IntervalDays ?? DBNull.Value);
                Add(insertLine, "@schedQty", line.ScheduledQty);
                Add(insertLine, "@user", userId);
                await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await UpdateStatusAsync(connection, transaction, company, contractId, ContractStatuses.Renewed, userId, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, company, contractId, "RENEWED", source.Status, ContractStatuses.Renewed, successor.ContractNo, userId, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, company, successor.Id, "CREATED", null, ContractStatuses.Draft, $"Renewal of {source.ContractNo}", userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return successor;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<SalesContract>> GetExpiringAsync(string companyId, int withinDays, string asOfDate, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        var asOf = DateOnly.Parse(asOfDate, CultureInfo.InvariantCulture);
        var until = asOf.AddDays(Math.Max(0, withinDays)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT contract_id FROM sct_contracts
            WHERE company_id = @c AND status = 'Active' AND {NotDeleted}
              AND end_date >= @from AND end_date <= @until
            ORDER BY end_date
            """);
        Add(command, "@c", company);
        Add(command, "@from", asOfDate);
        Add(command, "@until", until);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(ParseGuid(ReadString(reader, 0) ?? string.Empty));
        }
        var result = new List<SalesContract>();
        foreach (var id in ids)
        {
            var contract = await GetContractAsync(company, id, cancellationToken).ConfigureAwait(false);
            if (contract is not null) result.Add(contract);
        }
        return result;
    }

    public async Task<int> ExpireOverdueAsync(string companyId, string asOfDate, string userId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ids = new List<Guid>();
            await using (var read = CreateCommand(connection, transaction, $"""
                SELECT contract_id FROM sct_contracts
                WHERE company_id = @c AND status = 'Active' AND {NotDeleted} AND end_date < @asOf
                """))
            {
                Add(read, "@c", company);
                Add(read, "@asOf", asOfDate);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    ids.Add(ParseGuid(ReadString(reader, 0) ?? string.Empty));
            }
            foreach (var id in ids)
            {
                await UpdateStatusAsync(connection, transaction, company, id, ContractStatuses.Expired, userId, cancellationToken).ConfigureAwait(false);
                await InsertEventAsync(connection, transaction, company, id, "STATUS", ContractStatuses.Active, ContractStatuses.Expired, "Validity window passed", userId, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ids.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Progress refresh (stored draw-down maintenance)
    // ------------------------------------------------------------------
    public async Task RefreshProgressAsync(string companyKey, Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var company = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, companyKey.Trim())
                ?? throw new InvalidOperationException("Company was not found.");
            await RefreshProgressInTransactionAsync(connection, transaction, company, companyKey.Trim(), contractId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task RefreshProgressInTransactionAsync(
        DbConnection connection, DbTransaction transaction, string companyCode, string companyKey,
        Guid contractId, CancellationToken cancellationToken)
    {
        var delivered = await LinkedQtyByMaterialAsync(connection, transaction, companyKey, contractId, "DLV", cancellationToken).ConfigureAwait(false);
        var invoiced = await LinkedQtyByMaterialAsync(connection, transaction, companyKey, contractId, "SAL", cancellationToken).ConfigureAwait(false);
        var lines = await ReadLinesAsync(connection, transaction, companyCode, contractId, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            await using var update = CreateCommand(connection, transaction, """
                UPDATE sct_contract_lines SET delivered_qty = @delivered, invoiced_qty = @invoiced
                WHERE company_id = @c AND line_id = @id
                """);
            Add(update, "@delivered", delivered.GetValueOrDefault(line.MaterialId));
            Add(update, "@invoiced", invoiced.GetValueOrDefault(line.MaterialId));
            Add(update, "@c", companyCode);
            Add(update, "@id", FormatGuid(line.Id));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Posted quantities by material on chain documents of
    /// <paramref name="targetKind"/> (DLV delivered / SAL invoiced)
    /// linked from this contract's sales orders.</summary>
    private async Task<Dictionary<long, decimal>> LinkedQtyByMaterialAsync(
        DbConnection connection, DbTransaction transaction, string companyKey,
        Guid contractId, string targetKind, CancellationToken cancellationToken)
    {
        var keys = SalesPriceResolver.CompanyKeys(connection, transaction, _provider, companyKey);
        var inList = string.Join(", ", keys.Select((_, i) => $"@co{i}"));
        await using var command = CreateCommand(connection, transaction, $"""
            WITH RECURSIVE contract_docs (company_id, doc_id, doc_kind) AS (
                SELECT o.company_id, o.doc_id, o.doc_kind
                FROM sales_documents o
                WHERE o.company_id IN ({inList}) AND o.doc_kind = 'SO'
                  AND o.is_deleted = 0 AND o.contract_id = @contract
                UNION
                SELECT dl.company_id, dl.target_doc_id, dl.target_doc_kind
                FROM document_links dl
                JOIN contract_docs source
                  ON source.company_id = dl.company_id
                 AND source.doc_id = dl.source_doc_id
                 AND source.doc_kind = dl.source_doc_kind
            )
            SELECT l.material_id, SUM(l.qty)
            FROM contract_docs chain
            JOIN sales_documents t
              ON t.doc_id = chain.doc_id AND t.company_id = chain.company_id
             AND t.doc_kind = chain.doc_kind
             AND t.doc_kind = @kind AND t.status = 'Posted' AND t.is_deleted = 0
            JOIN sales_document_lines l
              ON l.doc_id = t.doc_id AND l.company_id = t.company_id
            WHERE l.material_id IS NOT NULL
            GROUP BY l.material_id
            """);
        for (var i = 0; i < keys.Count; i++) Add(command, $"@co{i}", keys[i]);
        Add(command, "@kind", targetKind);
        Add(command, "@contract", FormatGuid(contractId));
        var result = new Dictionary<long, decimal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                ReadDecimalOrNull(reader, 1) ?? 0m;
        return result;
    }

    // ------------------------------------------------------------------
    // Fulfillment + profitability
    // ------------------------------------------------------------------
    public async Task<ContractFulfillment?> GetFulfillmentAsync(string companyId, Guid contractId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        await RefreshProgressAsync(company, contractId, cancellationToken).ConfigureAwait(false);
        var contract = await GetContractAsync(company, contractId, cancellationToken).ConfigureAwait(false);
        if (contract is null) return null;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var keys = SalesPriceResolver.CompanyKeys(connection, null!, _provider, company);
        var inList = string.Join(", ", keys.Select((_, i) => $"@co{i}"));

        // Revenue: posted SAL documents linked from the contract's
        // orders (stored line totals). COGS: the valued stock
        // movements those invoices (and their linked deliveries, whose
        // movements carry chain-invoice COGS — the W7b pattern) wrote.
        var docNos = new List<string>();
        decimal revenue = 0m;
        await using (var invoices = CreateCommand(connection, null, $"""
            WITH RECURSIVE contract_docs (company_id, doc_id, doc_kind) AS (
                SELECT o.company_id, o.doc_id, o.doc_kind
                FROM sales_documents o
                WHERE o.company_id IN ({inList}) AND o.doc_kind = 'SO'
                  AND o.is_deleted = 0 AND o.contract_id = @contract
                UNION
                SELECT dl.company_id, dl.target_doc_id, dl.target_doc_kind
                FROM document_links dl
                JOIN contract_docs source
                  ON source.company_id = dl.company_id
                 AND source.doc_id = dl.source_doc_id
                 AND source.doc_kind = dl.source_doc_kind
            )
            SELECT t.doc_no, SUM(l.line_total)
            FROM contract_docs chain
            JOIN sales_documents t
              ON t.doc_id = chain.doc_id AND t.company_id = chain.company_id
             AND t.doc_kind = chain.doc_kind
             AND t.doc_kind = 'SAL' AND t.status = 'Posted' AND t.is_deleted = 0
            JOIN sales_document_lines l
              ON l.doc_id = t.doc_id AND l.company_id = t.company_id
            GROUP BY t.doc_no
            """))
        {
            for (var i = 0; i < keys.Count; i++) Add(invoices, $"@co{i}", keys[i]);
            Add(invoices, "@contract", FormatGuid(contractId));
            await using var reader = await invoices.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ReadString(reader, 0) is { } no) docNos.Add(no);
                revenue += ReadDecimalOrNull(reader, 1) ?? 0m;
            }
        }
        await using (var deliveries = CreateCommand(connection, null, $"""
            WITH RECURSIVE contract_docs (company_id, doc_id, doc_kind) AS (
                SELECT o.company_id, o.doc_id, o.doc_kind
                FROM sales_documents o
                WHERE o.company_id IN ({inList}) AND o.doc_kind = 'SO'
                  AND o.is_deleted = 0 AND o.contract_id = @contract
                UNION
                SELECT dl.company_id, dl.target_doc_id, dl.target_doc_kind
                FROM document_links dl
                JOIN contract_docs source
                  ON source.company_id = dl.company_id
                 AND source.doc_id = dl.source_doc_id
                 AND source.doc_kind = dl.source_doc_kind
            )
            SELECT d.doc_no
            FROM contract_docs chain
            JOIN sales_documents d
              ON d.company_id = chain.company_id AND d.doc_id = chain.doc_id
             AND d.doc_kind = chain.doc_kind
            WHERE d.doc_kind = 'DLV' AND d.status = 'Posted' AND d.is_deleted = 0
            """))
        {
            for (var i = 0; i < keys.Count; i++) Add(deliveries, $"@co{i}", keys[i]);
            Add(deliveries, "@contract", FormatGuid(contractId));
            await using var reader = await deliveries.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ReadString(reader, 0) is { } no) docNos.Add(no);
            }
        }

        decimal cogs = 0m;
        foreach (var docNo in docNos.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var movements = CreateCommand(connection, null, """
                SELECT COALESCE(SUM(-cost_amount), 0) FROM material_stock_movements
                WHERE company_id = @c AND document_no = @no AND qty_out > 0 AND cost_amount IS NOT NULL
                """);
            Add(movements, "@c", company);
            Add(movements, "@no", docNo);
            var value = await movements.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            cogs += Math.Max(0m, value is null or DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }

        return new ContractFulfillment
        {
            ContractId = contract.Id,
            ContractNo = contract.ContractNo,
            ContractedQty = contract.Lines.Sum(l => l.ContractedQty),
            DeliveredQty = contract.Lines.Sum(l => l.DeliveredQty),
            InvoicedQty = contract.Lines.Sum(l => l.InvoicedQty),
            RemainingQty = contract.Lines.Sum(l => l.RemainingQty),
            ContractedAmount = contract.Lines.Sum(l => l.ContractedQty * l.UnitPrice),
            DeliveredAmount = contract.Lines.Sum(l => l.DeliveredQty * l.UnitPrice),
            InvoicedAmount = contract.Lines.Sum(l => l.InvoicedQty * l.UnitPrice),
            Revenue = revenue,
            Cogs = cogs
        };
    }

    // ------------------------------------------------------------------
    // Scheduled delivery generation
    // ------------------------------------------------------------------
    /// <summary>
    /// For every Active contract line whose next_delivery_date is due,
    /// generates ONE DRAFT sales order per (contract, due date) for the
    /// scheduled quantities (capped at each line's remaining contracted
    /// quantity) and advances the cadence. The chain requires an order
    /// before a delivery note, so the generated document is a draft
    /// order carrying contract_id; converting it to a delivery follows
    /// the normal chain. Idempotent per period: the generated order's
    /// notes carry the marker SCT-SCHED:{contract_no}:{due_date} and a
    /// re-run for the same period finds it and skips.
    /// </summary>
    public async Task<int> GenerateDueDeliveriesAsync(string companyKey, string asOfDate, string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("User is required.", nameof(userId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var companyCode = PartyLedger.ResolveCompanyCode(connection, transaction, _provider, companyKey.Trim())
                ?? throw new InvalidOperationException("Company was not found.");
            var companyGuid = await ResolveCompanyGuidAsync(connection, transaction, companyCode, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Company was not found.");

            var due = new List<(SalesContract Contract, SalesContractLine Line)>();
            await using (var read = CreateCommand(connection, transaction, $"""
                SELECT c.contract_id FROM sct_contracts c
                JOIN sct_contract_lines l ON l.contract_id = c.contract_id AND l.company_id = c.company_id AND l.{NotDeleted}
                WHERE c.company_id = @c AND c.status = 'Active' AND c.{NotDeleted}
                  AND l.next_delivery_date IS NOT NULL AND l.next_delivery_date <= @asOf AND l.scheduled_qty > 0
                GROUP BY c.contract_id
                """))
            {
                Add(read, "@c", companyCode);
                Add(read, "@asOf", asOfDate);
                var ids = new List<Guid>();
                await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        ids.Add(ParseGuid(ReadString(reader, 0) ?? string.Empty));
                }
                foreach (var id in ids)
                {
                    var contract = (await ReadContractsAsync(connection, transaction, companyCode, id, null, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                    if (contract is null) continue;
                    contract.Lines = await ReadLinesAsync(connection, transaction, companyCode, id, cancellationToken).ConfigureAwait(false);
                    foreach (var line in contract.Lines)
                    {
                        if (line.NextDeliveryDate is not null
                            && string.CompareOrdinal(line.NextDeliveryDate, asOfDate) <= 0
                            && line.ScheduledQty > 0m)
                            due.Add((contract, line));
                    }
                }
            }

            var created = 0;
            foreach (var group in due.GroupBy(d => d.Contract.Id))
            {
                var contract = group.First().Contract;
                // Refresh stored progress first so the cap uses the
                // chain's real delivered / invoiced quantities.
                await RefreshProgressInTransactionAsync(connection, transaction, companyCode, companyGuid, contract.Id, cancellationToken).ConfigureAwait(false);
                var freshLines = await ReadLinesAsync(connection, transaction, companyCode, contract.Id, cancellationToken).ConfigureAwait(false);
                var dueLines = group.Select(g => freshLines.First(l => l.Id == g.Line.Id))
                    .Where(l => l.RemainingQty > 0m).ToList();
                if (dueLines.Count == 0) continue;

                var dueDate = dueLines.Min(l => l.NextDeliveryDate!);
                var marker = $"SCT-SCHED:{contract.ContractNo}:{dueDate}";
                var already = await ExistsTextAsync(connection, transaction,
                    "SELECT COUNT(*) FROM sales_documents WHERE contract_id = @contract AND notes = @marker AND is_deleted = 0",
                    ("@contract", FormatGuid(contract.Id)), ("@marker", marker), cancellationToken).ConfigureAwait(false);
                if (!already)
                {
                    var docNo = await NextSalesDocNoAsync(connection, transaction, companyGuid, "SO", "SO", cancellationToken).ConfigureAwait(false);
                    var orderLines = dueLines.Select(l => new
                    {
                        Line = l,
                        Qty = Math.Min(l.ScheduledQty, l.RemainingQty)
                    }).Where(x => x.Qty > 0m).ToList();
                    if (orderLines.Count == 0) continue;
                    var subtotal = orderLines.Sum(x => decimal.Round(x.Qty * x.Line.UnitPrice, 2));
                    long docId;
                    await using (var insert = CreateCommand(connection, transaction, $"""
                        INSERT INTO sales_documents
                            (company_id, doc_kind, doc_no, doc_date, party_id, party_name, party_code,
                             currency_code, exchange_rate, subtotal, discount_amount, tax_amount, grand_total,
                             status, notes, created_by, contract_id)
                        VALUES (@company, 'SO', @no, @date, @party, @partyName, @partyCode,
                                @currency, 1, @subtotal, 0, 0, @subtotal, 'Draft', @marker, @user, @contract)
                        {(IsPg ? "RETURNING doc_id" : "; SELECT last_insert_rowid();")}
                        """))
                    {
                        Add(insert, "@company", companyGuid);
                        Add(insert, "@no", docNo);
                        Add(insert, "@date", (object?)dueDate ?? DBNull.Value);
                        Add(insert, "@party", (object?)contract.PartyId ?? DBNull.Value);
                        Add(insert, "@partyName", (object?)contract.PartyName ?? DBNull.Value);
                        Add(insert, "@partyCode", (object?)contract.PartyCode ?? DBNull.Value);
                        Add(insert, "@currency", contract.CurrencyCode);
                        Add(insert, "@subtotal", subtotal);
                        Add(insert, "@marker", marker);
                        Add(insert, "@user", userId.Trim());
                        Add(insert, "@contract", FormatGuid(contract.Id));
                        docId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                    }
                    var lineNo = 0;
                    foreach (var x in orderLines)
                    {
                        lineNo++;
                        var lineTotal = decimal.Round(x.Qty * x.Line.UnitPrice, 2);
                        await using var insertLine = CreateCommand(connection, transaction, """
                            INSERT INTO sales_document_lines
                                (doc_id, line_no, material_id, material_code, description, unit, qty,
                                 unit_price, discount_pct, tax_rate, line_subtotal, line_tax, line_total)
                            VALUES (@doc, @lineNo, @material, @matCode, @desc, @unit, @qty,
                                    @price, 0, 0, @total, 0, @total)
                            """);
                        Add(insertLine, "@doc", docId);
                        Add(insertLine, "@lineNo", lineNo);
                        Add(insertLine, "@material", x.Line.MaterialId);
                        Add(insertLine, "@matCode", (object?)x.Line.MaterialCode ?? DBNull.Value);
                        Add(insertLine, "@desc", x.Line.Description ?? x.Line.MaterialCode ?? string.Empty);
                        Add(insertLine, "@unit", (object?)x.Line.Unit ?? DBNull.Value);
                        Add(insertLine, "@qty", x.Qty);
                        Add(insertLine, "@price", x.Line.UnitPrice);
                        Add(insertLine, "@total", lineTotal);
                        await insertLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await InsertEventAsync(connection, transaction, companyCode, contract.Id, "SCHEDULED", contract.Status, contract.Status, docNo, userId, cancellationToken).ConfigureAwait(false);
                    created++;
                }

                // Advance the cadence for every due line of this
                // contract (also when the period was already generated:
                // the marker check above is what prevents duplicates).
                foreach (var line in dueLines)
                {
                    var next = line.IntervalDays is > 0
                        ? DateOnly.Parse(line.NextDeliveryDate!, CultureInfo.InvariantCulture)
                            .AddDays(line.IntervalDays.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : null;
                    await using var advance = CreateCommand(connection, transaction, """
                        UPDATE sct_contract_lines SET next_delivery_date = @next
                        WHERE company_id = @c AND line_id = @id
                        """);
                    Add(advance, "@next", (object?)next ?? DBNull.Value);
                    Add(advance, "@c", companyCode);
                    Add(advance, "@id", FormatGuid(line.Id));
                    await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private void BindHeader(DbCommand command, string company, SalesContract contract, string userId)
    {
        Add(command, "@id", FormatGuid(contract.Id));
        Add(command, "@c", company);
        Add(command, "@no", contract.ContractNo.Trim());
        Add(command, "@party", (object?)contract.PartyId ?? DBNull.Value);
        Add(command, "@partyCode", (object?)contract.PartyCode ?? DBNull.Value);
        Add(command, "@partyName", (object?)contract.PartyName ?? DBNull.Value);
        Add(command, "@title", contract.Title.Trim());
        Add(command, "@start", contract.StartDate.Trim());
        Add(command, "@end", contract.EndDate.Trim());
        Add(command, "@status", contract.Status);
        Add(command, "@currency", string.IsNullOrWhiteSpace(contract.CurrencyCode) ? "SAR" : contract.CurrencyCode.Trim());
        Add(command, "@terms", (object?)contract.PaymentTerms ?? DBNull.Value);
        Add(command, "@guarantee", (object?)contract.GuaranteeText ?? DBNull.Value);
        Add(command, "@guaranteeAmount", contract.GuaranteeAmount);
        Add(command, "@renewalOf", (object?)(contract.RenewalOfId is Guid g ? FormatGuid(g) : null) ?? DBNull.Value);
        Add(command, "@notes", (object?)contract.Notes ?? DBNull.Value);
        Add(command, "@user", userId);
    }

    private async Task UpdateStatusAsync(DbConnection connection, DbTransaction transaction, string company, Guid contractId, string toStatus, string userId, CancellationToken cancellationToken)
    {
        await using var update = CreateCommand(connection, transaction, $"""
            UPDATE sct_contracts SET status = @status, updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND contract_id = @id AND {NotDeleted}
            """);
        Add(update, "@status", toStatus);
        Add(update, "@user", userId);
        Add(update, "@c", company);
        Add(update, "@id", FormatGuid(contractId));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Contract was not found while changing status.");
    }

    private async Task InsertEventAsync(DbConnection connection, DbTransaction transaction, string company, Guid contractId,
        string kind, string? fromStatus, string? toStatus, string? detail, string userId, CancellationToken cancellationToken)
    {
        await using var insert = CreateCommand(connection, transaction, """
            INSERT INTO sct_contract_events (event_id, company_id, contract_id, event_kind, from_status, to_status, detail, created_by)
            VALUES (@id, @c, @contract, @kind, @from, @to, @detail, @user)
            """);
        Add(insert, "@id", FormatGuid(Guid.NewGuid()));
        Add(insert, "@c", company);
        Add(insert, "@contract", FormatGuid(contractId));
        Add(insert, "@kind", kind);
        Add(insert, "@from", (object?)fromStatus ?? DBNull.Value);
        Add(insert, "@to", (object?)toStatus ?? DBNull.Value);
        Add(insert, "@detail", (object?)detail ?? DBNull.Value);
        Add(insert, "@user", userId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Next contract number: the sales-document numbering
    /// pattern (prefix + max numeric suffix + 1, starting at 1001),
    /// scoped per company, inside the caller's transaction.</summary>
    private async Task<string> NextContractNoAsync(DbConnection connection, DbTransaction transaction, string company, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            $"SELECT contract_no FROM sct_contracts WHERE company_id = @c AND {NotDeleted}");
        Add(command, "@c", company);
        var max = 1000;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = ReadString(reader, 0) ?? string.Empty;
            var dash = text.LastIndexOf('-');
            var suffix = dash >= 0 ? text[(dash + 1)..] : text;
            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > max)
                max = value;
        }
        return $"SCT-{max + 1}";
    }

    private async Task<string> NextSalesDocNoAsync(DbConnection connection, DbTransaction transaction, string companyGuid, string docKind, string prefix, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT doc_no FROM sales_documents WHERE company_id = @company AND doc_kind = @kind");
        Add(command, "@company", companyGuid);
        Add(command, "@kind", docKind);
        var max = 1000;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = ReadString(reader, 0) ?? string.Empty;
            var dash = text.LastIndexOf('-');
            var suffix = dash >= 0 ? text[(dash + 1)..] : text;
            if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > max)
                max = value;
        }
        return $"{prefix}-{max + 1}";
    }

    private async Task<string?> ResolveCompanyGuidAsync(DbConnection connection, DbTransaction transaction, string companyCode, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @code LIMIT 1");
        Add(command, "@code", companyCode);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction, string sql, string company, string id, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, "@c", company);
        Add(command, "@id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) > 0;
    }

    private async Task<bool> ExistsTextAsync(DbConnection connection, DbTransaction transaction, string sql,
        (string Name, object Value) first, (string Name, object Value) second, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        Add(command, first.Name, first.Value);
        Add(command, second.Name, second.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) > 0;
    }

    private DbConnection CreateConnection()
        => IsPg ? new NpgsqlConnection(_connectionString) : new SqliteConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
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

    private static string RequireCompany(string companyId)
        => string.IsNullOrWhiteSpace(companyId)
            ? throw new ArgumentException("Company is required.", nameof(companyId))
            : companyId.Trim();

    internal static string FormatGuid(Guid value) => value.ToString("D");
    internal static Guid ParseGuid(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal? ReadDecimalOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long? ReadLongOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

/// <summary>Allowed contract status transitions (service + repository
/// share this one map).</summary>
public static class ContractTransitions
{
    public static bool IsAllowed(string from, string to)
        => (from, to) switch
        {
            (ContractStatuses.Draft, ContractStatuses.Active) => true,
            (ContractStatuses.Active, ContractStatuses.Suspended) => true,
            (ContractStatuses.Suspended, ContractStatuses.Active) => true,
            (ContractStatuses.Active, ContractStatuses.Expired) => true,
            (ContractStatuses.Active, ContractStatuses.Completed) => true,
            (ContractStatuses.Suspended, ContractStatuses.Expired) => true,
            _ => false
        };
}

/// <summary>
/// The real contract price source for the W7a pricing engine
/// (resolution level (a)): an ACTIVE contract of the document's party,
/// inside its validity window on the document date, whose currency
/// matches (or the document carries none), prices the material at the
/// contracted unit price. Plugged in wherever
/// <see cref="NullContractPriceSource"/> was used.
/// </summary>
public sealed class ContractPriceSource : IContractPriceSource
{
    public static readonly ContractPriceSource Instance = new();

    public decimal? TryGetPrice(
        DbConnection connection, DbTransaction transaction, string provider,
        IReadOnlyList<string> companyKeys, string? partyCode,
        long materialId, string materialCode, string? unit, string? currency, string asOfDate)
    {
        if (companyKeys.Count == 0 || string.IsNullOrWhiteSpace(partyCode) || materialId <= 0)
            return null;
        if (!PartyLedger.TableExists(connection, transaction, provider, "sct_contracts"))
            return null;
        var isPg = SalesPriceResolver.IsPg(provider);
        var notDeleted = isPg ? "is_deleted = FALSE" : "is_deleted = 0";
        var inList = string.Join(", ", companyKeys.Select((_, i) => $"@co{i}"));
        var partyMatch = "c.party_code = @party";
        if (PartyLedger.TableExists(connection, transaction, provider, "parties")
            && PartyLedger.ColumnExists(connection, transaction, provider, "parties", "party_id"))
        {
            partyMatch += $" OR c.party_id IN (SELECT party_id FROM parties WHERE company_id IN ({inList}) AND code = @party AND {notDeleted})";
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT l.unit_price FROM sct_contract_lines l
            JOIN sct_contracts c ON c.contract_id = l.contract_id AND c.company_id = l.company_id
            WHERE c.company_id IN ({inList}) AND c.status = 'Active' AND c.{notDeleted} AND l.{notDeleted}
              AND l.material_id = @mat AND l.unit_price > 0
              AND c.start_date <= @date AND c.end_date >= @date
              AND ({partyMatch})
              AND (@currency = '' OR c.currency_code = @currency)
            ORDER BY c.start_date DESC
            LIMIT 1
            """;
        void Add(string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        for (var i = 0; i < companyKeys.Count; i++) Add($"@co{i}", companyKeys[i]);
        Add("@party", partyCode.Trim());
        Add("@mat", materialId);
        Add("@date", asOfDate);
        Add("@currency", currency?.Trim() ?? string.Empty);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Contract draw-down guard (spec section 10): runs inside the order
/// repository's save / post transactions for sales orders carrying a
/// contract_id. The contract must be Active and cover the document
/// date (else SAL-7023 ContractInactive), belong to the order's party,
/// and the CUMULATIVE ordered quantity per material — every other
/// non-cancelled order against the contract plus this document — must
/// stay within the contracted quantity (else SAL-7015
/// ContractQuantityExceeded). Documents without a contract pass.
/// </summary>
public static class ContractGuard
{
    public static void ValidateOrder(
        DbConnection connection, DbTransaction transaction, string provider,
        string companyKey, string? contractId, long? excludeDocId,
        DateOnly docDate, long? partyId, IReadOnlyList<(long MaterialId, decimal Qty)> lines)
    {
        if (string.IsNullOrWhiteSpace(contractId))
            return;
        if (!PartyLedger.TableExists(connection, transaction, provider, "sct_contracts"))
            throw new AppError(ErrorCodes.ContractInactive,
                "The linked contract cannot be verified because the contracts module is not installed.");
        var isPg = SalesPriceResolver.IsPg(provider);
        var notDeleted = isPg ? "is_deleted = FALSE" : "is_deleted = 0";
        var companyCode = PartyLedger.ResolveCompanyCode(connection, transaction, provider, companyKey.Trim())
            ?? companyKey.Trim();
        var dateText = docDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string status = string.Empty, startDate = string.Empty, endDate = string.Empty, contractNo = contractId;
        long? contractParty = null;
        var found = false;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"""
                SELECT status, start_date, end_date, party_id, contract_no FROM sct_contracts
                WHERE company_id = @c AND contract_id = @id AND {notDeleted} LIMIT 1
                """;
            Add(read, "@c", companyCode);
            Add(read, "@id", contractId.Trim());
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                found = true;
                status = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
                startDate = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
                endDate = Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty;
                contractParty = reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
                contractNo = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? contractId;
            }
        }
        if (!found || status != ContractStatuses.Active
            || string.CompareOrdinal(dateText, startDate) < 0 || string.CompareOrdinal(dateText, endDate) > 0)
        {
            throw new AppError(ErrorCodes.ContractInactive,
                found
                    ? $"Contract '{contractNo}' is '{status}' and covers {startDate} to {endDate}; order date {dateText} cannot draw down against it."
                    : $"Contract '{contractId}' was not found.",
                contractNo, status, dateText);
        }
        if (contractParty is not null && partyId is not null && contractParty != partyId)
            throw new AppError(ErrorCodes.ContractInactive,
                $"Contract '{contractNo}' belongs to a different party than the order.",
                contractNo, contractParty.Value.ToString(CultureInfo.InvariantCulture),
                partyId.Value.ToString(CultureInfo.InvariantCulture));

        // Cumulative ordered quantity per material from the contract's
        // OTHER live orders (draft + posted; cancelled excluded).
        var keys = SalesPriceResolver.CompanyKeys(connection, transaction, provider, companyKey);
        var inList = string.Join(", ", keys.Select((_, i) => $"@co{i}"));
        var alreadyOrdered = new Dictionary<long, decimal>();
        using (var ordered = connection.CreateCommand())
        {
            ordered.Transaction = transaction;
            ordered.CommandText = $"""
                SELECT l.material_id, SUM(l.qty) FROM sales_documents o
                JOIN sales_document_lines l ON l.doc_id = o.doc_id
                WHERE o.company_id IN ({inList}) AND o.doc_kind = 'SO' AND o.is_deleted = 0
                  AND o.status <> 'Cancelled' AND o.contract_id = @contract
                  AND (@exclude IS NULL OR o.doc_id <> @exclude) AND l.material_id IS NOT NULL
                GROUP BY l.material_id
                """;
            for (var i = 0; i < keys.Count; i++) Add(ordered, $"@co{i}", keys[i]);
            Add(ordered, "@contract", contractId.Trim());
            Add(ordered, "@exclude", (object?)excludeDocId ?? DBNull.Value);
            using var reader = ordered.ExecuteReader();
            while (reader.Read())
                alreadyOrdered[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        var contracted = new Dictionary<long, decimal>();
        using (var linesRead = connection.CreateCommand())
        {
            linesRead.Transaction = transaction;
            linesRead.CommandText = $"""
                SELECT material_id, contracted_qty FROM sct_contract_lines
                WHERE company_id = @c AND contract_id = @contract AND {notDeleted}
                """;
            Add(linesRead, "@c", companyCode);
            Add(linesRead, "@contract", contractId.Trim());
            using var reader = linesRead.ExecuteReader();
            while (reader.Read())
                contracted[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        foreach (var group in lines.Where(l => l.MaterialId > 0).GroupBy(l => l.MaterialId))
        {
            var limit = contracted.GetValueOrDefault(group.Key);
            var total = alreadyOrdered.GetValueOrDefault(group.Key) + group.Sum(l => l.Qty);
            if (total > limit)
                throw new AppError(ErrorCodes.ContractQuantityExceeded,
                    $"Material {group.Key}: cumulative ordered quantity {total} exceeds the contracted quantity {limit} of contract '{contractNo}'.",
                    contractNo, group.Key.ToString(CultureInfo.InvariantCulture),
                    total.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
