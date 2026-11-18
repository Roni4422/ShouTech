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

/// <summary>One bulk-grid row: an employee and one editable status code
/// per day of the month (P/A/L/V/H, blank = no record).</summary>
public sealed class HrAttendanceRow
{
    public HrAttendanceRow(HrEmployee employee, int daysInMonth)
    {
        Employee = employee;
        Days = new string[daysInMonth];
        Array.Fill(Days, string.Empty);
        Details = new Dictionary<int, HrAttendanceRecord>();
    }

    public HrEmployee Employee { get; }
    public string EmployeeDisplay => Employee.DisplayName;
    public string[] Days { get; }
    /// <summary>Full records (in/out, overtime) keyed by day number,
    /// carried through the grid so a status-only save never loses them.</summary>
    public Dictionary<int, HrAttendanceRecord> Details { get; }

    public string this[int index]
    {
        get => Days[index];
        set => Days[index] = value?.Trim().ToUpperInvariant() ?? string.Empty;
    }
}

/// <summary>
/// Monthly attendance register (Build HR 065, design section 3.3):
/// a bulk grid of active employees × days of the selected month; each
/// cell is a status code. Saving upserts the whole month in ONE
/// transaction (leave-owned rows are never overwritten — the
/// repository protects them). In/out + overtime for the selected
/// employee-day are edited under the grid.
/// </summary>
public sealed class HrAttendanceWin : BaseWin
{
    protected override string? ScheduleTintName => "Cyan";

    private readonly HrService? _service;
    private readonly List<HrAttendanceRow> _rows = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TextBox _year = new();
    private readonly TextBox _month = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _legend = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _inTime = new();
    private readonly TextBox _outTime = new();
    private readonly TextBox _overtime = new();
    private readonly Button _loadButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();
    private int _loadedYear;
    private int _loadedMonth;

