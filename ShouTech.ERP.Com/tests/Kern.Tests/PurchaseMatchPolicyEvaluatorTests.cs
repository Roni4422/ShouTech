using ShouTech.Domain.Entities;
using Xunit;

namespace Kern.Tests;

public sealed class PurchaseMatchPolicyEvaluatorTests
{
    private static PurchaseMatchPolicy EnabledPolicy() => new(
        IsEnabled: true,
        RequireReceiptMatch: true,
        PriceWarningPercent: 2m,
        PriceBlockPercent: 5m,
        TaxWarningPercent: 1m,
        TaxBlockPercent: 3m,
        BaseVarianceWarningAmount: 10m,
        BaseVarianceBlockAmount: 25m,
        PolicyVersion: 4);

    [Fact]
    public void DisabledPolicy_ReturnsNotConfigured_WithoutInventingPass()
    {
        var result = PurchaseMatchPolicyEvaluator.Evaluate(
            EnabledPolicy() with { IsEnabled = false },
            new PurchaseMatchVariance(0m, 0m, 0m, true));

        Assert.Equal("NotConfigured", result.Decision);
        Assert.Empty(result.ReasonCodes);
    }

    [Fact]
    public void ValuesWithinThresholds_ReturnPass()
    {
        var result = PurchaseMatchPolicyEvaluator.Evaluate(
            EnabledPolicy(),
            new PurchaseMatchVariance(2m, 1m, 10m, true));

        Assert.Equal("Pass", result.Decision);
    }

    [Fact]
    public void WarningThresholds_ReturnWarning()
    {
        var result = PurchaseMatchPolicyEvaluator.Evaluate(
            EnabledPolicy(),
            new PurchaseMatchVariance(3m, 1m, 10m, true));

        Assert.Equal("Warning", result.Decision);
        Assert.Contains("PURCHASE_PRICE_VARIANCE_WARNING", result.ReasonCodes);
    }

    [Fact]
    public void BlockingThresholds_ReturnBlocked()
    {
        var result = PurchaseMatchPolicyEvaluator.Evaluate(
            EnabledPolicy(),
            new PurchaseMatchVariance(5.01m, 3.01m, 25.01m, true));

        Assert.Equal("Blocked", result.Decision);
        Assert.Contains("PURCHASE_PRICE_VARIANCE_BLOCKED", result.ReasonCodes);
        Assert.Contains("PURCHASE_TAX_VARIANCE_BLOCKED", result.ReasonCodes);
        Assert.Contains("PURCHASE_BASE_VARIANCE_BLOCKED", result.ReasonCodes);
    }

    [Fact]
    public void RequiredReceiptMatch_IsHardBlock()
    {
        var result = PurchaseMatchPolicyEvaluator.Evaluate(
            EnabledPolicy(),
            new PurchaseMatchVariance(0m, 0m, 0m, false));

        Assert.Equal("Blocked", result.Decision);
        Assert.Contains("PURCHASE_RECEIPT_MATCH_REQUIRED", result.ReasonCodes);
    }

    [Fact]
    public void InvalidThresholdOrdering_IsRejected()
    {
        var policy = EnabledPolicy() with { PriceWarningPercent = 6m, PriceBlockPercent = 5m };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PurchaseMatchPolicyEvaluator.Evaluate(
                policy,
                new PurchaseMatchVariance(0m, 0m, 0m, true)));
    }

    [Fact]
    public void NegativeVariance_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PurchaseMatchPolicyEvaluator.Evaluate(
                EnabledPolicy(),
                new PurchaseMatchVariance(-1m, 0m, 0m, true)));
    }
}
