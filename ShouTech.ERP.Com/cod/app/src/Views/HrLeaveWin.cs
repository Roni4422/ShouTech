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
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Leave management (Build HR 065, design section 3.4): leave types,
/// per-employee balances with allocation, and applications with the
/// approve/reject workflow. Approval is permission-separated
/// (HR.LEAVE.APPROVE) and balance-checked in the repository — an
/// over-balance approval goes through only when the caller holds
/// HR.LEAVE.OVERRIDE, which this window passes as an explicit flag.
/// Approved leave writes its attendance rows and deducts the balance
/// in the same transaction; cancelling restores both.
/// </summary>
public sealed class HrLeaveWin : BaseWin
{
    protected override string? ScheduleTintName => "Denim";

    private readonly HrService? _service;
    private readonly List<HrLeaveType> _types = new();
    private readonly List<HrLeaveApplication> _applications = new();
    private readonly List<HrEmployee> _employees = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TextBlock _typesHeader = new();
    private readonly TextBlock _balancesHeader = new();
    private readonly TextBlock _appsHeader = new();
    private readonly DataGrid _typesGrid = new();
    private readonly DataGrid _balancesGrid = new();
    private readonly DataGrid _appsGrid = new();
    private readonly TextBlock _statusBar = new();

    private readonly TextBox _typeCode = new();
    private readonly TextBox _typeName = new();
    private readonly TextBox _typeDays = new();
    private readonly CheckBox _typePaid = new() { IsChecked = true };
    private readonly Button _typeSaveButton = new();
    private readonly Button _typeDeleteButton = new();

    private readonly ComboBox _allocEmployee = new();
    private readonly ComboBox _allocType = new();
    private readonly TextBox _allocYear = new();
    private readonly TextBox _allocDays = new();
    private readonly Button _allocateButton = new();

    private readonly ComboBox _appEmployee = new();
    private readonly ComboBox _appType = new();
    private readonly DatePicker _appFrom = new();
    private readonly DatePicker _appTo = new();
    private readonly CheckBox _appHalfDay = new();
    private readonly TextBox _appReason = new();
    private readonly Button _appSaveButton = new();
    private readonly Button _approveButton = new();
    private readonly Button _rejectButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedTypeId;
    private Guid? _selectedAppId;