    public HrAttendanceWin()
    {
        _service = AppHost.Hr;
        Width = 1180; Height = 640; MinWidth = 660; MinHeight = 360;
        _year.Text = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
        _month.Text = DateTime.Today.Month.ToString(CultureInfo.InvariantCulture);
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Hr.AttendanceView))
        {
            MessageBox.Show(this, T("Hr.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await LoadMonthAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(HrWinUtil.StatusBar(_statusBar));
        HrWinUtil.StyleButton(_loadButton, "Hr.LoadMonth");
        HrWinUtil.StyleButton(_saveButton, "Common.Save");
        HrWinUtil.StyleButton(_refreshButton, "Common.Refresh");
        HrWinUtil.StyleButton(_closeButton, "Common.Close");
        _loadButton.Click += async (_, _) => await LoadMonthAsync();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _refreshButton.Click += async (_, _) => await LoadMonthAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(HrWinUtil.BottomBar(_loadButton, _saveButton, _refreshButton, _closeButton));

        var topBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _year.MinWidth = 70; _month.MinWidth = 50; _month.Margin = new Thickness(8, 0, 0, 0);
        _legend.Margin = new Thickness(16, 4, 0, 0);
        topBar.Children.Add(_year);
        topBar.Children.Add(_month);
        topBar.Children.Add(_legend);
        DockPanel.SetDock(topBar, Dock.Top);
        root.Children.Add(topBar);

        var detailBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _inTime.MinWidth = 70; _outTime.MinWidth = 70; _outTime.Margin = new Thickness(8, 0, 0, 0);
        _overtime.MinWidth = 70; _overtime.Margin = new Thickness(8, 0, 0, 0);
        detailBar.Children.Add(_inTime);
        detailBar.Children.Add(_outTime);
        detailBar.Children.Add(_overtime);
        DockPanel.SetDock(detailBar, Dock.Bottom);
        root.Children.Add(detailBar);

        HrWinUtil.ConfigureGrid(_grid);
        _grid.IsReadOnly = false;
        _grid.CanUserAddRows = false;
        _grid.SelectionUnit = DataGridSelectionUnit.Cell;
        _grid.CurrentCellChanged += (_, _) => OnCellChanged();
        root.Children.Add(_grid);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Hr.AttendanceTitle");
        _legend.Text = T("Hr.CodesLegend");
        _inTime.ToolTip = T("Hr.InTime");
        _outTime.ToolTip = T("Hr.OutTime");
        _overtime.ToolTip = T("Hr.OvertimeHours");
        foreach (var (column, key) in _headers) column.Header = T(key);
        foreach (var b in new[] { _loadButton, _saveButton, _refreshButton, _closeButton })
            if (b.Tag is string key) b.Content = T(key);
    }

    private (int Year, int Month) ReadPeriod()
    {
        var year = int.TryParse(_year.Text, out var y) ? y : DateTime.Today.Year;
        var month = int.TryParse(_month.Text, out var m) ? Math.Clamp(m, 1, 12) : DateTime.Today.Month;
        return (year, month);
    }

    private async Task LoadMonthAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Hr.ServiceUnavailable"); return; }
            var (year, month) = ReadPeriod();
            _loadedYear = year; _loadedMonth = month;
            var daysInMonth = DateTime.DaysInMonth(year, month);
            var employees = await _service.GetEmployeesAsync(CurrentCompanyCode, HrEmployeeStatuses.Active);
            var records = await _service.GetAttendanceAsync(CurrentCompanyCode, year, month);
            _rows.Clear();
            foreach (var employee in employees)
            {
                var row = new HrAttendanceRow(employee, daysInMonth);
                foreach (var record in records.Where(r => r.EmployeeId == employee.Id))
                {
                    var day = DateOnly.Parse(record.AttendanceDate, CultureInfo.InvariantCulture).Day;
                    row.Days[day - 1] = HrAttendanceStatuses.ToCode(record.Status);
                    row.Details[day] = record;
                }
                _rows.Add(row);
            }
            _grid.Columns.Clear();
            _headers.Clear();
            var nameColumn = new DataGridTextColumn
            {
                Binding = new Binding(nameof(HrAttendanceRow.EmployeeDisplay)),
                Width = new DataGridLength(200),
                IsReadOnly = true
            };
            _headers.Add((nameColumn, "Hr.Employee"));
            _grid.Columns.Add(nameColumn);
            for (var day = 1; day <= daysInMonth; day++)
            {
                var index = day - 1;
                _grid.Columns.Add(new DataGridTextColumn
                {
                    Header = day.ToString(CultureInfo.InvariantCulture),
                    Binding = new Binding($"[{index}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                    Width = new DataGridLength(30)
                });
            }
            _grid.ItemsSource = null;
            _grid.ItemsSource = _rows;
            ApplyLocalizedText();
            _statusBar.Text = Tf("Hr.StatusLine", _rows.Count);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrAttendanceWin.LoadMonthAsync"); }
    }

    private void OnCellChanged()
    {
        _inTime.Text = string.Empty; _outTime.Text = string.Empty; _overtime.Text = string.Empty;
        if (_grid.CurrentCell.Item is not HrAttendanceRow row) return;
        var columnIndex = _grid.CurrentCell.Column?.DisplayIndex ?? 0;
        if (columnIndex <= 0) return;
        if (row.Details.TryGetValue(columnIndex, out var record))
        {
            _inTime.Text = record.InTime ?? string.Empty;
            _outTime.Text = record.OutTime ?? string.Empty;
            _overtime.Text = record.OvertimeHours.ToString(CultureInfo.InvariantCulture);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.AttendanceEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            if (_loadedYear == 0) throw new InvalidOperationException(T("Hr.SelectRecord"));
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            // The detail bar edits the currently selected cell's record.
            if (_grid.CurrentCell.Item is HrAttendanceRow currentRow && _grid.CurrentCell.Column is not null
                && _grid.CurrentCell.Column.DisplayIndex > 0)
            {
                var day = _grid.CurrentCell.Column.DisplayIndex;
                if (!currentRow.Details.TryGetValue(day, out var detail))
                {
                    detail = new HrAttendanceRecord { EmployeeId = currentRow.Employee.Id };
                    currentRow.Details[day] = detail;
                }
                detail.InTime = HrWinUtil.NullIfBlank(_inTime.Text);
                detail.OutTime = HrWinUtil.NullIfBlank(_outTime.Text);
                detail.OvertimeHours = decimal.TryParse(_overtime.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var ot) ? ot : 0m;
            }

            var records = new List<HrAttendanceRecord>();
            foreach (var row in _rows)
            {
                for (var day = 1; day <= row.Days.Length; day++)
                {
                    var code = row.Days[day - 1];
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        // A cleared cell for a manually-entered day deletes
                        // nothing here (attendance history is kept); only
                        // entered codes are upserted.
                        continue;
                    }
                    var status = HrAttendanceStatuses.FromCode(code)
                        ?? throw new InvalidOperationException($"'{code}' is not a valid attendance code (P/A/L/V/H).");
                    row.Details.TryGetValue(day, out var detail);
                    records.Add(new HrAttendanceRecord
                    {
                        Id = detail?.Id ?? Guid.NewGuid(),
                        EmployeeId = row.Employee.Id,
                        AttendanceDate = new DateOnly(_loadedYear, _loadedMonth, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Status = status,
                        InTime = detail?.InTime, OutTime = detail?.OutTime,
                        OvertimeHours = detail?.OvertimeHours ?? 0m,
                        Notes = detail?.Notes
                    });
                }
            }
            await _service.SaveAttendanceBatchAsync(CurrentCompanyCode, records, CurrentUserId);
            _statusBar.Text = T("Hr.Saved");
            await LoadMonthAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrAttendanceWin.SaveAsync"); }
    }
}
