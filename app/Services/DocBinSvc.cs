using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;
using ShouTech.Infrastructure;

namespace ShouTech.App.Services;

/// <summary>
/// Recycle-bin lifecycle for DOCUMENTS and JOURNAL ENTRIES
/// (SOFT_DELETE_STUDY, migration 122). Cards keep their existing
/// flag-only path through <see cref="RecycleBinSvc"/>; this service owns
/// the subjects that carry posting effects.
///
/// Posting semantics (status-preserving compensation): binning a POSTED
/// document never touches the original records. Inside ONE transaction
/// it writes compensation records in every effect store — a posted GL
/// clone with debit/credit swapped (the original entry keeps status 1,
/// so the pair nets to zero under both trial-balance conventions),
/// additive party-ledger mirrors, inverse stock movements driven
/// through the one StockValuationEngine from the stored original
/// movement rows, and negated tax-register mirrors under a '-BIN'
/// source kind. Restore writes the reinstatement set and removes the
/// tax mirrors; purge removes only the business record (header+lines) —
/// the ledger chain, which nets to zero, stays as the audit trail.
/// Exactly-once is guarded by bin_posting_events, checked and written
/// inside the same transaction as the effects.
/// </summary>
public static class DocBinSvc
{
    private static string Company => AppState.CurrentCompanyCode ?? string.Empty;
    private static bool IsPg => DatabaseRuntime.IsPostgreSql();
    private static string ProviderName => IsPg ? "PostgreSQL" : "SQLite";

