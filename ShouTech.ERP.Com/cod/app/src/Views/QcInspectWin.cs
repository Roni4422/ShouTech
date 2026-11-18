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
using ShouTech.Infrastructure;

namespace ShouTech.App.Views;

/// <summary>
/// Quality inspections window (Phase B 056, design section 5's
/// QcInspectWin + QcPendingWin combined): the pending-inspection
/// worklist (every Draft inspection the gate created at receipt /
/// production time, plus manually created outgoing inspections), the
/// inspection entry surface (readings per template characteristic with
/// live auto pass/fail, inspector override kept visible), and the
/// completion actions that release accepted stock from quarantine and
/// disposition rejected stock through the valuation engine
/// (QualityGate). The header carries the company's QC_GATE_MODE with
/// its setter. All writes go through <see cref="QualityService"/>.
/// </summary>
public sealed class QcInspectWin : BaseWin
{
    protected override string? ScheduleTintName => "Jade";

    private readonly QualityService? _service;
    private readonly List<QualityInspection> _inspections = new();
    private readonly List<QualityInspectionLine> _lines = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private List<QualityMaterialOption> _materials = new();
    private QualityInspection? _current;
    private QualityInspectionLine? _currentLine;

    private readonly DataGrid _grid = new();
    private readonly DataGrid _linesGrid = new();
    private readonly CheckBox _pendingOnly = new();
    private readonly ComboBox _gateMode = new();
    private readonly TextBlock _inspectionsHeader = new();
    private readonly TextBlock _linesHeader = new();
    private readonly TextBlock _detailHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _detailInfo = new();
    private readonly TextBox _reading1 = new();
    private readonly TextBox _reading2 = new();
    private readonly TextBox _reading3 = new();
    private readonly TextBox _readingText = new();
    private readonly ComboBox _lineResult = new();
    private readonly TextBox _lineNotes = new();
    private readonly TextBlock _autoResult = new();
    private readonly TextBox _acceptedQty = new();
    private readonly TextBox _rejectedQty = new();
    private readonly ComboBox _disposition = new();
    private readonly TextBox _completeNotes = new();
    private readonly ComboBox _newMaterial = new();
    private readonly TextBox _newDocNo = new();
    private readonly Button _saveButton = new();
    private readonly Button _completeButton = new();
    private readonly Button _newButton = new();
    private readonly Button _applyLineButton = new();
    private readonly Button _gateModeButton = new();
    private readonly Button _alertsButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public QcInspectWin()
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
        if (!AppHost.Permissions.Has(PermCat.Quality.InspectView))
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
        StyleButton(_saveButton, "Quality.SaveResults");
        StyleButton(_completeButton, "Quality.Complete");
        StyleButton(_newButton, "Quality.NewOutgoing");
        StyleButton(_applyLineButton, "Quality.ApplyLine");
        StyleButton(_gateModeButton, "Quality.SetGateMode");
        StyleButton(_alertsButton, "Quality.Alerts");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _saveButton.Click += async (_, _) => await SaveResultsAsync();
        _completeButton.Click += async (_, _) => await CompleteAsync();
        _newButton.Click += async (_, _) => await CreateOutgoingAsync();
        _applyLineButton.Click += (_, _) => ApplyLineEditor();
        _gateModeButton.Click += async (_, _) => await SetGateModeAsync();
        _alertsButton.Click += (_, _) => new QcAlertWin().Show();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _saveButton, _completeButton, _newButton, _applyLineButton, _gateModeButton, _alertsButton, _refreshButton, _closeButton })
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
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

        // ---- Worklist (left, top) ------------------------------------
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var listHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _inspectionsHeader.FontWeight = FontWeights.SemiBold;
        listHeader.Children.Add(_inspectionsHeader);
        _pendingOnly.Margin = new Thickness(16, 0, 0, 0);
        _pendingOnly.IsChecked = true;
        _pendingOnly.Checked += async (_, _) => await RefreshAsync();
        _pendingOnly.Unchecked += async (_, _) => await RefreshAsync();
        listHeader.Children.Add(_pendingOnly);
        DockPanel.SetDock(listHeader, Dock.Top);
        listPanel.Children.Add(listHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Quality.Col.DocNo", nameof(QualityInspection.RefDocNo), 110));
        _grid.Columns.Add(TextCol("Quality.Col.Type", nameof(QualityInspection.InspectionType), 80));
        _grid.Columns.Add(TextCol("Quality.Col.Material", nameof(QualityInspection.MaterialCode), 90));
        _grid.Columns.Add(TextCol("Quality.Col.Batch", nameof(QualityInspection.BatchNo), 90));
        _grid.Columns.Add(TextCol("Quality.Col.TotalQty", nameof(QualityInspection.TotalQty), 80, numeric: true));
        _grid.Columns.Add(TextCol("Quality.Col.DueDate", nameof(QualityInspection.DueDate), 90));
        _grid.Columns.Add(TextCol("Quality.Col.Status", nameof(QualityInspection.Status), 100));
        _grid.SelectionChanged += async (_, _) => await OnInspectionSelectedAsync();
        listPanel.Children.Add(_grid);
        Grid.SetRow(listPanel, 0);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        // ---- Detail + gate (right, top) -------------------------------
        var rightPanel = new StackPanel();
        _detailHeader.FontWeight = FontWeights.SemiBold;
        _detailHeader.Margin = new Thickness(0, 0, 0, 6);
        rightPanel.Children.Add(_detailHeader);
        _detailInfo.IsReadOnly = true;
        _detailInfo.TextWrapping = TextWrapping.Wrap;
        _detailInfo.MinHeight = 64;
        rightPanel.Children.Add(_detailInfo);
        var gateForm = CreateFormGrid(1);
        AddFormField(_labels, gateForm, 0, 0, "Quality.GateMode", _gateMode);
        _gateMode.ItemsSource = QualityGateModes.All;
        rightPanel.Children.Add(gateForm);
        var createHeader = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
        _labels.Add((createHeader, "Quality.NewOutgoing"));
        rightPanel.Children.Add(createHeader);
        var createForm = CreateFormGrid(1);
        AddFormField(_labels, createForm, 0, 0, "Quality.Col.Material", _newMaterial);
        AddFormField(_labels, createForm, 1, 0, "Quality.Col.DocNo", _newDocNo);
        rightPanel.Children.Add(createForm);
        var rightScroll = new ScrollViewer { Content = rightPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(rightScroll, 0);
        Grid.SetColumn(rightScroll, 1);
        content.Children.Add(rightScroll);

        // ---- Lines (left, bottom) --------------------------------------
        var linesPanel = new DockPanel { Margin = new Thickness(0, 10, 12, 0) };
        _linesHeader.FontWeight = FontWeights.SemiBold;
        _linesHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_linesHeader, Dock.Top);
        linesPanel.Children.Add(_linesHeader);
        ConfigureGrid(_linesGrid);
        _linesGrid.Columns.Add(TextCol("Quality.Col.Parameter", nameof(QualityInspectionLine.ParamCode), 110));
        _linesGrid.Columns.Add(TextCol("Quality.Col.ParamType", nameof(QualityInspectionLine.ParamType), 80));
        _linesGrid.Columns.Add(TextCol("Quality.Col.Min", nameof(QualityInspectionLine.MinValue), 70, numeric: true));
        _linesGrid.Columns.Add(TextCol("Quality.Col.Max", nameof(QualityInspectionLine.MaxValue), 70, numeric: true));
        _linesGrid.Columns.Add(TextCol("Quality.Col.AcceptedValue", nameof(QualityInspectionLine.AcceptedValue), 100));
        _linesGrid.Columns.Add(TextCol("Quality.Col.Result", nameof(QualityInspectionLine.Result), 70));
        _linesGrid.SelectionChanged += (_, _) => OnLineSelected();
        linesPanel.Children.Add(_linesGrid);
        Grid.SetRow(linesPanel, 1);
        Grid.SetColumn(linesPanel, 0);
        content.Children.Add(linesPanel);

        // ---- Line editor + completion (right, bottom) ------------------
        var editPanel = new StackPanel();
        var lineForm = CreateFormGrid(1);
        AddFormField(_labels, lineForm, 0, 0, "Quality.Reading1", _reading1);
        AddFormField(_labels, lineForm, 1, 0, "Quality.Reading2", _reading2);
        AddFormField(_labels, lineForm, 2, 0, "Quality.Reading3", _reading3);
        AddFormField(_labels, lineForm, 3, 0, "Quality.ReadingText", _readingText);
        AddFormField(_labels, lineForm, 4, 0, "Quality.Col.Result", _lineResult);
        AddFormField(_labels, lineForm, 5, 0, "Quality.LineNotes", _lineNotes);
        _lineResult.ItemsSource = new[] { "Auto", "Pass", "Fail" };
        _lineResult.SelectedItem = "Auto";
        foreach (var box in new[] { _reading1, _reading2, _reading3, _readingText })
            box.TextChanged += (_, _) => UpdateAutoResult();
        editPanel.Children.Add(lineForm);
        var autoPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        var autoLabel = new TextBlock { FontWeight = FontWeights.SemiBold };
        _labels.Add((autoLabel, "Quality.AutoResult"));
        autoPanel.Children.Add(autoLabel);
        _autoResult.Margin = new Thickness(8, 0, 0, 0);
        autoPanel.Children.Add(_autoResult);
        editPanel.Children.Add(autoPanel);
        var completeHeader = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) };
        _labels.Add((completeHeader, "Quality.Completion"));
        editPanel.Children.Add(completeHeader);
        var completeForm = CreateFormGrid(1);
        AddFormField(_labels, completeForm, 0, 0, "Quality.AcceptedQty", _acceptedQty);
        AddFormField(_labels, completeForm, 1, 0, "Quality.RejectedQty", _rejectedQty);
        AddFormField(_labels, completeForm, 2, 0, "Quality.Disposition", _disposition);
        AddFormField(_labels, completeForm, 3, 0, "Quality.Notes", _completeNotes);
        _disposition.ItemsSource = QualityDispositions.All;
        editPanel.Children.Add(completeForm);
        var editScroll = new ScrollViewer { Content = editPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(editScroll, 1);
        Grid.SetColumn(editScroll, 1);
        content.Children.Add(editScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Quality.InspectionsTitle");
        _inspectionsHeader.Text = T("Quality.InspectionsTitle");
        _linesHeader.Text = T("Quality.Lines");
        _detailHeader.Text = T("Quality.Detail");
        _pendingOnly.Content = T("Quality.PendingOnly");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _saveButton, _completeButton, _newButton, _applyLineButton, _gateModeButton, _alertsButton, _refreshButton, _closeButton })
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
            _gateMode.SelectedItem = await _service.GetGateModeAsync(company);
            _materials = (await _service.GetMaterialOptionsAsync(company)).ToList();
            _newMaterial.ItemsSource = _materials.Select(m => $"{m.Code} — {m.Name}").ToList();
            var rows = await _service.GetInspectionsAsync(company, _pendingOnly.IsChecked == true);
            _inspections.Clear();
            _inspections.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _inspections;
            var overdue = await _service.GetOverdueInspectionsAsync(company,
                DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            _statusBar.Text = Tf("Quality.StatusLine", _inspections.Count, overdue.Count);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcInspectWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OnInspectionSelectedAsync()
    {
        try
        {
            if (_service is null || _grid.SelectedItem is not QualityInspection selected)
                return;
            _current = await _service.GetInspectionAsync(CurrentCompanyCode, selected.Id);
            if (_current is null)
                return;
            _detailInfo.Text =
                $"{_current.RefDocNo} · {_current.InspectionType} · {_current.MaterialCode} {_current.MaterialName}\n" +
                $"{T("Quality.Col.TotalQty")}: {_current.TotalQty} · {T("Quality.Col.Status")}: {_current.Status}\n" +
                $"{T("Quality.Col.DueDate")}: {_current.DueDate ?? "-"} · {T("Quality.Col.Batch")}: {_current.BatchNo ?? "-"}";
            _lines.Clear();
            _lines.AddRange(_current.Lines);
            _linesGrid.ItemsSource = null;
            _linesGrid.ItemsSource = _lines;
            _acceptedQty.Text = _current.TotalQty.ToString("0.####", CultureInfo.InvariantCulture);
            _rejectedQty.Text = "0";
            _disposition.SelectedItem = QualityDispositions.Quarantine;
            _completeNotes.Text = _current.Notes ?? string.Empty;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcInspectWin.OnInspectionSelectedAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnLineSelected()
    {
        if (_linesGrid.SelectedItem is not QualityInspectionLine line)
            return;
        _currentLine = line;
        _reading1.Text = NumText(line, 1);
        _reading2.Text = NumText(line, 2);
        _reading3.Text = NumText(line, 3);
        _readingText.Text = line.Readings.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ReadingText))?.ReadingText ?? string.Empty;
        _lineResult.SelectedItem = line.Result ?? "Auto";
        _lineNotes.Text = line.Notes ?? string.Empty;
        UpdateAutoResult();
    }

    private void UpdateAutoResult()
    {
        if (_currentLine is null)
        {
            _autoResult.Text = string.Empty;
            return;
        }
        var evaluated = EvaluateEditorLine(_currentLine);
        _autoResult.Text = evaluated ?? T("Quality.Unjudged");
    }

    /// <summary>Reads the editor's readings into the selected line,
    /// keeping the override flag honest: a manual result that differs
    /// from the auto-evaluation is marked overridden.</summary>
    private void ApplyLineEditor()
    {
        if (_currentLine is null)
            return;
        var line = _currentLine;
        line.Readings.Clear();
        AddReading(line, 1, _reading1.Text);
        AddReading(line, 2, _reading2.Text);
        AddReading(line, 3, _reading3.Text);
        if (!string.IsNullOrWhiteSpace(_readingText.Text))
        {
            var text = line.Readings.FirstOrDefault(r => r.ReadingNo == 1)
                ?? new QualityReading { ReadingNo = 1 };
            if (!line.Readings.Contains(text))
                line.Readings.Add(text);
            text.ReadingText = _readingText.Text.Trim();
        }
        var manual = _lineResult.SelectedItem as string;
        var auto = EvaluateEditorLine(line);
        if (manual is "Pass" or "Fail")
        {
            line.Result = manual;
            line.IsOverridden = auto is not null && auto != manual;
        }
        else
        {
            line.Result = auto;
            line.IsOverridden = false;
        }
        line.Notes = string.IsNullOrWhiteSpace(_lineNotes.Text) ? null : _lineNotes.Text.Trim();
        _linesGrid.ItemsSource = null;
        _linesGrid.ItemsSource = _lines;
        _linesGrid.SelectedItem = line;
    }

    private string? EvaluateEditorLine(QualityInspectionLine line)
    {
        // Evaluate from the EDITOR's current readings, not the saved
        // ones, so the inspector sees the verdict as they type.
        var readings = new List<QualityReading>();
        void Add(int no, string text)
        {
            if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v)
                || decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
                readings.Add(new QualityReading { ReadingNo = no, ReadingNum = v });
        }
        Add(1, _reading1.Text);
        Add(2, _reading2.Text);
        Add(3, _reading3.Text);
        if (!string.IsNullOrWhiteSpace(_readingText.Text))
            readings.Add(new QualityReading { ReadingNo = 1, ReadingText = _readingText.Text.Trim() });
        var recorded = (_lineResult.SelectedItem as string) is "Pass" or "Fail"
            ? _lineResult.SelectedItem as string : null;
        return QualityGate.EvaluateLine(new QualityLineEvaluationInput(
            line.ParamType, line.MinValue, line.MaxValue, line.AcceptedValue, readings, recorded));
    }

    private async Task SaveResultsAsync()
    {
        try
        {
            if (_service is null || _current is null)
                throw new InvalidOperationException(T("Quality.SelectInspection"));
            if (!AppHost.Permissions.Has(PermCat.Quality.InspectEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            ApplyLineEditor();
            _current.Lines = _lines;
            await _service.SaveResultsAsync(CurrentCompanyCode, _current, CurrentUserId);
            _statusBar.Text = T("Quality.ResultsSaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcInspectWin.SaveResultsAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CompleteAsync()
    {
        try
        {
            if (_service is null || _current is null)
                throw new InvalidOperationException(T("Quality.SelectInspection"));
            if (!AppHost.Permissions.Has(PermCat.Quality.InspectComplete))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            if (_current.Status != QualityInspectionStatuses.Draft)
                throw new InvalidOperationException(T("Quality.AlreadyCompleted"));
            ApplyLineEditor();
            _current.Lines = _lines;
            await _service.SaveResultsAsync(CurrentCompanyCode, _current, CurrentUserId);
            var accepted = ParseDecimal(_acceptedQty.Text);
            var rejected = ParseDecimal(_rejectedQty.Text);
            var disposition = rejected > 0 ? _disposition.SelectedItem as string : null;
            var result = await _service.CompleteAsync(CurrentCompanyCode, _current.Id,
                accepted, rejected, disposition, CurrentUserId,
                string.IsNullOrWhiteSpace(_completeNotes.Text) ? null : _completeNotes.Text.Trim());
            MessageBox.Show(this, Tf("Quality.CompletedLine", result.Status, result.AcceptedQty, result.RejectedQty),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcInspectWin.CompleteAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CreateOutgoingAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Quality.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Quality.InspectEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            if (_newMaterial.SelectedIndex < 0 || _newMaterial.SelectedIndex >= _materials.Count)
                throw new InvalidOperationException(T("Quality.SelectMaterial"));
            if (string.IsNullOrWhiteSpace(_newDocNo.Text))
                throw new InvalidOperationException(T("Quality.DocNoRequired"));
            var material = _materials[_newMaterial.SelectedIndex];
            await _service.CreateInspectionAsync(CurrentCompanyCode, new QualityInspection
            {
                InspectionType = QualityInspectionTypes.Outgoing,
                RefDocKind = "SAL",
                RefDocNo = _newDocNo.Text.Trim(),
                MaterialId = material.MaterialId,
                TotalQty = 0m,
                SampleQty = 0m
            }, CurrentUserId);
            _newDocNo.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcInspectWin.CreateOutgoingAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SetGateModeAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Quality.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Quality.TemplateEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            var mode = _gateMode.SelectedItem as string ?? QualityGateModes.Default;
            await _service.SetGateModeAsync(CurrentCompanyCode, mode, CurrentUserId);
            _statusBar.Text = Tf("Quality.GateModeSet", mode);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcInspectWin.SetGateModeAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Helpers (BudgetWin pattern)
    // ------------------------------------------------------------------

    private static string NumText(QualityInspectionLine line, int no)
        => line.Readings.FirstOrDefault(r => r.ReadingNo == no)?.ReadingNum
            ?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void AddReading(QualityInspectionLine line, int no, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (!decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v)
            && !decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            throw new InvalidOperationException("Invalid number format.");
        line.Readings.Add(new QualityReading { ReadingNo = no, ReadingNum = v });
    }

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

    private static decimal ParseDecimal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }
}
