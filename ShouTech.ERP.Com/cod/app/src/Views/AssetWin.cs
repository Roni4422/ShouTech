using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Fixed Assets register (Phase 3): Explorer-style list + editor with the
/// computed depreciation schedule of the selected asset below (Al-Ameen
/// schedule pattern: sortable grid + totals in the status bar). Depreciation
/// is computed by <see cref="AssetService"/> and never stored. Actions live in
/// the Explorer-style bottom command bar; writes are validated, transactional,
/// and RBAC-checked.
/// </summary>
public sealed class AssetWin : BaseWin
{
    protected override string? ScheduleTintName => "Stone";

    private readonly AssetService? _service;
    private readonly List<FixedAsset> _assets = new();
    private readonly List<DepreciationYearRow> _schedule = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly DataGrid _scheduleGrid = new();
    private readonly TextBox _code = new();
    private readonly TextBox _name = new();
    private readonly TextBox _category = new();
    private readonly DatePicker _purchase = new();
    private readonly TextBox _cost = new();
    private readonly TextBox _salvage = new();
    private readonly TextBox _life = new();
    private readonly ComboBox _method = new();
    private readonly TextBox _location = new();
    private readonly ComboBox _status = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _accumulated = new();
    private readonly TextBox _bookValue = new();
    private readonly TextBlock _assetsHeader = new();
    private readonly TextBlock _scheduleHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;

