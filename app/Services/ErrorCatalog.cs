// ========================================================================
// FILE: Services/ErrorCatalog.cs
// PROJECT: SHOUTECH ERP — Numbered error catalog (the index)
//
// Every error in the application has a stable code (see
// ShouTech.Domain.ErrorCodes). The catalog files
// Resources/Errors/{En,Ar,Fr,Tr}.json map code -> name + localized
// message + hint. At runtime the code is looked up here and the right
// error is shown/logged in the current language.
//
// This service is the ONLY supported way to report an error:
//   AppHost.Errors.Report(code, exception, context)   (one line, logs)
//   AppHost.Errors.Resolve(code, args)                (dialog text)
//   AppHost.Errors.Classify(exception)                (code for a raw ex)
//
// It is intentionally independent of the legacy static catalog in
// Localization/ErrorCatalog.cs (Errors.json, legacy SYS-1xxx/USR-2xxx
// codes) which the ribbon windows still use; the two code spaces do
// not overlap. See docs/ERROR_CATALOG.md.
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using ShouTech.Domain;

namespace ShouTech.App.Services
{
    /// <summary>One resolved catalog entry, ready to show or log.</summary>
    public sealed class ErrorInfo
    {
        /// <summary>The code that was actually resolved (SYS-0001 when the requested code is unknown).</summary>
        public string Code { get; }

        /// <summary>The code the caller asked for (differs from <see cref="Code"/> only on fallback).</summary>
        public string RequestedCode { get; }

        /// <summary>Short English identifier of the error, e.g. "AccountLocked". Language-independent.</summary>
        public string Name { get; }

        /// <summary>Localized, formatted message for the current language.</summary>
        public string Message { get; }

        /// <summary>Localized user guidance, when the catalog provides it.</summary>
        public string? Hint { get; }

        /// <summary>True when <see cref="RequestedCode"/> exists in the catalog.</summary>
        public bool IsKnown { get; }

        public ErrorInfo(string code, string requestedCode, string name, string message, string? hint, bool isKnown)
        {
            Code = code;
            RequestedCode = requestedCode;
            Name = name;
            Message = message;
            Hint = hint;
            IsKnown = isKnown;
        }
    }

    public interface IErrorCatalog
    {
        /// <summary>
        /// Resolves a code to its localized message/hint in the current
        /// language (per-key English fallback). Unknown codes resolve to
        /// SYS-0001 with <see cref="ErrorInfo.IsKnown"/> = false.
        /// </summary>
        ErrorInfo Resolve(string code, params object[] args);

        /// <summary>Maps an exception to a catalog code (AppError code wins).</summary>
        string Classify(Exception? exception);

        /// <summary>
        /// The one-line error report: stamps the catalog code + name into
        /// errors.log / log.log through the coded LogError overload.
        /// Never throws.
        /// </summary>
        void Report(
            string code,
            Exception? exception = null,
            string? context = null,
            [CallerFilePath] string sourceFile = "",
            [CallerMemberName] string sourceMember = "",
            [CallerLineNumber] int sourceLine = 0);

        /// <summary>All codes registered in the English catalog (the index).</summary>
        IReadOnlyCollection<string> KnownCodes { get; }
    }

    /// <summary>
    /// Loads Resources/Errors/{En,Ar,Fr,Tr}.json. English is the base
    /// layer; the selected language overlays per key — the same fallback
    /// pattern as <see cref="LocSvc"/>. Thread-safe; catalogs are loaded
    /// lazily and cached per language.
    /// </summary>
    public sealed class ErrorCatalogSvc : IErrorCatalog
    {
        private sealed class Entry
        {
            public string Name = string.Empty;
            public string Message = string.Empty;
            public string? Hint;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Func<string> _languageProvider;
        private readonly string _catalogDirectory;
        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, Entry>> _overlays = new(StringComparer.OrdinalIgnoreCase);

        // The English index is static so the logging layer (LogServ) can
        // stamp [Name=...] onto error lines before/without a service
        // instance (logging is initialized first and must never depend
        // on this service being constructed). It resolves from the
        // deployed catalog directory next to the executable.
        private static readonly Lazy<Dictionary<string, Entry>> EnglishIndex = new(() => LoadCatalogFile(ResolveCatalogDirectory(), "En.json"));

        // The instance base layer: the same English index, but loaded
        // from THIS instance's catalog directory, so an instance pointed
        // at another directory (tests, tools) resolves consistently.
        private readonly Lazy<Dictionary<string, Entry>> _english;

