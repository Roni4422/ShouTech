using System;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;

using ShouTech.Domain;
namespace ShouTech.App.Services;

/// <summary>
/// App-layer party settlement surface for cash vouchers (Fix Phase Step 4,
/// sweep B-03). A cash voucher (CshWin) may name a party by CODE + KIND;
/// the code resolves against the ONE canonical party master
/// (<c>parties</c>, migration 061) in the current company — exactly, never
/// by parsing the code as a number (sweep C-2) — and the resolved
/// settlement is stored as a <c>voucher_party_links</c> row at draft-save
/// time. JournalRepository then writes the party_ledger settlement entry
/// inside the journal posting transaction, and the shared reversal core
/// mirrors it when the voucher is reversed, so the GL and the party
/// ledger can never drift apart for a voucher.
/// </summary>
public static class PartyLedgerSvc
{
    public sealed record VoucherPartyLink(
        string PartyKind, long PartyId, string PartyCode,
        string DocKind, string DocNo, decimal Amount, string CurrencyCode,
        // Supplier advances (migration 095): a PAY link may name the PO
        // it is an advance for; posting then debits Advances (1215).
        long? PoDocId = null, bool IsAdvance = false, string? PoDocNo = null)
    {
        /// <summary>G23: the domain request JournalRepository writes
        /// inside the draft-creation transaction. <paramref name="companyCode"/>
        /// is the current company code — the key voucher_party_links carries.</summary>
        public ShouTech.Domain.Entities.VoucherPartyLinkRequest ToRequest(string companyCode)
            => new(companyCode, PartyKind, PartyId, PartyCode, DocKind, DocNo, Amount, CurrencyCode);
    }

