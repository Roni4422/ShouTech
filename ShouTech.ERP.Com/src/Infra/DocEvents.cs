using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ShouTech.Infrastructure;

/// <summary>Event types recorded in sau_document_events (migration 103).
/// The set mirrors the table's CHECK constraint exactly.</summary>
public static class DocEventTypes
{
    public const string Created = "Created";
    public const string Modified = "Modified";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Posted = "Posted";
    public const string Reversed = "Reversed";
    public const string Cancelled = "Cancelled";
    public const string Allocated = "Allocated";
    public const string AccessDenied = "AccessDenied";
}

/// <summary>One row of the unified document event log (migration 103).</summary>
public sealed record DocEventRow(
    string EventId,
    string CompanyId,
    string DocSide,
    string DocKind,
    long DocId,
    string? DocNo,
    string EventType,
    string Actor,
    string? Details,
    string CreatedAt);

/// <summary>
/// The ONE writer/reader for sau_document_events (sales cycle W8,
/// migration 103) — the unified per-document audit log that replaces the
/// scattered per-feature histories for lifecycle purposes
/// (sau_approval_history keeps the approval workflow's own detail).
/// Writers hook the highest-leverage seams INSIDE the caller's
/// transaction, so an event commits or rolls back with the action it
/// describes: chain posting + reversal (CommercialDocumentRepository),
/// native invoice posting (InvRepo, via <see cref="Write"/>), payment
/// allocation (ReconciliationRepository) and permission denials at
/// action seams (PermServ.Require, via <see cref="RecordStandalone"/>).
/// Every write is guarded by a table-exists probe so a database that
/// predates migration 103 keeps posting exactly as before; a database
/// that HAS the table gets the event or the whole action fails — an
/// audit write never fails silently.
/// </summary>
public static class DocEvents
{
    /// <summary>Writes one event inside the caller's open transaction.
    /// No-op when sau_document_events does not exist (pre-103 schema).</summary>
    public static void Write(
        DbConnection connection, DbTransaction? transaction, string provider,
        string companyKey, string docSide, string docKind, long docId, string? docNo,
        string eventType, string actor, string? details)
    {
        if (!PartyLedger.TableExists(connection, transaction, provider, "sau_document_events"))
            return;
        using var command = GlPosting.CreateCommand(connection, transaction,
            """
            INSERT INTO sau_document_events
                (event_id, company_id, doc_side, doc_kind, doc_id, doc_no, event_type, actor, details)
            VALUES (@id, @company, @side, @kind, @doc, @docNo, @type, @actor, @details)
            """);
        GlPosting.Add(command, "@id", Guid.NewGuid().ToString("D"));
        GlPosting.Add(command, "@company", companyKey.Trim());
        GlPosting.Add(command, "@side", docSide.Trim().ToUpperInvariant());
        GlPosting.Add(command, "@kind", docKind.Trim());
        GlPosting.Add(command, "@doc", docId);
        GlPosting.Add(command, "@docNo", (object?)NullIfBlank(docNo) ?? DBNull.Value);
        GlPosting.Add(command, "@type", eventType.Trim());
        GlPosting.Add(command, "@actor", string.IsNullOrWhiteSpace(actor) ? string.Empty : actor.Trim());
        GlPosting.Add(command, "@details", (object?)NullIfBlank(details) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Writes one event on its own short-lived connection (the
    /// app-layer denial path: the permission service has no transaction
    /// of its own). Throws on failure — the caller reports it through
    /// the central error mechanism; a denied action stays denied either
    /// way, but the logging failure is never swallowed.</summary>
    public static void RecordStandalone(
        string provider, string connectionString,
        string companyKey, string docSide, string docKind, long docId, string? docNo,
        string eventType, string actor, string? details)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        using DbConnection connection = prov == "PostgreSQL"
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(connectionString);
        connection.Open();
        Write(connection, null, prov, companyKey, docSide, docKind, docId, docNo, eventType, actor, details);
    }

    /// <summary>Reads one document's events, oldest first, matching the
    /// company under ANY of the supplied keys (documents key the company
    /// by Guid text; denial rows written before a document context
    /// existed carry the company CODE — the viewer passes both).</summary>
    public static async Task<IReadOnlyList<DocEventRow>> GetForDocumentAsync(
        string provider, string connectionString,
        IReadOnlyList<string> companyKeys, string docSide, string docKind, long docId,
        CancellationToken cancellationToken = default)
    {
        var prov = GlPosting.NormalizeProvider(provider);
        var keys = companyKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct().ToList();
        if (keys.Count == 0)
            return Array.Empty<DocEventRow>();
        await using DbConnection connection = prov == "PostgreSQL"
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!PartyLedger.TableExists(connection, null, prov, "sau_document_events"))
            return Array.Empty<DocEventRow>();
        var inList = string.Join(", ", keys.Select((_, i) => $"@k{i}"));
        await using var command = GlPosting.CreateCommand(connection, null,
            $"""
            SELECT event_id, company_id, doc_side, doc_kind, doc_id, doc_no, event_type, actor, details, created_at
            FROM sau_document_events
            WHERE company_id IN ({inList}) AND doc_side = @side AND doc_kind = @kind AND doc_id = @doc
            ORDER BY created_at, event_id
            """);
        for (var i = 0; i < keys.Count; i++)
            GlPosting.Add(command, $"@k{i}", keys[i]);
        GlPosting.Add(command, "@side", docSide.Trim().ToUpperInvariant());
        GlPosting.Add(command, "@kind", docKind.Trim());
        GlPosting.Add(command, "@doc", docId);
        var result = new List<DocEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new DocEventRow(
                EventId: ReadString(reader, 0) ?? string.Empty,
                CompanyId: ReadString(reader, 1) ?? string.Empty,
                DocSide: ReadString(reader, 2) ?? string.Empty,
                DocKind: ReadString(reader, 3) ?? string.Empty,
                DocId: Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                DocNo: ReadString(reader, 5),
                EventType: ReadString(reader, 6) ?? string.Empty,
                Actor: ReadString(reader, 7) ?? string.Empty,
                Details: ReadString(reader, 8),
                CreatedAt: ReadString(reader, 9) ?? string.Empty));
        }
        return result;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}
