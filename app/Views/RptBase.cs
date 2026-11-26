using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ShouTech.App.Interaction;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Base window for every report in ShouTech ERP.
///
/// Each report is an independent window class (per product decision) that
/// inherits this base and only supplies:
///   - <see cref="BuildDefinition"/>   columns + filters
///   - <see cref="LoadDataAsync"/>     rows for the current filter values
///
/// The base provides, uniformly and without duplicated code:
///   filter bar, column chooser (per-user persisted), zebra striping bound to
///   the central theme, professional Excel/PDF export, encrypted branch export,
///   paginated printing, live re-localization, and full shell context
///   (language, flow direction, theme, company, user).
/// </summary>
public abstract class RptBase : BaseWin
{
    // The whole report family shares one tint: reports are read/print
    // surfaces, and a single recognizable report color keeps them distinct
    // from the entry windows they summarize.
    protected override string? ScheduleTintName => "Silver";

    private static readonly ConcurrentDictionary<string, CompositeFormat> StatusFormats = new(StringComparer.Ordinal);
    private readonly Grid _root = new();
    // Report filter scheme: ONE 3-pair form grid for every report — side
    // labels, star field columns, filters flow row by row so edges align
    // across all reports (Phenix rule; docs/WINDOW_PROPORTIONS.md).
    private readonly Grid _filterBar = CreateFormGrid(3);
    private readonly DataGrid _grid = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _totals = new();
    private readonly Button _refresh = new();
    private readonly Button _columns = new();
    private readonly Button _export = new();
    private readonly Button _print = new();
    private readonly Border _filterHost = new();
    private readonly Border _totalsBar = new();

    private readonly Dictionary<string, Control> _filterControls = new(StringComparer.Ordinal);
    private RptDef? _def;
    private RptData? _data;
    private List<RptCol> _activeColumns = new();

    /// <summary>Builds the static definition (columns + filters) of the report.</summary>
    protected abstract RptDef BuildDefinition();

