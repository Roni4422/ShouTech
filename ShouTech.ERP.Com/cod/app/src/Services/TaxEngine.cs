using System;
using System.Collections.Generic;
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

public static class TaxCountryPackCatalog
{
    private static readonly IReadOnlyDictionary<string, TaxCountryPack> Packs =
        new ReadOnlyDictionary<string, TaxCountryPack>(Build());

    public static IReadOnlyCollection<TaxCountryPack> All =>
        Packs.Values.OrderBy(x => x.CountryName, StringComparer.Ordinal).ToArray();

    public static bool TryGet(string countryCode, out TaxCountryPack pack) =>
        Packs.TryGetValue((countryCode ?? string.Empty).Trim().ToUpperInvariant(), out pack!);

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
