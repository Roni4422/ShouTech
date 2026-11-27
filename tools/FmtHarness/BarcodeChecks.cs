using System.Data.Common;
using Microsoft.Data.Sqlite;
using ShouTech.App;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Infrastructure;

// BarcodeChecks — composite scale-label proof (OPTWIN_SECTIONS
// §3.4, Landing 3).
//   F: the pure parser over the evidence layout (prefix 21; code
//      at 3 len 4; weight integer at 7 len 2; weight fraction at
//      9 len 3; 12-digit label incl. check digit).
//   G: the store + the REAL scan consumer chain — BcdTplSvc
//      round-trip, TaxSvc.ParseScan splitting a scanned label,
//      and MatchMaterial resolving the extracted code to the
//      seeded material (what POS and the invoice grid then fill).
internal static class BarcodeChecks
{
    private static readonly BarcodeTemplate Evidence =
        new("21", 12, 3, 4, 7, 2, 9, 3);

    public static void RunParser(Action<string, bool, string> check)
    {
        check("F1 evidence-layout label parses to code + weight",
            Evidence.TryParse("211234012505", out var code, out var weight) &&
            code == "1234" && weight == 1.250m,
            $"code={code} weight={weight}");
        check("F2 wrong prefix does not parse",
            !Evidence.TryParse("991234012505", out _, out _), "");
        check("F3 wrong length does not parse",
            !Evidence.TryParse("21123401250", out _, out _), "");
        check("F4 non-digit weight segment does not parse",
            !Evidence.TryParse("21123401A505", out _, out _), "");
        check("F5 zero weight does not parse",
            !Evidence.TryParse("211234000005", out _, out _), "");
        check("F6 fraction-only weight parses (0.500)",
            Evidence.TryParse("211234005005", out _, out var half) && half == 0.500m,
            $"weight={half}");
        var overlap = new BarcodeTemplate("21", 12, 3, 4, 5, 2, 9, 3);
        check("F7 overlapping segments are invalid",
            !overlap.IsValid(out _), "");
        var outside = new BarcodeTemplate("21", 12, 3, 4, 7, 2, 11, 3);
        check("F8 a segment past the total length is invalid",
            !outside.IsValid(out _), "");
    }

    public static void RunDb(Action<string, bool, string> check)
    {
        const string company = "BCD1";
        var root = Path.Combine(Path.GetTempPath(), "fmt-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "barcode.db");
        var connectionString = $"Data Source={dbPath};";
        try
        {
            var migration = SchemaMigrator.ApplyAllAsync("SQLite", dbPath).GetAwaiter().GetResult();
            check("G0 schema migrates (through 126)", migration.Success, migration.Message);

            var settings = AppCfgSvc.Current;
            settings.ConnectionStrings["Default"] = new ConnectionStringSettings { Provider = "SQLite", ConnectionString = dbPath };
            settings.Database ??= new DatabaseSettings();
            settings.Database.DefaultProvider = "SQLite";
            AppState.SetCompany(company, "Barcode Co", "Barcode Co");

            Exec(connectionString, $"INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en) VALUES ('{Guid.NewGuid():D}', '{company}', 'Barcode Co', 'Barcode Co');");
            Exec(connectionString,
                "INSERT INTO materials (material_id, company_id, code, name_ar, name_en, is_stocked, is_service, cost_method, allow_negative, is_deleted) VALUES (77, 'BCD1', '1234', 'Weighed cheese', 'Weighed cheese', 1, 0, 'WeightedAvg', 0, 0)");

            check("G1 template round-trips through the store",
                BcdTplSvc.Save(Evidence, true, "harness") && BcdTplSvc.Load() == Evidence, "");
            check("G2 an inactive template is invisible to the scan paths but stays editable",
                BcdTplSvc.Save(Evidence, false, "harness") &&
                BcdTplSvc.Load() is null && BcdTplSvc.LoadRow() is { Active: false }, "");
            check("G2b reactivating restores the scan-path view",
                BcdTplSvc.Save(Evidence, true, "harness") && BcdTplSvc.Load() == Evidence, "");

            var (scanCode, scanQty) = TaxSvc.ParseScan("211234012505");
            check("G3 ParseScan splits the label into code + weight",
                scanCode == "1234" && scanQty == 1.250m, $"code={scanCode} qty={scanQty}");
            var (plainCode, plainQty) = TaxSvc.ParseScan("1234");
            check("G4 a plain code passes ParseScan unchanged",
                plainCode == "1234" && plainQty is null, "");
            var match = TaxSvc.MatchMaterial(scanCode);
            check("G5 the extracted code resolves to the material (the fill both consumers do)",
                match is { Kind: TaxSvc.MaterialMatchKind.Exact } && match.Code == "1234",
                match?.Kind.ToString() ?? "null");
            check("G6 clearing the template disables parsing",
                BcdTplSvc.Save(null, false, "harness") &&
                TaxSvc.ParseScan("211234012505") is var cleared && cleared.Qty is null &&
                cleared.Code == "211234012505", "");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    private static void Exec(string connectionString, string sql)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
