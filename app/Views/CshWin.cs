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
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
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
public sealed class CshWin : BaseWin, IWindowMode
{
    protected override string? ScheduleTintName => "Teal";

    private readonly CshKind _kind;

    /// <summary>The voucher kind this window was opened with (Step 11:
    /// the router's mode identity — Receipt and Payment are different
    /// documents and must never reactivate each other).</summary>
    public CshKind Kind => _kind;
    public string ModeKey => _kind.ToString();

    private readonly TextBox _txtDocNo = new();
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today };
    private readonly TextBox _txtBox = new();
    private readonly TextBox _txtParty = new();
    private readonly TextBox _txtPartyCode = new();
    private readonly ComboBox _cbPartyKind = new();
    private readonly TextBox _txtAmount = new() { Text = "0.00" };
    private readonly TextBox _txtCur = new() { Text = UsrPref.Load().CurrencyCode };
    private readonly TextBox _txtRate = new() { Text = "1.00" };
    private readonly TextBox _txtRefNo = new();
    private readonly TextBox _txtCc = new();
    private readonly TextBox _txtMemo = new();
    // Supplier advances (migration 110, Payment mode only): flag the
    // voucher as an advance for one PO; posting debits Advances (1215).
    private readonly CheckBox _chkAdvance = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _txtPoNo = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnReverse = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemoveLine = new();

    private readonly TextBlock _lblTotCalc = new();
    private readonly TextBlock _lblDiff = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();

    // Grid columns by identity (owner grid spec): account first;
    // CostCenter/InvoiceRef/Memo are the optional (chooser) columns.
    private DataGridTextColumn _accountCodeColumn = null!;
    private DataGridTextColumn _accountNameColumn = null!;
    private DataGridTextColumn _amountColumn = null!;
    private DataGridTextColumn _costCenterColumn = null!;
    private DataGridTextColumn _invoiceRefColumn = null!;
    private DataGridTextColumn _memoColumn = null!;
    private readonly Button _btnColumns = new();

    private readonly ObservableCollection<CshLineVm> _lines = new();
    private Guid? _currentJournalId;
    private bool _isPosted;

    public CshWin(CshKind kind = CshKind.Receipt)
    {
        _kind = kind;
        Width = 920;
        Height = 580;
        MinWidth = 660;
        MinHeight = 360;
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

        var toolbar = new WrapPanel();
        FlowAlign.End(toolbar);
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "Vouchers.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Vouchers.Post");
        StyleButton(_btnReverse, "Vouchers.Reverse");
        StyleButton(_btnDelete, "Common.Delete");
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemoveLine, "Invoice.RemoveLine");
        _btnDelete.IsEnabled = false; // Enabled when a voucher is loaded.

        _btnNew.Click += (_, _) => ResetVoucher();
        _btnLoad.Click += async (_, _) => await LoadVoucherAsync();
        _btnSave.Click += async (_, _) => await SaveVoucherAsync();
        _btnPost.Click += async (_, _) => await PostVoucherAsync();
        _btnReverse.Click += async (_, _) => await ReverseVoucherAsync();
        _btnDelete.Click += (_, _) => DeleteVoucher();
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

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnPost, _btnReverse, _btnDelete, _btnPrint, _btnAddLine, _btnRemoveLine })
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

        // Header = one shared column grid (4 pairs), side labels, edges
        // aligned across rows; the memo spans the last three pairs
        // (Phenix rule).
        var headerForm = CreateFormGrid(4);
        headerForm.Margin = new Thickness(0, 0, 0, 8);
        AddFormField(headerForm, 0, 0, "Vouchers.VoucherNo", _txtDocNo);
        AddFormField(headerForm, 0, 1, "Vouchers.Date", _dtDate);
        AddFormField(headerForm, 0, 2, "Vouchers.Treasury", _txtBox);
        AddFormField(headerForm, 0, 3, _kind == CshKind.Receipt ? "Vouchers.ReceivedFrom" : "Vouchers.PaidTo", _txtParty);
        AddFormField(headerForm, 1, 0, "Invoice.Total", _txtAmount);
        AddFormField(headerForm, 1, 1, "Vouchers.Currency", _txtCur);
        AddFormField(headerForm, 1, 2, "Vouchers.ExchangeRate", _txtRate);
        AddFormField(headerForm, 1, 3, "Vouchers.RefNo", _txtRefNo);
        AddFormField(headerForm, 2, 0, "Vouchers.CostCenter", _txtCc);
        AddFormField(headerForm, 2, 1, "Vouchers.Memo", _txtMemo, spanPairs: 3);
        // Step 4: the party is a real lookup against the canonical party
        // master (code + kind), not free text — a resolved party makes the
        // voucher settle party_ledger at posting (see PartyLedgerSvc).
        _cbPartyKind.Items.Add(new ComboBoxItem { Tag = "C" });
        _cbPartyKind.Items.Add(new ComboBoxItem { Tag = "S" });
        _cbPartyKind.SelectedIndex = _kind == CshKind.Receipt ? 0 : 1;
        AddFormField(headerForm, 3, 0, "Vouchers.PartyCode", _txtPartyCode);
        AddFormField(headerForm, 3, 1, "Vouchers.PartyKind", _cbPartyKind);
        if (_kind == CshKind.Payment)
        {
            AddFormField(headerForm, 4, 0, "Vouchers.AdvanceForPo", _chkAdvance);
            AddFormField(headerForm, 4, 1, "Vouchers.PurchaseOrderNo", _txtPoNo);
        }
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
        _grid.ItemsSource = _lines;
        // Excel-like grid (owner spec item 1): line numbers live in
        // the fixed row-header column now; the LineNo data column is
        // dropped from the grid (LineNo is still maintained in code
        // and persisted as before). Account first (item 5), then the
        // amount, then the optional chooser columns (item 4).
        GridEngine.ApplyStandard(_grid, "TableSurfaceBrush", "TableAlternateRowBrush");
        _accountCodeColumn = TextCol(nameof(CshLineVm.AccountCode), 120);
        _accountNameColumn = TextCol(nameof(CshLineVm.AccountName), 220);
        _amountColumn = TextCol(nameof(CshLineVm.AmountText), 110);
        _costCenterColumn = TextCol(nameof(CshLineVm.CostCenter), 110);
        _invoiceRefColumn = TextCol(nameof(CshLineVm.InvoiceRef), 100);
        _memoColumn = TextCol(nameof(CshLineVm.Memo), 200);
        _grid.Columns.Add(_accountCodeColumn);
        _grid.Columns.Add(_accountNameColumn);
        _grid.Columns.Add(_amountColumn);
        _grid.Columns.Add(_costCenterColumn);
        _grid.Columns.Add(_invoiceRefColumn);
        _grid.Columns.Add(_memoColumn);
        ApplySavedColumnLayout();

        _grid.CellEditEnding += OnCellEditEnding;

        // Column chooser at the END of the grid header strip.
        var chooserStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };
        FlowAlign.SetAlignEnd(chooserStrip, true);
        StyleButton(_btnColumns, "Common.Columns");
        _btnColumns.Click += OnColumnsClick;
        chooserStrip.Children.Add(_btnColumns);
        var gridHost = new DockPanel();
        DockPanel.SetDock(chooserStrip, Dock.Top);
        gridHost.Children.Add(chooserStrip);
        gridHost.Children.Add(_grid);

        Grid.SetRow(gridHost, 1);
        body.Children.Add(gridHost);

        root.Children.Add(body);
        Content = root;
    }

    private void RenumberLines()
    {
        for (var i = 0; i < _lines.Count; i++)
            _lines[i].LineNo = i + 1;
    }

    /// <summary>The next sequential receipt or payment voucher number from its
    /// independent company-scoped series. A failure is reported through the
    /// error catalog and leaves the field empty rather than falling
    /// back to a fabricated timestamp number.</summary>
    private string AllocateVoucherNumber()
    {
        try
        {
            if (AppHost.JournalStore is not { } store)
                throw new InvalidOperationException("The journal store is not initialized.");
            return store.NextDocumentNumber(
                CurrentCompanyCode, _kind == CshKind.Receipt ? "RCV" : "PAY",
                DateOnly.FromDateTime(DateTime.Today));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NumberingAllocationFailed, ex, "CshWin.AllocateVoucherNumber");
            return string.Empty;
        }
    }

    private void ResetVoucher()
    {
        _currentJournalId = null;
        _isPosted = false;
        _txtDocNo.Text = AllocateVoucherNumber();
        _txtDocNo.IsReadOnly = true;
        _dtDate.SelectedDate = DateTime.Today;
        _txtParty.Text = string.Empty;
        _txtPartyCode.Text = string.Empty;
        _chkAdvance.IsChecked = false;
        _txtPoNo.Text = string.Empty;
        _cbPartyKind.SelectedIndex = _kind == CshKind.Receipt ? 0 : 1;
        _btnReverse.IsEnabled = false;
        _btnDelete.IsEnabled = false;
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
        _txtPartyCode.IsReadOnly = readOnly;
        _txtPoNo.IsReadOnly = readOnly;
        _chkAdvance.IsEnabled = !readOnly;
        _cbPartyKind.IsEnabled = !readOnly;
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

    private static (JournalService Journal, JournalRepository Store, Guid CompanyId, string Actor) RequireJournalContext()
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
                // An advance voucher's settlement line is the Advances
                // account by definition; default an empty account to it
                // (posting re-validates the debit lands on 1215 exactly).
                if (_chkAdvance.IsChecked == true && string.IsNullOrWhiteSpace(line.AccountCode))
                    line.AccountCode = GlAccounts.SupplierAdvances;
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

            // Step 4: a party code, when given, must resolve against the
            // canonical party master BEFORE anything is saved — an
            // unresolved code blocks the voucher instead of silently
            // posting a GL-only entry with a free-text name (B-03/L-7).
            PartyLedgerSvc.VoucherPartyLink? partyLink = null;
            if (_chkAdvance.IsChecked == true && string.IsNullOrWhiteSpace(_txtPartyCode.Text))
                throw new InvalidOperationException(T("CashVoucher.AdvanceSupplierOnly"));
            if (!string.IsNullOrWhiteSpace(_txtPartyCode.Text))
            {
                var resolved = PartyLedgerSvc.ResolveParty(_txtPartyCode.Text.Trim(), PartyKind)
                    ?? throw new InvalidOperationException($"{T("CashVoucher.PartyNotFound")}: {_txtPartyCode.Text.Trim()}");
                long? advancePoId = null;
                string? advancePoNo = null;
                if (_chkAdvance.IsChecked == true)
                {
                    // Advance marking (Y1): the PO must resolve in this
                    // company, be Posted, and belong to the same supplier
                    // — posting enforces all of it again in-transaction.
                    if (_kind != CshKind.Payment || PartyKind != "S")
                        throw new InvalidOperationException(T("CashVoucher.AdvanceSupplierOnly"));
                    advancePoNo = _txtPoNo.Text.Trim();
                    var po = PartyLedgerSvc.ResolvePurchaseOrder(advancePoNo)
                        ?? throw new InvalidOperationException($"{T("CashVoucher.PurchaseOrderNotFound")}: {advancePoNo}");
                    if (po.PartyId != resolved.LedgerId || po.Status != "Posted")
                        throw new InvalidOperationException($"{T("CashVoucher.AdvancePoInvalid")}: {advancePoNo}");
                    advancePoId = po.DocId;
                }
                partyLink = new PartyLedgerSvc.VoucherPartyLink(
                    PartyKind, resolved.LedgerId, _txtPartyCode.Text.Trim(),
                    _kind == CshKind.Receipt ? "RCV" : "PAY", _txtDocNo.Text.Trim(), total, currency,
                    PoDocId: advancePoId, IsAdvance: advancePoId is not null, PoDocNo: advancePoNo);
                if (string.IsNullOrWhiteSpace(_txtParty.Text))
                    _txtParty.Text = resolved.Name;
            }

            var description = string.IsNullOrWhiteSpace(_txtMemo.Text)
                ? $"{(_kind == CshKind.Receipt ? T("Vouchers.ReceiptTitle") : T("Vouchers.PaymentTitle"))} — {_txtParty.Text.Trim()}"
                : _txtMemo.Text.Trim();
            // G23: the party link travels INTO the draft creation, so
            // JournalRepository writes voucher_party_links inside the
            // same transaction as the draft — no separate-connection
            // write afterwards, no link-less draft on a crash.
            var id = await journal.CreateDraftAsync(
                companyId, DateOnly.FromDateTime(selectedDate), description, actor, requests,
                referenceNumber: string.IsNullOrWhiteSpace(_txtRefNo.Text) ? null : _txtRefNo.Text.Trim(),
                currencyCode: currency, exchangeRate: rate,
                documentNumber: _txtDocNo.Text.Trim(),
                partyLink: partyLink?.ToRequest(CurrentCompanyCode));
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
            var budgetWarnings = await journal.PostAsync(_currentJournalId.Value, CurrentUserId);
            _isPosted = true;
            _btnPost.IsEnabled = false;
            _btnReverse.IsEnabled = true;
            SetReadOnly(true);
            RefreshCalculations();
            if (budgetWarnings.Count > 0)
            {
                // Phase B 055: Warn-mode budget overruns (see JrnWin).
                var warningText = string.Join(Environment.NewLine, budgetWarnings.Select(w =>
                    Tf("Budget.WarningLine", w.BudgetName, w.Horizon, w.Overage)));
                MessageBox.Show(this, warningText, T("Budget.WarningTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(this, T("Vouchers.Posted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ReportFailure("Post", ex);
        }
    }

    private async Task ReverseVoucherAsync()
    {
        if (!_currentJournalId.HasValue || !_isPosted) return;
        if (!AppHost.Permissions.Has(PermCat.Vouchers.Post))
        {
            MessageBox.Show(this, T("CashVoucher.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (AppHost.Journal is not { } journal)
                throw new InvalidOperationException(T("CashVoucher.ServiceUnavailable"));
            if (string.IsNullOrWhiteSpace(CurrentUserId))
                throw new InvalidOperationException(T("CashVoucher.NoUser"));
            if (!AskConfirm("Vouchers.ConfirmReverse")) return;
            // The shared reversal core mirrors the voucher's party_ledger
            // settlement in the same transaction (migration 061 link), so
            // the party balance restores with the GL.
            await journal.ReverseAsync(_currentJournalId.Value, CurrentUserId);
            _btnReverse.IsEnabled = false;
            _btnPost.IsEnabled = false;
            RefreshCalculations();
            MessageBox.Show(this, T("Vouchers.Reversed"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Reverse", ex);
        }
    }

    /// <summary>Soft delete (SOFT_DELETE_STUDY): the voucher moves to
    /// the recycle bin; DocBinSvc reverses its posting effects while it
    /// is binned and reinstates them on restore. The form resets.</summary>
    private void DeleteVoucher()
    {
        if (!_currentJournalId.HasValue) return;
        if (!AskConfirm("RecycleBin.ConfirmDeleteJournal")) return;
        try
        {
            DocBinSvc.SoftDeleteJournal(_currentJournalId.Value,
                string.IsNullOrWhiteSpace(CurrentUserId) ? "system" : CurrentUserId);
            MessageBox.Show(this, T("RecycleBin.MovedToBin"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            ResetVoucher();
        }
        catch (Exception ex)
        {
            ReportFailure("Delete", ex);
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
            var snapshot = await journal.FindByDocumentNumberAsync(companyId, documentNumber, DateOnly.FromDateTime(_dtDate.SelectedDate ?? DateTime.Today))
                ?? throw new InvalidOperationException($"{T("CashVoucher.NotFound")}: {documentNumber}");
            _currentJournalId = snapshot.Id;
            _isPosted = snapshot.Status != JournalEntryStatus.Draft;
            var link = PartyLedgerSvc.LoadVoucherLink(snapshot.Id);
            if (link is not null)
            {
                _txtPartyCode.Text = link.PartyCode;
                _cbPartyKind.SelectedIndex = link.PartyKind == "S" ? 1 : 0;
                _chkAdvance.IsChecked = link.IsAdvance;
                _txtPoNo.Text = link.PoDocNo ?? string.Empty;
            }
            _btnReverse.IsEnabled = _isPosted && snapshot.Status == JournalEntryStatus.Posted;
            _btnDelete.IsEnabled = snapshot.Status != JournalEntryStatus.Reversed;
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
        if (exception is AppError appError)
        {
            // Coded business blocks (e.g. ACC-5007/5008 cost-center rules at
            // posting): localized catalog message, not the raw text.
            AppHost.Errors.Report(appError.Code, appError, $"CshWin.{operation}");
            var info = AppHost.Errors.Resolve(appError.Code, appError.Args.ToArray());
            var body = info.Message;
            if (!string.IsNullOrWhiteSpace(info.Hint))
                body += $"{Environment.NewLine}{Environment.NewLine}{info.Hint}";
            body += $"{Environment.NewLine}{Environment.NewLine}(Error {info.Code})";
            MessageBox.Show(this, body, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

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
        // Options-governed print behaviour (§3.3): default printer +
        // the per-user voucher copy count.
        PrintSetSvc.ApplyDialogSettings(dlg, UsrPref.Load().PrintVoucherCopies);
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
        // Headers bind by column identity (chooser-safe).
        _accountCodeColumn.Header = T("Vouchers.AccountCode");
        _accountNameColumn.Header = T("Vouchers.AccountName");
        _amountColumn.Header = T("MfgOrder.Amount");
        _costCenterColumn.Header = T("Vouchers.CostCenter");
        _invoiceRefColumn.Header = T("Vouchers.RefNo");
        _memoColumn.Header = T("Vouchers.Memo");
        if (_cbPartyKind.Items.Count == 2)
        {
            if (_cbPartyKind.Items[0] is ComboBoxItem customer) customer.Content = T("CustCard.Customer");
            if (_cbPartyKind.Items[1] is ComboBoxItem supplier) supplier.Content = T("CustCard.Supplier");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        RefreshCalculations();
    }

    // ------------------------------------------------------------------
    // Account entry (owner grid spec item 5, mirroring material
    // entry): committing the ACCOUNT cell resolves a typed code or
    // (partial) name immediately when unambiguous; otherwise the
    // chart of accounts opens as the search window, pre-filtered.
    // ------------------------------------------------------------------

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is CshLineVm line && ReferenceEquals(e.Column, _accountCodeColumn))
                ResolveAccountEntry(line);
            RefreshCalculations();
        }));
    }

    private void ResolveAccountEntry(CshLineVm line)
    {
        var text = line.AccountCode?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return;
        if (string.Equals(text, line.LastAccountText, StringComparison.Ordinal))
            return;
        line.LastAccountText = text;

        var match = AccRepo.MatchAccount(text);
        if (match is { Kind: AccRepo.AccountMatchKind.Exact or AccRepo.AccountMatchKind.UniquePartial, Account: not null })
        {
            ApplyAccount(line, match.Account.Code, AccountDisplayName(match.Account));
            return;
        }

        var pick = CoaWin.ShowPicker(this, text);
        if (pick is not null)
        {
            ApplyAccount(line, pick.Code, pick.DisplayName);
        }
        else if (string.IsNullOrWhiteSpace(line.AccountName))
        {
            _lblStatus.Text = Tf("Vouchers.AccountNotMatched", text);
        }
    }

    private static void ApplyAccount(CshLineVm line, string code, string displayName)
    {
        line.AccountCode = code;
        line.AccountName = displayName;
        line.LastAccountText = code;
    }

    private static string AccountDisplayName(AccRepo.AccRec account)
        => string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
            ? (!string.IsNullOrWhiteSpace(account.Name) ? account.Name : account.NameEn)
            : (!string.IsNullOrWhiteSpace(account.NameEn) ? account.NameEn : account.Name);

    // ------------------------------------------------------------------
    // Optional columns (spec item 4) via the shared ColChsWin;
    // per-user persistence through WinPref (scope per voucher kind).
    // ------------------------------------------------------------------

    private sealed record OptionalColumn(string Key, DataGridColumn Column, string HeaderKey);

    private List<OptionalColumn> OptionalColumns() => new()
    {
        new("CostCenter", _costCenterColumn, "Vouchers.CostCenter"),
        new("InvoiceRef", _invoiceRefColumn, "Vouchers.RefNo"),
        new("Memo", _memoColumn, "Vouchers.Memo"),
    };

    private string ColumnScope()
    {
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        var company = AppState.CurrentCompanyCode ?? "no-company";
        return $"{company}/{user}/Vch:CshWin:{_kind}";
    }

    private void ApplySavedColumnLayout()
    {
        var saved = WinPref.LoadReportColumns(ColumnScope());
        if (saved is null || saved.Count == 0)
            return;
        var optionals = OptionalColumns();
        foreach (var rec in saved)
        {
            var opt = optionals.FirstOrDefault(o => string.Equals(o.Key, rec.Key, StringComparison.Ordinal));
            if (opt is null)
                continue;
            opt.Column.Visibility = rec.Visible ? Visibility.Visible : Visibility.Collapsed;
            opt.Column.DisplayIndex = 3 + Math.Max(0, rec.Order);
            if (rec.Width is >= 30 and <= 600)
                opt.Column.Width = new DataGridLength(rec.Width);
        }
    }

    private void OnColumnsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var optionals = OptionalColumns();
            var items = optionals
                .OrderBy(o => o.Column.DisplayIndex)
                .Select(o => new ColChsWin.Item
                {
                    Key = o.Key,
                    Header = T(o.HeaderKey),
                    Visible = o.Column.Visibility == Visibility.Visible,
                    Locked = false
                })
                .ToList();
            var win = new ColChsWin(items) { Owner = this };
            if (win.ShowDialog() != true || win.Result is null)
                return;

            var recs = new List<WinPref.ColumnRec>();
            for (var i = 0; i < win.Result.Count; i++)
            {
                var chosen = win.Result[i];
                var opt = optionals.First(o => string.Equals(o.Key, chosen.Key, StringComparison.Ordinal));
                opt.Column.Visibility = chosen.Visible ? Visibility.Visible : Visibility.Collapsed;
                opt.Column.DisplayIndex = 3 + i;
                recs.Add(new WinPref.ColumnRec
                {
                    Key = opt.Key,
                    Visible = chosen.Visible,
                    Order = i,
                    Width = opt.Column.ActualWidth > 0 ? opt.Column.ActualWidth : opt.Column.Width.Value
                });
            }
            WinPref.SaveReportColumns(ColumnScope(), recs);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CshWin.OnColumnsClick");
        }
    }

    private string PartyKind => _cbPartyKind.SelectedIndex == 1 ? "S" : "C";

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

    private sealed class CshLineVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _accCode = "", _accName = "", _costCenter = "", _invoiceRef = "", _memo = "";
        private decimal _amount;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string AccountCode { get => _accCode; set { _accCode = value; OnChanged(); } }

        /// <summary>UI state only (never persisted): the last typed
        /// account text the entry matcher already processed.</summary>
        public string? LastAccountText { get; set; }
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
