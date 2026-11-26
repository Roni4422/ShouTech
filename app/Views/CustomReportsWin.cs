using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Npgsql;
using ShouTech.App.Reporting;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// Custom Reports window over the existing ReportRepo / ReportDefinition
/// store (CustomReportDefinitions). Findings from inspection: ReportRepo
/// (ListReportsAsync / LoadReportAsync / SaveReportAsync) and
/// ReportDesignerVM exist, and ReportGridVw is a UserControl host for the
/// VM — but NO window hosts the designer and NO generic data-source runner
/// exists. This window therefore: lists definitions per base report key
/// (keys discovered from the store itself), runs a definition by executing
/// its DataSourceKey against the RptQuery sources that actually exist
/// (feeding the rows through ReportDesignerVM so formula columns compute),
/// and provides a minimal real designer (name, key, data source,
/// visibility, columns seeded from the source) saved via
/// ReportRepo.SaveReportAsync. A definition whose data source is not an
/// RptQuery source reports that honestly instead of rendering fake rows.
/// </summary>
public sealed class CustomReportsWin : BaseWin
{
    protected override string? ScheduleTintName => "Violet";
    private static readonly string[] DataSources =
    {
        "TrialBalance", "BalanceSheet", "IncomeStatement", "CustomerBalances",
        "SupplierBalances", "SalesInvoices", "PurchaseInvoices", "StockBalances"
    };

    // CA1863: cache CompositeFormat instances for repeatedly used format strings.
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    private readonly List<ReportDefinition> _definitions = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly ComboBox _reportKey = new();
    private readonly DataGrid _listGrid = new();
    private readonly TextBox _name = new();
    private readonly TextBox _key = new();
    private readonly ComboBox _dataSource = new();
    private readonly ComboBox _visibility = new();
    private readonly TextBox _columnsInfo = new();
    private readonly ComboBox _pageSize = new();
    private readonly CheckBox _landscape = new();
    private readonly TextBox _marginMm = new();
    private readonly TextBox _titleSize = new();
    private readonly TextBox _headerBack = new();
    private readonly TextBox _headerText = new();
    private readonly TextBox _banding = new();
    private readonly TextBox _bodySize = new();
    private readonly CheckBox _gridLines = new();
    private readonly CheckBox _companyAddress = new();
    private readonly TextBox _totalsFormulas = new();
    private readonly DataGrid _runGrid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _runButton = new();
    private readonly Button _newButton = new();
    private readonly Button _editButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _printButton = new();
    private readonly Button _pdfButton = new();
    private readonly Button _excelButton = new();
    private readonly Button _exportDesignButton = new();
    private readonly Button _importDesignButton = new();
    private readonly Button _closeButton = new();

    private ReportDefinition? _editing;
    private ReportDefinition? _lastRunDef;
    private IReadOnlyList<ReportRow>? _lastRunRows;
    private Dictionary<string, object?>? _lastRunTotals;

    public CustomReportsWin()
    {
        Width = 1180;
        Height = 720;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Formats a localized pattern using a cached CompositeFormat
    /// to avoid re-parsing the format string on every call (CA1863),
    /// with the current UI culture applied explicitly (CA1305).
    /// </summary>
    private static string FormatLocalized(string pattern, params object?[] args)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        var compositeFormat = CompositeFormats.GetOrAdd(
            pattern,
            static value => CompositeFormat.Parse(value));

        return string.Format(CultureInfo.CurrentCulture, compositeFormat, args);
    }