    public HrLeaveWin()
    {
        _service = AppHost.Hr;
        Width = 1180; Height = 680; MinWidth = 660; MinHeight = 360;
        _allocYear.Text = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        _appFrom.SelectedDate = DateTime.Today;
        _appTo.SelectedDate = DateTime.Today;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Hr.LeaveView))
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
            (_typeSaveButton, "Common.Save"), (_typeDeleteButton, "Common.Delete"),
            (_allocateButton, "Hr.Allocate"), (_appSaveButton, "Common.Save"),
            (_approveButton, "Hr.Approve"), (_rejectButton, "Hr.Reject"), (_cancelButton, "Hr.Cancel"),
            (_refreshButton, "Common.Refresh"), (_closeButton, "Common.Close")
        })
            HrWinUtil.StyleButton(button, key);
        _typeSaveButton.Click += async (_, _) => await SaveTypeAsync();
        _typeDeleteButton.Click += async (_, _) => await DeleteTypeAsync();
        _allocateButton.Click += async (_, _) => await AllocateAsync();
        _appSaveButton.Click += async (_, _) => await SaveApplicationAsync();
        _approveButton.Click += async (_, _) => await ApproveAsync();
        _rejectButton.Click += async (_, _) => await RejectAsync();
        _cancelButton.Click += async (_, _) => await CancelAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(HrWinUtil.BottomBar(_refreshButton, _closeButton));

        var sections = new Grid();
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });

        // ---- Leave types
        var typesPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _typesHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_typesHeader, Dock.Top);
        typesPanel.Children.Add(_typesHeader);
        var typeBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _typeCode.MinWidth = 90; _typeName.MinWidth = 160; _typeName.Margin = new Thickness(8, 0, 0, 0);
        _typeDays.MinWidth = 60; _typeDays.Margin = new Thickness(8, 0, 0, 0);
        _typePaid.Margin = new Thickness(8, 4, 0, 0);
        typeBar.Children.Add(_typeCode); typeBar.Children.Add(_typeName); typeBar.Children.Add(_typeDays);
        typeBar.Children.Add(_typePaid); typeBar.Children.Add(_typeSaveButton); typeBar.Children.Add(_typeDeleteButton);
        DockPanel.SetDock(typeBar, Dock.Bottom);
        typesPanel.Children.Add(typeBar);
        HrWinUtil.ConfigureGrid(_typesGrid);
        _typesGrid.Columns.Add(TextCol("Hr.Code", nameof(HrLeaveType.Code), 90));
        _typesGrid.Columns.Add(TextCol("Hr.Col.Name", nameof(HrLeaveType.NameEn), 180));
        _typesGrid.Columns.Add(TextCol("Hr.AnnualDays", nameof(HrLeaveType.AnnualDays), 90));
        _typesGrid.Columns.Add(TextCol("Hr.Paid", nameof(HrLeaveType.IsPaid), 60));
        _typesGrid.SelectionChanged += (_, _) =>
        {
            if (_typesGrid.SelectedItem is not HrLeaveType t) return;
            _selectedTypeId = t.Id;
            _typeCode.Text = t.Code; _typeName.Text = t.NameEn;
            _typeDays.Text = t.AnnualDays.ToString(CultureInfo.InvariantCulture);
            _typePaid.IsChecked = t.IsPaid;
        };
        typesPanel.Children.Add(_typesGrid);
        Grid.SetRow(typesPanel, 0);
        sections.Children.Add(typesPanel);

        // ---- Balances
        var balancesPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _balancesHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_balancesHeader, Dock.Top);
        balancesPanel.Children.Add(_balancesHeader);
        var allocBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _allocEmployee.DisplayMemberPath = nameof(HrEmployee.DisplayName);
        _allocEmployee.MinWidth = 200;
        _allocType.DisplayMemberPath = nameof(HrLeaveType.DisplayName);
        _allocType.MinWidth = 160; _allocType.Margin = new Thickness(8, 0, 0, 0);
        _allocYear.MinWidth = 60; _allocYear.Margin = new Thickness(8, 0, 0, 0);
        _allocDays.MinWidth = 60; _allocDays.Margin = new Thickness(8, 0, 0, 0);
        allocBar.Children.Add(_allocEmployee); allocBar.Children.Add(_allocType);
        allocBar.Children.Add(_allocYear); allocBar.Children.Add(_allocDays); allocBar.Children.Add(_allocateButton);
        DockPanel.SetDock(allocBar, Dock.Bottom);
        balancesPanel.Children.Add(allocBar);
        HrWinUtil.ConfigureGrid(_balancesGrid);
        _balancesGrid.Columns.Add(TextCol("Hr.Col.Employee", nameof(HrLeaveBalance.EmployeeCode), 90));
        _balancesGrid.Columns.Add(TextCol("Hr.Col.LeaveType", nameof(HrLeaveBalance.LeaveTypeCode), 90));
        _balancesGrid.Columns.Add(TextCol("Hr.Col.Allocated", nameof(HrLeaveBalance.AllocatedDays), 90));
        _balancesGrid.Columns.Add(TextCol("Hr.Col.Used", nameof(HrLeaveBalance.UsedDays), 80));
        _balancesGrid.Columns.Add(TextCol("Hr.Col.Balance", nameof(HrLeaveBalance.Balance), 80));
        balancesPanel.Children.Add(_balancesGrid);
        Grid.SetRow(balancesPanel, 1);
        sections.Children.Add(balancesPanel);

        // ---- Applications
        var appsPanel = new DockPanel();
        _appsHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_appsHeader, Dock.Top);
        appsPanel.Children.Add(_appsHeader);
        var appBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _appEmployee.DisplayMemberPath = nameof(HrEmployee.DisplayName);
        _appEmployee.MinWidth = 180;
        _appType.DisplayMemberPath = nameof(HrLeaveType.DisplayName);
        _appType.MinWidth = 140; _appType.Margin = new Thickness(8, 0, 0, 0);
        _appFrom.Margin = new Thickness(8, 0, 0, 0); _appTo.Margin = new Thickness(8, 0, 0, 0);
        _appHalfDay.Margin = new Thickness(8, 4, 0, 0);
        _appReason.MinWidth = 140; _appReason.Margin = new Thickness(8, 0, 0, 0);
        appBar.Children.Add(_appEmployee); appBar.Children.Add(_appType); appBar.Children.Add(_appFrom);
        appBar.Children.Add(_appTo); appBar.Children.Add(_appHalfDay); appBar.Children.Add(_appReason);
        appBar.Children.Add(_appSaveButton); appBar.Children.Add(_approveButton);
        appBar.Children.Add(_rejectButton); appBar.Children.Add(_cancelButton);
        DockPanel.SetDock(appBar, Dock.Bottom);
        appsPanel.Children.Add(appBar);
        HrWinUtil.ConfigureGrid(_appsGrid);
        _appsGrid.Columns.Add(TextCol("Hr.Col.Employee", nameof(HrLeaveApplication.EmployeeCode), 90));
        _appsGrid.Columns.Add(TextCol("Hr.Col.LeaveType", nameof(HrLeaveApplication.LeaveTypeCode), 90));
        _appsGrid.Columns.Add(TextCol("Hr.FromDate", nameof(HrLeaveApplication.FromDate), 95));
        _appsGrid.Columns.Add(TextCol("Hr.ToDate", nameof(HrLeaveApplication.ToDate), 95));
        _appsGrid.Columns.Add(TextCol("Hr.Col.Days", nameof(HrLeaveApplication.TotalDays), 60));
        _appsGrid.Columns.Add(TextCol("Hr.Col.Status", nameof(HrLeaveApplication.Status), 90));
        _appsGrid.SelectionChanged += (_, _) =>
        {
            if (_appsGrid.SelectedItem is not HrLeaveApplication a) return;
            _selectedAppId = a.Id;
            _appEmployee.SelectedItem = _employees.FirstOrDefault(e => e.Id == a.EmployeeId);
            _appType.SelectedItem = _types.FirstOrDefault(t => t.Id == a.LeaveTypeId);
            _appFrom.SelectedDate = ParseDate(a.FromDate);
            _appTo.SelectedDate = ParseDate(a.ToDate);
            _appHalfDay.IsChecked = a.HalfDay;
            _appReason.Text = a.Reason ?? string.Empty;
        };
        appsPanel.Children.Add(_appsGrid);
        Grid.SetRow(appsPanel, 2);
        sections.Children.Add(appsPanel);

        root.Children.Add(sections);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Hr.LeaveTitle");
        _typesHeader.Text = T("Hr.LeaveTypes");
        _balancesHeader.Text = T("Hr.Balances");
        _appsHeader.Text = T("Hr.Applications");
        _typePaid.Content = T("Hr.Paid");
        _appHalfDay.Content = T("Hr.HalfDay");
        foreach (var (label, key) in _labels) label.Text = T(key);
        foreach (var (column, key) in _headers) column.Header = T(key);
        foreach (var b in new[] { _typeSaveButton, _typeDeleteButton, _allocateButton, _appSaveButton, _approveButton, _rejectButton, _cancelButton, _refreshButton, _closeButton })
            if (b.Tag is string key) b.Content = T(key);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Hr.ServiceUnavailable"); return; }
            var types = await _service.GetLeaveTypesAsync(CurrentCompanyCode);
            _types.Clear(); _types.AddRange(types);
            _typesGrid.ItemsSource = null; _typesGrid.ItemsSource = _types;
            _allocType.ItemsSource = _types; _appType.ItemsSource = _types;
            var employees = await _service.GetEmployeesAsync(CurrentCompanyCode);
            _employees.Clear(); _employees.AddRange(employees);
            _allocEmployee.ItemsSource = _employees; _appEmployee.ItemsSource = _employees;
            var year = int.TryParse(_allocYear.Text, out var y) ? y : DateTime.Today.Year;
            var balances = await _service.GetLeaveBalancesAsync(CurrentCompanyCode, year);
            _balancesGrid.ItemsSource = null; _balancesGrid.ItemsSource = balances;
            var apps = await _service.GetLeaveApplicationsAsync(CurrentCompanyCode);
            _applications.Clear(); _applications.AddRange(apps);
            _appsGrid.ItemsSource = null; _appsGrid.ItemsSource = _applications;
            _statusBar.Text = Tf("Hr.StatusLine", _applications.Count);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.RefreshAsync"); }
    }

    private async Task SaveTypeAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.LeaveEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var type = _selectedTypeId is Guid id ? _types.FirstOrDefault(t => t.Id == id) ?? new HrLeaveType() : new HrLeaveType();
            type.Code = _typeCode.Text.Trim();
            type.NameEn = _typeName.Text.Trim();
            type.AnnualDays = decimal.TryParse(_typeDays.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var days) ? days : -1m;
            type.IsPaid = _typePaid.IsChecked == true;
            await _service.SaveLeaveTypeAsync(CurrentCompanyCode, type, CurrentUserId);
            _selectedTypeId = type.Id;
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.SaveTypeAsync"); }
    }

    private async Task DeleteTypeAsync()
    {
        try
        {
            if (_service is null || _selectedTypeId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.LeaveEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.DeleteLeaveTypeAsync(CurrentCompanyCode, id, CurrentUserId);
            _selectedTypeId = null;
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.DeleteTypeAsync"); }
    }

    private async Task AllocateAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.LeaveEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            if (_allocEmployee.SelectedItem is not HrEmployee employee) throw new InvalidOperationException(T("Hr.SelectEmployee"));
            if (_allocType.SelectedItem is not HrLeaveType type) throw new InvalidOperationException(T("Hr.SelectRecord"));
            var year = int.TryParse(_allocYear.Text, out var y) ? y : DateTime.Today.Year;
            if (!decimal.TryParse(_allocDays.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var days))
                throw new InvalidOperationException(T("Common.InvalidNumberFormat"));
            await _service.AllocateLeaveAsync(CurrentCompanyCode, employee.Id, type.Id, year, days, CurrentUserId);
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.AllocateAsync"); }
    }

    private async Task SaveApplicationAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.LeaveEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            if (_appEmployee.SelectedItem is not HrEmployee employee) throw new InvalidOperationException(T("Hr.SelectEmployee"));
            if (_appType.SelectedItem is not HrLeaveType type) throw new InvalidOperationException(T("Hr.SelectRecord"));
            var app = _selectedAppId is Guid id ? _applications.FirstOrDefault(a => a.Id == id) ?? new HrLeaveApplication() : new HrLeaveApplication();
            app.EmployeeId = employee.Id;
            app.LeaveTypeId = type.Id;
            app.FromDate = FormatDate(_appFrom);
            app.ToDate = FormatDate(_appTo);
            app.HalfDay = _appHalfDay.IsChecked == true;
            app.Reason = HrWinUtil.NullIfBlank(_appReason.Text);
            await _service.SaveLeaveApplicationAsync(CurrentCompanyCode, app, CurrentUserId);
            _selectedAppId = app.Id;
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.SaveApplicationAsync"); }
    }

    private async Task ApproveAsync()
    {
        try
        {
            if (_service is null || _selectedAppId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.LeaveApprove)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            // The override flag is set ONLY from the caller's own
            // HR.LEAVE.OVERRIDE grant — never from the application.
            var allowOverride = AppHost.Permissions.Has(PermCat.Hr.LeaveOverride);
            await _service.ApproveLeaveAsync(CurrentCompanyCode, id, allowOverride, CurrentUserId);
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.ApproveAsync"); }
    }

    private async Task RejectAsync()
    {
        try
        {
            if (_service is null || _selectedAppId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.LeaveApprove)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.RejectLeaveAsync(CurrentCompanyCode, id, null, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.RejectAsync"); }
    }

    private async Task CancelAsync()
    {
        try
        {
            if (_service is null || _selectedAppId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.LeaveEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.CancelLeaveAsync(CurrentCompanyCode, id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrLeaveWin.CancelAsync"); }
    }

    private static string FormatDate(DatePicker picker)
        => picker.SelectedDate is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    private static DateTime? ParseDate(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn { Binding = new Binding(path), Width = new DataGridLength(width) };
        _headers.Add((column, key));
        return column;
    }
}
