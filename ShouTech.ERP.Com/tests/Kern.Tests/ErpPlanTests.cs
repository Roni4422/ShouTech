using System.Linq;
using ShouTech.Application.Services;
using Xunit;

namespace Kern.Tests;

public class ErpPlanTests
{
    [Fact]
    public void ErpPlan_ShouldPrioritizeCoreModulesFirst()
    {
        var plan = new ErpPlan();

        var feats = plan.Pri();

        Assert.Equal("ACC", feats.First().Code);
        Assert.Contains(feats, x => x.Code == "DASH");
        Assert.Contains(feats, x => x.Code == "AI");
    }

    [Fact]
    public void ErpPlan_ShouldExposeFastWorkflowChecklist()
    {
        var plan = new ErpPlan();

        var workflow = plan.FastFlow();

        Assert.Contains(workflow, x => x.Contains("invoice", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(workflow, x => x.Contains("stock", System.StringComparison.OrdinalIgnoreCase));
    }
}
