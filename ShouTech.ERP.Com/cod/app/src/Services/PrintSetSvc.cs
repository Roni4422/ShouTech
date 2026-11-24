#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Printing;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>
/// Print settings + company logo (OPTWIN_SECTIONS §3.3, Landing 2;
/// migration 125). The Options -> Printing section governs the
/// whole program's paper identity through this one service:
///   * <see cref="Compose"/> builds the ordered header lines from
///     the company profile + the per-field switches — consumed by
///     the invoice print pipeline (InvPrn) and the report renderer
///     (RptRend via XptServ) alike;
///   * the company logo lives on the companies row so it travels
///     with the company database on both providers;
///   * <see cref="PartyBalances"/> serves the previous/current
///     balance lines from the same party_ledger the Information
///     Window reads;
///   * <see cref="ApplyDialogSettings"/> applies the stored
///     default printer + the per-user copy counts to a print
///     dialog (invoice and voucher print paths).
/// All database access follows the PosTermRepo pattern (sync ADO
/// over DatabaseRuntime, company = AppState.CurrentCompanyCode).
/// </summary>
public static class PrintSetSvc
{
    /// <summary>Bumped on every settings/logo write so cached
    /// resolutions (the report pipeline's per-company header) can
    /// re-resolve after an Options save without a restart.</summary>
    public static int HeaderVersion { get; private set; }
    /// <summary>The stored printing switches for one company.
    /// Defaults reproduce the historic behaviour exactly (company
    /// name + address in the header, nothing else) so a company
    /// that never opened Options prints as before.</summary>
    public sealed record PrintSettingsRec(
        bool HeaderName,
        bool HeaderAddress,
        bool HeaderPhone,
        bool HeaderMobile,
        bool HeaderEmail,
        bool HeaderWebsite,
        bool HeaderLicense,
        bool HeaderTaxNumber,
        string? HeaderText,
        bool PrintPreviousBalance,
        bool PrintCurrentBalance,
        bool PrintUserOnInvoice,
        string? DefaultPrinter)
    {
        public static PrintSettingsRec Defaults { get; } = new(
            true, true, false, false, false, false, false, false,
            null, false, false, false, null);
    }

    /// <summary>The company-profile values the header can carry,
    /// assembled from the program's company profile (CoLocalStore —
    /// the same source the print address resolution already uses).
    /// License is the profile's licence value (stored in the DB as
    /// commercial_registration by the company create/update paths,
    /// Company.cs — one value, one switch).</summary>
    public sealed record CompanyProfileRec(
        string Name,
        string? Address,
        string? Phone,
        string? Mobile,
        string? Email,
        string? Website,
        string? License,
        string? TaxNumber);

    /// <summary>A resolved print header: ordered text lines plus
    /// the optional logo image bytes.</summary>
    public sealed record PrintHeader(IReadOnlyList<string> Lines, byte[]? LogoData);

    /// <summary>The optional party-band / footer extras for one
    /// document print, resolved from the settings: previous and/or
    /// current party balance, and the posting user line.</summary>
    public sealed record PrintExtras(
        decimal? PreviousBalance, decimal? CurrentBalance, string? PostedBy);

    // ---------------------------------------------------------------
    // Settings persistence
    // ---------------------------------------------------------------

