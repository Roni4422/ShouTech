using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Centralized styling, live balancing badge, and two-tone alternating row DataGrid (#FFFFFF / #E4F4F8).
/// </summary>
public sealed class JrnWin : BaseWin
{
    protected override string? ScheduleTintName => "Blue";

    // Note: the former ShowOperationUnavailable helper was removed — every
    // JrnWin operation (New/Load/Save/Post/Reverse/Print) is implemented;
    // no placeholder operations remain in this window.

    // CA1863: cache CompositeFormat instances for repeatedly used format strings.
    private static readonly ConcurrentDictionary<string, CompositeFormat> CompositeFormats =
        new(StringComparer.Ordinal);

    private readonly TextBox _txtDocNo = new();
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today };
    private readonly TextBox _txtRefNo = new();
    private readonly TextBox _txtCur = new() { Text = UsrPref.Load().CurrencyCode };
    private readonly TextBox _txtRate = new() { Text = "1.00" };
    private readonly TextBox _txtCc = new();
    private readonly TextBox _txtMemo = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnReverse = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemoveLine = new();
    private readonly Button _btnAutoBal = new();

    private readonly TextBlock _lblTotDr = new();
    private readonly TextBlock _lblTotCr = new();
    private readonly Border _badgeDiff = new();
    private readonly TextBlock _lblDiff = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();

    // Grid columns by identity (owner grid spec): the account is the
    // first field; CostCenter/Memo are the optional (chooser) columns.
    private DataGridTextColumn _accountCodeColumn = null!;
    private DataGridTextColumn _accountNameColumn = null!;
    private DataGridTextColumn _debitColumn = null!;
    private DataGridTextColumn _creditColumn = null!;
    private DataGridTextColumn _costCenterColumn = null!;
    private DataGridTextColumn _memoColumn = null!;
    private readonly Button _btnColumns = new();
    private readonly ObservableCollection<JrnLineVm> _lines = new();
    private Guid? _currentJournalId;
    private bool _journalPosted;
    private bool _journalReversed;

    /// <summary>
    /// True while the window shows a loaded DRAFT that may still be edited
    /// and saved back (JournalService.UpdateDraftAsync). Posted/reversed
    /// entries load read-only and never set this flag.
    /// </summary>
    private bool _loadedDraft;

    public JrnWin()
    {
        Width = 920;
        Height = 580;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetVoucher();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0) };
        FlowAlign.End(toolbar);
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "Vouchers.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Vouchers.Post");
        StyleButton(_btnReverse, "Vouchers.Reverse");
        _btnReverse.IsEnabled = false;
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemoveLine, "Invoice.RemoveLine");
        StyleButton(_btnAutoBal, "Vouchers.AutoBalance");

        _btnNew.Click += (_, _) => ResetVoucher();
        _btnLoad.Click += async (_, _) => await LoadVoucherAsync();
        _btnSave.Click += async (_, _) => await SaveVoucherAsync();
        _btnPost.Click += async (_, _) => await PostVoucherAsync();
        _btnReverse.Click += async (_, _) => await ReverseVoucherAsync();
        _btnPrint.Click += (_, _) => PrintVoucher();
        _btnAddLine.Click += (_, _) =>
        {
            var line = new JrnLineVm { LineNo = _lines.Count + 1 };
            _lines.Add(line);
            _grid.SelectedItem = line;
            _grid.ScrollIntoView(line);
        };
        _btnRemoveLine.Click += (_, _) =>
        {
            if (_grid.SelectedItem is JrnLineVm selected && _lines.Count > 1)
            {
                _lines.Remove(selected);
                RenumberLines();
                RefreshBalances();
            }
        };
        _btnAutoBal.Click += (_, _) => AutoBalance();

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnPost, _btnReverse, _btnPrint, _btnAddLine, _btnRemoveLine, _btnAutoBal })
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

        // 2. Footer summary with Live Difference Badge
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
        var leftSummary = new StackPanel { Orientation = Orientation.Horizontal };
        _lblTotCr.Margin = new Thickness(24, 0, 0, 0);
        _lblStatus.Margin = new Thickness(24, 0, 0, 0);
        leftSummary.Children.Add(_lblTotDr);
        leftSummary.Children.Add(_lblTotCr);
        leftSummary.Children.Add(_lblStatus);
        DockPanel.SetDock(leftSummary, Dock.Left);
        footerContent.Children.Add(leftSummary);

        _badgeDiff.CornerRadius = new CornerRadius(4);
        _badgeDiff.Padding = new Thickness(10, 3, 10, 3);
        _badgeDiff.Child = _lblDiff;
        _lblDiff.FontWeight = FontWeights.SemiBold;
        _lblDiff.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF");
        DockPanel.SetDock(_badgeDiff, Dock.Right);
        footerContent.Children.Add(_badgeDiff);

        footer.Child = footerContent;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        // 3. Center Body
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header = one shared column grid (4 pairs), side labels, edges
        // aligned across both rows; the memo spans the last two pairs
        // (Phenix rule).
        var headerForm = CreateFormGrid(4);
        headerForm.Margin = new Thickness(0, 0, 0, 8);
        AddFormField(headerForm, 0, 0, "Vouchers.VoucherNo", _txtDocNo);
        AddFormField(headerForm, 0, 1, "Vouchers.Date", _dtDate);
        AddFormField(headerForm, 0, 2, "Vouchers.RefNo", _txtRefNo);
        AddFormField(headerForm, 0, 3, "Vouchers.Currency", _txtCur);
        AddFormField(headerForm, 1, 0, "Vouchers.ExchangeRate", _txtRate);
        AddFormField(headerForm, 1, 1, "Vouchers.CostCenter", _txtCc);
        AddFormField(headerForm, 1, 2, "Vouchers.Memo", _txtMemo, spanPairs: 2);

        Grid.SetRow(headerForm, 0);
        body.Children.Add(headerForm);

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
        // Excel-like grid (owner spec item 1): the fixed row-header
        // column carries the line numbers now — the old LineNo data
        // column is dropped from the grid (LineNo itself is still
        // maintained in code and persisted exactly as before).
        // Account first (item 5), then amounts, then the optional
        // columns managed by the chooser (item 4).
        DataGridTableBehavior.SetShowRowNumbers(_grid, true);
        _accountCodeColumn = TextCol(nameof(JrnLineVm.AccountCode), 110);
        _accountNameColumn = TextCol(nameof(JrnLineVm.AccountName), 220, readOnly: true);
        _debitColumn = TextCol(nameof(JrnLineVm.DebitText), 100);
        _creditColumn = TextCol(nameof(JrnLineVm.CreditText), 100);
        _costCenterColumn = TextCol(nameof(JrnLineVm.CostCenter), 110);
        _memoColumn = TextCol(nameof(JrnLineVm.Memo), 200);
        _grid.Columns.Add(_accountCodeColumn);
        _grid.Columns.Add(_accountNameColumn);
        _grid.Columns.Add(_debitColumn);
        _grid.Columns.Add(_creditColumn);
        _grid.Columns.Add(_costCenterColumn);
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

    private void ResetVoucher()
    {
        _txtDocNo.Text = "JV-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _currentJournalId = null;
        _loadedDraft = false;
        _journalPosted = false;
        _journalReversed = false;
        _dtDate.SelectedDate = DateTime.Today;
        _txtRefNo.Text = string.Empty;
        _txtCc.Text = string.Empty;
        _txtMemo.Text = string.Empty;
        _lines.Clear();
        _lines.Add(new JrnLineVm { LineNo = 1 });
        _lines.Add(new JrnLineVm { LineNo = 2 });
        _btnLoad.IsEnabled = true;
        _btnSave.IsEnabled = true;
        _btnPost.IsEnabled = true;
        _btnReverse.IsEnabled = false;
        SetVoucherReadOnly(false);
        RefreshBalances();
    }

    private void RefreshBalances()
    {
        var totalDebit = _lines.Sum(l => l.Debit);
        var totalCredit = _lines.Sum(l => l.Credit);
        var diff = totalDebit - totalCredit;

        _lblTotDr.Text = $"{T("Vouchers.TotalDebit")}: {totalDebit:N2} {_txtCur.Text}";
        _lblTotCr.Text = $"{T("Vouchers.TotalCredit")}: {totalCredit:N2} {_txtCur.Text}";

        if (Math.Abs(diff) < 0.001m && (totalDebit > 0 || totalCredit > 0))
        {
            _badgeDiff.Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SuccessBrush", "#198754");
            _lblDiff.Text = $"{T("Vouchers.Balanced")} (0.00)";
        }
        else
        {
            _badgeDiff.Background = ShouTech.App.Services.VisuId.ResolveBrushResource("ErrorBrush", "#C0392B");
            _lblDiff.Text = $"{T("Vouchers.Difference")}: {Math.Abs(diff):N2} {_txtCur.Text}";
        }
    }

    private void AutoBalance()
    {
        var totalDebit = _lines.Sum(l => l.Debit);
        var totalCredit = _lines.Sum(l => l.Credit);
        var diff = totalDebit - totalCredit;

        if (Math.Abs(diff) < 0.001m) return;

        var lastLine = _lines.LastOrDefault(l => l.Debit == 0 && l.Credit == 0);
        if (lastLine == null)
        {
            lastLine = new JrnLineVm { LineNo = _lines.Count + 1 };
            _lines.Add(lastLine);
        }

        if (diff > 0)
        {
            lastLine.Credit = diff;
            lastLine.Debit = 0;
        }
        else
        {
            lastLine.Debit = Math.Abs(diff);
            lastLine.Credit = 0;
        }

        RefreshBalances();
    }

    private async Task LoadVoucherAsync()
    {
        var documentNumber = _txtDocNo.Text.Trim();
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            MessageBox.Show(this, T("Vouchers.EntryNumberRequired"), T("Common.Warning"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var user = ShouTech.App.Services.UsrSession.Current;
            var companyCode = user?.CompanyId;
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new InvalidOperationException("There is no active company in the user session.");
            if (ShouTech.App.Services.AppHost.JournalStore is not { } store ||
                ShouTech.App.Services.AppHost.Journal is not { } journal)
                throw new InvalidOperationException("The journal service is not initialized; check the database connection setup.");

            var companyId = await store.ResolveCompanyIdAsync(companyCode);
            var snapshot = await journal.FindByDocumentNumberAsync(companyId, documentNumber);
            if (snapshot is null)
                throw new InvalidOperationException($"Journal entry '{documentNumber}' was not found in the active company.");

            _currentJournalId = snapshot.Id;
            _journalPosted = snapshot.Status != ShouTech.Domain.Entities.JournalEntryStatus.Draft;
            _journalReversed = snapshot.Status == ShouTech.Domain.Entities.JournalEntryStatus.Reversed;
            _txtDocNo.Text = snapshot.DocumentNumber ?? string.Empty;
            _dtDate.SelectedDate = snapshot.EntryDate.ToDateTime(TimeOnly.MinValue);
            _txtRefNo.Text = snapshot.ReferenceNumber ?? string.Empty;
            _txtCur.Text = snapshot.CurrencyCode;
            _txtRate.Text = snapshot.ExchangeRate.ToString("0.########", CultureInfo.CurrentCulture);
            _txtMemo.Text = snapshot.Description;

            _lines.Clear();
            foreach (var line in snapshot.Lines)
            {
                _lines.Add(new JrnLineVm
                {
                    LineNo = line.LineNumber,
                    AccountCode = line.AccountCode,
                    AccountName = line.AccountName,
                    Debit = line.Debit,
                    Credit = line.Credit,
                    CostCenter = line.CostCenter ?? string.Empty,
                    Memo = line.Note ?? string.Empty
                });
            }

            var costCenters = snapshot.Lines
                .Select(line => line.CostCenter?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _txtCc.Text = costCenters.Length == 1 &&
                snapshot.Lines.All(line => string.IsNullOrWhiteSpace(line.CostCenter) ||
                    string.Equals(line.CostCenter.Trim(), costCenters[0], StringComparison.OrdinalIgnoreCase))
                ? costCenters[0]!
                : string.Empty;

            // A loaded draft stays editable and can be saved back; posted
            // and reversed entries are immutable and load read-only.
            var isDraft = snapshot.Status == ShouTech.Domain.Entities.JournalEntryStatus.Draft;
            _loadedDraft = isDraft;
            _btnSave.IsEnabled = isDraft;
            _btnPost.IsEnabled = isDraft;
            _btnReverse.IsEnabled = snapshot.Status == ShouTech.Domain.Entities.JournalEntryStatus.Posted;
            SetVoucherReadOnly(!isDraft);
            RefreshBalances();

            MessageBox.Show(
                this,
                FormatLocalized(T("Vouchers.EntryLoaded"), snapshot.Status, snapshot.Id.ToString("D")),
                T("Vouchers.EntryLoadedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (ex is not ShouTech.Domain.AppError)
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "JrnWin.LoadVoucherAsync");
            ReportPersistenceFailure("LoadByDocumentNumber", ex);
        }
    }

    private void SetVoucherReadOnly(bool readOnly)
    {
        _grid.IsReadOnly = readOnly;
        _dtDate.IsEnabled = !readOnly;
        _txtDocNo.IsReadOnly = readOnly;
        _txtRefNo.IsReadOnly = readOnly;
        _txtCur.IsReadOnly = readOnly;
        _txtRate.IsReadOnly = readOnly;
        _txtCc.IsReadOnly = readOnly;
        _txtMemo.IsReadOnly = readOnly;
        _btnAddLine.IsEnabled = !readOnly;
        _btnRemoveLine.IsEnabled = !readOnly;
        _btnAutoBal.IsEnabled = !readOnly;
    }

    /// <summary>
    /// Formats a localized pattern using a cached CompositeFormat
    /// to avoid re-parsing the format string on every call (CA1863),
    /// with the current UI culture applied explicitly (CA1305).
    /// </summary>
    private static string FormatLocalized(string pattern, params object?[] args)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        var compositeFormat = CompositeFormats.GetOrAdd(
            pattern,
            static value => CompositeFormat.Parse(value));

        return string.Format(CultureInfo.CurrentCulture, compositeFormat, args);
    }

    private async Task SaveVoucherAsync()
    {
        if (_currentJournalId.HasValue && !_loadedDraft)
        {
            MessageBox.Show(this, T("Vouchers.EntryAlreadySaved"), T("Vouchers.Saved"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            var user = ShouTech.App.Services.UsrSession.Current;
            var companyCode = user?.CompanyId;
            var actor = !string.IsNullOrWhiteSpace(user?.Id) ? user.Id : ShouTech.App.Services.AppHost.Session.CurrentUser;
            if (string.IsNullOrWhiteSpace(companyCode))
                throw new InvalidOperationException("There is no active company in the user session.");
            if (string.IsNullOrWhiteSpace(actor))
                throw new InvalidOperationException("Could not determine the current user to save the entry.");
            if (ShouTech.App.Services.AppHost.JournalStore is not { } store ||
                ShouTech.App.Services.AppHost.Journal is not { } journal)
                throw new InvalidOperationException("The journal save service is not initialized; check the database connection and its settings.");

            if (_dtDate.SelectedDate is not DateTime selectedDate)
                throw new InvalidOperationException("The entry date is required.");
            if (string.IsNullOrWhiteSpace(_txtDocNo.Text))
                throw new InvalidOperationException("Entry Number Required.");
            if (string.IsNullOrWhiteSpace(_txtMemo.Text))
                throw new InvalidOperationException("The entry description is required.");

            var currency = _txtCur.Text.Trim().ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                throw new InvalidOperationException("The currency code must consist of three Latin letters.");
            if (!decimal.TryParse(_txtRate.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var rate) &&
                !decimal.TryParse(_txtRate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out rate))
                throw new InvalidOperationException("The exchange rate is invalid.");
            if (rate <= 0)
                throw new InvalidOperationException("The exchange rate must be greater than zero.");
            if (_lines.Count < 2)
                throw new InvalidOperationException("The entry requires at least two lines.");

            var companyId = await store.ResolveCompanyIdAsync(companyCode);
            var requests = new List<ShouTech.Application.Services.JournalLineRequest>();
            foreach (var line in _lines)
            {
                if (string.IsNullOrWhiteSpace(line.AccountCode))
                    throw new InvalidOperationException($"The account code is required on line {line.LineNo}.");
                if (line.Debit < 0 || line.Credit < 0 || (line.Debit > 0 && line.Credit > 0) ||
                    (line.Debit == 0 && line.Credit == 0))
                    throw new InvalidOperationException($"Enter exactly one of debit or credit with a positive amount on line {line.LineNo}.");

                var accountId = await store.ResolveAccountIdAsync(companyId, line.AccountCode);
                line.AccountName = await store.ResolveAccountNameAsync(companyId, line.AccountCode);
                var costCenter = string.IsNullOrWhiteSpace(line.CostCenter) ? _txtCc.Text : line.CostCenter;
                requests.Add(new ShouTech.Application.Services.JournalLineRequest(
                    accountId, line.Debit, line.Credit, line.Memo, costCenter));
            }

            if (_currentJournalId.HasValue)
            {
                // Saving an edited draft back: the document number stays the
                // entry's identity; header and lines are replaced atomically
                // under the same validation as a new draft, and the change
                // is written to the journal audit trail.
                await journal.UpdateDraftAsync(
                    companyId,
                    _currentJournalId.Value,
                    DateOnly.FromDateTime(selectedDate),
                    _txtMemo.Text.Trim(),
                    actor,
                    requests,
                    referenceNumber: _txtRefNo.Text,
                    currencyCode: currency,
                    exchangeRate: rate);
                _loadedDraft = false;
                _btnSave.IsEnabled = false;
                _btnPost.IsEnabled = true;
                _btnReverse.IsEnabled = false;
                SetVoucherReadOnly(true);
                MessageBox.Show(this, FormatLocalized(T("Vouchers.EntryDraftUpdated"), _currentJournalId.Value.ToString("D")), T("Vouchers.Saved"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var id = await journal.CreateDraftAsync(
                companyId,
                DateOnly.FromDateTime(selectedDate),
                _txtMemo.Text.Trim(),
                actor,
                requests,
                referenceNumber: _txtRefNo.Text,
                currencyCode: currency,
                exchangeRate: rate,
                documentNumber: _txtDocNo.Text);
            _currentJournalId = id;
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = true;
            _btnReverse.IsEnabled = false;
            SetVoucherReadOnly(true);
            MessageBox.Show(this, FormatLocalized(T("Vouchers.EntryDraftSaved"), id.ToString("D")), T("Vouchers.Saved"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (ex is not ShouTech.Domain.AppError)
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "JrnWin.SaveVoucherAsync");
            ReportPersistenceFailure("SaveDraft", ex);
        }
    }

    private async Task PostVoucherAsync()
    {
        if (_journalPosted)
            return;

        try
        {
            if (!_currentJournalId.HasValue || _loadedDraft)
            {
                // New entry, or a loaded draft with unsaved edits: persist
                // first so posting always acts on what the user sees.
                await SaveVoucherAsync();
                if (!_currentJournalId.HasValue || _loadedDraft)
                    return;
            }

            var user = ShouTech.App.Services.UsrSession.Current;
            var actor = !string.IsNullOrWhiteSpace(user?.Id) ? user.Id : ShouTech.App.Services.AppHost.Session.CurrentUser;
            if (string.IsNullOrWhiteSpace(actor))
                throw new InvalidOperationException("Could not determine the current user to post the entry.");
            if (ShouTech.App.Services.AppHost.Journal is not { } journal)
                throw new InvalidOperationException("The journal posting service is not initialized.");

            var budgetWarnings = await journal.PostAsync(_currentJournalId.Value, actor);
            _journalPosted = true;
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = false;
            _btnReverse.IsEnabled = true;
            SetVoucherReadOnly(true);
            if (budgetWarnings.Count > 0)
            {
                // Phase B 055: Warn-mode budget overruns post with the
                // entry and are shown to the poster (the durable record
                // is the budget_control_events row the guard wrote).
                var warningText = string.Join(Environment.NewLine, budgetWarnings.Select(w =>
                    Tf("Budget.WarningLine", w.BudgetName, w.Horizon, w.Overage)));
                MessageBox.Show(this,
                    T("Vouchers.EntryPosted") + Environment.NewLine + Environment.NewLine + warningText,
                    T("Budget.WarningTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(this, T("Vouchers.EntryPosted"), T("Vouchers.Posted"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (ex is not ShouTech.Domain.AppError)
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "JrnWin.PostVoucherAsync");
            ReportPersistenceFailure("Post", ex);
        }
    }

    private async Task ReverseVoucherAsync()
    {
        if (_journalReversed || !_currentJournalId.HasValue)
            return;
        if (!AskConfirm("Vouchers.ConfirmReverse"))
            return;

        try
        {
            if (!ShouTech.App.Services.AppHost.Permissions.Has(
                    ShouTech.App.Services.PermCat.Accounting.JournalReverse))
                throw new InvalidOperationException("The current user does not have permission to reverse journal entries (ACC.JOURNAL.REVERSE).");

            var user = ShouTech.App.Services.UsrSession.Current;
            var actor = !string.IsNullOrWhiteSpace(user?.Id) ? user.Id : ShouTech.App.Services.AppHost.Session.CurrentUser;
            if (string.IsNullOrWhiteSpace(actor))
                throw new InvalidOperationException("Could not determine the current user to reverse the entry.");
            if (ShouTech.App.Services.AppHost.Journal is not { } journal)
                throw new InvalidOperationException("The journal reversal service is not initialized.");

            // JournalRepository.ReverseAsync creates the compensating posted
            // entry (lines swapped, linked via reversal_of_entry_id), marks
            // this entry Reversed, and writes audit events in one transaction.
            await journal.ReverseAsync(_currentJournalId.Value, actor);
            _journalReversed = true;
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = false;
            _btnReverse.IsEnabled = false;
            SetVoucherReadOnly(true);
            ShowInfo("Vouchers.Reversed");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "JrnWin.ReverseVoucherAsync");
            ReportPersistenceFailure("Reverse", ex);
        }
    }

    private void ReportPersistenceFailure(string operation, Exception exception)
    {
        if (exception is ShouTech.Domain.AppError appError)
        {
            // Coded business blocks (ACC-5005 draft no longer editable,
            // ACC-5007/5008 cost-center rules at posting): the log line
            // carries the real catalog code and the dialog shows the
            // localized catalog message.
            AppHost.Errors.Report(appError.Code, appError, $"JrnWin.{operation}");
            var info = AppHost.Errors.Resolve(appError.Code, appError.Args.ToArray());
            var body = info.Message;
            if (!string.IsNullOrWhiteSpace(info.Hint))
                body += $"{Environment.NewLine}{Environment.NewLine}{info.Hint}";
            body += $"{Environment.NewLine}{Environment.NewLine}(Error {info.Code})";
            MessageBox.Show(this, body, T("Vouchers.SaveFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        const string errorCode = "APP-2010";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var context = $"Operation={operation}; Window={GetType().Name}; Company={ShouTech.App.Services.UsrSession.Current?.CompanyId ?? "(none)"}";
        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowPersistence",
            $"[{definition.Code}] {definition.Name} | {context} | {exception.Message}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowPersistence",
            "DatabaseOperationFailed",
            $"Code={definition.Code}; {context}; Error={exception.Message}");
        MessageBox.Show(
            this,
            FormatLocalized(T("Vouchers.SaveFailedMsg"), definition.Code, exception.Message),
            T("Vouchers.SaveFailedTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Vouchers.JournalTitle");
        // Headers bind by column identity — index-based assignment
        // would mislabel columns after a chooser reorder.
        _accountCodeColumn.Header = T("Vouchers.AccountCode");
        _accountNameColumn.Header = T("Vouchers.AccountName");
        _debitColumn.Header = T("Vouchers.Debit");
        _creditColumn.Header = T("Vouchers.Credit");
        _costCenterColumn.Header = T("Vouchers.CostCenter");
        _memoColumn.Header = T("Vouchers.Memo");
        RefreshLocalizedContent(Content as DependencyObject);
        RefreshBalances();
    }

    // ------------------------------------------------------------------
    // Account entry (owner grid spec item 5, mirroring the material
    // entry of item 3): committing the ACCOUNT cell resolves the
    // typed code or (partial) name immediately when unambiguous
    // (AccRepo.MatchAccount over postable accounts); otherwise the
    // chart of accounts opens as the search window, pre-filtered.
    // ------------------------------------------------------------------

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is JrnLineVm line && ReferenceEquals(e.Column, _accountCodeColumn))
                ResolveAccountEntry(line);
            RefreshBalances();
        }));
    }

    private void ResolveAccountEntry(JrnLineVm line)
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
            ApplyAccount(line, match.Account.Code, match.Account);
            return;
        }

        var pick = CoaWin.ShowPicker(this, text);
        if (pick is not null)
        {
            ApplyAccount(line, pick.Code, null, pick.DisplayName);
        }
        else if (string.IsNullOrWhiteSpace(line.AccountName))
        {
            _lblStatus.Text = FormatLocalized(T("Vouchers.AccountNotMatched"), text);
        }
    }

    private static void ApplyAccount(JrnLineVm line, string code, AccRepo.AccRec? account, string? displayName = null)
    {
        line.AccountCode = code;
        line.AccountName = displayName ?? (account is null ? line.AccountName : AccountDisplayName(account));
        line.LastAccountText = code;
    }

    private static string AccountDisplayName(AccRepo.AccRec account)
        => string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
            ? (!string.IsNullOrWhiteSpace(account.Name) ? account.Name : account.NameEn)
            : (!string.IsNullOrWhiteSpace(account.NameEn) ? account.NameEn : account.Name);

    // ------------------------------------------------------------------
    // Optional columns (spec item 4): CostCenter + Memo via the
    // shared ColChsWin; per-user persistence through WinPref.
    // ------------------------------------------------------------------

    private sealed record OptionalColumn(string Key, DataGridColumn Column, string HeaderKey);

    private List<OptionalColumn> OptionalColumns() => new()
    {
        new("CostCenter", _costCenterColumn, "Vouchers.CostCenter"),
        new("Memo", _memoColumn, "Vouchers.Memo"),
    };

    private static string ColumnScope()
    {
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        var company = AppState.CurrentCompanyCode ?? "no-company";
        return $"{company}/{user}/Vch:JrnWin";
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
            opt.Column.DisplayIndex = 4 + Math.Max(0, rec.Order);
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
                opt.Column.DisplayIndex = 4 + i;
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
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "JrnWin.OnColumnsClick");
        }
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

    /// <summary>
    /// Prints the current voucher on an A4 page, following the same visual
    /// print pattern as <see cref="ShouTech.App.Services.InvPrn"/> for invoices.
    /// Print rendering is paper output (white page), not window styling.
    /// </summary>
    private void PrintVoucher()
    {
        var page = BuildPrintPage();
        var paginator = new SinglePagePaginator(page);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, string.IsNullOrWhiteSpace(_txtDocNo.Text) ? T("Vouchers.JournalTitle") : _txtDocNo.Text.Trim());
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
            Text = T("Vouchers.JournalTitle"),
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

        Meta("Vouchers.VoucherNo", _txtDocNo.Text.Trim());
        Meta("Vouchers.Date", (_dtDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Meta("Vouchers.RefNo", _txtRefNo.Text.Trim());
        Meta("Vouchers.Currency", _txtCur.Text.Trim());
        Meta("Vouchers.Memo", _txtMemo.Text.Trim());

        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (width, _) in new[] { (36, 0), (110, 0), (200, 0), (90, 0), (90, 0), (100, 0), (140, 0) })
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
        Cell(0, 1, T("Vouchers.AccountCode"), true);
        Cell(0, 2, T("Vouchers.AccountName"), true);
        Cell(0, 3, T("Vouchers.Debit"), true);
        Cell(0, 4, T("Vouchers.Credit"), true);
        Cell(0, 5, T("Vouchers.CostCenter"), true);
        Cell(0, 6, T("Vouchers.Memo"), true);

        var rowIndex = 1;
        foreach (var line in _lines)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rowIndex, 0, line.LineNo.ToString(CultureInfo.InvariantCulture));
            Cell(rowIndex, 1, line.AccountCode);
            Cell(rowIndex, 2, line.AccountName);
            Cell(rowIndex, 3, line.Debit == 0 ? "" : line.Debit.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 4, line.Credit == 0 ? "" : line.Credit.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 5, line.CostCenter);
            Cell(rowIndex, 6, line.Memo);
            rowIndex++;
        }

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(rowIndex, 2, T("Vouchers.TotalDebit") + " / " + T("Vouchers.TotalCredit"), true);
        Cell(rowIndex, 3, _lines.Sum(l => l.Debit).ToString("N2", CultureInfo.InvariantCulture), true);
        Cell(rowIndex, 4, _lines.Sum(l => l.Credit).ToString("N2", CultureInfo.InvariantCulture), true);

        stack.Children.Add(table);
        return root;
    }

    /// <summary>Single-page paginator, mirroring the one inside InvPrn.</summary>
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

    private sealed class JrnLineVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _accCode = "", _accName = "", _costCenter = "", _memo = "";
        private decimal _debit, _credit;

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string AccountCode { get => _accCode; set { _accCode = value; OnChanged(); } }

        /// <summary>UI state only (never persisted): the last typed
        /// account text the entry matcher already processed.</summary>
        public string? LastAccountText { get; set; }
        public string AccountName { get => _accName; set { _accName = value; OnChanged(); } }
        public string CostCenter { get => _costCenter; set { _costCenter = value; OnChanged(); } }
        public string Memo { get => _memo; set { _memo = value; OnChanged(); } }

        public decimal Debit { get => _debit; set { _debit = value; OnChanged(nameof(DebitText)); } }
        public decimal Credit { get => _credit; set { _credit = value; OnChanged(nameof(CreditText)); } }

        public string DebitText
        {
            get => _debit == 0 ? "" : _debit.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Debit = v; else Debit = 0; }
        }

        public string CreditText
        {
            get => _credit == 0 ? "" : _credit.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Credit = v; else Credit = 0; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