        public ErrorCatalogSvc(Func<string> languageProvider, string? catalogDirectory = null)
        {
            _languageProvider = languageProvider ?? throw new ArgumentNullException(nameof(languageProvider));
            _catalogDirectory = catalogDirectory ?? ResolveCatalogDirectory();
            _english = new Lazy<Dictionary<string, Entry>>(() => LoadCatalogFile(_catalogDirectory, "En.json"));
        }

        public IReadOnlyCollection<string> KnownCodes => _english.Value.Keys;

        // ---------------------------------------------------------------
        // Static catalog access for the logging layer
        // ---------------------------------------------------------------

        /// <summary>True when the code exists in the English catalog (the index).</summary>
        public static bool IsKnownCode(string? code)
            => !string.IsNullOrWhiteSpace(code) && EnglishIndex.Value.ContainsKey(code.Trim());

        /// <summary>The catalog name for a code (English identifier), or the SYS-0001 name for unknown codes.</summary>
        public static string GetCatalogName(string? code)
        {
            if (!string.IsNullOrWhiteSpace(code) && EnglishIndex.Value.TryGetValue(code.Trim(), out var entry))
                return entry.Name;

            return EnglishIndex.Value.TryGetValue(ErrorCodes.Unknown, out var fallback)
                ? fallback.Name
                : "UnknownError";
        }

        // ---------------------------------------------------------------
        // IErrorCatalog
        // ---------------------------------------------------------------

        public ErrorInfo Resolve(string code, params object[] args)
        {
            var requested = string.IsNullOrWhiteSpace(code) ? ErrorCodes.Unknown : code.Trim();

            var hasEnglish = _english.Value.TryGetValue(requested, out var englishEntry);

            // Selected-language overlay: per-field fallback — a field the
            // overlay entry leaves empty keeps the English value.
            Entry? overlay = null;
            var language = NormalizeLanguage(SafeCurrentLanguage());
            if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
                GetOverlay(language).TryGetValue(requested, out overlay);

            if (!hasEnglish)
            {
                // Unknown code (a code must exist in the English index to
                // be known): fall back to SYS-0001; the raw requested
                // code is preserved on the result (and in the log line).
                var fallback = _english.Value.TryGetValue(ErrorCodes.Unknown, out var unknownEntry)
                    ? unknownEntry
                    : new Entry { Name = "UnknownError", Message = "An unexpected error occurred." };

                return new ErrorInfo(
                    ErrorCodes.Unknown,
                    requested,
                    fallback.Name,
                    Format(fallback.Message, args),
                    fallback.Hint,
                    isKnown: false);
            }

            var name = !string.IsNullOrWhiteSpace(overlay?.Name) ? overlay!.Name : englishEntry!.Name;
            var message = !string.IsNullOrWhiteSpace(overlay?.Message) ? overlay!.Message : englishEntry!.Message;
            var hint = !string.IsNullOrWhiteSpace(overlay?.Hint) ? overlay!.Hint : englishEntry!.Hint;

            return new ErrorInfo(
                requested,
                requested,
                name,
                Format(message, args),
                hint,
                isKnown: true);
        }

        public string Classify(Exception? exception) => ClassifyException(exception);

        /// <summary>
        /// Maps common exception types to default catalog codes. Static so
        /// LogServ can classify without a service instance. An
        /// <see cref="AppError"/> always wins with its own code; an
        /// <see cref="AggregateException"/> is classified by its first
        /// inner exception.
        /// </summary>
        public static string ClassifyException(Exception? exception)
        {
            switch (exception)
            {
                case null:
                    return ErrorCodes.Unknown;
                case AppError appError:
                    return appError.Code;
                case AggregateException aggregate when aggregate.InnerExceptions.Count > 0:
                    return ClassifyException(aggregate.InnerExceptions[0]);
                case Microsoft.Data.Sqlite.SqliteException:
                case Npgsql.PostgresException:
                case System.Data.Common.DbException:
                    return ErrorCodes.DatabaseQueryFailed;
                case FileNotFoundException:
                case DirectoryNotFoundException:
                case IOException:
                case UnauthorizedAccessException:
                    return ErrorCodes.FileAccessFailed;
                case TimeoutException:
                    return ErrorCodes.DatabaseConnectionFailed;
                default:
                    return ErrorCodes.Unknown;
            }
        }

