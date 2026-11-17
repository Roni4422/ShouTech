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
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;

namespace ShouTech.App.Views;

public enum CshKind
{
    Receipt,
    Payment
}

/// <summary>
/// Cash voucher (RS-3): Receipt / Payment over the journal engine.
/// Save creates a balanced draft through JournalService (Receipt: Dr Box,
/// Cr lines; Payment mirrored). Posting is blocked while the header Amount
/// differs from the sum of lines (ERPNext difference rule, tolerance 0.005);
/// the difference is shown live in the footer. Posted vouchers are read-only;
/// drafts cannot be deleted (the journal engine has no draft delete) — the
/// Delete button stays disabled with an honest explanation in the status bar.
/// </summary>
public sealed class CshWin : BaseWin
{
    private readonly CshKind _kind;

    private readonly TextBox _txtDocNo = new() { Width = 150, Height = 26 };
    private readonly DatePicker _dtDate = new() { Width = 120, Height = 26, SelectedDate = DateTime.Today };
    private readonly TextBox _txtBox = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtParty = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtAmount = new() { Width = 110, Height = 26, Text = "0.00" };
    private readonly TextBox _txtCur = new() { Width = 70, Height = 26, Text = UsrPref.Load().CurrencyCode };
    private readonly TextBox _txtRate = new() { Width = 70, Height = 26, Text = "1.00" };
    private readonly TextBox _txtRefNo = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtCc = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtMemo = new() { Width = 400, Height = 26 };

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemoveLine = new();

    private readonly TextBlock _lblTotCalc = new();
    private readonly TextBlock _lblDiff = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<CshLineVm> _lines = new();
    private Guid? _currentJournalId;
    private bool _isPosted;