    // ------------------------------------------------------------------
    // Plumbing (InvRepo pattern: provider-agnostic ADO, $ parameters).
    // ------------------------------------------------------------------
    private static DbConnection Open()
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        DbConnection connection = IsPg
            ? new NpgsqlConnection(cs)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(cs));
        connection.Open();
        return connection;
    }

    private static DbTransaction Begin(DbConnection connection)
        => connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: false)
            : connection.BeginTransaction();

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    private static DbCommand Cmd(DbConnection c, DbTransaction tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static object GuidParam(Guid id) => IsPg ? id : id.ToString("D");

    private static Guid? ResolveCompanyGuid(DbConnection c, DbTransaction tx)
    {
        using var cmd = Cmd(c, tx, "SELECT company_id FROM companies WHERE company_code=$company;");
        Add(cmd, "$company", Company);
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull) return null;
        return Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var guid) && guid != Guid.Empty
            ? guid : null;
    }

    /// <summary>The stored doc_kind / source_doc_type code for a kind
    /// (the mapping lives on InvDoc; this keeps one source of truth).</summary>
    private static string KindCodeOf(InvDocKind kind) => new InvDoc { Kind = kind }.KindCode;

    private static string NowText()
        => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static AppError NotFound() => new(ErrorCodes.BinItemNotFound,
        "The item was not found in the recycle bin.");
    private static AppError StateConflict() => new(ErrorCodes.BinStateConflict,
        "The item's recycle-bin state does not allow this operation.");

    // ------------------------------------------------------------------
    // Documents — soft delete
    // ------------------------------------------------------------------
    public static void SoftDeleteDocument(long docId, InvDocKind kind, string actor)
    {
        if (kind == InvDocKind.PosSale)
            throw new AppError(ErrorCodes.BinKindExcluded,
                "POS sales are corrected inside the POS session flow, not through the recycle bin.");
        SoftDeleteDocumentCore(
            kind.IsSales() ? "sales_documents" : "purchase_documents",
            docId, KindCodeOf(kind), actor);
    }

    /// <summary>Chain documents (SO/PO/RFQ/SQ/DLV/RCV/RCR) share the
    /// same tables and effect stores under their own kind codes; their
    /// bin lifecycle is the same core.</summary>
    public static void SoftDeleteChainDocument(bool isSalesSide, long docId, string kindCode, string actor)
        => SoftDeleteDocumentCore(
            isSalesSide ? "sales_documents" : "purchase_documents", docId, kindCode, actor);

    private static void SoftDeleteDocumentCore(string table, long docId, string kindCode, string actor)
    {
        actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor;
        try
        {
            using var connection = Open();
            using var tx = Begin(connection);
            var doc = LoadDocument(connection, tx, table, docId, kindCode)
                ?? throw NotFound();
            if (doc.IsDeleted) throw StateConflict();
            using (var originGuard = connection.CreateCommand())
            {
                originGuard.Transaction = tx;
                originGuard.CommandText = $"SELECT original_doc_id FROM {table} WHERE doc_id=@id AND doc_kind=@kind;";
                Add(originGuard, "@id", docId);
                Add(originGuard, "@kind", kindCode);
                var originalId = originGuard.ExecuteScalar();
                if (originalId is not null and not DBNull)
                    throw new InvalidOperationException("A document generated from an original document cannot be deleted independently. Open the original document to manage it.");
            }
            // A cancelled document was already neutralized by its own
            // cancel flow (compensating rows under other numbers);
            // binning it would un-net those compensations.
            if (doc.Status == "Cancelled") throw StateConflict();
            GuardDocumentBinning(connection, tx, table, docId, kindCode);

            if (doc.Status == "Posted")
            {
                var eventId = Guid.NewGuid();
                var (firstClone, taxMirrorKind) = NeutralizeDocument(connection, tx, doc, kindCode, eventId, actor);
                WriteEvent(connection, tx, Company, "DOC", table, kindCode,
                    docId.ToString(CultureInfo.InvariantCulture), "NEUTRALIZE", firstClone, actor, eventId,
                    taxMirrorKind);
            }
            FlipDocumentDeleted(connection, tx, table, docId, deleted: true, actor);
            tx.Commit();
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.BinStateConflict, ex, "DocBinSvc.SoftDeleteDocument");
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Documents — restore
    // ------------------------------------------------------------------
    public static void RestoreDocument(long docId, InvDocKind kind, string actor)
        => RestoreDocumentCore(
            kind.IsSales() ? "sales_documents" : "purchase_documents",
            docId, KindCodeOf(kind), actor);

    public static void RestoreChainDocument(bool isSalesSide, long docId, string kindCode, string actor)
        => RestoreDocumentCore(
            isSalesSide ? "sales_documents" : "purchase_documents", docId, kindCode, actor);

    private static void RestoreDocumentCore(string table, long docId, string kindCode, string actor)
    {
        actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor;
        try
        {
            using var connection = Open();
            using var tx = Begin(connection);
            var doc = LoadDocument(connection, tx, table, docId, kindCode)
                ?? throw NotFound();
            if (!doc.IsDeleted) throw StateConflict();

            if (doc.Status == "Posted")
            {
                var openEvent = FindOpenEvent(connection, tx, Company, "DOC", table,
                    docId.ToString(CultureInfo.InvariantCulture));
                if (openEvent is null) throw StateConflict();
                var eventId = Guid.NewGuid();
                ReinstateDocument(connection, tx, doc, kindCode, openEvent.Value, eventId, actor);
                WriteEvent(connection, tx, Company, "DOC", table, kindCode,
                    docId.ToString(CultureInfo.InvariantCulture), "REINSTATE", null, actor, eventId,
                    $"OF {openEvent.Value.Id:D}");
            }
            FlipDocumentDeleted(connection, tx, table, docId, deleted: false, actor);
            tx.Commit();
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.BinStateConflict, ex, "DocBinSvc.RestoreDocument");
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Documents — permanent delete. The business record goes; the
    // ledger chain (which nets to zero) remains as the audit trail.
    // ------------------------------------------------------------------
    public static void PurgeDocument(long docId, InvDocKind kind, string actor)
        => PurgeDocumentCore(
            kind.IsSales() ? "sales_documents" : "purchase_documents",
            docId, KindCodeOf(kind), actor);

    public static void PurgeChainDocument(bool isSalesSide, long docId, string kindCode, string actor)
        => PurgeDocumentCore(
            isSalesSide ? "sales_documents" : "purchase_documents", docId, kindCode, actor);

    private static void PurgeDocumentCore(string table, long docId, string kindCode, string actor)
    {
        var lineTable = table == "sales_documents" ? "sales_document_lines" : "purchase_document_lines";
        try
        {
            using var connection = Open();
            using var tx = Begin(connection);
            var doc = LoadDocument(connection, tx, table, docId, kindCode)
                ?? throw NotFound();
            if (!doc.IsDeleted) throw StateConflict();
            using (var del = Cmd(connection, tx, $"DELETE FROM {lineTable} WHERE doc_id=$id;"))
            { Add(del, "$id", docId); del.ExecuteNonQuery(); }
            using (var del = Cmd(connection, tx,
                $"DELETE FROM {table} WHERE doc_id=$id AND (company_id=$company OR company_id=$guid);"))
            {
                Add(del, "$id", docId);
                Add(del, "$company", Company);
                Add(del, "$guid", ResolveCompanyGuid(connection, tx)?.ToString("D") ?? Company);
                if (del.ExecuteNonQuery() == 0) throw NotFound();
            }
            tx.Commit();
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.BinPurgeStillReferenced, ex, "DocBinSvc.PurgeDocument");
            throw new AppError(ErrorCodes.BinPurgeStillReferenced,
                "The document could not be permanently deleted because other records still reference it.", ex);
        }
    }

    // ------------------------------------------------------------------
    // Journals — soft delete / restore / purge
    // ------------------------------------------------------------------
    public static void SoftDeleteJournal(Guid entryId, string actor)
    {
        actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor;
        try
        {
            using var connection = Open();
            using var tx = Begin(connection);
            var entry = LoadJournal(connection, tx, entryId) ?? throw NotFound();
            if (entry.IsDeleted) throw StateConflict();
            // A reversal pair is one business correction: neither side
            // bins alone (status 2 = already reversed; a non-null
            // reversal link = this entry IS a reversal entry).
            if (entry.Status == 2 || entry.ReversalOfRaw is not null) throw StateConflict();

            if (entry.Status == 1)
            {
                var eventId = Guid.NewGuid();
                var cloneId = CloneJournalEntry(connection, tx, entry, swapped: true,
                    "BIN-" + entry.DocumentNumber + "-" + eventId.ToString("N")[..6], actor,
                    $"Recycle bin neutralization of {entry.DocumentNumber} [BIN {eventId:D}]",
                    ChainHead(connection, tx, entry));
                MirrorPartyRowsForJournal(connection, tx, entry.Id, cloneId,
                    $"BIN:NEUTRALIZE {eventId:D}");
                WriteEvent(connection, tx, entry.CompanyKey, "JOURNAL", "journal_entries",
                    entry.SourceDocType ?? "JRN", entryId.ToString("D"), "NEUTRALIZE",
                    cloneId, actor, eventId);
            }
            FlipJournalDeleted(connection, tx, entryId, deleted: true, actor);
            tx.Commit();
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.BinStateConflict, ex, "DocBinSvc.SoftDeleteJournal");
            throw;
        }
    }

    public static void RestoreJournal(Guid entryId, string actor)
    {
        actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor;
        try
        {
            using var connection = Open();
            using var tx = Begin(connection);
            var entry = LoadJournal(connection, tx, entryId) ?? throw NotFound();
            if (!entry.IsDeleted) throw StateConflict();

            if (entry.Status == 1)
            {
                var openEvent = FindOpenEvent(connection, tx, entry.CompanyKey, "JOURNAL",
                    "journal_entries", entryId.ToString("D"));
                if (openEvent is null) throw StateConflict();
                var eventId = Guid.NewGuid();
                var cloneId = CloneJournalEntry(connection, tx, entry, swapped: false,
                    "BIN-R-" + entry.DocumentNumber + "-" + eventId.ToString("N")[..6], actor,
                    $"Recycle bin reinstatement of {entry.DocumentNumber} [BIN {eventId:D}]",
                    FindBinClone(connection, tx, entry, openEvent.Value.Id));
                MirrorPartyRowsWithNote(connection, tx, $"BIN:NEUTRALIZE {openEvent.Value.Id:D}",
                    cloneId, $"BIN:REINSTATE {eventId:D}");
                WriteEvent(connection, tx, entry.CompanyKey, "JOURNAL", "journal_entries",
                    entry.SourceDocType ?? "JRN", entryId.ToString("D"), "REINSTATE",
                    cloneId, actor, eventId, $"OF {openEvent.Value.Id:D}");
            }
            FlipJournalDeleted(connection, tx, entryId, deleted: false, actor);
            tx.Commit();
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.BinStateConflict, ex, "DocBinSvc.RestoreJournal");
            throw;
        }
    }

    public static void PurgeJournal(Guid entryId, string actor)
    {
        try
        {
            using var connection = Open();
            using var tx = Begin(connection);
            var entry = LoadJournal(connection, tx, entryId) ?? throw NotFound();
            if (!entry.IsDeleted) throw StateConflict();
            if (entry.Status == 1)
                throw new AppError(ErrorCodes.BinPostedJournalPurgeBlocked,
                    "A posted journal entry cannot be permanently deleted.");
            using (var del = Cmd(connection, tx,
                "DELETE FROM journal_entry_lines WHERE journal_entry_id=$id;"))
            { Add(del, "$id", GuidParam(entryId)); del.ExecuteNonQuery(); }
            // The entry's own lifecycle audit rows belong to the record
            // being purged; the purge itself is captured by the
            // deletion_audit_log trigger snapshot of the header row.
            using (var del = Cmd(connection, tx,
                "DELETE FROM journal_audit_events WHERE journal_entry_id=$id;"))
            { Add(del, "$id", GuidParam(entryId)); del.ExecuteNonQuery(); }
            using (var del = Cmd(connection, tx,
                "DELETE FROM journal_entries WHERE journal_entry_id=$id;"))
            {
                Add(del, "$id", GuidParam(entryId));
                if (del.ExecuteNonQuery() == 0) throw NotFound();
            }
            tx.Commit();
        }
        catch (AppError) { throw; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.BinPurgeStillReferenced, ex, "DocBinSvc.PurgeJournal");
            throw new AppError(ErrorCodes.BinPurgeStillReferenced,
                "The journal entry could not be permanently deleted because other records still reference it.", ex);
        }
    }

    // ------------------------------------------------------------------
    // Row shapes
    // ------------------------------------------------------------------
    private sealed record DocRow(long DocId, string DocNo, string Status, bool IsDeleted, long? PartyId);

    private sealed class JournalRow
    {
        public required Guid Id;
        public required object IdRaw;
        public required string CompanyKey;
        public required object CompanyRaw;
        public required object EntryDateRaw;
        public required string DocumentNumber;
        public required int Status;
        public string? SourceDocType;
        public object? SourceDocIdRaw;
        public object? SourceDocNoRaw;
        public object? ReversalOfRaw;
        public bool IsDeleted;
        public object? FiscalPeriodRaw;
        public object? ReferenceRaw;
        public object? CurrencyRaw;
        public object? ExchangeRaw;
        public object? BaseCurrencyRaw;
    }

    private static DocRow? LoadDocument(DbConnection c, DbTransaction tx, string table, long docId, string kindCode)
    {
        using var cmd = Cmd(c, tx, $"""
            SELECT doc_no, status, is_deleted, party_id, doc_kind
            FROM {table}
            WHERE doc_id=$id AND (company_id=$company OR company_id=$guid);
            """);
        Add(cmd, "$id", docId);
        Add(cmd, "$company", Company);
        Add(cmd, "$guid", ResolveCompanyGuid(c, tx)?.ToString("D") ?? Company);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var storedKind = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture);
        if (!string.Equals(storedKind, kindCode, StringComparison.OrdinalIgnoreCase)) return null;
        return new DocRow(
            docId,
            Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture));
    }

    private static void GuardDocumentBinning(DbConnection c, DbTransaction tx, string table, long docId, string kindCode)
    {
        var guid = ResolveCompanyGuid(c, tx)?.ToString("D") ?? Company;
        // payment_allocations keys documents by side, not by table name
        // (CHECK constraint: doc_table IN ('SALES','PURCHASE')).
        var allocTable = table == "sales_documents" ? "SALES" : "PURCHASE";
        long Count(string sql)
        {
            using var cmd = Cmd(c, tx, sql);
            Add(cmd, "$id", docId);
            Add(cmd, "$kind", kindCode);
            Add(cmd, "$table", allocTable);
            Add(cmd, "$company", Company);
            Add(cmd, "$guid", guid);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (Count("SELECT COUNT(*) FROM payment_allocations WHERE doc_table=$table AND doc_id=$id;") > 0)
            throw new AppError(ErrorCodes.BinBlockedByAllocations,
                "The document cannot be moved to the recycle bin because payments are allocated to it.");
        if (Count("""
            SELECT COUNT(*) FROM document_links
            WHERE (company_id=$company OR company_id=$guid)
              AND ((source_doc_kind=$kind AND source_doc_id=$id)
                OR (target_doc_kind=$kind AND target_doc_id=$id));
            """) > 0)
            throw new AppError(ErrorCodes.BinBlockedByLinks,
                "The document cannot be moved to the recycle bin because other documents are linked to it.");
        if (Count("""
            SELECT COUNT(*) FROM document_reversals
            WHERE (company_id=$company OR company_id=$guid) AND status='Completed'
              AND ((original_doc_kind=$kind AND original_doc_id=$id)
                OR (reversal_doc_kind=$kind AND reversal_doc_id=$id));
            """) > 0)
            throw new AppError(ErrorCodes.BinBlockedByLinks,
                "The document cannot be moved to the recycle bin because it takes part in a completed reversal.");
    }

    private static void FlipDocumentDeleted(DbConnection c, DbTransaction tx, string table, long docId, bool deleted, string actor)
    {
        using var cmd = Cmd(c, tx, deleted
            ? $"UPDATE {table} SET is_deleted=1, deleted_at=$at, deleted_by=$by WHERE doc_id=$id AND is_deleted=0;"
            : $"UPDATE {table} SET is_deleted=0, deleted_at=NULL, deleted_by=NULL WHERE doc_id=$id AND is_deleted=1;");
        Add(cmd, "$id", docId);
        if (deleted) { Add(cmd, "$at", NowText()); Add(cmd, "$by", actor); }
        if (cmd.ExecuteNonQuery() == 0) throw StateConflict();
    }

    private static void FlipJournalDeleted(DbConnection c, DbTransaction tx, Guid entryId, bool deleted, string actor)
    {
        using var cmd = Cmd(c, tx, deleted
            ? "UPDATE journal_entries SET is_deleted=1, deleted_at=$at, deleted_by=$by WHERE journal_entry_id=$id AND is_deleted=0;"
            : "UPDATE journal_entries SET is_deleted=0, deleted_at=NULL, deleted_by=NULL WHERE journal_entry_id=$id AND is_deleted=1;");
        if (IsPg) cmd.CommandText = deleted
            ? "UPDATE journal_entries SET is_deleted=TRUE, deleted_at=$at, deleted_by=$by WHERE journal_entry_id=$id AND is_deleted=FALSE;"
            : "UPDATE journal_entries SET is_deleted=FALSE, deleted_at=NULL, deleted_by=NULL WHERE journal_entry_id=$id AND is_deleted=TRUE;";
        Add(cmd, "$id", GuidParam(entryId));
        if (deleted) { Add(cmd, "$at", NowText()); Add(cmd, "$by", actor); }
        if (cmd.ExecuteNonQuery() == 0) throw StateConflict();
    }

    // ------------------------------------------------------------------
    // bin_posting_events — the exactly-once ledger
    // ------------------------------------------------------------------
    private static void WriteEvent(DbConnection c, DbTransaction tx, string companyKey,
        string subjectKind, string docTable, string docKind, string docKey, string action,
        Guid? journalEntryId, string actor, Guid eventId, string? note = null)
    {
        using var cmd = Cmd(c, tx, """
            INSERT INTO bin_posting_events
                (event_id, company_key, subject_kind, doc_table, doc_kind, doc_key,
                 action, journal_entry_id, actor, created_at, note)
            VALUES ($event, $ck, $sk, $dt, $dkind, $dkey, $action, $je, $actor, $at, $note);
            """);
        Add(cmd, "$event", eventId.ToString("D"));
        Add(cmd, "$ck", companyKey);
        Add(cmd, "$sk", subjectKind);
        Add(cmd, "$dt", docTable);
        Add(cmd, "$dkind", docKind);
        Add(cmd, "$dkey", docKey);
        Add(cmd, "$action", action);
        Add(cmd, "$je", journalEntryId?.ToString("D"));
        Add(cmd, "$actor", actor);
        Add(cmd, "$at", NowText());
        Add(cmd, "$note", note);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The NEUTRALIZE event not yet answered by a REINSTATE
    /// (the REINSTATE row names it in its note: 'OF &lt;eventId&gt;').
    /// The NEUTRALIZE note carries the tax mirror kind it used.</summary>
    private static (Guid Id, string? Note)? FindOpenEvent(DbConnection c, DbTransaction tx, string companyKey,
        string subjectKind, string docTable, string docKey)
    {
        using var cmd = Cmd(c, tx, """
            SELECT n.event_id, n.note FROM bin_posting_events n
            WHERE n.company_key=$ck AND n.subject_kind=$sk AND n.doc_table=$dt AND n.doc_key=$dk
              AND n.action='NEUTRALIZE'
              AND NOT EXISTS (
                  SELECT 1 FROM bin_posting_events r
                  WHERE r.action='REINSTATE' AND r.company_key=n.company_key
                    AND r.subject_kind=n.subject_kind AND r.doc_table=n.doc_table
                    AND r.doc_key=n.doc_key AND r.note = 'OF ' || n.event_id)
            ORDER BY n.created_at DESC LIMIT 1;
            """);
        Add(cmd, "$ck", companyKey);
        Add(cmd, "$sk", subjectKind);
        Add(cmd, "$dt", docTable);
        Add(cmd, "$dk", docKey);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        if (!Guid.TryParse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture), out var id))
            return null;
        return (id, reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    // GL — find a document's posted entries; clone an entry
    // ------------------------------------------------------------------
    private static List<JournalRow> FindDocumentEntries(DbConnection c, DbTransaction tx, long docId, string kindCode)
    {
        var guid = ResolveCompanyGuid(c, tx);
        var rows = new List<JournalRow>();
        using var cmd = Cmd(c, tx, """
            SELECT journal_entry_id, company_id, entry_date, document_number, status,
                   source_doc_type, source_doc_id, source_doc_no, reversal_of_entry_id,
                   fiscal_period_id, reference_number,
                   currency_code, exchange_rate, base_currency_code, is_deleted
            FROM journal_entries
            WHERE company_id=$guid AND source_doc_type=$type AND source_doc_id=$source
              AND status=1 AND reversal_of_entry_id IS NULL;
            """);
        Add(cmd, "$guid", guid is null ? Company : GuidParam(guid.Value));
        Add(cmd, "$type", kindCode);
        Add(cmd, "$source", docId.ToString(CultureInfo.InvariantCulture));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadJournalRow(reader));
        return rows;
    }

    private static JournalRow? LoadJournal(DbConnection c, DbTransaction tx, Guid entryId)
    {
        var guid = ResolveCompanyGuid(c, tx);
        using var cmd = Cmd(c, tx, """
            SELECT journal_entry_id, company_id, entry_date, document_number, status,
                   source_doc_type, source_doc_id, source_doc_no, reversal_of_entry_id,
                   fiscal_period_id, reference_number,
                   currency_code, exchange_rate, base_currency_code, is_deleted
            FROM journal_entries
            WHERE journal_entry_id=$id AND company_id=$guid;
            """);
        Add(cmd, "$id", GuidParam(entryId));
        Add(cmd, "$guid", guid is null ? Company : GuidParam(guid.Value));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJournalRow(reader) : null;
    }

    private static JournalRow ReadJournalRow(DbDataReader reader) => new()
    {
        Id = Guid.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!),
        IdRaw = reader.GetValue(0),
        CompanyKey = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
        CompanyRaw = reader.GetValue(1),
        EntryDateRaw = reader.GetValue(2),
        DocumentNumber = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty,
        Status = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
        SourceDocType = reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture),
        SourceDocIdRaw = reader.IsDBNull(6) ? null : reader.GetValue(6),
        SourceDocNoRaw = reader.IsDBNull(7) ? null : reader.GetValue(7),
        ReversalOfRaw = reader.IsDBNull(8) ? null : reader.GetValue(8),
        FiscalPeriodRaw = reader.IsDBNull(9) ? null : reader.GetValue(9),
        ReferenceRaw = reader.IsDBNull(10) ? null : reader.GetValue(10),
        CurrencyRaw = reader.IsDBNull(11) ? null : reader.GetValue(11),
        ExchangeRaw = reader.IsDBNull(12) ? null : reader.GetValue(12),
        BaseCurrencyRaw = reader.IsDBNull(13) ? null : reader.GetValue(13),
        IsDeleted = Convert.ToBoolean(reader.GetValue(14), CultureInfo.InvariantCulture),
    };

    /// <summary>Walks the reversal chain from an entry to its current
    /// head. journal_entries.reversal_of_entry_id is UNIQUE — an entry
    /// can be the target of exactly one compensation — so each new bin
    /// clone anchors at the head of the chain (the entry whose effects
    /// are currently live), forming orig ← N1 ← R1 ← N2 ← R2 …</summary>
    private static object ChainHead(DbConnection c, DbTransaction tx, JournalRow start)
    {
        var current = start.IdRaw;
        for (var i = 0; i < 100; i++)
        {
            using var cmd = Cmd(c, tx,
                "SELECT journal_entry_id FROM journal_entries WHERE reversal_of_entry_id=$target LIMIT 1;");
            Add(cmd, "$target", current);
            var value = cmd.ExecuteScalar();
            if (value is null or DBNull) return current;
            current = value;
        }
        return current;
    }

    /// <summary>Finds one specific bin clone of an entry by the event
    /// marker in its description ('[BIN &lt;eventId&gt;]'), walking the
    /// entry's own reversal chain — one event can neutralize several
    /// entries of the same document, and each keeps its own chain.</summary>
    private static object? FindBinClone(DbConnection c, DbTransaction tx, JournalRow src, Guid eventId)
    {
        var marker = $"[BIN {eventId:D}]";
        var current = src.IdRaw;
        for (var i = 0; i < 100; i++)
        {
            using var cmd = Cmd(c, tx,
                "SELECT journal_entry_id, description FROM journal_entries WHERE reversal_of_entry_id=$target LIMIT 1;");
            Add(cmd, "$target", current);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var childId = reader.GetValue(0);
            var description = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (description is not null && description.Contains(marker, StringComparison.Ordinal))
                return childId;
            current = childId;
        }
        return null;
    }

    /// <summary>Inserts a posted clone of a journal entry — amounts
    /// swapped (neutralize) or as-is (reinstate) — linked back through
    /// reversal_of_entry_id. The source entry is never modified.</summary>
    private static Guid CloneJournalEntry(DbConnection c, DbTransaction tx, JournalRow src,
        bool swapped, string documentNumber, string actor, string description,
        object? reversalOfRaw = null)
    {
        var newId = Guid.NewGuid();
        // The database enforces the product lifecycle (insert as draft,
        // add lines, then post) — the clone follows it exactly.
        using (var cmd = Cmd(c, tx, $"""
            INSERT INTO journal_entries
                (journal_entry_id, company_id, entry_date, description, status, created_by,
                 reversal_of_entry_id, fiscal_period_id, reference_number,
                 currency_code, exchange_rate, document_number, source_doc_type, source_doc_id,
                 source_doc_no, base_currency_code, is_deleted)
            VALUES ($id, $company, $date, $desc, 0, $actor, $revof,
                    $period, $ref, $currency, $rate, $docno, $stype, $sid, $sno, $basecur,
                    {(IsPg ? "FALSE" : "0")});
            """))
        {
            Add(cmd, "$id", GuidParam(newId));
            Add(cmd, "$company", src.CompanyRaw);
            Add(cmd, "$date", src.EntryDateRaw);
            Add(cmd, "$desc", description);
            Add(cmd, "$actor", actor);
            Add(cmd, "$revof", reversalOfRaw ?? src.IdRaw);
            Add(cmd, "$period", src.FiscalPeriodRaw);
            Add(cmd, "$ref", src.ReferenceRaw);
            Add(cmd, "$currency", src.CurrencyRaw);
            Add(cmd, "$rate", src.ExchangeRaw);
            Add(cmd, "$docno", documentNumber);
            Add(cmd, "$stype", src.SourceDocType);
            Add(cmd, "$sid", src.SourceDocIdRaw);
            Add(cmd, "$sno", src.SourceDocNoRaw);
            Add(cmd, "$basecur", src.BaseCurrencyRaw);
            cmd.ExecuteNonQuery();
        }

        var lines = new List<object[]>();
        using (var cmd = Cmd(c, tx, """
            SELECT line_number, account_id, debit, credit, cost_center, note,
                   base_debit, base_credit, exchange_rate
            FROM journal_entry_lines WHERE journal_entry_id=$src ORDER BY line_number;
            """))
        {
            Add(cmd, "$src", src.IdRaw);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new object[9];
                for (var i = 0; i < 9; i++) row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                lines.Add(row);
            }
        }
        foreach (var line in lines)
        {
            using var cmd = Cmd(c, tx, """
                INSERT INTO journal_entry_lines
                    (journal_entry_id, line_number, account_id,
                     debit, credit, cost_center, note, base_debit, base_credit, exchange_rate)
                VALUES ($eid, $no, $account, $debit, $credit, $cc, $note, $bdebit, $bcredit, $rate);
                """);
            Add(cmd, "$eid", GuidParam(newId));
            Add(cmd, "$no", line[0]);
            Add(cmd, "$account", line[1]);
            Add(cmd, "$debit", swapped ? line[3] : line[2]);
            Add(cmd, "$credit", swapped ? line[2] : line[3]);
            Add(cmd, "$cc", line[4] is DBNull ? null : line[4]);
            Add(cmd, "$note", line[5] is DBNull ? null : line[5]);
            Add(cmd, "$bdebit", swapped ? line[7] : line[6]);
            Add(cmd, "$bcredit", swapped ? line[6] : line[7]);
            Add(cmd, "$rate", line[8] is DBNull ? null : line[8]);
            cmd.ExecuteNonQuery();
        }
        using (var post = Cmd(c, tx, """
            UPDATE journal_entries SET status=1, posted_by=$actor, posted_at=CURRENT_TIMESTAMP
            WHERE journal_entry_id=$id AND status=0;
            """))
        {
            Add(post, "$id", GuidParam(newId));
            Add(post, "$actor", actor);
            if (post.ExecuteNonQuery() == 0) throw StateConflict();
        }
        return newId;
    }

    // ------------------------------------------------------------------
    // Party ledger mirrors (additive both ways, note-marked)
    // ------------------------------------------------------------------
    private static void MirrorPartyRows(DbConnection c, DbTransaction tx, string whereSql,
        Action<DbCommand> bind, object? journalEntryRaw, string note)
    {
        var rows = new List<object[]>();
        using (var cmd = Cmd(c, tx, $"""
            SELECT company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date,
                   debit, credit, currency_code, base_debit, base_credit, exchange_rate, journal_entry_id
            FROM party_ledger WHERE {whereSql};
            """))
        {
            bind(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new object[14];
                for (var i = 0; i < 14; i++) row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                rows.Add(row);
            }
        }
        foreach (var row in rows)
        {
            using var cmd = Cmd(c, tx, """
                INSERT INTO party_ledger
                    (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date,
                     debit, credit, currency_code, note, journal_entry_id, base_debit, base_credit, exchange_rate)
                VALUES ($company, $pk, $pid, $dk, $did, $dno, $date,
                        $debit, $credit, $currency, $note, $je, $bdebit, $bcredit, $rate);
                """);
            Add(cmd, "$company", row[0]);
            Add(cmd, "$pk", row[1]);
            Add(cmd, "$pid", row[2]);
            Add(cmd, "$dk", row[3]);
            Add(cmd, "$did", row[4]);
            Add(cmd, "$dno", row[5] is DBNull ? null : row[5]);
            Add(cmd, "$date", row[6]);
            Add(cmd, "$debit", row[8]);   // swapped
            Add(cmd, "$credit", row[7]);  // swapped
            Add(cmd, "$currency", row[9] is DBNull ? null : row[9]);
            Add(cmd, "$note", note);
            Add(cmd, "$je", journalEntryRaw ?? (row[13] is DBNull ? null : row[13]));
            Add(cmd, "$bdebit", row[11] is DBNull ? null : row[11]);
            Add(cmd, "$bcredit", row[10] is DBNull ? null : row[10]);
            Add(cmd, "$rate", row[12] is DBNull ? null : row[12]);
            cmd.ExecuteNonQuery();
        }
    }

    private static void MirrorPartyRowsForJournal(DbConnection c, DbTransaction tx,
        Guid entryId, Guid cloneId, string note)
        => MirrorPartyRows(c, tx, "journal_entry_id=$je",
            cmd => Add(cmd, "$je", GuidParam(entryId)), GuidParam(cloneId), note);

    private static void MirrorPartyRowsWithNote(DbConnection c, DbTransaction tx,
        string sourceNote, Guid? cloneId, string note)
        => MirrorPartyRows(c, tx, "note=$note",
            cmd => Add(cmd, "$note", sourceNote),
            cloneId is null ? null : GuidParam(cloneId.Value), note);

    private static void MirrorPartyRowsForDocument(DbConnection c, DbTransaction tx,
        DocRow doc, string kindCode, Guid? cloneId, string note)
    {
        var guid = ResolveCompanyGuid(c, tx)?.ToString("D") ?? Company;
        MirrorPartyRows(c, tx,
            "(company_id=$company OR company_id=$guid) AND doc_kind=$kind AND doc_id=$id",
            cmd =>
            {
                Add(cmd, "$company", Company);
                Add(cmd, "$guid", guid);
                Add(cmd, "$kind", kindCode);
                Add(cmd, "$id", doc.DocId);
            },
            cloneId is null ? null : GuidParam(cloneId.Value), note);
    }

    // ------------------------------------------------------------------
    // Stock mirrors — driven through the one valuation engine from the
    // stored original movement rows.
    // ------------------------------------------------------------------
    private static string[] MovementTypes(string kindCode) => kindCode switch
    {
        "CNT" => new[] { "CNT", "ADJ" },
        // Every other kind posts movements under its own kind code
        // (SAL/SRT/PUR/PRT/ENT/EXT/OPN/TRN and the chain DLV/RCV/RCR).
        _ => new[] { kindCode },
    };

    private static void ApplyStockMirrors(DbConnection c, DbTransaction tx, DocRow doc,
        string kindCode, bool invert, string note, string actor)
    {
        var types = MovementTypes(kindCode);
        if (types.Length == 0) return;
        var inList = string.Join(", ", Array.ConvertAll(types, t => $"'{t}'"));
        var movements = new List<(long Id, long WarehouseId, long MaterialId, string Date,
            string Type, decimal QtyIn, decimal QtyOut, decimal? UnitCost, decimal? ValuationRate)>();
        using (var cmd = Cmd(c, tx, $"""
            SELECT stock_movement_id, warehouse_id, material_id, movement_date, movement_type,
                   qty_in, qty_out, unit_cost, valuation_rate
            FROM material_stock_movements
            WHERE company_id=$company AND document_no=$docno AND is_deleted=0
              AND (note IS NULL OR note NOT LIKE 'BIN:%')
              AND movement_type IN ({inList})
            ORDER BY stock_movement_id;
            """))
        {
            Add(cmd, "$company", Company);
            Add(cmd, "$docno", doc.DocNo);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                movements.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? null : Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? null : Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture)));
        }

        foreach (var m in movements)
        {
            var batchAllocs = new List<BatchAllocationRequest>();
            var serials = new List<SerialSelection>();
            using (var cmd = Cmd(c, tx, """
                SELECT b.batch_no, b.expiry_date, s.serial_no, a.qty
                FROM stock_movement_allocations a
                LEFT JOIN stock_batches b ON b.batch_id = a.batch_id
                LEFT JOIN stock_serials s ON s.serial_id = a.serial_id
                WHERE a.stock_movement_id=$mid;
                """))
            {
                Add(cmd, "$mid", m.Id);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var qty = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
                    if (!reader.IsDBNull(0))
                        batchAllocs.Add(new BatchAllocationRequest(
                            Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!,
                            qty,
                            reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)));
                    if (!reader.IsDBNull(2))
                        serials.Add(new SerialSelection(
                            Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)!));
                }
            }

            var qtyIn = invert ? m.QtyOut : m.QtyIn;
            var qtyOut = invert ? m.QtyIn : m.QtyOut;
            if (qtyIn == 0m && qtyOut == 0m) continue;
            // A re-entering receipt carries the rate the stock left at
            // (neutralize of an issue) or its original receipt cost
            // (reinstate of a receipt); issues are valued by the engine.
            decimal? receiptCost = qtyIn > 0
                ? (invert ? (m.ValuationRate ?? m.UnitCost) : m.UnitCost)
                : null;
            StockValuationEngine.Apply(c, tx, ProviderName,
                new StockMovementRequest(
                    Company, m.WarehouseId, m.MaterialId, m.Date, m.Type, doc.DocNo,
                    qtyIn, qtyOut,
                    ReceiptUnitCost: receiptCost,
                    Note: note,
                    BatchAllocations: batchAllocs.Count > 0 ? batchAllocs : null,
                    Serials: serials.Count > 0 ? serials : null,
                    OriginalDocumentNo: doc.DocNo,
                    PartyId: doc.PartyId,
                    Actor: actor));
        }
    }

    // ------------------------------------------------------------------
    // Tax register mirrors. The register is append-only BY TRIGGER
    // (no UPDATE, no DELETE of any row), so both directions are
    // additive: neutralization inserts negated rows under a per-cycle
    // kind '<KIND>-BIN' ('-BIN2', '-BIN3', … — the register's unique
    // key forbids reusing a kind for the same document), and
    // reinstatement inserts the mirror amounts negated again under
    // '<mirror kind>R'. Net register state is exact at every step and
    // the full cycle history stays visible in the register itself.
    // The NEUTRALIZE bin event's note carries the mirror kind used.
    // ------------------------------------------------------------------
    private static string? MirrorTaxRows(DbConnection c, DbTransaction tx, DocRow doc, string kindCode, string actor)
    {
        if (CountTaxRows(c, tx, doc, kindCode) == 0) return null;
        var cycle = 1;
        var mirrorKind = kindCode + "-BIN";
        while (CountTaxRows(c, tx, doc, mirrorKind) > 0)
        {
            cycle++;
            mirrorKind = kindCode + "-BIN" + cycle.ToString(CultureInfo.InvariantCulture);
        }
        CopyTaxRows(c, tx, doc, kindCode, mirrorKind, actor);
        return mirrorKind;
    }

    private static void ReinstateTaxRows(DbConnection c, DbTransaction tx, DocRow doc, string? mirrorKind, string actor)
    {
        if (string.IsNullOrWhiteSpace(mirrorKind)) return;
        CopyTaxRows(c, tx, doc, mirrorKind, mirrorKind + "R", actor);
    }

    private static long CountTaxRows(DbConnection c, DbTransaction tx, DocRow doc, string sourceKind)
    {
        using var cmd = Cmd(c, tx, """
            SELECT COUNT(*) FROM tax_transactions
            WHERE company_code=$company AND source_doc_kind=$kind AND source_doc_id=$id;
            """);
        Add(cmd, "$company", Company);
        Add(cmd, "$kind", sourceKind);
        Add(cmd, "$id", doc.DocId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Copies a document's register rows from one source kind
    /// to another with every amount negated.</summary>
    private static void CopyTaxRows(DbConnection c, DbTransaction tx, DocRow doc,
        string sourceKind, string targetKind, string actor)
    {
        var rows = new List<object[]>();
        using (var cmd = Cmd(c, tx, """
            SELECT company_code, country_code, source_doc_no, line_no, tax_code, tax_type,
                   tax_treatment, tax_rate, taxable_amount, tax_amount, is_recoverable,
                   tax_direction, tax_account_code, currency_code, exchange_rate,
                   base_tax_amount, journal_doc_no, posted_at, document_date
            FROM tax_transactions
            WHERE company_code=$company AND source_doc_kind=$kind AND source_doc_id=$id;
            """))
        {
            Add(cmd, "$company", Company);
            Add(cmd, "$kind", sourceKind);
            Add(cmd, "$id", doc.DocId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new object[19];
                for (var i = 0; i < 19; i++) row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                rows.Add(row);
            }
        }
        foreach (var row in rows)
        {
            using var cmd = Cmd(c, tx, """
                INSERT INTO tax_transactions
                    (company_code, country_code, source_doc_kind, source_doc_id,
                     source_doc_no, line_no, tax_code, tax_type, tax_treatment, tax_rate,
                     taxable_amount, tax_amount, is_recoverable, tax_direction, tax_account_code,
                     currency_code, exchange_rate, base_tax_amount, journal_doc_no, posted_at,
                     created_by, document_date)
                VALUES ($company, $country, $kind, $id,
                        $sno, $line, $tcode, $ttype, $ttreat, $trate,
                        $taxable, $tax, $recoverable, $direction, $taccount,
                        $currency, $rate, $basetax, $jno, $posted,
                        $actor, $docdate);
                """);
            Add(cmd, "$company", row[0]);
            Add(cmd, "$country", row[1] is DBNull ? null : row[1]);
            Add(cmd, "$kind", targetKind);
            Add(cmd, "$id", doc.DocId);
            Add(cmd, "$sno", row[2] is DBNull ? null : row[2]);
            Add(cmd, "$line", row[3]);
            Add(cmd, "$tcode", row[4]);
            Add(cmd, "$ttype", row[5] is DBNull ? null : row[5]);
            Add(cmd, "$ttreat", row[6] is DBNull ? null : row[6]);
            Add(cmd, "$trate", row[7]);
            Add(cmd, "$taxable", -Convert.ToDecimal(row[8], CultureInfo.InvariantCulture));
            Add(cmd, "$tax", -Convert.ToDecimal(row[9], CultureInfo.InvariantCulture));
            Add(cmd, "$recoverable", row[10] is DBNull ? null : row[10]);
            Add(cmd, "$direction", row[11] is DBNull ? null : row[11]);
            Add(cmd, "$taccount", row[12] is DBNull ? null : row[12]);
            Add(cmd, "$currency", row[13] is DBNull ? null : row[13]);
            Add(cmd, "$rate", row[14] is DBNull ? null : row[14]);
            Add(cmd, "$basetax", row[15] is DBNull ? DBNull.Value : -Convert.ToDecimal(row[15], CultureInfo.InvariantCulture));
            Add(cmd, "$jno", row[16] is DBNull ? null : row[16]);
            Add(cmd, "$posted", row[17] is DBNull ? null : row[17]);
            Add(cmd, "$actor", actor);
            Add(cmd, "$docdate", row[18] is DBNull ? null : row[18]);
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------
    // Document neutralize / reinstate orchestration
    // ------------------------------------------------------------------
    private static (Guid? FirstClone, string? TaxMirrorKind) NeutralizeDocument(
        DbConnection c, DbTransaction tx, DocRow doc,
        string kindCode, Guid eventId, string actor)
    {
        Guid? firstClone = null;
        foreach (var entry in FindDocumentEntries(c, tx, doc.DocId, kindCode))
        {
            var cloneId = CloneJournalEntry(c, tx, entry, swapped: true,
                "BIN-" + entry.DocumentNumber + "-" + eventId.ToString("N")[..6], actor,
                $"Recycle bin neutralization of {entry.DocumentNumber} [BIN {eventId:D}]",
                ChainHead(c, tx, entry));
            firstClone ??= cloneId;
        }
        MirrorPartyRowsForDocument(c, tx, doc, kindCode, firstClone, $"BIN:NEUTRALIZE {eventId:D}");
        ApplyStockMirrors(c, tx, doc, kindCode, invert: true, $"BIN:NEUTRALIZE {eventId:D}", actor);
        var taxMirrorKind = MirrorTaxRows(c, tx, doc, kindCode, actor);
        return (firstClone, taxMirrorKind);
    }

    private static void ReinstateDocument(DbConnection c, DbTransaction tx, DocRow doc,
        string kindCode, (Guid Id, string? Note) openEvent, Guid eventId, string actor)
    {
        Guid? firstClone = null;
        foreach (var entry in FindDocumentEntries(c, tx, doc.DocId, kindCode))
        {
            var reversalOf = FindBinClone(c, tx, entry, openEvent.Id);
            var cloneId = CloneJournalEntry(c, tx, entry, swapped: false,
                "BIN-R-" + entry.DocumentNumber + "-" + eventId.ToString("N")[..6], actor,
                $"Recycle bin reinstatement of {entry.DocumentNumber} [BIN {eventId:D}]", reversalOf);
            firstClone ??= cloneId;
        }
        MirrorPartyRowsWithNote(c, tx, $"BIN:NEUTRALIZE {openEvent.Id:D}",
            firstClone, $"BIN:REINSTATE {eventId:D}");
        ApplyStockMirrors(c, tx, doc, kindCode, invert: false, $"BIN:REINSTATE {eventId:D}", actor);
        ReinstateTaxRows(c, tx, doc, openEvent.Note, actor);
    }
}
