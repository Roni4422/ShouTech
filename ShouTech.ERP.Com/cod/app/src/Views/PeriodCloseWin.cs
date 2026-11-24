using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Month period-close window (docs/PERIOD_CLOSE_STUDY.md). One engine —
/// the window calls OperationsService, which validates company ownership
/// and delegates to AccountingStructureRepository.ClosePeriodAsync /
/// ReopenPeriodAsync; no parallel close mechanism lives here. The window
/// lists the selected period's unposted items BEFORE closing (the same
/// rows the engine counts for its ACC-5022 refusal), gates Close on
/// ACC.PERIOD.CLOSE and Reopen on ACC.PERIOD.REOPEN (buttons disabled
/// without the permission, and PermServ.Require re-checks — with its
/// audited denial — before either action runs), and mirrors the fiscal
/// year's bounds + period-end activation state from the same
/// fiscal_years store the Options window's Fiscal section binds to.
/// Closing applies to the CURRENT company only: every service call is
/// keyed by CurrentCompanyCode and the repository proves the period
/// belongs to it.
/// </summary>
public sealed class PeriodCloseWin : BaseWin
{
    protected override string? ScheduleTintName => "Tuff";
    private readonly OperationsService? _service;
    private readonly List<FiscalYearRow> _years = new();
    private readonly List<FiscalPeriodRow> _periods = new();
    private readonly List<PeriodUnpostedItemRow> _unposted = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly ComboBox _yearCombo = new();
    private readonly TextBlock _yearInfo = new();
    private readonly DataGrid _periodsGrid = new();
    private readonly DataGrid _unpostedGrid = new();
    private readonly TextBox _reasonBox = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _closePeriodButton = new();
    private readonly Button _reopenButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();
    private bool _binding;

