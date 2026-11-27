using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using Xunit;

namespace Kern.Tests;

public sealed class PurchaseMatchServiceTests
{
    [Fact]
    public async Task PolicyWrite_RequiresDedicatedManagePermission()
    {
        var repository = new FakePurchaseMatchRepository();
        var service = new PurchaseMatchService(repository, _ => false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SavePolicyAsync("COMP-01",
                new PurchaseMatchPolicy(true, true, 1m, 5m, 1m, 5m, 1m, 5m, 1),
                0, "buyer"));

        Assert.Equal(0, repository.SaveCalls);
    }

    [Fact]
    public async Task Override_RequiresDedicatedOverridePermission()
    {
        var repository = new FakePurchaseMatchRepository();
        var service = new PurchaseMatchService(repository, key => key == "PURCH.MATCH.POLICY.MANAGE");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReleaseBlockedEvaluationAsync(
                "COMP-01", 44, Guid.NewGuid(), "manager", "Approved by finance"));

        Assert.Equal(0, repository.OverrideCalls);
    }

    [Fact]
    public async Task AuthorizedOverride_IsPersistedAndAudited()
    {
        var repository = new FakePurchaseMatchRepository();
        var auditCalls = new List<string>();
        var service = new PurchaseMatchService(
            repository,
            key => key == "PURCH.MATCH.OVERRIDE",
            (_, action, details) =>
            {
                auditCalls.Add(action + "|" + details);
                return Task.CompletedTask;
            });
        var evaluationId = Guid.NewGuid();

        var overrideId = await service.ReleaseBlockedEvaluationAsync(
            "COMP-01", 44, evaluationId, "finance-manager", "Approved after finance review");

        Assert.Equal(repository.LastOverrideId, overrideId);
        Assert.Equal(1, repository.OverrideCalls);
        Assert.Equal(44, repository.LastOverrideInvoiceId);
        Assert.Equal(evaluationId, repository.LastOverrideEvaluationId);
        Assert.Equal("finance-manager", repository.LastOverrideActor);
        Assert.Equal("Approved after finance review", repository.LastOverrideReason);
        Assert.Single(auditCalls);
        Assert.Contains("Purchase match block released", auditCalls[0]);
        Assert.Contains("Approved after finance review", auditCalls[0]);
    }

    [Fact]
    public async Task AuthorizedPolicyWrite_IsPersistedAndAudited()
    {
        var repository = new FakePurchaseMatchRepository();
        var auditCalls = new List<string>();
        var service = new PurchaseMatchService(
            repository,
            key => key == "PURCH.MATCH.POLICY.MANAGE",
            (_, action, details) =>
            {
                auditCalls.Add(action + "|" + details);
                return Task.CompletedTask;
            });

        var policy = new PurchaseMatchPolicy(true, true, 2m, 5m, 1m, 3m, 10m, 25m, 1);
        await service.SavePolicyAsync("COMP-01", policy, 0, "manager");

        Assert.Equal(1, repository.SaveCalls);
        Assert.Single(auditCalls);
        Assert.Contains("Purchase match policy updated", auditCalls[0]);
    }

    private sealed class FakePurchaseMatchRepository : IPurchaseMatchPolicyRepository
    {
        public int SaveCalls { get; private set; }
        public int OverrideCalls { get; private set; }
        public Guid LastOverrideId { get; private set; }
        public long LastOverrideInvoiceId { get; private set; }
        public Guid LastOverrideEvaluationId { get; private set; }
        public string? LastOverrideActor { get; private set; }
        public string? LastOverrideReason { get; private set; }

        public Task<PurchaseMatchPolicy?> GetPolicyAsync(string companyCode, CancellationToken cancellationToken = default)
            => Task.FromResult<PurchaseMatchPolicy?>(null);

        public Task<IReadOnlyList<PurchaseMatchEvaluationSnapshot>> GetRecentEvaluationsAsync(
            string companyCode, int take = 50, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PurchaseMatchEvaluationSnapshot>>(Array.Empty<PurchaseMatchEvaluationSnapshot>());

        public Task SavePolicyAsync(
            string companyCode, PurchaseMatchPolicy policy, int expectedVersion, string actor,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return Task.CompletedTask;
        }

        public Task<Guid> RecordOverrideAsync(
            string companyCode, long invoiceDocId, Guid evaluationId, string actor, string reason,
            CancellationToken cancellationToken = default)
        {
            OverrideCalls++;
            LastOverrideId = Guid.NewGuid();
            LastOverrideInvoiceId = invoiceDocId;
            LastOverrideEvaluationId = evaluationId;
            LastOverrideActor = actor;
            LastOverrideReason = reason;
            return Task.FromResult(LastOverrideId);
        }

        public Task<PurchaseMatchEvaluationSnapshot> ReevaluateLatestAsync(
            string companyCode, long invoiceDocId, string actor,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new PurchaseMatchEvaluationSnapshot(
                Guid.NewGuid(), invoiceDocId, 1, 1, "Pass", Array.Empty<string>(),
                "{}", "{}", actor, DateTime.UtcNow));

    }
}

// ---- merged from tests/Kern.Tests/PurchaseMatchPolicyEvaluatorTests.cs ----
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
