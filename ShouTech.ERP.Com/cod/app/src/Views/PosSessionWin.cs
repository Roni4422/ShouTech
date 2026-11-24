using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// POS session window (Build POS 066): opens a session with the
/// counted opening float (POS.SESSION.OPEN), shows the open session
/// with its live Z figures, and closes it with the counted cash —
/// expected and variance are computed from the session's real tenders
/// by PosRepository, never typed. Past sessions are listed below.
/// The legacy CloseCashWin day-close remains for history; sessions
/// supersede it (its expected formula counted the wrong documents).
/// </summary>
public sealed class PosSessionWin : BaseWin
{
    protected override string? ScheduleTintName => "Peach";
    private readonly List<PosSession> _sessions = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    // Terminal picker (POS family P2): an editable combo fed by
    // the DEFINED terminals (PosTermWin / pos_terminals) — a
    // session opens on a defined terminal, not on free text.
    private readonly ComboBox _terminal = new() { IsEditable = true };
    private readonly TextBox _opening = new() { Text = "0" };
    private readonly TextBox _counted = new();
    private readonly TextBox _expenseAmount = new() { Text = "0" };
    private readonly TextBox _expenseReason = new();
    private readonly TextBox _expenseRef = new();
    private readonly TextBlock _openInfo = new();
    private readonly TextBlock _zInfo = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _openButton = new();
    private readonly Button _closeSessionButton = new();
    private readonly Button _paidOutButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private PosSession? _open;

