using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Infrastructure;

/// <summary>Sections D+E (OPTWIN_SECTIONS §3.3, Landing 2):
/// D — the pure header composer: exactly the switched-on profile
///     fields, in order, with the free-text header last.
/// E — the real store against a migrated SQLite database:
///     settings round-trip, logo round-trip on the companies row,
///     and party balances (current vs previous-before-this-doc)
///     from party_ledger.</summary>
internal static class PrintSettingsChecks
{
    public static void RunComposer(Action<string, bool, string> check)
    {
        var profile = new PrintSetSvc.CompanyProfileRec(
            "Acme Trading", "Line 1\nLine 2", "011-123", "0999",
            "a@b.co", "acme.example", "LIC-9", "TAX-3");
        string Identity(string key) => key;

        var allOn = new PrintSetSvc.PrintSettingsRec(
            true, true, true, true, true, true, true, true,
            "Quality first", false, false, false, null);
        var lines = PrintSetSvc.Compose(profile, allOn, Identity);
        var expected = new[]
        {
            "Acme Trading", "Line 1", "Line 2", "Tel: 011-123", "Mobile: 0999",
            "Email: a@b.co", "Website: acme.example",
            "Commercial register / licence: LIC-9", "Tax number: TAX-3", "Quality first"
        };
        check("D1 composer emits exactly the switched-on fields in order, header text last",
            lines.SequenceEqual(expected), string.Join(" | ", lines));

        var nameOnly = PrintSetSvc.Compose(profile,
            PrintSetSvc.PrintSettingsRec.Defaults with { HeaderAddress = false }, Identity);
        check("D2 composer with only the name switched on emits the name alone",
            nameOnly.SequenceEqual(new[] { "Acme Trading" }), string.Join(" | ", nameOnly));

        var defaults = PrintSetSvc.Compose(profile, PrintSetSvc.PrintSettingsRec.Defaults, Identity);
        check("D3 composer defaults reproduce the historic header (name + address)",
            defaults.SequenceEqual(new[] { "Acme Trading", "Line 1", "Line 2" }),
            string.Join(" | ", defaults));

        var translated = PrintSetSvc.Compose(profile,
            PrintSetSvc.PrintSettingsRec.Defaults with { HeaderName = false, HeaderAddress = false, HeaderPhone = true },
            key => key == "Print.Tel" ? "TELX" : key);
        check("D4 composer labels come from the translate function",
            translated.SequenceEqual(new[] { "TELX: 011-123" }), string.Join(" | ", translated));

        var sparse = PrintSetSvc.Compose(
            profile with { Phone = null, Mobile = "  " },
            allOn with { HeaderText = null }, Identity);
        check("D5 composer skips switched-on fields the profile does not carry",
            !sparse.Any(l => l.Contains("011-123")) && sparse.Count == 7,
            string.Join(" | ", sparse));
    }

    public static void RunDb(Action<string, bool, string> check)
    {
        RunDbAsync(check).GetAwaiter().GetResult();
    }

    private static async Task RunDbAsync(Action<string, bool, string> check)
    {
        const string company = "PRTH";
        var root = Path.Combine(Path.GetTempPath(), "fmt-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "print.db");
        var cs = $"Data Source={dbPath};";

        var migration = await SchemaMigrator.ApplyAllAsync("SQLite", dbPath);
        check("E0 schema migrates (through 125)", migration.Success, migration.Message);

        var settings = AppCfgSvc.Current;
        settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
        settings.Database ??= new DatabaseSettings();
        settings.Database.DefaultProvider = "SQLite";
        AppState.SetCompany(company, "Print Harness Co", "Print Harness Co");

        void Exec(string sql)
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec($"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{Guid.NewGuid():D}', '{company}', 'Print Co', 'Print Co');");

        check("E1 print settings default to the historic behaviour when never saved",
            PrintSetSvc.Load() == PrintSetSvc.PrintSettingsRec.Defaults, "");

        var custom = new PrintSetSvc.PrintSettingsRec(
            true, false, true, false, true, false, true, false,
            "Header!", true, false, true, "Printer-9");
        PrintSetSvc.Save(custom, "fmt-harness");
        check("E2 print settings round-trip through the database",
            PrintSetSvc.Load() == custom, PrintSetSvc.Load().ToString());

        PrintSetSvc.SaveLogo(new byte[] { 1, 2, 3, 4 }, "image/png");
        var logo = PrintSetSvc.LoadLogo();
        check("E3 company logo round-trips on the companies row",
            logo is not null && logo.Value.Data.SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
            logo.Value.MediaType == "image/png", "");
        PrintSetSvc.ClearLogo();
        check("E4 clearing the logo removes it", PrintSetSvc.LoadLogo() is null, "");

        Exec($"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit) VALUES ('{company}', 'C', 7, 'SAL', 100, 'SAL-100', '2026-01-01', 500, 0);");
        Exec($"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit) VALUES ('{company}', 'C', 7, 'SAL', 101, 'SAL-101', '2026-01-02', 200, 0);");
        Exec($"INSERT INTO party_ledger (company_id, party_kind, party_id, doc_kind, doc_id, doc_no, entry_date, debit, credit) VALUES ('{company}', 'C', 7, 'RCV', 55, 'RCV-55', '2026-01-03', 0, 100);");
        var (current, previous) = PrintSetSvc.PartyBalances("C", 7, "SAL", 101);
        check("E5 party balances: current includes this document, previous excludes it",
            current == 600m && previous == 400m, $"current={current} previous={previous}");
    }
}
