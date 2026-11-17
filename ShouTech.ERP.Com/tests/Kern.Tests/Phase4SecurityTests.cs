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

    [Fact]
    public void Policy_AcceptsStrongPassword()
    {
        var errors = PwdPolicy.Validate("Str0ng!Passphrase", EnterprisePolicy(), "alice");
        Assert.Empty(errors);
    }

    [Fact]
    public void Policy_RejectsShortPassword()
    {
        var errors = PwdPolicy.Validate("Ab1!xyz", EnterprisePolicy(), "alice");
        Assert.Contains(errors, e => e.Contains("at least 12"));
    }

    [Fact]
    public void Policy_RejectsMissingCharacterClasses()
    {
        var policy = EnterprisePolicy();
        Assert.NotEmpty(PwdPolicy.Validate("alllowercase1!x", policy));   // no uppercase
        Assert.NotEmpty(PwdPolicy.Validate("ALLUPPERCASE1!X", policy));   // no lowercase
        Assert.NotEmpty(PwdPolicy.Validate("NoDigitsHere!x", policy));    // no digit
        Assert.NotEmpty(PwdPolicy.Validate("NoSymbol1234x", policy));     // no symbol
    }

    [Fact]
    public void Policy_RejectsCommonAndUsernamePasswords()
    {
        var policy = EnterprisePolicy();
        Assert.Contains(PwdPolicy.Validate("qwerty123", policy), e => e.Contains("common") || e.Contains("at least"));
        Assert.NotEmpty(PwdPolicy.Validate("SomeUser!123", policy, "someuser!123"));
    }

    [Fact]
    public void Policy_EnterpriseDefaults_AreStrong()
    {
        var policy = new PasswordPolicySettings();
        Assert.True(policy.MinLength >= 12);
        Assert.True(policy.RequireUpperCase);
        Assert.True(policy.RequireLowerCase);
        Assert.True(policy.RequireNumbers);
        Assert.True(policy.RequireSpecialChars);
        Assert.False(policy.AllowCommonPasswords);
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
