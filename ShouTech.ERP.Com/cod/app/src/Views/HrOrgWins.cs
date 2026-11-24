using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>Shared scaffolding for the two HR organisation masters
/// (list + card on the CrmLeadWin pattern, bottom command bar).</summary>
internal static class HrWinUtil
{
    public static void ConfigureGrid(DataGrid grid)
    {
        grid.AutoGenerateColumns = false;
        grid.CanUserAddRows = false;
        grid.CanUserDeleteRows = false;
        grid.IsReadOnly = true;
        grid.CanUserSortColumns = true;
        grid.CanUserReorderColumns = true;
        grid.CanUserResizeColumns = true;
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.RowHeaderWidth = 0;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        grid.AlternationCount = 2;
        DataGridTableBehavior.SetEnableStandardContextMenu(grid, true);
    }

    public static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }

    public static Border BottomBar(params Button[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        foreach (var b in buttons) bar.Children.Add(b);
        var border = new Border
        {
            Child = bar,
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(border, Dock.Bottom);
        return border;
    }

    public static Border StatusBar(TextBlock status)
    {
        var border = new Border
        {
            Child = status,
            Padding = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(border, Dock.Bottom);
        return border;
    }

    public static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static void ReportError(Window owner, Exception ex, string context)
    {
        AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, context);
        MessageBox.Show(owner, ex.Message, owner.Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

/// <summary>
/// Departments master (Build HR 065, design section 3.1): tree-capable
/// list + card — code, names, parent, manager, cost center, active.
/// Deletion is guarded by the repository while employees or child
/// departments reference the record (HR-1008).
/// </summary>
public sealed class HrDeptWin : BaseWin
{
    protected override string? ScheduleTintName => "Sage";

    private readonly HrService? _service;
    private readonly List<HrDepartment> _rows = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _code = new();
    private readonly TextBox _nameEn = new();
    private readonly TextBox _nameAr = new();
    private readonly ComboBox _parent = new();
    private readonly ComboBox _manager = new();
    private readonly TextBox _costCenter = new();
    private readonly CheckBox _active = new() { IsChecked = true };
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();
    private Guid? _selectedId;

    public HrDeptWin()
    {
        _service = AppHost.Hr;
        Width = 980; Height = 560; MinWidth = 660; MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Hr.OrgView))
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
        HrWinUtil.StyleButton(_newButton, "Common.New");
        HrWinUtil.StyleButton(_saveButton, "Common.Save");
        HrWinUtil.StyleButton(_deleteButton, "Common.Delete");
        HrWinUtil.StyleButton(_refreshButton, "Common.Refresh");
        HrWinUtil.StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(HrWinUtil.BottomBar(_newButton, _saveButton, _deleteButton, _refreshButton, _closeButton));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });
        HrWinUtil.ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Hr.Code", nameof(HrDepartment.Code), 90));
        _grid.Columns.Add(TextCol("Hr.Col.Name", nameof(HrDepartment.NameEn), 200));
        _grid.Columns.Add(TextCol("Hr.CostCenter", nameof(HrDepartment.CostCenter), 110));
        _grid.SelectionChanged += (_, _) => OnSelected();
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Hr.Code", _code);
        AddFormField(_labels, form, 1, 0, "Hr.NameEn", _nameEn);
        AddFormField(_labels, form, 2, 0, "Hr.NameAr", _nameAr);
        AddFormField(_labels, form, 3, 0, "Hr.ParentDepartment", _parent);
        AddFormField(_labels, form, 4, 0, "Hr.Manager", _manager);
        AddFormField(_labels, form, 5, 0, "Hr.CostCenter", _costCenter);
        AddFormField(_labels, form, 6, 0, "Hr.Active", _active);
        _parent.DisplayMemberPath = nameof(HrDepartment.DisplayName);
        _manager.DisplayMemberPath = nameof(HrEmployee.DisplayName);
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        content.Children.Add(formScroll);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Hr.DepartmentsTitle");
        foreach (var (label, key) in _labels) label.Text = T(key);
        foreach (var (column, key) in _headers) column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _refreshButton, _closeButton })
            if (b.Tag is string key) b.Content = T(key);
        _active.Content = T("Hr.Active");
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Hr.ServiceUnavailable"); return; }
            var rows = await _service.GetDepartmentsAsync(CurrentCompanyCode);
            _rows.Clear(); _rows.AddRange(rows);
            _grid.ItemsSource = null; _grid.ItemsSource = _rows;
            _parent.ItemsSource = _rows;
            _manager.ItemsSource = await _service.GetEmployeesAsync(CurrentCompanyCode);
            _statusBar.Text = Tf("Hr.StatusLine", _rows.Count);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrDeptWin.RefreshAsync"); }
    }

    private void OnSelected()
    {
        if (_grid.SelectedItem is not HrDepartment row) return;
        _selectedId = row.Id;
        _code.Text = row.Code; _nameEn.Text = row.NameEn; _nameAr.Text = row.NameAr;
        _parent.SelectedItem = _rows.FirstOrDefault(d => d.Id == row.ParentId);
        _costCenter.Text = row.CostCenter ?? string.Empty;
        _active.IsChecked = row.IsActive;
        _manager.SelectedItem = (_manager.ItemsSource as IEnumerable<HrEmployee>)?.FirstOrDefault(e => e.Id == row.ManagerEmployeeId);
    }

    private void ClearEditor()
    {
        _selectedId = null; _grid.SelectedItem = null;
        _code.Text = string.Empty; _nameEn.Text = string.Empty; _nameAr.Text = string.Empty;
        _parent.SelectedItem = null; _manager.SelectedItem = null;
        _costCenter.Text = string.Empty; _active.IsChecked = true;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.OrgEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var row = _selectedId is Guid id ? _rows.FirstOrDefault(r => r.Id == id) ?? new HrDepartment() : new HrDepartment();
            row.Code = _code.Text.Trim(); row.NameEn = _nameEn.Text.Trim(); row.NameAr = _nameAr.Text.Trim();
            row.ParentId = (_parent.SelectedItem as HrDepartment)?.Id;
            row.ManagerEmployeeId = (_manager.SelectedItem as HrEmployee)?.Id;
            row.CostCenter = HrWinUtil.NullIfBlank(_costCenter.Text);
            row.IsActive = _active.IsChecked == true;
            await _service.SaveDepartmentAsync(CurrentCompanyCode, row, CurrentUserId);
            _selectedId = row.Id;
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrDeptWin.SaveAsync"); }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.OrgEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.DeleteDepartmentAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrDeptWin.DeleteAsync"); }
    }

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn { Binding = new Binding(path), Width = new DataGridLength(width) };
        _headers.Add((column, key));
        return column;
    }
}

