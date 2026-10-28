using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShouTech.App.Services;

// ====================================================================
//  ShouTech Coa (.shc) — Chart of Accounts & Currency Templates
//  Single unified file: format, models, loader, parser, service.
// ====================================================================

// --------------------------------------------------------------------
//  Format constants
// --------------------------------------------------------------------

/// <summary>ShouTech Coa (.shc) format constants.</summary>
public static class ShcFormat
{
    public const string Extension = ".shc";
    public const string FormatName = "ShouTech Coa";
    public const string FormatId = "shc";
    public const string MimeType = "application/x-shoutech-coa";
    public const string SchemaUrl = "https://shoutech.app/schemas/coa-v5.json";
    public const int CurrentSchemaVersion = 5;
    public const string ChartFilter =
        "ShouTech Coa Chart (*.shc)|*.shc|" +
        "All Files (*.*)|*.*";
    public const string CurrencyFilter =
        "ShouTech Coa Currencies (*.shc)|*.shc|" +
        "All Files (*.*)|*.*";

    public static bool IsShcFile(string filePath)
        => filePath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static string EnsureExtension(string filePath)
        => IsShcFile(filePath) ? filePath : filePath + Extension;

    public static string StripExtension(string filePath)
        => IsShcFile(filePath) ? filePath[..^Extension.Length] : filePath;
}

// ====================================================================
//  CHART OF ACCOUNTS — Models
// ====================================================================

public sealed class CoaTemplateV5
{
    [JsonPropertyName("format")] public string Format { get; set; } = ShcFormat.FormatId;
    [JsonPropertyName("formatName")] public string FormatName { get; set; } = ShcFormat.FormatName;
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = ShcFormat.CurrentSchemaVersion;
    [JsonPropertyName("templateCode")] public string TemplateCode { get; set; } = "";
    [JsonPropertyName("templateKind")] public string TemplateKind { get; set; } = "commercial";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "country";
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("sourceEncoding")] public string SourceEncoding { get; set; } = "utf-8";
    [JsonPropertyName("languages")] public string[] Languages { get; set; } = ["ar", "en"];
    [JsonPropertyName("metadata")] public CoaMetadata Metadata { get; set; } = new();
    [JsonPropertyName("features")] public CoaFeatures Features { get; set; } = new();
    [JsonPropertyName("accounts")] public List<CoaAccount> Accounts { get; set; } = new();
    [JsonPropertyName("closingAccounts")] public List<CoaClosingAccount> ClosingAccounts { get; set; } = new();
    [JsonPropertyName("defaultAccounts")] public CoaDefaultAccounts DefaultAccounts { get; set; } = new();
    [JsonPropertyName("balSheets")] public List<CoaBalSheet> BalSheets { get; set; } = new();
    [JsonPropertyName("bills")] public List<CoaBill> Bills { get; set; } = new();
    [JsonPropertyName("standardBills")] public List<CoaStandardBill> StandardBills { get; set; } = new();
    [JsonPropertyName("standardPayments")] public List<CoaStandardPayment> StandardPayments { get; set; } = new();
    [JsonPropertyName("transfers")] public List<CoaTransfer> Transfers { get; set; } = new();
}

public sealed class CoaMetadata
{
    [JsonPropertyName("nameKey")] public string NameKey { get; set; } = "";
    [JsonPropertyName("nameAr")] public string? NameAr { get; set; }
    [JsonPropertyName("nameEn")] public string? NameEn { get; set; }
    [JsonPropertyName("latinName")] public string? LatinName { get; set; }
    [JsonPropertyName("framework")] public string? Framework { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; } = "ShouTech";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("releasedAt")] public string? ReleasedAt { get; set; }
}

public sealed class CoaFeatures
{
    [JsonPropertyName("isMinimal")] public bool IsMinimal { get; set; }
    [JsonPropertyName("hasArabicCodes")] public bool HasArabicCodes { get; set; }
    [JsonPropertyName("usesFlatStructure")] public bool UsesFlatStructure { get; set; }
    [JsonPropertyName("hasBills")] public bool HasBills { get; set; }
    [JsonPropertyName("hasBalSheets")] public bool HasBalSheets { get; set; }
    [JsonPropertyName("hasPayments")] public bool HasPayments { get; set; }
    [JsonPropertyName("hasTransfers")] public bool HasTransfers { get; set; }
    [JsonPropertyName("hasManufacturing")] public bool HasManufacturing { get; set; }
}

