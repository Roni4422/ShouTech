using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Localization;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

// ====================================================================
//  Column definition
// ====================================================================
/// <summary>Column definition for the search dialog.</summary>
public sealed class SerWinColumn
{
    public string Key    { get; init; } = "";
    public string Header { get; init; } = "";
    public double Width  { get; init; } = 100;
    public bool Fill     { get; init; }
    public HorizontalAlignment Align { get; init; } = HorizontalAlignment.Left;
}

// ====================================================================
//  Row item
// ====================================================================
/// <summary>One row shown in the search dialog.</summary>
public sealed class SerWinItem
{
    public object? Payload { get; init; }
    public long Id { get; init; }
    public string SearchText { get; init; } = "";
    public Dictionary<string, object?> Values { get; init; } = new();
}

// ====================================================================
//  SerWin — Enterprise search dialog
// ====================================================================
/// <summary>
/// Reusable search dialog for the entire application.
///
/// Behaviour:
///   • Searches only within the current company's database
///     (callers pass in items already scoped to that company).
///   • Follows the application language (ar/en/fr/tr) with RTL support.
///   • Persists its size and position in %LocalAppData%\ShouTech\ERP\serwin.json.
///   • Reacts to live language changes (via AppServiceHost.Localization).
/// </summary>
public partial class SerWin : BaseWin
{
    // ---------- Collections ----------
    private readonly ObservableCollection<SerWinItem> _all  = new();
    private readonly ObservableCollection<SerWinItem> _view = new();

    // ---------- Preferences ----------
    private static UsrPref? _cachedPrefs;

    private static UsrPref GetPrefs()
    {
        if (_cachedPrefs != null) return _cachedPrefs;

        try { _cachedPrefs = UsrPref.Load(); }
        catch { _cachedPrefs = new UsrPref(); }

        return _cachedPrefs;
    }

