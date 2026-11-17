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
    private readonly DataGrid _runGrid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _runButton = new();
    private readonly Button _newButton = new();
    private readonly Button _editButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private ReportDefinition? _editing;

    public CustomReportsWin()
    {
        Width = 1180;
        Height = 720;
        MinWidth = 980;
        MinHeight = 580;
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
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _runButton.Click += async (_, _) => await RunAsync();
        _newButton.Click += (_, _) => NewDefinition();
        _editButton.Click += async (_, _) => await EditAsync();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_runButton, _newButton, _editButton, _saveButton, _refreshButton, _closeButton));

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

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
        Grid.SetRow(designer, 0);
        Grid.SetColumn(designer, 1);
        content.Children.Add(designer);

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
        F2Ui.ApplyTexts(_labels, _headers, new[] { _runButton, _newButton, _editButton, _saveButton, _refreshButton, _closeButton }, T);
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
        cmd.CommandText = isPg
            ? "SELECT DISTINCT ReportKey FROM CustomReportDefinitions WHERE IsDeleted = FALSE ORDER BY ReportKey"
            : "SELECT DISTINCT ReportKey FROM CustomReportDefinitions WHERE IsDeleted = 0 ORDER BY ReportKey";
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
    }

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
            _runGrid.ItemsSource = table.DefaultView;
            _statusBar.Text = $"{loaded.ReportName} · {table.Rows.Count}";
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