public sealed class CoaAccount
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("parentId")] public int? ParentId { get; set; }
    [JsonPropertyName("finalAccountId")] public int FinalAccountId { get; set; }
    [JsonPropertyName("cashFlowType")] public int CashFlowType { get; set; }
    [JsonPropertyName("incomeType")] public int IncomeType { get; set; }
    [JsonPropertyName("balSheetId")] public int BalSheetId { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "Asset";
    [JsonPropertyName("balance")] public string Balance { get; set; } = "Debit";
    [JsonPropertyName("posting")] public bool Posting { get; set; }
    [JsonPropertyName("cardType")] public string? CardType { get; set; }
    [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();
}

public sealed class CoaClosingAccount
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("parentId")] public int? ParentId { get; set; }
    [JsonPropertyName("shouldTransfer")] public bool ShouldTransfer { get; set; }
    [JsonPropertyName("financial")] public int Financial { get; set; }
    [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();
}

public sealed class CoaDefaultAccounts
{
    [JsonPropertyName("cash")] public string? Cash { get; set; }
    [JsonPropertyName("customer")] public string? Customer { get; set; }
    [JsonPropertyName("balance")] public string? Balance { get; set; }
    [JsonPropertyName("profitAndLoss")] public string? ProfitAndLoss { get; set; }
    [JsonPropertyName("trading")] public string? Trading { get; set; }
}

public sealed class CoaBalSheet
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();
}


public class CoaBill
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("iType")] public int IType { get; set; }
    [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();
    [JsonPropertyName("abrv")] public Dictionary<string, string> Abrv { get; set; } = new();
    [JsonPropertyName("colors")] public CoaColors Colors { get; set; } = new();
    [JsonPropertyName("defaultBillAcc")] public string? DefaultBillAcc { get; set; }
    [JsonPropertyName("defaultDiscAcc")] public string? DefaultDiscAcc { get; set; }
    [JsonPropertyName("defaultExtraAcc")] public string? DefaultExtraAcc { get; set; }
    [JsonPropertyName("defaultCashAcc")] public string? DefaultCashAcc { get; set; }
    [JsonPropertyName("defaultCostAcc")] public string? DefaultCostAcc { get; set; }
    [JsonPropertyName("defaultStockAcc")] public string? DefaultStockAcc { get; set; }
    [JsonPropertyName("affectsLastPrice")] public bool AffectsLastPrice { get; set; }
    [JsonPropertyName("affectsCostPrice")] public bool AffectsCostPrice { get; set; }
    [JsonPropertyName("affectsProfit")] public bool AffectsProfit { get; set; }
    [JsonPropertyName("affectsCustomerPrice")] public bool AffectsCustomerPrice { get; set; }
    [JsonPropertyName("discAffectsCost")] public bool DiscAffectsCost { get; set; }
    [JsonPropertyName("extraAffectsCost")] public bool ExtraAffectsCost { get; set; }
    [JsonPropertyName("discAffectsProfit")] public bool DiscAffectsProfit { get; set; }
    [JsonPropertyName("extraAffectsProfit")] public bool ExtraAffectsProfit { get; set; }
    [JsonPropertyName("noEntry")] public bool NoEntry { get; set; }
    [JsonPropertyName("autoEntry")] public bool AutoEntry { get; set; }
    [JsonPropertyName("autoPost")] public bool AutoPost { get; set; }
    [JsonPropertyName("genContraAcc")] public bool GenContraAcc { get; set; }
    [JsonPropertyName("defaultPrice")] public int? DefaultPrice { get; set; }
    [JsonPropertyName("defaultBonusPrice")] public int? DefaultBonusPrice { get; set; }
    [JsonPropertyName("defaultCostPrice")] public int? DefaultCostPrice { get; set; }
    [JsonPropertyName("fields")] public Dictionary<string, int> Fields { get; set; } = new();
}

public sealed class CoaColors
{
    [JsonPropertyName("c1")] public string C1 { get; set; } = "0xFFFFFF";
    [JsonPropertyName("c2")] public string C2 { get; set; } = "0x5BADFF";
}

public sealed class CoaStandardBill : CoaBill { }

public sealed class CoaStandardPayment
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("payType")] public int PayType { get; set; }
    [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();
    [JsonPropertyName("abrv")] public Dictionary<string, string> Abrv { get; set; } = new();
    [JsonPropertyName("colors")] public CoaColors Colors { get; set; } = new();
    [JsonPropertyName("defaultAccPtr")] public string? DefaultAccPtr { get; set; }
    [JsonPropertyName("acceptCostAcc")] public bool AcceptCostAcc { get; set; }
    [JsonPropertyName("detailed")] public bool Detailed { get; set; }
    [JsonPropertyName("autoPost")] public bool AutoPost { get; set; }
    [JsonPropertyName("fields")] public Dictionary<string, int> Fields { get; set; } = new();
}