    // ---------- Log ----------
    private static readonly string LogFile =
        Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "log",
            "log.log");

    private static void LogError(string context, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.AppendAllText(LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SerWin] [{context}] " +
                $"{ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n");
        }
        catch { }
    }

    // ---------- Result ----------
    public SerWinItem? SelectedItem { get; private set; }

    // ================================================================
    //  Constructor
    // ================================================================
    public SerWin()
    {
        InitializeComponent();
        VisuId.ApplyWindow(this);

        DgResults.ItemsSource = _view;

        Loaded  += OnLoaded;
        Closing += OnClosing;
    }

    // ================================================================
    //  Lifecycle
    // ================================================================
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyDirectionAndLanguage();
            
            RestoreWindowState();

            // React to language changes while the dialog is open.
            try
            {
                AppServiceHost.Localization.LanguageChanged += OnLanguageChanged;
                Closed += (_, _) =>
                {
                    try { AppServiceHost.Localization.LanguageChanged -= OnLanguageChanged; }
                    catch { }
                };
            }
            catch { }

            TxtSearch.Focus();
        }
        catch (Exception ex) { LogError(nameof(OnLoaded), ex); }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { SaveWindowState(); }
        catch (Exception ex) { LogError(nameof(OnClosing), ex); }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        try { Dispatcher.Invoke(ApplyDirectionAndLanguage); }
        catch { try { ApplyDirectionAndLanguage(); } catch { } }
    }

    // ================================================================
    //  Language / direction
    // ================================================================
    private void ApplyDirectionAndLanguage()
    {
        try
        {
            var lang = App.CurrentLanguageCode ?? "ar";
            bool isRtl = lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase);

            FlowDirection = isRtl
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;

            BtnOk.Content     = Loc.Get("SerWin.Ok",     "OK");
            BtnCancel.Content = Loc.Get("SerWin.Cancel", "Cancel");
        }
        catch (Exception ex) { LogError(nameof(ApplyDirectionAndLanguage), ex); }
    }

    // ================================================================
    //  Visual identity — uses cached, frozen brushes
    // ================================================================
    private static readonly Dictionary<string, SolidColorBrush> _brushes =
        new(StringComparer.OrdinalIgnoreCase);

    private static SolidColorBrush Brush(string hex)
    {
        if (_brushes.TryGetValue(hex, out var cached)) return cached;

        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        _brushes[hex] = b;
        return b;
    }

    

    // ================================================================
    //  Preferences (size + position)
    // ================================================================
    private void RestoreWindowState()
    {
        try
        {
            var saved = SerWinPrefs.Load();

            if (saved.Width is >= 520 and <= 900) Width = saved.Width;
            else Width = 640;

            if (saved.Height is >= 340 and <= 700) Height = saved.Height;
            else Height = 430;

            if (double.IsFinite(saved.Left) &&
                double.IsFinite(saved.Top)  &&
                IsPositionOnScreen(saved.Left, saved.Top, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = saved.Left;
                Top  = saved.Top;
            }
        }
        catch (Exception ex) { LogError(nameof(RestoreWindowState), ex); }
    }

    private void SaveWindowState()
    {
        try
        {
            var state = new SerWinPrefs();

            if (WindowState == WindowState.Normal)
            {
                state.Width  = Width;
                state.Height = Height;
                state.Left   = Left;
                state.Top    = Top;
            }
            else if (RestoreBounds.Width > 0)
            {
                state.Width  = RestoreBounds.Width;
                state.Height = RestoreBounds.Height;
                state.Left   = RestoreBounds.Left;
                state.Top    = RestoreBounds.Top;
            }
            else return;

            state.Save();
        }
        catch (Exception ex) { LogError(nameof(SaveWindowState), ex); }
    }

    private static bool IsPositionOnScreen(double left, double top, double w, double h)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(w)    || !double.IsFinite(h))
            return false;

        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;

        return left >= vl && top >= vt &&
               left + w <= vl + vw &&
               top + h  <= vt + vh;
    }

    // ================================================================
    //  Public API
    // ================================================================
    /// <summary>
    /// Shows the dialog and returns the selected item (or null).
    ///
    /// The items must already be scoped to the current company.
    /// Callers usually populate them from a Repo that filters by
    /// AppState.CurrentCompanyCode.
    /// </summary>
    public static SerWinItem? Pick(
        Window? owner,
        string title,
        IEnumerable<SerWinItem> items,
        IEnumerable<SerWinColumn> columns,
        string initialQuery = "")
    {
        var dlg = new SerWin();

        if (owner != null)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        dlg.Title = title ?? "Search";
        dlg.Configure(items, columns, initialQuery);

        var ok = dlg.ShowDialog();

        return ok == true ? dlg.SelectedItem : null;
    }

    internal static MatRepo.MatRec? PickMaterial(Window? owner, string initialQuery = "")
    {
        var materials = MatRepo.LoadAll();
        var items = materials.Select(material => new SerWinItem
        {
            Payload = material,
            Id = material.Id,
            SearchText = string.Join(
                " ",
                material.Code,
                material.Name,
                material.NameEn,
                material.ShortName,
                material.SearchName,
                material.AltCode,
                material.Gtin,
                material.SupplierCode),
            Values = new Dictionary<string, object?>
            {
                ["Code"] = material.Code,
                ["Name"] = material.Name,
                ["NameEn"] = material.NameEn,
                ["Group"] = material.GroupCode,
                ["Status"] = material.Status
            }
        }).ToList();

        var columns = new[]
        {
            new SerWinColumn
            {
                Key = "Code",
                Header = Loc.Get("Reports.Col.MaterialCode", "Material Code"),
                Width = 120
            },
            new SerWinColumn
            {
                Key = "Name",
                Header = Loc.Get("Reports.Col.MaterialName", "Material Name"),
                Fill = true
            },
            new SerWinColumn
            {
                Key = "NameEn",
                Header = Loc.Get("MatCard.LatinName", "Latin Name"),
                Width = 180
            },
            new SerWinColumn
            {
                Key = "Group",
                Header = Loc.Get("Inventory.Group", "Group"),
                Width = 100
            },
            new SerWinColumn
            {
                Key = "Status",
                Header = Loc.Get("MatCard.StatusLabel", "Status"),
                Width = 90
            }
        };

        return Pick(
            owner,
            Loc.Get("Inventory.SearchMaterial", "Search Material"),
            items,
            columns,
            initialQuery)?.Payload as MatRepo.MatRec;
    }

    public void Configure(
        IEnumerable<SerWinItem> items,
        IEnumerable<SerWinColumn> columns,
        string initialQuery = "")
    {
        try
        {
            _all.Clear();
            if (items != null)
            {
                foreach (var it in items)
                    _all.Add(it);
            }

            BuildColumns(columns ?? Enumerable.Empty<SerWinColumn>());

            TxtSearch.Clear();
            ApplyFilter();
        }
        catch (Exception ex) { LogError(nameof(Configure), ex); }
    }

    // ================================================================
    //  Columns
    // ================================================================
    private void BuildColumns(IEnumerable<SerWinColumn> columns)
    {
        DgResults.Columns.Clear();

        foreach (var col in columns)
        {
            var dgCol = new DataGridTextColumn
            {
                Header = col.Header ?? "",
                Binding = new Binding($"Values[{col.Key}]"),
                IsReadOnly = true,
                Width = col.Fill
                    ? new DataGridLength(1, DataGridLengthUnitType.Star)
                    : new DataGridLength(col.Width)
            };

            if (col.Align != HorizontalAlignment.Left)
            {
                var style = new Style(typeof(DataGridCell));
                style.Setters.Add(new Setter(
                    DataGridCell.HorizontalContentAlignmentProperty, col.Align));
                dgCol.CellStyle = style;
            }

            DgResults.Columns.Add(dgCol);
        }
    }

    // ================================================================
    //  Filtering
    // ================================================================
    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = TxtSearch.Text?.Trim() ?? "";

        _view.Clear();

        IEnumerable<SerWinItem> src = _all;

        if (!string.IsNullOrEmpty(q))
        {
            src = _all.Where(x =>
                !string.IsNullOrEmpty(x.SearchText) &&
                x.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var it in src)
            _view.Add(it);

        if (_view.Count > 0)
            DgResults.SelectedIndex = 0;
        else
            DgResults.SelectedItem = null;
    }

    // ================================================================
    //  Keyboard & mouse
    // ================================================================
    private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                MoveSelection(+1);
                break;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case Key.Enter:
                e.Handled = true;
                Accept();
                break;

            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;

            case Key.F5:
                e.Handled = true;
                DgResults.SelectedIndex = 0;
                break;
        }
    }

    private void DgResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
    }

    private void DgResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        while (source != null && source is not DataGridRow)
            source = VisualTreeHelper.GetParent(source);

        if (source is not DataGridRow) return;

        Accept();
    }

    private void MoveSelection(int delta)
    {
        if (_view.Count == 0) return;

        var idx = DgResults.SelectedIndex;

        if (idx < 0) idx = 0;
        else idx += delta;

        if (idx < 0) idx = _view.Count - 1;
        if (idx >= _view.Count) idx = 0;

        DgResults.SelectedIndex = idx;
        DgResults.ScrollIntoView(_view[idx]);
    }

    // ================================================================
    //  Accept / Cancel
    // ================================================================
    private void BtnOk_Click(object sender, RoutedEventArgs e)     => Accept();
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Accept()
    {
        if (DgResults.SelectedItem is not SerWinItem item) return;

        SelectedItem = item;
        DialogResult = true;
        Close();
    }

    private void Cancel()
    {
        SelectedItem = null;
        DialogResult = false;
        Close();
    }
    protected override void ApplyLocalizedText()
    {
        // Captions via existing localization path.
    }

}

// ====================================================================
//  SerWin preferences — Source-generated JSON (no reflection at runtime)
// ====================================================================
internal sealed class SerWinPrefs
{
    public double Width  { get; set; } = 760;
    public double Height { get; set; } = 540;
    public double Left   { get; set; }
    public double Top    { get; set; }

    // ---------- Paths ----------
    private static readonly string Folder =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShouTech", "ERP");

    private static readonly string FilePath =
        Path.Combine(Folder, "serwin.json");

    // ---------- Cached serializer options (fixes CA1869) ----------
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    // ---------- Load ----------
    public static SerWinPrefs Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new SerWinPrefs();

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return new SerWinPrefs();

            return JsonSerializer.Deserialize<SerWinPrefs>(json) ?? new SerWinPrefs();
        }
        catch
        {
            return new SerWinPrefs();
        }
    }

    // ---------- Save (atomic) ----------
    public void Save()
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(Folder);

            var json = JsonSerializer.Serialize(this, Options);

            tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
            tmp = null;
        }
        catch
        {
            // Never allow preference saving to crash the application.
        }
        finally
        {
            if (tmp != null)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}