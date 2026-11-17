using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Phase 3 service-layer coverage: depreciation math (straight-line and
/// declining-balance, incl. pro-rated accumulated depreciation), project/task
/// validation, and reconciliation input validation. Repository fakes keep the
/// tests at the service contract, following the existing Kern.Tests patterns.
/// </summary>
public class Phase3ServiceTests
{
    // ---------------------------------------------------------------- Assets

    [Fact]
    public void StraightLine_Schedule_ReachesSalvageExactly()
    {
        var asset = new FixedAsset
        {
            Code = "A1", Name = "Truck", Cost = 10000m, SalvageValue = 1000m,
            UsefulLifeYears = 3, DepreciationMethod = DepreciationMethods.StraightLine,
            PurchaseDate = new DateOnly(2024, 1, 1)
        };

        var rows = AssetService.BuildSchedule(asset);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3000m, rows[0].Depreciation);
        Assert.Equal(7000m, rows[0].BookValue);
        Assert.Equal(9000m, rows[2].Accumulated);
        Assert.Equal(1000m, rows[2].BookValue);
    }

    [Fact]
    public void DecliningBalance_Schedule_NeverDropsBelowSalvage()
    {
        var asset = new FixedAsset
        {
            Code = "A2", Name = "Machine", Cost = 10000m, SalvageValue = 500m,
            UsefulLifeYears = 5, DepreciationMethod = DepreciationMethods.DecliningBalance,
            PurchaseDate = new DateOnly(2024, 1, 1)
        };

        var rows = AssetService.BuildSchedule(asset);

        Assert.Equal(4000m, rows[0].Depreciation); // 40% double-declining on 10000
        Assert.All(rows, r => Assert.True(r.BookValue >= 500m));
        Assert.Equal(500m, rows[^1].BookValue);
        Assert.Equal(9500m, rows[^1].Accumulated);
    }

    [Fact]
    public void AccumulatedDepreciation_ProRatesByWholeMonths_AndCapsAtDepreciableBase()
    {
        var asset = new FixedAsset
        {
            Code = "A3", Name = "Laptop pool", Cost = 12000m, SalvageValue = 0m,
            UsefulLifeYears = 4, DepreciationMethod = DepreciationMethods.StraightLine,
            PurchaseDate = new DateOnly(2024, 1, 15)
        };

        Assert.Equal(0m, AssetService.AccumulatedDepreciation(asset, new DateOnly(2024, 1, 14)));
        Assert.Equal(1500m, AssetService.AccumulatedDepreciation(asset, new DateOnly(2024, 7, 15))); // 6 x 250
        Assert.Equal(3000m, AssetService.AccumulatedDepreciation(asset, new DateOnly(2025, 1, 15))); // 12 months
        Assert.Equal(12000m, AssetService.AccumulatedDepreciation(asset, new DateOnly(2030, 6, 1))); // capped
        Assert.Equal(0m, AssetService.BookValue(asset, new DateOnly(2030, 6, 1)));
    }

    [Fact]
    public async Task AssetService_Save_RejectsInvalidRegisterData()
    {
        var service = new AssetService(new FakeAssetRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new FixedAsset { Code = "X", Name = "Y", Cost = 100m, SalvageValue = 150m, UsefulLifeYears = 5 }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new FixedAsset { Code = "X", Name = "Y", Cost = 100m, UsefulLifeYears = 0 }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new FixedAsset { Code = " ", Name = "Y", Cost = 100m, UsefulLifeYears = 5 }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new FixedAsset { Code = "X", Name = "Y", Cost = -1m, UsefulLifeYears = 5 }, "u1"));
    }

    [Fact]
    public async Task AssetService_Save_PersistsValidAsset()
    {
        var repo = new FakeAssetRepository();
        var service = new AssetService(repo);
        var asset = new FixedAsset { Code = "FA-1", Name = "Forklift", Cost = 25000m, SalvageValue = 2500m, UsefulLifeYears = 8 };

        var id = await service.SaveAsync("C1", asset, "u1");

        Assert.Equal(asset.Id, id);
        Assert.Single(repo.Items);
    }

    // -------------------------------------------------------------- Projects

    [Fact]
    public async Task ProjectService_Save_RejectsInvalidProjects()
    {
        var service = new ProjectService(new FakeProjectRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new Project { Code = "P1", Name = "X", Budget = -5m }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new Project { Code = "P1", Name = "X", StartDate = new DateOnly(2026, 5, 1), EndDate = new DateOnly(2026, 4, 1) }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new Project { Code = "P1", Name = "X", Status = "Bogus" }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync("C1", new Project { Code = "", Name = "X" }, "u1"));
    }

    [Fact]
    public async Task ProjectService_SaveAndTask_RoundTrip()
    {
        var repo = new FakeProjectRepository();
        var service = new ProjectService(repo);
        var project = new Project
        {
            Code = "PRJ-1", Name = "Warehouse fit-out", Budget = 50000m,
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 6, 30),
            Status = ProjectStatuses.Active
        };

        var id = await service.SaveAsync("C1", project, "u1");
        var taskId = await service.SaveTaskAsync("C1", new ProjectTask { ProjectId = id, Name = "Survey", Status = ProjectTaskStatuses.ToDo }, "u1");

        Assert.Single(repo.Items);
        Assert.Equal(taskId, repo.Tasks.Single().Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveTaskAsync("C1", new ProjectTask { ProjectId = id, Name = " ", Status = ProjectTaskStatuses.ToDo }, "u1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveTaskAsync("C1", new ProjectTask { ProjectId = id, Name = "Ok", Status = "Nope" }, "u1"));
    }

    // -------------------------------------------------------- Reconciliation

    [Fact]
    public async Task ReconciliationService_Allocate_ValidatesInputsBeforeRepository()
    {
        var repo = new FakeReconciliationRepository();
        var service = new ReconciliationService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AllocateAsync("C1", "C", 10, "SALES", 20, 0m, "u1"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AllocateAsync("C1", "X", 10, "SALES", 20, 5m, "u1"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AllocateAsync("C1", "C", 10, "LEDGER", 20, 5m, "u1"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AllocateAsync("C1", "C", 0, "SALES", 20, 5m, "u1"));
        Assert.Null(repo.LastCall);
    }

    [Fact]
    public async Task ReconciliationService_Allocate_PassesThroughToRepository()
    {
        var repo = new FakeReconciliationRepository();
        var service = new ReconciliationService(repo);

        var id = await service.AllocateAsync("C1", "S", 42, "PURCHASE", 77, 125.50m, "u1", "partial");

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal((42L, "PURCHASE", 77L, 125.50m), repo.LastCall);
    }

    // ------------------------------------------------------------------ Fakes

    private sealed class FakeAssetRepository : IAssetRepository
    {
        public List<FixedAsset> Items { get; } = new();
        public Task<IReadOnlyList<FixedAsset>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FixedAsset>>(Items);
        public Task SaveAsync(string companyId, FixedAsset asset, string userId, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(a => a.Id == asset.Id);
            Items.Add(asset);
            return Task.CompletedTask;
        }
        public Task SoftDeleteAsync(string companyId, Guid assetId, string userId, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(a => a.Id == assetId);
            return Task.CompletedTask;
        }
        public Task<string?> PostDepreciationAsync(string companyId, Guid assetId, decimal accumulatedDepreciation, DateOnly asOf, string userId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeProjectRepository : IProjectRepository
    {
        public List<Project> Items { get; } = new();
        public List<ProjectTask> Tasks { get; } = new();
        public Task<IReadOnlyList<Project>> GetAllAsync(string companyId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Project>>(Items);
        public Task SaveAsync(string companyId, Project project, string userId, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(p => p.Id == project.Id);
            Items.Add(project);
            return Task.CompletedTask;
        }
        public Task SoftDeleteAsync(string companyId, Guid projectId, string userId, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(p => p.Id == projectId);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ProjectTask>> GetTasksAsync(string companyId, Guid projectId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProjectTask>>(Tasks.Where(t => t.ProjectId == projectId).ToList());
        public Task SaveTaskAsync(string companyId, ProjectTask task, string userId, CancellationToken cancellationToken = default)
        {
            Tasks.RemoveAll(t => t.Id == task.Id);
            Tasks.Add(task);
            return Task.CompletedTask;
        }
        public Task SoftDeleteTaskAsync(string companyId, Guid taskId, string userId, CancellationToken cancellationToken = default)
        {
            Tasks.RemoveAll(t => t.Id == taskId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReconciliationRepository : IReconciliationRepository
    {
        public (long Payment, string DocTable, long Doc, decimal Amount)? LastCall { get; private set; }
        public Task<IReadOnlyList<OpenDocumentRow>> GetOpenDocumentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OpenDocumentRow>>(Array.Empty<OpenDocumentRow>());
        public Task<IReadOnlyList<UnallocatedPaymentRow>> GetPaymentsAsync(string companyId, string partyKind, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UnallocatedPaymentRow>>(Array.Empty<UnallocatedPaymentRow>());
        public Task<Guid> AllocateAsync(string companyId, string partyKind, long paymentLedgerId, string docTable, long docId, decimal amount, string userId, string? note = null, CancellationToken cancellationToken = default)
        {
            LastCall = (paymentLedgerId, docTable, docId, amount);
            return Task.FromResult(Guid.NewGuid());
        }
    }
}