    public AssetWin()
    {
        _service = AppHost.Assets;
        Width = 1180;
        Height = 740;
        MinWidth = 980;
        MinHeight = 620;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Assets.View))
        {
            MessageBox.Show(this, T("Assets.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var statusBorder = new Border
        {
            Child = _statusBar,
            Padding = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _refreshButton, _closeButton })
            bar.Children.Add(b);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(180) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _assetsHeader.FontWeight = FontWeights.SemiBold;
        _assetsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_assetsHeader, Dock.Top);
        listPanel.Children.Add(_assetsHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Assets.Code", nameof(FixedAsset.Code), 90));
        _grid.Columns.Add(TextCol("Assets.Name", nameof(FixedAsset.Name), 190));
        _grid.Columns.Add(TextCol("Assets.Category", nameof(FixedAsset.Category), 110));
        _grid.Columns.Add(TextCol("Assets.PurchaseDate", nameof(FixedAsset.PurchaseDate), 100, center: true));
        _grid.Columns.Add(TextCol("Assets.Cost", nameof(FixedAsset.Cost), 110, numeric: true));
        _grid.Columns.Add(TextCol("Assets.Status", nameof(FixedAsset.Status), 90));
        _grid.SelectionChanged += (_, _) => OnAssetSelected();
        listPanel.Children.Add(_grid);
        Grid.SetRow(listPanel, 0);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var editor = new StackPanel();
        var row1 = new WrapPanel();
        row1.Children.Add(Labeled("Assets.Code", _code, 140));
        row1.Children.Add(Labeled("Assets.Status", _status, 150));
        editor.Children.Add(row1);
        editor.Children.Add(Labeled("Assets.Name", _name, 300));
        var row2 = new WrapPanel();
        row2.Children.Add(Labeled("Assets.Category", _category, 140));
        row2.Children.Add(Labeled("Assets.Location", _location, 150));
        editor.Children.Add(row2);
        editor.Children.Add(Labeled("Assets.PurchaseDate", _purchase, 160));
        var row3 = new WrapPanel();
        row3.Children.Add(Labeled("Assets.Cost", _cost, 140));
        row3.Children.Add(Labeled("Assets.Salvage", _salvage, 140));
        editor.Children.Add(row3);
        var row4 = new WrapPanel();
        row4.Children.Add(Labeled("Assets.LifeYears", _life, 120));
        row4.Children.Add(Labeled("Assets.Method", _method, 170));
        editor.Children.Add(row4);
        var row5 = new WrapPanel();
        row5.Children.Add(Labeled("Assets.Accumulated", _accumulated, 140));
        row5.Children.Add(Labeled("Assets.BookValue", _bookValue, 140));
        editor.Children.Add(row5);
        editor.Children.Add(Labeled("Assets.Notes", _notes, 300));
        _accumulated.IsReadOnly = true;
        _bookValue.IsReadOnly = true;
        _method.ItemsSource = DepreciationMethods.All;
        _method.SelectedItem = DepreciationMethods.StraightLine;
        _status.ItemsSource = AssetStatuses.All;
        _status.SelectedItem = AssetStatuses.Active;
        _purchase.SelectedDate = DateTime.Today;
        var editorScroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(editorScroll, 0);
        Grid.SetColumn(editorScroll, 1);
        content.Children.Add(editorScroll);

        _scheduleHeader.FontWeight = FontWeights.SemiBold;
        _scheduleHeader.Margin = new Thickness(0, 10, 0, 6);
        Grid.SetRow(_scheduleHeader, 1);
        Grid.SetColumnSpan(_scheduleHeader, 2);
        content.Children.Add(_scheduleHeader);

        ConfigureGrid(_scheduleGrid);
        _scheduleGrid.Columns.Add(TextCol("Assets.Year", nameof(DepreciationYearRow.Year), 80, center: true));
        _scheduleGrid.Columns.Add(TextCol("Assets.Depreciation", nameof(DepreciationYearRow.Depreciation), 150, numeric: true));
        _scheduleGrid.Columns.Add(TextCol("Assets.Accumulated", nameof(DepreciationYearRow.Accumulated), 170, numeric: true));
        _scheduleGrid.Columns.Add(TextCol("Assets.BookValue", nameof(DepreciationYearRow.BookValue), 150, numeric: true));
        Grid.SetRow(_scheduleGrid, 2);
        Grid.SetColumnSpan(_scheduleGrid, 2);
        content.Children.Add(_scheduleGrid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Assets.Title");
        _assetsHeader.Text = T("Assets.Title");
        _scheduleHeader.Text = T("Assets.Schedule");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Assets.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetAllAsync(CurrentCompanyCode);
            _assets.Clear();
            _assets.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _assets;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "AssetWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnAssetSelected()
    {
        if (_grid.SelectedItem is not FixedAsset asset)
            return;
        _selectedId = asset.Id;
        _code.Text = asset.Code;
        _name.Text = asset.Name;
        _category.Text = asset.Category ?? string.Empty;
        _purchase.SelectedDate = asset.PurchaseDate.ToDateTime(TimeOnly.MinValue);
        _cost.Text = asset.Cost.ToString("0.##", CultureInfo.CurrentCulture);
        _salvage.Text = asset.SalvageValue.ToString("0.##", CultureInfo.CurrentCulture);
        _life.Text = asset.UsefulLifeYears.ToString(CultureInfo.InvariantCulture);
        _method.SelectedItem = asset.DepreciationMethod;
        _location.Text = asset.Location ?? string.Empty;
        _status.SelectedItem = asset.Status;
        _notes.Text = asset.Notes ?? string.Empty;
        var today = DateOnly.FromDateTime(DateTime.Today);
        _accumulated.Text = AssetService.AccumulatedDepreciation(asset, today).ToString("N2", CultureInfo.CurrentCulture);
        _bookValue.Text = AssetService.BookValue(asset, today).ToString("N2", CultureInfo.CurrentCulture);
        _schedule.Clear();
        _schedule.AddRange(AssetService.BuildSchedule(asset));
        _scheduleGrid.ItemsSource = null;
        _scheduleGrid.ItemsSource = _schedule;
        UpdateStatusBar();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Assets.ServiceUnavailable"));
            var isNew = !_selectedId.HasValue;
            if (!AppHost.Permissions.Has(isNew ? PermCat.Assets.Create : PermCat.Assets.Update))
                throw new InvalidOperationException(T("Assets.PermissionDenied"));
            var asset = new FixedAsset
            {
                Id = _selectedId ?? Guid.NewGuid(),
                Code = _code.Text.Trim(),
                Name = _name.Text.Trim(),
                Category = NullIfBlank(_category.Text),
                PurchaseDate = _purchase.SelectedDate.HasValue ? DateOnly.FromDateTime(_purchase.SelectedDate.Value) : DateOnly.FromDateTime(DateTime.Today),
                Cost = ParseDecimal(_cost.Text),
                SalvageValue = ParseDecimal(_salvage.Text),
                UsefulLifeYears = ParseInt(_life.Text),
                DepreciationMethod = _method.SelectedItem as string ?? DepreciationMethods.StraightLine,
                Location = NullIfBlank(_location.Text),
                Status = _status.SelectedItem as string ?? AssetStatuses.Active,
                Notes = NullIfBlank(_notes.Text)
            };
            await _service.SaveAsync(CurrentCompanyCode, asset, CurrentUserId);
            _selectedId = asset.Id;
            MessageBox.Show(this, T("Assets.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "AssetWin.SaveAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || !_selectedId.HasValue)
                return;
            if (!AppHost.Permissions.Has(PermCat.Assets.Delete))
                throw new InvalidOperationException(T("Assets.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteAsync(CurrentCompanyCode, _selectedId.Value, CurrentUserId);
            _selectedId = null;
            ClearEditor();
            MessageBox.Show(this, T("Assets.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "AssetWin.DeleteAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _code.Clear();
        _name.Clear();
        _category.Clear();
        _purchase.SelectedDate = DateTime.Today;
        _cost.Clear();
        _salvage.Clear();
        _life.Clear();
        _method.SelectedItem = DepreciationMethods.StraightLine;
        _location.Clear();
        _status.SelectedItem = AssetStatuses.Active;
        _notes.Clear();
        _accumulated.Clear();
        _bookValue.Clear();
        _schedule.Clear();
        _scheduleGrid.ItemsSource = null;
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var totalCost = _assets.Sum(a => a.Cost);
        var totalBook = _assets.Sum(a => AssetService.BookValue(a, today));
        _statusBar.Text = $"{_assets.Count} · {T("Assets.Cost")}: {totalCost:N2} · {T("Assets.BookValue")}: {totalBook:N2}";
    }

    private static void ConfigureGrid(DataGrid grid)
    {
        grid.AutoGenerateColumns = false;
        grid.CanUserAddRows = false;
        grid.CanUserDeleteRows = false;
        grid.IsReadOnly = true;
        grid.CanUserSortColumns = true;
        grid.CanUserReorderColumns = true;
        grid.CanUserResizeColumns = true;
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.RowHeaderWidth = 0;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        grid.AlternationCount = 2;
        DataGridTableBehavior.SetEnableStandardContextMenu(grid, true);
    }

    private DataGridTextColumn TextCol(string key, string path, double width, bool numeric = false, bool center = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        if (numeric)
            column.ElementStyle = RightAlignStyle;
        else if (center)
            column.ElementStyle = CenterAlignStyle;
        _headers.Add((column, key));
        return column;
    }

    private static readonly Style RightAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) }
    };

    private static readonly Style CenterAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center) }
    };

    private StackPanel Labeled(string key, Control control, double width = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        if (width > 0)
            panel.Width = width;
        var label = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _labels.Add((label, key));
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = 92;
        button.MinHeight = 32;
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Padding = new Thickness(12, 3, 12, 3);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal ParseDecimal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }

    private static int ParseInt(string text)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ||
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Useful life must be a whole number of years.");
    }
}
