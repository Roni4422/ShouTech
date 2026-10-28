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

public enum CshKind
{
    Receipt,
    Payment
}

/// <summary>
/// Supports Receipt & Payment modes with split allocations and alternating zebra table.
/// </summary>
public sealed class CshWin : BaseWin
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


    private readonly CshKind _kind;

    private readonly TextBox _txtDocNo = new() { Width = 110, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly TextBox _txtBox = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtParty = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtAmount = new() { Width = 110, Height = 26, Text = "0.00" };
    private readonly TextBox _txtCur = new() { Width = 70, Height = 26, Text = "SAR" };
    private readonly TextBox _txtRate = new() { Width = 70, Height = 26, Text = "1.00" };
    private readonly TextBox _txtRefNo = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtCc = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtMemo = new() { Width = 400, Height = 26 };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemoveLine = new();

    private readonly TextBlock _lblTotCalc = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<CshLineVm> _lines = new();
    private bool _isPosted;

    public CshWin(CshKind kind = CshKind.Receipt)
    {
        _kind = kind;
        Width = 880;
        Height = 560;
        MinWidth = 760;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetVoucher();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Vouchers.Post");
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemoveLine, "Invoice.RemoveLine");

        _btnNew.Click += (_, _) => ResetVoucher();
        _btnSave.Click += (_, _) => SaveVoucher();
        _btnPost.Click += (_, _) => PostVoucher();
        _btnPrint.Click += (_, _) => ShowOperationUnavailable("Print");
        _btnAddLine.Click += (_, _) =>
        {
            var l = new CshLineVm { LineNo = _lines.Count + 1 };
            _lines.Add(l);
            _grid.SelectedItem = l;
            _grid.ScrollIntoView(l);
        };
        _btnRemoveLine.Click += (_, _) =>
        {
            if (_grid.SelectedItem is CshLineVm selected && _lines.Count > 1)
            {
                _lines.Remove(selected);
                RenumberLines();
                RefreshCalculations();
            }
        };

        foreach (var b in new[] { _btnNew, _btnSave, _btnPost, _btnPrint, _btnAddLine, _btnRemoveLine })
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
        DockPanel.SetDock(_lblTotCalc, Dock.Left);
        DockPanel.SetDock(_lblStatus, Dock.Right);
        footerContent.Children.Add(_lblTotCalc);
        footerContent.Children.Add(_lblStatus);
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
        headerPanel.Children.Add(Labeled(_kind == CshKind.Receipt ? "Vouchers.ReceivedFrom" : "Vouchers.PaidTo", _txtParty));
        headerPanel.Children.Add(Labeled("Invoice.Total", _txtAmount));
        headerPanel.Children.Add(Labeled("Vouchers.Currency", _txtCur));
        headerPanel.Children.Add(Labeled("Vouchers.ExchangeRate", _txtRate));
        headerPanel.Children.Add(Labeled("Vouchers.RefNo", _txtRefNo));
        headerPanel.Children.Add(Labeled("Vouchers.CostCenter", _txtCc));
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

        _grid.ItemsSource = _lines;
        _grid.Columns.Add(TextCol(nameof(CshLineVm.LineNo), 45, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(CshLineVm.AccountCode), 120));
        _grid.Columns.Add(TextCol(nameof(CshLineVm.AccountName), 220));
        _grid.Columns.Add(TextCol(nameof(CshLineVm.AmountText), 110));
        _grid.Columns.Add(TextCol(nameof(CshLineVm.CostCenter), 110));
        _grid.Columns.Add(TextCol(nameof(CshLineVm.InvoiceRef), 100));
        _grid.Columns.Add(TextCol(nameof(CshLineVm.Memo), 200));

        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshCalculations);

        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private void RenumberLines()
    {
        for (var i = 0; i < _lines.Count; i++)
            _lines[i].LineNo = i + 1;
    }

    private void ResetVoucher()
    {
        _isPosted = false;
        var prefix = _kind == CshKind.Receipt ? "RCV-" : "PAY-";
        _txtDocNo.Text = prefix + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        _dtDate.SelectedDate = DateTime.Today;
        _txtParty.Text = string.Empty;
        _txtAmount.Text = "0.00";
        _txtRefNo.Text = string.Empty;
        _txtCc.Text = string.Empty;
        _txtMemo.Text = string.Empty;
        _lines.Clear();
        _lines.Add(new CshLineVm { LineNo = 1 });
        _btnSave.IsEnabled = true;
        _btnPost.IsEnabled = true;
        _grid.IsReadOnly = false;
        RefreshCalculations();
    }

    private void RefreshCalculations()
    {
        var sum = _lines.Sum(l => l.Amount);
        if (decimal.TryParse(_txtAmount.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var headerTotal) && headerTotal > 0 && sum == 0)
        {
            sum = headerTotal;
            if (_lines.Count > 0 && _lines[0].Amount == 0)
                _lines[0].Amount = headerTotal;
        }

        _lblTotCalc.Text = $"{T("Invoice.Total")}: {sum:N2} {_txtCur.Text}";
        _lblStatus.Text = $"{T("Invoice.Status")}: {(_isPosted ? T("Invoice.StatusPosted") : T("Invoice.StatusDraft"))}";
    }

    private void SaveVoucher()
    {
        ShowPersistenceUnavailable();
    }

    private void PostVoucher()
    {
        ShowPersistenceUnavailable();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T(_kind == CshKind.Receipt ? "Vouchers.ReceiptTitle" : "Vouchers.PaymentTitle");
        if (_grid.Columns.Count >= 7)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("Vouchers.AccountCode");
            _grid.Columns[2].Header = T("Vouchers.AccountName");
            _grid.Columns[3].Header = T("MfgOrder.Amount");
            _grid.Columns[4].Header = T("Vouchers.CostCenter");
            _grid.Columns[5].Header = T("Vouchers.RefNo");
            _grid.Columns[6].Header = T("Vouchers.Memo");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        RefreshCalculations();
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

    private sealed class CshLineVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _accCode = "", _accName = "", _costCenter = "", _invoiceRef = "", _memo = "";
        private decimal _amount;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string AccountCode { get => _accCode; set { _accCode = value; OnChanged(); } }
        public string AccountName { get => _accName; set { _accName = value; OnChanged(); } }
        public string CostCenter { get => _costCenter; set { _costCenter = value; OnChanged(); } }
        public string InvoiceRef { get => _invoiceRef; set { _invoiceRef = value; OnChanged(); } }
        public string Memo { get => _memo; set { _memo = value; OnChanged(); } }

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
