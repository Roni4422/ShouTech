using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;

namespace ShouTech.Infrastructure;

/// <summary>Warranty lifecycle (migration 105). Expired is DERIVED from
/// the stored expiry date on read (and stamped onto the row when a
/// claim attempt proves it), so a warranty can never show Active past
/// its expiry; Void is the only manual terminal state.</summary>
public static class WarrantyStatuses
{
    public const string Active = "Active";
    public const string Expired = "Expired";
    public const string Void = "Void";

    public static readonly IReadOnlyList<string> All = new[] { Active, Expired, Void };
}

/// <summary>Warranty claim decisions (migration 105).</summary>
public static class WarrantyClaimDecisions
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";

    public static readonly IReadOnlyList<string> All = new[] { Pending, Approved, Rejected };
}

/// <summary>What an approved warranty claim does with the unit.</summary>
public static class WarrantyClaimActions
{
    public const string Repair = "Repair";
    public const string Replace = "Replace";
    public const string Return = "Return";

    public static readonly IReadOnlyList<string> All = new[] { Repair, Replace, Return };
}

/// <summary>One registered warranty (swa_warranties): one row per sold
/// serial, expiry computed at registration from the sale date plus the
/// term in months and stored — never recomputed later.</summary>
public sealed record WarrantyRecord(
    long Id, long SerialId, string SerialNo, long MaterialId, string MaterialCode,
    long? PartyId, string PartyName, string SourceDocKind, long? SourceDocId, string SourceDocNo,
    string SaleDate, int WarrantyMonths, string ExpiryDate, string StoredStatus, string? Terms)
{
    /// <summary>The status as of a date: Void is terminal; otherwise the
    /// stored expiry decides between Active and Expired.</summary>
    public string EffectiveStatus(DateOnly asOf)
        => StoredStatus == WarrantyStatuses.Void
            ? WarrantyStatuses.Void
            : DateOnly.TryParseExact(ExpiryDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) && expiry < asOf
                ? WarrantyStatuses.Expired
                : WarrantyStatuses.Active;
}

/// <summary>One warranty claim (swa_warranty_claims). An Approved claim
/// with Action = Return carries the RMA it created in RmaId.</summary>
public sealed record WarrantyClaimRecord(
    long Id, long WarrantyId, string ClaimDate, string Issue, string Decision,
    string? Action, string? ResolutionNote, string? DecidedBy, string? DecidedAt, long? RmaId);

/// <summary>The outcome of a document registration scan: how many
/// warranties were newly registered and how many sold serials were
/// skipped (already registered, or no warranty term known).</summary>
public sealed record WarrantyRegistrationResult(int Registered, int Skipped);

/// <summary>
/// Warranty registration + claims (sales Level 3, migration 105), in
/// the BatchSerialService shape (provider + connection string, sync,
/// parameterized, transactional). Registration DERIVES from the sold
/// serial records of migration 054: a posted SAL / POS document's
/// outbound serial allocations (stock_movement_allocations with
/// qty &lt; 0 on the document's movements) are the serials the customer
/// holds. Registration is idempotent — the (company, serial) unique
/// key plus an existence probe mean a re-scan registers nothing twice.
/// Posting itself is deliberately NOT hooked: registration runs from
/// the warranty window's explicit action, so the posting path stays
/// untouched. Claim state errors are the cataloged SAL-7026.
/// </summary>
public sealed class WarrantyService
{
    private readonly string _provider;
    private readonly string _connectionString;