    /// <summary>Loads report rows for the collected filter values.</summary>
    protected abstract Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct);

    /// <summary>Optional: choices for warehouse filters (id + display text).</summary>
    protected virtual IReadOnlyList<KeyValuePair<string, string>> LoadWarehouseChoices()
        => Array.Empty<KeyValuePair<string, string>>();

    /// <summary>Optional: choices for material-group filters (id + display text).</summary>
    protected virtual IReadOnlyList<KeyValuePair<string, string>> LoadMaterialGroupChoices()
        => Array.Empty<KeyValuePair<string, string>>();

    protected RptBase()
    {
        Width = 1040;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildLayout();
        Loaded += async (_, _) => await RefreshAsync();
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    private void BuildLayout()
    {
        _root.Margin = new Thickness(8);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title and actions
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // filters
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // totals
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // actions (bottom, layout rule)

        Grid.SetRow(_title, 0);
        _title.FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(18);
        _title.FontWeight = FontWeights.SemiBold;
        _title.Margin = new Thickness(0);

        _filterHost.Padding = new Thickness(12, 8, 12, 8);
        _filterHost.CornerRadius = new CornerRadius(6);
        _filterHost.BorderThickness = new Thickness(1);
        _filterHost.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _filterHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _filterHost.Child = _filterBar;
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            
        };
        FlowAlign.End(toolbar);
        _refresh.MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth;
        _refresh.Click += async (_, _) => await RefreshAsync();
        _columns.MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth;
        _columns.Margin = new Thickness(8, 0, 0, 0);
        _columns.Click += OnColumnsClick;
        _export.MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth;
        _export.Margin = new Thickness(8, 0, 0, 0);
        _export.Click += OnExportClick;
        _print.MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth;
        _print.Margin = new Thickness(8, 0, 0, 0);
        _print.Click += OnPrintClick;
        foreach (var button in new[] { _refresh, _columns, _export, _print })
            button.SetResourceReference(StyleProperty, "VisuButtonStyle");

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 0);
        heading.Children.Add(_title);
        toolbar.Children.Add(_refresh);
        toolbar.Children.Add(_columns);
        toolbar.Children.Add(_export);
        toolbar.Children.Add(_print);
        Grid.SetRow(heading, 0);
        _root.Children.Add(heading);

        Grid.SetRow(_filterHost, 1);
        _root.Children.Add(_filterHost);

        ConfigureGrid();
        Grid.SetRow(_grid, 2);
        _root.Children.Add(_grid);

        _totalsBar.Padding = new Thickness(6, 3, 6, 3);
        _totalsBar.Margin = new Thickness(0, 4, 0, 0);
        _totalsBar.CornerRadius = new CornerRadius(6);
        _totalsBar.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        _totalsBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _totalsBar.BorderThickness = new Thickness(1);
        _totalsBar.Visibility = Visibility.Collapsed;
        _totals.FontWeight = FontWeights.Bold;
        _totalsBar.Child = _totals;
        Grid.SetRow(_totalsBar, 3);
        _root.Children.Add(_totalsBar);

        _status.Margin = new Thickness(0, 3, 0, 0);
        _status.FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(10);
        _status.Opacity = 0.75;
        Grid.SetRow(_status, 4);
        _root.Children.Add(_status);

        // Action bar at the BOTTOM of the window (layout rule): the report
        // commands (Refresh / Columns / Export / Print) live under the status
        // line instead of in the title row.
        // Explorer-style bottom command bar: flat, transparent, subtle 1px top divider.
        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Margin = new Thickness(0, 6, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(actionBar, 5);
        _root.Children.Add(actionBar);

        Content = _root;
    }

    private void ConfigureGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserResizeColumns = true;
        _grid.CanUserReorderColumns = true;
        _grid.CanUserSortColumns = true;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        // Excel-like grids (owner spec item 1): numbered row
        // headers on every report grid — one central switch here
        // covers all 71 RptBase-derived report windows.
        GridEngine.ApplyStandard(_grid, "TableSurfaceBrush", "TableAlternateRowBrush");
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.BorderThickness = new Thickness(1);
        _grid.SetResourceReference(DataGrid.BorderBrushProperty, "BorderBrush");
        _grid.EnableRowVirtualization = true;
        _grid.EnableColumnVirtualization = true;
    }

    // ------------------------------------------------------------------
    // Filters
    // ------------------------------------------------------------------

    private void BuildFilters()
    {
        _filterBar.Children.Clear();
        _filterControls.Clear();

        // Filters flow into the shared 3-pair filter grid: index i lands on
        // row i/3, pair i%3, so every report's filters share one aligned
        // scheme. Heights come from the central styles (32) via
        // AddFormField; MinWidth floors use the FieldWidth classes.
        var index = 0;
        foreach (var flt in _def!.Filters)
        {
            Control input = flt.Kind switch
            {
                RptFltKind.DateFrom or RptFltKind.DateTo => new DatePicker
                {
                    MinWidth = ShouTech.App.Services.VisuId.FieldWidthShort
                },
                RptFltKind.Choice => BuildCombo(flt.Choices, localizeItems: true),
                RptFltKind.Warehouse => BuildCombo(LoadWarehouseChoices()),
                RptFltKind.MaterialGroup => BuildCombo(LoadMaterialGroupChoices()),
                RptFltKind.Material => new TextBox { MinWidth = ShouTech.App.Services.VisuId.FieldWidthMedium, ToolTip = T("Inventory.MaterialFilterHint") },
                _ => new TextBox { MinWidth = ShouTech.App.Services.VisuId.FieldWidthShort }
            };

            if (flt.DefaultValue is DateTime dt && input is DatePicker dp)
                dp.SelectedDate = dt;
            else if (flt.DefaultValue is string s && input is TextBox tb)
                tb.Text = s;

            UIElement filterCell = input;
            if (flt.Kind == RptFltKind.Material && input is TextBox materialFilter)
            {
                // The cell content is the picker panel, so style the inner
                // text box explicitly (AddFormField styles the cell itself).
                ApplyFieldStyle(materialFilter);
                materialFilter.TextChanged += (_, _) => materialFilter.Tag = null;
                var lookup = new Button
                {
                    Content = "...",
                    Width = ShouTech.App.Services.VisuId.ControlHeight,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = T("Inventory.SearchMaterial")
                };
                lookup.SetResourceReference(StyleProperty, "VisuButtonStyle");
                lookup.Click += (_, _) =>
                {
                    var material = SerWin.PickMaterial(this, materialFilter.Text);
                    if (material == null)
                        return;

                    var name = material.Name;
                    materialFilter.Text = $"{material.Code} - {name}";
                    materialFilter.Tag = material.Code;
                };
                var picker = new DockPanel();
                DockPanel.SetDock(lookup, Dock.Right);
                picker.Children.Add(lookup);
                picker.Children.Add(materialFilter);
                filterCell = picker;
            }

            AddFormField(_filterBar, index / 3, index % 3, flt.LabelTextKey, filterCell);
            _filterControls[flt.Key] = input;
            index++;
        }
    }

    private static ComboBox BuildCombo(
        IReadOnlyList<KeyValuePair<string, string>>? choices,
        bool localizeItems = false)
    {
        var combo = new ComboBox { MinWidth = ShouTech.App.Services.VisuId.FieldWidthMedium };
        if (choices != null)
        {
            foreach (var (value, display) in choices)
                combo.Items.Add(new ComboBoxItem
                {
                    Content = localizeItems ? T(display) : display,
                    Tag = value
                });
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        return combo;
    }

    private RptFltValues CollectFilters()
    {
        var values = new RptFltValues();
        foreach (var (key, control) in _filterControls)
        {
            switch (control)
            {
                case DatePicker dp:
                    values.Set(key, dp.SelectedDate);
                    break;
                case ComboBox cb:
                    values.Set(key, (cb.SelectedItem as ComboBoxItem)?.Tag as string);
                    break;
                case TextBox tb:
                    values.Set(
                        key,
                        tb.Tag as string ??
                        (string.IsNullOrWhiteSpace(tb.Text) ? null : tb.Text.Trim()));
                    break;
            }
        }
        return values;
    }

    // ------------------------------------------------------------------
    // Data + columns
    // ------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        _refresh.IsEnabled = false;
        try
        {
            // Definition/filter construction lives inside the try since
            // Step 6: filter choice loaders query the database
            // (RptQuery.WarehouseChoices), and a query failure there must
            // surface through this same path now that RptQuery throws
            // cataloged errors instead of returning empty sets (RC-5).
            _def ??= BuildDefinition();
            if (_filterControls.Count == 0 && _def.Filters.Count > 0)
                BuildFilters();

            _data = await LoadDataAsync(CollectFilters(), CancellationToken.None);
            ApplyColumnLayout();
            BindGrid();
            RenderTotals();
        }
        catch (Exception exL366)
        {
            // A cataloged AppError was already reported centrally by its
            // source (e.g. RptQuery reports DB-1005); report only the
            // failures that arrive unreported, so one failure = one log.
            if (exL366 is not ShouTech.Domain.AppError)
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL366, "RptBase.RefreshAsync");
            ShowError("Report.LoadFailed");
        }
        finally
        {
            _refresh.IsEnabled = true;
        }
    }

    /// <summary>Applies the saved per-user column layout (visibility, order, width).</summary>
    private void ApplyColumnLayout()
    {
        var saved = WinPref.LoadReportColumns(ColumnScope());
        var byKey = _def!.Columns.ToDictionary(c => c.Key, StringComparer.Ordinal);

        if (saved != null)
        {
            _activeColumns = saved
                .OrderBy(s => s.Order)
                .Where(s => byKey.ContainsKey(s.Key))
                .Select(s =>
                {
                    var col = byKey[s.Key];
                    col.Width = s.Width > 0 ? s.Width : col.Width;
                    return new KeyValuePair<RptCol, bool>(col, s.Visible || !col.AllowHide);
                })
                .Where(p => p.Value)
                .Select(p => p.Key)
                .Concat(byKey.Values.Where(c => saved.All(s => s.Key != c.Key)))
                .ToList();
        }
        else
        {
            _activeColumns = _def.Columns.Where(c => c.VisibleByDefault || !c.AllowHide).ToList();
        }

        // Visibility flags for the chooser dialog.
        _visibility = _def.Columns.ToDictionary(
            c => c.Key,
            c => _activeColumns.Any(a => a.Key == c.Key),
            StringComparer.Ordinal);
        _orderedAll = _activeColumns
            .Concat(_def.Columns.Where(c => !_activeColumns.Any(a => a.Key == c.Key)))
            .ToList();
    }

    private Dictionary<string, bool> _visibility = new(StringComparer.Ordinal);
    private List<RptCol> _orderedAll = new();

    private void BindGrid()
    {
        _grid.Columns.Clear();
        foreach (var col in _orderedAll.Where(c => _visibility.GetValueOrDefault(c.Key)))
        {
            var textCol = new DataGridTextColumn
            {
                Header = T(col.HeaderTextKey),
                Binding = new Binding($"[{col.Key}]"),
                Width = col.Width
            };

            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,
                RptRend.IsNumeric(col.Type) ? TextAlignment.Right
                : col.Type is RptColType.Date or RptColType.DateTime ? TextAlignment.Center
                : TextAlignment.Left));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(1, 0, 1, 0)));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            textCol.ElementStyle = style;

            if (RptRend.IsNumeric(col.Type) || col.Type is RptColType.Date or RptColType.DateTime)
            {
                textCol.Binding.StringFormat = col.Type switch
                {
                    RptColType.Money or RptColType.FractionalNumber => col.Format ?? "N2",
                    RptColType.WholeNumber => col.Format ?? "N0",
                    RptColType.Percent => col.Format ?? "P1",
                    RptColType.Date => "yyyy-MM-dd",
                    RptColType.DateTime => "yyyy-MM-dd HH:mm",
                    _ => col.Format
                };
            }

            _grid.Columns.Add(textCol);
        }

        _grid.ItemsSource = _data?.Rows;
        ApplySavedSort();
        UpdateStatus();
    }

    private bool _sortRestoreReported;

    /// <summary>Restores the user's saved grid sort (WinPref grid
    /// state). A row shape that cannot be view-sorted falls back to
    /// the natural order — reported once, never blocking.</summary>
    private void ApplySavedSort()
    {
        var state = WinPref.LoadGridState(ColumnScope());
        if (state?.SortKey is not { Length: > 0 } key)
            return;
        try
        {
            var direction = state.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
            _grid.Items.SortDescriptions.Clear();
            _grid.Items.SortDescriptions.Add(new SortDescription(key, direction));
            foreach (var col in _grid.Columns.OfType<DataGridTextColumn>())
            {
                if ((col.Binding as Binding)?.Path.Path == $"[{key}]")
                    col.SortDirection = direction;
            }
        }
        catch (Exception exSort)
        {
            if (!_sortRestoreReported)
            {
                _sortRestoreReported = true;
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exSort, "RptBase.ApplySavedSort");
            }
        }
    }

    /// <summary>Captures the grid's current sort into the per-user
    /// store when the window closes (BaseWin state hook).</summary>
    protected override void OnSaveWindowState()
    {
        try
        {
            var sd = _grid.Items.SortDescriptions.FirstOrDefault();
            WinPref.SaveGridState(ColumnScope(), new WinPref.GridStateRec
            {
                SortKey = sd.PropertyName,
                Ascending = sd.Direction != ListSortDirection.Descending
            });
        }
        catch (Exception exSort)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exSort, "RptBase.OnSaveWindowState");
        }
    }

    /// <summary>
    /// Al-Ameen schedule pattern: a totals footer pinned at the bottom of the
    /// grid. Reports may opt out when their rows are a structured statement
    /// (sections/subtotals) where a flat column sum would double-count.
    /// </summary>
    protected virtual bool AllowComputedTotals => true;

    private void RenderTotals()
    {
        if (_data == null)
        {
            _totalsBar.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (_data.Totals != null && _data.Totals.Count > 0)
        {
            foreach (var col in _orderedAll)
            {
                if (!_visibility.GetValueOrDefault(col.Key))
                    continue;
                if (_data.Totals.TryGetValue(col.Key, out var v) && v != null)
                    parts.Add($"{T(col.HeaderTextKey)}: {RptRend.FormatCell(v, col)}");
            }
        }
        else if (AllowComputedTotals && _data.Rows.Count > 0)
        {
            // No totals supplied by the report: compute them from the bound
            // rows for numeric columns only. Non-numeric or heterogeneous
            // values are skipped cell-by-cell, never guessed.
            foreach (var col in _orderedAll)
            {
                if (!_visibility.GetValueOrDefault(col.Key) || !RptRend.IsNumeric(col.Type))
                    continue;
                decimal sum = 0;
                var any = false;
                foreach (var row in _data.Rows)
                {
                    if (row.TryGetValue(col.Key, out var v) && v != null &&
                        decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    {
                        sum += d;
                        any = true;
                    }
                }
                if (any)
                    parts.Add($"{T(col.HeaderTextKey)}: {RptRend.FormatCell(sum, col)}");
            }
            if (parts.Count > 0)
                parts.Insert(0, T("Reports.Totals"));
        }

        _totals.Text = string.Join("    |    ", parts);
        _totalsBar.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus()
    {
        var count = _data?.Rows.Count ?? 0;
        var format = StatusFormats.GetOrAdd(T("Report.StatusLine"), CompositeFormat.Parse);
        _status.Text = string.Format(
            CultureInfo.CurrentCulture,
            format,
            count, CurrentCompanyDisplay, CurrentUserDisplay);
    }

    // ------------------------------------------------------------------
    // Column chooser
    // ------------------------------------------------------------------

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        var items = _orderedAll.Select(c => new ColChsWin.Item
        {
            Key = c.Key,
            Header = T(c.HeaderTextKey),
            Visible = _visibility.GetValueOrDefault(c.Key),
            Locked = !c.AllowHide
        });

        var dialog = new ColChsWin(items) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _orderedAll = dialog.Result
            .Select(i => _def!.Columns.First(c => c.Key == i.Key))
            .ToList();
        _visibility = dialog.Result.ToDictionary(i => i.Key, i => i.Visible, StringComparer.Ordinal);

        var recs = dialog.Result.Select((i, idx) =>
        {
            var col = _def!.Columns.First(c => c.Key == i.Key);
            return new WinPref.ColumnRec { Key = i.Key, Visible = i.Visible, Order = idx, Width = col.Width };
        }).ToList();
        WinPref.SaveReportColumns(ColumnScope(), recs);

        BindGrid();
        RenderTotals();
    }

    private string ColumnScope()
    {
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        var company = AppState.CurrentCompanyCode ?? "no-company";
        return $"{company}/{user}/Rpt:{_def!.ReportKey}";
    }

    // ------------------------------------------------------------------
    // Export / print
    // ------------------------------------------------------------------

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_data == null || _data.Rows.Count == 0)
        {
            ShowInfo("Report.NoData");
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Export.Excel", (_, _) => XptServ.ExportExcel(this, BuildExportModel())));
        menu.Items.Add(MenuItem("Export.Pdf", (_, _) => XptServ.ExportPdf(this, BuildExportModel())));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Export.DataEncrypted", (_, _) => XptServ.ExportEncrypted(this, BuildExportModel())));
        menu.PlacementTarget = _export;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string key, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = T(key) };
        item.Click += handler;
        return item;
    }

    private XptServ.ExportModel BuildExportModel()
    {
        var visibleCols = _orderedAll
            .Where(c => _visibility.GetValueOrDefault(c.Key))
            .ToList();

        return new XptServ.ExportModel
        {
            ReportKey = _def!.ReportKey,
            Title = T(_def.TitleTextKey),
            CompanyName = CurrentCompanyDisplay,
            GeneratedBy = CurrentUserDisplay,
            FilterSummary = FilterSummary(),
            Columns = visibleCols,
            Rows = _data!.Rows,
            Totals = _data.Totals,
            Rtl = FlowDirection == FlowDirection.RightToLeft,
            Translate = T
        };
    }

    private string FilterSummary()
    {
        if (_def == null)
            return string.Empty;

        var parts = new List<string>();
        foreach (var flt in _def.Filters)
        {
            if (!_filterControls.TryGetValue(flt.Key, out var control))
                continue;

            string? value = control switch
            {
                DatePicker dp when dp.SelectedDate.HasValue => dp.SelectedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ComboBox cb => (cb.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                TextBox tb when !string.IsNullOrWhiteSpace(tb.Text) => tb.Text.Trim(),
                _ => null
            };
            if (!string.IsNullOrEmpty(value))
                parts.Add($"{T(flt.LabelTextKey)}: {value}");
        }
        return string.Join("   ·   ", parts);
    }

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        if (_data == null || _data.Rows.Count == 0)
        {
            ShowInfo("Report.NoData");
            return;
        }
        XptServ.Print(this, BuildExportModel());
    }

    // ------------------------------------------------------------------
    // Localization
    // ------------------------------------------------------------------

    protected override void ApplyLocalizedText()
    {
        _def ??= BuildDefinition();
        Title = $"{T(_def.TitleTextKey)} - {CurrentCompanyDisplay}";
        _title.Text = T(_def.TitleTextKey);
        _refresh.Content = T("Common.Refresh");
        _columns.Content = T("Common.Columns");
        _export.Content = T("Common.Export");
        _print.Content = T("Common.Print");

        if (_filterControls.Count > 0)
        {
            var filterValues = _filterControls.ToDictionary(
                entry => entry.Key,
                entry => CaptureFilterValue(entry.Value),
                StringComparer.Ordinal);
            BuildFilters();
            foreach (var (key, value) in filterValues)
            {
                if (_filterControls.TryGetValue(key, out var control))
                    RestoreFilterValue(control, value);
            }
        }
        if (_data != null)
        {
            BindGrid();
            RenderTotals();
        }
        UpdateStatus();
    }

    private static object? CaptureFilterValue(Control control)
        => control switch
        {
            DatePicker picker => picker.SelectedDate,
            ComboBox combo => (combo.SelectedItem as ComboBoxItem)?.Tag as string,
            TextBox text => (text.Text, text.Tag),
            _ => null
        };

    private static void RestoreFilterValue(Control control, object? value)
    {
        switch (control, value)
        {
            case (DatePicker picker, DateTime date):
                picker.SelectedDate = date;
                break;
            case (DatePicker picker, null):
                picker.SelectedDate = null;
                break;
            case (ComboBox combo, string selected):
                combo.SelectedItem = combo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, selected, StringComparison.Ordinal));
                break;
            case (TextBox text, ValueTuple<string, object?> state):
                text.Text = state.Item1;
                text.Tag = state.Item2;
                break;
        }
    }

    protected override void OnLanguageChanged()
    {
        _grid.AlternatingRowBackground = RptRend.ZebraBrush();
    }
}
