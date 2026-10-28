using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ShouTech.App.Common;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Phase 7B - "Generate barcode" (Inventory > Barcode).
/// Automatically assigns a unique EAN-13 barcode to ALL materials loaded
/// from the live materials repository. Materials that already carry a
/// barcode are skipped by default (regenerate mode can overwrite them),
/// and generated barcodes are written back to the material cards.
/// </summary>
public sealed class BcdGenWin : BaseWin
{
    private readonly TextBox _txtPrefix = new() { Width = 90, Text = "628" };
    private readonly Button _btnGenerateMissing = new();
    private readonly Button _btnGenerateAll = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPrint = new();
    private readonly TextBox _txtSearch = new();
    private readonly TextBlock _status = new();
    private readonly Button _btnSelectAll = new();
    private readonly Button _btnSelectNone = new();
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        IsReadOnly = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        SelectionMode = DataGridSelectionMode.Single
    };

    private readonly List<MatRow> _rows = new();
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    public BcdGenWin()
    {
        Width = 860;
        Height = 560;
        MinWidth = 660;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        LoadMaterials();
        ApplyFilter();
    }

    // ------------------------------------------------------------------
    // UI
    // ------------------------------------------------------------------

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // Top toolbar.
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

        toolbar.Children.Add(MakeLabel(L("BarcodeGen.Prefix", "Prefix:")));
        _txtPrefix.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        toolbar.Children.Add(_txtPrefix);

        StyleButton(_btnGenerateMissing, L("BarcodeGen.GenerateMissing", "Generate for materials without barcode"));
        _btnGenerateMissing.Click += (_, _) => Generate(overwrite: false);
        toolbar.Children.Add(_btnGenerateMissing);

        StyleButton(_btnGenerateAll, L("BarcodeGen.RegenerateAll", "Regenerate all"));
        _btnGenerateAll.Click += (_, _) => Generate(overwrite: true);
        toolbar.Children.Add(_btnGenerateAll);

        StyleButton(_btnSave, T("Common.Save"));
        _btnSave.Click += (_, _) => SaveGenerated();
        toolbar.Children.Add(_btnSave);

        StyleButton(_btnPrint, T("Common.Print"));
        _btnPrint.Click += (_, _) => PrintGenerated();
        toolbar.Children.Add(_btnPrint);

        StyleButton(_btnSelectAll, T("Common.SelectAll"));
        _btnSelectAll.Click += (_, _) => SetInclude(true);
        toolbar.Children.Add(_btnSelectAll);

        StyleButton(_btnSelectNone, T("Common.ClearAll"));
        _btnSelectNone.Click += (_, _) => SetInclude(false);
        toolbar.Children.Add(_btnSelectNone);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // Search row.
        var searchPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _txtSearch.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        _txtSearch.TextChanged += (_, _) => ApplyFilter();
        var searchLabel = new TextBlock
        {
            Text = T("Buttons.Search") + ":",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        searchLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchPanel.Children.Add(searchLabel);
        searchPanel.Children.Add(_txtSearch);
        DockPanel.SetDock(searchPanel, Dock.Top);
        root.Children.Add(searchPanel);

        // Grid.
        BuildColumns();
        _grid.SetResourceReference(BackgroundProperty, "SurfaceSecondaryBrush");
        _grid.SetResourceReference(ForegroundProperty, "TextPrimary");
        root.Children.Add(_grid);

        // Status.
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.FontWeight = FontWeights.SemiBold;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        Content = root;
    }

    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = T("Common.SelectAll"),
            Binding = new Binding(nameof(MatRow.Include))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            }
        });
        _grid.Columns.Add(MakeCol(T("MatCard.Code"), nameof(MatRow.Code), 90));
        _grid.Columns.Add(MakeCol(T("MatCard.Name"), nameof(MatRow.Name), 230));
        _grid.Columns.Add(MakeCol(T("MatCard.Barcode"), nameof(MatRow.Barcode), 150));
        _grid.Columns.Add(MakeCol(T("Reports.Col.Status"), nameof(MatRow.Status), 140));
    }

    private static DataGridTextColumn MakeCol(string header, string prop, int width)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(prop) { Mode = BindingMode.OneWay }
        };
        col.IsReadOnly = true;
        if (width > 0)
            col.Width = new DataGridLength(width);
        return col;
    }

    // ------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------

    private async void LoadMaterials()
    {
        SetBusy(true);
        try
        {
            // Load off the UI thread — material tables can hold tens of
            // thousands of rows in production databases.
            var materials = await Task.Run(() => MatRepo.LoadAll());

            _rows.Clear();
            foreach (var material in materials)
            {
                _rows.Add(new MatRow
                {
                    Id = material.Id,
                    Code = material.Code,
                    Name = DisplayName(material),
                    Barcode = material.Unit1Barcode ?? string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            ApplyFilter();
        }
    }

    private void SetInclude(bool include)
    {
        foreach (var row in _rows)
            row.Include = include;
        _grid.Items.Refresh();
    }

    private void SetBusy(bool busy)
    {
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        _btnGenerateMissing.IsEnabled = !busy;
        _btnGenerateAll.IsEnabled = !busy;
        _btnSave.IsEnabled = !busy;
        _btnPrint.IsEnabled = !busy;
        _grid.IsEnabled = !busy;
    }

    private static string DisplayName(Services.MatRepo.MatRec material)
    {
        var isArabic = string.Equals(
            App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase);
        return isArabic || string.IsNullOrWhiteSpace(material.NameEn)
            ? material.Name
            : material.NameEn;
    }

    private void ApplyFilter()
    {
        var q = (_txtSearch.Text ?? string.Empty).Trim();
        var source = string.IsNullOrEmpty(q)
            ? _rows.ToList()
            : _rows.Where(r =>
                    (r.Code ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                 || (r.Name ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                 || (r.Barcode ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _grid.ItemsSource = source;

        var missing = source.Count(r => string.IsNullOrWhiteSpace(r.Barcode));
        _status.Text = FormatLocalized(
            "BarcodeGen.Summary",
            "{0} of {1} material(s) shown. Missing barcode: {2}.",
            source.Count, _rows.Count, missing);
    }

    // ------------------------------------------------------------------
    // Generation
    // ------------------------------------------------------------------

    private void Generate(bool overwrite)
    {
        var prefix = (_txtPrefix.Text ?? string.Empty).Trim();
        if (prefix.Length == 0)
            prefix = "628";

        // A GS1 company prefix must be numeric with room for the item
        // sequence and the EAN-13 checksum digit.
        if (prefix.Length < 2 || prefix.Length > 11 || !prefix.All(char.IsDigit))
        {
            _status.Text = T("Messages.InvalidInput") + ": " + prefix;
            return;
        }

        var used = new HashSet<string>(
            _rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Barcode))
                .Select(r => r.Barcode),
            StringComparer.Ordinal);

        var seq = _rows.Count > 0 ? _rows.Max(r => r.Id) : 0;
        var generated = 0;
        var skipped = 0;

        foreach (var row in _rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Barcode) && !overwrite)
            {
                skipped++;
                continue;
            }

            string candidate;
            do
            {
                seq++;
                candidate = Code128.BuildEan13(prefix, seq);
            }
            while (!used.Add(candidate));

            row.Barcode = candidate;
            row.Status = L("BarcodeGen.Generated", "Generated");
            generated++;
        }

        _status.Text = FormatLocalized(
            "BarcodeGen.Result",
            "Barcodes generated: {0}. Skipped (already coded): {1}. Total materials: {2}. Use Save to write them to the material cards.",
            generated, skipped, _rows.Count);

        _grid.Items.Refresh();
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private async void SaveGenerated()
    {
        var changed = _rows.Where(r => r.Include && !string.IsNullOrWhiteSpace(r.Barcode)).ToList();
        if (changed.Count == 0)
        {
            MessageBox.Show(this, T("Messages.NoData"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        int saved;
        int failed;
        try
        {
            var pending = changed
                .Select(r => (r.Id, Barcode: r.Barcode))
                .ToList();

            // Persist off the UI thread. Each record is loaded fresh with
            // children included so saving the barcode never wipes prices,
            // units or extra barcodes.
            (saved, failed) = await Task.Run(() =>
            {
                var s = 0;
                var f = 0;
                foreach (var (id, barcode) in pending)
                {
                    try
                    {
                        var full = MatRepo.LoadById(id, includeChildren: true);
                        if (full == null)
                        {
                            f++;
                            continue;
                        }

                        full.Unit1Barcode = barcode;
                        MatRepo.Save(full, isNew: false);
                        s++;
                    }
                    catch (Exception ex)
                    {
                        AppHost.Logging?.LogError(
                            "BarcodeGeneration",
                            $"Failed to persist generated barcode | MaterialId={id}",
                            ex);
                        f++;
                    }
                }

                return (s, f);
            });
        }
        finally
        {
            SetBusy(false);
        }

        _status.Text = FormatLocalized(
            "BarcodeGen.SavedSummary",
            "Saved to material cards: {0}. Failed: {1}.",
            saved, failed);

        // Never display a success message when every write failed or some writes failed.
        var outcomeIcon = failed == 0
            ? MessageBoxImage.Information
            : saved > 0
                ? MessageBoxImage.Warning
                : MessageBoxImage.Error;
        MessageBox.Show(
            this,
            _status.Text,
            Title,
            MessageBoxButton.OK,
            outcomeIcon);
    }

    // ------------------------------------------------------------------
    // Printing
    // ------------------------------------------------------------------

    private void PrintGenerated()
    {
        var printable = _rows
            .Where(r => r.Include && !string.IsNullOrWhiteSpace(r.Barcode))
            .ToList();

        if (printable.Count == 0)
        {
            MessageBox.Show(this, T("Messages.NoData"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            // Print a proper label sheet: three columns per page, every
            // label scaled uniformly into its cell, pages filled in order.
            const double margin = 12;
            const int columns = 3;
            const double cellHeight = 116;

            var cellWidth = (dialog.PrintableAreaWidth - margin * 2) / columns;
            var rowsPerPage = Math.Max(1,
                (int)((dialog.PrintableAreaHeight - margin * 2) / cellHeight));
            var perPage = columns * rowsPerPage;

            for (var first = 0; first < printable.Count; first += perPage)
            {
                var page = new Grid { Margin = new Thickness(margin) };
                for (var c = 0; c < columns; c++)
                    page.ColumnDefinitions.Add(new ColumnDefinition());
                for (var r = 0; r < rowsPerPage; r++)
                    page.RowDefinitions.Add(new RowDefinition());

                var slice = printable.Skip(first).Take(perPage).ToList();
                for (var i = 0; i < slice.Count; i++)
                {
                    var cell = new Viewbox
                    {
                        Width = cellWidth,
                        Height = cellHeight - 4,
                        Stretch = Stretch.Uniform,
                        Child = BarcodeLabelPrinter.BuildLabel(slice[i].Name, slice[i].Barcode)
                    };
                    Grid.SetColumn(cell, i % columns);
                    Grid.SetRow(cell, i / columns);
                    page.Children.Add(cell);
                }

                dialog.PrintVisual(page, Title);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Localization helpers
    // ------------------------------------------------------------------

    protected override void ApplyLocalizedText()
    {
        Title = L("Inventory.GenerateBarcode", "Generate barcode");
        _btnGenerateMissing.Content = L("BarcodeGen.GenerateMissing", "Generate for materials without barcode");
        _btnGenerateAll.Content = L("BarcodeGen.RegenerateAll", "Regenerate all");
        _btnSave.Content = T("Common.Save");
        _btnPrint.Content = T("Common.Print");
        _btnSelectAll.Content = T("Common.SelectAll");
        _btnSelectNone.Content = T("Common.ClearAll");
    }

    private static string L(string key, string fallback)
    {
        try
        {
            var s = T(key);
            return string.IsNullOrWhiteSpace(s) || string.Equals(s, key, StringComparison.Ordinal)
                ? fallback
                : s;
        }
        catch
        {
            return fallback;
        }
    }

    private static string FormatLocalized(string key, string fallback, params object?[] args)
    {
        var format = L(key, fallback);
        var compositeFormat = CompositeFormats.GetOrAdd(format, static value => CompositeFormat.Parse(value));
        return string.Format(CultureInfo.CurrentCulture, compositeFormat, args);
    }

    private static TextBlock MakeLabel(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return block;
    }

    private static void StyleButton(Button btn, string text)
    {
        btn.Content = text;
        btn.Margin = new Thickness(10, 0, 0, 0);
        btn.Padding = new Thickness(10, 4, 10, 4);
        btn.Height = 28;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    /// <summary>Row model. Barcode and status are mutated by the generator.</summary>
    public sealed class MatRow
    {
        public bool Include { get; set; } = true;
        public long Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
