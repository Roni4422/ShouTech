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
    public void ReadRequiresBudgetViewPermission()
    {
        var service = DeniedService();
        Assert.Throws<UnauthorizedAccessException>(() => service.GetAllAsync(Guid.NewGuid()));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetVarianceAsync(Guid.NewGuid()));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetFiscalYearsAsync(Guid.NewGuid()));
    }

    [Fact]
    public void DeleteRequiresBudgetEditPermission()
    {
        var service = DeniedService();
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), "user"));
    }

    [Fact]
    public void StatusChangeRequiresBudgetActivationPermission()
    {
        var service = DeniedService();
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.SetStatusAsync(Guid.NewGuid(), Guid.NewGuid(), "Active", "user"));
    }
}
