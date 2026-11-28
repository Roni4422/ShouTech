using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShouTech.Infrastructure;

/// <summary>
/// Reads provider-specific migration bundles while preserving migration IDs
/// and checksums produced by both LF and CRLF working trees.
/// </summary>
public static class MigrationBundleReader
{
    public sealed record Migration(
        string Id,
        string Path,
        string Sql,
        string Hash,
        string AlternateHash,
        string RawHash)
    {
        public bool MatchesHash(string candidate)
        {
            var value = candidate.Trim();
            return string.Equals(Hash, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(AlternateHash, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(RawHash, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly Regex BundleMarker = new(
        @"^-- @shoutech-migration (?<id>[A-Za-z0-9_.-]+) (?<windows>[A-Fa-f0-9]{64}) (?<linux>[A-Fa-f0-9]{64}) (?<raw>[A-Fa-f0-9]{64}) (?<content>[A-Fa-f0-9]{64})[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LegacyBundleMarker = new(
        @"^-- @shoutech-migration (?<id>[A-Za-z0-9_.-]+) (?<legacy>[A-Fa-f0-9]{64}) (?<content>[A-Fa-f0-9]{64})[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<Migration> Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Migration directory not found: {directory}");

        var migrations = new List<Migration>();
        var files = Directory.GetFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var bytes = File.ReadAllBytes(path);
            var content = File.ReadAllText(path, Encoding.UTF8);
            var markers = BundleMarker.Matches(content);
            var legacyMarkers = LegacyBundleMarker.Matches(content);

            if (markers.Count > 0 && legacyMarkers.Count > 0)
                throw new InvalidDataException($"Mixed migration bundle formats in {path}.");

            if (markers.Count == 0 && legacyMarkers.Count == 0)
            {
                if (content.Contains("-- @shoutech-migration", StringComparison.Ordinal))
                    throw new InvalidDataException($"Malformed migration marker in {path}.");

                var hashes = ComputeLegacyHashes(bytes, content);
                migrations.Add(new Migration(
                    Path.GetFileName(path), path, content,
                    hashes.Windows, hashes.Linux, hashes.Raw));
                continue;
            }

            var isCurrentFormat = markers.Count > 0;
            var matches = isCurrentFormat ? markers : legacyMarkers;
            if (!string.IsNullOrWhiteSpace(content[..matches[0].Index]))
                throw new InvalidDataException($"Unexpected content before the first migration marker in {path}.");

            for (var index = 0; index < matches.Count; index++)
            {
                var marker = matches[index];
                var start = marker.Index + marker.Length;
                if (start < content.Length && content[start] == '\r')
                    start++;
                if (start < content.Length && content[start] == '\n')
                    start++;

                var end = index + 1 < matches.Count ? matches[index + 1].Index : content.Length;
                if (end < start)
                    throw new InvalidDataException($"Invalid migration segment boundaries in {path}.");

                var sql = content[start..end];
                var expectedContentHash = marker.Groups["content"].Value;
                var actualContentHash = ComputeHash(sql);
                if (!string.Equals(actualContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Migration bundle checksum mismatch: {marker.Groups["id"].Value} in {path}.");

                if (isCurrentFormat)
                {
                    migrations.Add(new Migration(
                        marker.Groups["id"].Value,
                        path,
                        sql,
                        marker.Groups["windows"].Value.ToUpperInvariant(),
                        marker.Groups["linux"].Value.ToUpperInvariant(),
                        marker.Groups["raw"].Value.ToUpperInvariant()));
                }
                else
                {
                    var legacyHash = marker.Groups["legacy"].Value.ToUpperInvariant();
                    migrations.Add(new Migration(
                        marker.Groups["id"].Value, path, sql,
                        legacyHash, legacyHash, legacyHash));
                }
            }
        }

        var duplicates = migrations
            .GroupBy(migration => migration.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicates.Length > 0)
            throw new InvalidDataException(
                "Duplicate migration IDs were discovered (IDs are case-insensitive): " +
                string.Join(", ", duplicates));

        return migrations
            .OrderBy(migration => migration.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static Migration FindHistory(IReadOnlyList<Migration> migrations)
    {
        var history = migrations.FirstOrDefault(migration =>
            string.Equals(migration.Id, "000_history.sql", StringComparison.OrdinalIgnoreCase));
        return history ?? throw new FileNotFoundException(
            "Required DBA migration-history bootstrap is missing.");
    }

    public static string ComputeHash(string content)
    {
        var normalized = NormalizeLineEndings(content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static (string Windows, string Linux, string Raw) ComputeLegacyHashes(
        byte[] rawBytes, string content)
    {
        var hasUtf8Bom = rawBytes.Length >= 3
            && rawBytes[0] == 0xEF
            && rawBytes[1] == 0xBB
            && rawBytes[2] == 0xBF;
        var normalizedLf = NormalizeLineEndings(content);
        var linuxBytes = Encoding.UTF8.GetBytes(normalizedLf);
        var windowsBytes = Encoding.UTF8.GetBytes(normalizedLf.Replace("\n", "\r\n", StringComparison.Ordinal));

        if (hasUtf8Bom)
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            linuxBytes = bom.Concat(linuxBytes).ToArray();
            windowsBytes = bom.Concat(windowsBytes).ToArray();
        }

        return (
            Convert.ToHexString(SHA256.HashData(windowsBytes)),
            Convert.ToHexString(SHA256.HashData(linuxBytes)),
            Convert.ToHexString(SHA256.HashData(rawBytes)));
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
}
