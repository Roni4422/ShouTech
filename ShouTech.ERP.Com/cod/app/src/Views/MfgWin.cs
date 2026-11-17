using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Manufacturing order (RS-8): the order carries its own raw-material lines
/// (ShouTech has no BOM master). Save persists a draft through
/// MfgOrderService; Post consumes the raw lines and produces the finished
/// item through material_stock_movements in one repository transaction,
/// computing TotalCost = sum of raw amounts + overhead and
/// UnitCost = TotalCost / Qty. Posted orders are read-only; Cancel of a
/// posted order reverses its stock movements. Print follows the JrnWin
/// A4 pattern.
/// </summary>
public sealed class MfgWin : BaseWin
{
    protected override string? ScheduleTintName => "Green";

    private readonly TextBox _txtDocNo = new();
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today };
    private readonly TextBox _txtItem = new();
    private readonly TextBox _txtItemName = new();
    private readonly TextBox _txtQty = new() { Text = "1" };
    private readonly TextBox _txtUnit = new() { Text = "PCS" };
    private readonly TextBox _txtWhs = new();
    private readonly TextBox _txtOverheads = new() { Text = "0.00" };
    private readonly TextBox _txtStdCost = new() { Text = "0.00" };
    private readonly TextBox _txtNotes = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnCancelOrder = new();
    private readonly Button _btnAddItem = new();
    private readonly Button _btnRemItem = new();
    private readonly Button _btnPrint = new();

    private readonly TextBlock _lblTotCost = new();
    private readonly TextBlock _lblUnitCost = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<MfgItemVm> _items = new();
    private Guid? _currentId;
    private string _status = MfgOrderStatuses.Draft;

    public MfgWin()
    {
        Width = 920;
        Height = 580;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        ApplyLocalizedText();
        ResetOrder();
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
        if (!_currentId.HasValue)
            await RefreshNextDocNoAsync();
    }

    private async Task RefreshNextDocNoAsync()
    {
        try
        {
            if (AppHost.Manufacturing is { } service && !string.IsNullOrWhiteSpace(CurrentCompanyCode))
                _txtDocNo.Text = (await service.GetNextDocNoAsync(CurrentCompanyCode)).ToString(CultureInfo.InvariantCulture);
        }
        catch { /* the service validates again on save; a stale number is corrected there */ }
    }

    /// <summary>Loads an existing order by its per-company document number (used by MfgListWin).</summary>
    public async void LoadOrder(int docNo)
    {
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            var order = await service.GetByDocNoAsync(CurrentCompanyCode, docNo)
                ?? throw new InvalidOperationException($"{T("MfgOrders.NotFound")}: {docNo}");
            _currentId = order.Id;
            _status = order.Status;
            _txtDocNo.Text = order.DocNo.ToString(CultureInfo.InvariantCulture);
            _dtDate.SelectedDate = order.DocDate.ToDateTime(TimeOnly.MinValue);
            _txtItem.Text = order.ItemCode;
            _txtItemName.Text = order.ItemName ?? string.Empty;
            _txtQty.Text = order.Qty.ToString("0.###", CultureInfo.InvariantCulture);
            _txtUnit.Text = order.UnitCode;
            _txtWhs.Text = order.WarehouseCode ?? string.Empty;
            _txtOverheads.Text = order.OverheadAmount.ToString("0.00", CultureInfo.InvariantCulture);
            _txtStdCost.Text = order.StandardUnitCost.ToString("0.00", CultureInfo.InvariantCulture);
            _txtNotes.Text = order.Notes ?? string.Empty;
            _items.Clear();
            var lineNo = 0;
            foreach (var line in order.Lines)
                _items.Add(new MfgItemVm
                {
                    LineNo = ++lineNo,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName ?? string.Empty,
                    Unit = line.UnitCode ?? string.Empty,
                    Qty = line.Qty,
                    UnitCost = line.UnitCost,
                    StandardUnitCost = line.StandardUnitCost
                });
            var readOnly = _status != MfgOrderStatuses.Draft;
            _btnSave.IsEnabled = !readOnly;
            _btnPost.IsEnabled = _status == MfgOrderStatuses.Draft;
            _btnCancelOrder.IsEnabled = _status != MfgOrderStatuses.Cancelled;
            _grid.IsReadOnly = readOnly;
            CalculateCost();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Vouchers.Post");
        StyleButton(_btnCancelOrder, "MfgOrders.CancelOrder");
        StyleButton(_btnAddItem, "Invoice.AddLine");
        StyleButton(_btnRemItem, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");

        _btnNew.Click += (_, _) => { ResetOrder(); _ = RefreshNextDocNoAsync(); };
        _btnSave.Click += async (_, _) => await SaveOrderAsync();
        _btnPost.Click += async (_, _) => await PostOrderAsync();
        _btnCancelOrder.Click += async (_, _) => await CancelOrderAsync();
        _btnPrint.Click += (_, _) => PrintOrder();
        _btnAddItem.Click += (_, _) =>
        {
            var item = new MfgItemVm { LineNo = _items.Count + 1, Unit = "KG", Qty = 1, UnitCost = 0 };
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

        foreach (var b in new[] { _btnNew, _btnSave, _btnPost, _btnCancelOrder, _btnAddItem, _btnRemItem, _btnPrint })
            toolbar.Children.Add(b);

        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBar, Dock.Bottom);

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
        DockPanel.SetDock(_lblStatus, Dock.Right);
        footerContent.Children.Add(_lblTotCost);
        footerContent.Children.Add(_lblStatus);
        var footerStack = new StackPanel();
        footerStack.Children.Add(footerContent);
        footerStack.Children.Add(_lblUnitCost);
        footer.Child = footerStack;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header = one shared column grid (4 pairs), side labels, edges
        // aligned across rows; the memo spans the last three pairs
        // (Phenix rule).
        var headerForm = CreateFormGrid(4);
        headerForm.Margin = new Thickness(0, 0, 0, 8);
        AddFormField(headerForm, 0, 0, "MfgOrder.OrderNo", _txtDocNo);
        AddFormField(headerForm, 0, 1, "MfgOrder.Date", _dtDate);
        AddFormField(headerForm, 0, 2, "MfgOrder.TargetMaterial", _txtItem);
        AddFormField(headerForm, 0, 3, "Invoice.Col.Description", _txtItemName);
        AddFormField(headerForm, 1, 0, "MfgOrder.Quantity", _txtQty);
        AddFormField(headerForm, 1, 1, "MfgOrder.Unit", _txtUnit);
        AddFormField(headerForm, 1, 2, "Invoice.Warehouse", _txtWhs);
        AddFormField(headerForm, 1, 3, "MfgOrder.ExtraCosts", _txtOverheads);
        AddFormField(headerForm, 2, 0, "MfgOrders.StandardUnitCost", _txtStdCost);
        AddFormField(headerForm, 2, 1, "Vouchers.Memo", _txtNotes, spanPairs: 3);
        Grid.SetRow(headerForm, 0);
        body.Children.Add(headerForm);

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
        _txtQty.TextChanged += (_, _) => CalculateCost();
        _txtOverheads.TextChanged += (_, _) => CalculateCost();
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
        _currentId = null;
        _status = MfgOrderStatuses.Draft;
        _txtDocNo.Text = "0";
        _dtDate.SelectedDate = DateTime.Today;
        _txtItem.Text = string.Empty;
        _txtItemName.Text = string.Empty;
        _txtQty.Text = "1";
        _txtUnit.Text = "PCS";
        _txtWhs.Text = string.Empty;
        _txtOverheads.Text = "0.00";
        _txtStdCost.Text = "0.00";
        _txtNotes.Text = string.Empty;
        _items.Clear();
        _items.Add(new MfgItemVm { LineNo = 1, Unit = "KG", Qty = 1, UnitCost = 0 });
        _btnSave.IsEnabled = true;
        _btnPost.IsEnabled = true;
        _btnCancelOrder.IsEnabled = false;
        _grid.IsReadOnly = false;
        CalculateCost();
    }

    private static decimal ParseDecimal(TextBox box)
        => decimal.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ||
           decimal.TryParse(box.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out v) ? v : 0m;

    private void CalculateCost()
    {
        var overheads = ParseDecimal(_txtOverheads);
        var targetQty = ParseDecimal(_txtQty);
        if (targetQty <= 0) targetQty = 1;
        var rawTotal = _items.Sum(i => i.TotalCost);
        var grandTotal = rawTotal + overheads;
        var unitCost = grandTotal / targetQty;
        _lblTotCost.Text = $"{T("MfgOrder.TotalCost")}: {grandTotal:N2}";
        _lblTotCost.FontWeight = FontWeights.Bold;
        _lblUnitCost.Text = $"{T("MfgOrder.UnitCost")}: {unitCost:N2}";
        _lblStatus.Text = $"{T("Invoice.Status")}: {_status}";
    }

    private MfgOrder BuildOrder()
    {
        var order = new MfgOrder
        {
            Id = _currentId ?? Guid.NewGuid(),
            DocNo = int.TryParse(_txtDocNo.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var docNo) ? docNo : 0,
            DocDate = DateOnly.FromDateTime(_dtDate.SelectedDate ?? DateTime.Today),
            ItemCode = _txtItem.Text.Trim(),
            ItemName = string.IsNullOrWhiteSpace(_txtItemName.Text) ? null : _txtItemName.Text.Trim(),
            Qty = ParseDecimal(_txtQty),
            UnitCode = string.IsNullOrWhiteSpace(_txtUnit.Text) ? "PCS" : _txtUnit.Text.Trim(),
            WarehouseCode = string.IsNullOrWhiteSpace(_txtWhs.Text) ? null : _txtWhs.Text.Trim(),
            OverheadAmount = ParseDecimal(_txtOverheads),
            StandardUnitCost = ParseDecimal(_txtStdCost),
            Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim(),
            Status = _status
        };
        foreach (var item in _items.Where(i => i.Qty > 0 || !string.IsNullOrWhiteSpace(i.ItemCode)))
            order.Lines.Add(new MfgOrderLine
            {
                OrderId = order.Id,
                LineKind = MfgLineKinds.Raw,
                ItemCode = item.ItemCode.Trim(),
                ItemName = string.IsNullOrWhiteSpace(item.ItemName) ? null : item.ItemName.Trim(),
                Qty = item.Qty,
                UnitCode = string.IsNullOrWhiteSpace(item.Unit) ? null : item.Unit.Trim(),
                UnitCost = item.UnitCost,
                StandardUnitCost = item.StandardUnitCost
            });
        return order;
    }

    private async Task SaveOrderAsync()
    {
        if (!AppHost.Permissions.Has(_currentId.HasValue ? PermCat.Manufacturing.OrderUpdate : PermCat.Manufacturing.OrderCreate))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            if (string.IsNullOrWhiteSpace(CurrentCompanyCode))
                throw new InvalidOperationException(T("MfgOrders.NoCompany"));
            if (string.IsNullOrWhiteSpace(CurrentUserId))
                throw new InvalidOperationException(T("MfgOrders.NoUser"));
            var order = BuildOrder();
            await service.SaveDraftAsync(CurrentCompanyCode, order, CurrentUserId);
            _currentId = order.Id;
            _txtDocNo.Text = order.DocNo.ToString(CultureInfo.InvariantCulture);
            CalculateCost();
            MessageBox.Show(this, T("MfgOrder.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveDraft", ex);
        }
    }

    private async Task PostOrderAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.ProductionPost))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            if (!_currentId.HasValue)
            {
                await SaveOrderAsync();
                if (!_currentId.HasValue) return;
            }
            if (!AskConfirm("MfgOrders.ConfirmPost")) return;
            await service.PostAsync(CurrentCompanyCode, _currentId.Value, CurrentUserId);
            _status = MfgOrderStatuses.Posted;
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = false;
            _btnCancelOrder.IsEnabled = true;
            _grid.IsReadOnly = true;
            CalculateCost();
            MessageBox.Show(this, T("MfgOrder.Posted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Post", ex);
        }
    }

    private async Task CancelOrderAsync()
    {
        if (!_currentId.HasValue) return;
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.OrderCancel))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!AskConfirm("MfgOrders.ConfirmCancel")) return;
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            await service.CancelAsync(CurrentCompanyCode, _currentId.Value, CurrentUserId);
            _status = MfgOrderStatuses.Cancelled;
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = false;
            _btnCancelOrder.IsEnabled = false;
            _grid.IsReadOnly = true;
            CalculateCost();
        }
        catch (Exception ex)
        {
            ReportFailure("Cancel", ex);
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        const string errorCode = "APP-2010";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var context = $"Operation={operation}; Window={GetType().Name}; Company={CurrentCompanyCode}";
        AppHost.Logging?.LogError("WindowPersistence",
            $"[{definition.Code}] {definition.Name} | {context} | {exception.Message}", exception);
        MessageBox.Show(this, $"[{definition.Code}] {exception.Message}", Title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------------------------
    // Print (JrnWin A4 pattern).
    // ------------------------------------------------------------------
    private void PrintOrder()
    {
        var root = new Border { Background = Brushes.White, Padding = new Thickness(40), Width = 794, Height = 1123 };
        var stack = new StackPanel();
        root.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = CurrentCompanyDisplay, FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79))
        });
        stack.Children.Add(new TextBlock
        {
            Text = T("MfgOrder.Title"), FontSize = 15, FontWeight = FontWeights.SemiBold,
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
        Meta("MfgOrder.TargetMaterial", $"{_txtItem.Text.Trim()} — {_txtItemName.Text.Trim()} x {_txtQty.Text.Trim()} {_txtUnit.Text.Trim()}");
        Meta("Invoice.Warehouse", _txtWhs.Text.Trim());
        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var width in new[] { 110, 200, 70, 90, 100, 110 })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        void Cell(int row, int col, string text, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 11, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(3, 2, 3, 2), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col); table.Children.Add(tb);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, T("Invoice.Col.Code"), true); Cell(0, 1, T("Invoice.Col.Description"), true);
        Cell(0, 2, T("MfgOrder.Unit"), true); Cell(0, 3, T("MfgOrder.Quantity"), true);
        Cell(0, 4, T("Invoice.Col.PurchasePrice"), true); Cell(0, 5, T("Invoice.Col.LineTotal"), true);
        var rowIndex = 1;
        foreach (var item in _items)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rowIndex, 0, item.ItemCode); Cell(rowIndex, 1, item.ItemName); Cell(rowIndex, 2, item.Unit);
            Cell(rowIndex, 3, item.Qty.ToString("0.###", CultureInfo.InvariantCulture));
            Cell(rowIndex, 4, item.UnitCost.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 5, item.TotalCost.ToString("N2", CultureInfo.InvariantCulture));
            rowIndex++;
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(rowIndex, 4, T("MfgOrder.TotalCost"), true);
        Cell(rowIndex, 5, (_items.Sum(i => i.TotalCost) + ParseDecimal(_txtOverheads)).ToString("N2", CultureInfo.InvariantCulture), true);
        stack.Children.Add(table);

        var paginator = new SinglePagePaginator(root);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, Title);
    }

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


    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 8, 0);
        btn.MinWidth = VisuId.ButtonMinWidth;
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
        private decimal _qty = 1, _cost, _stdCost;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(); } }
        public string ItemName { get => _itemName; set { _itemName = value; OnChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnChanged(); } }

        public decimal Qty { get => _qty; set { _qty = value; OnChanged(nameof(QtyText)); OnChanged(nameof(TotalCostText)); } }
        public decimal UnitCost { get => _cost; set { _cost = value; OnChanged(nameof(CostText)); OnChanged(nameof(TotalCostText)); } }
        public decimal StandardUnitCost { get => _stdCost; set { _stdCost = value; OnChanged(); } }

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
