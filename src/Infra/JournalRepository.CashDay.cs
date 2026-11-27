#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain.Entities;

namespace ShouTech.Infrastructure;

public sealed partial class JournalRepository
{
    public async Task<IReadOnlyList<CashMovementRow>> ListSourceVouchersForDateAsync(
        Guid companyId,
        DateOnly entryDate,
        IReadOnlyList<string> seriesPrefixes,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("Company is required.", nameof(companyId));
        if (seriesPrefixes is null || seriesPrefixes.Count == 0)
            return Array.Empty<CashMovementRow>();

        var prefixes = seriesPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (prefixes.Count == 0)
            return Array.Empty<CashMovementRow>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var notDeleted = _provider == "PostgreSQL" ? "is_deleted = FALSE" : "is_deleted = 0";
        var likeClauses = new List<string>();
        for (var i = 0; i < prefixes.Count; i++)
            likeClauses.Add($"(source_doc_no LIKE @p{i} OR document_number LIKE @p{i})");
        var likeSql = string.Join(" OR ", likeClauses);
        await using var command = CreateCommand(connection, null, $"""
            SELECT CAST(journal_entry_id AS TEXT),
                   COALESCE(NULLIF(source_doc_no, ''), document_number) AS source_no,
                   CASE WHEN document_number LIKE 'JRN-%' THEN document_number ELSE NULL END AS central_no,
                   entry_date,
                   COALESCE(description, ''),
                   status,
                   (SELECT COALESCE(SUM(debit), 0) FROM journal_entry_lines l WHERE l.journal_entry_id = e.journal_entry_id),
                   (SELECT COALESCE(SUM(credit), 0) FROM journal_entry_lines l WHERE l.journal_entry_id = e.journal_entry_id)
            FROM journal_entries e
            WHERE company_id = @company
              AND {notDeleted}
              AND entry_date = @entryDate
              AND ({likeSql})
            ORDER BY COALESCE(NULLIF(source_doc_no, ''), document_number)
            """);
        Add(command, "@company", FormatGuid(companyId));
        Add(command, "@entryDate", entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        for (var i = 0; i < prefixes.Count; i++)
            Add(command, $"@p{i}", prefixes[i] + "-%");

        var rows = new List<CashMovementRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var idText = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!Guid.TryParse(idText, out var id))
                continue;
            var sourceNo = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
            var centralNo = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
            DateOnly date;
            if (reader.GetValue(3) is DateTime dt)
                date = DateOnly.FromDateTime(dt);
            else if (!DateOnly.TryParse(Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                date = entryDate;
            var desc = reader.IsDBNull(4) ? string.Empty : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty;
            var status = reader.IsDBNull(5) ? string.Empty : Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture) ?? string.Empty;
            var debit = reader.IsDBNull(6) ? 0m : Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture);
            var credit = reader.IsDBNull(7) ? 0m : Convert.ToDecimal(reader.GetValue(7), CultureInfo.InvariantCulture);
            rows.Add(new CashMovementRow(id, sourceNo, centralNo, date, desc, status, debit, credit));
        }
        return rows;
    }
}
