using System;
using System.Text;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Svc;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Phase 4 enterprise security tests: password policy, login lockout state
/// machine, SQL identifier safety, and webhook HMAC-SHA256 signing.
/// (Database-backed lockout enforcement is covered by the runtime smoke
/// harness against a migrated SQLite database.)
/// </summary>
public class Phase4SecurityTests
{
    private static PasswordPolicySettings EnterprisePolicy() => new();

    // ------------------------------------------------------------ PwdPolicy

    [Theory]
    [InlineData("abc")]
    [InlineData("123")]
    [InlineData("!@#")]
    [InlineData("a1!")]
    [InlineData("A B")]
    [InlineData("qwerty123")]
    public void Policy_AcceptsAnyUserChosenPasswordComposition(string password)
    {
        var errors = PwdPolicy.Validate(password, EnterprisePolicy(), "alice");
        Assert.Empty(errors);
    }

    [Fact]
    public void Policy_RejectsAnEmptyPasswordAsRequired()
    {
        var errors = PwdPolicy.Validate("", EnterprisePolicy(), "alice");
        Assert.Contains(errors, e => e.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("12")]
    public void Policy_RejectsNonEmptyPasswordsShorterThanThreeCharacters(string password)
    {
        var errors = PwdPolicy.Validate(password, EnterprisePolicy(), "alice");
        Assert.Contains(errors, e => e.Contains("at least 3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Policy_RejectsOnlyValuesExceedingMaximumLength()
    {
        var policy = EnterprisePolicy();
        var tooLong = new string('x', 129);
        Assert.Contains(PwdPolicy.Validate(tooLong, policy), e => e.Contains("not exceed 128"));
    }

    [Fact]
    public void Policy_DefaultsAllowUserChosenComposition()
    {
        var policy = new PasswordPolicySettings();
        Assert.Equal(3, policy.MinLength);
        Assert.Equal(128, policy.MaxLength);
        Assert.False(policy.RequireUpperCase);
        Assert.False(policy.RequireLowerCase);
        Assert.False(policy.RequireNumbers);
        Assert.False(policy.RequireSpecialChars);
        Assert.True(policy.AllowCommonPasswords);
    }

    // ---------------------------------------------------------- LoginLockout

    [Fact]
    public void Lockout_LocksAtThresholdAndExpires()
    {
        var now = new DateTime(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);
        var state = LoginLockout.Clean;

        for (var i = 0; i < 4; i++)
            state = LoginLockout.RegisterFailure(state, now, maxAttempts: 5, lockoutMinutes: 15);
        Assert.Equal(4, state.FailedCount);
        Assert.False(LoginLockout.IsLocked(state, now));

        state = LoginLockout.RegisterFailure(state, now, maxAttempts: 5, lockoutMinutes: 15);
        Assert.Equal(5, state.FailedCount);
        Assert.True(LoginLockout.IsLocked(state, now.AddMinutes(14)));
        Assert.False(LoginLockout.IsLocked(state, now.AddMinutes(15).AddTicks(1)));
    }

    [Fact]
    public void Lockout_SuccessResetsToClean()
    {
        var state = new LoginLockout.State(4, null);
        var clean = LoginLockout.RegisterSuccess();
        Assert.Equal(0, clean.FailedCount);
        Assert.Null(clean.LockedUntilUtc);
        Assert.NotEqual(state, clean);
    }

    [Fact]
    public void Lockout_ConfiguredLimitsHaveSaneDefaults()
    {
        var (maxAttempts, lockoutMinutes) = LoginLockout.GetConfiguredLimits();
        Assert.True(maxAttempts >= 1);
        Assert.True(lockoutMinutes >= 1);
    }

    // ---------------------------------------------------------- SqlIdentifier

    [Theory]
    [InlineData("users", true)]
    [InlineData("sales_documents", true)]
    [InlineData("_audit2", true)]
    [InlineData("users; DROP TABLE users", false)]
    [InlineData("user name", false)]
    [InlineData("1users", false)]
    [InlineData("users--", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SqlIdentifier_ShapeValidation(string? name, bool expected)
        => Assert.Equal(expected, SqlIdentifier.IsSafe(name));

    [Fact]
    public void SqlIdentifier_QuoteRejectsUnsafe()
    {
        Assert.Equal("\"journal_entries\"", SqlIdentifier.Quote("journal_entries"));
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Quote("x\"; DROP TABLE users; --"));
    }

    // ---------------------------------------------------------- WebhookSigner

    [Fact]
    public void WebhookSigner_MatchesRfc4231TestCase2()
    {
        // RFC 4231 Test Case 2: key "Jefe", data "what do ya want for nothing?"
        var body = Encoding.UTF8.GetBytes("what do ya want for nothing?");
        var hex = WebhookSigner.ComputeSignatureHex(body, "Jefe");
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", hex);
        Assert.Equal("sha256=" + hex, WebhookSigner.ComputeSignatureHeaderValue(body, "Jefe"));
    }

    [Fact]
    public void WebhookSigner_DifferentBodyDifferentSignature()
    {
        var a = WebhookSigner.ComputeSignatureHex(Encoding.UTF8.GetBytes("{\"a\":1}"), "s3cr3t-key");
        var b = WebhookSigner.ComputeSignatureHex(Encoding.UTF8.GetBytes("{\"a\":2}"), "s3cr3t-key");
        Assert.NotEqual(a, b);
    }

    // ---------------------------------------------------------------- PwdHash

    [Fact]
    public void PwdHash_RoundTrips_AndHonorsStoredIterations()
    {
        var created = PwdHash.Create("Str0ng!Passphrase", iterations: 100_000);
        Assert.True(PwdHash.Verify("Str0ng!Passphrase", created.Hash, created.Salt));
        Assert.False(PwdHash.Verify("wrong-password", created.Hash, created.Salt));
        // Stored at a lower work factor than the current default: flagged for
        // the transparent rehash-on-login upgrade, still verifiable.
        Assert.True(PwdHash.NeedsRehash(created.Hash));
        var current = PwdHash.Create("Str0ng!Passphrase");
        Assert.False(PwdHash.NeedsRehash(current.Hash));
    }
}
