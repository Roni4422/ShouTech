using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ShouTech.App.Reporting.Invoices;

namespace ShouTech.App.Views;

public enum OrdKind
{
    Sales,
    Purchase
}

/// <summary>
/// </summary>
public sealed class OrdWin : BaseWin
{
    /// <summary>Logs unavailable operations instead of reporting a false success.</summary>
    private void ShowOperationUnavailable(string operation)
    {
        const string errorCode = "APP-2011";
        var context = $"Operation={operation} is not implemented in window {GetType().Name}.";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var exception = new NotSupportedException(context);

        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowOperation",
            $"[{definition.Code}] {definition.Name} | Window={GetType().Name} | {context}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowOperation",
            "OperationUnavailable",
            $"Code={definition.Code} | Window={GetType().Name} | {context}");

        MessageBox.Show(
            this,
            $"[{definition.Code}] {definition.Message}\n{context}",
            "العملية غير متاحة",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }


    /// <summary>
    /// Prevents a false success message when this screen has no database persistence wired.
    /// This intentionally does not claim to save any values.
    /// </summary>
    private void ShowPersistenceUnavailable()
    {
        const string errorCode = "APP-2010";
        const string context = "This window has no database persistence implementation.";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var exception = new NotSupportedException(context);

        // Errors are routed to errors.log; the operational trace goes to log.log.
        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowPersistence",
            $"[{definition.Code}] {definition.Name} | Window={GetType().Name} | {context}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowPersistence",
            "PersistenceUnavailable",
            $"Code={definition.Code} | Window={GetType().Name}");

        MessageBox.Show(
            this,
            $"[{definition.Code}] {definition.Message}\n{context}\nلم يتم حفظ أي بيانات.",
            "العملية غير متاحة",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }


    private readonly OrdKind _kind;

    private readonly TextBox _txtDocNo = new() { Width = 110, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly DatePicker _dtDeliveryDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today.AddDays(7) };
    private readonly TextBox _txtPartyCode = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtPartyName = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtWhs = new() { Width = 110, Height = 26, Text = "WH-01" };
    private readonly TextBox _txtNotes = new() { Width = 380, Height = 26 };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnConvert = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemLine = new();
    private readonly Button _btnPrint = new();

    private readonly TextBlock _lblTotal = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<OrderLineVm> _lines = new();

    public OrdWin(OrdKind kind = OrdKind.Sales)
    {
        _kind = kind;
        Width = 880;
        Height = 560;
        MinWidth = 780;
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
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnConvert, "OrderDoc.ConvertToInvoice");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemLine, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");

        _btnNew.Click += (_, _) => ResetOrder();
        _btnSave.Click += (_, _) => ShowPersistenceUnavailable();
        _btnConvert.Click += (_, _) => ConvertToInvoice();
        _btnPrint.Click += (_, _) => ShowOperationUnavailable("Print");

        _btnAddLine.Click += (_, _) =>
        {
            var l = new OrderLineVm { LineNo = _lines.Count + 1, Unit = "PCS", Qty = 1, UnitPrice = 100 };
            _lines.Add(l);
            _grid.SelectedItem = l;
            _grid.ScrollIntoView(l);
            RefreshTotals();
        };

        _btnRemLine.Click += (_, _) =>
        {
            if (_grid.SelectedItem is OrderLineVm selected && _lines.Count > 1)
            {
                _lines.Remove(selected);
                Renumber();
                RefreshTotals();
            }
        };

        foreach (var b in new[] { _btnNew, _btnSave, _btnConvert, _btnAddLine, _btnRemLine, _btnPrint })
            toolbar.Children.Add(b);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

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
        DockPanel.SetDock(_lblTotal, Dock.Left);
        DockPanel.SetDock(_lblStatus, Dock.Right);
        footerContent.Children.Add(_lblTotal);
        footerContent.Children.Add(_lblStatus);
        footer.Child = footerContent;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // 3. Body
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        headerPanel.Children.Add(Labeled("OrderDoc.OrderNo", _txtDocNo));
        headerPanel.Children.Add(Labeled("OrderDoc.Date", _dtDate));
        headerPanel.Children.Add(Labeled("OrderDoc.DeliveryDate", _dtDeliveryDate));
        headerPanel.Children.Add(Labeled(_kind == OrdKind.Sales ? "Invoice.CustomerCode" : "Invoice.SupplierCode", _txtPartyCode));
        headerPanel.Children.Add(Labeled(_kind == OrdKind.Sales ? "Invoice.CustomerName" : "Invoice.SupplierName", _txtPartyName));
        headerPanel.Children.Add(Labeled("Invoice.Warehouse", _txtWhs));
        headerPanel.Children.Add(Labeled("Invoice.Notes", _txtNotes));

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