public sealed class CoaTransfer
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("inTypeNum")] public int InTypeNum { get; set; }
    [JsonPropertyName("outTypeNum")] public int OutTypeNum { get; set; }
    [JsonPropertyName("extraBelongsToIn")] public bool ExtraBelongsToIn { get; set; }
    [JsonPropertyName("extraBelongsToOut")] public bool ExtraBelongsToOut { get; set; }
    [JsonPropertyName("discBelongsToIn")] public bool DiscBelongsToIn { get; set; }
    [JsonPropertyName("discBelongsToOut")] public bool DiscBelongsToOut { get; set; }
    [JsonPropertyName("classBelongsToIn")] public bool ClassBelongsToIn { get; set; }
    [JsonPropertyName("classBelongsToOut")] public bool ClassBelongsToOut { get; set; }
}

// ====================================================================
//  CURRENCY — Models
// ====================================================================

public sealed class CurrencyRec
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
    [JsonPropertyName("cultureCode")] public int CultureCode { get; set; }
    [JsonPropertyName("names")] public Dictionary<string, string> Names { get; set; } = new();
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    [JsonPropertyName("fractionAr")] public string FractionAr { get; set; } = "";
    [JsonPropertyName("fractionEn")] public string FractionEn { get; set; } = "";
    [JsonPropertyName("decimals")] public int Decimals { get; set; } = 2;

    public string DisplayName => Names.TryGetValue("ar", out var ar) && !string.IsNullOrWhiteSpace(ar)
        ? ar
        : Names.TryGetValue("en", out var en) ? en : Code;

    public override string ToString() => $"{Code} - {DisplayName}";
}

public sealed class CurrencyFile
{
    [JsonPropertyName("format")] public string Format { get; set; } = "shc";
    [JsonPropertyName("formatName")] public string FormatName { get; set; } = "ShouTech Coa";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("currencies")] public List<CurrencyRec> Currencies { get; set; } = new();
}

// ====================================================================
//  LOADER — reads and writes .shc
// ====================================================================

public static class CoaTemplateLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Loads a chart of accounts template from a .shc file.</summary>
    public static CoaTemplateV5 Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Template not found: {filePath}");
        if (!ShcFormat.IsShcFile(filePath))
            throw new NotSupportedException($"Not a .shc file: {filePath}");

        var json = File.ReadAllText(filePath, Encoding.UTF8);
        var template = JsonSerializer.Deserialize<CoaTemplateV5>(json, JsonOptions)
            ?? throw new InvalidDataException($"Invalid .shc file: {filePath}");

        if (template.SchemaVersion > ShcFormat.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Template uses schema v{template.SchemaVersion}, " +
                $"but this application supports up to v{ShcFormat.CurrentSchemaVersion}.");

        return template;
    }

    /// <summary>Saves a chart of accounts template as a .shc file.</summary>
    public static void Save(CoaTemplateV5 template, string filePath)
    {
        var finalPath = ShcFormat.EnsureExtension(filePath);
        var dir = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(template, JsonOptions);
        var final = "{\n  \"$schema\": \"" + ShcFormat.SchemaUrl + "\",\n" +
                    json.TrimStart().Substring(1).TrimStart();

        File.WriteAllText(finalPath, final, new UTF8Encoding(false));
    }
}

// ====================================================================
//  CURRENCY PARSER — reads currencies and produces .shc
// ====================================================================

