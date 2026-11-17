using System;
using System.Collections.Generic;
using System.Globalization;
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

/// <summary>
/// Quality definitions window (Phase B 056, design section 5's
/// QcParamWin + QcTemplateWin combined): inspection parameters
/// (measurable characteristics), templates (parameters + acceptance
/// criteria per line), and the material assignment that wires a
/// material to its incoming / outgoing templates and its
/// quality_check_required / qc_outgoing_required flags — the same
/// columns the material card's Quality checkbox writes. All writes go
/// through <see cref="QualityService"/> (validated, transactional;
/// template lines are replaced wholesale with the header).
/// </summary>
public sealed class QcTemplateWin : BaseWin
{
    protected override string? ScheduleTintName => "Peridot";

    private readonly QualityService? _service;
    private readonly List<QualityParameter> _parameters = new();
    private readonly List<QualityTemplate> _templates = new();
    private readonly List<QualityTemplateLine> _templateLines = new();
    private readonly List<QualityMaterialOption> _materials = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _paramGrid = new();
    private readonly DataGrid _templateGrid = new();
    private readonly DataGrid _lineGrid = new();
    private readonly DataGrid _materialGrid = new();
    private readonly TextBox _paramCode = new();
    private readonly TextBox _paramNameEn = new();
    private readonly TextBox _paramNameAr = new();
    private readonly ComboBox _paramType = new();
    private readonly TextBox _paramUnit = new();
    private readonly TextBox _paramGroup = new();
    private readonly CheckBox _paramActive = new();
    private readonly TextBox _templateCode = new();
    private readonly TextBox _templateNameEn = new();
    private readonly TextBox _templateNameAr = new();
    private readonly ComboBox _templateType = new();
    private readonly CheckBox _templateActive = new();
    private readonly ComboBox _lineParam = new();
    private readonly TextBox _lineMin = new();
    private readonly TextBox _lineMax = new();
    private readonly TextBox _lineAccepted = new();
    private readonly CheckBox _lineMandatory = new();
    private readonly CheckBox _assignQc = new();
    private readonly CheckBox _assignOutgoing = new();
    private readonly ComboBox _assignTemplateIn = new();
    private readonly ComboBox _assignTemplateOut = new();
    private readonly TextBlock _paramsHeader = new();
    private readonly TextBlock _templatesHeader = new();
    private readonly TextBlock _assignHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _saveParamButton = new();
    private readonly Button _deleteParamButton = new();
    private readonly Button _saveTemplateButton = new();
    private readonly Button _deleteTemplateButton = new();
    private readonly Button _addLineButton = new();
    private readonly Button _removeLineButton = new();
    private readonly Button _saveAssignButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedParamId;
    private Guid? _selectedTemplateId;
    private Guid? _selectedLineId;
    private long? _selectedMaterialId;