    public WarrantyService(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private bool IsPg => _provider == "PostgreSQL";
    private string NotDeleted => IsPg ? "is_deleted = FALSE" : "is_deleted = 0";
    private string NowExpr => IsPg ? "now()" : "datetime('now')";

    private DbConnection OpenConnection()
    {
        DbConnection connection = IsPg
            ? new NpgsqlConnection(_connectionString)
            : new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // ------------------------------------------------------------------
    // Reads
    // ------------------------------------------------------------------
    public IReadOnlyList<WarrantyRecord> ListWarranties(string companyId, string? status = null)
    {
        RequireCompany(companyId);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var sql = $"""
            SELECT warranty_id, serial_id, serial_no, material_id, material_code, party_id, party_name,
                   source_doc_kind, source_doc_id, source_doc_no, sale_date, warranty_months, expiry_date, status, terms
            FROM swa_warranties
            WHERE company_id = @c AND {NotDeleted}
            """;
        if (!string.IsNullOrWhiteSpace(status)) sql += " AND status = @status";
        sql += " ORDER BY expiry_date, serial_no";
        cmd.CommandText = sql;
        Add(cmd, "@c", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(status)) Add(cmd, "@status", status.Trim());
        var result = new List<WarrantyRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadWarranty(reader));
        return result;
    }

    public WarrantyRecord? GetWarranty(string companyId, long warrantyId)
    {
        RequireCompany(companyId);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT warranty_id, serial_id, serial_no, material_id, material_code, party_id, party_name,
                   source_doc_kind, source_doc_id, source_doc_no, sale_date, warranty_months, expiry_date, status, terms
            FROM swa_warranties
            WHERE company_id = @c AND warranty_id = @id AND {NotDeleted}
            """;
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@id", warrantyId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadWarranty(reader) : null;
    }

    public IReadOnlyList<WarrantyClaimRecord> ListClaims(string companyId, long? warrantyId = null)
    {
        RequireCompany(companyId);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var sql = $"""
            SELECT claim_id, warranty_id, claim_date, issue, decision, action, resolution_note, decided_by, decided_at, rma_id
            FROM swa_warranty_claims
            WHERE company_id = @c AND {NotDeleted}
            """;
        if (warrantyId is not null) sql += " AND warranty_id = @w";
        sql += " ORDER BY claim_date DESC, claim_id DESC";
        cmd.CommandText = sql;
        Add(cmd, "@c", companyId.Trim());
        if (warrantyId is not null) Add(cmd, "@w", warrantyId.Value);
        var result = new List<WarrantyClaimRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadClaim(reader));
        return result;
    }

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------
    /// <summary>Scans one POSTED sales invoice / POS sale (by document
    /// number) for sold serials and registers a warranty for each one
    /// that lacks one. The term is the material card's warranty_months
    /// (migration 013) when set, else <paramref name="defaultMonths"/>;
    /// a serial with no term from either source is skipped, never
    /// registered with a fabricated term. Idempotent by construction.</summary>
    public WarrantyRegistrationResult RegisterFromDocument(
        string companyId, string? companyGuidKey, string sourceDocNo,
        int defaultMonths, string? terms, string userId)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        if (string.IsNullOrWhiteSpace(sourceDocNo))
            throw new ArgumentException("A source document number is required.", nameof(sourceDocNo));
        if (defaultMonths < 0)
            throw new ArgumentOutOfRangeException(nameof(defaultMonths), "The default warranty term cannot be negative.");

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            // The Guid company key chain documents carry, resolved from
            // the companies registry when the caller did not supply it.
            var guidKey = string.IsNullOrWhiteSpace(companyGuidKey)
                ? ResolveGuidKey(connection, tx, companyId.Trim())
                : companyGuidKey.Trim();

            // 1) The posted source document (either company key: chain
            // documents may carry the Guid, native ones the code).
            long docId;
            string docKind, docDate, partyName;
            long? partyId;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    SELECT doc_id, doc_kind, doc_date, party_id, COALESCE(party_name, '')
                    FROM sales_documents
                    WHERE doc_no = @no AND doc_kind IN ('SAL', 'POS') AND status = 'Posted'
                      AND (company_id = @c OR company_id = @guid) AND {NotDeleted}
                    ORDER BY doc_id LIMIT 1
                    """;
                Add(cmd, "@no", sourceDocNo.Trim());
                Add(cmd, "@c", companyId.Trim());
                Add(cmd, "@guid", guidKey);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException(
                        $"Posted sales document '{sourceDocNo.Trim()}' was not found for this company.");
                docId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                docKind = Text(reader, 1) ?? string.Empty;
                docDate = DatePart(Text(reader, 2))
                    ?? DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                partyId = reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
                partyName = Text(reader, 4) ?? string.Empty;
            }

            // 2) The serials that left on this document: outbound
            // allocations (qty < 0) on the document's stock movements.
            var sold = new List<(long SerialId, string SerialNo, long MaterialId, string MaterialCode, int CardMonths)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    SELECT DISTINCT s.serial_id, s.serial_no, s.material_id,
                           COALESCE(m.code, ''), COALESCE(m.warranty_months, 0)
                    FROM material_stock_movements mv
                    INNER JOIN stock_movement_allocations a ON a.stock_movement_id = mv.stock_movement_id
                    INNER JOIN stock_serials s ON s.serial_id = a.serial_id
                    LEFT JOIN materials m ON m.company_id = s.company_id AND m.material_id = s.material_id
                    WHERE mv.document_no = @no AND a.serial_id IS NOT NULL AND a.qty < 0
                      AND (mv.company_id = @c OR mv.company_id = @guid)
                      AND (s.company_id = @c OR s.company_id = @guid)
                    ORDER BY s.serial_no
                    """;
                Add(cmd, "@no", sourceDocNo.Trim());
                Add(cmd, "@c", companyId.Trim());
                Add(cmd, "@guid", guidKey);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    sold.Add((
                        Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                        Text(reader, 1) ?? string.Empty,
                        Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                        Text(reader, 3) ?? string.Empty,
                        Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture)));
                }
            }

            // 3) Register the missing ones.
            var registered = 0;
            var skipped = 0;
            foreach (var serial in sold)
            {
                var months = serial.CardMonths > 0 ? serial.CardMonths : defaultMonths;
                if (months <= 0) { skipped++; continue; }
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = $"""
                    INSERT INTO swa_warranties (company_id, serial_id, serial_no, material_id, material_code,
                        party_id, party_name, source_doc_kind, source_doc_id, source_doc_no,
                        sale_date, warranty_months, expiry_date, status, terms, created_by)
                    VALUES (@c, @serial, @serialNo, @material, @code, @party, @partyName,
                        @kind, @docId, @docNo, @sale, @months, @expiry, 'Active', @terms, @user)
                    ON CONFLICT (company_id, serial_id) DO NOTHING
                    """;
                Add(insert, "@c", companyId.Trim());
                Add(insert, "@serial", serial.SerialId);
                Add(insert, "@serialNo", serial.SerialNo);
                Add(insert, "@material", serial.MaterialId);
                Add(insert, "@code", serial.MaterialCode);
                Add(insert, "@party", (object?)partyId ?? DBNull.Value);
                Add(insert, "@partyName", partyName);
                Add(insert, "@kind", docKind);
                Add(insert, "@docId", docId);
                Add(insert, "@docNo", sourceDocNo.Trim());
                Add(insert, "@sale", docDate);
                Add(insert, "@months", months);
                Add(insert, "@expiry", Expiry(docDate, months));
                Add(insert, "@terms", (object?)NullIfBlank(terms) ?? DBNull.Value);
                Add(insert, "@user", userId.Trim());
                if (insert.ExecuteNonQuery() == 1) registered++; else skipped++;
            }
            tx.Commit();
            return new WarrantyRegistrationResult(registered, skipped);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Manual registration by serial lookup: the serial must
    /// exist for the company; its last outbound movement supplies the
    /// source document and sale date (falling back to the serial's
    /// current holder and today when it never moved out). Registering
    /// a serial that already has a live warranty returns that warranty
    /// unchanged — manual registration is idempotent too.</summary>
    public WarrantyRecord RegisterManual(
        string companyId, string serialNo, int months, string? terms, string userId)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        if (string.IsNullOrWhiteSpace(serialNo))
            throw new ArgumentException("A serial number is required.", nameof(serialNo));
        if (months < 0)
            throw new ArgumentOutOfRangeException(nameof(months), "The warranty term cannot be negative.");

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            long serialId, materialId;
            string materialCode, partyName = string.Empty;
            long? partyId;
            int cardMonths;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    SELECT s.serial_id, s.material_id, COALESCE(m.code, ''), s.party_id, COALESCE(m.warranty_months, 0)
                    FROM stock_serials s
                    LEFT JOIN materials m ON m.company_id = s.company_id AND m.material_id = s.material_id
                    WHERE s.company_id = @c AND s.serial_no = @serial AND {NotDeleted.Replace("is_deleted", "s.is_deleted")}
                    ORDER BY s.serial_id LIMIT 1
                    """;
                Add(cmd, "@c", companyId.Trim());
                Add(cmd, "@serial", serialNo.Trim());
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException($"Serial '{serialNo.Trim()}' was not found for this company.");
                serialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                materialId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                materialCode = Text(reader, 2) ?? string.Empty;
                partyId = reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
                cardMonths = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
            }

