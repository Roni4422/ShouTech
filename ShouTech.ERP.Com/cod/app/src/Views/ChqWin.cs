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
public sealed class ChqWin : BaseWin
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


    private readonly TextBox _txtDocNo = new() { Width = 110, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly TextBox _txtBox = new() { Width = 160, Height = 26 };
    private readonly TextBox _txtCur = new() { Width = 70, Height = 26, Text = "SAR" };
    private readonly TextBox _txtMemo = new() { Width = 380, Height = 26 };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnAddChq = new();
    private readonly Button _btnRemChq = new();
    private readonly Button _btnPrint = new();

    private readonly TextBlock _lblTotCount = new();
    private readonly TextBlock _lblTotAmount = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<ChqVm> _cheques = new();

    public ChqWin()
    {
        Width = 920;
        Height = 560;
        MinWidth = 800;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetBatch();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnAddChq, "Invoice.AddLine");
        StyleButton(_btnRemChq, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");

        _btnNew.Click += (_, _) => ResetBatch();
        _btnSave.Click += (_, _) => ShowPersistenceUnavailable();
        _btnPrint.Click += (_, _) => ShowOperationUnavailable("Print");
        _btnAddChq.Click += (_, _) =>
        {
            var c = new ChqVm { LineNo = _cheques.Count + 1, DueDate = DateTime.Today.AddDays(30) };
            _cheques.Add(c);
            _grid.SelectedItem = c;
            _grid.ScrollIntoView(c);
            RefreshSummary();
        };
        _btnRemChq.Click += (_, _) =>
        {
            if (_grid.SelectedItem is ChqVm selected && _cheques.Count > 1)
            {
                _cheques.Remove(selected);
                Renumber();
                RefreshSummary();
            }
        };

        foreach (var b in new[] { _btnNew, _btnSave, _btnAddChq, _btnRemChq, _btnPrint })
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
        DockPanel.SetDock(_lblTotCount, Dock.Left);
        DockPanel.SetDock(_lblTotAmount, Dock.Right);
        footerContent.Children.Add(_lblTotCount);
        footerContent.Children.Add(_lblTotAmount);
        footer.Child = footerContent;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // 3. Body
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        headerPanel.Children.Add(Labeled("Vouchers.VoucherNo", _txtDocNo));
        headerPanel.Children.Add(Labeled("Vouchers.Date", _dtDate));
        headerPanel.Children.Add(Labeled("Vouchers.Treasury", _txtBox));
        headerPanel.Children.Add(Labeled("Vouchers.Currency", _txtCur));
        headerPanel.Children.Add(Labeled("Vouchers.Memo", _txtMemo));

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

        _grid.ItemsSource = _cheques;
        _grid.Columns.Add(TextCol(nameof(ChqVm.LineNo), 45, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(ChqVm.ChequeNo), 110));
        _grid.Columns.Add(TextCol(nameof(ChqVm.BankName), 140));
        _grid.Columns.Add(TextCol(nameof(ChqVm.Branch), 100));
        _grid.Columns.Add(TextCol(nameof(ChqVm.DueDateText), 100));
        _grid.Columns.Add(TextCol(nameof(ChqVm.AmountText), 110));
        _grid.Columns.Add(TextCol(nameof(ChqVm.Drawer), 150));
        _grid.Columns.Add(TextCol(nameof(ChqVm.AccountCode), 100));
        _grid.Columns.Add(TextCol(nameof(ChqVm.Memo), 160));

        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshSummary);

        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private void Renumber()
    {
        for (var i = 0; i < _cheques.Count; i++)
            _cheques[i].LineNo = i + 1;
    }

    private void ResetBatch()
    {
        _txtDocNo.Text = "CHQ-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        _dtDate.SelectedDate = DateTime.Today;
        _txtBox.Text = string.Empty;
        _txtMemo.Text = string.Empty;
        _cheques.Clear();
        _cheques.Add(new ChqVm { LineNo = 1, DueDate = DateTime.Today.AddDays(30) });
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var count = _cheques.Count;
        var total = _cheques.Sum(c => c.Amount);
        _lblTotCount.Text = $"{T("Vouchers.ChequeNo")}: {count}";
        _lblTotAmount.Text = $"{T("Invoice.Total")}: {total:N2} {_txtCur.Text}";
        _lblTotAmount.FontWeight = FontWeights.Bold;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Vouchers.ChequeTitle");
        if (_grid.Columns.Count >= 9)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("Vouchers.ChequeNo");
            _grid.Columns[2].Header = T("Vouchers.Bank");
            _grid.Columns[3].Header = T("BankCard.Branch");
            _grid.Columns[4].Header = T("Vouchers.DueDate");
            _grid.Columns[5].Header = T("MfgOrder.Amount");
            _grid.Columns[6].Header = T("Vouchers.Drawer");
            _grid.Columns[7].Header = T("Vouchers.AccountCode");
            _grid.Columns[8].Header = T("Vouchers.Memo");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        RefreshSummary();
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
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 3) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 1), Opacity = 0.85 });
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

    private sealed class ChqVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _chequeNo = "", _bankName = "", _branch = "", _drawer = "", _accCode = "", _memo = "";
        private DateTime _dueDate = DateTime.Today;
        private decimal _amount;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string ChequeNo { get => _chequeNo; set { _chequeNo = value; OnChanged(); } }
        public string BankName { get => _bankName; set { _bankName = value; OnChanged(); } }
        public string Branch { get => _branch; set { _branch = value; OnChanged(); } }
        public string Drawer { get => _drawer; set { _drawer = value; OnChanged(); } }
        public string AccountCode { get => _accCode; set { _accCode = value; OnChanged(); } }
        public string Memo { get => _memo; set { _memo = value; OnChanged(); } }

        public DateTime DueDate { get => _dueDate; set { _dueDate = value; OnChanged(); OnChanged(nameof(DueDateText)); } }
        public string DueDateText
        {
            get => _dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            set { if (DateTime.TryParse(value, out var d)) DueDate = d; }
        }

        public decimal Amount { get => _amount; set { _amount = value; OnChanged(nameof(AmountText)); } }
        public string AmountText
        {
            get => _amount == 0 ? "" : _amount.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Amount = v; else Amount = 0; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
