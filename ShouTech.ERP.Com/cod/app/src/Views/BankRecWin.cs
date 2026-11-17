using System;
using System.Collections.Generic;
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
/// Bank Reconciliation window (RS-9): pick a bank GL account (banks table
/// gl_account_code, falling back to reconcile-flagged chart accounts, with
/// free entry), list its posted journal lines with cleared state from
/// bank_reconciliation_marks (migration 035), mark lines cleared with a
/// statement date/reference, or unmark them. Saves are transactional through
/// OperationsService. The totals footer shows book balance, cleared total,
/// uncleared total and difference (book - cleared - uncleared).
/// </summary>
public sealed class BankRecWin : BaseWin
{
    protected override string? ScheduleTintName => "Slate";

    private readonly OperationsService? _service;
    private readonly List<BankJournalLineRow> _lines = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly ComboBox _account = new();
    private readonly DatePicker _statementDate = new();
    private readonly TextBox _statementRef = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _totals = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _markButton = new();
    private readonly Button _unmarkButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

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
        await LoadAccountsAsync();
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_markButton, "BankRec.MarkCleared");
        F2Ui.StyleButton(_unmarkButton, "BankRec.Unmark");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _markButton.Click += async (_, _) => await MarkAsync();
        _unmarkButton.Click += async (_, _) => await UnmarkAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_markButton, _unmarkButton, _refreshButton, _closeButton));

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
        DockPanel.SetDock(selector, Dock.Top);
        content.Children.Add(selector);

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
        content.Children.Add(_grid);

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

    protected override void ApplyLocalizedText()
    {
        Title = T("BankRec.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _markButton, _unmarkButton, _refreshButton, _closeButton }, T);
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
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            if (!string.IsNullOrWhiteSpace(code))
            {
                var rows = await _service.GetBankJournalLinesAsync(CurrentCompanyCode, code);
                _lines.AddRange(rows);
                _grid.ItemsSource = _lines;
            }
            UpdateTotals();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTotals()
    {
        var totals = OperationsService.ComputeTotals(_lines);
        _totals.Text = $"{T("BankRec.BookBalance")}: {F2Ui.Fmt(totals.BookBalance)}   ·   " +
                       $"{T("BankRec.ClearedTotal")}: {F2Ui.Fmt(totals.ClearedTotal)}   ·   " +
                       $"{T("BankRec.UnclearedTotal")}: {F2Ui.Fmt(totals.UnclearedTotal)}   ·   " +
                       $"{T("BankRec.Difference")}: {F2Ui.Fmt(totals.Difference)}";
        _statusBar.Text = $"{_lines.Count} · {AccountCode}";
    }

    private async Task MarkAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("BankRec.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Banking.ReconciliationExecute))
                throw new InvalidOperationException(T("BankRec.PermissionDenied"));
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
            await _service.SaveBankMarksAsync(CurrentCompanyCode, marks, CurrentUserId);
            MessageBox.Show(this, T("BankRec.Marked"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task UnmarkAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("BankRec.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Banking.ReconciliationExecute))
                throw new InvalidOperationException(T("BankRec.PermissionDenied"));
            var keys = _lines.Where(l => l.Selected && l.IsCleared).Select(l => l.LineKey).ToList();
            if (keys.Count == 0)
                throw new InvalidOperationException(T("BankRec.NothingToUnmark"));
            await _service.UnmarkBankLinesAsync(CurrentCompanyCode, keys, CurrentUserId);
            MessageBox.Show(this, T("BankRec.Unmarked"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
