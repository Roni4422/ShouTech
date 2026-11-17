using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Manufacturing order. Fully persisted through <see cref="CardStore"/>
/// (migration 050: manufacturing_orders + manufacturing_order_lines):
/// the header and EVERY raw-material schedule row round-trip to the
/// database, and Post consumes the raw materials and receives the
/// finished good in material_stock_movements / material_stock_balances
/// inside one transaction (see CardStore.PostOrder).
/// </summary>
public sealed class MfgWin : BaseWin
{
    private readonly TextBox _txtDocNo = new() { Width = 110, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly TextBox _txtItem = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtItemName = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtQty = new() { Width = 80, Height = 26, Text = "1" };
    private readonly TextBox _txtUnit = new() { Width = 70, Height = 26, Text = "PCS" };
    private readonly TextBox _txtWhs = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtOverheads = new() { Width = 90, Height = 26, Text = "0.00" };

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnAddItem = new();
    private readonly Button _btnRemItem = new();
    private readonly Button _btnPrint = new();

    private readonly TextBlock _lblTotCost = new();
    private readonly TextBlock _lblUnitCost = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<MfgItemVm> _items = new();

    private long? _loadedId;
    private string _status = "Draft";

    public MfgWin()
    {
        Width = 920;
        Height = 560;
        MinWidth = 800;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetOrder();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "Vouchers.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "MfgOrder.Posted");
        StyleButton(_btnAddItem, "Invoice.AddLine");
        StyleButton(_btnRemItem, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");

        _btnNew.Click += (_, _) => ResetOrder();
        _btnLoad.Click += (_, _) => LoadOrder();
        _btnSave.Click += (_, _) => SaveOrder();
        _btnPost.Click += (_, _) => PostOrder();
        _btnPrint.Click += (_, _) => PrintOrder();

        _btnAddItem.Click += (_, _) =>
        {
            var item = new MfgItemVm { LineNo = _items.Count + 1, Unit = "KG", Qty = 1, UnitCost = 10 };
            _items.Add(item);
            _grid.SelectedItem = item;
            _grid.ScrollIntoView(item);
            CalculateCost();
        };

        _btnRemItem.Click += (_, _) =>
        {
            if (_grid.SelectedItem is MfgItemVm selected && _items.Count > 1)
            {
                _items.Remove(selected);
                Renumber();
                CalculateCost();
            }
        };

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnPost, _btnAddItem, _btnRemItem, _btnPrint })
            toolbar.Children.Add(b);

        // Explorer-style bottom command bar: flat, transparent, subtle 1px top divider.
        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBar, Dock.Bottom);

        // 2. Footer
        var footer = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var footerContent = new DockPanel();
        DockPanel.SetDock(_lblTotCost, Dock.Left);
        DockPanel.SetDock(_lblUnitCost, Dock.Right);
        footerContent.Children.Add(_lblTotCost);
        footerContent.Children.Add(_lblUnitCost);
        footer.Child = footerContent;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        // 3. Body
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        headerPanel.Children.Add(Labeled("MfgOrder.OrderNo", _txtDocNo));
        headerPanel.Children.Add(Labeled("MfgOrder.Date", _dtDate));
        headerPanel.Children.Add(Labeled("MfgOrder.TargetMaterial", _txtItem));
        headerPanel.Children.Add(Labeled("Invoice.Col.Description", _txtItemName));
        headerPanel.Children.Add(Labeled("MfgOrder.Quantity", _txtQty));
        headerPanel.Children.Add(Labeled("MfgOrder.Unit", _txtUnit));
        headerPanel.Children.Add(Labeled("Invoice.Warehouse", _txtWhs));
        headerPanel.Children.Add(Labeled("MfgOrder.ExtraCosts", _txtOverheads));

        Grid.SetRow(headerPanel, 0);
        body.Children.Add(headerPanel);

        // Setup DataGrid
        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");

