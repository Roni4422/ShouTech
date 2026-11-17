using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Party manager (ribbon chunk B): list of all parties (customers + suppliers)
/// with a kind filter, real balances from party_ledger, and a bottom command
/// bar (New / Edit / Delete / Open Card / Refresh / Close). Editing happens in
/// <see cref="CstCard"/>; deletes are guarded by the service against ledger
/// references.
/// </summary>
public sealed class PartyManagerWin : BaseWin
{
    private readonly ComboBox _cbKind = new() { Width = 160, Height = 26 };
    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _newButton = new();
    private readonly Button _editButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _openCardButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly List<PartyRow> _rows = new();

    public PartyManagerWin()
    {
        Width = 1040;
        Height = 620;
        MinWidth = 840;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Customers.View))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StyleButton(_editButton, "Common.Edit");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_openCardButton, "MasterCards.OpenCard");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => OpenCard(null);
        _editButton.Click += (_, _) =>
        {
            if (_grid.SelectedItem is PartyRow row)
                OpenCard(row);
        };
        _openCardButton.Click += (_, _) =>
        {
            if (_grid.SelectedItem is PartyRow row)
                OpenCard(row);
        };
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _editButton, _deleteButton, _openCardButton, _refreshButton, _closeButton })
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

        var filterPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        filterPanel.Children.Add(Labeled("MasterCards.Kind", _cbKind));
        _cbKind.SelectionChanged += async (_, _) => await RefreshAsync();
        DockPanel.SetDock(filterPanel, Dock.Top);
        root.Children.Add(filterPanel);

        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("MasterCards.Kind", nameof(PartyRow.Kind), 70, center: true));
        _grid.Columns.Add(TextCol("CustCard.Code", nameof(PartyRow.Code), 120));
        _grid.Columns.Add(TextCol("MasterCards.Name", nameof(PartyRow.NameEn), 240));
        _grid.Columns.Add(TextCol("CustCard.Phone", nameof(PartyRow.Phone), 120));
        _grid.Columns.Add(TextCol("CustCard.CreditLimit", nameof(PartyRow.CreditLimit), 110, numeric: true));
        _grid.Columns.Add(TextCol("CustCard.Balance", nameof(PartyRow.Balance), 120, numeric: true));
        _grid.Columns.Add(TextCol("MasterCards.Active", nameof(PartyRow.ActiveText), 80, center: true));
        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is PartyRow row)
                OpenCard(row);
        };
        root.Children.Add(_grid);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("MasterCards.PartyManagerTitle");
        var selected = _cbKind.SelectedIndex;
        _cbKind.Items.Clear();
        _cbKind.Items.Add(T("MasterCards.All"));
        _cbKind.Items.Add(T("CustCard.Customer"));
        _cbKind.Items.Add(T("CustCard.Supplier"));
        _cbKind.SelectedIndex = selected < 0 ? 0 : selected;
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _editButton, _deleteButton, _openCardButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    private string? SelectedKind() => _cbKind.SelectedIndex switch
    {
        1 => PartyKinds.Customer,
        2 => PartyKinds.Supplier,
        _ => null
    };

    private async Task RefreshAsync()
    {
        try
        {
            var service = AppHost.MasterCards;
            if (service is null)
            {
                _statusBar.Text = T("MasterCards.ServiceUnavailable");
                return;
            }
            var parties = await service.GetPartiesAsync(CurrentCompanyCode, SelectedKind());
            _rows.Clear();
            foreach (var party in parties)
            {
                var balance = await service.GetPartyBalanceAsync(CurrentCompanyCode, party.Kind, party.Code);
                _rows.Add(new PartyRow
                {
                    Id = party.Id,
                    Kind = party.Kind,
                    Code = party.Code,
                    NameEn = party.NameEn,
                    Phone = party.Phone ?? party.Mobile ?? string.Empty,
                    CreditLimit = party.CreditLimit,
                    Balance = balance.Balance,
                    IsActive = party.IsActive
                });
            }
            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenCard(PartyRow? row)
    {
        var card = new CstCard(row is not null && row.Kind == PartyKinds.Supplier) { Owner = this };
        card.Show();
        if (row is not null)
            _ = card.LoadParty(row.Kind, row.Code);
        card.Closed += async (_, _) => await RefreshAsync();
    }

    private async Task DeleteAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (_grid.SelectedItem is not PartyRow row)
                return;
            if (!AppHost.Permissions.Has(PermCat.Customers.Delete))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await service.DeletePartyAsync(CurrentCompanyCode, row.Id, row.Kind, row.Code, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateStatusBar()
    {
        decimal total = 0;
        foreach (var row in _rows)
            total += row.Balance;
        _statusBar.Text = $"{_rows.Count} · {T("CustCard.Balance")}: {total:N2}";
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

    private static StackPanel Labeled(string key, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 6) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 2), Opacity = 0.85 });
        if (control is ComboBox comboBox)
            comboBox.SetResourceReference(StyleProperty, "VisuComboBoxStyle");
        sp.Children.Add(control);
        return sp;
    }

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = 92;
        button.MinHeight = 32;
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Padding = new Thickness(12, 3, 12, 3);
    }

    /// <summary>One party row with its live ledger balance.</summary>
    public sealed class PartyRow
    {
        public Guid Id { get; init; }
        public string Kind { get; init; } = PartyKinds.Customer;
        public string Code { get; init; } = string.Empty;
        public string NameEn { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public decimal CreditLimit { get; init; }
        public decimal Balance { get; init; }
        public bool IsActive { get; init; }
        public string ActiveText => IsActive ? "Yes" : "No";
    }
}
