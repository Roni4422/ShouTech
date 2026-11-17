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
        string DocKind, string DocNo, decimal Amount, string CurrencyCode);

    public static (long LedgerId, string Name)? ResolveParty(string code, string kind)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(kind))
            return null;
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var company = AppState.CurrentCompanyCode ?? "";

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
                (journal_entry_id, company_id, party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code, created_by)
            VALUES
                ($id, $company, $kind, $party, $code, $docKind, $docNo, $amount, $currency, $user);
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
                SELECT party_kind, party_id, party_code, doc_kind, doc_no, amount, currency_code
                FROM voucher_party_links
                WHERE journal_entry_id=$id;
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
                Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture) ?? "SAR");
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