        _grid.ItemsSource = _lines;
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.LineNo), 45, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.ItemCode), 110));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.Description), 240));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.Unit), 70));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.QtyText), 90));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.PriceText), 100));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.DiscountText), 80));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Binding = new Binding(nameof(OrderLineVm.LineTotalText)) { Mode = BindingMode.OneWay },
            Width = 110,
            IsReadOnly = true
        });

        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshTotals);

        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private void Renumber()
    {
        for (var i = 0; i < _lines.Count; i++)
            _lines[i].LineNo = i + 1;
    }

    private void ResetOrder()
    {
        var prefix = _kind == OrdKind.Sales ? "SO-" : "PO-";
        _txtDocNo.Text = prefix + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        _dtDate.SelectedDate = DateTime.Today;
        _dtDeliveryDate.SelectedDate = DateTime.Today.AddDays(7);
        _txtPartyCode.Text = string.Empty;
        _txtPartyName.Text = string.Empty;
        _txtNotes.Text = string.Empty;
        _lines.Clear();
        _lines.Add(new OrderLineVm { LineNo = 1, Unit = "PCS", Qty = 1, UnitPrice = 100 });
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        var total = _lines.Sum(l => l.LineTotal);
        _lblTotal.Text = $"{T("Invoice.Total")}: {total:N2} SAR";
        _lblTotal.FontWeight = FontWeights.Bold;
        _lblStatus.Text = $"{T("OrderDoc.Status")}: {T("Invoice.StatusDraft")}";
    }

    private void ConvertToInvoice()
    {
        // An order is not persisted yet; opening a blank invoice is not a conversion.
        ShowOperationUnavailable("ConvertToInvoice");
    }

    protected override void ApplyLocalizedText()
    {
        Title = T(_kind == OrdKind.Sales ? "OrderDoc.TitleSales" : "OrderDoc.TitlePurchase");
        if (_grid.Columns.Count >= 8)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("Invoice.Col.Code");
            _grid.Columns[2].Header = T("Invoice.Col.Description");
            _grid.Columns[3].Header = T("Invoice.Col.Unit");
            _grid.Columns[4].Header = T("Invoice.Col.Qty");
            _grid.Columns[5].Header = T(_kind == OrdKind.Sales ? "Invoice.Col.SalePrice" : "Invoice.Col.PurchasePrice");
            _grid.Columns[6].Header = T("Invoice.Col.Discount");
            _grid.Columns[7].Header = T("Invoice.Col.LineTotal");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        RefreshTotals();
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

    private sealed class OrderLineVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _itemCode = "", _desc = "", _unit = "";
        private decimal _qty = 1, _price, _disc;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(); } }
        public string Description { get => _desc; set { _desc = value; OnChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnChanged(); } }

        public decimal Qty { get => _qty; set { _qty = value; OnChanged(nameof(QtyText)); OnChanged(nameof(LineTotalText)); } }
        public decimal UnitPrice { get => _price; set { _price = value; OnChanged(nameof(PriceText)); OnChanged(nameof(LineTotalText)); } }
        public decimal DiscountPct { get => _disc; set { _disc = value; OnChanged(nameof(DiscountText)); OnChanged(nameof(LineTotalText)); } }

        public decimal LineTotal => _qty * _price * (1m - _disc / 100m);
        public string LineTotalText => LineTotal.ToString("N2", CultureInfo.InvariantCulture);

        public string QtyText
        {
            get => _qty.ToString("0.###", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Qty = v; }
        }

        public string PriceText
        {
            get => _price.ToString("0.####", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) UnitPrice = v; }
        }

        public string DiscountText
        {
            get => _disc.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) DiscountPct = v; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