    public QcTemplateWin()
    {
        _service = AppHost.Quality;
        Width = 1180;
        Height = 740;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Quality.TemplateView))
        {
            MessageBox.Show(this, T("Quality.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var statusBorder = new Border
        {
            Child = _statusBar,
            Padding = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_saveParamButton, "Quality.SaveParameter");
        StyleButton(_deleteParamButton, "Quality.DeleteParameter");
        StyleButton(_saveTemplateButton, "Quality.SaveTemplate");
        StyleButton(_deleteTemplateButton, "Quality.DeleteTemplate");
        StyleButton(_addLineButton, "Quality.AddLine");
        StyleButton(_removeLineButton, "Quality.RemoveLine");
        StyleButton(_saveAssignButton, "Quality.SaveAssignment");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _saveParamButton.Click += async (_, _) => await SaveParameterAsync();
        _deleteParamButton.Click += async (_, _) => await DeleteParameterAsync();
        _saveTemplateButton.Click += async (_, _) => await SaveTemplateAsync();
        _deleteTemplateButton.Click += async (_, _) => await DeleteTemplateAsync();
        _addLineButton.Click += (_, _) => AddLine();
        _removeLineButton.Click += (_, _) => RemoveLine();
        _saveAssignButton.Click += async (_, _) => await SaveAssignmentAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _saveParamButton, _deleteParamButton, _saveTemplateButton, _deleteTemplateButton, _addLineButton, _removeLineButton, _saveAssignButton, _refreshButton, _closeButton })
            bar.Children.Add(b);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

        // ---- Parameters (left top) -------------------------------------
        var paramPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _paramsHeader.FontWeight = FontWeights.SemiBold;
        _paramsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_paramsHeader, Dock.Top);
        paramPanel.Children.Add(_paramsHeader);
        ConfigureGrid(_paramGrid);
        _paramGrid.Columns.Add(TextCol("Quality.Col.Code", nameof(QualityParameter.Code), 90));
        _paramGrid.Columns.Add(TextCol("Quality.Col.Name", nameof(QualityParameter.NameEn), 150));
        _paramGrid.Columns.Add(TextCol("Quality.Col.ParamType", nameof(QualityParameter.ParamType), 80));
        _paramGrid.Columns.Add(TextCol("Quality.Col.Unit", nameof(QualityParameter.Unit), 60));
        _paramGrid.SelectionChanged += (_, _) => OnParameterSelected();
        paramPanel.Children.Add(_paramGrid);
        Grid.SetRow(paramPanel, 0);
        Grid.SetColumn(paramPanel, 0);
        content.Children.Add(paramPanel);

        var paramForm = CreateFormGrid(1);
        AddFormField(_labels, paramForm, 0, 0, "Quality.Col.Code", _paramCode);
        AddFormField(_labels, paramForm, 1, 0, "Quality.NameEn", _paramNameEn);
        AddFormField(_labels, paramForm, 2, 0, "Quality.NameAr", _paramNameAr);
        AddFormField(_labels, paramForm, 3, 0, "Quality.Col.ParamType", _paramType);
        AddFormField(_labels, paramForm, 4, 0, "Quality.Col.Unit", _paramUnit);
        AddFormField(_labels, paramForm, 5, 0, "Quality.Col.Group", _paramGroup);
        AddFormElement(paramForm, 6, 0, _paramActive);
        _paramActive.Content = T("Quality.Active");
        _paramType.ItemsSource = QualityParamTypes.All;
        _paramType.SelectedItem = QualityParamTypes.Numeric;
        _paramActive.IsChecked = true;
        var paramScroll = new ScrollViewer { Content = paramForm, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(paramScroll, 0);
        Grid.SetColumn(paramScroll, 1);
        content.Children.Add(paramScroll);

        // ---- Templates + lines (left bottom) -----------------------------
        var templatePanel = new Grid { Margin = new Thickness(0, 10, 12, 0) };
        templatePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        templatePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        templatePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _templatesHeader.FontWeight = FontWeights.SemiBold;
        _templatesHeader.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(_templatesHeader, 0);
        templatePanel.Children.Add(_templatesHeader);
        ConfigureGrid(_templateGrid);
        _templateGrid.Columns.Add(TextCol("Quality.Col.Code", nameof(QualityTemplate.Code), 90));
        _templateGrid.Columns.Add(TextCol("Quality.Col.Name", nameof(QualityTemplate.NameEn), 150));
        _templateGrid.Columns.Add(TextCol("Quality.Col.Type", nameof(QualityTemplate.InspectionType), 90));
        _templateGrid.SelectionChanged += (_, _) => OnTemplateSelected();
        Grid.SetRow(_templateGrid, 1);
        templatePanel.Children.Add(_templateGrid);
        ConfigureGrid(_lineGrid);
        _lineGrid.Columns.Add(TextCol("Quality.Col.Parameter", nameof(QualityTemplateLine.ParamCode), 100));
        _lineGrid.Columns.Add(TextCol("Quality.Col.Min", nameof(QualityTemplateLine.MinValue), 70, numeric: true));
        _lineGrid.Columns.Add(TextCol("Quality.Col.Max", nameof(QualityTemplateLine.MaxValue), 70, numeric: true));
        _lineGrid.Columns.Add(TextCol("Quality.Col.AcceptedValue", nameof(QualityTemplateLine.AcceptedValue), 100));
        _lineGrid.SelectionChanged += (_, _) => OnLineSelected();
        Grid.SetRow(_lineGrid, 2);
        templatePanel.Children.Add(_lineGrid);
        Grid.SetRow(templatePanel, 1);
        Grid.SetColumn(templatePanel, 0);
        content.Children.Add(templatePanel);

        // ---- Template editor + assignment (right bottom) ------------------
        var rightPanel = new StackPanel();
        var templateForm = CreateFormGrid(1);
        AddFormField(_labels, templateForm, 0, 0, "Quality.Col.Code", _templateCode);
        AddFormField(_labels, templateForm, 1, 0, "Quality.NameEn", _templateNameEn);
        AddFormField(_labels, templateForm, 2, 0, "Quality.NameAr", _templateNameAr);
        AddFormField(_labels, templateForm, 3, 0, "Quality.Col.Type", _templateType);
        AddFormElement(templateForm, 4, 0, _templateActive);
        _templateActive.Content = T("Quality.Active");
        _templateActive.IsChecked = true;
        _templateType.ItemsSource = QualityInspectionTypes.All;
        _templateType.SelectedItem = QualityInspectionTypes.Incoming;
        rightPanel.Children.Add(templateForm);
        var lineForm = CreateFormGrid(1);
        AddFormField(_labels, lineForm, 0, 0, "Quality.Col.Parameter", _lineParam);
        AddFormField(_labels, lineForm, 1, 0, "Quality.Col.Min", _lineMin);
        AddFormField(_labels, lineForm, 2, 0, "Quality.Col.Max", _lineMax);
        AddFormField(_labels, lineForm, 3, 0, "Quality.Col.AcceptedValue", _lineAccepted);
        AddFormElement(lineForm, 4, 0, _lineMandatory);
        _lineMandatory.Content = T("Quality.Mandatory");
        _lineMandatory.IsChecked = true;
        var lineHeader = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
        _labels.Add((lineHeader, "Quality.Lines"));
        rightPanel.Children.Add(lineHeader);
        rightPanel.Children.Add(lineForm);
        _assignHeader.FontWeight = FontWeights.SemiBold;
        _assignHeader.Margin = new Thickness(0, 10, 0, 6);
        rightPanel.Children.Add(_assignHeader);
        ConfigureGrid(_materialGrid);
        _materialGrid.Columns.Add(TextCol("Quality.Col.Code", nameof(QualityMaterialOption.Code), 80));
        _materialGrid.Columns.Add(TextCol("Quality.Col.Name", nameof(QualityMaterialOption.Name), 140));
        _materialGrid.MaxHeight = 140;
        _materialGrid.SelectionChanged += (_, _) => OnMaterialSelected();
        rightPanel.Children.Add(_materialGrid);
        var assignForm = CreateFormGrid(1);
        AddFormElement(assignForm, 0, 0, _assignQc);
        AddFormElement(assignForm, 1, 0, _assignOutgoing);
        AddFormField(_labels, assignForm, 2, 0, "Quality.TemplateIn", _assignTemplateIn);
        AddFormField(_labels, assignForm, 3, 0, "Quality.TemplateOut", _assignTemplateOut);
        _assignQc.Content = T("Quality.CheckRequired");
        _assignOutgoing.Content = T("Quality.OutgoingRequired");
        rightPanel.Children.Add(assignForm);
        var rightScroll = new ScrollViewer { Content = rightPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(rightScroll, 1);
        Grid.SetColumn(rightScroll, 1);
        content.Children.Add(rightScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Quality.TemplatesTitle");
        _paramsHeader.Text = T("Quality.Parameters");
        _templatesHeader.Text = T("Quality.Templates");
        _assignHeader.Text = T("Quality.Assignment");
        _paramActive.Content = T("Quality.Active");
        _templateActive.Content = T("Quality.Active");
        _lineMandatory.Content = T("Quality.Mandatory");
        _assignQc.Content = T("Quality.CheckRequired");
        _assignOutgoing.Content = T("Quality.OutgoingRequired");
        foreach (var (label, key) in _labels)
            if (!string.IsNullOrEmpty(key))
                label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _saveParamButton, _deleteParamButton, _saveTemplateButton, _deleteTemplateButton, _addLineButton, _removeLineButton, _saveAssignButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    // ------------------------------------------------------------------
    // Data flow
    // ------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Quality.ServiceUnavailable");
                return;
            }
            var company = CurrentCompanyCode;
            _parameters.Clear();
            _parameters.AddRange(await _service.GetParametersAsync(company));
            _paramGrid.ItemsSource = null;
            _paramGrid.ItemsSource = _parameters;
            _lineParam.ItemsSource = _parameters.Select(p => $"{p.Code} — {p.NameEn}").ToList();

            _templates.Clear();
            _templates.AddRange(await _service.GetTemplatesAsync(company));
            _templateGrid.ItemsSource = null;
            _templateGrid.ItemsSource = _templates;
            var templateLabels = _templates.Select(t => $"{t.Code} — {t.NameEn}").ToList();
            _assignTemplateIn.ItemsSource = templateLabels;
            _assignTemplateOut.ItemsSource = templateLabels;

            _materials.Clear();
            _materials.AddRange(await _service.GetMaterialOptionsAsync(company));
            _materialGrid.ItemsSource = null;
            _materialGrid.ItemsSource = _materials;
            _statusBar.Text = Tf("Quality.DefinitionsStatus", _parameters.Count, _templates.Count, _materials.Count);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcTemplateWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnParameterSelected()
    {
        if (_paramGrid.SelectedItem is not QualityParameter p)
            return;
        _selectedParamId = p.Id;
        _paramCode.Text = p.Code;
        _paramNameEn.Text = p.NameEn;
        _paramNameAr.Text = p.NameAr;
        _paramType.SelectedItem = p.ParamType;
        _paramUnit.Text = p.Unit ?? string.Empty;
        _paramGroup.Text = p.GroupName ?? string.Empty;
        _paramActive.IsChecked = p.IsActive;
    }

    private async Task SaveParameterAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Quality.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Quality.ParamEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            await _service.SaveParameterAsync(CurrentCompanyCode, new QualityParameter
            {
                Id = _selectedParamId ?? Guid.NewGuid(),
                Code = _paramCode.Text.Trim(),
                NameEn = _paramNameEn.Text.Trim(),
                NameAr = _paramNameAr.Text.Trim(),
                ParamType = _paramType.SelectedItem as string ?? QualityParamTypes.Numeric,
                Unit = NullIfBlank(_paramUnit.Text),
                GroupName = NullIfBlank(_paramGroup.Text),
                IsActive = _paramActive.IsChecked == true
            }, CurrentUserId);
            _selectedParamId = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcTemplateWin.SaveParameterAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteParameterAsync()
    {
        try
        {
            if (_service is null || _selectedParamId is not Guid id)
                return;
            if (!AppHost.Permissions.Has(PermCat.Quality.ParamEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteParameterAsync(CurrentCompanyCode, id, CurrentUserId);
            _selectedParamId = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcTemplateWin.DeleteParameterAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTemplateSelected()
    {
        if (_templateGrid.SelectedItem is not QualityTemplate t)
            return;
        _selectedTemplateId = t.Id;
        _templateCode.Text = t.Code;
        _templateNameEn.Text = t.NameEn;
        _templateNameAr.Text = t.NameAr;
        _templateType.SelectedItem = t.InspectionType;
        _templateActive.IsChecked = t.IsActive;
        _templateLines.Clear();
        _templateLines.AddRange(t.Lines.Select(CloneLine));
        _lineGrid.ItemsSource = null;
        _lineGrid.ItemsSource = _templateLines;
    }

    private void OnLineSelected()
    {
        if (_lineGrid.SelectedItem is not QualityTemplateLine line)
            return;
        _selectedLineId = line.Id;
        _lineParam.SelectedIndex = _parameters.FindIndex(p => p.Id == line.ParamId);
        _lineMin.Text = line.MinValue?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
        _lineMax.Text = line.MaxValue?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
        _lineAccepted.Text = line.AcceptedValue ?? string.Empty;
        _lineMandatory.IsChecked = line.IsMandatory;
    }

    private void AddLine()
    {
        try
        {
            if (_lineParam.SelectedIndex < 0 || _lineParam.SelectedIndex >= _parameters.Count)
                throw new InvalidOperationException(T("Quality.SelectParameter"));
            var parameter = _parameters[_lineParam.SelectedIndex];
            var existing = _selectedLineId is Guid id ? _templateLines.FirstOrDefault(l => l.Id == id) : null;
            var line = existing ?? new QualityTemplateLine { Id = Guid.NewGuid(), Sort = _templateLines.Count };
            line.ParamId = parameter.Id;
            line.ParamCode = parameter.Code;
            line.ParamName = parameter.NameEn;
            line.ParamType = parameter.ParamType;
            line.MinValue = ParseDecimalOrNull(_lineMin.Text);
            line.MaxValue = ParseDecimalOrNull(_lineMax.Text);
            line.AcceptedValue = NullIfBlank(_lineAccepted.Text);
            line.IsMandatory = _lineMandatory.IsChecked == true;
            if (existing is null)
            {
                _templateLines.Add(line);
                _selectedLineId = line.Id;
            }
            _lineGrid.ItemsSource = null;
            _lineGrid.ItemsSource = _templateLines;
            _lineGrid.SelectedItem = line;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcTemplateWin.AddLine");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveLine()
    {
        if (_selectedLineId is not Guid id)
            return;
        _templateLines.RemoveAll(l => l.Id == id);
        _selectedLineId = null;
        _lineGrid.ItemsSource = null;
        _lineGrid.ItemsSource = _templateLines;
    }

    private async Task SaveTemplateAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Quality.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Quality.TemplateEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            await _service.SaveTemplateAsync(CurrentCompanyCode, new QualityTemplate
            {
                Id = _selectedTemplateId ?? Guid.NewGuid(),
                Code = _templateCode.Text.Trim(),
                NameEn = _templateNameEn.Text.Trim(),
                NameAr = _templateNameAr.Text.Trim(),
                InspectionType = _templateType.SelectedItem as string ?? QualityInspectionTypes.Incoming,
                IsActive = _templateActive.IsChecked == true,
                Lines = _templateLines.Select(CloneLine).ToList()
            }, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcTemplateWin.SaveTemplateAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteTemplateAsync()
    {
        try
        {
            if (_service is null || _selectedTemplateId is not Guid id)
                return;
            if (!AppHost.Permissions.Has(PermCat.Quality.TemplateEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteTemplateAsync(CurrentCompanyCode, id, CurrentUserId);
            _selectedTemplateId = null;
            _templateLines.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcTemplateWin.DeleteTemplateAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnMaterialSelected()
    {
        if (_materialGrid.SelectedItem is not QualityMaterialOption m)
            return;
        _selectedMaterialId = m.MaterialId;
        _assignQc.IsChecked = m.QualityCheckRequired;
        _assignOutgoing.IsChecked = m.OutgoingRequired;
        _assignTemplateIn.SelectedIndex = m.TemplateInId is Guid tin ? _templates.FindIndex(t => t.Id == tin) : -1;
        _assignTemplateOut.SelectedIndex = m.TemplateOutId is Guid tout ? _templates.FindIndex(t => t.Id == tout) : -1;
    }

    private async Task SaveAssignmentAsync()
    {
        try
        {
            if (_service is null || _selectedMaterialId is not long materialId)
                throw new InvalidOperationException(T("Quality.SelectMaterial"));
            if (!AppHost.Permissions.Has(PermCat.Quality.TemplateEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            Guid? tin = _assignTemplateIn.SelectedIndex >= 0 && _assignTemplateIn.SelectedIndex < _templates.Count
                ? _templates[_assignTemplateIn.SelectedIndex].Id : null;
            Guid? tout = _assignTemplateOut.SelectedIndex >= 0 && _assignTemplateOut.SelectedIndex < _templates.Count
                ? _templates[_assignTemplateOut.SelectedIndex].Id : null;
            await _service.SetMaterialQualityAsync(CurrentCompanyCode, materialId,
                _assignQc.IsChecked == true, _assignOutgoing.IsChecked == true, tin, tout, CurrentUserId);
            _statusBar.Text = T("Quality.AssignmentSaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcTemplateWin.SaveAssignmentAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Helpers (BudgetWin pattern)
    // ------------------------------------------------------------------

    private static QualityTemplateLine CloneLine(QualityTemplateLine line) => new()
    {
        Id = line.Id,
        ParamId = line.ParamId,
        ParamCode = line.ParamCode,
        ParamName = line.ParamName,
        ParamType = line.ParamType,
        MinValue = line.MinValue,
        MaxValue = line.MaxValue,
        AcceptedValue = line.AcceptedValue,
        IsMandatory = line.IsMandatory,
        Sort = line.Sort
    };

    private static void ConfigureGrid(DataGrid grid)
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

    private DataGridTextColumn TextCol(string key, string path, double width, bool numeric = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        if (numeric)
            column.ElementStyle = RightAlignStyle;
        _headers.Add((column, key));
        return column;
    }

    private static readonly Style RightAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) }
    };

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? ParseDecimalOrNull(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }
}