        _grid.ItemsSource = _items;
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.LineNo), 45, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.ItemCode), 120));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.ItemName), 230));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.Unit), 70));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.QtyText), 90));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.CostText), 100));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Binding = new Binding(nameof(MfgItemVm.TotalCostText)) { Mode = BindingMode.OneWay },
            Width = 110,
            IsReadOnly = true
        });

        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(CalculateCost);

        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private void Renumber()
    {
        for (var i = 0; i < _items.Count; i++)
            _items[i].LineNo = i + 1;
    }

    private void ResetOrder()
    {
        _loadedId = null;
        _status = "Draft";
        _txtDocNo.Text = "MO-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        _dtDate.SelectedDate = DateTime.Today;
        _txtItem.Text = string.Empty;
        _txtItemName.Text = string.Empty;
        _txtQty.Text = "1";
        _txtUnit.Text = "PCS";
        _txtWhs.Text = "WH-01";
        _txtOverheads.Text = "0.00";
        _items.Clear();
        _items.Add(new MfgItemVm { LineNo = 1, Unit = "KG", Qty = 1, UnitCost = 10 });
        SetPostedUi(false);
        CalculateCost();
    }

    private void SetPostedUi(bool posted)
    {
        _btnSave.IsEnabled = !posted;
        _btnPost.IsEnabled = !posted;
        _btnAddItem.IsEnabled = !posted;
        _btnRemItem.IsEnabled = !posted;
        _grid.IsReadOnly = posted;
    }

    private void LoadOrder()
    {
        try
        {
            var rec = CardStore.LoadOrder(_txtDocNo.Text);
            if (rec is null)
            {
                MessageBox.Show(this, T("Cards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _loadedId = rec.Id;
            _status = rec.Status;
            _txtDocNo.Text = rec.OrderNo;
            _dtDate.SelectedDate = rec.OrderDate;
            _txtItem.Text = rec.FinishedMaterialCode;
            _txtItemName.Text = rec.FinishedMaterialName;
            _txtQty.Text = rec.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
            _txtUnit.Text = rec.UnitCode;
            _txtWhs.Text = rec.WarehouseCode;
            _txtOverheads.Text = rec.OverheadCost.ToString("0.00", CultureInfo.InvariantCulture);
            _items.Clear();
            foreach (var line in rec.Lines)
            {
                _items.Add(new MfgItemVm
                {
                    LineNo = line.LineNo,
                    ItemCode = line.MaterialCode,
                    ItemName = line.MaterialName,
                    Unit = line.UnitCode,
                    Qty = line.Quantity,
                    UnitCost = line.UnitCost
                });
            }
            SetPostedUi(string.Equals(_status, "Posted", StringComparison.OrdinalIgnoreCase));
            CalculateCost();
        }
        catch (Exception ex)
        {
            ReportFailure("LoadOrder", ex);
        }
    }

    /// <summary>Builds the persistence record from the current UI, resolving material/warehouse codes.</summary>
    private CardStore.MfgOrderRec CaptureOrder()
    {
        // Commit any in-progress grid cell edit so the schedule values
        // below are the values the user actually sees.
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);

        var orderNo = _txtDocNo.Text.Trim();
        if (string.IsNullOrWhiteSpace(orderNo))
            throw new InvalidOperationException(T("Cards.CodeRequired"));
        if (_dtDate.SelectedDate is not DateTime date)
            throw new InvalidOperationException(T("Cards.InvalidAmount"));

        var qty = ParseDec(_txtQty.Text);
        if (qty <= 0)
            throw new InvalidOperationException(T("Cards.InvalidAmount"));
        var overheads = ParseDec(_txtOverheads.Text);

        var finished = CardStore.ResolveMaterial(_txtItem.Text);
        var warehouseId = CardStore.ResolveWarehouseId(_txtWhs.Text);

        var lines = _items.Select(i =>
        {
            var material = CardStore.ResolveMaterial(i.ItemCode);
            return new CardStore.MfgLineRec(
                i.LineNo, material?.Id, i.ItemCode.Trim(), i.ItemName.Trim(),
                i.Unit.Trim(), i.Qty, i.UnitCost);
        }).ToList();

        var total = lines.Sum(l => l.Quantity * l.UnitCost) + overheads;
        return new CardStore.MfgOrderRec(
            _loadedId ?? 0, orderNo, date, finished?.Id,
            _txtItem.Text.Trim(),
            string.IsNullOrWhiteSpace(_txtItemName.Text) ? finished?.Name ?? string.Empty : _txtItemName.Text.Trim(),
            qty, _txtUnit.Text.Trim(), warehouseId, _txtWhs.Text.Trim(),
            overheads, total, qty > 0 ? total / qty : 0, _status, lines);
    }

    private void SaveOrder()
    {
        try
        {
            var permission = _loadedId is null ? PermCat.Manufacturing.OrderCreate : PermCat.Manufacturing.OrderUpdate;
            if (!AppHost.Permissions.Has(permission))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));
            var rec = CaptureOrder();
            _loadedId = CardStore.SaveOrder(rec, Actor());
            MessageBox.Show(this, T("MfgOrder.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveOrder", ex);
        }
    }

    private void PostOrder()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Manufacturing.ProductionPost))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));
            if (_loadedId is null)
            {
                SaveOrder();
                if (_loadedId is null) return;
            }
            CardStore.PostOrder(_loadedId.Value, Actor());
            _status = "Posted";
            SetPostedUi(true);
            MessageBox.Show(this, T("MfgOrder.Posted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("PostOrder", ex);
        }
    }

    private void CalculateCost()
    {
        decimal.TryParse(_txtOverheads.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var overheads);
        decimal.TryParse(_txtQty.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var targetQty);
        if (targetQty <= 0) targetQty = 1;

        var rawMaterialsTotal = _items.Sum(i => i.TotalCost);
        var grandTotal = rawMaterialsTotal + overheads;
        var unitCost = grandTotal / targetQty;

        _lblTotCost.Text = $"{T("MfgOrder.TotalCost")}: {grandTotal:N2} SAR";
        _lblTotCost.FontWeight = FontWeights.Bold;
        _lblUnitCost.Text = $"{T("MfgOrder.UnitCost")}: {unitCost:N2} SAR";
    }

    private static decimal ParseDec(string text)
        => decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static string Actor()
        => !string.IsNullOrWhiteSpace(UsrSession.Current?.Id)
            ? UsrSession.Current!.Id
            : AppHost.Session.CurrentUser ?? string.Empty;

    private void ReportFailure(string operation, Exception exception)
    {
        AppHost.Logging?.LogError("WindowPersistence",
            $"Window={GetType().Name} | Operation={operation} | {exception.Message}", exception);
        MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Prints the current order on an A4 page (same pattern as JrnWin.PrintVoucher).</summary>
    private void PrintOrder()
    {
        var page = BuildPrintPage();
        var paginator = new SinglePagePaginator(page);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, string.IsNullOrWhiteSpace(_txtDocNo.Text) ? T("MfgOrder.Title") : _txtDocNo.Text.Trim());
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
            Text = T("MfgOrder.Title"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 10)
        });

        void Meta(string labelKey, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            row.Children.Add(new TextBlock { Text = T(labelKey) + ": ", FontWeight = FontWeights.SemiBold, FontSize = 12 });
            row.Children.Add(new TextBlock { Text = value, FontSize = 12 });
            stack.Children.Add(row);
        }

        Meta("MfgOrder.OrderNo", _txtDocNo.Text.Trim());
        Meta("MfgOrder.Date", (_dtDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Meta("MfgOrder.TargetMaterial", _txtItem.Text.Trim() + " " + _txtItemName.Text.Trim());
        Meta("MfgOrder.Quantity", _txtQty.Text.Trim() + " " + _txtUnit.Text.Trim());
        Meta("Invoice.Warehouse", _txtWhs.Text.Trim());

        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var width in new[] { 36, 120, 260, 70, 90, 100, 110 })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });

        void Cell(int row, int col, string text, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(3, 2, 3, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            table.Children.Add(tb);
        }

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, "#", true);
        Cell(0, 1, T("Invoice.Col.Code"), true);
        Cell(0, 2, T("Invoice.Col.Description"), true);
        Cell(0, 3, T("MfgOrder.Unit"), true);
        Cell(0, 4, T("MfgOrder.Quantity"), true);
        Cell(0, 5, T("Invoice.Col.PurchasePrice"), true);
        Cell(0, 6, T("Invoice.Col.LineTotal"), true);

        var rowIndex = 1;
        foreach (var item in _items)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rowIndex, 0, item.LineNo.ToString(CultureInfo.InvariantCulture));
            Cell(rowIndex, 1, item.ItemCode);
            Cell(rowIndex, 2, item.ItemName);
            Cell(rowIndex, 3, item.Unit);
            Cell(rowIndex, 4, item.Qty.ToString("0.###", CultureInfo.InvariantCulture));
            Cell(rowIndex, 5, item.UnitCost.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 6, item.TotalCost.ToString("N2", CultureInfo.InvariantCulture));
            rowIndex++;
        }

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(rowIndex, 2, T("MfgOrder.TotalCost"), true);
        Cell(rowIndex, 6, _items.Sum(i => i.TotalCost).ToString("N2", CultureInfo.InvariantCulture), true);

        stack.Children.Add(table);
        return root;
    }

    /// <summary>Single-page paginator, mirroring the one inside JrnWin/InvPrn.</summary>
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

    protected override void ApplyLocalizedText()
    {
        Title = T("MfgOrder.Title");
        if (_grid.Columns.Count >= 7)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("Invoice.Col.Code");
            _grid.Columns[2].Header = T("Invoice.Col.Description");
            _grid.Columns[3].Header = T("MfgOrder.Unit");
            _grid.Columns[4].Header = T("MfgOrder.Quantity");
            _grid.Columns[5].Header = T("Invoice.Col.PurchasePrice");
            _grid.Columns[6].Header = T("Invoice.Col.LineTotal");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        CalculateCost();
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(VisualTreeHelper.GetChild(parent, i));
    }

    private static StackPanel Labeled(string key, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 2), Opacity = 0.85 });
        if (control is TextBox textBox)
            textBox.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        sp.Children.Add(control);
        return sp;
    }

    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 5, 0);
        btn.Padding = new Thickness(9, 3, 9, 3);
        btn.MinWidth = 76;
        btn.Height = 28;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private static DataGridTextColumn TextCol(string path, double width, bool readOnly = false)
        => new()
        {
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width,
            IsReadOnly = readOnly
        };

    private sealed class MfgItemVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _itemCode = "", _itemName = "", _unit = "";
        private decimal _qty = 1, _cost;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(); } }
        public string ItemName { get => _itemName; set { _itemName = value; OnChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnChanged(); } }

        public decimal Qty { get => _qty; set { _qty = value; OnChanged(nameof(QtyText)); OnChanged(nameof(TotalCostText)); } }
        public decimal UnitCost { get => _cost; set { _cost = value; OnChanged(nameof(CostText)); OnChanged(nameof(TotalCostText)); } }

        public decimal TotalCost => _qty * _cost;
        public string TotalCostText => TotalCost.ToString("N2", CultureInfo.InvariantCulture);

        public string QtyText
        {
            get => _qty.ToString("0.###", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Qty = v; }
        }

        public string CostText
        {
            get => _cost.ToString("0.####", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) UnitCost = v; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
