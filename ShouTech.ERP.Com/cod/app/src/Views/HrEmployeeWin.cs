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
/// The employee card (Build HR 065, design section 3.2 — the
/// comparison's CORE field set, MatCard standard): record list +
/// navigator, full persistence for every field, the status lifecycle
/// (Active / Suspended / Terminated) and the salary-assignment section
/// (structure + base + from date). Terminated employees leave payroll
/// but keep their history (repository-enforced).
/// </summary>
public sealed class HrEmployeeWin : BaseWin
{
    protected override string? ScheduleTintName => "Basalt";

    private readonly HrService? _service;
    private readonly List<HrEmployee> _employees = new();
    private readonly List<HrDepartment> _departments = new();
    private readonly List<HrDesignation> _designations = new();
    private readonly List<HrSalaryStructure> _structures = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBlock _assignmentInfo = new();

    private readonly TextBox _code = new();
    private readonly ComboBox _status = new();
    private readonly TextBox _firstName = new();
    private readonly TextBox _middleName = new();
    private readonly TextBox _lastName = new();
    private readonly TextBox _nameAr = new();
    private readonly TextBox _gender = new();
    private readonly DatePicker _dateOfBirth = new();
    private readonly TextBox _nationalId = new();
    private readonly TextBox _passportNo = new();
    private readonly TextBox _socialNo = new();
    private readonly TextBox _taxId = new();
    private readonly TextBox _phone = new();
    private readonly TextBox _mobile = new();
    private readonly TextBox _personalEmail = new();
    private readonly TextBox _workEmail = new();
    private readonly TextBox _addressPresent = new();
    private readonly TextBox _addressPermanent = new();
    private readonly TextBox _emergencyName = new();
    private readonly TextBox _emergencyPhone = new();
    private readonly ComboBox _department = new();
    private readonly ComboBox _designation = new();
    private readonly TextBox _branch = new();
    private readonly ComboBox _reportsTo = new();
    private readonly TextBox _costCenter = new();
    private readonly DatePicker _hireDate = new();
    private readonly ComboBox _employmentType = new();
    private readonly ComboBox _salaryMode = new();
    private readonly DatePicker _terminationDate = new();
    private readonly TextBox _terminationReason = new();
    private readonly TextBox _bankName = new();
    private readonly TextBox _bankAccount = new();
    private readonly TextBox _baseSalary = new();
    private readonly TextBox _currency = new();
    private readonly TextBox _userId = new();
    private readonly TextBox _notes = new();

    private readonly ComboBox _assignStructure = new();
    private readonly TextBox _assignBase = new();
    private readonly DatePicker _assignFrom = new();

    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _prevButton = new();
    private readonly Button _nextButton = new();
    private readonly Button _activateButton = new();
    private readonly Button _suspendButton = new();
    private readonly Button _terminateButton = new();
    private readonly Button _assignButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;