public static class CurrencyParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Parses currencies from an INI-style source file.</summary>
    public static List<CurrencyRec> Parse(string sourcePath)
    {
        var result = new List<CurrencyRec>();
        if (!File.Exists(sourcePath))
            return result;

        var lines = File.ReadAllLines(sourcePath, Encoding.UTF8);
        var inSection = false;

        foreach (var raw in lines)
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
				continue;

			if (line.StartsWith('[') && line.EndsWith(']'))
			{
				inSection = line[1..^1].Trim()
					.Equals("Currency", StringComparison.OrdinalIgnoreCase);
				continue;
			}

            if (!inSection) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            if (!int.TryParse(key, out _)) continue;

            try
            {
                var rec = ParseCurrencyLine(line[(eq + 1)..]);
                if (rec != null) result.Add(rec);
            }
            catch { /* skip malformed */ }
        }

        for (var i = 0; i < result.Count; i++)
            result[i].Id = i + 1;

        return result;
    }

    /// <summary>Saves currencies to a .shc file.</summary>
    public static void Save(List<CurrencyRec> currencies, string outputPath)
    {
        var file = new CurrencyFile
        {
            Format = ShcFormat.FormatId,
            FormatName = ShcFormat.FormatName,
            SchemaVersion = 1,
            Currencies = currencies
        };

        var json = JsonSerializer.Serialize(file, JsonOptions);
        var final = "{\n  \"$schema\": \"https://shoutech.app/schemas/currency-v1.json\",\n" +
                    json.TrimStart().Substring(1).TrimStart();

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, final, new UTF8Encoding(false));
    }

    /// <summary>Loads currencies from a .shc file.</summary>
    public static List<CurrencyRec> Load(string filePath)
    {
        if (!File.Exists(filePath))
            return new List<CurrencyRec>();

        var json = File.ReadAllText(filePath, Encoding.UTF8);
        var file = JsonSerializer.Deserialize<CurrencyFile>(json, JsonOptions);
        return file?.Currencies ?? new List<CurrencyRec>();
    }

    private static CurrencyRec? ParseCurrencyLine(string line)
    {
        var parts = SplitCsv(line);
        if (parts.Count < 9) return null;

        var code = parts[8].Trim();
        if (string.IsNullOrWhiteSpace(code)) return null;

		_ = int.TryParse(parts[3].Trim(), out var decimals);
		_ = int.TryParse(parts[7].Trim(), out var culture);

        return new CurrencyRec
        {
            Code = code,
            CountryCode = parts.Count > 9 && !string.IsNullOrWhiteSpace(parts[9])
                ? parts[9].Trim() : null,
            CultureCode = culture,
            Symbol = parts[1].Trim(),
            FractionAr = parts[2].Trim(),
            FractionEn = parts[6].Trim(),
            Decimals = decimals > 0 ? decimals : 2,
            Names = new Dictionary<string, string>
            {
                ["ar"] = parts[0].Trim(),
                ["en"] = parts[5].Trim()
            }
        };
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;

        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { result.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }

        result.Add(sb.ToString());
        return result;
    }
}

// ====================================================================
//  SERVICE — for CompanyWizard integration
// ====================================================================

public static class CoaTemplatesSvc
{
    private static string TemplatesFolder
    {
        get
        {
            // Prefer output-dir copy, then source-tree relative paths used in development.
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Resources", "COA"),
                Path.Combine(AppContext.BaseDirectory, "Resources", "coa"),
                Path.Combine(AppContext.BaseDirectory, "coa"),
            };
            foreach (var path in candidates)
            {
                if (Directory.Exists(path))
                    return path;
            }
            return candidates[0];
        }
    }

    private static string CurrenciesFolder =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "Currencies");

    /// <summary>Returns all available templates from the Charts folder.</summary>
    public static IReadOnlyList<CoaTemplateInfo> GetAvailableTemplates()
    {
        var result = new List<CoaTemplateInfo>();
        if (!Directory.Exists(TemplatesFolder))
            return result;

        foreach (var file in Directory.EnumerateFiles(TemplatesFolder, "*.shc"))
        {
            try
            {
                var template = CoaTemplateLoader.Load(file);
                result.Add(new CoaTemplateInfo
                {
                    FilePath = file,
                    FileName = Path.GetFileName(file),
                    Code = template.TemplateCode,
                    Kind = template.TemplateKind,
                    Country = template.Country,
                    NameAr = template.Metadata.NameAr
                        ?? template.Metadata.LatinName
                        ?? template.TemplateCode,
                    NameEn = template.Metadata.NameEn
                        ?? template.Metadata.LatinName
                        ?? template.TemplateCode,
                    AccountCount = template.Accounts.Count,
                    BillCount = template.Bills.Count,
                    HasManufacturing = template.Features.HasManufacturing
                });
            }
            catch { /* skip broken files */ }
        }

        return result.OrderBy(x => x.Code).ToList();
    }

    /// <summary>Loads a template by its file name.</summary>
    public static CoaTemplateV5? LoadByFileName(string fileName)
    {
        var path = Path.Combine(TemplatesFolder, fileName);
        if (!File.Exists(path)) return null;

        try { return CoaTemplateLoader.Load(path); }
        catch { return null; }
    }

    /// <summary>Loads the default (fallback) template.</summary>
    public static CoaTemplateV5? LoadDefault()
    {
        var simple = Path.Combine(TemplatesFolder, "simple.shc");
        if (File.Exists(simple))
            return CoaTemplateLoader.Load(simple);

        var empty = Path.Combine(TemplatesFolder, "empty.shc");
        if (File.Exists(empty))
            return CoaTemplateLoader.Load(empty);

        return null;
    }

    /// <summary>Loads the currencies file.</summary>
    public static List<CurrencyRec> LoadCurrencies()
        => CurrencyParser.Load(Path.Combine(CurrenciesFolder, "cur.shc"));
}

/// <summary>Lightweight info about an available template.</summary>
public sealed class CoaTemplateInfo
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Code { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Country { get; set; }
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public int AccountCount { get; set; }
    public int BillCount { get; set; }
    public bool HasManufacturing { get; set; }

    public override string ToString() => $"{Code} - {NameAr} ({AccountCount} accounts)";
}