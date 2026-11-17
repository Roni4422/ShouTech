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
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

public enum ChqKind
{
    Incoming,
    Outgoing
}

/// <summary>
/// Cheque batch window (RS-4): each grid row is an individual cheque sharing
/// the header context (date, portfolio/box account, currency). Save inserts
/// the batch and posts ONE receipt/payment journal entry through
/// ChequeService (IN: Dr Box total, Cr Party per cheque; OUT mirrored),
/// storing journal_entry_id on every row. Lifecycle actions (Deposit, Clear,
/// Bounce, Endorse, Cancel) act on the selected saved cheque and are also
/// available in the register (ChqListWin).
/// </summary>
public sealed class ChqWin : BaseWin
{
    protected override string? ScheduleTintName => "Periwinkle";

    private readonly ChqKind _kind;

    private readonly TextBox _txtDocNo = new() { Width = 110, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly TextBox _txtBox = new() { Width = 160, Height = 26 };
    private readonly TextBox _txtCur = new() { Width = 70, Height = 26, Text = UsrPref.Load().CurrencyCode };
    private readonly TextBox _txtMemo = new() { Width = 300, Height = 26 };
    private readonly TextBox _txtDepositBank = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtEndorseTo = new() { Width = 150, Height = 26 };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnAddChq = new();
    private readonly Button _btnRemChq = new();
    private readonly Button _btnDeposit = new();
    private readonly Button _btnClear = new();
    private readonly Button _btnBounce = new();
    private readonly Button _btnEndorse = new();
    private readonly Button _btnCancelChq = new();
    private readonly Button _btnPrint = new();

    private readonly TextBlock _lblTotCount = new();
    private readonly TextBlock _lblTotAmount = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<ChqVm> _cheques = new();
    private bool _saved;

    public ChqWin() : this(ChqKind.Incoming) { }

    public ChqWin(ChqKind kind)
    {
        _kind = kind;
        Width = 980;
        Height = 600;
        MinWidth = 800;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        ApplyLocalizedText();
        ResetBatch();
        Loaded += OnLoaded;
    }

    public ChqKind Kind => _kind;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Vouchers.SecuritiesView))
        {
            MessageBox.Show(this, T("Cheques.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnAddChq, "Invoice.AddLine");
        StyleButton(_btnRemChq, "Invoice.RemoveLine");
        StyleButton(_btnDeposit, "Cheques.Deposit");
        StyleButton(_btnClear, "Cheques.Clear");
        StyleButton(_btnBounce, "Cheques.Bounce");
        StyleButton(_btnEndorse, "Cheques.Endorse");
        StyleButton(_btnCancelChq, "Cheques.CancelCheque");
        StyleButton(_btnPrint, "Invoice.Print");

        _btnNew.Click += (_, _) => ResetBatch();
        _btnSave.Click += async (_, _) => await SaveBatchAsync();
        _btnPrint.Click += (_, _) => PrintRegister();
        _btnDeposit.Click += async (_, _) => await LifecycleAsync("Deposit");
        _btnClear.Click += async (_, _) => await LifecycleAsync("Clear");
        _btnBounce.Click += async (_, _) => await LifecycleAsync("Bounce");
        _btnEndorse.Click += async (_, _) => await LifecycleAsync("Endorse");
        _btnCancelChq.Click += async (_, _) => await LifecycleAsync("Cancel");
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
            if (_saved) return;
            if (_grid.SelectedItem is ChqVm selected && _cheques.Count > 1)
            {
                _cheques.Remove(selected);
                Renumber();
                RefreshSummary();
            }
        };

        foreach (var b in new[] { _btnNew, _btnSave, _btnAddChq, _btnRemChq, _btnDeposit, _btnClear, _btnBounce, _btnEndorse, _btnCancelChq, _btnPrint })
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
        DockPanel.SetDock(_lblTotCount, Dock.Left);
        DockPanel.SetDock(_lblTotAmount, Dock.Right);
        footerContent.Children.Add(_lblTotCount);
        footerContent.Children.Add(_lblTotAmount);
        var footerStack = new StackPanel();
        footerStack.Children.Add(footerContent);
        footerStack.Children.Add(_lblStatus);
        footer.Child = footerStack;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var headerPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        headerPanel.Children.Add(Labeled("Vouchers.VoucherNo", _txtDocNo));
        headerPanel.Children.Add(Labeled("Vouchers.Date", _dtDate));
        headerPanel.Children.Add(Labeled("Vouchers.Treasury", _txtBox));
        headerPanel.Children.Add(Labeled("Vouchers.Currency", _txtCur));
        headerPanel.Children.Add(Labeled("Vouchers.Memo", _txtMemo));
        headerPanel.Children.Add(Labeled("Cheques.DepositBankAccount", _txtDepositBank));
        headerPanel.Children.Add(Labeled("Cheques.EndorsedToAccount", _txtEndorseTo));
        Grid.SetRow(headerPanel, 0);
        body.Children.Add(headerPanel);

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
        _grid.Columns.Add(TextCol(nameof(ChqVm.Status), 100, readOnly: true));
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
        _saved = false;
        _txtDocNo.Text = "CHQ-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _dtDate.SelectedDate = DateTime.Today;
        _txtBox.Text = string.Empty;
        _txtMemo.Text = string.Empty;
        _txtDepositBank.Text = string.Empty;
        _txtEndorseTo.Text = string.Empty;
        _cheques.Clear();
        _cheques.Add(new ChqVm { LineNo = 1, DueDate = DateTime.Today.AddDays(30) });
        _btnSave.IsEnabled = true;
        _grid.IsReadOnly = false;
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var count = _cheques.Count;
        var total = _cheques.Sum(c => c.Amount);
        _lblTotCount.Text = $"{T("Vouchers.ChequeNo")}: {count}";
        _lblTotAmount.Text = $"{T("Invoice.Total")}: {total:N2} {_txtCur.Text}";
        _lblTotAmount.FontWeight = FontWeights.Bold;
        _lblStatus.Text = _saved ? T("Cheques.BatchSaved") : T("Cheques.BatchDraft");
    }

    private async Task SaveBatchAsync()
    {
        if (_saved)
        {
            MessageBox.Show(this, T("Cheques.BatchSaved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AppHost.Permissions.Has(PermCat.Vouchers.SecuritiesManage))
        {
            MessageBox.Show(this, T("Cheques.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (AppHost.Cheques is not { } service)
                throw new InvalidOperationException(T("Cheques.ServiceUnavailable"));
            if (string.IsNullOrWhiteSpace(CurrentCompanyCode))
                throw new InvalidOperationException(T("Cheques.NoCompany"));
            if (string.IsNullOrWhiteSpace(CurrentUserId))
                throw new InvalidOperationException(T("Cheques.NoUser"));
            if (string.IsNullOrWhiteSpace(_txtBox.Text))
                throw new InvalidOperationException(T("Cheques.BoxRequired"));
            if (_dtDate.SelectedDate is not DateTime issueDate)
                throw new InvalidOperationException(T("Cheques.DateRequired"));
            var currency = _txtCur.Text.Trim().ToUpperInvariant();
            if (currency.Length != 3)
                throw new InvalidOperationException(T("Cheques.CurrencyInvalid"));

            var rows = _cheques.Where(c => c.Amount > 0 || !string.IsNullOrWhiteSpace(c.ChequeNo)).ToList();
            if (rows.Count == 0)
                throw new InvalidOperationException(T("Vouchers.NeedLines"));

            var entities = new List<Cheque>();
            foreach (var row in rows)
            {
                var entity = new Cheque
                {
                    Direction = _kind == ChqKind.Incoming ? ChequeDirections.In : ChequeDirections.Out,
                    ChequeNo = row.ChequeNo.Trim(),
                    BankName = row.BankName.Trim(),
                    BranchName = string.IsNullOrWhiteSpace(row.Branch) ? null : row.Branch.Trim(),
                    DrawerName = string.IsNullOrWhiteSpace(row.Drawer) ? null : row.Drawer.Trim(),
                    PartyName = string.IsNullOrWhiteSpace(row.Drawer) ? null : row.Drawer.Trim(),
                    PartyAccountCode = row.AccountCode.Trim(),
                    Amount = row.Amount,
                    CurrencyCode = currency,
                    ExchangeRate = 1m,
                    IssueDate = DateOnly.FromDateTime(issueDate),
                    DueDate = DateOnly.FromDateTime(row.DueDate),
                    PortfolioAccountCode = _txtBox.Text.Trim(),
                    Notes = string.IsNullOrWhiteSpace(row.Memo) ? (string.IsNullOrWhiteSpace(_txtMemo.Text) ? null : _txtMemo.Text.Trim()) : row.Memo.Trim()
                };
                row.EntityId = entity.Id;
                entities.Add(entity);
            }

            var ids = await service.SaveBatchAsync(CurrentCompanyCode, entities, _txtBox.Text.Trim(), CurrentUserId,
                DateOnly.FromDateTime(issueDate),
                string.IsNullOrWhiteSpace(_txtMemo.Text) ? null : _txtMemo.Text.Trim());
            _saved = true;
            _btnSave.IsEnabled = false;
            _grid.IsReadOnly = true;
            foreach (var row in _cheques)
                if (row.EntityId.HasValue && ids.Contains(row.EntityId.Value))
                    row.Status = _kind == ChqKind.Incoming ? ChequeStatuses.InPortfolio : ChequeStatuses.Issued;
            RefreshSummary();
            MessageBox.Show(this, T("Cheques.BatchSaved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveBatch", ex);
        }
    }

    private async Task LifecycleAsync(string action)
    {
        if (_grid.SelectedItem is not ChqVm row || !row.EntityId.HasValue)
        {
            MessageBox.Show(this, T("Cheques.SelectSavedCheque"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!AppHost.Permissions.Has(PermCat.Vouchers.SecuritiesManage))
        {
            MessageBox.Show(this, T("Cheques.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (AppHost.Cheques is not { } service)
                throw new InvalidOperationException(T("Cheques.ServiceUnavailable"));
            var today = DateOnly.FromDateTime(DateTime.Today);
            switch (action)
            {
                case "Deposit":
                    if (string.IsNullOrWhiteSpace(_txtDepositBank.Text))
                        throw new InvalidOperationException(T("Cheques.DepositBankRequired"));
                    await service.DepositAsync(CurrentCompanyCode, row.EntityId.Value, _txtDepositBank.Text.Trim(), today, CurrentUserId);
                    row.Status = ChequeStatuses.Deposited;
                    break;
                case "Clear":
                    await service.ClearAsync(CurrentCompanyCode, row.EntityId.Value, today, CurrentUserId);
                    row.Status = ChequeStatuses.Cleared;
                    break;
                case "Bounce":
                    await service.BounceAsync(CurrentCompanyCode, row.EntityId.Value, today,
                        string.IsNullOrWhiteSpace(_txtMemo.Text) ? null : _txtMemo.Text.Trim(), 0m, CurrentUserId);
                    row.Status = ChequeStatuses.Bounced;
                    break;
                case "Endorse":
                    if (string.IsNullOrWhiteSpace(_txtEndorseTo.Text))
                        throw new InvalidOperationException(T("Cheques.EndorseToRequired"));
                    await service.EndorseAsync(CurrentCompanyCode, row.EntityId.Value, _txtEndorseTo.Text.Trim(), today, CurrentUserId);
                    row.Status = ChequeStatuses.Endorsed;
                    break;
                case "Cancel":
                    await service.CancelOutgoingAsync(CurrentCompanyCode, row.EntityId.Value, CurrentUserId);
                    row.Status = ChequeStatuses.Cancelled;
                    break;
            }
            RefreshSummary();
        }
        catch (Exception ex)
        {
            ReportFailure(action, ex);
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
    // Print: register-style print of the batch (JrnWin A4 pattern).
    // ------------------------------------------------------------------
    private void PrintRegister()
    {
        var page = BuildPrintPage();
        var paginator = new SinglePagePaginator(page);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, Title);
    }

    private Border BuildPrintPage()
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
            Text = T(_kind == ChqKind.Incoming ? "Cheques.IncomingTitle" : "Cheques.OutgoingTitle"),
            FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 10)
        });
        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var width in new[] { 100, 130, 130, 90, 100, 130, 90 })
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
        Cell(0, 0, T("Vouchers.ChequeNo"), true); Cell(0, 1, T("Vouchers.Bank"), true);
        Cell(0, 2, T("Vouchers.Drawer"), true); Cell(0, 3, T("Vouchers.DueDate"), true);
        Cell(0, 4, T("MfgOrder.Amount"), true); Cell(0, 5, T("Vouchers.AccountCode"), true);
        Cell(0, 6, T("Vouchers.Status"), true);
        var rowIndex = 1;
        foreach (var c in _cheques)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rowIndex, 0, c.ChequeNo); Cell(rowIndex, 1, c.BankName); Cell(rowIndex, 2, c.Drawer);
            Cell(rowIndex, 3, c.DueDateText); Cell(rowIndex, 4, c.Amount.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 5, c.AccountCode); Cell(rowIndex, 6, c.Status);
            rowIndex++;
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(rowIndex, 3, T("Invoice.Total"), true);
        Cell(rowIndex, 4, _cheques.Sum(c => c.Amount).ToString("N2", CultureInfo.InvariantCulture), true);
        stack.Children.Add(table);
        return root;
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
        Title = T(_kind == ChqKind.Incoming ? "Cheques.IncomingTitle" : "Cheques.OutgoingTitle");
        if (_grid.Columns.Count >= 10)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("Vouchers.ChequeNo");
            _grid.Columns[2].Header = T("Vouchers.Bank");
            _grid.Columns[3].Header = T("BankCard.Branch");
            _grid.Columns[4].Header = T("Vouchers.DueDate");
            _grid.Columns[5].Header = T("MfgOrder.Amount");
            _grid.Columns[6].Header = T("Vouchers.Drawer");
            _grid.Columns[7].Header = T("Vouchers.AccountCode");
            _grid.Columns[8].Header = T("Vouchers.Status");
            _grid.Columns[9].Header = T("Vouchers.Memo");
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
        private string _chequeNo = "", _bankName = "", _branch = "", _drawer = "", _accCode = "", _memo = "", _status = "";
        private DateTime _dueDate = DateTime.Today;
        private decimal _amount;

        public Guid? EntityId { get; set; }
        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string ChequeNo { get => _chequeNo; set { _chequeNo = value; OnChanged(); } }
        public string BankName { get => _bankName; set { _bankName = value; OnChanged(); } }
        public string Branch { get => _branch; set { _branch = value; OnChanged(); } }
        public string Drawer { get => _drawer; set { _drawer = value; OnChanged(); } }
        public string AccountCode { get => _accCode; set { _accCode = value; OnChanged(); } }
        public string Memo { get => _memo; set { _memo = value; OnChanged(); } }
        public string Status { get => _status; set { _status = value; OnChanged(); } }

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
