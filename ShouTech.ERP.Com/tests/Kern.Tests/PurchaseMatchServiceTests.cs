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
            return Task.FromResult(Guid.NewGuid());
        }
    }
}
