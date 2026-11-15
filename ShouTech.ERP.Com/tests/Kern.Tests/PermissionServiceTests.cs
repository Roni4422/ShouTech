using ShouTech.App.Services;
using Xunit;

namespace Kern.Tests;

public sealed class PermissionServiceTests
{
    [Fact]
    public void AdministratorReceivesPermissionBypassOnlyForAuthenticatedSession()
    {
        var previous = UsrSession.Current;
        try
        {
            UsrSession.Apply(new UserInfo
            {
                IsAuthenticated = true,
                AccessLevel = UsrAccLvl.Administrator
            });

            Assert.True(new PermServ().Has(PermCat.Customers.Create));

            UsrSession.Apply(new UserInfo
            {
                IsAuthenticated = false,
                AccessLevel = UsrAccLvl.Administrator
            });

            Assert.False(new PermServ().Has(PermCat.Customers.Create));
        }
        finally
        {
            UsrSession.Apply(previous);
        }
    }

    [Fact]
    public void UserWithoutLoadedPermissionIsDenied()
    {
        var previous = UsrSession.Current;
        try
        {
            UsrSession.Apply(new UserInfo
            {
                IsAuthenticated = true,
                AccessLevel = UsrAccLvl.User
            });

            Assert.False(new PermServ().Has(PermCat.Customers.Create));
        }
        finally
        {
            UsrSession.Apply(previous);
        }
    }
}
