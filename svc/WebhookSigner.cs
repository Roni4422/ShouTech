using System;
using System.Security.Cryptography;
using System.Text;

namespace ShouTech.Svc;

/// <summary>
/// Webhook payload signing (Phase 4 enterprise hardening).
///
/// When the operator configures SHOUTECH_WEBHOOK_SECRET, every outbound
/// webhook carries the header:
///     X-ShouTech-Signature: sha256=&lt;hex HMAC-SHA256 of the raw body&gt;
/// Receivers can verify authenticity and integrity by recomputing the HMAC
/// over the exact request body bytes with the shared secret. When no secret
/// is configured, messages are sent unsigned (the worker logs a warning once)
/// so existing unsigned receivers keep working. Delivery semantics are
/// unchanged (at-least-once); the signature covers the body bytes exactly as
/// transmitted.
/// </summary>
public static class WebhookSigner
{
    public const string SignatureHeaderName = "X-ShouTech-Signature";
    public const string SecretEnvironmentVariable = "SHOUTECH_WEBHOOK_SECRET";

    /// <summary>Returns the configured signing secret, or null when signing is disabled.</summary>
    public static string? GetConfiguredSecret()
    {
        var secret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        return string.IsNullOrWhiteSpace(secret) ? null : secret;
    }

    /// <summary>Computes the lowercase hex HMAC-SHA256 of <paramref name="body"/> keyed by <paramref name="secret"/>.</summary>
    public static string ComputeSignatureHex(ReadOnlySpan<byte> body, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>The full header value: <c>sha256=&lt;hex&gt;</c>.</summary>
    public static string ComputeSignatureHeaderValue(ReadOnlySpan<byte> body, string secret)
        => "sha256=" + ComputeSignatureHex(body, secret);
}
