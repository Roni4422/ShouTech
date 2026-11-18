using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Payroll runs (Build HR 065, design sections 3.6 + 4 + 5): pick a
/// period → Generate draft payslips for every eligible Active employee
/// → review/edit exceptional draft lines (system lines are locked —
/// fix attendance/leave and regenerate) → Post (HR.PAYROLL.POST,
/// permission-separated): payslips finalize and the run posts to GL
/// through GlPosting per the component account mapping → Reverse
/// re-opens symmetrically. Pay salaries opens the EXISTING CshWin
/// payment voucher against the 2204 payable — no second payer.
/// </summary>
public sealed class HrPayrollWin : BaseWin
{
    protected override string? ScheduleTintName => "Cobalt";

    private readonly HrService? _service;
    private readonly List<HrPayrollRun> _runs = new();
    private readonly List<HrPayslipLine> _editLines = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TextBlock _runsHeader = new();
    private readonly TextBlock _slipsHeader = new();
    private readonly TextBlock _linesHeader = new();
    private readonly DataGrid _runsGrid = new();
    private readonly DataGrid _slipsGrid = new();
    private readonly DataGrid _linesGrid = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBlock _hint = new();
    private readonly TextBox _year = new();
    private readonly TextBox _month = new();
    private readonly TextBox _lineCode = new();
    private readonly TextBox _lineName = new();
    private readonly ComboBox _lineKind = new();
    private readonly TextBox _lineAmount = new();
    private readonly TextBox _lineAccount = new();

    private readonly Button _generateButton = new();
    private readonly Button _postButton = new();
    private readonly Button _reverseButton = new();
    private readonly Button _payButton = new();
    private readonly Button _lineAddButton = new();
    private readonly Button _lineRemoveButton = new();
    private readonly Button _lineSaveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private HrPayrollRun? _selectedRun;
    private HrPayslip? _selectedSlip;

