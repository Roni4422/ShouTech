namespace ShouTech.Domain;

/// <summary>
/// A cataloged application error. Every deliberate failure raised by the
/// library layers (Domain / Application / Infrastructure) is an
/// <see cref="AppError"/> carrying a stable code from the central error
/// catalog (see docs/ERROR_CATALOG.md and <see cref="ErrorCodes"/>).
///
/// The app layer never shows <see cref="Exception.Message"/> of an
/// <see cref="AppError"/> directly to the user as the primary text: it
/// resolves <see cref="Code"/> through the error catalog service, which
/// returns the localized message and hint for the current language.
/// The message passed to the constructor is the canonical English text
/// (identical to the catalog's English message) so logs stay readable
/// even before the catalog is consulted.
///
/// Codes are immutable once assigned: never renumber, never reuse a
/// retired code for a different meaning.
/// </summary>
public sealed class AppError : Exception
{
    /// <summary>Stable catalog code, e.g. "ACC-5001".</summary>
    public string Code { get; }

    /// <summary>Optional format arguments for the catalog message ({0}, {1}, ...).</summary>
    public IReadOnlyList<object> Args { get; }

    public AppError(string code, string message, params object[] args)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? ErrorCodes.Unknown : code.Trim();
        Args = args ?? Array.Empty<object>();
    }

    public AppError(string code, string message, Exception innerException, params object[] args)
        : base(message, innerException)
    {
        Code = string.IsNullOrWhiteSpace(code) ? ErrorCodes.Unknown : code.Trim();
        Args = args ?? Array.Empty<object>();
    }
}
