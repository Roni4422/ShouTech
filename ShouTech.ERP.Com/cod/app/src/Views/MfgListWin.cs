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
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Manufacturing orders list (RS-8): Al-Ameen schedule grid
/// (Doc No | Date | Item | Qty | Unit Cost | Total | Status) with totals in
/// the status bar and Open / New / Refresh / Close in the bottom command bar.
/// Open loads the selected order into MfgWin via its LoadOrder(docNo).
/// RBAC: MFG.ORDER.*.
/// </summary>
public sealed class MfgListWin : BaseWin
{
    private readonly List<MfgOrder> _orders = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _openButton = new();
    private readonly Button _newButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public MfgListWin()
    {
        Width = 1080;
        Height = 640;
        MinWidth = 900;
        MinHeight = 520;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.OrderView))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StyleButton(_openButton, "MfgOrders.Open");
        StyleButton(_newButton, "Common.New");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _openButton.Click += (_, _) => OpenSelected();
        _newButton.Click += (_, _) => { var win = new MfgWin { Owner = this }; win.Show(); };
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _openButton, _newButton, _refreshButton, _closeButton })
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

        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("MfgOrder.OrderNo", nameof(MfgOrder.DocNo), 90, center: true));
        _grid.Columns.Add(TextCol("MfgOrder.Date", nameof(MfgOrder.DocDate), 100, center: true));
        _grid.Columns.Add(TextCol("MfgOrder.TargetMaterial", nameof(MfgOrder.ItemCode), 130));
        _grid.Columns.Add(TextCol("Invoice.Col.Description", nameof(MfgOrder.ItemName), 220));
        _grid.Columns.Add(TextCol("MfgOrder.Quantity", nameof(MfgOrder.Qty), 90, numeric: true));
        _grid.Columns.Add(TextCol("MfgOrder.UnitCost", nameof(MfgOrder.UnitCost), 110, numeric: true));
        _grid.Columns.Add(TextCol("MfgOrder.TotalCost", nameof(MfgOrder.TotalCost), 120, numeric: true));
        _grid.Columns.Add(TextCol("Vouchers.Status", nameof(MfgOrder.Status), 100, center: true));
        _grid.MouseDoubleClick += (_, _) => OpenSelected();
        root.Children.Add(_grid);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("MfgOrders.ListTitle");
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _openButton, _newButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (AppHost.Manufacturing is not { } service)
            {
                _statusBar.Text = T("MfgOrders.ServiceUnavailable");
                return;
            }
            var rows = await service.GetAllAsync(CurrentCompanyCode);
            _orders.Clear();
            _orders.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _orders;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateStatusBar()
    {
        var postedTotal = _orders.Where(o => o.Status == MfgOrderStatuses.Posted).Sum(o => o.TotalCost);
        _statusBar.Text = $"{_orders.Count} · {T("MfgOrder.TotalCost")}: {postedTotal:N2}";
    }

    private void OpenSelected()
    {
        if (_grid.SelectedItem is not MfgOrder order)
        {
            MessageBox.Show(this, T("MfgOrders.SelectOrder"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var win = new MfgWin { Owner = this };
        win.Show();
        win.LoadOrder(order.DocNo);
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
            Binding = new Binding(path) { StringFormat = path == nameof(MfgOrder.DocDate) ? "yyyy-MM-dd" : null },
            Width = new DataGridLength(width)
        };
        if (numeric) column.ElementStyle = RightAlignStyle;
        else if (center) column.ElementStyle = CenterAlignStyle;
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
        button.MinWidth = 92;
        button.MinHeight = 32;
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Padding = new Thickness(12, 3, 12, 3);
    }
}