    public HrPayrollWin()
    {
        _service = AppHost.Hr;
        Width = 1180; Height = 700; MinWidth = 660; MinHeight = 360;
        _year.Text = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        _month.Text = DateTime.Today.Month.ToString(CultureInfo.InvariantCulture);
        _lineKind.ItemsSource = HrComponentKinds.All;
        _lineKind.SelectedItem = HrComponentKinds.Earning;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Hr.PayrollView))
        {
            MessageBox.Show(this, T("Hr.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(HrWinUtil.StatusBar(_statusBar));
        foreach (var (button, key) in new[]
        {
            (_generateButton, "Hr.Generate"), (_postButton, "Hr.Post"), (_reverseButton, "Hr.Reverse"),
            (_payButton, "Hr.PaySalaries"), (_lineAddButton, "Hr.AddLine"), (_lineRemoveButton, "Hr.RemoveLine"),
            (_lineSaveButton, "Hr.SaveLines"), (_refreshButton, "Common.Refresh"), (_closeButton, "Common.Close")
        })
            HrWinUtil.StyleButton(button, key);
        _generateButton.Click += async (_, _) => await GenerateAsync();
        _postButton.Click += async (_, _) => await PostAsync();
        _reverseButton.Click += async (_, _) => await ReverseAsync();
        _payButton.Click += (_, _) => RptNav.OpenPaymentVoucher(this);
        _lineAddButton.Click += (_, _) => AddLine();
        _lineRemoveButton.Click += (_, _) => RemoveLine();
        _lineSaveButton.Click += async (_, _) => await SaveLinesAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(HrWinUtil.BottomBar(_generateButton, _postButton, _reverseButton, _payButton, _refreshButton, _closeButton));

        var sections = new Grid();
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Runs
        var runsPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _runsHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_runsHeader, Dock.Top);
        runsPanel.Children.Add(_runsHeader);
        var periodBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _year.MinWidth = 70; _month.MinWidth = 50; _month.Margin = new Thickness(8, 0, 0, 0);
        _hint.Margin = new Thickness(16, 4, 0, 0);
        _hint.TextWrapping = TextWrapping.Wrap;
        periodBar.Children.Add(_year); periodBar.Children.Add(_month); periodBar.Children.Add(_hint);
        DockPanel.SetDock(periodBar, Dock.Bottom);
        runsPanel.Children.Add(periodBar);
        HrWinUtil.ConfigureGrid(_runsGrid);
        _runsGrid.Columns.Add(TextCol("Hr.RunNo", nameof(HrPayrollRun.RunNo), 100));
        _runsGrid.Columns.Add(TextCol("Hr.Col.Period", nameof(HrPayrollRun.PeriodFrom), 100));
        _runsGrid.Columns.Add(TextCol("Hr.Col.Status", nameof(HrPayrollRun.Status), 80));
        _runsGrid.Columns.Add(TextCol("Hr.Col.Employees", nameof(HrPayrollRun.EmployeeCount), 80));
        _runsGrid.Columns.Add(TextCol("Hr.Col.Gross", nameof(HrPayrollRun.TotalGross), 100));
        _runsGrid.Columns.Add(TextCol("Hr.Col.Deductions", nameof(HrPayrollRun.TotalDeductions), 100));
        _runsGrid.Columns.Add(TextCol("Hr.Col.Net", nameof(HrPayrollRun.TotalNet), 100));
        _runsGrid.Columns.Add(TextCol("Hr.Journal", nameof(HrPayrollRun.JournalDocNo), 110));
        _runsGrid.SelectionChanged += async (_, _) => await OnRunSelectedAsync();
        runsPanel.Children.Add(_runsGrid);
        Grid.SetRow(runsPanel, 0);
        sections.Children.Add(runsPanel);

        // ---- Payslips
        var slipsPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _slipsHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_slipsHeader, Dock.Top);
        slipsPanel.Children.Add(_slipsHeader);
        HrWinUtil.ConfigureGrid(_slipsGrid);
        _slipsGrid.Columns.Add(TextCol("Hr.Col.Employee", nameof(HrPayslip.EmployeeCode), 90));
        _slipsGrid.Columns.Add(TextCol("Hr.Employee", nameof(HrPayslip.EmployeeName), 160));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.Base", nameof(HrPayslip.BaseSalary), 90));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.PaidDays", nameof(HrPayslip.PaidDays), 70));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.UnpaidDays", nameof(HrPayslip.UnpaidLeaveDays), 80));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.AbsentDays", nameof(HrPayslip.AbsentDays), 80));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.Gross", nameof(HrPayslip.GrossPay), 95));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.Deductions", nameof(HrPayslip.TotalDeductions), 95));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.Net", nameof(HrPayslip.NetPay), 95));
        _slipsGrid.Columns.Add(TextCol("Hr.Col.Status", nameof(HrPayslip.Status), 70));
        _slipsGrid.SelectionChanged += (_, _) => OnSlipSelected();
        slipsPanel.Children.Add(_slipsGrid);
        Grid.SetRow(slipsPanel, 1);
        sections.Children.Add(slipsPanel);

        // ---- Payslip lines (draft editing)
        var linesPanel = new DockPanel();
        _linesHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_linesHeader, Dock.Top);
        linesPanel.Children.Add(_linesHeader);
        var lineBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _lineCode.MinWidth = 100; _lineName.MinWidth = 140; _lineName.Margin = new Thickness(8, 0, 0, 0);
        _lineKind.Margin = new Thickness(8, 0, 0, 0);
        _lineAmount.MinWidth = 80; _lineAmount.Margin = new Thickness(8, 0, 0, 0);
        _lineAccount.MinWidth = 70; _lineAccount.Margin = new Thickness(8, 0, 0, 0);
        lineBar.Children.Add(_lineCode); lineBar.Children.Add(_lineName); lineBar.Children.Add(_lineKind);
        lineBar.Children.Add(_lineAmount); lineBar.Children.Add(_lineAccount);
        lineBar.Children.Add(_lineAddButton); lineBar.Children.Add(_lineRemoveButton); lineBar.Children.Add(_lineSaveButton);
        DockPanel.SetDock(lineBar, Dock.Bottom);
        linesPanel.Children.Add(lineBar);
        HrWinUtil.ConfigureGrid(_linesGrid);
        _linesGrid.IsReadOnly = false;
        _linesGrid.Columns.Add(TextCol("Hr.Col.Component", nameof(HrPayslipLine.ComponentCode), 120));
        _linesGrid.Columns.Add(TextCol("Hr.Col.Name", nameof(HrPayslipLine.ComponentName), 180));
        _linesGrid.Columns.Add(TextCol("Hr.Col.Kind", nameof(HrPayslipLine.Kind), 90));
        _linesGrid.Columns.Add(TextCol("Hr.CalcType", nameof(HrPayslipLine.CalcType), 100));
        var amountColumn = new DataGridTextColumn
        {
            Binding = new Binding(nameof(HrPayslipLine.Amount)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
            Width = new DataGridLength(100)
        };
        _headers.Add((amountColumn, "Hr.Col.Amount"));
        _linesGrid.Columns.Add(amountColumn);
        _linesGrid.Columns.Add(TextCol("Hr.Account", nameof(HrPayslipLine.AccountCode), 80));
        linesPanel.Children.Add(_linesGrid);
        Grid.SetRow(linesPanel, 2);
        sections.Children.Add(linesPanel);

        root.Children.Add(sections);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Hr.PayrollTitle");
        _runsHeader.Text = T("Hr.Runs");
        _slipsHeader.Text = T("Hr.Payslips");
        _linesHeader.Text = T("Hr.PayslipLines");
        _hint.Text = T("Hr.PayableAccountHint");
        foreach (var (column, key) in _headers) column.Header = T(key);
        foreach (var b in new[] { _generateButton, _postButton, _reverseButton, _payButton, _lineAddButton, _lineRemoveButton, _lineSaveButton, _refreshButton, _closeButton })
            if (b.Tag is string key) b.Content = T(key);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Hr.ServiceUnavailable"); return; }
            var runs = await _service.GetRunsAsync(CurrentCompanyCode);
            _runs.Clear(); _runs.AddRange(runs);
            _runsGrid.ItemsSource = null; _runsGrid.ItemsSource = _runs;
            _statusBar.Text = Tf("Hr.StatusLine", _runs.Count);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrPayrollWin.RefreshAsync"); }
    }

    private async Task OnRunSelectedAsync()
    {
        try
        {
            _selectedSlip = null;
            _editLines.Clear();
            _linesGrid.ItemsSource = null;
            if (_runsGrid.SelectedItem is not HrPayrollRun header || _service is null)
            {
                _selectedRun = null;
                _slipsGrid.ItemsSource = null;
                return;
            }
            var run = await _service.GetRunAsync(CurrentCompanyCode, header.Id);
            _selectedRun = run;
            _slipsGrid.ItemsSource = null;
            _slipsGrid.ItemsSource = run?.Payslips;
            if (run is not null)
                _statusBar.Text = Tf("Hr.GeneratedLine", run.EmployeeCount, run.RunNo);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrPayrollWin.OnRunSelectedAsync"); }
    }

    private void OnSlipSelected()
    {
        if (_slipsGrid.SelectedItem is not HrPayslip slip) return;
        _selectedSlip = slip;
        _editLines.Clear();
        _editLines.AddRange(slip.Lines);
        _linesGrid.ItemsSource = null;
        _linesGrid.ItemsSource = _editLines;
    }

    private async Task GenerateAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var year = int.TryParse(_year.Text, out var y) ? y : DateTime.Today.Year;
            var month = int.TryParse(_month.Text, out var m) ? Math.Clamp(m, 1, 12) : DateTime.Today.Month;
            var run = await _service.GenerateRunAsync(CurrentCompanyCode, year, month, CurrentUserId);
            _statusBar.Text = Tf("Hr.GeneratedLine", run.EmployeeCount, run.RunNo);
            await RefreshAsync();
            _runsGrid.SelectedItem = _runs.FirstOrDefault(r => r.Id == run.Id);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrPayrollWin.GenerateAsync"); }
    }

    private async Task PostAsync()
    {
        try
        {
            if (_service is null || _selectedRun is null) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollPost)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var doc = await _service.PostRunAsync(CurrentCompanyCode, _selectedRun.Id, CurrentUserId);
            _statusBar.Text = Tf("Hr.PostedLine", doc);
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrPayrollWin.PostAsync"); }
    }

    private async Task ReverseAsync()
    {
        try
        {
            if (_service is null || _selectedRun is null) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollPost)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var doc = await _service.ReverseRunAsync(CurrentCompanyCode, _selectedRun.Id, CurrentUserId);
            _statusBar.Text = Tf("Hr.ReversedLine", doc);
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrPayrollWin.ReverseAsync"); }
    }

    private void AddLine()
    {
        try
        {
            if (_selectedRun is null || _selectedRun.Status != HrPayrollStatuses.Draft)
                throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (string.IsNullOrWhiteSpace(_lineCode.Text)) throw new InvalidOperationException(T("Hr.SelectRecord"));
            _editLines.Add(new HrPayslipLine
            {
                ComponentCode = _lineCode.Text.Trim(),
                ComponentName = HrWinUtil.NullIfBlank(_lineName.Text),
                Kind = _lineKind.SelectedItem as string ?? HrComponentKinds.Earning,
                CalcType = HrCalcTypes.Fixed,
                Amount = decimal.TryParse(_lineAmount.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : 0m,
                AccountCode = HrWinUtil.NullIfBlank(_lineAccount.Text),
                IsSystem = false
            });
            _linesGrid.ItemsSource = null; _linesGrid.ItemsSource = _editLines;
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrPayrollWin.AddLine"); }
    }

    private void RemoveLine()
    {
        if (_linesGrid.SelectedItem is HrPayslipLine line && !line.IsSystem)
        {
            _editLines.Remove(line);
            _linesGrid.ItemsSource = null; _linesGrid.ItemsSource = _editLines;
        }
    }

    private async Task SaveLinesAsync()
    {
        try
        {
            if (_service is null || _selectedSlip is null) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            _linesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            await _service.UpdatePayslipLinesAsync(CurrentCompanyCode, _selectedSlip.Id, _editLines.ToList(), CurrentUserId);
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrPayrollWin.SaveLinesAsync"); }
    }

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn { Binding = new Binding(path), Width = new DataGridLength(width) };
        _headers.Add((column, key));
        return column;
    }
}