            var existing = FindBySerial(connection, tx, companyId, serialId);
            if (existing is not null)
            {
                tx.Commit();
                return existing;
            }

            var term = months > 0 ? months : cardMonths;
            if (term <= 0)
                throw new InvalidOperationException(
                    "No warranty term: pass the term in months or set warranty months on the material card.");

            // Sale facts from the serial's last outbound movement.
            var sourceDocNo = string.Empty;
            var saleDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    SELECT mv.document_no, mv.movement_date
                    FROM stock_movement_allocations a
                    INNER JOIN material_stock_movements mv ON mv.stock_movement_id = a.stock_movement_id
                    WHERE a.serial_id = @serial AND a.qty < 0
                    ORDER BY mv.stock_movement_id DESC LIMIT 1
                    """;
                Add(cmd, "@serial", serialId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    sourceDocNo = Text(reader, 0) ?? string.Empty;
                    saleDate = DatePart(Text(reader, 1)) ?? saleDate;
                }
            }
            if (partyId is not null)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    SELECT COALESCE(NULLIF(name_en, ''), name_ar, '') FROM parties
                    WHERE company_id = @c AND ledger_id = @party AND {NotDeleted} LIMIT 1
                    """;
                Add(cmd, "@c", companyId.Trim());
                Add(cmd, "@party", partyId.Value);
                partyName = Text(cmd.ExecuteScalar()) ?? string.Empty;
            }

            long warrantyId;
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = IsPg
                    ? """
                      INSERT INTO swa_warranties (company_id, serial_id, serial_no, material_id, material_code,
                          party_id, party_name, source_doc_kind, source_doc_id, source_doc_no,
                          sale_date, warranty_months, expiry_date, status, terms, created_by)
                      VALUES (@c, @serial, @serialNo, @material, @code, @party, @partyName,
                          '', NULL, @docNo, @sale, @months, @expiry, 'Active', @terms, @user)
                      RETURNING warranty_id
                      """
                    : """
                      INSERT INTO swa_warranties (company_id, serial_id, serial_no, material_id, material_code,
                          party_id, party_name, source_doc_kind, source_doc_id, source_doc_no,
                          sale_date, warranty_months, expiry_date, status, terms, created_by)
                      VALUES (@c, @serial, @serialNo, @material, @code, @party, @partyName,
                          '', NULL, @docNo, @sale, @months, @expiry, 'Active', @terms, @user);
                      SELECT last_insert_rowid();
                      """;
                Add(insert, "@c", companyId.Trim());
                Add(insert, "@serial", serialId);
                Add(insert, "@serialNo", serialNo.Trim());
                Add(insert, "@material", materialId);
                Add(insert, "@code", materialCode);
                Add(insert, "@party", (object?)partyId ?? DBNull.Value);
                Add(insert, "@partyName", partyName);
                Add(insert, "@docNo", sourceDocNo);
                Add(insert, "@sale", saleDate);
                Add(insert, "@months", term);
                Add(insert, "@expiry", Expiry(saleDate, term));
                Add(insert, "@terms", (object?)NullIfBlank(terms) ?? DBNull.Value);
                Add(insert, "@user", userId.Trim());
                warrantyId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            tx.Commit();
            return GetWarranty(companyId, warrantyId)
                ?? throw new InvalidOperationException("The warranty was registered but could not be re-read.");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Voids a warranty (e.g. the unit was refunded outside a
    /// claim). Only a live warranty can be voided.</summary>
    public void VoidWarranty(string companyId, long warrantyId, string userId)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE swa_warranties SET status = 'Void', updated_by = @user, updated_at = {NowExpr}
            WHERE company_id = @c AND warranty_id = @id AND status = 'Active' AND {NotDeleted}
            """;
        Add(cmd, "@user", userId.Trim());
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@id", warrantyId);
        if (cmd.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("Only an active warranty can be voided (it may already be void or expired).");
    }

    // ------------------------------------------------------------------
    // Claims
    // ------------------------------------------------------------------
    /// <summary>Files a claim against a warranty. The warranty must be
    /// in force ON the claim date: a Void warranty, or one whose expiry
    /// precedes the claim date, is SAL-7026 (the attempt also stamps a
    /// stored-Active row Expired so the register stays honest).</summary>
    public long FileClaim(string companyId, long warrantyId, string issue, string claimDate, string userId)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        if (string.IsNullOrWhiteSpace(issue))
            throw new ArgumentException("The claim issue is required.", nameof(issue));
        var claimDay = ParseDay(claimDate, nameof(claimDate));

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            var warranty = LoadWarranty(connection, tx, companyId, warrantyId)
                ?? throw new AppError(ErrorCodes.WarrantyClaimInvalid,
                    "The warranty does not exist for this company.", warrantyId.ToString(CultureInfo.InvariantCulture));
            if (warranty.StoredStatus == WarrantyStatuses.Void)
                throw new AppError(ErrorCodes.WarrantyClaimInvalid,
                    $"Warranty for serial '{warranty.SerialNo}' is void; no claim can be filed against it.", warranty.SerialNo);
            if (warranty.EffectiveStatus(claimDay) == WarrantyStatuses.Expired)
            {
                StampExpired(connection, tx, companyId, warrantyId);
                throw new AppError(ErrorCodes.WarrantyClaimInvalid,
                    $"Warranty for serial '{warranty.SerialNo}' expired on {warranty.ExpiryDate}; the claim date {claimDate} is outside cover.", warranty.SerialNo);
            }

            long claimId;
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = IsPg
                    ? """
                      INSERT INTO swa_warranty_claims (company_id, warranty_id, claim_date, issue, decision, created_by)
                      VALUES (@c, @w, @date, @issue, 'Pending', @user) RETURNING claim_id
                      """
                    : """
                      INSERT INTO swa_warranty_claims (company_id, warranty_id, claim_date, issue, decision, created_by)
                      VALUES (@c, @w, @date, @issue, 'Pending', @user); SELECT last_insert_rowid();
                      """;
                Add(insert, "@c", companyId.Trim());
                Add(insert, "@w", warrantyId);
                Add(insert, "@date", claimDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(insert, "@issue", issue.Trim());
                Add(insert, "@user", userId.Trim());
                claimId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            tx.Commit();
            return claimId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Decides a Pending claim. Approval requires an action
    /// (Repair / Replace / Return); a Return approval must carry the
    /// RMA it created (<paramref name="rmaId"/>, created by the caller
    /// through the RMA workflow so the returns machinery owns it).
    /// Deciding a claim that is not Pending is SAL-7026.</summary>
    public void DecideClaim(string companyId, long claimId, bool approve, string? action,
        string? resolutionNote, string decidedBy, long? rmaId = null)
    {
        RequireCompany(companyId);
        RequireUser(decidedBy);
        if (approve)
        {
            if (action is not (WarrantyClaimActions.Repair or WarrantyClaimActions.Replace or WarrantyClaimActions.Return))
                throw new AppError(ErrorCodes.WarrantyClaimInvalid,
                    "An approved claim requires an action: Repair, Replace or Return.", claimId.ToString(CultureInfo.InvariantCulture));
            if (action == WarrantyClaimActions.Return && rmaId is not > 0)
                throw new AppError(ErrorCodes.WarrantyClaimInvalid,
                    "A Return claim must link the RMA created for it before it can be approved.", claimId.ToString(CultureInfo.InvariantCulture));
        }
        else if (string.IsNullOrWhiteSpace(resolutionNote))
            throw new ArgumentException("A rejection requires a resolution note stating why.", nameof(resolutionNote));

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE swa_warranty_claims
                SET decision = @decision, action = @action, resolution_note = @note,
                    decided_by = @by, decided_at = @at, rma_id = @rma,
                    updated_by = @by, updated_at = {NowExpr}
                WHERE company_id = @c AND claim_id = @id AND decision = 'Pending' AND {NotDeleted}
                """;
            Add(cmd, "@decision", approve ? WarrantyClaimDecisions.Approved : WarrantyClaimDecisions.Rejected);
            Add(cmd, "@action", approve ? (object)action! : DBNull.Value);
            Add(cmd, "@note", (object?)NullIfBlank(resolutionNote) ?? DBNull.Value);
            Add(cmd, "@by", decidedBy.Trim());
            Add(cmd, "@at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Add(cmd, "@rma", approve && action == WarrantyClaimActions.Return ? (object)rmaId!.Value : DBNull.Value);
            Add(cmd, "@c", companyId.Trim());
            Add(cmd, "@id", claimId);
            if (cmd.ExecuteNonQuery() == 0)
                throw new AppError(ErrorCodes.WarrantyClaimInvalid,
                    "The claim is not pending (it was already decided, or it does not exist).",
                    claimId.ToString(CultureInfo.InvariantCulture));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private WarrantyRecord? FindBySerial(DbConnection connection, DbTransaction tx, string companyId, long serialId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT warranty_id, serial_id, serial_no, material_id, material_code, party_id, party_name,
                   source_doc_kind, source_doc_id, source_doc_no, sale_date, warranty_months, expiry_date, status, terms
            FROM swa_warranties WHERE company_id = @c AND serial_id = @serial AND {NotDeleted}
            """;
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@serial", serialId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadWarranty(reader) : null;
    }

    private WarrantyRecord? LoadWarranty(DbConnection connection, DbTransaction tx, string companyId, long warrantyId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT warranty_id, serial_id, serial_no, material_id, material_code, party_id, party_name,
                   source_doc_kind, source_doc_id, source_doc_no, sale_date, warranty_months, expiry_date, status, terms
            FROM swa_warranties WHERE company_id = @c AND warranty_id = @id AND {NotDeleted}
            {(IsPg ? "FOR UPDATE" : string.Empty)}
            """;
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@id", warrantyId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadWarranty(reader) : null;
    }

    private void StampExpired(DbConnection connection, DbTransaction tx, string companyId, long warrantyId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            UPDATE swa_warranties SET status = 'Expired', updated_at = {NowExpr}
            WHERE company_id = @c AND warranty_id = @id AND status = 'Active' AND {NotDeleted}
            """;
        Add(cmd, "@c", companyId.Trim());
        Add(cmd, "@id", warrantyId);
        cmd.ExecuteNonQuery();
    }

    private static WarrantyRecord ReadWarranty(DbDataReader reader) => new(
        Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
        Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
        Text(reader, 2) ?? string.Empty,
        Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
        Text(reader, 4) ?? string.Empty,
        reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
        Text(reader, 6) ?? string.Empty,
        Text(reader, 7) ?? string.Empty,
        reader.IsDBNull(8) ? null : Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture),
        Text(reader, 9) ?? string.Empty,
        DatePart(Text(reader, 10)) ?? string.Empty,
        Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture),
        DatePart(Text(reader, 12)) ?? string.Empty,
        Text(reader, 13) ?? WarrantyStatuses.Active,
        Text(reader, 14));

    private static WarrantyClaimRecord ReadClaim(DbDataReader reader) => new(
        Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
        Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
        DatePart(Text(reader, 2)) ?? string.Empty,
        Text(reader, 3) ?? string.Empty,
        Text(reader, 4) ?? WarrantyClaimDecisions.Pending,
        Text(reader, 5),
        Text(reader, 6),
        Text(reader, 7),
        Text(reader, 8),
        reader.IsDBNull(9) ? null : Convert.ToInt64(reader.GetValue(9), CultureInfo.InvariantCulture));

    /// <summary>The company's Guid key from the companies registry
    /// (the SrtRepo.ResolveGuidKey pattern); falls back to the code
    /// itself when the registry has no row.</summary>
    private string ResolveGuidKey(DbConnection connection, DbTransaction tx, string companyCode)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT company_id FROM companies WHERE company_code = @code LIMIT 1";
        Add(cmd, "@code", companyCode);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? companyCode : Convert.ToString(value, CultureInfo.InvariantCulture) ?? companyCode;
    }

    private static string Expiry(string saleDate, int months)
    {
        var sale = ParseDay(saleDate, nameof(saleDate));
        return sale.AddMonths(months).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateOnly ParseDay(string? value, string paramName)
    {
        var part = DatePart(value);
        if (part is null || !DateOnly.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            throw new ArgumentException($"A valid date (yyyy-MM-dd) is required; got '{value}'.", paramName);
        return day;
    }

    /// <summary>The date part of a stored timestamp ('yyyy-MM-dd' or
    /// 'yyyy-MM-dd HH:mm:ss' / ISO), or null when blank.</summary>
    private static string? DatePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length >= 10 ? trimmed[..10] : trimmed;
    }

    private static string? Text(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string? Text(object? value)
        => value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User is required.", nameof(userId));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgresql" or "postgres" or "npgsql" => "PostgreSQL",
        _ => provider.Trim()
    };
}
