using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShouTech.Infrastructure;

/// <summary>
/// Reads provider-specific migration bundles while preserving the original
/// migration IDs and checksums stored by earlier releases.
/// </summary>
public static class MigrationBundleReader
{
    public sealed record Migration(string Id, string Path, string Sql, string Hash);

    private static readonly Regex Marker = new(
        @"^-- @shoutech-migration (?<id>[A-Za-z0-9_.-]+) (?<legacy>[A-Fa-f0-9]{64}) (?<content>[A-Fa-f0-9]{64})[ 	]*\r?$",
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
            var markers = Marker.Matches(content);

            if (markers.Count == 0)
            {
                migrations.Add(new Migration(
                    Path.GetFileName(path),
                    path,
                    content,
                    Convert.ToHexString(SHA256.HashData(bytes))));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(content[..markers[0].Index]))
                throw new InvalidDataException($"Unexpected content before the first migration marker in {path}.");

            for (var index = 0; index < markers.Count; index++)
            {
                var marker = markers[index];
                var start = marker.Index + marker.Length;
                if (start < content.Length && content[start] == '\r')
                    start++;
                if (start < content.Length && content[start] == '\n')
                    start++;

                var end = index + 1 < markers.Count ? markers[index + 1].Index : content.Length;
                if (end < start)
                    throw new InvalidDataException($"Invalid migration segment boundaries in {path}.");

                var sql = content[start..end];
                var expectedContentHash = marker.Groups["content"].Value;
                var actualContentHash = ComputeHash(sql);
                if (!string.Equals(actualContentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Migration bundle checksum mismatch: {marker.Groups["id"].Value} in {path}.");

                migrations.Add(new Migration(
                    marker.Groups["id"].Value,
                    path,
                    sql,
                    marker.Groups["legacy"].Value.ToUpperInvariant()));
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

    public static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
