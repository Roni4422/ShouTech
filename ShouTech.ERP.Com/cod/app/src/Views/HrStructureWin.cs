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
/// Salary structures (Build HR 065, design section 3.5): the component
/// catalog (earnings / deductions / employer contributions — fixed or
/// % formulas, rates in DATA, never hardcoded law), structure editing
/// with per-line GL account mapping, and designation-default
/// assignments. Employee assignments live on the employee card.
/// </summary>
public sealed class HrStructureWin : BaseWin
{
    protected override string? ScheduleTintName => "Alder";

    private readonly HrService? _service;
    private readonly List<HrSalaryComponent> _components = new();
    private readonly List<HrSalaryStructure> _structures = new();
    private readonly List<HrStructureLine> _editLines = new();
    private readonly List<HrDesignation> _designations = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TextBlock _componentsHeader = new();
    private readonly TextBlock _structuresHeader = new();
    private readonly TextBlock _assignmentsHeader = new();
    private readonly DataGrid _componentsGrid = new();
    private readonly DataGrid _structuresGrid = new();
    private readonly DataGrid _linesGrid = new();
    private readonly DataGrid _assignmentsGrid = new();
    private readonly TextBlock _statusBar = new();

    private readonly TextBox _compCode = new();
    private readonly TextBox _compName = new();
    private readonly ComboBox _compKind = new();
    private readonly ComboBox _compCalc = new();
    private readonly TextBox _compAmount = new();
    private readonly TextBox _compPercent = new();
    private readonly Button _compSaveButton = new();
    private readonly Button _compDeleteButton = new();

    private readonly TextBox _structCode = new();
    private readonly TextBox _structName = new();
    private readonly ComboBox _lineComponent = new();
    private readonly ComboBox _lineCalc = new();
    private readonly TextBox _lineAmount = new();
    private readonly TextBox _linePercent = new();
    private readonly TextBox _lineAccount = new();
    private readonly Button _lineAddButton = new();
    private readonly Button _lineRemoveButton = new();
    private readonly Button _structSaveButton = new();
    private readonly Button _structDeleteButton = new();

    private readonly ComboBox _assignDesignation = new();
    private readonly ComboBox _assignStructure = new();
    private readonly TextBox _assignBase = new();
    private readonly DatePicker _assignFrom = new();
    private readonly Button _assignButton = new();
    private readonly Button _deactivateButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedComponentId;
    private Guid? _selectedStructureId;
    private Guid? _selectedAssignmentId;

