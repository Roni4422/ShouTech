using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using ShouTech.Domain;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// Error catalog integrity and behavior (feat/error-catalog):
/// the catalog is the index that maps a code to the right localized
/// error, so its invariants are tested directly — no duplicate codes,
/// English completeness, per-language keys subset of English, English
/// fallback, classification, and AppError code carriage.
/// </summary>
public class ErrorCatalogTests
{
    private static string CatalogDirectory()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "Resources", "Errors");
        if (File.Exists(Path.Combine(fromOutput, "En.json")))
            return fromOutput;

        // Walk up from the test output directory to the repo source tree.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "cod", "app", "src", "Resources", "Errors");
            if (File.Exists(Path.Combine(candidate, "En.json")))
                return candidate;

            var nested = Path.Combine(dir.FullName, "ShouTech.ERP.Com", "cod", "app", "src", "Resources", "Errors");
            if (File.Exists(Path.Combine(nested, "En.json")))
                return nested;
        }

        throw new DirectoryNotFoundException("Error catalog directory (Resources/Errors) not found.");
    }

    private static Dictionary<string, JsonElement> Load(string fileName)
    {
        var path = Path.Combine(CatalogDirectory(), fileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in doc.RootElement.EnumerateObject())
            result[pair.Name] = pair.Value.Clone();
        return result;
    }

    [Fact]
    public void ErrorCodes_Constants_AreUnique_AndAllExistInEnglishCatalog()
    {
        var constants = typeof(ErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(constants);
        Assert.Equal(constants.Count, constants.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var en = Load("En.json");
        foreach (var code in constants)
            Assert.True(en.ContainsKey(code), $"ErrorCodes constant {code} has no entry in En.json.");
    }

    [Fact]
    public void EveryCode_InAnyLanguage_ExistsInEnglish()
    {
        var en = Load("En.json");
        foreach (var file in new[] { "Ar.json", "Fr.json", "Tr.json" })
        {
            var other = Load(file);
            foreach (var code in other.Keys)
                Assert.True(en.ContainsKey(code), $"{file} contains {code}, which is missing from En.json.");
        }
    }

    [Fact]
    public void EveryEnglishEntry_HasNameAndMessage()
    {
        var en = Load("En.json");
        Assert.NotEmpty(en);
        foreach (var (code, entry) in en)
        {
            Assert.True(entry.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()),
                $"{code} has no name.");
            Assert.True(entry.TryGetProperty("message", out var message) && !string.IsNullOrWhiteSpace(message.GetString()),
                $"{code} has no message.");
        }
    }

    [Fact]
    public void Resolve_FallsBackToEnglish_ForMissingFrenchKey()
    {
        // Temp catalog: full English, French carrying only SYS-0001.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "errcat-" + Guid.NewGuid().ToString("N")));
        try
        {
            File.Copy(Path.Combine(CatalogDirectory(), "En.json"), Path.Combine(dir.FullName, "En.json"));
            var en = Load("En.json");
            var sys0001 = en[ErrorCodes.Unknown];
            File.WriteAllText(
                Path.Combine(dir.FullName, "Fr.json"),
                "{\n  \"SYS-0001\": { \"code\": \"SYS-0001\", \"name\": \"" + sys0001.GetProperty("name").GetString() +
                "\", \"message\": \"FR-MARKER\" }\n}");

            var svc = new ErrorCatalogSvc(() => "fr", dir.FullName);

            var translated = svc.Resolve(ErrorCodes.Unknown);
            Assert.Equal("FR-MARKER", translated.Message);

            var fellBack = svc.Resolve(ErrorCodes.AccountLocked);
            var expected = en[ErrorCodes.AccountLocked].GetProperty("message").GetString();
            Assert.Equal(expected, fellBack.Message);
            Assert.True(fellBack.IsKnown);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_UnknownCode_FallsBackToSys0001_PreservingRequestedCode()
    {
        var svc = new ErrorCatalogSvc(() => "en", CatalogDirectory());
        var info = svc.Resolve("XX-9999");

        Assert.Equal(ErrorCodes.Unknown, info.Code);
        Assert.False(info.IsKnown);
        Assert.Equal("XX-9999", info.RequestedCode);
        Assert.False(string.IsNullOrWhiteSpace(info.Message));
    }

    [Fact]
    public void Resolve_FormatsPlaceholders()
    {
        var svc = new ErrorCatalogSvc(() => "en", CatalogDirectory());
        var info = svc.Resolve(ErrorCodes.PasswordPolicyViolation, "too short");

        Assert.Contains("too short", info.Message);
        Assert.DoesNotContain("{0}", info.Message);
    }

    [Fact]
    public void Classify_MapsMainExceptionTypes()
    {
        var svc = new ErrorCatalogSvc(() => "en", CatalogDirectory());

        Assert.Equal(ErrorCodes.Unknown, svc.Classify(new InvalidOperationException("x")));
        Assert.Equal(ErrorCodes.FileAccessFailed, svc.Classify(new IOException("x")));
        Assert.Equal(ErrorCodes.FileAccessFailed, svc.Classify(new FileNotFoundException("x")));
        Assert.Equal(ErrorCodes.FileAccessFailed, svc.Classify(new UnauthorizedAccessException("x")));
        Assert.Equal(ErrorCodes.DatabaseConnectionFailed, svc.Classify(new TimeoutException("x")));
        Assert.Equal(ErrorCodes.DatabaseQueryFailed, svc.Classify(new SqliteException("x", 1)));
        Assert.Equal(ErrorCodes.JournalNotBalanced, svc.Classify(new AppError(ErrorCodes.JournalNotBalanced, "x")));
        Assert.Equal(ErrorCodes.FileAccessFailed,
            svc.Classify(new AggregateException(new IOException("inner"))));
        Assert.Equal(ErrorCodes.Unknown, svc.Classify(null));
    }

    [Fact]
    public void AppError_CarriesCodeAndArgs()
    {
        var error = new AppError(ErrorCodes.JournalNotBalanced, "not balanced", 12.5m);

        Assert.Equal(ErrorCodes.JournalNotBalanced, error.Code);
        Assert.Equal("not balanced", error.Message);
        Assert.Single(error.Args);
        Assert.Equal(12.5m, error.Args[0]);

        var blank = new AppError("  ", "no code");
        Assert.Equal(ErrorCodes.Unknown, blank.Code);
    }
}
