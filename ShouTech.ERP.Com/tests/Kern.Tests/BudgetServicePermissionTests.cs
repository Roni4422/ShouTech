using System;
using System.Threading.Tasks;
using ShouTech.Application.Services;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

public sealed class BudgetServicePermissionTests
{
    private static BudgetService DeniedService()
        => new(new BudgetRepository("SQLite", "Data Source=:memory:"), _ => false);

    [Fact]
    public async Task ReadRequiresBudgetViewPermission()
    {
        var service = DeniedService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAllAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetVarianceAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetFiscalYearsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteRequiresBudgetEditPermission()
    {
        var service = DeniedService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), "user"));
    }

    [Fact]
    public async Task StatusChangeRequiresBudgetActivationPermission()
    {
        var service = DeniedService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetStatusAsync(Guid.NewGuid(), Guid.NewGuid(), "Active", "user"));
    }
}
