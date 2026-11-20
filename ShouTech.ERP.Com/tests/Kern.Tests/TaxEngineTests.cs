using System.Collections.Generic;
using System.Linq;
using ShouTech.App.Services;
using Xunit;

namespace Kern.Tests;

public sealed class TaxEngineTests
{
    [Theory]
    [InlineData("SA")]
    [InlineData("JO")]
    [InlineData("EG")]
    [InlineData("KW")]
    [InlineData("AE")]
    [InlineData("QA")]
    [InlineData("BH")]
    [InlineData("OM")]
    [InlineData("SY")]
    [InlineData("IQ")]
    [InlineData("LB")]
    [InlineData("US")]
    [InlineData("DE")]
    [InlineData("FR")]
    [InlineData("GB")]
    [InlineData("TR")]
    [InlineData("ZZ")]
    public void CountryCatalog_ContainsRequestedJurisdictions(string code)
    {
        Assert.True(TaxCountryPackCatalog.TryGet(code, out var pack));
        Assert.Equal(code, pack.CountryCode);
    }

    [Fact]
    public void CountryCatalog_ContainsAllEuMemberStates()
    {
        string[] euCodes =
        {
            "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI",
            "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU",
            "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE"
        };

        foreach (var code in euCodes)
            Assert.True(TaxCountryPackCatalog.TryGet(code, out _), $"Missing EU jurisdiction {code}.");
    }

    [Theory]
    [InlineData("SA")]
    [InlineData("JO")]
    [InlineData("EG")]
    [InlineData("KW")]
    [InlineData("AE")]
    [InlineData("QA")]
    [InlineData("BH")]
    [InlineData("OM")]
    [InlineData("SY")]
    [InlineData("IQ")]
    [InlineData("LB")]
    [InlineData("US")]
    [InlineData("DE")]
    [InlineData("FR")]
    [InlineData("GB")]
    [InlineData("TR")]
    [InlineData("CA")]
    [InlineData("AU")]
    [InlineData("IN")]
    [InlineData("BR")]
    [InlineData("ZZ")]
    public void CountryPackCatalog_IncludesRequestedAndAdditionalJurisdictions(string countryCode)
    {
        Assert.True(TaxCountryPackCatalog.TryGet(countryCode, out var pack));
        Assert.Equal(countryCode, pack.CountryCode);
    }

    [Theory]
    [InlineData("SA", 15d)]
    [InlineData("JO", 16d)]
    [InlineData("EG", 14d)]
    [InlineData("AE", 5d)]
    [InlineData("BH", 10d)]
    [InlineData("OM", 5d)]
    [InlineData("LB", 10d)]
    [InlineData("GB", 20d)]
    [InlineData("TR", 20d)]
    [InlineData("DE", 19d)]
    [InlineData("FR", 20d)]
    [InlineData("HU", 27d)]
    [InlineData("FI", 25.5d)]
    public void CountryTaxPack_SeedsSourcedStandardRate(string countryCode, double expectedRate)
    {
        var rules = TaxCountryPackCatalog.GetInitialRules(countryCode);
        var standard = rules.Single(x => x.IsDefault);

        Assert.Equal((decimal)expectedRate, standard.Rate);
        Assert.False(string.IsNullOrWhiteSpace(standard.SourceUrl));
        Assert.Contains(rules, x => x.Treatment == "ZeroRated" && x.Rate == 0m);
        Assert.Contains(rules, x => x.Treatment == "Exempt" && x.Rate == 0m);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("KW")]
    [InlineData("QA")]
    [InlineData("SY")]
    [InlineData("IQ")]
    [InlineData("ZZ")]
    public void CountryTaxPack_DoesNotInventUniversalRateForLocalSystems(string countryCode)
    {
        Assert.Empty(TaxCountryPackCatalog.GetInitialRules(countryCode));
    }

    [Fact]
    public void TaxCalculator_ExclusiveTax_ComputesTaxAndGross()
    {
        var result = TaxCalculator.Calculate(
            100m,
            new List<TaxComponentDefinition> { new("VAT", 15m) },
            taxInclusive: false);

        Assert.Equal(100m, result.TaxableBase);
        Assert.Equal(15m, result.TotalTax);
        Assert.Equal(115m, result.GrossTotal);
        Assert.Equal(15m, result.Components[0].Amount);
    }

    [Fact]
    public void TaxCalculator_InclusiveTax_ExtractsTaxFromGross()
    {
        var result = TaxCalculator.Calculate(
            115m,
            new List<TaxComponentDefinition> { new("VAT", 15m) },
            taxInclusive: true);

        Assert.Equal(100m, result.TaxableBase);
        Assert.Equal(15m, result.TotalTax);
        Assert.Equal(115m, result.GrossTotal);
    }

    [Fact]
    public void TaxCalculator_CompoundTax_UsesPriorTaxInNextBase()
    {
        var result = TaxCalculator.Calculate(
            100m,
            new List<TaxComponentDefinition>
            {
                new("A", 10m),
                new("B", 5m, IsCompound: true)
            },
            taxInclusive: false);

        Assert.Equal(10m, result.Components[0].Amount);
        Assert.Equal(5.5m, result.Components[1].Amount);
        Assert.Equal(15.5m, result.TotalTax);
        Assert.Equal(115.5m, result.GrossTotal);
    }

    [Fact]
    public void TaxCalculator_ZeroRate_ProducesNoTax()
    {
        var result = TaxCalculator.Calculate(
            125m,
            new List<TaxComponentDefinition> { new("ZERO", 0m) },
            taxInclusive: false);

        Assert.Equal(0m, result.TotalTax);
        Assert.Equal(125m, result.GrossTotal);
    }

    [Fact]
    public void TaxCalculator_RejectsNegativeRates()
    {
        Assert.Throws<System.ArgumentException>(() =>
            TaxCalculator.Calculate(
                100m,
                new List<TaxComponentDefinition> { new("INVALID", -1m) },
                taxInclusive: false));
    }
}