    public CshWin(CshKind kind = CshKind.Receipt)
    {
        _kind = kind;
        Width = 920;
        Height = 580;
        MinWidth = 760;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetVoucher();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Vouchers.View))
        {
            MessageBox.Show(this, T("CashVoucher.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "Vouchers.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Vouchers.Post");
        StyleButton(_btnDelete, "Common.Delete");
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemoveLine, "Invoice.RemoveLine");
        _btnDelete.IsEnabled = false; // JournalService has no draft delete; see footer note.

        _btnNew.Click += (_, _) => ResetVoucher();
        _btnLoad.Click += async (_, _) => await LoadVoucherAsync();
        _btnSave.Click += async (_, _) => await SaveVoucherAsync();
        _btnPost.Click += async (_, _) => await PostVoucherAsync();
        _btnPrint.Click += (_, _) => PrintVoucher();
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
        _txtAmount.TextChanged += (_, _) => RefreshCalculations();

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnPost, _btnDelete, _btnPrint, _btnAddLine, _btnRemoveLine })
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
        DockPanel.SetDock(_lblTotCalc, Dock.Left);
        DockPanel.SetDock(_lblStatus, Dock.Right);
        footerContent.Children.Add(_lblTotCalc);
        footerContent.Children.Add(_lblStatus);
        var footerStack = new StackPanel();
        footerStack.Children.Add(footerContent);
        footerStack.Children.Add(_lblDiff);
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
        headerPanel.Children.Add(Labeled(_kind == CshKind.Receipt ? "Vouchers.ReceivedFrom" : "Vouchers.PaidTo", _txtParty));
        headerPanel.Children.Add(Labeled("Invoice.Total", _txtAmount));
        headerPanel.Children.Add(Labeled("Vouchers.Currency", _txtCur));
        headerPanel.Children.Add(Labeled("Vouchers.ExchangeRate", _txtRate));
        headerPanel.Children.Add(Labeled("Vouchers.RefNo", _txtRefNo));
        headerPanel.Children.Add(Labeled("Vouchers.CostCenter", _txtCc));
        headerPanel.Children.Add(Labeled("Vouchers.Memo", _txtMemo));
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
        _currentJournalId = null;
        _isPosted = false;
        var prefix = _kind == CshKind.Receipt ? "RCV-" : "PAY-";
        _txtDocNo.Text = prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _txtDocNo.IsReadOnly = false;
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
        SetReadOnly(false);
        RefreshCalculations();
    }

    private void SetReadOnly(bool readOnly)
    {
        _grid.IsReadOnly = readOnly;
        _dtDate.IsEnabled = !readOnly;
        _txtBox.IsReadOnly = readOnly;
        _txtParty.IsReadOnly = readOnly;
        _txtAmount.IsReadOnly = readOnly;
        _txtCur.IsReadOnly = readOnly;
        _txtRate.IsReadOnly = readOnly;
        _txtRefNo.IsReadOnly = readOnly;
        _txtCc.IsReadOnly = readOnly;
        _txtMemo.IsReadOnly = readOnly;
        _btnAddLine.IsEnabled = !readOnly;
        _btnRemoveLine.IsEnabled = !readOnly;
    }

    private decimal LinesTotal() => _lines.Sum(l => l.Amount);

    private decimal HeaderAmount()
        => decimal.TryParse(_txtAmount.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ||
           decimal.TryParse(_txtAmount.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out v) ? v : 0m;

    private void RefreshCalculations()
    {
        var sum = LinesTotal();
        var header = HeaderAmount();
        var diff = header - sum;
        _lblTotCalc.Text = $"{T("Invoice.Total")}: {sum:N2} {_txtCur.Text}";
        _lblDiff.Text = Math.Abs(diff) <= 0.005m
            ? $"{T("Vouchers.Balanced")} (0.00)"
            : $"{T("Vouchers.Difference")}: {diff:N2} {_txtCur.Text}";
        _lblDiff.FontWeight = Math.Abs(diff) <= 0.005m ? FontWeights.Normal : FontWeights.SemiBold;
        _lblStatus.Text = $"{T("Invoice.Status")}: {(_isPosted ? T("Invoice.StatusPosted") : T("Invoice.StatusDraft"))} · {T("CashVoucher.DeleteUnavailable")}";
    }

    private (JournalService Journal, JournalRepository Store, Guid CompanyId, string Actor) RequireJournalContext()
    {
        if (string.IsNullOrWhiteSpace(CurrentCompanyCode))
            throw new InvalidOperationException(T("CashVoucher.NoCompany"));
        if (string.IsNullOrWhiteSpace(CurrentUserId))
            throw new InvalidOperationException(T("CashVoucher.NoUser"));
        if (AppHost.JournalStore is not { } store || AppHost.Journal is not { } journal)
            throw new InvalidOperationException(T("CashVoucher.ServiceUnavailable"));
        return (journal, store, Guid.Empty, CurrentUserId);
    }

    private async Task SaveVoucherAsync()
    {
        if (_currentJournalId.HasValue)
        {
            MessageBox.Show(this, T("CashVoucher.AlreadySaved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AppHost.Permissions.Has(PermCat.Vouchers.Create))
        {
            MessageBox.Show(this, T("CashVoucher.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var (journal, store, _, actor) = RequireJournalContext();
            if (_dtDate.SelectedDate is not DateTime selectedDate)
                throw new InvalidOperationException(T("CashVoucher.DateRequired"));
            if (string.IsNullOrWhiteSpace(_txtDocNo.Text))
                throw new InvalidOperationException(T("CashVoucher.DocNoRequired"));
            if (string.IsNullOrWhiteSpace(_txtBox.Text))
                throw new InvalidOperationException(T("CashVoucher.BoxRequired"));
            if (string.IsNullOrWhiteSpace(_txtMemo.Text) && string.IsNullOrWhiteSpace(_txtParty.Text))
                throw new InvalidOperationException(T("CashVoucher.MemoRequired"));
            var currency = _txtCur.Text.Trim().ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                throw new InvalidOperationException(T("CashVoucher.CurrencyInvalid"));
            if (!decimal.TryParse(_txtRate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) &&
                !decimal.TryParse(_txtRate.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out rate))
                throw new InvalidOperationException(T("CashVoucher.RateInvalid"));
            if (rate <= 0)
                throw new InvalidOperationException(T("CashVoucher.RateInvalid"));
            var validLines = _lines.Where(l => l.Amount > 0).ToList();
            if (validLines.Count == 0)
                throw new InvalidOperationException(T("Vouchers.NeedLines"));

            var companyId = await store.ResolveCompanyIdAsync(CurrentCompanyCode);
            var boxId = await store.ResolveAccountIdAsync(companyId, _txtBox.Text.Trim());
            var requests = new List<JournalLineRequest>();
            var total = 0m;
            foreach (var line in validLines)
            {
                if (string.IsNullOrWhiteSpace(line.AccountCode))
                    throw new InvalidOperationException($"{T("Vouchers.AccountCode")} — {line.LineNo}");
                var accountId = await store.ResolveAccountIdAsync(companyId, line.AccountCode.Trim());
                line.AccountName = await store.ResolveAccountNameAsync(companyId, line.AccountCode.Trim());
                var cc = string.IsNullOrWhiteSpace(line.CostCenter) ? _txtCc.Text.Trim() : line.CostCenter.Trim();
                var note = string.IsNullOrWhiteSpace(line.Memo) ? _txtMemo.Text.Trim() : line.Memo.Trim();
                total += line.Amount;
                requests.Add(_kind == CshKind.Receipt
                    ? new JournalLineRequest(accountId, 0m, line.Amount, note, cc)
                    : new JournalLineRequest(accountId, line.Amount, 0m, note, cc));
            }
            // Box line last for payments / first-equivalent balancing entry:
            // the journal engine only requires balance, order is presentational.
            var boxNote = string.IsNullOrWhiteSpace(_txtMemo.Text) ? _txtParty.Text.Trim() : _txtMemo.Text.Trim();
            requests.Insert(0, _kind == CshKind.Receipt
                ? new JournalLineRequest(boxId, total, 0m, boxNote, _txtCc.Text.Trim())
                : new JournalLineRequest(boxId, 0m, total, boxNote, _txtCc.Text.Trim()));

            var description = string.IsNullOrWhiteSpace(_txtMemo.Text)
                ? $"{(_kind == CshKind.Receipt ? T("Vouchers.ReceiptTitle") : T("Vouchers.PaymentTitle"))} — {_txtParty.Text.Trim()}"
                : _txtMemo.Text.Trim();
            var id = await journal.CreateDraftAsync(
                companyId, DateOnly.FromDateTime(selectedDate), description, actor, requests,
                referenceNumber: string.IsNullOrWhiteSpace(_txtRefNo.Text) ? null : _txtRefNo.Text.Trim(),
                currencyCode: currency, exchangeRate: rate,
                documentNumber: _txtDocNo.Text.Trim());
            _currentJournalId = id;
            _btnSave.IsEnabled = false;
            SetReadOnly(true);
            RefreshCalculations();
            MessageBox.Show(this, $"{T("Vouchers.Saved")}\n{id:D}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveDraft", ex);
        }
    }

    private async Task PostVoucherAsync()
    {
        if (_isPosted) return;
        if (!AppHost.Permissions.Has(PermCat.Vouchers.Post))
        {
            MessageBox.Show(this, T("CashVoucher.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // ERPNext difference rule: header Amount must equal the sum of lines.
        var diff = HeaderAmount() - LinesTotal();
        if (Math.Abs(diff) > 0.005m)
        {
            MessageBox.Show(this,
                $"{T("CashVoucher.AmountMismatch")}: {diff:N2} {_txtCur.Text.Trim()}",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (!_currentJournalId.HasValue)
            {
                await SaveVoucherAsync();
                if (!_currentJournalId.HasValue) return;
            }
            if (AppHost.Journal is not { } journal)
                throw new InvalidOperationException(T("CashVoucher.ServiceUnavailable"));
            if (string.IsNullOrWhiteSpace(CurrentUserId))
                throw new InvalidOperationException(T("CashVoucher.NoUser"));
            if (!AskConfirm("Vouchers.ConfirmPost")) return;
            await journal.PostAsync(_currentJournalId.Value, CurrentUserId);
            _isPosted = true;
            _btnPost.IsEnabled = false;
            SetReadOnly(true);
            RefreshCalculations();
            MessageBox.Show(this, T("Vouchers.Posted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Post", ex);
        }
    }

    private async Task LoadVoucherAsync()
    {
        var documentNumber = _txtDocNo.Text.Trim();
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            MessageBox.Show(this, T("CashVoucher.DocNoRequired"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var (journal, store, _, _) = RequireJournalContext();
            var companyId = await store.ResolveCompanyIdAsync(CurrentCompanyCode);
            var snapshot = await journal.FindByDocumentNumberAsync(companyId, documentNumber)
                ?? throw new InvalidOperationException($"{T("CashVoucher.NotFound")}: {documentNumber}");
            _currentJournalId = snapshot.Id;
            _isPosted = snapshot.Status != JournalEntryStatus.Draft;
            _dtDate.SelectedDate = snapshot.EntryDate.ToDateTime(TimeOnly.MinValue);
            _txtRefNo.Text = snapshot.ReferenceNumber ?? string.Empty;
            _txtCur.Text = snapshot.CurrencyCode;
            _txtRate.Text = snapshot.ExchangeRate.ToString("0.########", CultureInfo.InvariantCulture);
            _txtMemo.Text = snapshot.Description;

            // The box line is the single Debit line on a receipt (Credit on a
            // payment) whose amount equals the total of the remaining lines.
            var lines = snapshot.Lines.OrderBy(l => l.LineNumber).ToList();
            var total = lines.Sum(l => Math.Max(l.Debit, l.Credit));
            JournalEntryLineSnapshot? boxLine = null;
            foreach (var candidate in lines)
            {
                var amount = _kind == CshKind.Receipt ? candidate.Debit : candidate.Credit;
                var rest = lines.Where(l => l != candidate).Sum(l => _kind == CshKind.Receipt ? l.Credit : l.Debit);
                if (amount > 0 && Math.Abs(amount - rest) <= 0.005m) { boxLine = candidate; break; }
            }
            _lines.Clear();
            var lineNo = 0;
            foreach (var line in lines)
            {
                if (boxLine is not null && line == boxLine)
                {
                    _txtBox.Text = line.AccountCode;
                    continue;
                }
                _lines.Add(new CshLineVm
                {
                    LineNo = ++lineNo,
                    AccountCode = line.AccountCode,
                    AccountName = line.AccountName,
                    Amount = _kind == CshKind.Receipt ? line.Credit : line.Debit,
                    CostCenter = line.CostCenter ?? string.Empty,
                    Memo = line.Note ?? string.Empty
                });
            }
            if (_lines.Count == 0 && lines.Count > 0)
            {
                // Fallback: no box line identified — show every line.
                _txtBox.Text = string.Empty;
                lineNo = 0;
                foreach (var line in lines)
                    _lines.Add(new CshLineVm
                    {
                        LineNo = ++lineNo,
                        AccountCode = line.AccountCode,
                        AccountName = line.AccountName,
                        Amount = Math.Max(line.Debit, line.Credit),
                        CostCenter = line.CostCenter ?? string.Empty,
                        Memo = line.Note ?? string.Empty
                    });
            }
            var partyLinesTotal = _lines.Sum(l => l.Amount);
            _txtAmount.Text = (partyLinesTotal > 0 ? partyLinesTotal : total / 2).ToString("0.00", CultureInfo.InvariantCulture);
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = snapshot.Status == JournalEntryStatus.Draft;
            _txtDocNo.IsReadOnly = true;
            SetReadOnly(true);
            RefreshCalculations();
        }
        catch (Exception ex)
        {
            ReportFailure("LoadByDocumentNumber", ex);
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        const string errorCode = "APP-2010";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var context = $"Operation={operation}; Window={GetType().Name}; Company={CurrentCompanyCode}";
        AppHost.Logging?.LogError("WindowPersistence",
            $"[{definition.Code}] {definition.Name} | {context} | {exception.Message}", exception);
        AppHost.Logging?.LogOperation("WindowPersistence", "DatabaseOperationFailed",
            $"Code={definition.Code}; {context}; Error={exception.Message}");
        MessageBox.Show(this, $"[{definition.Code}] {exception.Message}", Title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------------------------
    // Print: formal voucher (JrnWin A4 pattern) with amount in words.
    // ------------------------------------------------------------------
    private void PrintVoucher()
    {
        var page = BuildPrintPage();
        var paginator = new SinglePagePaginator(page);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, string.IsNullOrWhiteSpace(_txtDocNo.Text) ? Title : _txtDocNo.Text.Trim());
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
            Text = T(_kind == CshKind.Receipt ? "Vouchers.ReceiptTitle" : "Vouchers.PaymentTitle"),
            FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 10)
        });
        void Meta(string labelKey, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            row.Children.Add(new TextBlock { Text = T(labelKey) + ": ", FontWeight = FontWeights.SemiBold, FontSize = 12 });
            row.Children.Add(new TextBlock { Text = value, FontSize = 12 });
            stack.Children.Add(row);
        }
        Meta("Vouchers.VoucherNo", _txtDocNo.Text.Trim());
        Meta("Vouchers.Date", (_dtDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Meta("Vouchers.Treasury", _txtBox.Text.Trim());
        Meta(_kind == CshKind.Receipt ? "Vouchers.ReceivedFrom" : "Vouchers.PaidTo", _txtParty.Text.Trim());
        Meta("Vouchers.RefNo", _txtRefNo.Text.Trim());
        Meta("Vouchers.Memo", _txtMemo.Text.Trim());

        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var width in new[] { 36, 110, 200, 100, 110, 160 })
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
        Cell(0, 0, "#", true); Cell(0, 1, T("Vouchers.AccountCode"), true); Cell(0, 2, T("Vouchers.AccountName"), true);
        Cell(0, 3, T("MfgOrder.Amount"), true); Cell(0, 4, T("Vouchers.CostCenter"), true); Cell(0, 5, T("Vouchers.Memo"), true);
        var rowIndex = 1;
        foreach (var line in _lines)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rowIndex, 0, line.LineNo.ToString(CultureInfo.InvariantCulture));
            Cell(rowIndex, 1, line.AccountCode);
            Cell(rowIndex, 2, line.AccountName);
            Cell(rowIndex, 3, line.Amount.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 4, line.CostCenter);
            Cell(rowIndex, 5, line.Memo);
            rowIndex++;
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(rowIndex, 2, T("Invoice.Total"), true);
        Cell(rowIndex, 3, LinesTotal().ToString("N2", CultureInfo.InvariantCulture), true);
        stack.Children.Add(table);

        stack.Children.Add(new TextBlock
        {
            Text = $"{T("CashVoucher.AmountInWords")}: {NumWords.ToWords(HeaderAmount() > 0 ? HeaderAmount() : LinesTotal(), _txtCur.Text.Trim())}",
            FontSize = 12, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 16, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
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