        public void Report(
            string code,
            Exception? exception = null,
            string? context = null,
            [CallerFilePath] string sourceFile = "",
            [CallerMemberName] string sourceMember = "",
            [CallerLineNumber] int sourceLine = 0)
        {
            // Reporting must never throw into the caller's error path.
            try
            {
                var appArgs = (exception as AppError)?.Args;
                var info = Resolve(code, appArgs == null ? Array.Empty<object>() : new List<object>(appArgs).ToArray());
                // Capture the reporting call site at compile time. This is
                // more reliable than guessing the originating window from a
                // translated message or from a later global exception handler.
                var normalizedPath = (sourceFile ?? string.Empty)
                    .Replace('\\\\', '/');
                var fileName = string.IsNullOrWhiteSpace(normalizedPath)
                    ? "Unknown"
                    : Path.GetFileName(normalizedPath);
                var windowName = normalizedPath.Contains("/Views/", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(fileName)
                    : "Non-Window";
                var source = $"Window={windowName} | SourceFile={fileName} | Method={sourceMember} | Line={sourceLine}";
                var message = string.IsNullOrWhiteSpace(context)
                    ? $"{info.Name} | {source}"
                    : $"{info.Name} | {context} | {source}";

                if (!info.IsKnown)
                    message += $" | UnregisteredCode={info.RequestedCode}";

                AppHost.Logging?.LogError("Errors", info.Code, message, exception);
            }
            catch
            {
                // The catalog's own reporting path is the one place a
                // failure cannot be reported through itself; swallow so a
                // broken catalog can never take down an error handler.
            }
        }

        /// <summary>
        /// Last-resort logging for the catalog's own internal failures:
        /// writes a coded SYS-0001 line through the logging service
        /// directly, bypassing catalog resolution. Never throws.
        /// </summary>
        private static void SafeLog(string message)
        {
            try
            {
                AppHost.Logging?.LogError("Errors", ErrorCodes.Unknown, message);
            }
            catch
            {
                // Terminal fallback: nothing may throw out of error reporting.
            }
        }

        // ---------------------------------------------------------------
        // Loading
        // ---------------------------------------------------------------

        private string SafeCurrentLanguage()
        {
            try
            {
                return _languageProvider() ?? "en";
            }
            catch (Exception ex)
            {
                SafeLog($"Error catalog language provider failed: {ex.Message}");
                return "en";
            }
        }

        private static string NormalizeLanguage(string? languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                return "en";

            var code = languageCode.Trim().ToLowerInvariant();
            return code.Length > 2 ? code[..2] : code;
        }

        private Dictionary<string, Entry> GetOverlay(string language)
        {
            lock (_gate)
            {
                if (_overlays.TryGetValue(language, out var cached))
                    return cached;

                var fileName = language switch
                {
                    "ar" => "Ar.json",
                    "fr" => "Fr.json",
                    "tr" => "Tr.json",
                    _ => $"{char.ToUpper(language[0], CultureInfo.InvariantCulture)}{language[1..]}.json"
                };

                var loaded = LoadCatalogFile(_catalogDirectory, fileName);
                _overlays[language] = loaded;
                return loaded;
            }
        }

        private static Dictionary<string, Entry> LoadCatalogFile(string directory, string fileName)
        {
            var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(directory, fileName);
                if (!File.Exists(path))
                    return result;

                var raw = JsonSerializer.Deserialize<Dictionary<string, RawEntry>>(File.ReadAllText(path), JsonOptions);
                if (raw == null)
                    return result;

                foreach (var pair in raw)
                {
                    if (pair.Value == null)
                        continue;

                    result[pair.Key.Trim()] = new Entry
                    {
                        Name = string.IsNullOrWhiteSpace(pair.Value.Name) ? pair.Key : pair.Value.Name,
                        Message = pair.Value.Message ?? string.Empty,
                        Hint = string.IsNullOrWhiteSpace(pair.Value.Hint) ? null : pair.Value.Hint
                    };
                }
            }
            catch (Exception ex)
            {
                // A malformed optional catalog file must never prevent
                // startup or error reporting; resolution falls back to
                // the English index / SYS-0001.
                SafeLog($"Failed to load error catalog file '{fileName}': {ex.Message}");
            }

            return result;
        }

        private static string ResolveCatalogDirectory()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(basePath, "Resources", "Errors"),
                Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "Resources", "Errors")),
                Path.GetFullPath(Path.Combine(basePath, "..", "..", "Resources", "Errors"))
            };

            foreach (var path in candidates)
            {
                if (Directory.Exists(path))
                    return path;
            }

            return candidates[0];
        }

        private static string Format(string message, object[]? args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrEmpty(message))
                return message;

            try
            {
                return string.Format(CultureInfo.InvariantCulture, message, args);
            }
            catch (FormatException ex)
            {
                SafeLog($"Error catalog message format failed: {ex.Message}");
                return message;
            }
        }

        private sealed class RawEntry
        {
            public string? Code { get; set; }
            public string? Name { get; set; }
            public string? Message { get; set; }
            public string? Hint { get; set; }
        }
    }
}
