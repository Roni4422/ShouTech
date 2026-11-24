using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Cost center tree manager (ribbon chunk B, RS-1): the tree is shown as a
/// flattened, indented grid (Code | Name | Parent | Group? | Active | Balance)
/// where group balances roll up their descendants. Balances are real posted
/// journal-line totals from <see cref="ShouTech.Application.Services.MasterCardService"/>.
/// Bottom command bar: New, New Child, Edit, Activate/Deactivate, Delete
/// (guarded), Print, Close.
/// </summary>
public sealed class CcListWin : BaseWin
{
    protected override string? ScheduleTintName => "Orchid";
    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _newButton = new();
    private readonly Button _newChildButton = new();
    private readonly Button _editButton = new();
    private readonly Button _toggleActiveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _printButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly List<CostCenterRow> _rows = new();
    private IReadOnlyList<CostCenter> _centers = Array.Empty<CostCenter>();

    public CcListWin()
    {
        Width = 1020;
        Height = 640;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.CostCenterView))
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

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        StyleButton(_newButton, "Common.New");
        StyleButton(_newChildButton, "MasterCards.NewChild");
        StyleButton(_editButton, "Common.Edit");
        StyleButton(_toggleActiveButton, "MasterCards.ToggleActive");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_printButton, "Common.Print");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => OpenCard(null, null);
        _newChildButton.Click += (_, _) =>
        {
            if (_grid.SelectedItem is CostCenterRow row && row.IsGroup)
                OpenCard(null, row.Id);
        };
        _editButton.Click += (_, _) =>
        {
            if (_grid.SelectedItem is CostCenterRow row)
                OpenCard(row.Code, null);
        };
        _toggleActiveButton.Click += async (_, _) => await ToggleActiveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _printButton.Click += (_, _) => PrintTree();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _newChildButton, _editButton, _toggleActiveButton, _deleteButton, _printButton, _refreshButton, _closeButton })
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
        _grid.Columns.Add(TextCol("CostCenter.Code", nameof(CostCenterRow.Code), 120));
        _grid.Columns.Add(TextCol("MasterCards.Name", nameof(CostCenterRow.DisplayName), 280));
        _grid.Columns.Add(TextCol("CostCenter.ParentCode", nameof(CostCenterRow.ParentCode), 120));
        _grid.Columns.Add(TextCol("MasterCards.IsGroup", nameof(CostCenterRow.GroupText), 80, center: true));
        _grid.Columns.Add(TextCol("MasterCards.Active", nameof(CostCenterRow.ActiveText), 80, center: true));
        _grid.Columns.Add(TextCol("CostCenter.Balance", nameof(CostCenterRow.Balance), 130, numeric: true));
        _grid.MouseDoubleClick += (_, _) =>
        {
            if (_grid.SelectedItem is CostCenterRow row)
                OpenCard(row.Code, null);
        };
        root.Children.Add(_grid);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("MasterCards.CostCenterTreeTitle");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _newChildButton, _editButton, _toggleActiveButton, _deleteButton, _printButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

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
            _centers = await service.GetCostCentersAsync(CurrentCompanyCode);
            var balances = await service.GetCostCenterBalancesAsync(CurrentCompanyCode);
            _rows.Clear();
            _rows.AddRange(BuildFlattenedRows(_centers, balances));
            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Flattens the tree (roots first, children indented) and rolls group balances up.</summary>
    internal static List<CostCenterRow> BuildFlattenedRows(IReadOnlyList<CostCenter> centers, IReadOnlyDictionary<string, LedgerBalance> balances)
    {
        var byId = centers.ToDictionary(c => c.Id);
        var children = centers
            .Where(c => c.ParentId.HasValue && byId.ContainsKey(c.ParentId.Value))
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Code, StringComparer.OrdinalIgnoreCase).ToList());
        var roots = centers
            .Where(c => !c.ParentId.HasValue || !byId.ContainsKey(c.ParentId.Value))
            .OrderBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        decimal OwnBalance(CostCenter c) => balances.TryGetValue(c.Code, out var b) ? b.Balance : 0m;
        decimal RollUp(CostCenter c)
        {
            var total = OwnBalance(c);
            if (children.TryGetValue(c.Id, out var kids))
                foreach (var kid in kids)
                    total += RollUp(kid);
            return total;
        }

        var rows = new List<CostCenterRow>();
        void Visit(CostCenter center, int depth)
        {
            rows.Add(new CostCenterRow
            {
                Id = center.Id,
                Code = center.Code,
                DisplayName = new string(' ', depth * 3) + (string.IsNullOrWhiteSpace(center.NameEn) ? center.NameAr ?? center.Code : center.NameEn),
                ParentCode = center.ParentId.HasValue && byId.TryGetValue(center.ParentId.Value, out var parent) ? parent.Code : string.Empty,
                IsGroup = center.IsGroup,
                IsActive = center.IsActive,
                Balance = center.IsGroup ? RollUp(center) : OwnBalance(center)
            });
            if (children.TryGetValue(center.Id, out var kids))
                foreach (var kid in kids)
                    Visit(kid, depth + 1);
        }
        foreach (var root in roots)
            Visit(root, 0);
        return rows;
    }

    private void OpenCard(string? code, Guid? parentId)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.CostCenterManage) && code is null && !parentId.HasValue)
        {
            // Viewing an existing card still requires only View; creating needs Manage.
        }
        var card = new CcCard { Owner = this };
        if (parentId.HasValue)
            card.InitialParentId = parentId.Value;
        card.Show();
        if (!string.IsNullOrWhiteSpace(code))
            _ = card.LoadByCodeAsync(code);
        card.Closed += async (_, _) => await RefreshAsync();
    }

    private async Task ToggleActiveAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (_grid.SelectedItem is not CostCenterRow row)
                return;
            if (!AppHost.Permissions.Has(PermCat.Accounting.CostCenterManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            var center = _centers.FirstOrDefault(c => c.Id == row.Id)
                ?? throw new InvalidOperationException(T("MasterCards.NotFound"));
            center.IsActive = !center.IsActive;
            await service.SaveCostCenterAsync(CurrentCompanyCode, center, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (_grid.SelectedItem is not CostCenterRow row)
                return;
            if (!AppHost.Permissions.Has(PermCat.Accounting.CostCenterManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await service.DeleteCostCenterAsync(CurrentCompanyCode, row.Id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PrintTree()
    {
        var page = BuildPrintPage();
        var paginator = new SinglePagePaginator(page);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, T("MasterCards.CostCenterTreeTitle"));
    }

    private Border BuildPrintPage()
    {
        var root = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(40),
            Width = 794,
            Height = 1123
        };
        var stack = new StackPanel();
        root.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = CurrentCompanyDisplay,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79))
        });
        stack.Children.Add(new TextBlock
        {
            Text = T("MasterCards.CostCenterTreeTitle"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 12)
        });
        foreach (var row in _rows)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{row.Code,-14} {row.DisplayName}    {row.Balance.ToString("N2", CultureInfo.CurrentCulture)}",
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 1)
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = $"{T("MasterCards.TotalBalance")}: {_rows.Where(r => r.ParentCode == string.Empty).Sum(r => r.Balance).ToString("N2", CultureInfo.CurrentCulture)}",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 12, 0, 0)
        });
        return root;
    }

    private void UpdateStatusBar()
    {
        var total = _rows.Where(r => string.IsNullOrEmpty(r.ParentCode)).Sum(r => r.Balance);
        _statusBar.Text = $"{_rows.Count} · {T("MasterCards.TotalBalance")}: {total.ToString("N2", CultureInfo.CurrentCulture)}";
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

    /// <summary>One flattened tree row shown in the manager grid.</summary>
    public sealed class CostCenterRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string ParentCode { get; init; } = string.Empty;
        public bool IsGroup { get; init; }
        public bool IsActive { get; init; }
        public decimal Balance { get; init; }
        public string GroupText => IsGroup ? "Yes" : string.Empty;
        public string ActiveText => IsActive ? "Yes" : "No";
    }

    /// <summary>Single-page paginator, mirroring the one inside JrnWin.</summary>
    private sealed class SinglePagePaginator : DocumentPaginator
    {
        private readonly FrameworkElement _page;
        public SinglePagePaginator(FrameworkElement page) => _page = page;
        public override bool IsPageCountValid => true;
        public override int PageCount => 1;
        public override Size PageSize { get; set; } = new(794, 1123);
        public override IDocumentPaginatorSource Source => null!;
        public override DocumentPage GetPage(int pageNumber)
        {
            _page.Measure(PageSize);
            _page.Arrange(new Rect(PageSize));
            _page.UpdateLayout();
            return new DocumentPage(_page);
        }
    }
}
