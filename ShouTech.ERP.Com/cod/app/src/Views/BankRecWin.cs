using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Bank Reconciliation window (RS-9, rebuilt in Step 8): a REAL
/// two-sided reconciliation. The book side is the account's posted
/// journal lines; the bank side is a persisted bank statement
/// (migration 063: bank_statements + bank_statement_lines) whose
/// opening/closing balances and lines are entered here. Statement
/// lines are matched one-to-one to book lines (equal amounts); each
/// match upserts the cleared mark (035) with its statement link.
/// The footer Difference = statement closing − opening − matched book
/// total: a real quantity that is non-zero whenever the bank and the
/// books disagree, moves by every matched amount, and reaches zero
/// legitimately only at a full match — at which point the statement
/// can be closed (the service refuses, ACC-5016, while it is not).
/// Legacy bare Mark/Unmark (no statement line) remains available.
/// The account list comes from the canonical banks store (031).
/// </summary>
public sealed class BankRecWin : BaseWin
{
    protected override string? ScheduleTintName => "Slate";

    /// <summary>Grid row for one statement line.</summary>
    public sealed class StatementLineRow
    {
        public StatementLineRow(BankStatementLine line) => Line = line;
        public BankStatementLine Line { get; }
        public Guid Id => Line.Id;
        public string DateText => Line.LineDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public string Reference => Line.Reference ?? string.Empty;
        public string Description => Line.Description ?? string.Empty;
        public decimal Amount => Line.Amount;
        public string MatchedText => Line.IsMatched ? "✓" : string.Empty;
    }

    private readonly OperationsService? _service;
    private readonly List<BankJournalLineRow> _lines = new();
    private readonly List<StatementLineRow> _statementRows = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly List<(DataGridColumn Column, string Key)> _statementHeaders = new();

    private readonly ComboBox _account = new();
    private readonly DatePicker _statementDate = new();
    private readonly TextBox _statementRef = new();
    private readonly TextBox _openingBalance = new() { Text = "0" };
    private readonly TextBox _closingBalance = new() { Text = "0" };
    private readonly DataGrid _grid = new();
    private readonly DataGrid _statementGrid = new();
    private readonly DatePicker _lineDate = new();
    private readonly TextBox _lineRef = new();
    private readonly TextBox _lineDesc = new();
    private readonly TextBox _lineAmount = new();
    private readonly TextBlock _totals = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _markButton = new();
    private readonly Button _unmarkButton = new();
    private readonly Button _saveStatementButton = new();
    private readonly Button _addLineButton = new();
    private readonly Button _removeLineButton = new();
    private readonly Button _matchButton = new();
    private readonly Button _unmatchButton = new();
    private readonly Button _closeStatementButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private BankStatement? _statement;
    private List<BankStatementLine> _statementLines = new();

