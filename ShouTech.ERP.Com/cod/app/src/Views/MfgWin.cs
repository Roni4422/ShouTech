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

namespace ShouTech.App.Views;

/// <summary>
/// </summary>
public sealed class MfgWin : BaseWin
{

    /// <summary>Reports that persistence is not implemented instead of claiming success.</summary>
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


    private readonly TextBox _txtDocNo = new() { Width = 110, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly TextBox _txtItem = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtItemName = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtQty = new() { Width = 80, Height = 26, Text = "1" };
    private readonly TextBox _txtUnit = new() { Width = 70, Height = 26, Text = "PCS" };
    private readonly TextBox _txtWhs = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtOverheads = new() { Width = 90, Height = 26, Text = "0.00" };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnAddItem = new();
    private readonly Button _btnRemItem = new();
    private readonly Button _btnPrint = new();

    private readonly TextBlock _lblTotCost = new();
    private readonly TextBlock _lblUnitCost = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<MfgItemVm> _items = new();

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
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "MfgOrder.Posted");
        StyleButton(_btnAddItem, "Invoice.AddLine");
        StyleButton(_btnRemItem, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");

        _btnNew.Click += (_, _) => ResetOrder();
        _btnSave.Click += (_, _) => ShowPersistenceUnavailable();
        _btnPost.Click += (_, _) => ShowPersistenceUnavailable();
        _btnPrint.Click += (_, _) => ShowPersistenceUnavailable();

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

        foreach (var b in new[] { _btnNew, _btnSave, _btnPost, _btnAddItem, _btnRemItem, _btnPrint })
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
        DockPanel.SetDock(_lblTotCost, Dock.Left);
        DockPanel.SetDock(_lblUnitCost, Dock.Right);
        footerContent.Children.Add(_lblTotCost);
        footerContent.Children.Add(_lblUnitCost);
        footer.Child = footerContent;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

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
        _btnSave.IsEnabled = true;
        _btnPost.IsEnabled = true;
        _grid.IsReadOnly = false;
        CalculateCost();
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
