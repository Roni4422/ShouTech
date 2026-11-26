using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

using ShouTech.Domain;
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
    /// <summary>On-disk schema version. A file written by a NEWER
    /// version is never trusted: the reader falls back to defaults
    /// for every window rather than guessing at a future shape.</summary>
    public const int CurrentVersion = 1;

    private sealed class PrefBag
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, GeometryRec> Geometry { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ColumnRec>> ReportColumns { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, GridStateRec> GridStates { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>Saved grid sort state (report grids): the column key
    /// the user sorted by and its direction.</summary>
    public sealed class GridStateRec
    {
        public string? SortKey { get; set; }
        public bool Ascending { get; set; } = true;
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

    /// <summary>
    /// Restores the last saved geometry for the window. Returns true
    /// when a saved record was applied (the caller then skips the
    /// first-open size cap: a saved user geometry WINS over the cap —
    /// owner rule 2026-11-24). Validation follows the platform
    /// guidance: the saved rectangle is kept when a usable part of
    /// it intersects the VIRTUAL screen (negative coordinates are
    /// legal on multi-monitor setups and are validated by
    /// intersection, never by sign); when too little of it is
    /// visible (display removed, resolution changed), it falls back
    /// to a clamp inside the primary work area. A saved Minimized
    /// state is never restored — SaveGeometry records only
    /// Normal/Maximized, so a window minimized at close reopens
    /// with its RestoreBounds as a normal window.
    /// </summary>
    public static bool RestoreGeometry(Window window, string scope)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            if (!bag.Geometry.TryGetValue(elementKey, out var rec))
            {
                // Company/admin defaults sit UNDER user overrides.
                var fallback = LoadDefaultsBag(scope);
                if (fallback is null || !fallback.Geometry.TryGetValue(elementKey, out rec))
                    return false;
            }

            var work = SystemParameters.WorkArea;
            var width = Math.Min(Math.Max(rec.Width, window.MinWidth > 0 ? window.MinWidth : 400), work.Width);
            var height = Math.Min(Math.Max(rec.Height, window.MinHeight > 0 ? window.MinHeight : 300), work.Height);
            var saved = new Rect(rec.Left, rec.Top, width, height);

            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(saved, virtualScreen);
            double left, top;
            if (!visible.IsEmpty && visible.Width >= 100 && visible.Height >= 40)
            {
                // Enough of the window is reachable on a current
                // display: keep its place, clamped inside the
                // virtual screen.
                left = Math.Min(Math.Max(saved.Left, virtualScreen.Left), virtualScreen.Right - width);
                top = Math.Min(Math.Max(saved.Top, virtualScreen.Top), virtualScreen.Bottom - height);
            }
            else
            {
                left = Math.Min(Math.Max(rec.Left, work.Left), work.Right - width);
                top = Math.Min(Math.Max(rec.Top, work.Top), work.Bottom - height);
            }

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
            if (rec.Maximized)
                window.WindowState = WindowState.Maximized;
            return true;
        }
        catch (Exception exL77)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsLoadFailed, exL77, "WinPref.RestoreGeometry");
            // Preference restore is best effort only.
            return false;
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
        catch (Exception exL106)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsSaveFailed, exL106, "WinPref.SaveGeometry");
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
            if (bag.ReportColumns.TryGetValue(elementKey, out var cols))
                return new List<ColumnRec>(cols);
            var fallback = LoadDefaultsBag(scope);
            return fallback is not null && fallback.ReportColumns.TryGetValue(elementKey, out var defaults)
                ? new List<ColumnRec>(defaults)
                : null;
        }
        catch (Exception exL126)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsLoadFailed, exL126, "WinPref.LoadReportColumns");
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
        catch (Exception exL141)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsSaveFailed, exL141, "WinPref.SaveReportColumns");
            // Preference save is best effort only.
        }
    }

    // ------------------------------------------------------------------
    // Named values + grid states (panes, tabs, last-used values, sort)
    // ------------------------------------------------------------------

    /// <summary>Saves one named value under the element scope
    /// (splitter/pane widths, selected tab, expanded state,
    /// last-used dialog values — the F&O personalization set).</summary>
    public static void SaveValue(string scope, string key, string value)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            bag.Values[elementKey + "/" + key] = value;
            SaveBag(scope, bag);
        }
        catch (Exception exVal)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsSaveFailed, exVal, "WinPref.SaveValue");
        }
    }

    public static string? LoadValue(string scope, string key)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            if (bag.Values.TryGetValue(elementKey + "/" + key, out var value))
                return value;
            var fallback = LoadDefaultsBag(scope);
            return fallback is not null && fallback.Values.TryGetValue(elementKey + "/" + key, out var def)
                ? def
                : null;
        }
        catch (Exception exVal)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsLoadFailed, exVal, "WinPref.LoadValue");
            return null;
        }
    }

    public static void SaveGridState(string scope, GridStateRec state)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            bag.GridStates[elementKey] = state;
            SaveBag(scope, bag);
        }
        catch (Exception exGrid)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsSaveFailed, exGrid, "WinPref.SaveGridState");
        }
    }

    public static GridStateRec? LoadGridState(string scope)
    {
        try
        {
            var bag = LoadBag(scope, out var elementKey);
            if (bag.GridStates.TryGetValue(elementKey, out var state))
                return state;
            var fallback = LoadDefaultsBag(scope);
            return fallback is not null && fallback.GridStates.TryGetValue(elementKey, out var def)
                ? def
                : null;
        }
        catch (Exception exGrid)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsLoadFailed, exGrid, "WinPref.LoadGridState");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Reset (OptWin): clears USER-scoped state only; company/admin
    // defaults and other users are untouched. Windows already open
    // re-save their own state when they close, as usual.
    // ------------------------------------------------------------------

    public enum ResetKind { Geometry, Layouts, All }

    public static void ResetUserState(string company, string user, ResetKind kind)
    {
        try
        {
            var scope = company + "/" + user + "/_reset";
            var bag = LoadBag(scope, out _);
            if (kind is ResetKind.Geometry or ResetKind.All)
                bag.Geometry.Clear();
            if (kind is ResetKind.Layouts or ResetKind.All)
            {
                bag.ReportColumns.Clear();
                bag.GridStates.Clear();
            }
            if (kind is ResetKind.All)
                bag.Values.Clear();
            SaveBag(scope, bag);
        }
        catch (Exception exReset)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsSaveFailed, exReset, "WinPref.ResetUserState");
        }
    }

    /// <summary>The company-defaults bag ("{company}/defaults"):
    /// administrator-provided starting layouts that sit UNDER the
    /// user's own overrides. Read-only from the app today.</summary>
    private static PrefBag? LoadDefaultsBag(string scope)
    {
        var parts = scope.Split('/');
        if (parts.Length < 2)
            return null;
        var company = parts[0];
        var elementKey = parts[^1];
        var bag = LoadBag(company + "/defaults/" + elementKey, out _);
        return bag;
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
                    var bag = JsonSerializer.Deserialize<PrefBag>(json, JsonOpts) ?? new PrefBag();
                    // A file from a NEWER schema is never trusted:
                    // fall back to defaults for every window in it
                    // instead of guessing at a future shape. A file
                    // with no version (pre-versioning) is legacy v0
                    // and is accepted; it is upgraded on next save.
                    return bag.Version > CurrentVersion ? new PrefBag() : bag;
                }
                catch (Exception exL174)
                {
                    AppHost.Errors.Report(ErrorCodes.SettingsLoadFailed, exL174, "WinPref.LoadBag");
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
            bag.Version = CurrentVersion;
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