    public HrEmployeeWin()
    {
        _service = AppHost.Hr;
        Width = 1180; Height = 680; MinWidth = 660; MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Hr.EmployeeView))
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
            (_newButton, "Common.New"), (_saveButton, "Common.Save"), (_deleteButton, "Common.Delete"),
            (_prevButton, "Hr.Previous"), (_nextButton, "Hr.Next"), (_activateButton, "Hr.Activate"),
            (_suspendButton, "Hr.Suspend"), (_terminateButton, "Hr.Terminate"), (_assignButton, "Hr.Assign"),
            (_refreshButton, "Common.Refresh"), (_closeButton, "Common.Close")
        })
            HrWinUtil.StyleButton(button, key);
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _prevButton.Click += (_, _) => MoveSelection(-1);
        _nextButton.Click += (_, _) => MoveSelection(1);
        _activateButton.Click += async (_, _) => await SetStatusAsync(HrEmployeeStatuses.Active);
        _suspendButton.Click += async (_, _) => await SetStatusAsync(HrEmployeeStatuses.Suspended);
        _terminateButton.Click += async (_, _) => await SetStatusAsync(HrEmployeeStatuses.Terminated);
        _assignButton.Click += async (_, _) => await AssignAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(HrWinUtil.BottomBar(_newButton, _saveButton, _deleteButton, _prevButton, _nextButton,
            _activateButton, _suspendButton, _terminateButton, _refreshButton, _closeButton));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 260 });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        HrWinUtil.ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Hr.EmployeeCode", nameof(HrEmployee.EmployeeCode), 80));
        _grid.Columns.Add(TextCol("Hr.FullName", nameof(HrEmployee.FullName), 150));
        _grid.Columns.Add(TextCol("Hr.Col.Status", nameof(HrEmployee.Status), 80));
        _grid.SelectionChanged += (_, _) => OnSelected();
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var right = new DockPanel();
        var assignPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var assignRow = new StackPanel { Orientation = Orientation.Horizontal };
        _assignStructure.DisplayMemberPath = nameof(HrSalaryStructure.DisplayName);
        _assignStructure.MinWidth = 200;
        _assignBase.MinWidth = 100; _assignBase.Margin = new Thickness(8, 0, 0, 0);
        _assignFrom.Margin = new Thickness(8, 0, 0, 0);
        assignRow.Children.Add(_assignStructure);
        assignRow.Children.Add(_assignBase);
        assignRow.Children.Add(_assignFrom);
        assignRow.Children.Add(_assignButton);
        assignPanel.Children.Add(assignRow);
        _assignmentInfo.Margin = new Thickness(0, 6, 0, 0);
        _assignmentInfo.TextWrapping = TextWrapping.Wrap;
        assignPanel.Children.Add(_assignmentInfo);
        DockPanel.SetDock(assignPanel, Dock.Bottom);
        right.Children.Add(assignPanel);

        var form = CreateFormGrid(2);
        AddFormField(_labels, form, 0, 0, "Hr.EmployeeCode", _code);
        AddFormField(_labels, form, 0, 1, "Hr.Status", _status);
        AddFormField(_labels, form, 1, 0, "Hr.FirstName", _firstName);
        AddFormField(_labels, form, 1, 1, "Hr.MiddleName", _middleName);
        AddFormField(_labels, form, 2, 0, "Hr.LastName", _lastName);
        AddFormField(_labels, form, 2, 1, "Hr.NameAr", _nameAr);
        AddFormField(_labels, form, 3, 0, "Hr.Gender", _gender);
        AddFormField(_labels, form, 3, 1, "Hr.DateOfBirth", _dateOfBirth);
        AddFormField(_labels, form, 4, 0, "Hr.NationalId", _nationalId);
        AddFormField(_labels, form, 4, 1, "Hr.PassportNo", _passportNo);
        AddFormField(_labels, form, 5, 0, "Hr.SocialInsuranceNo", _socialNo);
        AddFormField(_labels, form, 5, 1, "Hr.TaxId", _taxId);
        AddFormField(_labels, form, 6, 0, "Hr.Phone", _phone);
        AddFormField(_labels, form, 6, 1, "Hr.Mobile", _mobile);
        AddFormField(_labels, form, 7, 0, "Hr.PersonalEmail", _personalEmail);
        AddFormField(_labels, form, 7, 1, "Hr.WorkEmail", _workEmail);
        AddFormField(_labels, form, 8, 0, "Hr.AddressPresent", _addressPresent, spanPairs: 2);
        AddFormField(_labels, form, 9, 0, "Hr.AddressPermanent", _addressPermanent, spanPairs: 2);
        AddFormField(_labels, form, 10, 0, "Hr.EmergencyContact", _emergencyName);
        AddFormField(_labels, form, 10, 1, "Hr.EmergencyPhone", _emergencyPhone);
        AddFormField(_labels, form, 11, 0, "Hr.Department", _department);
        AddFormField(_labels, form, 11, 1, "Hr.Designation", _designation);
        AddFormField(_labels, form, 12, 0, "Hr.Branch", _branch);
        AddFormField(_labels, form, 12, 1, "Hr.ReportsTo", _reportsTo);
        AddFormField(_labels, form, 13, 0, "Hr.CostCenter", _costCenter);
        AddFormField(_labels, form, 13, 1, "Hr.HireDate", _hireDate);
        AddFormField(_labels, form, 14, 0, "Hr.EmploymentType", _employmentType);
        AddFormField(_labels, form, 14, 1, "Hr.SalaryMode", _salaryMode);
        AddFormField(_labels, form, 15, 0, "Hr.TerminationDate", _terminationDate);
        AddFormField(_labels, form, 15, 1, "Hr.TerminationReason", _terminationReason);
        AddFormField(_labels, form, 16, 0, "Hr.BankName", _bankName);
        AddFormField(_labels, form, 16, 1, "Hr.BankAccount", _bankAccount);
        AddFormField(_labels, form, 17, 0, "Hr.BaseSalary", _baseSalary);
        AddFormField(_labels, form, 17, 1, "Hr.Currency", _currency);
        AddFormField(_labels, form, 18, 0, "Hr.UserId", _userId);
        AddFormField(_labels, form, 18, 1, "Hr.Notes", _notes);
        _status.ItemsSource = HrEmployeeStatuses.All;
        _status.SelectedItem = HrEmployeeStatuses.Active;
        _employmentType.ItemsSource = HrEmploymentTypes.All;
        _employmentType.SelectedItem = HrEmploymentTypes.FullTime;
        _salaryMode.ItemsSource = HrSalaryModes.All;
        _salaryMode.SelectedItem = HrSalaryModes.Bank;
        _department.DisplayMemberPath = nameof(HrDepartment.DisplayName);
        _designation.DisplayMemberPath = nameof(HrDesignation.DisplayName);
        _reportsTo.DisplayMemberPath = nameof(HrEmployee.DisplayName);
        _hireDate.SelectedDate = DateTime.Today;
        _assignFrom.SelectedDate = DateTime.Today;
        _currency.Text = "SAR";
        right.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(right, 1);
        content.Children.Add(right);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Hr.EmployeeCardTitle");
        foreach (var (label, key) in _labels) label.Text = T(key);
        foreach (var (column, key) in _headers) column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _prevButton, _nextButton, _activateButton, _suspendButton, _terminateButton, _assignButton, _refreshButton, _closeButton })
            if (b.Tag is string key) b.Content = T(key);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Hr.ServiceUnavailable"); return; }
            var rows = await _service.GetEmployeesAsync(CurrentCompanyCode);
            _employees.Clear(); _employees.AddRange(rows);
            _grid.ItemsSource = null; _grid.ItemsSource = _employees;
            _reportsTo.ItemsSource = _employees;
            var departments = await _service.GetDepartmentsAsync(CurrentCompanyCode);
            _departments.Clear(); _departments.AddRange(departments);
            _department.ItemsSource = _departments;
            var designations = await _service.GetDesignationsAsync(CurrentCompanyCode);
            _designations.Clear(); _designations.AddRange(designations);
            _designation.ItemsSource = _designations;
            var structures = await _service.GetStructuresAsync(CurrentCompanyCode);
            _structures.Clear(); _structures.AddRange(structures.Where(s => s.IsActive));
            _assignStructure.ItemsSource = _structures;
            _statusBar.Text = Tf("Hr.StatusLine", _employees.Count);
            if (_selectedId is Guid id && _employees.Any(e => e.Id == id))
                await ShowAssignmentInfoAsync(id);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrEmployeeWin.RefreshAsync"); }
    }

    private async Task ShowAssignmentInfoAsync(Guid employeeId)
    {
        if (_service is null) return;
        var assignments = await _service.GetAssignmentsAsync(CurrentCompanyCode);
        var active = assignments.FirstOrDefault(a => a.EmployeeId == employeeId && a.IsActive);
        _assignmentInfo.Text = active is null
            ? string.Empty
            : $"{active.StructureCode} · {active.BaseSalary:N2} {active.CurrencyCode} · {active.FromDate}";
        if (active is not null)
        {
            _assignStructure.SelectedItem = _structures.FirstOrDefault(s => s.Id == active.StructureId);
            _assignBase.Text = active.BaseSalary.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void OnSelected()
    {
        if (_grid.SelectedItem is not HrEmployee e) return;
        _selectedId = e.Id;
        _code.Text = e.EmployeeCode;
        _status.SelectedItem = e.Status;
        _firstName.Text = e.FirstName; _middleName.Text = e.MiddleName ?? string.Empty;
        _lastName.Text = e.LastName ?? string.Empty; _nameAr.Text = e.NameAr ?? string.Empty;
        _gender.Text = e.Gender ?? string.Empty;
        SetDate(_dateOfBirth, e.DateOfBirth);
        _nationalId.Text = e.NationalId ?? string.Empty; _passportNo.Text = e.PassportNo ?? string.Empty;
        _socialNo.Text = e.SocialInsuranceNo ?? string.Empty; _taxId.Text = e.TaxId ?? string.Empty;
        _phone.Text = e.Phone ?? string.Empty; _mobile.Text = e.Mobile ?? string.Empty;
        _personalEmail.Text = e.PersonalEmail ?? string.Empty; _workEmail.Text = e.WorkEmail ?? string.Empty;
        _addressPresent.Text = e.AddressPresent ?? string.Empty; _addressPermanent.Text = e.AddressPermanent ?? string.Empty;
        _emergencyName.Text = e.EmergencyContactName ?? string.Empty; _emergencyPhone.Text = e.EmergencyContactPhone ?? string.Empty;
        _department.SelectedItem = _departments.FirstOrDefault(d => d.Id == e.DepartmentId);
        _designation.SelectedItem = _designations.FirstOrDefault(d => d.Id == e.DesignationId);
        _branch.Text = e.Branch ?? string.Empty;
        _reportsTo.SelectedItem = _employees.FirstOrDefault(m => m.Id == e.ReportsToId);
        _costCenter.Text = e.CostCenter ?? string.Empty;
        SetDate(_hireDate, e.HireDate);
        _employmentType.SelectedItem = e.EmploymentType;
        _salaryMode.SelectedItem = e.SalaryMode;
        SetDate(_terminationDate, e.TerminationDate);
        _terminationReason.Text = e.TerminationReason ?? string.Empty;
        _bankName.Text = e.BankName ?? string.Empty; _bankAccount.Text = e.BankAccountNo ?? string.Empty;
        _baseSalary.Text = e.BaseSalary.ToString(CultureInfo.InvariantCulture);
        _currency.Text = e.CurrencyCode;
        _userId.Text = e.UserId ?? string.Empty;
        _notes.Text = e.Notes ?? string.Empty;
        _ = ShowAssignmentInfoAsync(e.Id);
    }

    private void ClearEditor()
    {
        _selectedId = null; _grid.SelectedItem = null;
        foreach (var box in new[] { _code, _firstName, _middleName, _lastName, _nameAr, _gender, _nationalId, _passportNo, _socialNo, _taxId, _phone, _mobile, _personalEmail, _workEmail, _addressPresent, _addressPermanent, _emergencyName, _emergencyPhone, _branch, _costCenter, _terminationReason, _bankName, _bankAccount, _userId, _notes })
            box.Text = string.Empty;
        _baseSalary.Text = "0"; _currency.Text = "SAR";
        _status.SelectedItem = HrEmployeeStatuses.Active;
        _employmentType.SelectedItem = HrEmploymentTypes.FullTime;
        _salaryMode.SelectedItem = HrSalaryModes.Bank;
        _department.SelectedItem = null; _designation.SelectedItem = null; _reportsTo.SelectedItem = null;
        _dateOfBirth.SelectedDate = null; _terminationDate.SelectedDate = null;
        _hireDate.SelectedDate = DateTime.Today;
        _assignmentInfo.Text = string.Empty;
    }

    private HrEmployee BuildEmployee()
    {
        var e = _selectedId is Guid id ? _employees.FirstOrDefault(x => x.Id == id) ?? new HrEmployee() : new HrEmployee();
        e.EmployeeCode = _code.Text.Trim();
        e.Status = _status.SelectedItem as string ?? HrEmployeeStatuses.Active;
        e.FirstName = _firstName.Text.Trim();
        e.MiddleName = HrWinUtil.NullIfBlank(_middleName.Text);
        e.LastName = HrWinUtil.NullIfBlank(_lastName.Text);
        e.NameAr = HrWinUtil.NullIfBlank(_nameAr.Text);
        e.Gender = HrWinUtil.NullIfBlank(_gender.Text);
        e.DateOfBirth = GetDate(_dateOfBirth);
        e.NationalId = HrWinUtil.NullIfBlank(_nationalId.Text);
        e.PassportNo = HrWinUtil.NullIfBlank(_passportNo.Text);
        e.SocialInsuranceNo = HrWinUtil.NullIfBlank(_socialNo.Text);
        e.TaxId = HrWinUtil.NullIfBlank(_taxId.Text);
        e.Phone = HrWinUtil.NullIfBlank(_phone.Text);
        e.Mobile = HrWinUtil.NullIfBlank(_mobile.Text);
        e.PersonalEmail = HrWinUtil.NullIfBlank(_personalEmail.Text);
        e.WorkEmail = HrWinUtil.NullIfBlank(_workEmail.Text);
        e.AddressPresent = HrWinUtil.NullIfBlank(_addressPresent.Text);
        e.AddressPermanent = HrWinUtil.NullIfBlank(_addressPermanent.Text);
        e.EmergencyContactName = HrWinUtil.NullIfBlank(_emergencyName.Text);
        e.EmergencyContactPhone = HrWinUtil.NullIfBlank(_emergencyPhone.Text);
        e.DepartmentId = (_department.SelectedItem as HrDepartment)?.Id;
        e.DesignationId = (_designation.SelectedItem as HrDesignation)?.Id;
        e.Branch = HrWinUtil.NullIfBlank(_branch.Text);
        e.ReportsToId = (_reportsTo.SelectedItem as HrEmployee)?.Id;
        e.CostCenter = HrWinUtil.NullIfBlank(_costCenter.Text);
        e.HireDate = GetDate(_hireDate) ?? string.Empty;
        e.EmploymentType = _employmentType.SelectedItem as string ?? HrEmploymentTypes.FullTime;
        e.SalaryMode = _salaryMode.SelectedItem as string ?? HrSalaryModes.Bank;
        e.TerminationDate = GetDate(_terminationDate);
        e.TerminationReason = HrWinUtil.NullIfBlank(_terminationReason.Text);
        e.BankName = HrWinUtil.NullIfBlank(_bankName.Text);
        e.BankAccountNo = HrWinUtil.NullIfBlank(_bankAccount.Text);
        e.BaseSalary = decimal.TryParse(_baseSalary.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var salary) ? salary : -1m;
        e.CurrencyCode = string.IsNullOrWhiteSpace(_currency.Text) ? "SAR" : _currency.Text.Trim();
        e.UserId = HrWinUtil.NullIfBlank(_userId.Text);
        e.Notes = HrWinUtil.NullIfBlank(_notes.Text);
        return e;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.EmployeeEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var e = BuildEmployee();
            await _service.SaveEmployeeAsync(CurrentCompanyCode, e, CurrentUserId);
            _selectedId = e.Id;
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
            _grid.SelectedItem = _employees.FirstOrDefault(x => x.Id == e.Id);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrEmployeeWin.SaveAsync"); }
    }

    private async Task SetStatusAsync(string status)
    {
        try
        {
            if (_selectedId is null) throw new InvalidOperationException(T("Hr.SelectEmployee"));
            _status.SelectedItem = status;
            if (status == HrEmployeeStatuses.Terminated && _terminationDate.SelectedDate is null)
                _terminationDate.SelectedDate = DateTime.Today;
            await SaveAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrEmployeeWin.SetStatusAsync"); }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id) throw new InvalidOperationException(T("Hr.SelectEmployee"));
            if (!AppHost.Permissions.Has(PermCat.Hr.EmployeeEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.DeleteEmployeeAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrEmployeeWin.DeleteAsync"); }
    }

    private async Task AssignAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid employeeId) throw new InvalidOperationException(T("Hr.SelectEmployee"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            if (_assignStructure.SelectedItem is not HrSalaryStructure structure) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!decimal.TryParse(_assignBase.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var baseSalary))
                throw new InvalidOperationException(T("Common.InvalidNumberFormat"));
            var assignment = new HrSalaryAssignment
            {
                EmployeeId = employeeId, StructureId = structure.Id, BaseSalary = baseSalary,
                CurrencyCode = string.IsNullOrWhiteSpace(_currency.Text) ? "SAR" : _currency.Text.Trim(),
                FromDate = GetDate(_assignFrom) ?? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                IsActive = true
            };
            await _service.SaveAssignmentAsync(CurrentCompanyCode, assignment, CurrentUserId);
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrEmployeeWin.AssignAsync"); }
    }

    private void MoveSelection(int delta)
    {
        if (_employees.Count == 0) return;
        var index = _selectedId is Guid id ? _employees.FindIndex(e => e.Id == id) : -1;
        index = Math.Clamp(index + delta, 0, _employees.Count - 1);
        _grid.SelectedItem = _employees[index];
        _grid.ScrollIntoView(_employees[index]);
    }

    private static string? GetDate(DatePicker picker)
        => picker.SelectedDate is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static void SetDate(DatePicker picker, string? value)
        => picker.SelectedDate = !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn { Binding = new Binding(path), Width = new DataGridLength(width) };
        _headers.Add((column, key));
        return column;
    }
}
