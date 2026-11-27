namespace ShouTech.Domain.Entities;

public sealed record PurchaseMatchPolicy(
    bool IsEnabled,
    bool RequireReceiptMatch,
    decimal PriceWarningPercent,
    decimal PriceBlockPercent,
    decimal TaxWarningPercent,
    decimal TaxBlockPercent,
    decimal BaseVarianceWarningAmount,
    decimal BaseVarianceBlockAmount,
    int PolicyVersion,
    // Q4 (migration 111): receipt over-delivery tolerance %. 0 = the
    // historical hard block at exactly the ordered quantity.
    decimal ReceiptOverTolerancePct = 0m)
{
    public void Validate()
    {
        if (ReceiptOverTolerancePct < 0m || ReceiptOverTolerancePct > 1000m)
            throw new ArgumentOutOfRangeException(nameof(ReceiptOverTolerancePct));
        if (PriceWarningPercent < 0 || PriceBlockPercent < PriceWarningPercent)
            throw new ArgumentOutOfRangeException(nameof(PriceBlockPercent));
        if (TaxWarningPercent < 0 || TaxBlockPercent < TaxWarningPercent)
            throw new ArgumentOutOfRangeException(nameof(TaxBlockPercent));
        if (BaseVarianceWarningAmount < 0 || BaseVarianceBlockAmount < BaseVarianceWarningAmount)
            throw new ArgumentOutOfRangeException(nameof(BaseVarianceBlockAmount));
        if (PolicyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(PolicyVersion));
    }
}

public sealed record PurchaseMatchVariance(
    decimal PriceVariancePercent,
    decimal TaxVariancePercent,
    decimal AbsoluteBaseVarianceAmount,
    bool HasReceiptMatch);

public sealed record PurchaseMatchEvaluation(
    string Decision,
    IReadOnlyList<string> ReasonCodes,
    int PolicyVersion);

public static class PurchaseMatchPolicyEvaluator
{
    public static PurchaseMatchEvaluation Evaluate(
        PurchaseMatchPolicy policy,
        PurchaseMatchVariance variance)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(variance);
        policy.Validate();

        if (variance.PriceVariancePercent < 0 ||
            variance.TaxVariancePercent < 0 ||
            variance.AbsoluteBaseVarianceAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(variance), "Variance values must be absolute and non-negative.");

        if (!policy.IsEnabled)
            return new PurchaseMatchEvaluation("NotConfigured", Array.Empty<string>(), policy.PolicyVersion);

        var reasons = new List<string>();
        var blocked = false;
        var warning = false;

        if (policy.RequireReceiptMatch && !variance.HasReceiptMatch)
        {
            blocked = true;
            reasons.Add("PURCHASE_RECEIPT_MATCH_REQUIRED");
        }

        EvaluateThreshold(
            variance.PriceVariancePercent,
            policy.PriceWarningPercent,
            policy.PriceBlockPercent,
            "PURCHASE_PRICE_VARIANCE_WARNING",
            "PURCHASE_PRICE_VARIANCE_BLOCKED",
            reasons,
            ref warning,
            ref blocked);

        EvaluateThreshold(
            variance.TaxVariancePercent,
            policy.TaxWarningPercent,
            policy.TaxBlockPercent,
            "PURCHASE_TAX_VARIANCE_WARNING",
            "PURCHASE_TAX_VARIANCE_BLOCKED",
            reasons,
            ref warning,
            ref blocked);

        EvaluateThreshold(
            variance.AbsoluteBaseVarianceAmount,
            policy.BaseVarianceWarningAmount,
            policy.BaseVarianceBlockAmount,
            "PURCHASE_BASE_VARIANCE_WARNING",
            "PURCHASE_BASE_VARIANCE_BLOCKED",
            reasons,
            ref warning,
            ref blocked);

        var decision = blocked ? "Blocked" : warning ? "Warning" : "Pass";
        return new PurchaseMatchEvaluation(decision, reasons.AsReadOnly(), policy.PolicyVersion);
    }

    private static void EvaluateThreshold(
        decimal value,
        decimal warningThreshold,
        decimal blockThreshold,
        string warningCode,
        string blockCode,
        ICollection<string> reasons,
        ref bool warning,
        ref bool blocked)
    {
        if (value > blockThreshold)
        {
            blocked = true;
            reasons.Add(blockCode);
        }
        else if (value > warningThreshold)
        {
            warning = true;
            reasons.Add(warningCode);
        }
    }
}

// ---- merged from src/Domain/Entities/PurchaseMatchEvaluationSnapshot.cs ----
public sealed record PurchaseMatchEvaluationSnapshot(
    Guid EvaluationId,
    long InvoiceDocId,
    int EvaluationVersion,
    int PolicyVersion,
    string Decision,
    IReadOnlyList<string> ReasonCodes,
    string PolicySnapshotJson,
    string ResultSnapshotJson,
    string? EvaluatedBy,
    DateTime EvaluatedAt);