    public BankRecWin()
    {
        _service = AppHost.Operations;
        Width = 1180;
        Height = 700;
        MinWidth = 960;
        MinHeight = 560;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Banking.ReconciliationView))
        {
            MessageBox.Show(this, T("BankRec.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _statementDate.SelectedDate = DateTime.Today;
        _lineDate.SelectedDate = DateTime.Today;
        await LoadAccountsAsync();
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_markButton, "BankRec.MarkCleared");
        F2Ui.StyleButton(_unmarkButton, "BankRec.Unmark");
        F2Ui.StyleButton(_saveStatementButton, "BankRec.SaveStatement");
        F2Ui.StyleButton(_addLineButton, "BankRec.AddLine");
        F2Ui.StyleButton(_removeLineButton, "BankRec.RemoveLine");
        F2Ui.StyleButton(_matchButton, "BankRec.Match");
        F2Ui.StyleButton(_unmatchButton, "BankRec.Unmatch");
        F2Ui.StyleButton(_closeStatementButton, "BankRec.CloseStatement");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _markButton.Click += async (_, _) => await MarkAsync();
        _unmarkButton.Click += async (_, _) => await UnmarkAsync();
        _saveStatementButton.Click += async (_, _) => await SaveStatementAsync();
        _addLineButton.Click += async (_, _) => await AddLineAsync();
        _removeLineButton.Click += async (_, _) => await RemoveLineAsync();
        _matchButton.Click += async (_, _) => await MatchAsync();
        _unmatchButton.Click += async (_, _) => await UnmatchAsync();
        _closeStatementButton.Click += async (_, _) => await CloseStatementAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_markButton, _unmarkButton, _saveStatementButton, _addLineButton,
            _removeLineButton, _matchButton, _unmatchButton, _closeStatementButton, _refreshButton, _closeButton));

        var totalsBorder = new Border
        {
            Child = _totals,
            Padding = new Thickness(2, 6, 2, 2),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        totalsBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(totalsBorder, Dock.Bottom);
        root.Children.Add(totalsBorder);

        var content = new DockPanel();
        var selector = F2Ui.CreateFormGrid(3);
        selector.Margin = new Thickness(0, 0, 0, 8);
        _account.IsEditable = true;
        _account.SelectionChanged += async (_, _) => await RefreshAsync();
        F2Ui.AddField(_labels, selector, 0, 0, "BankRec.Account", _account);
        F2Ui.AddField(_labels, selector, 0, 1, "BankRec.StatementDate", _statementDate);
        F2Ui.AddField(_labels, selector, 0, 2, "BankRec.StatementRef", _statementRef);
        F2Ui.AddField(_labels, selector, 1, 0, "BankRec.OpeningBalance", _openingBalance);
        F2Ui.AddField(_labels, selector, 1, 1, "BankRec.ClosingBalance", _closingBalance);
        DockPanel.SetDock(selector, Dock.Top);
        content.Children.Add(selector);

        var split = new Grid();
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

        F2Ui.ConfigureGrid(_grid);
        _grid.IsReadOnly = false;
        var check = new DataGridCheckBoxColumn
        {
            Binding = new Binding(nameof(BankJournalLineRow.Selected)) { Mode = BindingMode.TwoWay },
            Width = new DataGridLength(44)
        };
        _headers.Add((check, "BankRec.Select"));
        _grid.Columns.Add(check);
        _grid.Columns.Add(ReadOnlyCol("BankRec.Date", nameof(BankJournalLineRow.EntryDate), 95, center: true));
        _grid.Columns.Add(ReadOnlyCol("BankRec.DocNo", nameof(BankJournalLineRow.DocNo), 130));
        _grid.Columns.Add(ReadOnlyCol("BankRec.Memo", nameof(BankJournalLineRow.Memo), 300));
        _grid.Columns.Add(ReadOnlyCol("BankRec.Debit", nameof(BankJournalLineRow.Debit), 110, numeric: true));
        _grid.Columns.Add(ReadOnlyCol("BankRec.Credit", nameof(BankJournalLineRow.Credit), 110, numeric: true));
        _grid.Columns.Add(ReadOnlyCol("BankRec.Cleared", nameof(BankJournalLineRow.IsCleared), 70, center: true));
        _grid.Columns.Add(ReadOnlyCol("BankRec.StatementDate", nameof(BankJournalLineRow.StatementDate), 110, center: true));
        _grid.Columns.Add(ReadOnlyCol("BankRec.StatementRef", nameof(BankJournalLineRow.StatementRef), 130));
        Grid.SetRow(_grid, 0);
        split.Children.Add(_grid);

        var lineEntry = F2Ui.CreateFormGrid(4);
        lineEntry.Margin = new Thickness(0, 8, 0, 4);
        F2Ui.AddField(_labels, lineEntry, 0, 0, "BankRec.Date", _lineDate);
        F2Ui.AddField(_labels, lineEntry, 0, 1, "BankRec.StatementRef", _lineRef);
        F2Ui.AddField(_labels, lineEntry, 0, 2, "BankRec.Description", _lineDesc);
        F2Ui.AddField(_labels, lineEntry, 0, 3, "BankRec.Amount", _lineAmount);
        Grid.SetRow(lineEntry, 1);
        split.Children.Add(lineEntry);

        F2Ui.ConfigureGrid(_statementGrid);
        _statementGrid.IsReadOnly = true;
        _statementGrid.Columns.Add(StatementCol("BankRec.Date", nameof(StatementLineRow.DateText), 95, center: true));
        _statementGrid.Columns.Add(StatementCol("BankRec.StatementRef", nameof(StatementLineRow.Reference), 130));
        _statementGrid.Columns.Add(StatementCol("BankRec.Description", nameof(StatementLineRow.Description), 300));
        _statementGrid.Columns.Add(StatementCol("BankRec.Amount", nameof(StatementLineRow.Amount), 110, numeric: true));
        _statementGrid.Columns.Add(StatementCol("BankRec.Matched", nameof(StatementLineRow.MatchedText), 70, center: true));
        Grid.SetRow(_statementGrid, 2);
        split.Children.Add(_statementGrid);

        content.Children.Add(split);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    private DataGridTextColumn ReadOnlyCol(string key, string path, double width, bool numeric = false, bool center = false)
    {
        var col = F2Ui.TextCol(_headers, key, path, width, numeric, center);
        col.IsReadOnly = true;
        return col;
    }

    private DataGridTextColumn StatementCol(string key, string path, double width, bool numeric = false, bool center = false)
        => F2Ui.TextCol(_statementHeaders, key, path, width, numeric, center);

    protected override void ApplyLocalizedText()
    {
        Title = T("BankRec.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _markButton, _unmarkButton, _saveStatementButton, _addLineButton, _removeLineButton, _matchButton, _unmatchButton, _closeStatementButton, _refreshButton, _closeButton }, T);
        foreach (var (column, key) in _statementHeaders)
            column.Header = T(key);
        UpdateTotals();
    }

    private string AccountCode
    {
        get
        {
            if (_account.SelectedItem is BankAccountOption option)
                return option.AccountCode;
            return (_account.Text ?? string.Empty).Trim();
        }
    }

    private void RequireExecute()
    {
        if (_service is null)
            throw new InvalidOperationException(T("BankRec.ServiceUnavailable"));
        if (!AppHost.Permissions.Has(PermCat.Banking.ReconciliationExecute))
            throw new InvalidOperationException(T("BankRec.PermissionDenied"));
    }

    private async Task LoadAccountsAsync()
    {
        try
        {
            if (_service is null)
                return;
            var accounts = await _service.GetBankAccountsAsync(CurrentCompanyCode);
            _account.ItemsSource = accounts;
            _account.DisplayMemberPath = nameof(BankAccountOption.DisplayName);
            if (accounts.Count > 0)
                _account.SelectedIndex = 0;
        }
        catch (Exception ex) { ReportFailure("LoadAccounts", ex); }
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("BankRec.ServiceUnavailable");
                return;
            }
            var code = AccountCode;
            _lines.Clear();
            _grid.ItemsSource = null;
            _statement = null;
            _statementLines = new List<BankStatementLine>();
            _statementRows.Clear();
            _statementGrid.ItemsSource = null;
            if (!string.IsNullOrWhiteSpace(code))
            {
                var rows = await _service.GetBankJournalLinesAsync(CurrentCompanyCode, code);
                _lines.AddRange(rows);
                _grid.ItemsSource = _lines;

                var statements = await _service.GetBankStatementsAsync(CurrentCompanyCode, code);
                _statement = null;
                foreach (var candidate in statements)
                {
                    if (candidate.Status == BankStatementStatuses.Open) { _statement = candidate; break; }
                    _statement ??= candidate;
                }
                if (_statement is not null)
                {
                    _statementDate.SelectedDate = _statement.StatementDate.ToDateTime(TimeOnly.MinValue);
                    _statementRef.Text = _statement.StatementRef ?? string.Empty;
                    _openingBalance.Text = _statement.OpeningBalance.ToString(CultureInfo.InvariantCulture);
                    _closingBalance.Text = _statement.ClosingBalance.ToString(CultureInfo.InvariantCulture);
                    _statementLines = (await _service.GetBankStatementLinesAsync(CurrentCompanyCode, _statement.Id)).ToList();
                    _statementRows.AddRange(_statementLines.Select(l => new StatementLineRow(l)));
                    _statementGrid.ItemsSource = _statementRows;
                }
            }
            UpdateTotals();
        }
        catch (Exception ex) { ReportFailure("Refresh", ex); }
    }

    private void UpdateTotals()
    {
        var totals = OperationsService.ComputeTotals(_lines, _statement, _statementLines);
        _totals.Text = $"{T("BankRec.BookBalance")}: {F2Ui.Fmt(totals.BookBalance)}   ·   " +
                       $"{T("BankRec.ClearedTotal")}: {F2Ui.Fmt(totals.ClearedTotal)}   ·   " +
                       $"{T("BankRec.UnclearedTotal")}: {F2Ui.Fmt(totals.UnclearedTotal)}   ·   " +
                       $"{T("BankRec.ClosingBalance")}: {F2Ui.Fmt(totals.StatementBalance)}   ·   " +
                       $"{T("BankRec.Difference")}: {F2Ui.Fmt(totals.Difference)}";
        var unmatchedBook = _lines.Count(l => !l.IsCleared);
        var unmatchedStatement = _statementLines.Count(l => !l.IsMatched);
        _statusBar.Text = $"{_lines.Count} · {AccountCode} · {T("BankRec.UnclearedTotal")}: {unmatchedBook} / {unmatchedStatement}";
    }

    private async Task SaveStatementAsync()
    {
        try
        {
            RequireExecute();
            if (_statementDate.SelectedDate is not DateTime date)
                throw new InvalidOperationException(T("BankRec.StatementDateRequired"));
            if (!decimal.TryParse(_openingBalance.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var opening))
                throw new InvalidOperationException(T("BankRec.ClosingBalance"));
            if (!decimal.TryParse(_closingBalance.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var closing))
                throw new InvalidOperationException(T("BankRec.ClosingBalance"));
            var statement = _statement ?? new BankStatement();
            statement.AccountCode = AccountCode;
            statement.StatementDate = DateOnly.FromDateTime(date);
            statement.StatementRef = F2Ui.NullIfBlank(_statementRef.Text);
            statement.PeriodFrom = statement.StatementDate;
            statement.PeriodTo = statement.StatementDate;
            statement.OpeningBalance = opening;
            statement.ClosingBalance = closing;
            await _service!.SaveBankStatementAsync(CurrentCompanyCode, statement, CurrentUserId);
            MessageBox.Show(this, T("BankRec.StatementSaved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("SaveStatement", ex); }
    }

    private async Task AddLineAsync()
    {
        try
        {
            RequireExecute();
            if (_statement is null)
                throw new InvalidOperationException(T("BankRec.StatementRequired"));
            if (_lineDate.SelectedDate is not DateTime date)
                throw new InvalidOperationException(T("BankRec.StatementDateRequired"));
            if (!decimal.TryParse(_lineAmount.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) || amount == 0m)
                throw new InvalidOperationException(T("BankRec.Amount"));
            await _service!.AddBankStatementLineAsync(CurrentCompanyCode, new BankStatementLine
            {
                StatementId = _statement.Id,
                AccountCode = AccountCode,
                LineDate = DateOnly.FromDateTime(date),
                Reference = F2Ui.NullIfBlank(_lineRef.Text),
                Description = F2Ui.NullIfBlank(_lineDesc.Text),
                Amount = amount
            }, CurrentUserId);
            _lineRef.Text = string.Empty;
            _lineDesc.Text = string.Empty;
            _lineAmount.Text = string.Empty;
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("AddStatementLine", ex); }
    }

    private async Task RemoveLineAsync()
    {
        try
        {
            RequireExecute();
            if (_statementGrid.SelectedItem is not StatementLineRow row)
                throw new InvalidOperationException(T("BankRec.SelectStatementLine"));
            await _service!.RemoveBankStatementLineAsync(CurrentCompanyCode, row.Id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("RemoveStatementLine", ex); }
    }

    private async Task MatchAsync()
    {
        try
        {
            RequireExecute();
            if (_statement is null)
                throw new InvalidOperationException(T("BankRec.StatementRequired"));
            if (_statementGrid.SelectedItem is not StatementLineRow statementRow)
                throw new InvalidOperationException(T("BankRec.SelectStatementLine"));
            if (_grid.SelectedItem is not BankJournalLineRow bookLine)
                throw new InvalidOperationException(T("BankRec.SelectBookLine"));
            // Reload fresh copies so stale grid state cannot match twice.
            var freshStatementLines = await _service!.GetBankStatementLinesAsync(CurrentCompanyCode, _statement.Id);
            var statementLine = freshStatementLines.FirstOrDefault(l => l.Id == statementRow.Id)
                ?? throw new InvalidOperationException(T("BankRec.SelectStatementLine"));
            var freshBookLines = await _service.GetBankJournalLinesAsync(CurrentCompanyCode, AccountCode);
            var freshBookLine = freshBookLines.FirstOrDefault(l => l.LineKey == bookLine.LineKey)
                ?? throw new InvalidOperationException(T("BankRec.SelectBookLine"));
            await _service.MatchBankLineAsync(CurrentCompanyCode, statementLine, freshBookLine, _statement.Id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Match", ex); }
    }

    private async Task UnmatchAsync()
    {
        try
        {
            RequireExecute();
            if (_statementGrid.SelectedItem is not StatementLineRow row)
                throw new InvalidOperationException(T("BankRec.SelectStatementLine"));
            await _service!.UnmatchBankLineAsync(CurrentCompanyCode, row.Id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Unmatch", ex); }
    }

    private async Task CloseStatementAsync()
    {
        try
        {
            RequireExecute();
            if (_statement is null)
                throw new InvalidOperationException(T("BankRec.StatementRequired"));
            await _service!.CloseBankStatementAsync(CurrentCompanyCode, _statement.Id, CurrentUserId);
            MessageBox.Show(this, T("BankRec.StatementSaved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("CloseStatement", ex); }
    }

    private async Task MarkAsync()
    {
        try
        {
            RequireExecute();
            if (_statementDate.SelectedDate is not DateTime date)
                throw new InvalidOperationException(T("BankRec.StatementDateRequired"));
            var marks = _lines
                .Where(l => l.Selected && !l.IsCleared)
                .Select(l => new BankReconciliationMark
                {
                    JournalEntryId = l.EntryId,
                    JournalLineKey = l.LineKey,
                    AccountCode = AccountCode,
                    StatementDate = DateOnly.FromDateTime(date),
                    StatementRef = F2Ui.NullIfBlank(_statementRef.Text),
                    ClearedAmount = l.Amount
                })
                .ToList();
            if (marks.Count == 0)
                throw new InvalidOperationException(T("BankRec.NothingToMark"));
            await _service!.SaveBankMarksAsync(CurrentCompanyCode, marks, CurrentUserId);
            MessageBox.Show(this, T("BankRec.Marked"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Mark", ex); }
    }

    private async Task UnmarkAsync()
    {
        try
        {
            RequireExecute();
            var keys = _lines.Where(l => l.Selected && l.IsCleared).Select(l => l.LineKey).ToList();
            if (keys.Count == 0)
                throw new InvalidOperationException(T("BankRec.NothingToUnmark"));
            await _service!.UnmarkBankLinesAsync(CurrentCompanyCode, keys, CurrentUserId);
            MessageBox.Show(this, T("BankRec.Unmarked"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure("Unmark", ex); }
    }

    /// <summary>Central error reporting (BNK-07): failures are logged
    /// with the catalog code (APP-2010 pattern) instead of a raw
    /// unlogged MessageBox.</summary>
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
}
