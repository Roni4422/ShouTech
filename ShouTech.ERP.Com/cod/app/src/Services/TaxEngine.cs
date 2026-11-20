using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Linq;

namespace ShouTech.App.Services;

public enum TaxRegimeKind
{
    Vat,
    GeneralSalesTax,
    VatAndScheduleTax,
    SalesAndUseTax,
    EuropeanVat,
    LocalConfigurationRequired
}

public sealed record TaxCountryPack(
    string CountryCode,
    string CountryName,
    TaxRegimeKind Regime,
    string Authority,
    string RuleDataStatus);

public sealed record TaxPackRuleDefinition(
    string Code,
    string Name,
    string TaxType,
    string Treatment,
    decimal Rate,
    string EffectiveFrom,
    string SourceUrl,
    string SourceReference,
    bool IsDefault = false,
    bool IsRecoverable = true,
    string OutputAccountCode = "2205",
    string InputAccountCode = "1205");

public static class TaxCountryPackCatalog
{
    private static readonly IReadOnlyDictionary<string, decimal> StandardRates =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["SA"] = 15m, ["JO"] = 16m, ["EG"] = 14m, ["AE"] = 5m,
            ["BH"] = 10m, ["OM"] = 5m, ["LB"] = 10m, ["GB"] = 20m,
            ["TR"] = 20m,
            ["AT"] = 20m, ["BE"] = 21m, ["BG"] = 20m, ["HR"] = 25m,
            ["CY"] = 19m, ["CZ"] = 21m, ["DK"] = 25m, ["EE"] = 24m,
            ["FI"] = 25.5m, ["FR"] = 20m, ["DE"] = 19m, ["GR"] = 24m,
            ["HU"] = 27m, ["IE"] = 23m, ["IT"] = 22m, ["LV"] = 21m,
            ["LT"] = 21m, ["LU"] = 17m, ["MT"] = 18m, ["NL"] = 21m,
            ["PL"] = 23m, ["PT"] = 23m, ["RO"] = 21m, ["SK"] = 23m,
            ["SI"] = 22m, ["ES"] = 21m, ["SE"] = 25m
        };

    private static readonly IReadOnlyDictionary<string, TaxCountryPack> Packs =
        new ReadOnlyDictionary<string, TaxCountryPack>(Build());

    public static IReadOnlyCollection<TaxCountryPack> All =>
        Packs.Values.OrderBy(x => x.CountryName, StringComparer.Ordinal).ToArray();

    public static bool TryGet(string countryCode, out TaxCountryPack pack)
    {
        if (Packs.TryGetValue((countryCode ?? string.Empty).Trim().ToUpperInvariant(), out var found))
        {
            pack = found;
            return true;
        }

        pack = null!;
        return false;
    }

    /// <summary>
    /// Returns the jurisdiction's safe starter rules. These are baseline
    /// standard-rate rules only; they do not classify products or replace
    /// reduced-rate, exemption, place-of-supply, or filing rules.
    /// Jurisdictions without a single general indirect-tax rate deliberately
    /// return no automatic rate and require local configuration.
    /// </summary>
    public static IReadOnlyList<TaxPackRuleDefinition> GetInitialRules(string countryCode)
    {
        var code = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!StandardRates.TryGetValue(code, out var rate))
            return Array.Empty<TaxPackRuleDefinition>();

        var (taxType, taxCode, sourceUrl, sourceReference, effectiveFrom) = code switch
        {
            "SA" => ("VAT", "SA-VAT15", "https://zatca.gov.sa/en/RulesRegulations/VAT/Pages/About-VAT.aspx",
                "ZATCA standard VAT rate: 15%; rate increased effective 2020-07-01.", "2020-07-01"),
            "JO" => ("GST", "JO-GST16", "https://www.istd.gov.jo/EN/Modules/faq",
                "ISTD General Sales Tax standard rate: 16%; special schedule rates require item classification.", "2009-01-01"),
            "EG" => ("VAT", "EG-VAT14", "https://www.eta.gov.eg/sites/default/files/2024-02/Law-english-no.67-2016.pdf.pdf",
                "VAT Law 67/2016 Article 3: standard rate 14% from fiscal year 2017/2018.", "2017-07-01"),
            "AE" => ("VAT", "AE-VAT5", "https://mof.gov.ae/en/public-finance/tax/value-added-tax-vat/",
                "UAE Ministry of Finance: standard VAT rate 5%, effective 2018-01-01.", "2018-01-01"),
            "BH" => ("VAT", "BH-VAT10", "https://www.bahrain.bh/new/en/business-vat_en.html",
                "Bahrain Government: standard VAT rate 10%, effective 2022-01-01.", "2022-01-01"),
            "OM" => ("VAT", "OM-VAT5", "https://tms.taxoman.gov.om/portal/tax-rate",
                "Oman Tax Authority: standard VAT rate 5%, effective 2021-04-16.", "2021-04-16"),
            "LB" => ("VAT", "LB-VAT10", "https://finance.gov.lb/en-us/Taxation/Individuals/VAT/Pages/Tax-Rate.aspx",
                "Lebanon Ministry of Finance: standard VAT rate 10%.", "2002-02-01"),
            "GB" => ("VAT", "GB-VAT20", "https://www.gov.uk/vat-rates",
                "HMRC standard VAT rate: 20%; reduced and zero rates depend on supply classification.", "2011-01-04"),
            "TR" => ("VAT", "TR-VAT20", "https://www.gib.gov.tr/",
                "Turkish Revenue Administration: standard VAT rate baseline; reduced rates depend on supply classification.", "2023-07-10"),
            _ => ("VAT", $"{code}-VAT-STD", "https://ec.europa.eu/taxation_customs/tedb/",
                "EU member-state standard-rate snapshot as of 2026-11-20; verify national authority, territory, and supply classification.", "2026-01-01")
        };

        var rules = new List<TaxPackRuleDefinition>
        {
            new(taxCode, $"Standard {taxType} ({rate.ToString(CultureInfo.InvariantCulture)}%)",
                taxType, "Standard", rate, effectiveFrom, sourceUrl, sourceReference,
                IsDefault: true),
            new($"{code}-ZERO", "Zero-rated supply (eligibility required)", taxType, "ZeroRated", 0m,
                effectiveFrom, sourceUrl, "Use only for supplies legally eligible for zero rating; retain input-tax recovery only where permitted.",
                IsRecoverable: true),
            new($"{code}-EXEMPT", "Exempt supply (eligibility required)", taxType, "Exempt", 0m,
                effectiveFrom, sourceUrl, "Use only for supplies legally exempt; input-tax recovery may be restricted.",
                IsRecoverable: false)
        };

        if (code == "JO")
        {
            foreach (var reducedRate in new[] { 10m, 5m, 4m, 2m, 1m })
                rules.Add(new TaxPackRuleDefinition(
                    $"JO-GST{reducedRate.ToString("0", CultureInfo.InvariantCulture)}",
                    $"Special schedule GST ({reducedRate.ToString("0", CultureInfo.InvariantCulture)}%)",
                    "GST", "Schedule", reducedRate, effectiveFrom,
                    "https://www.istd.gov.jo/EN/List/General__Sales_Tax_Tables",
                    "Jordan ISTD schedule rates; apply only to the goods/services identified by the official tax tables."));
        }
        else if (code == "EG")
        {
            rules.Add(new TaxPackRuleDefinition(
                "EG-VAT5-MACH", "Manufacturing machinery and equipment (5%)",
                "VAT", "Special", 5m, effectiveFrom, sourceUrl,
                "Egypt VAT Law 67/2016 Article 3: machinery/equipment used in manufacturing or service provision, subject to statutory exclusions."));
        }
        else if (code == "GB")
        {
            rules.Add(new TaxPackRuleDefinition(
                "GB-VAT5", "Reduced VAT (5%)", "VAT", "Reduced", 5m,
                effectiveFrom, sourceUrl, "HMRC reduced rate; use only for eligible supplies."));
        }
        else if (code == "TR")
        {
            rules.Add(new TaxPackRuleDefinition(
                "TR-VAT10", "Reduced VAT (10%)", "VAT", "Reduced", 10m,
                effectiveFrom, sourceUrl, "Turkish reduced VAT category; confirm current classification."));
            rules.Add(new TaxPackRuleDefinition(
                "TR-VAT1", "Reduced VAT (1%)", "VAT", "Reduced", 1m,
                effectiveFrom, sourceUrl, "Turkish reduced VAT category; confirm current classification."));
        }

        return rules.AsReadOnly();
    }

    public static TaxCountryPack? FindByNameOrCode(string value)
    {
        var key = (value ?? string.Empty).Trim();
        if (key.Length == 0) return null;
        if (TryGet(key, out var byCode)) return byCode;

        return Packs.Values.FirstOrDefault(x =>
            string.Equals(x.CountryName, key, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, TaxCountryPack> Build()
    {
        var result = new Dictionary<string, TaxCountryPack>(StringComparer.OrdinalIgnoreCase);

        void Add(string code, string name, TaxRegimeKind regime, string authority, string status)
        {
            result.Add(code, new TaxCountryPack(code, name, regime, authority, status));
        }

        Add("SA", "Saudi Arabia", TaxRegimeKind.Vat, "Zakat, Tax and Customs Authority (ZATCA)", "Framework identified; detailed rule and e-invoice validation required");
        Add("JO", "Jordan", TaxRegimeKind.GeneralSalesTax, "Income and Sales Tax Department", "Framework identified; detailed rule validation required");
        Add("EG", "Egypt", TaxRegimeKind.VatAndScheduleTax, "Egyptian Tax Authority", "Framework identified; detailed rule and e-invoice validation required");
        Add("KW", "Kuwait", TaxRegimeKind.LocalConfigurationRequired, "Kuwait Ministry of Finance / competent authority", "Local tax applicability requires verification");
        Add("AE", "United Arab Emirates", TaxRegimeKind.Vat, "Federal Tax Authority", "Framework identified; VAT and corporate tax are separate modules");
        Add("QA", "Qatar", TaxRegimeKind.LocalConfigurationRequired, "General Tax Authority", "Local tax applicability requires verification");
        Add("BH", "Bahrain", TaxRegimeKind.Vat, "National Bureau for Revenue", "Framework identified; detailed rule validation required");
        Add("OM", "Oman", TaxRegimeKind.Vat, "Oman Tax Authority", "Framework identified; detailed rule validation required");
        Add("SY", "Syria", TaxRegimeKind.LocalConfigurationRequired, "Competent Syrian tax authorities", "Current tax rules require local legal verification");
        Add("IQ", "Iraq", TaxRegimeKind.LocalConfigurationRequired, "General Commission for Taxes", "Current tax rules require local legal verification");
        Add("LB", "Lebanon", TaxRegimeKind.Vat, "Ministry of Finance - Directorate of VAT", "Framework identified; detailed rule validation required");
        Add("US", "United States", TaxRegimeKind.SalesAndUseTax, "State and local tax authorities", "State, local, sourcing and nexus datasets required");

        Add("AT", "Austria", TaxRegimeKind.EuropeanVat, "Austrian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("BE", "Belgium", TaxRegimeKind.EuropeanVat, "Belgian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("BG", "Bulgaria", TaxRegimeKind.EuropeanVat, "Bulgarian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("HR", "Croatia", TaxRegimeKind.EuropeanVat, "Croatian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("CY", "Cyprus", TaxRegimeKind.EuropeanVat, "Cyprus Tax Department", "EU VAT framework; national rules and rates require verification");
        Add("CZ", "Czechia", TaxRegimeKind.EuropeanVat, "Czech tax authority", "EU VAT framework; national rules and rates require verification");
        Add("DK", "Denmark", TaxRegimeKind.EuropeanVat, "Danish Tax Agency", "EU VAT framework; national rules and rates require verification");
        Add("EE", "Estonia", TaxRegimeKind.EuropeanVat, "Estonian Tax and Customs Board", "EU VAT framework; national rules and rates require verification");
        Add("FI", "Finland", TaxRegimeKind.EuropeanVat, "Finnish Tax Administration", "EU VAT framework; national rules and rates require verification");
        Add("FR", "France", TaxRegimeKind.EuropeanVat, "French tax authority", "EU VAT framework; national rules and rates require verification");
        Add("DE", "Germany", TaxRegimeKind.EuropeanVat, "German tax authority", "EU VAT framework; national rules and rates require verification");
        Add("GR", "Greece", TaxRegimeKind.EuropeanVat, "Greek tax authority", "EU VAT framework; national rules and rates require verification");
        Add("HU", "Hungary", TaxRegimeKind.EuropeanVat, "Hungarian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("IE", "Ireland", TaxRegimeKind.EuropeanVat, "Revenue Commissioners", "EU VAT framework; national rules and rates require verification");
        Add("IT", "Italy", TaxRegimeKind.EuropeanVat, "Italian Revenue Agency", "EU VAT framework; national rules and rates require verification");
        Add("LV", "Latvia", TaxRegimeKind.EuropeanVat, "Latvian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("LT", "Lithuania", TaxRegimeKind.EuropeanVat, "Lithuanian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("LU", "Luxembourg", TaxRegimeKind.EuropeanVat, "Luxembourg tax authority", "EU VAT framework; national rules and rates require verification");
        Add("MT", "Malta", TaxRegimeKind.EuropeanVat, "Malta tax authority", "EU VAT framework; national rules and rates require verification");
        Add("NL", "Netherlands", TaxRegimeKind.EuropeanVat, "Dutch Tax Administration", "EU VAT framework; national rules and rates require verification");
        Add("PL", "Poland", TaxRegimeKind.EuropeanVat, "Polish tax authority", "EU VAT framework; national rules and rates require verification");
        Add("PT", "Portugal", TaxRegimeKind.EuropeanVat, "Portuguese Tax and Customs Authority", "EU VAT framework; national rules and rates require verification");
        Add("RO", "Romania", TaxRegimeKind.EuropeanVat, "Romanian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("SK", "Slovakia", TaxRegimeKind.EuropeanVat, "Slovak tax authority", "EU VAT framework; national rules and rates require verification");
        Add("SI", "Slovenia", TaxRegimeKind.EuropeanVat, "Slovenian tax authority", "EU VAT framework; national rules and rates require verification");
        Add("ES", "Spain", TaxRegimeKind.EuropeanVat, "Spanish Tax Agency", "EU VAT framework; national rules and rates require verification");
        Add("SE", "Sweden", TaxRegimeKind.EuropeanVat, "Swedish Tax Agency", "EU VAT framework; national rules and rates require verification");

        Add("GB", "United Kingdom", TaxRegimeKind.Vat, "HM Revenue and Customs", "Post-EU national VAT rules require verification");
        Add("TR", "Türkiye", TaxRegimeKind.Vat, "Revenue Administration", "National VAT rules require verification");
        Add("CA", "Canada", TaxRegimeKind.LocalConfigurationRequired, "Canada Revenue Agency and provincial tax authorities", "Federal GST/HST and provincial PST rules require province-specific setup");
        Add("AU", "Australia", TaxRegimeKind.LocalConfigurationRequired, "Australian Taxation Office", "GST registration, taxable supply, and input credit rules require local setup");
        Add("NZ", "New Zealand", TaxRegimeKind.LocalConfigurationRequired, "Inland Revenue", "GST registration and supply classification require local setup");
        Add("JP", "Japan", TaxRegimeKind.LocalConfigurationRequired, "National Tax Agency", "Consumption tax, reduced rates, and qualified invoice rules require local setup");
        Add("KR", "South Korea", TaxRegimeKind.LocalConfigurationRequired, "National Tax Service", "VAT and electronic tax invoice rules require local setup");
        Add("SG", "Singapore", TaxRegimeKind.LocalConfigurationRequired, "Inland Revenue Authority of Singapore", "GST registration and reverse-charge rules require local setup");
        Add("CH", "Switzerland", TaxRegimeKind.LocalConfigurationRequired, "Federal Tax Administration", "VAT rates and place-of-supply rules require local setup");
        Add("NO", "Norway", TaxRegimeKind.LocalConfigurationRequired, "Norwegian Tax Administration", "VAT rates and sector-specific rules require local setup");
        Add("IS", "Iceland", TaxRegimeKind.LocalConfigurationRequired, "Directorate of Internal Revenue", "VAT and reduced-rate rules require local setup");
        Add("IN", "India", TaxRegimeKind.LocalConfigurationRequired, "Goods and Services Tax Network / CBIC", "CGST, SGST, IGST, cess, and place-of-supply rules require local setup");
        Add("ZA", "South Africa", TaxRegimeKind.LocalConfigurationRequired, "South African Revenue Service", "VAT, zero-rated, and exempt supply rules require local setup");
        Add("MX", "Mexico", TaxRegimeKind.LocalConfigurationRequired, "Servicio de Administración Tributaria", "IVA, IEPS, withholding, and CFDI rules require local setup");
        Add("ID", "Indonesia", TaxRegimeKind.LocalConfigurationRequired, "Directorate General of Taxes", "VAT, luxury sales tax, and e-Faktur rules require local setup");
        Add("MY", "Malaysia", TaxRegimeKind.LocalConfigurationRequired, "Royal Malaysian Customs Department", "Sales tax and service tax rules require local setup");
        Add("TH", "Thailand", TaxRegimeKind.LocalConfigurationRequired, "Revenue Department", "VAT and specific business tax rules require local setup");
        Add("PH", "Philippines", TaxRegimeKind.LocalConfigurationRequired, "Bureau of Internal Revenue", "VAT, percentage tax, and withholding rules require local setup");
        Add("BR", "Brazil", TaxRegimeKind.LocalConfigurationRequired, "Receita Federal and state tax authorities", "ICMS, ISS, IPI, PIS/COFINS, and reform transition rules require local setup");
        Add("AR", "Argentina", TaxRegimeKind.LocalConfigurationRequired, "AFIP / ARCA", "IVA, perceptions, retentions, and regional rules require local setup");
        Add("CL", "Chile", TaxRegimeKind.LocalConfigurationRequired, "Servicio de Impuestos Internos", "IVA and electronic tax document rules require local setup");
        Add("CO", "Colombia", TaxRegimeKind.LocalConfigurationRequired, "Dirección de Impuestos y Aduanas Nacionales", "IVA, consumption tax, withholding, and electronic invoicing require local setup");
        Add("NG", "Nigeria", TaxRegimeKind.LocalConfigurationRequired, "Federal Inland Revenue Service", "VAT, withholding, and state levies require local setup");
        Add("KE", "Kenya", TaxRegimeKind.LocalConfigurationRequired, "Kenya Revenue Authority", "VAT, excise, and eTIMS rules require local setup");
        Add("GH", "Ghana", TaxRegimeKind.LocalConfigurationRequired, "Ghana Revenue Authority", "VAT, levies, and electronic invoicing require local setup");
        Add("PK", "Pakistan", TaxRegimeKind.LocalConfigurationRequired, "Federal Board of Revenue and provincial authorities", "Sales tax on goods and services requires federal/provincial setup");
        Add("BD", "Bangladesh", TaxRegimeKind.LocalConfigurationRequired, "National Board of Revenue", "VAT, supplementary duty, and Mushak rules require local setup");
        Add("VN", "Vietnam", TaxRegimeKind.LocalConfigurationRequired, "General Department of Taxation", "VAT rates and electronic invoice rules require local setup");
        Add("IL", "Israel", TaxRegimeKind.LocalConfigurationRequired, "Israel Tax Authority", "VAT, withholding, and invoice allocation rules require local setup");

        Add("ZZ", "Other / Custom jurisdiction", TaxRegimeKind.LocalConfigurationRequired, "User-configured competent authority", "Custom tax rules require manual legal validation");
        return result;
    }
}

public sealed record TaxComponentDefinition(
    string Code,
    decimal Rate,
    bool IsCompound = false,
    bool IsRecoverable = true);

public sealed record TaxComponentAmount(
    string Code,
    decimal Rate,
    decimal TaxableBase,
    decimal Amount,
    bool IsRecoverable);

public sealed record TaxCalculationResult(
    decimal TaxableBase,
    decimal TotalTax,
    decimal GrossTotal,
    IReadOnlyList<TaxComponentAmount> Components);

public static class TaxCalculator
{
    public static TaxCalculationResult Calculate(
        decimal amount,
        IReadOnlyList<TaxComponentDefinition> components,
        bool taxInclusive,
        int currencyPrecision = 2)
    {
        if (amount < 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        if (components is null) throw new ArgumentNullException(nameof(components));
        if (currencyPrecision is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(currencyPrecision));
        if (components.Any(x => string.IsNullOrWhiteSpace(x.Code) || x.Rate < 0m || x.Rate > 100000m))
            throw new ArgumentException("Tax components require a code and a valid non-negative rate.", nameof(components));

        var factor = 1m;
        var accumulatedFactorTax = 0m;
        foreach (var component in components)
        {
            var componentBaseFactor = component.IsCompound ? 1m + accumulatedFactorTax : 1m;
            var taxFactor = componentBaseFactor * component.Rate / 100m;
            accumulatedFactorTax += taxFactor;
        }
        factor += accumulatedFactorTax;

        var taxableBase = taxInclusive && components.Count > 0 ? amount / factor : amount;
        var componentResults = new List<TaxComponentAmount>(components.Count);
        var accumulatedTax = 0m;

        foreach (var component in components)
        {
            var componentBase = component.IsCompound ? taxableBase + accumulatedTax : taxableBase;
            var rawTax = componentBase * component.Rate / 100m;
            var roundedTax = decimal.Round(rawTax, currencyPrecision, MidpointRounding.AwayFromZero);
            componentResults.Add(new TaxComponentAmount(
                component.Code,
                component.Rate,
                decimal.Round(componentBase, currencyPrecision, MidpointRounding.AwayFromZero),
                roundedTax,
                component.IsRecoverable));
            accumulatedTax += roundedTax;
        }

        var totalTax = componentResults.Sum(x => x.Amount);
        var roundedBase = decimal.Round(taxableBase, currencyPrecision, MidpointRounding.AwayFromZero);
        var gross = taxInclusive
            ? decimal.Round(amount, currencyPrecision, MidpointRounding.AwayFromZero)
            : decimal.Round(roundedBase + totalTax, currencyPrecision, MidpointRounding.AwayFromZero);

        return new TaxCalculationResult(roundedBase, totalTax, gross, componentResults.AsReadOnly());
    }
}