    private static int UserId => int.TryParse(AppState.CurrentUser?.Id, out var id) ? id : 0;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Reporting.CustomView))
        {
            MessageBox.Show(this, T("CustomReports.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _dataSource.ItemsSource = DataSources;
        _dataSource.SelectedIndex = 0;
        _visibility.ItemsSource = Enum.GetNames<ReportVisibility>();
        _visibility.SelectedItem = ReportVisibility.Private.ToString();
        _pageSize.ItemsSource = new[] { "A4", "A5", "Letter" };
        _pageSize.SelectedItem = "A4";
        LoadStyleIntoForm(new ReportDefinition());
        await LoadKeysAsync();
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_runButton, "CustomReports.Run");
        F2Ui.StyleButton(_newButton, "Common.New");
        F2Ui.StyleButton(_editButton, "CustomReports.Edit");
        F2Ui.StyleButton(_saveButton, "Common.Save");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_printButton, "Common.Print");
        F2Ui.StyleButton(_pdfButton, "Export.Pdf");
        F2Ui.StyleButton(_excelButton, "Export.Excel");
        F2Ui.StyleButton(_exportDesignButton, "CustomReports.ExportDesign");
        F2Ui.StyleButton(_importDesignButton, "CustomReports.ImportDesign");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _runButton.Click += async (_, _) => await RunAsync();
        _newButton.Click += (_, _) => NewDefinition();
        _editButton.Click += async (_, _) => await EditAsync();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _printButton.Click += async (_, _) => await PrintRunAsync();
        _pdfButton.Click += async (_, _) => await ExportRunAsync(pdf: true);
        _excelButton.Click += async (_, _) => await ExportRunAsync(pdf: false);
        _exportDesignButton.Click += async (_, _) => await ExportDesignAsync();
        _importDesignButton.Click += async (_, _) => await ImportDesignAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_runButton, _newButton, _editButton, _saveButton, _refreshButton,
            _printButton, _pdfButton, _excelButton, _exportDesignButton, _importDesignButton, _closeButton));

        var content = new Grid();
        // Step 12 screen-fit: the run-results row was a fixed 190 DIP
        // and the designer column a fixed 330 — at the 660x360 minimum
        // the designer form (taller than the remaining star row) clipped.
        // Star proportions now (1.4* / 1* rows, results MinHeight 80;
        // 2.5* / 1* designer column, MinWidth 240) + a scrollable
        // designer host below.
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var keyRow = F2Ui.CreateFormGrid(1);
        keyRow.Margin = new Thickness(0, 0, 0, 8);
        _reportKey.IsEditable = true;
        _reportKey.SelectionChanged += async (_, _) => await RefreshAsync();
        F2Ui.AddField(_labels, keyRow, 0, 0, "CustomReports.ReportKey", _reportKey);
        DockPanel.SetDock(keyRow, Dock.Top);
        listPanel.Children.Add(keyRow);

        F2Ui.ConfigureGrid(_listGrid);
        _listGrid.Columns.Add(F2Ui.TextCol(_headers, "CustomReports.Name", nameof(ReportDefinition.ReportName), 220));
        _listGrid.Columns.Add(F2Ui.TextCol(_headers, "CustomReports.ReportKey", nameof(ReportDefinition.ReportKey), 130));
        _listGrid.Columns.Add(F2Ui.TextCol(_headers, "CustomReports.DataSource", nameof(ReportDefinition.DataSourceKey), 140));
        _listGrid.Columns.Add(F2Ui.TextCol(_headers, "CustomReports.Visibility", nameof(ReportDefinition.Visibility), 100, center: true));
        _listGrid.Columns.Add(F2Ui.TextCol(_headers, "CustomReports.System", nameof(ReportDefinition.IsSystem), 70, center: true));
        listPanel.Children.Add(_listGrid);
        Grid.SetRow(listPanel, 0);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        // Designer = a one-pair form grid: side labels, aligned edges
        // down the whole panel (Phenix rule).
        var designer = F2Ui.CreateFormGrid(1);
        F2Ui.AddField(_labels, designer, 0, 0, "CustomReports.Name", _name);
        F2Ui.AddField(_labels, designer, 1, 0, "CustomReports.ReportKey", _key);
        F2Ui.AddField(_labels, designer, 2, 0, "CustomReports.DataSource", _dataSource);
        F2Ui.AddField(_labels, designer, 3, 0, "CustomReports.Visibility", _visibility);
        _columnsInfo.IsReadOnly = true;
        _columnsInfo.Height = 90;
        _columnsInfo.TextWrapping = TextWrapping.Wrap;
        F2Ui.AddField(_labels, designer, 4, 0, "CustomReports.Columns", _columnsInfo);
        // B2 styling + page setup (migration 115 columns).
        F2Ui.AddField(_labels, designer, 5, 0, "CustomReports.PageSize", _pageSize);
        F2Ui.AddField(_labels, designer, 6, 0, "CustomReports.Landscape", _landscape);
        F2Ui.AddField(_labels, designer, 7, 0, "CustomReports.MarginMm", _marginMm);
        F2Ui.AddField(_labels, designer, 8, 0, "CustomReports.TitleFontSize", _titleSize);
        F2Ui.AddField(_labels, designer, 9, 0, "CustomReports.HeaderBackColor", _headerBack);
        F2Ui.AddField(_labels, designer, 10, 0, "CustomReports.HeaderTextColor", _headerText);
        F2Ui.AddField(_labels, designer, 11, 0, "CustomReports.BandingColor", _banding);
        F2Ui.AddField(_labels, designer, 12, 0, "CustomReports.BodyFontSize", _bodySize);
        F2Ui.AddField(_labels, designer, 13, 0, "CustomReports.GridLines", _gridLines);
        F2Ui.AddField(_labels, designer, 14, 0, "CustomReports.CompanyAddress", _companyAddress);
        _totalsFormulas.AcceptsReturn = true;
        _totalsFormulas.Height = 64;
        _totalsFormulas.TextWrapping = TextWrapping.Wrap;
        F2Ui.AddField(_labels, designer, 15, 0, "CustomReports.TotalsFormulas", _totalsFormulas);
        var designerScroll = new ScrollViewer { Content = designer, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(designerScroll, 0);
        Grid.SetColumn(designerScroll, 1);
        content.Children.Add(designerScroll);

        _runGrid.AutoGenerateColumns = true;
        _runGrid.IsReadOnly = true;
        _runGrid.CanUserAddRows = false;
        _runGrid.CanUserDeleteRows = false;
        Grid.SetRow(_runGrid, 1);
        Grid.SetColumnSpan(_runGrid, 2);
        content.Children.Add(_runGrid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CustomReports.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[]
        {
            _runButton, _newButton, _editButton, _saveButton, _refreshButton,
            _printButton, _pdfButton, _excelButton, _exportDesignButton, _importDesignButton, _closeButton
        }, T);
    }

    private string SelectedKey => (_reportKey.SelectedItem as string) ?? _reportKey.Text.Trim();

    private async Task LoadKeysAsync()
    {
        try
        {
            var keys = await QueryReportKeysAsync();
            _reportKey.ItemsSource = keys;
            if (keys.Count > 0)
                _reportKey.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            // Step 6 (report-layer swallow inventory): this catch used to
            // be silent, so a broken definitions store looked like "no
            // custom reports exist". The failure is now reported through
            // the central mechanism; the window still opens with an
            // empty key list, and listing then surfaces the problem.
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "CustomReportsWin.LoadKeysAsync");
        }
    }

    private static async Task<IReadOnlyList<string>> QueryReportKeysAsync()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            // Step 6: no connection string is a configuration failure,
            // not "no report keys" — surface it cataloged (DB-1004);
            // LoadKeysAsync reports it through the central mechanism.
            throw new AppError(ErrorCodes.DatabaseNotInitialized,
                "No database connection is configured for reports.");
        var isPg = DatabaseRuntime.IsPostgreSql();
        await using var connection = isPg
            ? (DbConnection)new NpgsqlConnection(cs)
            : new SqliteConnection(cs);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT ReportKey FROM CustomReportDefinitions WHERE IsDeleted = 0 ORDER BY ReportKey";
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    private async Task RefreshAsync()
    {
        try
        {
            _definitions.Clear();
            _listGrid.ItemsSource = null;
            var key = SelectedKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                var rows = await ReportRepo.ListReportsAsync(key, UserId);
                _definitions.AddRange(rows);
                _listGrid.ItemsSource = _definitions;
            }
            _statusBar.Text = $"{_definitions.Count} · {key}";
        }
        catch (Exception ex)
        {
            _statusBar.Text = ex.Message;
        }
    }

    private void NewDefinition()
    {
        _editing = new ReportDefinition
        {
            ReportKey = string.IsNullOrWhiteSpace(_key.Text) ? SelectedKey : _key.Text.Trim(),
            OwnerUserID = UserId,
            LanguageCode = "en"
        };
        _name.Text = string.Empty;
        _key.Text = _editing.ReportKey;
        _dataSource.SelectedIndex = 0;
        _visibility.SelectedItem = ReportVisibility.Private.ToString();
        _columnsInfo.Text = T("CustomReports.ColumnsSeededOnSave");
        LoadStyleIntoForm(new ReportDefinition());
    }

    /// <summary>Pushes a definition's B2 styling + page setup into
    /// the designer form (also used to reset the form to defaults).</summary>
    private void LoadStyleIntoForm(ReportDefinition def)
    {
        _pageSize.SelectedItem = string.IsNullOrWhiteSpace(def.PageSize) ? "A4" : def.PageSize;
        _landscape.IsChecked = def.IsLandscape;
        _marginMm.Text = def.MarginMm.ToString(CultureInfo.InvariantCulture);
        _titleSize.Text = def.TitleFontSize.ToString(CultureInfo.InvariantCulture);
        _headerBack.Text = def.HeaderBackColor ?? string.Empty;
        _headerText.Text = def.HeaderTextColor ?? string.Empty;
        _banding.Text = def.BandingColor ?? string.Empty;
        _bodySize.Text = def.BodyFontSize.ToString(CultureInfo.InvariantCulture);
        _gridLines.IsChecked = def.ShowGridLines;
        _companyAddress.IsChecked = def.ShowCompanyAddress;
        _totalsFormulas.Text = string.Join(Environment.NewLine,
            def.Columns
                .Where(c => !string.IsNullOrWhiteSpace(c.TotalsFormula))
                .OrderBy(c => c.DisplayOrder)
                .Select(c => $"{c.FieldKey} = {c.TotalsFormula}"));
    }

    /// <summary>Reads the B2 styling + page setup back from the form
    /// into the definition, including the totals-formula editor (one
    /// "FieldKey = expression" per line; expressions are validated
    /// by ReportRepo at save).</summary>
    private void ApplyStyleFromForm(ReportDefinition def)
    {
        def.PageSize = _pageSize.SelectedItem as string ?? "A4";
        def.IsLandscape = _landscape.IsChecked == true;
        if (double.TryParse(_marginMm.Text.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var margin) && margin >= 0)
            def.MarginMm = margin;
        if (double.TryParse(_titleSize.Text.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var titleSize) && titleSize > 0)
            def.TitleFontSize = titleSize;
        if (double.TryParse(_bodySize.Text.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var bodySize) && bodySize > 0)
            def.BodyFontSize = bodySize;
        def.HeaderBackColor = NullIfBlank(_headerBack.Text);
        def.HeaderTextColor = NullIfBlank(_headerText.Text);
        def.BandingColor = NullIfBlank(_banding.Text);
        def.ShowGridLines = _gridLines.IsChecked == true;
        def.ShowCompanyAddress = _companyAddress.IsChecked == true;

        foreach (var col in def.Columns)
            col.TotalsFormula = null;
        foreach (var rawLine in _totalsFormulas.Text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new InvalidOperationException(
                    FormatLocalized(T("CustomReports.TotalsFormulaUnknownField"), line));
            var field = line[..eq].Trim();
            var col = def.Columns.FirstOrDefault(c =>
                string.Equals(c.FieldKey, field, StringComparison.OrdinalIgnoreCase));
            if (col is null)
                throw new InvalidOperationException(
                    FormatLocalized(T("CustomReports.TotalsFormulaUnknownField"), field));
            col.TotalsFormula = line[(eq + 1)..].Trim();
        }
    }

    private static string? NullIfBlank(string text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private async Task EditAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Reporting.DesignerView))
                throw new InvalidOperationException(T("CustomReports.PermissionDenied"));
            if (_listGrid.SelectedItem is not ReportDefinition selected || selected.ReportDefID is not int id)
                throw new InvalidOperationException(T("CustomReports.SelectFirst"));
            var loaded = await ReportRepo.LoadReportAsync(id, UserId);
            if (loaded is null)
                throw new InvalidOperationException(T("CustomReports.NotFound"));
            _editing = loaded;
            _name.Text = loaded.ReportName;
            _key.Text = loaded.ReportKey;
            _dataSource.SelectedItem = loaded.DataSourceKey;
            _visibility.SelectedItem = loaded.Visibility.ToString();
            _columnsInfo.Text = string.Join(", ", loaded.Columns.OrderBy(c => c.DisplayOrder).Select(c => c.FieldKey));
            LoadStyleIntoForm(loaded);
            _statusBar.Text = loaded.ReportName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Reporting.CustomManage) &&
                !AppHost.Permissions.Has(PermCat.Reporting.DesignerManage))
                throw new InvalidOperationException(T("CustomReports.PermissionDenied"));
            var def = _editing ?? new ReportDefinition { OwnerUserID = UserId, LanguageCode = "en" };
            def.ReportName = _name.Text.Trim();
            def.ReportKey = _key.Text.Trim();
            def.DataSourceKey = _dataSource.SelectedItem as string ?? string.Empty;
            if (Enum.TryParse<ReportVisibility>(_visibility.SelectedItem as string, out var visibility))
                def.Visibility = visibility;
            if (string.IsNullOrWhiteSpace(def.ReportName) || string.IsNullOrWhiteSpace(def.ReportKey))
                throw new InvalidOperationException(T("CustomReports.NameAndKeyRequired"));

            if (def.Columns.Count == 0)
            {
                var rows = await ExecuteSourceAsync(def.DataSourceKey, CancellationToken.None);
                var fields = rows is { Count: > 0 }
                    ? rows[0].Keys.ToList()
                    : new List<string> { "Code", "Name" };
                var order = 0;
                foreach (var field in fields)
                {
                    def.Columns.Add(new ReportColumnDef
                    {
                        FieldKey = field,
                        CaptionEN = field,
                        CaptionAR = field,
                        DisplayOrder = order++,
                        ColumnWidth = 140,
                        IsVisible = true,
                        DataFormat = ReportDataFormat.Text
                    });
                }
            }

            ApplyStyleFromForm(def);

            var companyGuid = await ResolveCompanyGuidAsync();
            var id = await ReportRepo.SaveReportAsync(def, companyGuid, UserId);
            def.ReportDefID = id;
            _editing = def;
            MessageBox.Show(this, T("CustomReports.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadKeysAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (_listGrid.SelectedItem is not ReportDefinition selected || selected.ReportDefID is not int id)
                throw new InvalidOperationException(T("CustomReports.SelectFirst"));
            var loaded = await ReportRepo.LoadReportAsync(id, UserId);
            if (loaded is null)
                throw new InvalidOperationException(T("CustomReports.NotFound"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var rows = await ExecuteSourceAsync(loaded.DataSourceKey, cts.Token);
            if (rows is null)
            {
                _statusBar.Text = FormatLocalized(
                    T("CustomReports.SourceNotRunnable"), loaded.DataSourceKey);
                _runGrid.ItemsSource = null;
                return;
            }

            var companyGuid = await ResolveCompanyGuidAsync();
            var vm = new ReportDesignerVM(loaded, companyGuid, UserId);
            vm.SetData(rows.Select(r => new Dictionary<string, object?>(r)));

            // B2: totals = plain aggregates + cell-level totals
            // formulas, computed over the full result set.
            var totals = loaded.ShowTotalsRow ? vm.ComputeTotals(vm.Rows) : null;
            _lastRunDef = loaded;
            _lastRunRows = vm.Rows;
            _lastRunTotals = totals;

            var table = new DataTable();
            var fields = loaded.VisibleColumns.Select(c => c.FieldKey).ToList();
            if (fields.Count == 0 && vm.Rows.Count > 0)
                fields = vm.Rows[0].Keys.ToList();
            foreach (var field in fields)
                table.Columns.Add(field);
            foreach (var row in vm.Rows)
            {
                var dataRow = table.NewRow();
                foreach (var field in fields)
                    dataRow[field] = row.TryGetValue(field, out var v) ? v ?? DBNull.Value : DBNull.Value;
                table.Rows.Add(dataRow);
            }
            if (totals != null)
            {
                var totalRow = table.NewRow();
                foreach (var field in fields)
                    totalRow[field] = totals.TryGetValue(field, out var v) ? v ?? DBNull.Value : DBNull.Value;
                table.Rows.Add(totalRow);
            }
            _runGrid.ItemsSource = table.DefaultView;
            _statusBar.Text = $"{loaded.ReportName} · {vm.Rows.Count}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task PrintRunAsync()
    {
        try
        {
            var model = await BuildExportModelAsync();
            if (model != null)
                XptServ.Print(this, model);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportRunAsync(bool pdf)
    {
        try
        {
            var model = await BuildExportModelAsync();
            if (model == null)
                return;
            if (pdf)
                XptServ.ExportPdf(this, model);
            else
                XptServ.ExportExcel(this, model);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Builds the export/print model for the last run,
    /// carrying the design's style + totals through the real report
    /// path (XptServ → RptRend).</summary>
    private async Task<XptServ.ExportModel?> BuildExportModelAsync()
    {
        if (_lastRunDef is null || _lastRunRows is null)
        {
            _statusBar.Text = T("CustomReports.RunFirst");
            return null;
        }

        var def = _lastRunDef;
        var cols = def.VisibleColumns.Select(c => new RptCol
        {
            Key = c.FieldKey,
            HeaderTextKey = string.IsNullOrWhiteSpace(c.CaptionEN) ? c.FieldKey : c.CaptionEN!,
            Type = c.DataFormat switch
            {
                ReportDataFormat.Money => RptColType.Money,
                ReportDataFormat.Qty => RptColType.FractionalNumber,
                ReportDataFormat.Percent => RptColType.Percent,
                ReportDataFormat.Date => RptColType.Date,
                ReportDataFormat.DateTime => RptColType.DateTime,
                _ => RptColType.Text
            },
            Width = c.ColumnWidth
        }).ToList();

        return new XptServ.ExportModel
        {
            ReportKey = def.ReportKey,
            Title = def.ReportName,
            CompanyName = CurrentCompanyDisplay,
            GeneratedBy = CurrentUserDisplay,
            FilterSummary = string.Empty,
            Columns = cols,
            Rows = _lastRunRows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(),
            Totals = _lastRunTotals,
            Rtl = FlowDirection == FlowDirection.RightToLeft,
            Translate = key => T(key),
            Style = def.ToStyle(),
            CompanyAddress = def.ShowCompanyAddress ? await CompanyAddressAsync() : null
        };
    }

    private static async Task<string?> CompanyAddressAsync()
    {
        try
        {
            var companies = await CoLocalStore.LoadAsync();
            var rec = companies.FirstOrDefault(c =>
                string.Equals(c.Code, CurrentCompanyCode, StringComparison.OrdinalIgnoreCase));
            if (rec is null)
                return null;
            var cityCountry = string.Join(", ",
                new[] { rec.City, rec.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var parts = new[] { rec.AddressLine1, rec.AddressLine2, cityCountry }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var text = string.Join("\n", parts);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "CustomReportsWin.CompanyAddressAsync");
            return null;
        }
    }

    /// <summary>Exports the selected design as an encrypted .strd
    /// file (B0 mechanism) — readable only by ShouTech.</summary>
    private async Task ExportDesignAsync()
    {
        try
        {
            if (_listGrid.SelectedItem is not ReportDefinition selected || selected.ReportDefID is not int id)
                throw new InvalidOperationException(T("CustomReports.SelectFirst"));
            var loaded = await ReportRepo.LoadReportAsync(id, UserId);
            if (loaded is null)
                throw new InvalidOperationException(T("CustomReports.NotFound"));
            var dialog = new SaveFileDialog
            {
                Filter = DesignFileSvc.FilterFor(DesignFileSvc.Kind.Report),
                DefaultExt = DesignFileSvc.ExtensionFor(DesignFileSvc.Kind.Report),
                AddExtension = true,
                OverwritePrompt = true,
                FileName = loaded.ReportKey + DesignFileSvc.ExtensionFor(DesignFileSvc.Kind.Report)
            };
            if (dialog.ShowDialog(this) != true)
                return;
            if (!DesignFileSvc.Export(dialog.FileName, DesignFileSvc.Kind.Report,
                    loaded.ReportName, ReportDesignJson.Serialize(loaded)))
                throw new InvalidOperationException(T("CustomReports.DesignExportFailed"));
            _statusBar.Text = T("CustomReports.DesignExported");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Imports an encrypted .strd design as a NEW private
    /// design owned by the current user.</summary>
    private async Task ImportDesignAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Reporting.CustomManage) &&
                !AppHost.Permissions.Has(PermCat.Reporting.DesignerManage))
                throw new InvalidOperationException(T("CustomReports.PermissionDenied"));
            var dialog = new OpenFileDialog
            {
                Filter = DesignFileSvc.FilterFor(DesignFileSvc.Kind.Report)
            };
            if (dialog.ShowDialog(this) != true)
                return;
            var json = DesignFileSvc.Import(dialog.FileName, DesignFileSvc.Kind.Report, out _);
            var def = json is null ? null : ReportDesignJson.Deserialize(json, UserId);
            if (def is null)
                throw new InvalidOperationException(T("CustomReports.DesignImportFailed"));
            var companyGuid = await ResolveCompanyGuidAsync();
            await ReportRepo.SaveReportAsync(def, companyGuid, UserId);
            _statusBar.Text = T("CustomReports.DesignImported");
            await LoadKeysAsync();
            if (_reportKey.ItemsSource is IEnumerable<string> keys && keys.Contains(def.ReportKey))
                _reportKey.SelectedItem = def.ReportKey;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Executes a DataSourceKey against the RptQuery sources that
    /// exist; returns null for any other key (honest not-runnable state).</summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> ExecuteSourceAsync(string dataSourceKey, CancellationToken ct)
    {
        var from = new DateTime(DateTime.Today.Year, 1, 1);
        var to = DateTime.Today;
        return dataSourceKey switch
        {
            "TrialBalance" => await RptQuery.TrialBalanceAsync(from, to, ct),
            "BalanceSheet" => await RptQuery.BalanceSheetAsync(to, ct),
            "IncomeStatement" => await RptQuery.IncomeStatementAsync(from, to, ct),
            "CustomerBalances" => await RptQuery.CustomerBalancesAsync(ct),
            "SupplierBalances" => await RptQuery.SupplierBalancesAsync(ct),
            "SalesInvoices" => await RptQuery.SalesInvoiceListAsync(from, to, ct),
            "PurchaseInvoices" => await RptQuery.PurchaseInvoiceListAsync(from, to, ct),
            "StockBalances" => await RptQuery.StockBalancesAsync(null, null, null, null, ct),
            _ => null
        };
    }

    private static async Task<Guid> ResolveCompanyGuidAsync()
    {
        try
        {
            var companies = await CoLocalStore.LoadAsync();
            return companies.FirstOrDefault(c => string.Equals(c.Code, CurrentCompanyCode, StringComparison.OrdinalIgnoreCase))?.CompanyId ?? Guid.Empty;
        }
        catch (Exception ex)
        {
            // Step 6 (report-layer swallow inventory): a failed company
            // lookup used to silently become Guid.Empty, so custom
            // reports ran against no company and returned nothing. The
            // failure is now reported centrally before the fallback.
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "CustomReportsWin.ResolveCompanyGuidAsync");
            return Guid.Empty;
        }
    }
}
