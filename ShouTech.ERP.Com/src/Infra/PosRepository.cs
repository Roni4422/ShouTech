using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Infra;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral POS persistence (migration 066, docs/BUILD_POS.md):
/// parameterized SQL only, company scoping (by CODE) on every
/// statement, single-transaction writes. Sessions, tenders, held bills
/// and the Z report live here; the sale document itself posts through
/// the verified InvRepo path, which writes its tender rows and its
/// pos_session_id inside the posting transaction (the same shapes this
/// repository reads back).
/// </summary>
public sealed class PosRepository : IPosRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public PosRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string BoolTrue => _provider == "PostgreSQL" ? "true" : "1";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";
    private object Bool(bool value) => _provider == "PostgreSQL" ? value : (value ? 1 : 0);

    // ==================================================================
    // Sessions
    // ==================================================================

    public async Task<PosSession> OpenSessionAsync(string companyId, string terminalCode, string cashier, int? warehouseId, string currencyCode, decimal openingCash, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(terminalCode)) throw new ArgumentException("A terminal code is required.", nameof(terminalCode));
        if (openingCash < 0) throw new AppError(ErrorCodes.PosSessionStateInvalid, "The opening cash count cannot be negative.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM pos_sessions WHERE company_id = @company AND terminal_code = @terminal AND status = 'Open' AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@terminal", terminalCode.Trim())).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.PosSessionAlreadyOpen,
                    $"Terminal '{terminalCode.Trim()}' already has an open session; close it before opening another.", terminalCode.Trim());
            var isPg = _provider == "PostgreSQL";
            var sesConfig = await SequenceAllocator.ReadConfigAsync(connection, transaction, isPg, companyId.Trim(), "SES",
                SequenceAllocator.DefaultConfig("SES"), cancellationToken).ConfigureAwait(false);
            var year = DateTime.Today.Year;
            var sesSegment = sesConfig.YearlySegment ? year : 0;
            var sesPrefix = sesConfig.YearlySegment ? $"SES-{year}-" : "SES-";
            var sesSeed = await CountAsync(connection, transaction,
                $"""
                SELECT COALESCE(MAX(CAST({(isPg ? "SUBSTRING(session_no FROM @start)" : "SUBSTR(session_no, @start)")} AS {(isPg ? "BIGINT" : "INTEGER")})), 0)
                FROM pos_sessions
                WHERE company_id = @company AND session_no LIKE @pattern
                  AND {(isPg ? "SUBSTRING(session_no FROM @start) ~ '^[0-9]+$'" : "SUBSTR(session_no, @start) GLOB '[0-9]*' AND SUBSTR(session_no, @start) NOT GLOB '*[^0-9]*'")}
                """,
                ("@company", companyId.Trim()), ("@pattern", $"{sesPrefix}%"), ("@start", sesPrefix.Length + 1)).ConfigureAwait(false);
            var sesAllocated = await SequenceAllocator.AllocateAsync(connection, transaction, isPg, companyId.Trim(), "SES",
                sesSegment, sesSeed, sesConfig.StartValue - 1, cancellationToken).ConfigureAwait(false);
            var session = new PosSession
            {
                SessionNo = SequenceAllocator.Format(sesConfig, sesSegment, sesAllocated),
                TerminalCode = terminalCode.Trim(),
                Cashier = cashier?.Trim() ?? string.Empty,
                WarehouseId = warehouseId,
                CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim(),
                OpeningCash = openingCash,
                Status = PosSessionStatuses.Open,
                OpenedBy = userId
            };
            await ExecAsync(connection, transaction, $"""
                INSERT INTO pos_sessions
                    (id, company_id, session_no, terminal_code, cashier, warehouse_id, currency_code,
                     opening_cash, status, opened_by, created_by)
                VALUES (@id, @company, @no, @terminal, @cashier, @warehouse, @currency,
                        @opening, 'Open', @user, @user)
                """,
                ("@id", (object)FormatGuid(session.Id)), ("@company", companyId.Trim()),
                ("@no", session.SessionNo), ("@terminal", session.TerminalCode),
                ("@cashier", session.Cashier), ("@warehouse", (object?)warehouseId ?? DBNull.Value),
                ("@currency", session.CurrencyCode), ("@opening", openingCash),
                ("@user", userId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<PosSession?> GetOpenSessionAsync(string companyId, string terminalCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT id, session_no, terminal_code, cashier, warehouse_id, currency_code, opening_cash,
                   status, CAST(opened_at AS TEXT), opened_by, CAST(closed_at AS TEXT), closed_by,
                   counted_cash, expected_cash, variance, notes
            FROM pos_sessions
            WHERE company_id = @company AND terminal_code = @terminal AND status = 'Open' AND {NotDeleted}
            ORDER BY created_at DESC LIMIT 1
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@terminal", (terminalCode ?? string.Empty).Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    public async Task<int?> GetTerminalWarehouseIdAsync(string companyId, string terminalCode, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(terminalCode)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT w.warehouse_id
            FROM pos_terminals t
            INNER JOIN warehouses w
                ON w.company_id = t.company_id
               AND CAST(w.warehouse_code AS TEXT) = t.warehouse_code
               AND COALESCE(w.is_deleted, 0) = 0
               AND COALESCE(w.is_active, 1) = 1
            WHERE t.company_id = @company
              AND t.code = @terminal
              AND COALESCE(t.is_deleted, 0) = 0
              AND COALESCE(t.is_active, 1) = 1
            LIMIT 1
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@terminal", terminalCode.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<PosSession?> GetSessionAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT id, session_no, terminal_code, cashier, warehouse_id, currency_code, opening_cash,
                   status, CAST(opened_at AS TEXT), opened_by, CAST(closed_at AS TEXT), closed_by,
                   counted_cash, expected_cash, variance, notes
            FROM pos_sessions WHERE company_id = @company AND id = @id AND {NotDeleted}
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(sessionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    public async Task<IReadOnlyList<PosSession>> GetSessionsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT id, session_no, terminal_code, cashier, warehouse_id, currency_code, opening_cash,
                   status, CAST(opened_at AS TEXT), opened_by, CAST(closed_at AS TEXT), closed_by,
                   counted_cash, expected_cash, variance, notes
            FROM pos_sessions WHERE company_id = @company AND {NotDeleted}
            ORDER BY created_at DESC LIMIT 200
            """);
        Add(command, "@company", companyId.Trim());
        var result = new List<PosSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadSession(reader));
        return result;
    }

    public async Task<PosZReport> CloseSessionAsync(string companyId, Guid sessionId, decimal countedCash, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (countedCash < 0) throw new AppError(ErrorCodes.PosSessionStateInvalid, "The counted cash cannot be negative.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ScalarStringAsync(connection, transaction,
                $"SELECT status FROM pos_sessions WHERE company_id = @company AND id = @id AND {NotDeleted}",
                ("@company", companyId.Trim()), ("@id", FormatGuid(sessionId))).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.PosSessionStateInvalid, "The POS session was not found for this company.");
            if (!string.Equals(status, PosSessionStatuses.Open, StringComparison.Ordinal))
                throw new AppError(ErrorCodes.PosSessionStateInvalid, "Only an open session can be closed.", status);

            var z = await BuildZReportAsync(connection, transaction, companyId, sessionId).ConfigureAwait(false);
            var variance = countedCash - z.ExpectedCash;
            await ExecAsync(connection, transaction, $"""
                UPDATE pos_sessions
                SET status = 'Closed', counted_cash = @counted, expected_cash = @expected,
                    variance = @variance, closed_by = @user, closed_at = {NowExpr},
                    updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @company AND id = @id AND status = 'Open'
                """,
                ("@counted", countedCash), ("@expected", z.ExpectedCash), ("@variance", variance),
                ("@user", userId), ("@company", companyId.Trim()), ("@id", FormatGuid(sessionId))).ConfigureAwait(false);

            // Mirror into the legacy day-close register (migration 037)
            // so CloseCashWin's history keeps one row per close.
            var session = await GetSessionInTxAsync(connection, transaction, companyId, sessionId).ConfigureAwait(false);
            // The terminal's separate cashbox-CLOSING account (P4) is
            // recorded on the register row: where the counted cash
            // closes to, per the terminal definition.
            var closingAccount = await ScalarStringAsync(connection, transaction,
                "SELECT closing_account_code FROM pos_terminals WHERE company_id = @company AND code = @terminal AND is_deleted = 0",
                ("@company", companyId.Trim()), ("@terminal", session.TerminalCode)).ConfigureAwait(false);
            await ExecAsync(connection, transaction, """
                INSERT INTO pos_cash_closings
                    (id, company_id, terminal_code, business_date, opened_by, closed_by,
                     opening_amount, expected_amount, counted_amount, difference, status, notes, created_by, closing_account_code)
                VALUES (@id, @company, @terminal, @date, @opened, @closed,
                        @opening, @expected, @counted, @diff, 'Closed', @notes, @user, @closingAccount)
                """,
                ("@id", (object)FormatGuid(Guid.NewGuid())), ("@company", companyId.Trim()),
                ("@terminal", session.TerminalCode), ("@date", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("@opened", (object?)NullIfBlank(session.OpenedBy) ?? DBNull.Value), ("@closed", userId),
                ("@opening", session.OpeningCash), ("@expected", z.ExpectedCash),
                ("@counted", countedCash), ("@diff", variance),
                ("@notes", (object)$"Session {session.SessionNo}"), ("@user", userId),
                ("@closingAccount", (object?)NullIfBlank(closingAccount) ?? DBNull.Value)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            z.CountedCash = countedCash;
            z.Variance = variance;
            z.Status = PosSessionStatuses.Closed;
            return z;
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    // ==================================================================
    // Settings
    // ==================================================================

    public async Task<PosSettings> GetSettingsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT allow_sessionless, default_terminal_code, default_warehouse_id FROM pos_settings WHERE company_id = @company");
        Add(command, "@company", companyId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return new PosSettings();
        return new PosSettings
        {
            AllowSessionless = ReadBool(reader, 0),
            DefaultTerminalCode = ReadString(reader, 1),
            DefaultWarehouseId = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture)
        };
    }

    public async Task SaveSettingsAsync(string companyId, PosSettings settings, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var exists = await CountAsync(connection, null,
            "SELECT COUNT(*) FROM pos_settings WHERE company_id = @company",
            ("@company", companyId.Trim())).ConfigureAwait(false) > 0;
        if (exists)
            await ExecAsync(connection, null, $"""
                UPDATE pos_settings SET allow_sessionless = @allow, default_terminal_code = @terminal,
                    default_warehouse_id = @warehouse, updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @company
                """,
                ("@allow", Bool(settings.AllowSessionless)), ("@terminal", (object?)NullIfBlank(settings.DefaultTerminalCode) ?? DBNull.Value),
                ("@warehouse", (object?)settings.DefaultWarehouseId ?? DBNull.Value),
                ("@user", userId), ("@company", companyId.Trim())).ConfigureAwait(false);
        else
            await ExecAsync(connection, null, $"""
                INSERT INTO pos_settings (company_id, allow_sessionless, default_terminal_code, default_warehouse_id, updated_by, updated_at)
                VALUES (@company, @allow, @terminal, @warehouse, @user, {NowExpr})
                """,
                ("@company", companyId.Trim()), ("@allow", Bool(settings.AllowSessionless)),
                ("@terminal", (object?)NullIfBlank(settings.DefaultTerminalCode) ?? DBNull.Value),
                ("@warehouse", (object?)settings.DefaultWarehouseId ?? DBNull.Value),
                ("@user", userId)).ConfigureAwait(false);
    }

    // ==================================================================
    // Products
    // ==================================================================

    public async Task<IReadOnlyList<PosProduct>> SearchProductsAsync(string companyId, string? term, int? warehouseId = null, int limit = 60, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (limit <= 0) limit = 60;
        if (limit > 500) limit = 500;
        var text = (term ?? string.Empty).Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var deleted = _provider == "PostgreSQL" ? "COALESCE(is_deleted, false) = false" : "COALESCE(is_deleted, 0) = 0";
        var activeStockBalance = _provider == "PostgreSQL" ? "sb.is_deleted = FALSE" : "COALESCE(sb.is_deleted, 0) = 0";
        var sql = $"""
            SELECT material_id, code,
                   COALESCE(NULLIF(pos_name,''), NULLIF(name_en,''), NULLIF(name_ar,''), code),
                   COALESCE(price_retail, price_list, 0),
                   COALESCE(default_unit, unit1, ''),
                   COALESCE(tax_rate, 0), image_path,
                   COALESCE(has_batch, 0), COALESCE(has_serial, 0),
                   COALESCE((SELECT SUM(sb.available_qty) FROM material_stock_balances sb
                       WHERE sb.company_id = materials.company_id AND sb.material_id = materials.material_id
                         AND (@warehouse IS NULL OR sb.warehouse_id = @warehouse)
                         AND {activeStockBalance}), 0),
                   COALESCE((SELECT SUM(sb.reserved_qty) FROM material_stock_balances sb
                       WHERE sb.company_id = materials.company_id AND sb.material_id = materials.material_id
                         AND (@warehouse IS NULL OR sb.warehouse_id = @warehouse)
                         AND {activeStockBalance}), 0)
            FROM materials
            WHERE company_id = @company AND {deleted}
            """ + (text.Length == 0 ? "" : """
              AND (code LIKE @like OR alt_code LIKE @like OR gtin LIKE @like
                   OR unit1_barcode LIKE @like OR unit2_barcode LIKE @like OR unit3_barcode LIKE @like
                   OR name_en LIKE @like OR name_ar LIKE @like OR pos_name LIKE @like
                   OR short_name LIKE @like OR search_name LIKE @like)
            """) + $"""
            ORDER BY {(text.Length == 0 ? "code" : """
                CASE WHEN code = @term OR alt_code = @term OR gtin = @term
                      OR unit1_barcode = @term OR unit2_barcode = @term OR unit3_barcode = @term
                     THEN 0 ELSE 1 END, code
                """)}
            LIMIT {limit}
            """;
        await using var command = CreateCommand(connection, null, sql);
        Add(command, "@company", companyId.Trim());
        Add(command, "@warehouse", warehouseId.HasValue ? warehouseId.Value : DBNull.Value);
        if (text.Length > 0)
        {
            Add(command, "@like", "%" + text + "%");
            Add(command, "@term", text);
        }
        var result = new List<PosProduct>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new PosProduct
            {
                MaterialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Code = ReadString(reader, 1) ?? string.Empty,
                Name = ReadString(reader, 2) ?? string.Empty,
                Price = ReadDecimal(reader, 3),
                Unit = ReadString(reader, 4) ?? string.Empty,
                TaxRate = ReadDecimal(reader, 5),
                ImagePath = ReadString(reader, 6),
                HasBatch = ReadBoolValue(reader.GetValue(7)),
                HasSerial = ReadBoolValue(reader.GetValue(8)),
                OnHandQty = ReadDecimal(reader, 9),
                ReservedQty = ReadDecimal(reader, 10)
            });
        return result;
    }

    // ==================================================================
    // Tenders
    // ==================================================================

    public async Task RecordTendersAsync(string companyId, IReadOnlyList<PosTender> tenders, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(tenders);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var tender in tenders)
                await InsertTenderAsync(connection, transaction, companyId, tender, userId).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    /// <summary>The one tender INSERT shape — InvRepo's posting
    /// transaction writes the identical columns so a tender recorded at
    /// posting and one recorded here are indistinguishable.</summary>
    internal static async Task InsertTenderAsync(DbConnection connection, DbTransaction transaction, string companyId, PosTender tender, string userId)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO pos_sale_tenders
                (id, company_id, session_id, doc_id, doc_no, doc_kind, tender_type,
                 amount, tendered_amount, change_amount, card_ref, created_by)
            VALUES (@id, @company, @session, @doc, @docno, @kind, @type,
                    @amount, @tendered, @change, @ref, @user)
            """);
        Add(command, "@id", (object)(tender.Id == Guid.Empty ? Guid.NewGuid().ToString("D") : tender.Id.ToString("D")));
        Add(command, "@company", companyId.Trim());
        Add(command, "@session", (object?)(tender.SessionId is Guid s ? s.ToString("D") : null) ?? DBNull.Value);
        Add(command, "@doc", tender.DocId);
        Add(command, "@docno", tender.DocNo ?? string.Empty);
        Add(command, "@kind", string.IsNullOrWhiteSpace(tender.DocKind) ? "POS" : tender.DocKind);
        Add(command, "@type", tender.TenderType);
        Add(command, "@amount", tender.Amount);
        Add(command, "@tendered", (object?)tender.TenderedAmount ?? DBNull.Value);
        Add(command, "@change", tender.ChangeAmount);
        Add(command, "@ref", (object?)NullIfBlank(tender.CardRef) ?? DBNull.Value);
        Add(command, "@user", userId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PosTender>> GetTendersForDocAsync(string companyId, long docId, string docKind, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT id, session_id, doc_id, doc_no, doc_kind, tender_type, amount, tendered_amount, change_amount, card_ref
            FROM pos_sale_tenders
            WHERE company_id = @company AND doc_id = @doc AND doc_kind = @kind AND {NotDeleted}
            ORDER BY created_at, tender_type
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@doc", docId);
        Add(command, "@kind", docKind);
        var result = new List<PosTender>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadTender(reader));
        return result;
    }

    public async Task<IReadOnlyList<PosTender>> GetTendersForSessionAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSessionTendersAsync(connection, null, companyId, sessionId).ConfigureAwait(false);
    }

    private async Task<List<PosTender>> ReadSessionTendersAsync(DbConnection connection, DbTransaction? transaction, string companyId, Guid sessionId)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT id, session_id, doc_id, doc_no, doc_kind, tender_type, amount, tendered_amount, change_amount, card_ref
            FROM pos_sale_tenders
            WHERE company_id = @company AND session_id = @session AND {NotDeleted}
            ORDER BY created_at, tender_type
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@session", FormatGuid(sessionId));
        var result = new List<PosTender>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false)) result.Add(ReadTender(reader));
        return result;
    }

    // ==================================================================
    // Held bills
    // ==================================================================

    public async Task<PosHeldBill> HoldBillAsync(string companyId, PosHeldBill bill, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Lines.Count == 0) throw new AppError(ErrorCodes.PosHeldBillStateInvalid, "A held bill needs at least one line.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var year = DateTime.Today.Year;
            var count = await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM pos_held_bills WHERE company_id = @company AND held_no LIKE @pattern",
                ("@company", companyId.Trim()), ("@pattern", $"HLD-{year}-%")).ConfigureAwait(false);
            bill.HeldNo = $"HLD-{year}-{(count + 1).ToString("D4", CultureInfo.InvariantCulture)}";
            bill.Status = PosHeldStatuses.Held;
            await ExecAsync(connection, transaction, """
                INSERT INTO pos_held_bills
                    (id, company_id, session_id, held_no, cashier, party_id, party_code, party_name,
                     currency_code, bill_discount, notes, status, created_by)
                VALUES (@id, @company, @session, @no, @cashier, @party, @pcode, @pname,
                        @currency, @discount, @notes, 'Held', @user)
                """,
                ("@id", (object)FormatGuid(bill.Id)), ("@company", companyId.Trim()),
                ("@session", (object?)(bill.SessionId is Guid s ? FormatGuid(s) : null) ?? DBNull.Value),
                ("@no", bill.HeldNo), ("@cashier", bill.Cashier ?? string.Empty),
                ("@party", (object?)bill.PartyId ?? DBNull.Value),
                ("@pcode", (object?)NullIfBlank(bill.PartyCode) ?? DBNull.Value),
                ("@pname", (object?)NullIfBlank(bill.PartyName) ?? DBNull.Value),
                ("@currency", string.IsNullOrWhiteSpace(bill.CurrencyCode) ? "SAR" : bill.CurrencyCode),
                ("@discount", bill.BillDiscount), ("@notes", (object?)NullIfBlank(bill.Notes) ?? DBNull.Value),
                ("@user", userId)).ConfigureAwait(false);
            var lineNo = 0;
            foreach (var line in bill.Lines)
            {
                line.LineNo = ++lineNo;
                await ExecAsync(connection, transaction, """
                    INSERT INTO pos_held_bill_lines
                        (id, held_id, line_no, material_id, material_code, description, unit,
                         qty, unit_price, discount_pct, tax_rate, warehouse_id)
                    VALUES (@id, @held, @no, @mid, @mcode, @desc, @unit, @qty, @price, @disc, @tax, @wh)
                    """,
                    ("@id", (object)Guid.NewGuid().ToString("D")), ("@held", FormatGuid(bill.Id)),
                    ("@no", line.LineNo), ("@mid", (object?)line.MaterialId ?? DBNull.Value),
                    ("@mcode", (object?)NullIfBlank(line.MaterialCode) ?? DBNull.Value),
                    ("@desc", line.Description ?? string.Empty), ("@unit", (object?)NullIfBlank(line.Unit) ?? DBNull.Value),
                    ("@qty", line.Qty), ("@price", line.UnitPrice), ("@disc", line.DiscountPct),
                    ("@tax", line.TaxRate), ("@wh", (object?)line.WarehouseId ?? DBNull.Value)).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return bill;
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<PosHeldBill>> GetHeldBillsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            SELECT id FROM pos_held_bills
            WHERE company_id = @company AND status = 'Held' AND {NotDeleted}
            ORDER BY created_at
            """);
        Add(command, "@company", companyId.Trim());
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(ReadGuid(reader, 0));
        var result = new List<PosHeldBill>();
        foreach (var id in ids)
        {
            var bill = await GetHeldBillAsync(companyId, id, cancellationToken).ConfigureAwait(false);
            if (bill is not null) result.Add(bill);
        }
        return result;
    }

    public async Task<PosHeldBill?> GetHeldBillAsync(string companyId, Guid heldId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        PosHeldBill? bill;
        await using (var command = CreateCommand(connection, null, $"""
            SELECT id, session_id, held_no, cashier, party_id, party_code, party_name,
                   currency_code, bill_discount, notes, status, CAST(created_at AS TEXT)
            FROM pos_held_bills WHERE company_id = @company AND id = @id AND {NotDeleted}
            """))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@id", FormatGuid(heldId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            bill = new PosHeldBill
            {
                Id = ReadGuid(reader, 0), SessionId = ReadGuidOrNull(reader, 1),
                HeldNo = ReadString(reader, 2) ?? string.Empty, Cashier = ReadString(reader, 3) ?? string.Empty,
                PartyId = reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                PartyCode = ReadString(reader, 5), PartyName = ReadString(reader, 6),
                CurrencyCode = ReadString(reader, 7) ?? "SAR", BillDiscount = ReadDecimal(reader, 8),
                Notes = ReadString(reader, 9), Status = ReadString(reader, 10) ?? PosHeldStatuses.Held,
                CreatedAt = ReadString(reader, 11)
            };
        }
        await using (var command = CreateCommand(connection, null, """
            SELECT line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, warehouse_id
            FROM pos_held_bill_lines WHERE held_id = @held ORDER BY line_no
            """))
        {
            Add(command, "@held", FormatGuid(heldId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                bill.Lines.Add(new PosHeldLine
                {
                    LineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    MaterialId = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    MaterialCode = ReadString(reader, 2), Description = ReadString(reader, 3) ?? string.Empty,
                    Unit = ReadString(reader, 4), Qty = ReadDecimal(reader, 5), UnitPrice = ReadDecimal(reader, 6),
                    DiscountPct = ReadDecimal(reader, 7), TaxRate = ReadDecimal(reader, 8),
                    WarehouseId = reader.IsDBNull(9) ? null : Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture)
                });
        }
        return bill;
    }

    public async Task<PosHeldBill> RecallHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var bill = await GetHeldBillAsync(companyId, heldId, cancellationToken).ConfigureAwait(false)
            ?? throw new AppError(ErrorCodes.PosHeldBillNotFound, "The held bill was not found for this company.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE pos_held_bills SET status = 'Recalled', updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @company AND id = @id AND status = 'Held' AND {NotDeleted}
            """);
        Add(command, "@user", userId);
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(heldId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new AppError(ErrorCodes.PosHeldBillStateInvalid,
                $"Held bill '{bill.HeldNo}' is '{bill.Status}' and cannot be recalled again.", bill.HeldNo);
        bill.Status = PosHeldStatuses.Recalled;
        return bill;
    }

    public async Task CancelHeldBillAsync(string companyId, Guid heldId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, $"""
            UPDATE pos_held_bills SET status = 'Cancelled', updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @company AND id = @id AND status = 'Held' AND {NotDeleted}
            """);
        Add(command, "@user", userId);
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(heldId));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new AppError(ErrorCodes.PosHeldBillStateInvalid, "Only a held bill can be cancelled.");
    }

    // ==================================================================
    // Returns / Z
    // ==================================================================

    public async Task<PosOriginalBill?> GetOriginalBillAsync(string companyId, string docNo, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(docNo)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        PosOriginalBill? bill;
        await using (var command = CreateCommand(connection, null, $"""
            SELECT doc_id, doc_no, doc_date, party_id, party_code, party_name, currency_code, exchange_rate,
                   discount_amount, extra_amount, grand_total, paid_amount, pos_session_id
            FROM sales_documents
            WHERE company_id = @company AND doc_kind = 'POS' AND doc_no = @no
              AND status = 'Posted' AND {NotDeleted}
            """))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@no", docNo.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            bill = new PosOriginalBill
            {
                DocId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                DocNo = ReadString(reader, 1) ?? string.Empty,
                DocDate = ReadString(reader, 2) ?? string.Empty,
                PartyId = reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                PartyCode = ReadString(reader, 4), PartyName = ReadString(reader, 5),
                CurrencyCode = ReadString(reader, 6) ?? "SAR",
                ExchangeRate = reader.IsDBNull(7) ? 1m : ReadDecimal(reader, 7),
                DiscountAmount = ReadDecimal(reader, 8), ExtraAmount = ReadDecimal(reader, 9),
                GrandTotal = ReadDecimal(reader, 10), PaidAmount = ReadDecimal(reader, 11),
                SessionId = ReadGuidOrNull(reader, 12)
            };
        }
        // Already-returned quantities: posted SRT documents whose
        // original is this bill; their lines keep the ORIGINAL line
        // numbers (PosPosting builds them that way), so a per-line_no
        // sum is the returned quantity in the original line's unit.
        var returned = new Dictionary<int, decimal>();
        await using (var command = CreateCommand(connection, null, $"""
            SELECT l.line_no, COALESCE(SUM(l.qty), 0)
            FROM sales_document_lines l
            JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = @company AND d.doc_kind = 'SRT' AND d.original_doc_id = @orig
              AND d.status = 'Posted' AND d.{NotDeleted}
            GROUP BY l.line_no
            """))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@orig", bill.DocId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                returned[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = ReadDecimal(reader, 1);
        }
        await using (var command = CreateCommand(connection, null, """
            SELECT line_no, material_id, material_code, description, unit, qty, unit_price, discount_pct, tax_rate, warehouse_id, tax_calculation_mode
            FROM sales_document_lines WHERE doc_id = @doc ORDER BY line_no
            """))
        {
            Add(command, "@doc", bill.DocId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var lineNo = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                bill.Lines.Add(new PosReturnableLine(
                    lineNo,
                    reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    ReadString(reader, 2) ?? string.Empty, ReadString(reader, 3) ?? string.Empty,
                    ReadString(reader, 4), ReadDecimal(reader, 5), ReadDecimal(reader, 6),
                    ReadDecimal(reader, 7), ReadDecimal(reader, 8),
                    reader.IsDBNull(9) ? null : Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture),
                    returned.GetValueOrDefault(lineNo))
                {
                    TaxCalculationMode = ReadString(reader, 10) ?? "Exclusive"
                });
            }
        }
        return bill;
    }

    public async Task<PosZReport> GetZReportAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await BuildZReportAsync(connection, null, companyId, sessionId).ConfigureAwait(false);
    }

    private async Task<PosZReport> BuildZReportAsync(DbConnection connection, DbTransaction? transaction, string companyId, Guid sessionId)
    {
        var session = await GetSessionInTxAsync(connection, transaction, companyId, sessionId).ConfigureAwait(false);
        var z = new PosZReport
        {
            SessionId = sessionId, SessionNo = session.SessionNo,
            TerminalCode = session.TerminalCode, Cashier = session.Cashier,
            Status = session.Status, OpeningCash = session.OpeningCash,
            CountedCash = session.CountedCash
        };
        await using (var command = CreateCommand(connection, transaction, $"""
            SELECT doc_kind, COUNT(*), COALESCE(SUM(grand_total), 0), COALESCE(SUM(tax_amount), 0), COALESCE(SUM(discount_amount), 0)
            FROM sales_documents
            WHERE company_id = @company AND pos_session_id = @session AND status = 'Posted' AND {NotDeleted}
              AND doc_kind IN ('POS', 'SRT')
            GROUP BY doc_kind
            """))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@session", FormatGuid(sessionId));
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var kind = ReadString(reader, 0);
                if (kind == "POS")
                {
                    z.SalesCount = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                    z.SalesTotal = ReadDecimal(reader, 2);
                    z.TaxTotal += ReadDecimal(reader, 3);
                    z.DiscountTotal += ReadDecimal(reader, 4);
                }
                else if (kind == "SRT")
                {
                    z.ReturnsCount = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                    z.ReturnsTotal = ReadDecimal(reader, 2);
                    z.TaxTotal -= ReadDecimal(reader, 3);
                    z.DiscountTotal -= ReadDecimal(reader, 4);
                }
            }
        }
        var tenders = await ReadSessionTendersAsync(connection, transaction, companyId, sessionId).ConfigureAwait(false);
        z.CashTendered = tenders.Where(t => t.DocKind == "POS" && t.TenderType == PosTenderTypes.Cash).Sum(t => t.Amount);
        z.CardTendered = tenders.Where(t => t.DocKind == "POS" && t.TenderType == PosTenderTypes.Card).Sum(t => t.Amount);
        z.OnAccountTendered = tenders.Where(t => t.DocKind == "POS" && t.TenderType == PosTenderTypes.OnAccount).Sum(t => t.Amount);
        z.CashRefunded = -tenders.Where(t => t.DocKind == "SRT" && t.TenderType == PosTenderTypes.Cash).Sum(t => t.Amount);
        z.CardRefunded = -tenders.Where(t => t.DocKind == "SRT" && t.TenderType == PosTenderTypes.Card).Sum(t => t.Amount);
        // Paid-outs (migration 104): cash that left the drawer for
        // till expenses reduces the expectation exactly like a refund.
        var expenses = await ReadSessionExpensesAsync(connection, transaction, companyId, sessionId).ConfigureAwait(false);
        z.PaidOutsCount = expenses.Count;
        z.PaidOutsTotal = expenses.Sum(e => e.Amount);
        z.ExpectedCash = PosTenderMath.ExpectedCash(session.OpeningCash, tenders, z.PaidOutsTotal);
        z.Variance = session.CountedCash is decimal counted ? counted - z.ExpectedCash : null;
        return z;
    }

    private async Task<PosSession> GetSessionInTxAsync(DbConnection connection, DbTransaction? transaction, string companyId, Guid sessionId)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT id, session_no, terminal_code, cashier, warehouse_id, currency_code, opening_cash,
                   status, CAST(opened_at AS TEXT), opened_by, CAST(closed_at AS TEXT), closed_by,
                   counted_cash, expected_cash, variance, notes
            FROM pos_sessions WHERE company_id = @company AND id = @id AND {NotDeleted}
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(sessionId));
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
            throw new AppError(ErrorCodes.PosSessionStateInvalid, "The POS session was not found for this company.");
        return ReadSession(reader);
    }

    // ==================================================================
    // Session paid-outs (migration 104)
    // ==================================================================

    public async Task<PosSessionExpense> RecordSessionExpenseAsync(string companyId, Guid sessionId, decimal amount, string reason, string? referenceNo, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (amount <= 0) throw new AppError(ErrorCodes.PosSessionStateInvalid, "A paid-out amount must be positive.");
        if (string.IsNullOrWhiteSpace(reason)) throw new AppError(ErrorCodes.PosSessionStateInvalid, "A paid-out needs a reason.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ScalarStringAsync(connection, transaction,
                $"SELECT status FROM pos_sessions WHERE company_id = @company AND id = @id AND {NotDeleted}",
                ("@company", companyId.Trim()), ("@id", FormatGuid(sessionId))).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.PosSessionStateInvalid, "The POS session was not found for this company.");
            if (!string.Equals(status, PosSessionStatuses.Open, StringComparison.Ordinal))
                throw new AppError(ErrorCodes.PosSessionStateInvalid, "Paid-outs can only be recorded against an open session.", status);
            var year = DateTime.Today.Year;
            var count = await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM pos_session_expenses WHERE company_id = @company AND expense_no LIKE @pattern",
                ("@company", companyId.Trim()), ("@pattern", $"EXP-{year}-%")).ConfigureAwait(false);
            var expense = new PosSessionExpense
            {
                SessionId = sessionId,
                ExpenseNo = $"EXP-{year}-{(count + 1).ToString("D4", CultureInfo.InvariantCulture)}",
                Amount = amount,
                Reason = reason.Trim(),
                ReferenceNo = NullIfBlank(referenceNo),
                CreatedBy = userId
            };
            await ExecAsync(connection, transaction, """
                INSERT INTO pos_session_expenses
                    (id, company_id, session_id, expense_no, amount, reason, reference_no, created_by)
                VALUES (@id, @company, @session, @no, @amount, @reason, @ref, @user)
                """,
                ("@id", (object)FormatGuid(expense.Id)), ("@company", companyId.Trim()),
                ("@session", FormatGuid(sessionId)), ("@no", expense.ExpenseNo),
                ("@amount", expense.Amount), ("@reason", expense.Reason),
                ("@ref", (object?)expense.ReferenceNo ?? DBNull.Value), ("@user", userId)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return expense;
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<PosSessionExpense>> GetSessionExpensesAsync(string companyId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSessionExpensesAsync(connection, null, companyId, sessionId).ConfigureAwait(false);
    }

    private async Task<List<PosSessionExpense>> ReadSessionExpensesAsync(DbConnection connection, DbTransaction? transaction, string companyId, Guid sessionId)
    {
        await using var command = CreateCommand(connection, transaction, $"""
            SELECT id, session_id, expense_no, amount, reason, reference_no, created_by, CAST(created_at AS TEXT)
            FROM pos_session_expenses
            WHERE company_id = @company AND session_id = @session AND {NotDeleted}
            ORDER BY created_at, expense_no
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@session", FormatGuid(sessionId));
        var result = new List<PosSessionExpense>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            result.Add(new PosSessionExpense
            {
                Id = ReadGuid(reader, 0), SessionId = ReadGuid(reader, 1),
                ExpenseNo = ReadString(reader, 2) ?? string.Empty, Amount = ReadDecimal(reader, 3),
                Reason = ReadString(reader, 4) ?? string.Empty, ReferenceNo = ReadString(reader, 5),
                CreatedBy = ReadString(reader, 6), CreatedAt = ReadString(reader, 7)
            });
        return result;
    }

    // ==================================================================
    // Discount limits (migration 104)
    // ==================================================================

    public async Task<decimal?> GetDiscountLimitPctAsync(string companyId, string username, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (string.IsNullOrWhiteSpace(username)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var userLimit = await ScalarDecimalOrNullAsync(connection, null,
            "SELECT max_discount_pct FROM pos_discount_limits WHERE company_id = @company AND subject_kind = 'USER' AND subject_key = @key",
            ("@company", companyId.Trim()), ("@key", username.Trim())).ConfigureAwait(false);
        if (userLimit is not null) return userLimit;
        // The highest ROLE row among the cashier's roles in this
        // company (users join companies by CODE, the Auth.cs shape).
        return await ScalarDecimalOrNullAsync(connection, null, """
            SELECT MAX(l.max_discount_pct)
            FROM pos_discount_limits l
            JOIN companies c ON c.company_code = @company
            JOIN users u ON u.company_id = c.company_id AND u.username = @key
            JOIN user_roles ur ON ur.user_id = u.user_id
            JOIN roles r ON r.role_id = ur.role_id AND r.company_id = c.company_id
            WHERE l.company_id = @company AND l.subject_kind = 'ROLE' AND l.subject_key = r.role_code
            """,
            ("@company", companyId.Trim()), ("@key", username.Trim())).ConfigureAwait(false);
    }

    public async Task SaveDiscountLimitAsync(string companyId, string subjectKind, string subjectKey, decimal maxDiscountPct, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (subjectKind is not (PosDiscountLimitSubjects.User or PosDiscountLimitSubjects.Role))
            throw new AppError(ErrorCodes.PosSessionStateInvalid, $"Unknown discount-limit subject kind '{subjectKind}'.", subjectKind ?? string.Empty);
        if (string.IsNullOrWhiteSpace(subjectKey))
            throw new AppError(ErrorCodes.PosSessionStateInvalid, "A discount-limit subject key (username or role code) is required.");
        if (maxDiscountPct < 0m || maxDiscountPct > 100m)
            throw new AppError(ErrorCodes.PosSessionStateInvalid, "A discount limit must be between 0 and 100 percent.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var exists = await CountAsync(connection, null,
            "SELECT COUNT(*) FROM pos_discount_limits WHERE company_id = @company AND subject_kind = @kind AND subject_key = @key",
            ("@company", companyId.Trim()), ("@kind", subjectKind), ("@key", subjectKey.Trim())).ConfigureAwait(false) > 0;
        if (exists)
            await ExecAsync(connection, null, $"""
                UPDATE pos_discount_limits SET max_discount_pct = @pct, updated_by = @user, updated_at = {NowExpr}
                WHERE company_id = @company AND subject_kind = @kind AND subject_key = @key
                """,
                ("@pct", maxDiscountPct), ("@user", userId), ("@company", companyId.Trim()),
                ("@kind", subjectKind), ("@key", subjectKey.Trim())).ConfigureAwait(false);
        else
            await ExecAsync(connection, null, $"""
                INSERT INTO pos_discount_limits (company_id, subject_kind, subject_key, max_discount_pct, updated_by, updated_at)
                VALUES (@company, @kind, @key, @pct, @user, {NowExpr})
                """,
                ("@company", companyId.Trim()), ("@kind", subjectKind), ("@key", subjectKey.Trim()),
                ("@pct", maxDiscountPct), ("@user", userId)).ConfigureAwait(false);
    }

    // ==================================================================
    // Performance (migration 104)
    // ==================================================================

    public async Task<IReadOnlyList<PosPerformanceRow>> GetPerformanceAsync(string companyId, DateTime from, DateTime to, string? terminalCode, string? cashier, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var expenseDate = _provider == "PostgreSQL" ? "CAST(e.created_at AS DATE)" : "date(e.created_at)";
        // One row per (terminal, cashier): documents carry their
        // session (pos_session_id, 066), so sales/returns/discounts
        // group through the session that ran them; paid-outs group
        // through their own session. Sessions with only paid-outs in
        // the period still appear (the second UNION arm).
        await using var command = CreateCommand(connection, null, $"""
            SELECT s.terminal_code, s.cashier,
                   COALESCE(SUM(CASE WHEN d.doc_kind = 'POS' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN d.doc_kind = 'POS' THEN d.grand_total ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN d.doc_kind = 'SRT' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN d.doc_kind = 'SRT' THEN d.grand_total ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN d.doc_kind = 'POS' THEN d.discount_amount ELSE 0 END), 0)
                   - COALESCE(SUM(CASE WHEN d.doc_kind = 'SRT' THEN d.discount_amount ELSE 0 END), 0),
                   COALESCE((SELECT SUM(e.amount) FROM pos_session_expenses e
                             WHERE e.company_id = @company AND e.session_id = s.id AND e.{NotDeleted}
                               AND {expenseDate} >= @efrom AND {expenseDate} <= @eto), 0)
            FROM pos_sessions s
            JOIN sales_documents d ON d.company_id = @company AND d.pos_session_id = s.id
                 AND d.status = 'Posted' AND d.{NotDeleted} AND d.doc_kind IN ('POS', 'SRT')
            WHERE s.company_id = @company AND s.{NotDeleted}
              AND d.doc_date >= @from AND d.doc_date <= @to
              AND (@terminal = '' OR s.terminal_code = @terminal)
              AND (@cashier = '' OR s.cashier = @cashier)
            GROUP BY s.id, s.terminal_code, s.cashier
            UNION ALL
            SELECT s.terminal_code, s.cashier, 0, 0, 0, 0, 0,
                   COALESCE(SUM(e.amount), 0)
            FROM pos_sessions s
            JOIN pos_session_expenses e ON e.company_id = @company AND e.session_id = s.id AND e.{NotDeleted}
            WHERE s.company_id = @company AND s.{NotDeleted}
              AND {expenseDate} >= @efrom AND {expenseDate} <= @eto
              AND (@terminal = '' OR s.terminal_code = @terminal)
              AND (@cashier = '' OR s.cashier = @cashier)
              AND NOT EXISTS (SELECT 1 FROM sales_documents d2
                              WHERE d2.company_id = @company AND d2.pos_session_id = s.id
                                AND d2.status = 'Posted' AND d2.{NotDeleted} AND d2.doc_kind IN ('POS', 'SRT')
                                AND d2.doc_date >= @from AND d2.doc_date <= @to)
            GROUP BY s.id, s.terminal_code, s.cashier
            ORDER BY 1, 2
            """);
        Add(command, "@company", companyId.Trim());
        // doc_date is ISO text in both dialects; the expense date is
        // text in SQLite (date()) but a real DATE in PostgreSQL
        // (CAST), so the expense range gets provider-shaped params.
        Add(command, "@from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@efrom", _provider == "PostgreSQL" ? (object)from.Date : from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@eto", _provider == "PostgreSQL" ? (object)to.Date : to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(command, "@terminal", (terminalCode ?? string.Empty).Trim());
        Add(command, "@cashier", (cashier ?? string.Empty).Trim());
        // A session can legitimately appear once per arm only (the
        // arms are disjoint), but two sessions of the same cashier on
        // the same terminal must MERGE into one row.
        var byKey = new Dictionary<(string, string), PosPerformanceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (ReadString(reader, 0) ?? string.Empty, ReadString(reader, 1) ?? string.Empty);
            if (!byKey.TryGetValue(key, out var row))
                byKey[key] = row = new PosPerformanceRow { TerminalCode = key.Item1, Cashier = key.Item2 };
            row.SalesCount += Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            row.GrossSales += ReadDecimal(reader, 3);
            row.ReturnsCount += Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
            row.ReturnsTotal += ReadDecimal(reader, 5);
            row.DiscountsGiven += ReadDecimal(reader, 6);
            row.PaidOuts += ReadDecimal(reader, 7);
        }
        return byKey.Values.OrderBy(r => r.TerminalCode).ThenBy(r => r.Cashier).ToList();
    }

    private static async Task<decimal?> ScalarDecimalOrNullAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToDecimal(result, CultureInfo.InvariantCulture);
    }

    // ==================================================================
    // Helpers (HrRepository shape)
    // ==================================================================

    private static PosSession ReadSession(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0), SessionNo = ReadString(reader, 1) ?? string.Empty,
        TerminalCode = ReadString(reader, 2) ?? string.Empty, Cashier = ReadString(reader, 3) ?? string.Empty,
        WarehouseId = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
        CurrencyCode = ReadString(reader, 5) ?? "SAR", OpeningCash = ReadDecimal(reader, 6),
        Status = ReadString(reader, 7) ?? PosSessionStatuses.Open, OpenedAt = ReadString(reader, 8),
        OpenedBy = ReadString(reader, 9), ClosedAt = ReadString(reader, 10), ClosedBy = ReadString(reader, 11),
        CountedCash = reader.IsDBNull(12) ? null : ReadDecimal(reader, 12),
        ExpectedCash = reader.IsDBNull(13) ? null : ReadDecimal(reader, 13),
        Variance = reader.IsDBNull(14) ? null : ReadDecimal(reader, 14),
        Notes = ReadString(reader, 15)
    };

    private static PosTender ReadTender(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0), SessionId = ReadGuidOrNull(reader, 1),
        DocId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
        DocNo = ReadString(reader, 3) ?? string.Empty, DocKind = ReadString(reader, 4) ?? "POS",
        TenderType = ReadString(reader, 5) ?? PosTenderTypes.Cash, Amount = ReadDecimal(reader, 6),
        TenderedAmount = reader.IsDBNull(7) ? null : ReadDecimal(reader, 7),
        ChangeAmount = ReadDecimal(reader, 8), CardRef = ReadString(reader, 9)
    };

    private static async Task<long> CountAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task ExecAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task RollbackAsync(DbTransaction transaction)
    {
        try { await transaction.RollbackAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { /* already completed */ }
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null) command.Transaction = transaction;
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

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString("D");

    private static Guid ReadGuidValue(object value) => value switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out var parsed) => parsed,
        _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
    };

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => ReadGuidValue(reader.GetValue(ordinal));

    private static Guid? ReadGuidOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuidValue(reader.GetValue(ordinal));

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool ReadBool(DbDataReader reader, int ordinal)
        => !reader.IsDBNull(ordinal) && ReadBoolValue(reader.GetValue(ordinal));

    private static bool ReadBoolValue(object value) => value switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