    /// <summary>Resolves a purchase order by number in the current
    /// company (either company key: chain documents carry the Guid,
    /// legacy rows the code) for the CshWin advance picker. Returns the
    /// PO's doc id, supplier ledger id and status, or null.</summary>
    public static (long DocId, long PartyId, string Status)? ResolvePurchaseOrder(string docNo)
    {
        if (string.IsNullOrWhiteSpace(docNo))
            return null;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var company = AppState.CurrentCompanyCode ?? "";
        try
        {
            using var connection = Open(cs);
            string companyGuid = company;
            using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = "SELECT company_id FROM companies WHERE company_code = $c;";
                Add(lookup, "$c", company);
                var value = lookup.ExecuteScalar();
                if (value is not null and not DBNull)
                    companyGuid = Convert.ToString(value, CultureInfo.InvariantCulture) ?? company;
            }
            using var cmd = connection.CreateCommand();
            var deletedPredicate = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";
            cmd.CommandText = $"""
                SELECT doc_id, COALESCE(party_id, 0), status
                FROM purchase_documents
                WHERE doc_kind = 'PO' AND doc_no = $no AND {deletedPredicate}
                  AND CAST(company_id AS TEXT) IN ($code, $guid)
                LIMIT 1;
                """;
            Add(cmd, "$no", docNo.Trim());
            Add(cmd, "$code", company);
            Add(cmd, "$guid", companyGuid);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PartyLedgerSvc.ResolvePurchaseOrder");
            return null;
        }
    }

    /// <summary>The PO advance summary OrdWin shows (Y1 visibility):
    /// total advances posted (journal still Posted) for the PO, the part
    /// consumed by advance allocations, and the remaining balance.
    /// Display only — allocation re-validates everything itself.</summary>
    public static (decimal Total, decimal Consumed, decimal Remaining) GetPoAdvanceSummary(long poDocId)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return (0m, 0m, 0m);
        var company = AppState.CurrentCompanyCode ?? "";
        try
        {
            using var connection = Open(cs);
            var advancePredicate = DatabaseRuntime.IsPostgreSql() ? "vl.is_advance = TRUE" : "vl.is_advance = 1";
            decimal total, consumed;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT COALESCE(SUM(vl.amount), 0)
                    FROM voucher_party_links vl
                    JOIN journal_entries j
                      ON CAST(j.journal_entry_id AS TEXT) = vl.journal_entry_id AND j.status = 1
                    WHERE vl.company_id = $c AND vl.po_doc_id = $po AND {advancePredicate};
                    """;
                Add(cmd, "$c", company);
                Add(cmd, "$po", poDocId);
                total = Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COALESCE(SUM(amount), 0)
                    FROM payment_allocations
                    WHERE company_id = $c AND advance_po_doc_id = $po AND COALESCE(source_kind, 'PAYMENT') = 'ADVANCE';
                    """;
                Add(cmd, "$c", company);
                Add(cmd, "$po", poDocId);
                consumed = Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            return (total, consumed, total - consumed);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PartyLedgerSvc.GetPoAdvanceSummary");
            return (0m, 0m, 0m);
        }
    }

    public static (long LedgerId, string Name)? ResolveParty(string code, string kind)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(kind))
            return null;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var company = AppState.CurrentCompanyCode ?? "";

        // W1 (migration 095): a BLACKLISTED party is rejected at
        // resolution for new documents, with the reason — its balances
        // stay visible in reports, but no new document may name it.
        // NOTE: no catalogued error code exists for this rejection yet
        // (ACC-5020 requested — see W1 report).
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            var deletedPredicate = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";
            var blacklistedPredicate = DatabaseRuntime.IsPostgreSql() ? "is_blacklisted = TRUE" : "COALESCE(is_blacklisted, 0) = 1";
            cmd.CommandText = $"""
                SELECT blacklist_reason
                FROM parties
                WHERE company_id=$c AND kind=$kind AND code=$code AND {deletedPredicate} AND {blacklistedPredicate}
                LIMIT 1;
                """;
            Add(cmd, "$c", company);
            Add(cmd, "$kind", kind.Trim().ToUpperInvariant());
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var reason = reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                throw new AppError(ErrorCodes.PartyBlacklisted,
                    $"Party '{code.Trim()}' is blacklisted and cannot be used on new documents." +
                    (string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Reason: {reason}"));
            }
        }
        catch (AppError)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PartyLedgerSvc.ResolveParty.Blacklist");
        }

        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            var deletedPredicate = DatabaseRuntime.IsPostgreSql() ? "is_deleted = FALSE" : "is_deleted = 0";
            var activePredicate = DatabaseRuntime.IsPostgreSql() ? "is_active = TRUE" : "is_active = 1";
            cmd.CommandText = $"""
                SELECT ledger_id, COALESCE(NULLIF(name_en, ''), name_ar, '')
                FROM parties
                WHERE company_id=$c AND kind=$kind AND code=$code AND {deletedPredicate} AND {activePredicate}
                LIMIT 1;
                """;
            Add(cmd, "$c", company);
            Add(cmd, "$kind", kind.Trim().ToUpperInvariant());
            Add(cmd, "$code", code.Trim());
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) return null;
            return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PartyLedgerSvc.ResolveParty");
            return null;
        }
    }

    /// <summary>Legacy standalone link write, kept for callers that
    /// already hold a committed draft id. New voucher saves must NOT
    /// use this: CshWin passes the link through JournalService.CreateDraftAsync
    /// so it is written inside the draft transaction (G23) — a draft
    /// must never exist without its intended party link.</summary>
    public static void SaveVoucherLink(
        Guid journalEntryId, string docKind, string docNo, decimal amount,
        string currencyCode, VoucherPartyLink link, string userId)
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection.");
        var company = AppState.CurrentCompanyCode ?? "";
        using var connection = Open(cs);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO voucher_party_links
                (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by, po_doc_id, is_advance)
            VALUES
                ($id, $company, $kind, $party, $code, $docKind, $docNo, $amount, $currency, $user, $po, $advance);
            """;
        Add(cmd, "$id", journalEntryId.ToString("D"));
        Add(cmd, "$company", company);
        Add(cmd, "$kind", link.PartyKind);
        Add(cmd, "$party", link.PartyId);
        Add(cmd, "$code", link.PartyCode);
        Add(cmd, "$docKind", docKind);
        Add(cmd, "$docNo", docNo);
        Add(cmd, "$amount", amount);
        Add(cmd, "$currency", string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim());
        Add(cmd, "$user", userId);
        Add(cmd, "$po", (object?)link.PoDocId ?? DBNull.Value);
        Add(cmd, "$advance", DatabaseRuntime.IsPostgreSql() ? (object)link.IsAdvance : (link.IsAdvance ? 1 : 0));
        cmd.ExecuteNonQuery();
    }

    public static VoucherPartyLink? LoadVoucherLink(Guid journalEntryId)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        try
        {
            using var connection = Open(cs);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT vl.party_kind, vl.party_id, vl.party_code, vl.doc_kind, vl.doc_no, vl.amount, vl.currency_code,
                       vl.po_doc_id, vl.is_advance, po.doc_no
                FROM voucher_party_links vl
                LEFT JOIN purchase_documents po ON po.doc_id = vl.po_doc_id
                WHERE vl.journal_entry_id=$id;
                """;
            Add(cmd, "$id", journalEntryId.ToString("D"));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new VoucherPartyLink(
                Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "C",
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "",
                Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "",
                Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "",
                Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? "SAR",
                reader.IsDBNull(7) ? null : Convert.ToInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                !reader.IsDBNull(8) && Convert.ToBoolean(reader.GetValue(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(9) ? null : Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PartyLedgerSvc.LoadVoucherLink");
            return null;
        }
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
}