/// <summary>
/// Designations master (Build HR 065, design section 3.1): list + card
/// — code, names, department, description, active. Deletion is guarded
/// while employees reference the designation (HR-1008).
/// </summary>
public sealed class HrDesigWin : BaseWin
{
    protected override string? ScheduleTintName => "Fern";

    private readonly HrService? _service;
    private readonly List<HrDesignation> _rows = new();
    private readonly List<HrDepartment> _departments = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _code = new();
    private readonly TextBox _nameEn = new();
    private readonly TextBox _nameAr = new();
    private readonly ComboBox _department = new();
    private readonly TextBox _description = new();
    private readonly CheckBox _active = new() { IsChecked = true };
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();
    private Guid? _selectedId;

    public HrDesigWin()
    {
        _service = AppHost.Hr;
        Width = 980; Height = 560; MinWidth = 660; MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Hr.OrgView))
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
        HrWinUtil.StyleButton(_newButton, "Common.New");
        HrWinUtil.StyleButton(_saveButton, "Common.Save");
        HrWinUtil.StyleButton(_deleteButton, "Common.Delete");
        HrWinUtil.StyleButton(_refreshButton, "Common.Refresh");
        HrWinUtil.StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(HrWinUtil.BottomBar(_newButton, _saveButton, _deleteButton, _refreshButton, _closeButton));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });
        HrWinUtil.ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Hr.Code", nameof(HrDesignation.Code), 90));
        _grid.Columns.Add(TextCol("Hr.Col.Name", nameof(HrDesignation.NameEn), 200));
        _grid.SelectionChanged += (_, _) => OnSelected();
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Hr.Code", _code);
        AddFormField(_labels, form, 1, 0, "Hr.NameEn", _nameEn);
        AddFormField(_labels, form, 2, 0, "Hr.NameAr", _nameAr);
        AddFormField(_labels, form, 3, 0, "Hr.Department", _department);
        AddFormField(_labels, form, 4, 0, "Hr.Description", _description);
        AddFormField(_labels, form, 5, 0, "Hr.Active", _active);
        _department.DisplayMemberPath = nameof(HrDepartment.DisplayName);
        _description.TextWrapping = TextWrapping.Wrap;
        _description.MinHeight = 44;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        content.Children.Add(formScroll);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Hr.DesignationsTitle");
        foreach (var (label, key) in _labels) label.Text = T(key);
        foreach (var (column, key) in _headers) column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _refreshButton, _closeButton })
            if (b.Tag is string key) b.Content = T(key);
        _active.Content = T("Hr.Active");
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Hr.ServiceUnavailable"); return; }
            var rows = await _service.GetDesignationsAsync(CurrentCompanyCode);
            _rows.Clear(); _rows.AddRange(rows);
            _grid.ItemsSource = null; _grid.ItemsSource = _rows;
            var departments = await _service.GetDepartmentsAsync(CurrentCompanyCode);
            _departments.Clear(); _departments.AddRange(departments);
            _department.ItemsSource = _departments;
            _statusBar.Text = Tf("Hr.StatusLine", _rows.Count);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrDesigWin.RefreshAsync"); }
    }

    private void OnSelected()
    {
        if (_grid.SelectedItem is not HrDesignation row) return;
        _selectedId = row.Id;
        _code.Text = row.Code; _nameEn.Text = row.NameEn; _nameAr.Text = row.NameAr;
        _department.SelectedItem = _departments.FirstOrDefault(d => d.Id == row.DepartmentId);
        _description.Text = row.Description ?? string.Empty;
        _active.IsChecked = row.IsActive;
    }

    private void ClearEditor()
    {
        _selectedId = null; _grid.SelectedItem = null;
        _code.Text = string.Empty; _nameEn.Text = string.Empty; _nameAr.Text = string.Empty;
        _department.SelectedItem = null; _description.Text = string.Empty; _active.IsChecked = true;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.OrgEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var row = _selectedId is Guid id ? _rows.FirstOrDefault(r => r.Id == id) ?? new HrDesignation() : new HrDesignation();
            row.Code = _code.Text.Trim(); row.NameEn = _nameEn.Text.Trim(); row.NameAr = _nameAr.Text.Trim();
            row.DepartmentId = (_department.SelectedItem as HrDepartment)?.Id;
            row.Description = HrWinUtil.NullIfBlank(_description.Text);
            row.IsActive = _active.IsChecked == true;
            await _service.SaveDesignationAsync(CurrentCompanyCode, row, CurrentUserId);
            _selectedId = row.Id;
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrDesigWin.SaveAsync"); }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.OrgEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.DeleteDesignationAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrDesigWin.DeleteAsync"); }
    }

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn { Binding = new Binding(path), Width = new DataGridLength(width) };
        _headers.Add((column, key));
        return column;
    }
}