    public PeriodCloseWin()
    {
        _service = AppHost.Operations;
        Width = 980;
        Height = 620;
        MinWidth = 660;
        MinHeight = 380;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _closePeriodButton.IsEnabled = AppHost.Permissions.Has(PermCat.System.PeriodClose);
        _reopenButton.IsEnabled = AppHost.Permissions.Has(PermCat.System.PeriodReopen);
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_closePeriodButton, "PeriodClose.ClosePeriod");
        F2Ui.StyleButton(_reopenButton, "PeriodClose.ReopenPeriod");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _closePeriodButton.Click += async (_, _) => await ClosePeriodAsync();
        _reopenButton.Click += async (_, _) => await ReopenPeriodAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_closePeriodButton, _reopenButton, _refreshButton, _closeButton));

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var yearRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var yearLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _labels.Add((yearLabel, "PeriodClose.FiscalYear"));
        yearRow.Children.Add(yearLabel);
        _yearCombo.MinWidth = 300;
        _yearCombo.SelectionChanged += async (_, _) => await OnYearChangedAsync();
        yearRow.Children.Add(_yearCombo);
        Grid.SetRow(yearRow, 0);
        content.Children.Add(yearRow);

        _yearInfo.Margin = new Thickness(0, 0, 0, 8);
        _yearInfo.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_yearInfo, 1);
        content.Children.Add(_yearInfo);

        F2Ui.ConfigureGrid(_periodsGrid);
        _periodsGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColNumber", nameof(FiscalPeriodRow.PeriodNumber), 60, center: true));
        _periodsGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColName", nameof(FiscalPeriodRow.PeriodName), 110, center: true));
        _periodsGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColStart", nameof(FiscalPeriodRow.StartDate), 110, center: true));
        _periodsGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColEnd", nameof(FiscalPeriodRow.EndDate), 110, center: true));
        _periodsGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColStatus", nameof(FiscalPeriodRow.Status), 90, center: true));
        _periodsGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColClosedBy", nameof(FiscalPeriodRow.ClosedBy), 130));
        _periodsGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColClosedAt", nameof(FiscalPeriodRow.ClosedAt), 170, center: true));
        _periodsGrid.SelectionChanged += async (_, _) => await LoadUnpostedAsync();
        Grid.SetRow(_periodsGrid, 2);
        content.Children.Add(_periodsGrid);

        var unpostedLabel = new TextBlock { Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold };
        _labels.Add((unpostedLabel, "PeriodClose.UnpostedTitle"));
        Grid.SetRow(unpostedLabel, 3);
        content.Children.Add(unpostedLabel);

        F2Ui.ConfigureGrid(_unpostedGrid);
        _unpostedGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColKind", nameof(PeriodUnpostedItemRow.Kind), 90, center: true));
        _unpostedGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColDocKind", nameof(PeriodUnpostedItemRow.DocKind), 90, center: true));
        _unpostedGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColDocNo", nameof(PeriodUnpostedItemRow.DocNo), 120));
        _unpostedGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColDocDate", nameof(PeriodUnpostedItemRow.DocDate), 110, center: true));
        _unpostedGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColDescription", nameof(PeriodUnpostedItemRow.Description), 260));
        _unpostedGrid.Columns.Add(F2Ui.TextCol(_headers, "PeriodClose.ColAmount", nameof(PeriodUnpostedItemRow.Amount), 110));
        Grid.SetRow(_unpostedGrid, 4);
        content.Children.Add(_unpostedGrid);

        var reasonRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var reasonLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _labels.Add((reasonLabel, "PeriodClose.ReopenReason"));
        reasonRow.Children.Add(reasonLabel);
        _reasonBox.MinWidth = 420;
        reasonRow.Children.Add(_reasonBox);
        Grid.SetRow(reasonRow, 5);
        content.Children.Add(reasonRow);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("PeriodClose.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _closePeriodButton, _reopenButton, _refreshButton, _closeButton }, T);
        UpdateYearInfo();
    }

    private FiscalYearRow? SelectedYear =>
        _yearCombo.SelectedIndex >= 0 && _yearCombo.SelectedIndex < _years.Count
            ? _years[_yearCombo.SelectedIndex]
            : null;

    private FiscalPeriodRow? SelectedPeriod => _periodsGrid.SelectedItem as FiscalPeriodRow;

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("PeriodClose.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetFiscalYearsAsync(CurrentCompanyCode);
            _years.Clear();
            _years.AddRange(rows);
            _binding = true;
            _yearCombo.Items.Clear();
            foreach (var year in _years)
                _yearCombo.Items.Add($"{year.FiscalYear} ({year.StartDate} - {year.EndDate})");
            if (_years.Count > 0)
                _yearCombo.SelectedIndex = _years.Count - 1;
            _binding = false;
            if (_years.Count > 0)
            {
                await OnYearChangedAsync();
            }
            else
            {
                _periods.Clear();
                BindPeriods();
            }
            _statusBar.Text = $"{_years.Count}";
        }
        catch (AppError ex) { ShowCodedError(ex, "PeriodCloseWin.Refresh"); }
        catch (Exception ex) { ReportAndShow(ex, "PeriodCloseWin.Refresh"); }
    }

    private async Task OnYearChangedAsync()
    {
        if (_binding)
            return;
        UpdateYearInfo();
        try
        {
            if (_service is null || SelectedYear is not { } year)
            {
                _periods.Clear();
                BindPeriods();
                return;
            }
            var rows = await _service.GetFiscalPeriodsAsync(CurrentCompanyCode, year.Id);
            _periods.Clear();
            _periods.AddRange(rows);
            BindPeriods();
            if (_periods.Count > 0)
            {
                _periodsGrid.SelectedIndex = 0;
                await LoadUnpostedAsync();
            }
        }
        catch (AppError ex) { ShowCodedError(ex, "PeriodCloseWin.YearChanged"); }
        catch (Exception ex) { ReportAndShow(ex, "PeriodCloseWin.YearChanged"); }
    }

    private void BindPeriods()
    {
        _periodsGrid.ItemsSource = null;
        _periodsGrid.ItemsSource = _periods;
        _unposted.Clear();
        _unpostedGrid.ItemsSource = null;
        _unpostedGrid.ItemsSource = _unposted;
    }

    private void UpdateYearInfo()
    {
        if (SelectedYear is not { } year)
        {
            _yearInfo.Text = string.Empty;
            return;
        }
        var text = Tf("PeriodClose.YearInfo", year.FiscalYear, year.StartDate, year.EndDate,
            year.PeriodEndActivation ? T("PeriodClose.ActivationOn") : T("PeriodClose.ActivationOff"));
        if (year.PeriodEndActivation && year.EndDate.Length >= 10 &&
            DateOnly.TryParse(year.EndDate[..10], CultureInfo.InvariantCulture, out var end) &&
            DateOnly.FromDateTime(DateTime.Today) > end)
            text += " " + T("PeriodClose.PeriodEnded");
        _yearInfo.Text = text;
    }

    private async Task LoadUnpostedAsync()
    {
        try
        {
            if (_service is null || SelectedPeriod is not { } period)
                return;
            var rows = await _service.GetPeriodUnpostedItemsAsync(CurrentCompanyCode, period.Id);
            _unposted.Clear();
            _unposted.AddRange(rows);
            _unpostedGrid.ItemsSource = null;
            _unpostedGrid.ItemsSource = _unposted;
            _statusBar.Text = Tf("PeriodClose.StatusLine", _periods.Count, _unposted.Count);
        }
        catch (AppError ex) { ShowCodedError(ex, "PeriodCloseWin.LoadUnposted"); }
        catch (Exception ex) { ReportAndShow(ex, "PeriodCloseWin.LoadUnposted"); }
    }

    private async Task ClosePeriodAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("PeriodClose.ServiceUnavailable"));
            if (SelectedPeriod is not { } period)
            {
                ShowWarn("PeriodClose.SelectPeriod");
                return;
            }
            if (period.Status != "OPEN")
            {
                ShowWarn("PeriodClose.NotOpen");
                return;
            }
            // docSide keeps PermServ.Require's default: sau_document_events
            // is a SALES/PURCHASE-sided document table (migration 103);
            // the PERIOD docKind + period name carry the context.
            if (!AppHost.Permissions.Require(PermCat.System.PeriodClose, docKind: "PERIOD", docNo: period.PeriodName))
            {
                ShowWarn("PeriodClose.PermissionDenied");
                return;
            }
            if (MessageBox.Show(this, Tf("PeriodClose.ConfirmClose", period.PeriodName), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.ClosePeriodAsync(CurrentCompanyCode, period.Id, CurrentUserId);
            ShowInfo("PeriodClose.ClosedOk");
            await RefreshAsync();
        }
        catch (AppError ex) { ShowCodedError(ex, "PeriodCloseWin.ClosePeriod"); }
        catch (Exception ex) { ReportAndShow(ex, "PeriodCloseWin.ClosePeriod"); }
    }

    private async Task ReopenPeriodAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("PeriodClose.ServiceUnavailable"));
            if (SelectedPeriod is not { } period)
            {
                ShowWarn("PeriodClose.SelectPeriod");
                return;
            }
            if (period.Status != "CLOSED")
            {
                ShowWarn("PeriodClose.NotClosed");
                return;
            }
            if (!AppHost.Permissions.Require(PermCat.System.PeriodReopen, docKind: "PERIOD", docNo: period.PeriodName))
            {
                ShowWarn("PeriodClose.PermissionDenied");
                return;
            }
            if (MessageBox.Show(this, Tf("PeriodClose.ConfirmReopen", period.PeriodName), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            // The reason is gated by the engine (ACC-5025) and recorded in
            // the period event ledger + security audit log.
            await _service.ReopenPeriodAsync(CurrentCompanyCode, period.Id, CurrentUserId, _reasonBox.Text);
            _reasonBox.Clear();
            ShowInfo("PeriodClose.ReopenedOk");
            await RefreshAsync();
        }
        catch (AppError ex) { ShowCodedError(ex, "PeriodCloseWin.ReopenPeriod"); }
        catch (Exception ex) { ReportAndShow(ex, "PeriodCloseWin.ReopenPeriod"); }
    }

    private void ShowCodedError(AppError ex, string context)
    {
        AppHost.Errors.Report(ex.Code, ex, context);
        var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
        var body = info.Message;
        if (!string.IsNullOrWhiteSpace(info.Hint))
            body += $"{Environment.NewLine}{Environment.NewLine}{info.Hint}";
        body += $"{Environment.NewLine}{Environment.NewLine}(Error {info.Code})";
        MessageBox.Show(this, body, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ReportAndShow(Exception ex, string context)
    {
        AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, context);
        MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