    public PosSessionWin()
    {
        Width = 980;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_openButton, "POS.SessionOpen");
        F2Ui.StyleButton(_closeSessionButton, "POS.SessionClose");
        F2Ui.StyleButton(_paidOutButton, "POS.PaidOut");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _openButton.Click += async (_, _) => await OpenAsync();
        _closeSessionButton.Click += async (_, _) => await CloseSessionAsync();
        _paidOutButton.Click += async (_, _) => await PaidOutAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_openButton, _closeSessionButton, _paidOutButton, _refreshButton, _closeButton));

        var content = new DockPanel();
        var form = F2Ui.CreateFormGrid(3);
        form.Margin = new Thickness(0, 0, 0, 4);
        F2Ui.AddField(_labels, form, 0, 0, "POS.Terminal", _terminal);
        F2Ui.AddField(_labels, form, 0, 1, "POS.OpeningCash", _opening);
        F2Ui.AddField(_labels, form, 0, 2, "POS.CountedCash", _counted);
        F2Ui.AddField(_labels, form, 1, 0, "POS.ExpenseAmount", _expenseAmount);
        F2Ui.AddField(_labels, form, 1, 1, "POS.ExpenseReason", _expenseReason);
        F2Ui.AddField(_labels, form, 1, 2, "POS.ExpenseRef", _expenseRef);
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);

        var info = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        _openInfo.FontWeight = FontWeights.SemiBold;
        info.Children.Add(_openInfo);
        info.Children.Add(_zInfo);
        DockPanel.SetDock(info, Dock.Top);
        content.Children.Add(info);

        F2Ui.ConfigureGrid(_grid);
        _grid.IsReadOnly = true;
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.SessionNo", nameof(PosSession.SessionNo), 120));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Terminal", nameof(PosSession.TerminalCode), 100));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Cashier", nameof(PosSession.Cashier), 130));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Status", nameof(PosSession.Status), 80, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.OpeningCash", nameof(PosSession.OpeningCash), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.ExpectedCash", nameof(PosSession.ExpectedCash), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.CountedCash", nameof(PosSession.CountedCash), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Variance", nameof(PosSession.Variance), 100, numeric: true));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("POS.SessionTitle");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _openButton, _closeSessionButton, _paidOutButton, _refreshButton, _closeButton }, T);
        UpdateInfo();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var service = AppHost.Pos;
            if (service is null) { _statusBar.Text = T("POS.ServiceUnavailable"); return; }
            var settings = await service.GetSettingsAsync(CurrentCompanyCode);
            var defined = PosTermRepo.LoadAll().FindAll(t => t.IsActive);
            _terminal.ItemsSource = defined.ConvertAll(t => t.Code);
            if (string.IsNullOrWhiteSpace(_terminal.Text))
                _terminal.Text = settings.DefaultTerminalCode
                    ?? (defined.Count > 0 ? defined[0].Code : "T1");
            _open = await service.GetOpenSessionAsync(CurrentCompanyCode, _terminal.Text.Trim());
            var rows = await service.GetSessionsAsync(CurrentCompanyCode);
            _sessions.Clear();
            _sessions.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _sessions;
            if (_open is not null)
            {
                var z = await service.GetZReportAsync(CurrentCompanyCode, _open.Id);
                _zInfo.Text = $"{T("POS.ZSummary")}: {z.SalesCount} · {F2Ui.Fmt(z.SalesTotal)} | {T("POS.ZPaidOuts")}: {F2Ui.Fmt(z.PaidOutsTotal)} | {T("POS.ExpectedCash")}: {F2Ui.Fmt(z.ExpectedCash)}";
            }
            else _zInfo.Text = string.Empty;
            UpdateInfo();
            _statusBar.Text = $"{_sessions.Count}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSessionWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateInfo()
    {
        _openInfo.Text = _open is null
            ? T("POS.NoOpenSession")
            : $"{T("POS.OpenSession")}: {_open.SessionNo} · {_open.TerminalCode} · {_open.Cashier} · {T("POS.OpeningCash")}: {F2Ui.Fmt(_open.OpeningCash)}";
        _openButton.IsEnabled = _open is null;
        _closeSessionButton.IsEnabled = _open is not null;
        _paidOutButton.IsEnabled = _open is not null;
    }

    /// <summary>Records a drawer paid-out against the open session
    /// (W9): POS.EXPENSE.MANAGE, a positive amount and a reason are
    /// required; the paid-out immediately reduces the session's
    /// expected cash (see the refreshed Z line).</summary>
    private async Task PaidOutAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Pos.ExpenseManage)) { ShowWarn("POS.PermissionDenied"); return; }
            var service = AppHost.Pos ?? throw new InvalidOperationException(T("POS.ServiceUnavailable"));
            if (_open is null) { ShowWarn("POS.NoOpenSession"); return; }
            var amount = F2Ui.ParseDecimal(_expenseAmount.Text);
            if (amount <= 0) { ShowWarn("POS.ExpenseNeedsAmount"); return; }
            if (string.IsNullOrWhiteSpace(_expenseReason.Text)) { ShowWarn("POS.ExpenseNeedsReason"); return; }
            if (!AskConfirm("POS.ConfirmPaidOut")) return;
            var expense = await service.RecordSessionExpenseAsync(CurrentCompanyCode, _open.Id, amount,
                _expenseReason.Text.Trim(), _expenseRef.Text.Trim(), CurrentUserId);
            _expenseAmount.Text = "0";
            _expenseReason.Clear();
            _expenseRef.Clear();
            _statusBar.Text = $"{T("POS.PaidOut")}: {expense.ExpenseNo} · {F2Ui.Fmt(expense.Amount)}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSessionWin.PaidOutAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OpenAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Pos.SessionOpen)) { ShowWarn("POS.PermissionDenied"); return; }
            var service = AppHost.Pos ?? throw new InvalidOperationException(T("POS.ServiceUnavailable"));

            // Defined-terminal enforcement (POS family P2): when
            // the company defines terminals, a session may open
            // only on a defined, active one — and only by a user
            // on its permitted list when a list is set. With no
            // definitions at all, the legacy free-code path stays
            // (the settings default), so existing installs do not
            // break before they define their first terminal.
            var defined = PosTermRepo.LoadAll();
            if (defined.Count > 0)
            {
                var terminal = defined.Find(t =>
                    string.Equals(t.Code, _terminal.Text.Trim(), StringComparison.OrdinalIgnoreCase) && t.IsActive);
                if (terminal is null) { ShowWarn("POS.TerminalNotDefined"); return; }
                var username = AppState.CurrentUser?.Username ?? CurrentUserDisplay;
                if (!PosTermRepo.IsUserPermitted(terminal, username)) { ShowWarn("POS.TerminalUserNotPermitted"); return; }
            }

            var settings = await service.GetSettingsAsync(CurrentCompanyCode);
            var warehouseId = await service.GetTerminalWarehouseIdAsync(CurrentCompanyCode, _terminal.Text.Trim())
                ?? settings.DefaultWarehouseId;
            if (warehouseId is null)
            {
                ShowWarn("POS.WarehouseRequired");
                return;
            }
            _open = await service.OpenSessionAsync(CurrentCompanyCode, _terminal.Text.Trim(), CurrentUserDisplay,
                warehouseId, UsrPref.Load().CurrencyCode, F2Ui.ParseDecimal(_opening.Text), CurrentUserId);
            UpdateInfo();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSessionWin.OpenAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CloseSessionAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Pos.SessionClose)) { ShowWarn("POS.PermissionDenied"); return; }
            var service = AppHost.Pos ?? throw new InvalidOperationException(T("POS.ServiceUnavailable"));
            if (_open is null) return;
            if (!AskConfirm("POS.ConfirmCloseSession")) return;
            var z = await service.CloseSessionAsync(CurrentCompanyCode, _open.Id, F2Ui.ParseDecimal(_counted.Text), CurrentUserId);
            _open = null;
            _zInfo.Text = $"{T("POS.ZSummary")}: {z.SalesCount} · {F2Ui.Fmt(z.SalesTotal)} | {T("POS.Variance")}: {F2Ui.Fmt(z.Variance ?? 0m)}";
            UpdateInfo();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSessionWin.CloseSessionAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