    public HrStructureWin()
    {
        _service = AppHost.Hr;
        Width = 1180; Height = 700; MinWidth = 660; MinHeight = 360;
        _compKind.ItemsSource = HrComponentKinds.All;
        _compKind.SelectedItem = HrComponentKinds.Earning;
        _compCalc.ItemsSource = HrCalcTypes.All;
        _compCalc.SelectedItem = HrCalcTypes.Fixed;
        _lineCalc.ItemsSource = HrCalcTypes.All;
        _lineCalc.SelectedItem = HrCalcTypes.Fixed;
        _lineComponent.DisplayMemberPath = nameof(HrSalaryComponent.DisplayName);
        _assignDesignation.DisplayMemberPath = nameof(HrDesignation.DisplayName);
        _assignStructure.DisplayMemberPath = nameof(HrSalaryStructure.DisplayName);
        _assignFrom.SelectedDate = DateTime.Today;
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
            (_compSaveButton, "Common.Save"), (_compDeleteButton, "Common.Delete"),
            (_lineAddButton, "Hr.AddLine"), (_lineRemoveButton, "Hr.RemoveLine"),
            (_structSaveButton, "Common.Save"), (_structDeleteButton, "Common.Delete"),
            (_assignButton, "Hr.Assign"), (_deactivateButton, "Hr.Deactivate"),
            (_refreshButton, "Common.Refresh"), (_closeButton, "Common.Close")
        })
            HrWinUtil.StyleButton(button, key);
        _compSaveButton.Click += async (_, _) => await SaveComponentAsync();
        _compDeleteButton.Click += async (_, _) => await DeleteComponentAsync();
        _lineAddButton.Click += (_, _) => AddLine();
        _lineRemoveButton.Click += (_, _) => RemoveLine();
        _structSaveButton.Click += async (_, _) => await SaveStructureAsync();
        _structDeleteButton.Click += async (_, _) => await DeleteStructureAsync();
        _assignButton.Click += async (_, _) => await AssignDesignationAsync();
        _deactivateButton.Click += async (_, _) => await DeactivateAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(HrWinUtil.BottomBar(_refreshButton, _closeButton));

        var sections = new Grid();
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.3, GridUnitType.Star) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.8, GridUnitType.Star) });

        // ---- Components
        var compPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _componentsHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_componentsHeader, Dock.Top);
        compPanel.Children.Add(_componentsHeader);
        var compBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _compCode.MinWidth = 90; _compName.MinWidth = 150; _compName.Margin = new Thickness(8, 0, 0, 0);
        _compKind.Margin = new Thickness(8, 0, 0, 0); _compCalc.Margin = new Thickness(8, 0, 0, 0);
        _compAmount.MinWidth = 70; _compAmount.Margin = new Thickness(8, 0, 0, 0);
        _compPercent.MinWidth = 60; _compPercent.Margin = new Thickness(8, 0, 0, 0);
        compBar.Children.Add(_compCode); compBar.Children.Add(_compName); compBar.Children.Add(_compKind);
        compBar.Children.Add(_compCalc); compBar.Children.Add(_compAmount); compBar.Children.Add(_compPercent);
        compBar.Children.Add(_compSaveButton); compBar.Children.Add(_compDeleteButton);
        DockPanel.SetDock(compBar, Dock.Bottom);
        compPanel.Children.Add(compBar);
        HrWinUtil.ConfigureGrid(_componentsGrid);
        _componentsGrid.Columns.Add(TextCol("Hr.Code", nameof(HrSalaryComponent.Code), 100));
        _componentsGrid.Columns.Add(TextCol("Hr.Col.Name", nameof(HrSalaryComponent.NameEn), 200));
        _componentsGrid.Columns.Add(TextCol("Hr.Col.Kind", nameof(HrSalaryComponent.Kind), 100));
        _componentsGrid.Columns.Add(TextCol("Hr.CalcType", nameof(HrSalaryComponent.DefaultCalcType), 110));
        _componentsGrid.SelectionChanged += (_, _) =>
        {
            if (_componentsGrid.SelectedItem is not HrSalaryComponent c) return;
            _selectedComponentId = c.Id;
            _compCode.Text = c.Code; _compName.Text = c.NameEn;
            _compKind.SelectedItem = c.Kind; _compCalc.SelectedItem = c.DefaultCalcType;
            _compAmount.Text = c.DefaultAmount.ToString(CultureInfo.InvariantCulture);
            _compPercent.Text = c.DefaultPercent.ToString(CultureInfo.InvariantCulture);
        };
        compPanel.Children.Add(_componentsGrid);
        Grid.SetRow(compPanel, 0);
        sections.Children.Add(compPanel);

        // ---- Structures + lines
        var structPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _structuresHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_structuresHeader, Dock.Top);
        structPanel.Children.Add(_structuresHeader);
        var structBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _structCode.MinWidth = 90; _structName.MinWidth = 150; _structName.Margin = new Thickness(8, 0, 0, 0);
        structBar.Children.Add(_structCode); structBar.Children.Add(_structName);
        structBar.Children.Add(_structSaveButton); structBar.Children.Add(_structDeleteButton);
        DockPanel.SetDock(structBar, Dock.Bottom);
        structPanel.Children.Add(structBar);
        var lineBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _lineComponent.MinWidth = 180;
        _lineCalc.Margin = new Thickness(8, 0, 0, 0);
        _lineAmount.MinWidth = 70; _lineAmount.Margin = new Thickness(8, 0, 0, 0);
        _linePercent.MinWidth = 60; _linePercent.Margin = new Thickness(8, 0, 0, 0);
        _lineAccount.MinWidth = 70; _lineAccount.Margin = new Thickness(8, 0, 0, 0);
        lineBar.Children.Add(_lineComponent); lineBar.Children.Add(_lineCalc); lineBar.Children.Add(_lineAmount);
        lineBar.Children.Add(_linePercent); lineBar.Children.Add(_lineAccount);
        lineBar.Children.Add(_lineAddButton); lineBar.Children.Add(_lineRemoveButton);
        DockPanel.SetDock(lineBar, Dock.Bottom);
        structPanel.Children.Add(lineBar);
        var structContent = new Grid();
        structContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        structContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        HrWinUtil.ConfigureGrid(_structuresGrid);
        _structuresGrid.Columns.Add(TextCol("Hr.Code", nameof(HrSalaryStructure.Code), 90));
        _structuresGrid.Columns.Add(TextCol("Hr.Col.Name", nameof(HrSalaryStructure.NameEn), 160));
        _structuresGrid.SelectionChanged += (_, _) => OnStructureSelected();
        Grid.SetColumn(_structuresGrid, 0);
        structContent.Children.Add(_structuresGrid);
        HrWinUtil.ConfigureGrid(_linesGrid);
        _linesGrid.Columns.Add(TextCol("Hr.Col.Component", nameof(HrStructureLine.ComponentCode), 110));
        _linesGrid.Columns.Add(TextCol("Hr.Col.Kind", nameof(HrStructureLine.Kind), 90));
        _linesGrid.Columns.Add(TextCol("Hr.CalcType", nameof(HrStructureLine.CalcType), 110));
        _linesGrid.Columns.Add(TextCol("Hr.Col.Amount", nameof(HrStructureLine.Amount), 80));
        _linesGrid.Columns.Add(TextCol("Hr.Percent", nameof(HrStructureLine.Percent), 70));
        _linesGrid.Columns.Add(TextCol("Hr.Account", nameof(HrStructureLine.AccountCode), 80));
        Grid.SetColumn(_linesGrid, 1);
        structContent.Children.Add(_linesGrid);
        structPanel.Children.Add(structContent);
        Grid.SetRow(structPanel, 1);
        sections.Children.Add(structPanel);

        // ---- Designation assignments
        var assignPanel = new DockPanel();
        _assignmentsHeader.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_assignmentsHeader, Dock.Top);
        assignPanel.Children.Add(_assignmentsHeader);
        var assignBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _assignDesignation.MinWidth = 170; _assignStructure.MinWidth = 170; _assignStructure.Margin = new Thickness(8, 0, 0, 0);
        _assignBase.MinWidth = 80; _assignBase.Margin = new Thickness(8, 0, 0, 0);
        _assignFrom.Margin = new Thickness(8, 0, 0, 0);
        assignBar.Children.Add(_assignDesignation); assignBar.Children.Add(_assignStructure);
        assignBar.Children.Add(_assignBase); assignBar.Children.Add(_assignFrom);
        assignBar.Children.Add(_assignButton); assignBar.Children.Add(_deactivateButton);
        DockPanel.SetDock(assignBar, Dock.Bottom);
        assignPanel.Children.Add(assignBar);
        HrWinUtil.ConfigureGrid(_assignmentsGrid);
        _assignmentsGrid.Columns.Add(TextCol("Hr.Col.Designation", "DesignationId", 110));
        _assignmentsGrid.Columns.Add(TextCol("Hr.Structures", nameof(HrSalaryAssignment.StructureCode), 110));
        _assignmentsGrid.Columns.Add(TextCol("Hr.BaseSalary", nameof(HrSalaryAssignment.BaseSalary), 90));
        _assignmentsGrid.Columns.Add(TextCol("Hr.FromDate", nameof(HrSalaryAssignment.FromDate), 95));
        _assignmentsGrid.Columns.Add(TextCol("Hr.Col.Status", nameof(HrSalaryAssignment.IsActive), 70));
        _assignmentsGrid.SelectionChanged += (_, _) =>
        {
            if (_assignmentsGrid.SelectedItem is HrSalaryAssignment a) _selectedAssignmentId = a.Id;
        };
        assignPanel.Children.Add(_assignmentsGrid);
        Grid.SetRow(assignPanel, 2);
        sections.Children.Add(assignPanel);

        root.Children.Add(sections);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Hr.StructuresTitle");
        _componentsHeader.Text = T("Hr.Components");
        _structuresHeader.Text = T("Hr.Structures");
        _assignmentsHeader.Text = T("Hr.Assignments");
        foreach (var (column, key) in _headers) column.Header = T(key);
        foreach (var b in new[] { _compSaveButton, _compDeleteButton, _lineAddButton, _lineRemoveButton, _structSaveButton, _structDeleteButton, _assignButton, _deactivateButton, _refreshButton, _closeButton })
            if (b.Tag is string key) b.Content = T(key);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Hr.ServiceUnavailable"); return; }
            var components = await _service.GetComponentsAsync(CurrentCompanyCode);
            _components.Clear(); _components.AddRange(components);
            _componentsGrid.ItemsSource = null; _componentsGrid.ItemsSource = _components;
            _lineComponent.ItemsSource = _components;
            var structures = await _service.GetStructuresAsync(CurrentCompanyCode);
            _structures.Clear(); _structures.AddRange(structures);
            _structuresGrid.ItemsSource = null; _structuresGrid.ItemsSource = _structures;
            _assignStructure.ItemsSource = _structures;
            var designations = await _service.GetDesignationsAsync(CurrentCompanyCode);
            _designations.Clear(); _designations.AddRange(designations);
            _assignDesignation.ItemsSource = _designations;
            var assignments = await _service.GetAssignmentsAsync(CurrentCompanyCode);
            _assignmentsGrid.ItemsSource = null;
            _assignmentsGrid.ItemsSource = assignments.Where(a => a.DesignationId is not null).ToList();
            _statusBar.Text = Tf("Hr.StatusLine", _structures.Count);
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.RefreshAsync"); }
    }

    private void OnStructureSelected()
    {
        if (_structuresGrid.SelectedItem is not HrSalaryStructure s) return;
        _selectedStructureId = s.Id;
        _structCode.Text = s.Code; _structName.Text = s.NameEn;
        _editLines.Clear();
        _editLines.AddRange(s.Lines);
        _linesGrid.ItemsSource = null; _linesGrid.ItemsSource = _editLines;
    }

    private async Task SaveComponentAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var c = _selectedComponentId is Guid id ? _components.FirstOrDefault(x => x.Id == id) ?? new HrSalaryComponent() : new HrSalaryComponent();
            c.Code = _compCode.Text.Trim(); c.NameEn = _compName.Text.Trim();
            c.Kind = _compKind.SelectedItem as string ?? HrComponentKinds.Earning;
            c.DefaultCalcType = _compCalc.SelectedItem as string ?? HrCalcTypes.Fixed;
            c.DefaultAmount = ParseDecimal(_compAmount.Text);
            c.DefaultPercent = ParseDecimal(_compPercent.Text);
            await _service.SaveComponentAsync(CurrentCompanyCode, c, CurrentUserId);
            _selectedComponentId = c.Id;
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.SaveComponentAsync"); }
    }

    private async Task DeleteComponentAsync()
    {
        try
        {
            if (_service is null || _selectedComponentId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.DeleteComponentAsync(CurrentCompanyCode, id, CurrentUserId);
            _selectedComponentId = null;
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.DeleteComponentAsync"); }
    }

    private void AddLine()
    {
        try
        {
            if (_lineComponent.SelectedItem is not HrSalaryComponent component) throw new InvalidOperationException(T("Hr.SelectRecord"));
            _editLines.Add(new HrStructureLine
            {
                ComponentId = component.Id, ComponentCode = component.Code,
                ComponentName = component.NameEn, Kind = component.Kind,
                CalcType = _lineCalc.SelectedItem as string ?? component.DefaultCalcType,
                Amount = ParseDecimal(_lineAmount.Text), Percent = ParseDecimal(_linePercent.Text),
                AccountCode = HrWinUtil.NullIfBlank(_lineAccount.Text), IsActive = true
            });
            _linesGrid.ItemsSource = null; _linesGrid.ItemsSource = _editLines;
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.AddLine"); }
    }

    private void RemoveLine()
    {
        if (_linesGrid.SelectedItem is HrStructureLine line)
        {
            _editLines.Remove(line);
            _linesGrid.ItemsSource = null; _linesGrid.ItemsSource = _editLines;
        }
    }

    private async Task SaveStructureAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            var s = _selectedStructureId is Guid id ? _structures.FirstOrDefault(x => x.Id == id) ?? new HrSalaryStructure() : new HrSalaryStructure();
            s.Code = _structCode.Text.Trim(); s.NameEn = _structName.Text.Trim();
            s.Lines = _editLines.ToList();
            await _service.SaveStructureAsync(CurrentCompanyCode, s, CurrentUserId);
            _selectedStructureId = s.Id;
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.SaveStructureAsync"); }
    }

    private async Task DeleteStructureAsync()
    {
        try
        {
            if (_service is null || _selectedStructureId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.DeleteStructureAsync(CurrentCompanyCode, id, CurrentUserId);
            _selectedStructureId = null; _editLines.Clear();
            _linesGrid.ItemsSource = null;
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.DeleteStructureAsync"); }
    }

    private async Task AssignDesignationAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Hr.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            if (_assignDesignation.SelectedItem is not HrDesignation designation) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (_assignStructure.SelectedItem is not HrSalaryStructure structure) throw new InvalidOperationException(T("Hr.SelectRecord"));
            var assignment = new HrSalaryAssignment
            {
                DesignationId = designation.Id, StructureId = structure.Id,
                BaseSalary = ParseDecimal(_assignBase.Text), CurrencyCode = "SAR",
                FromDate = _assignFrom.SelectedDate is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
                IsActive = true
            };
            await _service.SaveAssignmentAsync(CurrentCompanyCode, assignment, CurrentUserId);
            _statusBar.Text = T("Hr.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.AssignDesignationAsync"); }
    }

    private async Task DeactivateAsync()
    {
        try
        {
            if (_service is null || _selectedAssignmentId is not Guid id) throw new InvalidOperationException(T("Hr.SelectRecord"));
            if (!AppHost.Permissions.Has(PermCat.Hr.PayrollEdit)) throw new InvalidOperationException(T("Hr.PermissionDenied"));
            await _service.DeactivateAssignmentAsync(CurrentCompanyCode, id, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex) { HrWinUtil.ReportError(this, ex, "HrStructureWin.DeactivateAsync"); }
    }

    private static decimal ParseDecimal(string text)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn { Binding = new Binding(path), Width = new DataGridLength(width) };
        _headers.Add((column, key));
        return column;
    }
}
