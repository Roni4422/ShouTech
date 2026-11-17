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

namespace ShouTech.App.Views;

/// <summary>
/// POS loyalty points window (ribbon completion F1): accounts grid (party,
/// balance) with the selected account's entries below. Earn/Redeem write a
/// loyalty_entries row and update the balance in ONE transaction through
/// <see cref="DefinitionService"/>; redemption is blocked when the balance
/// is insufficient (enforced in the repository transaction).
/// </summary>
public sealed class LoyaltyWin : BaseWin
{
    private readonly DefinitionService? _service;
    private readonly List<LoyaltyAccount> _accounts = new();
    private readonly List<LoyaltyEntry> _entries = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly DataGrid _entryGrid = new();
    private readonly TextBox _party = new();
    private readonly TextBox _points = new();
    private readonly TextBox _docRef = new();
    private readonly TextBox _notes = new();
    private readonly TextBlock _accountsHeader = new();
    private readonly TextBlock _entriesHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _earnButton = new();
    private readonly Button _redeemButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private string? _selectedAccountId;

    public LoyaltyWin()
    {
        _service = AppHost.Definitions;
        Width = 1080;
        Height = 700;
        MinWidth = 900;
        MinHeight = 560;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Pos.TerminalView) && !AppHost.Permissions.Has(PermCat.Pos.DiscountManage))
        {
            MessageBox.Show(this, T("Loyalty.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StyleButton(_earnButton, "Loyalty.Earn");
        StyleButton(_redeemButton, "Loyalty.Redeem");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _earnButton.Click += async (_, _) => await AdjustAsync(earn: true);
        _redeemButton.Click += async (_, _) => await AdjustAsync(earn: false);
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _earnButton, _redeemButton, _refreshButton, _closeButton })
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
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _accountsHeader.FontWeight = FontWeights.SemiBold;
        _accountsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_accountsHeader, Dock.Top);
        listPanel.Children.Add(_accountsHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Loyalty.PartyCode", nameof(LoyaltyAccount.PartyCode), 160));
        _grid.Columns.Add(TextCol("Loyalty.Balance", nameof(LoyaltyAccount.PointsBalance), 140, numeric: true));
        _grid.SelectionChanged += async (_, _) => await OnAccountSelectedAsync();
        listPanel.Children.Add(_grid);
        Grid.SetRow(listPanel, 0);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        // Editor = a one-pair form grid: side labels, aligned edges
        // down the whole editor (Phenix rule).
        var editor = CreateFormGrid(1);
        AddFormField(_labels, editor, 0, 0, "Loyalty.PartyCode", _party);
        AddFormField(_labels, editor, 1, 0, "Loyalty.Points", _points);
        AddFormField(_labels, editor, 2, 0, "Loyalty.DocRef", _docRef);
        AddFormField(_labels, editor, 3, 0, "Loyalty.Notes", _notes);
        Grid.SetRow(editor, 0);
        Grid.SetColumn(editor, 1);
        content.Children.Add(editor);

        _entriesHeader.FontWeight = FontWeights.SemiBold;
        _entriesHeader.Margin = new Thickness(0, 10, 0, 6);
        Grid.SetRow(_entriesHeader, 1);
        Grid.SetColumnSpan(_entriesHeader, 2);
        content.Children.Add(_entriesHeader);

        ConfigureGrid(_entryGrid);
        _entryGrid.Columns.Add(TextCol("Loyalty.EntryDate", nameof(LoyaltyEntry.EntryDate), 110, center: true));
        _entryGrid.Columns.Add(TextCol("Loyalty.Points", nameof(LoyaltyEntry.Points), 110, numeric: true));
        _entryGrid.Columns.Add(TextCol("Loyalty.DocRef", nameof(LoyaltyEntry.DocRef), 160));
        _entryGrid.Columns.Add(TextCol("Loyalty.Notes", nameof(LoyaltyEntry.Notes), 260));
        Grid.SetRow(_entryGrid, 2);
        Grid.SetColumnSpan(_entryGrid, 2);
        content.Children.Add(_entryGrid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Loyalty.Title");
        _accountsHeader.Text = T("Loyalty.Title");
        _entriesHeader.Text = T("Loyalty.Entries");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _earnButton, _redeemButton, _refreshButton, _closeButton })
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
                _statusBar.Text = T("Loyalty.ServiceUnavailable");
                return;
            }
            var rows = await _service.ListLoyaltyAccountsAsync(CurrentCompanyCode);
            _accounts.Clear();
            _accounts.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _accounts;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OnAccountSelectedAsync()
    {
        if (_grid.SelectedItem is not LoyaltyAccount account)
            return;
        _selectedAccountId = account.Id;
        _party.Text = account.PartyCode;
        _entries.Clear();
        _entryGrid.ItemsSource = null;
        if (_service is null)
            return;
        try
        {
            var rows = await _service.ListLoyaltyEntriesAsync(CurrentCompanyCode, account.Id);
            _entries.AddRange(rows);
            _entryGrid.ItemsSource = _entries;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateStatusBar();
    }

    private async Task AdjustAsync(bool earn)
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Loyalty.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Pos.DiscountManage))
                throw new InvalidOperationException(T("Loyalty.PermissionDenied"));
            var party = _party.Text.Trim();
            if (party.Length == 0)
                throw new InvalidOperationException(T("Loyalty.PartyRequired"));
            var points = ParseDecimal(_points.Text);
            var newBalance = earn
                ? await _service.EarnPointsAsync(CurrentCompanyCode, party, points, NullIfBlank(_docRef.Text), NullIfBlank(_notes.Text), CurrentUserId)
                : await _service.RedeemPointsAsync(CurrentCompanyCode, party, points, NullIfBlank(_docRef.Text), NullIfBlank(_notes.Text), CurrentUserId);
            MessageBox.Show(this, $"{T("Loyalty.Balance")}: {newBalance:0.##}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            _points.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateStatusBar()
    {
        var total = _accounts.Sum(a => a.PointsBalance);
        _statusBar.Text = $"{_accounts.Count} · {T("Loyalty.Balance")}: {total:N2} · {_entries.Count} {T("Loyalty.Entries")}";
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


    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal ParseDecimal(string text)
    {
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Points must be a number.");
    }
}