    public static PrintSettingsRec Load()
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT header_name, header_address, header_phone, header_mobile,
                   header_email, header_website, header_license, header_tax_number,
                   header_text, print_previous_balance, print_current_balance,
                   print_user_on_invoice, default_printer
            FROM print_settings WHERE company_id = @company;
            """;
        Add(command, "@company", company);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return PrintSettingsRec.Defaults;
        return new PrintSettingsRec(
            Truthy(reader.GetValue(0)), Truthy(reader.GetValue(1)),
            Truthy(reader.GetValue(2)), Truthy(reader.GetValue(3)),
            Truthy(reader.GetValue(4)), Truthy(reader.GetValue(5)),
            Truthy(reader.GetValue(6)), Truthy(reader.GetValue(7)),
            Str(reader.GetValue(8)),
            Truthy(reader.GetValue(9)), Truthy(reader.GetValue(10)),
            Truthy(reader.GetValue(11)),
            Str(reader.GetValue(12)));
    }

    public static void Save(PrintSettingsRec rec, string actor)
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO print_settings
                (company_id, header_name, header_address, header_phone, header_mobile,
                 header_email, header_website, header_license, header_tax_number,
                 header_text, print_previous_balance, print_current_balance,
                 print_user_on_invoice, default_printer, updated_by, updated_at)
            VALUES
                (@company, @name, @address, @phone, @mobile,
                 @email, @website, @license, @tax,
                 @text, @prev, @curr, @user, @printer, @actor, CURRENT_TIMESTAMP)
            ON CONFLICT(company_id) DO UPDATE SET
                header_name = excluded.header_name,
                header_address = excluded.header_address,
                header_phone = excluded.header_phone,
                header_mobile = excluded.header_mobile,
                header_email = excluded.header_email,
                header_website = excluded.header_website,
                header_license = excluded.header_license,
                header_tax_number = excluded.header_tax_number,
                header_text = excluded.header_text,
                print_previous_balance = excluded.print_previous_balance,
                print_current_balance = excluded.print_current_balance,
                print_user_on_invoice = excluded.print_user_on_invoice,
                default_printer = excluded.default_printer,
                updated_by = excluded.updated_by,
                updated_at = excluded.updated_at;
            """;
        Add(command, "@company", company);
        Add(command, "@name", rec.HeaderName ? 1 : 0);
        Add(command, "@address", rec.HeaderAddress ? 1 : 0);
        Add(command, "@phone", rec.HeaderPhone ? 1 : 0);
        Add(command, "@mobile", rec.HeaderMobile ? 1 : 0);
        Add(command, "@email", rec.HeaderEmail ? 1 : 0);
        Add(command, "@website", rec.HeaderWebsite ? 1 : 0);
        Add(command, "@license", rec.HeaderLicense ? 1 : 0);
        Add(command, "@tax", rec.HeaderTaxNumber ? 1 : 0);
        Add(command, "@text", string.IsNullOrWhiteSpace(rec.HeaderText) ? null : rec.HeaderText.Trim());
        Add(command, "@prev", rec.PrintPreviousBalance ? 1 : 0);
        Add(command, "@curr", rec.PrintCurrentBalance ? 1 : 0);
        Add(command, "@user", rec.PrintUserOnInvoice ? 1 : 0);
        Add(command, "@printer", string.IsNullOrWhiteSpace(rec.DefaultPrinter) ? null : rec.DefaultPrinter.Trim());
        Add(command, "@actor", actor);
        command.ExecuteNonQuery();
        HeaderVersion++;
    }

    // ---------------------------------------------------------------
    // Company logo (companies row)
    // ---------------------------------------------------------------

    public static (byte[] Data, string? MediaType)? LoadLogo()
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT logo_data, logo_media_type FROM companies
            WHERE company_code = @company AND COALESCE(is_deleted, 0) = 0;
            """;
        Add(command, "@company", company);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
            return null;
        var data = (byte[])reader.GetValue(0);
        return data.Length == 0 ? null : (data, Str(reader.GetValue(1)));
    }

    public static void SaveLogo(byte[] data, string? mediaType)
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE companies SET logo_data = @data, logo_media_type = @media
            WHERE company_code = @company AND COALESCE(is_deleted, 0) = 0;
            """;
        Add(command, "@data", data);
        Add(command, "@media", mediaType);
        Add(command, "@company", company);
        command.ExecuteNonQuery();
        HeaderVersion++;
    }

    public static void ClearLogo()
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE companies SET logo_data = NULL, logo_media_type = NULL
            WHERE company_code = @company AND COALESCE(is_deleted, 0) = 0;
            """;
        Add(command, "@company", company);
        command.ExecuteNonQuery();
        HeaderVersion++;
    }

    // ---------------------------------------------------------------
    // Header composition (the pure core — harness-proven)
    // ---------------------------------------------------------------

    /// <summary>Builds the ordered header lines: exactly the
    /// profile fields the settings switch on, in a fixed order
    /// (name, address, phone, mobile, email, website, licence,
    /// tax number), then the free-text header line. Pure: labels
    /// resolve through <paramref name="translate"/> with English
    /// fallbacks, so the logic is headless-testable.</summary>
    public static IReadOnlyList<string> Compose(
        CompanyProfileRec profile, PrintSettingsRec settings, Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(translate);
        string L(string key, string fallback)
        {
            var value = translate(key);
            return string.IsNullOrWhiteSpace(value) || value == key ? fallback : value;
        }
        static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var lines = new List<string>();
        if (settings.HeaderName && Clean(profile.Name) is { } name)
            lines.Add(name);
        if (settings.HeaderAddress && Clean(profile.Address) is { } address)
            foreach (var part in address.Split('\n'))
                if (Clean(part) is { } line)
                    lines.Add(line);
        if (settings.HeaderPhone && Clean(profile.Phone) is { } phone)
            lines.Add($"{L("Print.Tel", "Tel")}: {phone}");
        if (settings.HeaderMobile && Clean(profile.Mobile) is { } mobile)
            lines.Add($"{L("Print.Mobile", "Mobile")}: {mobile}");
        if (settings.HeaderEmail && Clean(profile.Email) is { } email)
            lines.Add($"{L("Print.Email", "Email")}: {email}");
        if (settings.HeaderWebsite && Clean(profile.Website) is { } website)
            lines.Add($"{L("Print.Website", "Website")}: {website}");
        if (settings.HeaderLicense && Clean(profile.License) is { } license)
            lines.Add($"{L("Print.License", "Commercial register / licence")}: {license}");
        if (settings.HeaderTaxNumber && Clean(profile.TaxNumber) is { } tax)
            lines.Add($"{L("Print.TaxNo", "Tax number")}: {tax}");
        if (Clean(settings.HeaderText) is { } text)
            lines.Add(text);
        return lines;
    }

    /// <summary>Assembles the current company's profile from the
    /// local company store (the print pipeline's existing source).</summary>
    public static CompanyProfileRec? LoadProfile()
    {
        var code = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var companies = CoLocalStore.LoadAsync().GetAwaiter().GetResult();
        var rec = companies.FirstOrDefault(c =>
            string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        if (rec is null)
            return null;
        var lang = AppHost.Localization.GetCurrentLanguage();
        var name = string.Equals(lang, "ar", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(rec.Name)
            ? rec.Name
            : !string.IsNullOrWhiteSpace(rec.NameEn) ? rec.NameEn : rec.Name;
        var cityCountry = string.Join(", ",
            new[] { rec.City, rec.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var addressParts = new[] { rec.AddressLine1, rec.AddressLine2, cityCountry }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var address = string.Join("\n", addressParts);
        return new CompanyProfileRec(
            name,
            address.Length == 0 ? null : address,
            rec.Phone, rec.Mobile, rec.Email, rec.Website, rec.License, rec.TaxNumber);
    }

    /// <summary>Resolves the full print header (lines + logo) for
    /// the current company. Any failure is reported centrally and
    /// degrades to null — callers fall back to the historic header,
    /// so printing never breaks because settings could not load.</summary>
    public static PrintHeader? ResolveHeader()
    {
        try
        {
            var profile = LoadProfile();
            if (profile is null)
                return null;
            var settings = Load();
            var lines = Compose(profile, settings,
                key => AppHost.Localization.GetString(key));
            byte[]? logo = null;
            try { logo = LoadLogo()?.Data; }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PrintSetSvc.ResolveHeader.Logo");
            }
            return new PrintHeader(lines, logo);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PrintSetSvc.ResolveHeader");
            return null;
        }
    }

    // ---------------------------------------------------------------
    // Party balances for the printed balance lines
    // ---------------------------------------------------------------

    /// <summary>Current = the party's full party_ledger balance
    /// (base amounts, debit − credit — the Information Window's
    /// convention). Previous = the same sum EXCLUDING the rows the
    /// given document itself wrote, i.e. the balance before this
    /// document. Both from posted ledger rows only.</summary>
    public static (decimal Current, decimal Previous) PartyBalances(
        string partyKind, long partyId, string docKind, long docId)
    {
        var company = RequireCompany();
        using var connection = OpenConnection();
        decimal Sum(bool excludeDoc)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(SUM(COALESCE(base_debit, debit) - COALESCE(base_credit, credit)), 0)
                FROM party_ledger
                WHERE company_id = @company AND party_kind = @kind AND party_id = @party
                """ + (excludeDoc
                    ? " AND NOT (COALESCE(doc_kind, '') = @docKind AND COALESCE(doc_id, 0) = @docId);"
                    : ";");
            Add(command, "@company", company);
            Add(command, "@kind", partyKind);
            Add(command, "@party", partyId);
            if (excludeDoc)
            {
                Add(command, "@docKind", docKind);
                Add(command, "@docId", docId);
            }
            var value = command.ExecuteScalar();
            return value is null or DBNull ? 0m : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return (Sum(false), Sum(true));
    }

    // ---------------------------------------------------------------
    // Print dialog application
    // ---------------------------------------------------------------

    /// <summary>Applies the stored default printer (when set and
    /// still installed) and the copy count to a print dialog.
    /// Failures are reported and leave the dialog untouched — the
    /// user can still pick a printer manually.</summary>
    public static void ApplyDialogSettings(PrintDialog dialog, int copies)
    {
        ApplyDefaultPrinter(dialog);
        try
        {
            if (copies > 1)
            {
                var ticket = dialog.PrintTicket;
                if (ticket is not null)
                {
                    ticket.CopyCount = copies;
                    dialog.PrintTicket = ticket;
                }
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PrintSetSvc.ApplyDialogSettings.Copies");
        }
    }

    /// <summary>Applies only the stored default printer (report
    /// printing uses this without the invoice/voucher copy
    /// counts).</summary>
    public static void ApplyDefaultPrinter(PrintDialog dialog)
    {
        try
        {
            var settings = Load();
            if (!string.IsNullOrWhiteSpace(settings.DefaultPrinter))
            {
                using var server = new LocalPrintServer();
                PrintQueue? match = null;
                foreach (var queue in server.GetPrintQueues(new[]
                         {
                             EnumeratedPrintQueueTypes.Local,
                             EnumeratedPrintQueueTypes.Connections
                         }))
                {
                    if (match is null && string.Equals(queue.Name, settings.DefaultPrinter,
                            StringComparison.OrdinalIgnoreCase))
                        match = queue;
                    else
                        queue.Dispose();
                }
                if (match is not null)
                    dialog.PrintQueue = match;
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PrintSetSvc.ApplyDialogSettings.Printer");
        }
    }

    // ---------------------------------------------------------------
    // ADO helpers (PosTermRepo pattern)
    // ---------------------------------------------------------------

    private static string RequireCompany()
    {
        var company = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(company))
            throw new InvalidOperationException("The active company is not configured.");
        return company;
    }

    private static DbConnection OpenConnection()
    {
        var connectionString = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The active company database connection is not configured.");
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool Truthy(object? value) => value switch
    {
        null or DBNull => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0
    };

    private static string? Str(object? value) => value switch
    {
        null or DBNull => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };
}
