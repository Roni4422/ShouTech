using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ShouTech.App.Services;

/// <summary>
/// Per-user preference store for window geometry and report column layouts.
///
/// Storage layout (never holds secrets, plain JSON by design):
///   %LocalAppData%/ShouTech/ERP/Prefs/{company}/{user}.prefs.json
///
/// Every key is scoped by company + user + element so that two users on the
/// same machine never overwrite each other's layouts.
/// All failures are swallowed: preferences are a convenience, never a blocker.
/// </summary>
public static class WinPref
{
    private sealed class PrefBag
    {
        public Dictionary<string, GeometryRec> Geometry { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ColumnRec>> ReportColumns { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class GeometryRec
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    /// <summary>Saved column layout entry for a report (visibility + order + width).</summary>
    public sealed class ColumnRec
    {
        public string Key { get; set; } = string.Empty;
        public bool Visible { get; set; } = true;
        public int Order { get; set; }
        public double Width { get; set; }
    }

    private static readonly ConcurrentDictionary<string, PrefBag> Cache = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly object IoLock = new();

    // ------------------------------------------------------------------
    // Window geometry
    // ------------------------------------------------------------------

    /// <summary>Restores the last saved geometry for the window, clamped to the work area.</summary>
    public static void RestoreGeometry(Window window, string scope)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            if (!bag.Geometry.TryGetValue(elementKey, out var rec))
                return;

            var work = SystemParameters.WorkArea;
            var width = Math.Min(Math.Max(rec.Width, window.MinWidth > 0 ? window.MinWidth : 400), work.Width);
            var height = Math.Min(Math.Max(rec.Height, window.MinHeight > 0 ? window.MinHeight : 300), work.Height);
            var left = Math.Min(Math.Max(rec.Left, work.Left), work.Right - width);
            var top = Math.Min(Math.Max(rec.Top, work.Top), work.Bottom - height);

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
            if (rec.Maximized)
                window.WindowState = WindowState.Maximized;
        }
        catch
        {
            // Preference restore is best effort only.
        }
    }

    /// <summary>Saves the current geometry of the window.</summary>
    public static void SaveGeometry(Window window, string scope)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;

            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                return;

            bag.Geometry[elementKey] = new GeometryRec
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = window.WindowState == WindowState.Maximized
            };
            SaveBag(scope, bag);
        }
        catch
        {
            // Preference save is best effort only.
        }
    }

    // ------------------------------------------------------------------
    // Report column layouts
    // ------------------------------------------------------------------

    /// <summary>Returns the saved column layout for a report, or null when absent.</summary>
    public static List<ColumnRec>? LoadReportColumns(string scope)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            return bag.ReportColumns.TryGetValue(elementKey, out var cols)
                ? new List<ColumnRec>(cols)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persists the column layout chosen by the user for a report.</summary>
    public static void SaveReportColumns(string scope, IReadOnlyList<ColumnRec> columns)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            bag.ReportColumns[elementKey] = new List<ColumnRec>(columns);
            SaveBag(scope, bag);
        }
        catch
        {
            // Preference save is best effort only.
        }
    }

    // ------------------------------------------------------------------
    // Storage internals
    // ------------------------------------------------------------------

    /// <summary>
    /// Splits "company/user/ElementName" into the owning bag (company/user)
    /// and the element key inside it.
    /// </summary>
    private static PrefBag LoadBag(string scope, out string elementKey)
    {
        var parts = scope.Split('/');
        elementKey = parts.Length > 0 ? parts[^1] : scope;
        var bagKey = parts.Length >= 2 ? string.Concat(parts[0], "/", parts[1]) : "default";

        return Cache.GetOrAdd(bagKey, key =>
        {
            lock (IoLock)
            {
                var path = BagPath(key);
                if (!File.Exists(path))
                    return new PrefBag();

                try
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<PrefBag>(json, JsonOpts) ?? new PrefBag();
                }
                catch
                {
                    // A corrupted preference file must never crash the application.
                    return new PrefBag();
                }
            }
        });
    }

    private static void SaveBag(string scope, PrefBag bag)
    {
        var parts = scope.Split('/');
        var bagKey = parts.Length >= 2 ? string.Concat(parts[0], "/", parts[1]) : "default";

        lock (IoLock)
        {
            var path = BagPath(bagKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(bag, JsonOpts);
            File.WriteAllText(path, json);
        }
    }

    private static string BagPath(string bagKey)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShouTech", "ERP", "Prefs");

        var safe = string.Concat(bagKey.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        safe = safe.Replace('/', '_');
        return Path.Combine(root, safe + ".prefs.json");
    }
}
