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
using ShouTech.App.Views.Reports;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Budgets window (Phase B 055, design section 3's BudgetWin): the
/// budget definition surface — header (fiscal year from the shell
/// company's years, budget-against kind, Draft/Active/Closed status,
/// per-horizon Stop/Warn/Ignore actions) + lines grid (account x cost
/// center / project with annual amounts) + the monthly distribution
/// editor for the selected line. Explorer-style layout like ProjWin:
/// list + editor as the content, actions in the bottom command bar,
/// slim status bar at the very bottom. All writes go through
/// <see cref="BudgetService"/> (validated, transactional; saving a line
/// flags its account budget-opt-in on the chart in the same
/// transaction, and only Active budgets are enforced by BudgetGuard at
/// posting). The Variance button opens BudgetVarWin.
/// </summary>
public sealed class BudgetWin : BaseWin
{
    protected override string? ScheduleTintName => "Copper";

    private readonly BudgetService? _service;
    private readonly List<Budget> _budgets = new();
    private readonly List<BudgetLine> _lines = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private List<BudgetFiscalYear> _fiscalYears = new();
    private List<BudgetAccountOption> _accounts = new();
    private List<BudgetDimensionOption> _costCenters = new();
    private List<BudgetDimensionOption> _projects = new();
    private Guid _companyId = Guid.Empty;

    private readonly DataGrid _grid = new();
    private readonly DataGrid _linesGrid = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _fiscalYear = new();
    private readonly ComboBox _against = new();
    private readonly ComboBox _status = new();
    private readonly ComboBox _actionAnnual = new();
    private readonly ComboBox _actionMonthly = new();
    private readonly TextBox _notes = new();
    private readonly ComboBox _lineAccount = new();
    private readonly ComboBox _lineDimension = new();
    private readonly TextBox _lineAmount = new();
    private readonly TextBlock _budgetsHeader = new();
    private readonly TextBlock _linesHeader = new();
    private readonly TextBlock _distributionHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox[] _monthBoxes = new TextBox[12];
    private readonly TextBlock[] _monthLabels = new TextBlock[12];
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _activateButton = new();
    private readonly Button _closeBudgetButton = new();
    private readonly Button _addLineButton = new();
    private readonly Button _removeLineButton = new();
    private readonly Button _spreadButton = new();
    private readonly Button _varianceButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;
    private Guid? _selectedLineId;
    private bool _loadingDistribution;

    public BudgetWin()
    {
        _service = AppHost.Budgets;
        Width = 1180;
        Height = 740;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.BudgetView))
        {
            MessageBox.Show(this, T("Budget.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        try
        {
            if (AppHost.JournalStore is { } store && !string.IsNullOrWhiteSpace(CurrentCompanyCode))
                _companyId = await store.ResolveCompanyIdAsync(CurrentCompanyCode);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BudgetWin.OnLoaded");
            _statusBar.Text = T("Budget.CompanyUnavailable");
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

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_activateButton, "Budget.Activate");
        StyleButton(_closeBudgetButton, "Budget.CloseBudget");
        StyleButton(_addLineButton, "Budget.AddLine");
        StyleButton(_removeLineButton, "Budget.RemoveLine");
        StyleButton(_spreadButton, "Budget.SpreadEqually");
        StyleButton(_varianceButton, "Budget.Variance");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _activateButton.Click += async (_, _) => await SetStatusAsync(BudgetStatuses.Active);
        _closeBudgetButton.Click += async (_, _) => await SetStatusAsync(BudgetStatuses.Closed);
        _addLineButton.Click += (_, _) => AddLine();
        _removeLineButton.Click += (_, _) => RemoveLine();
        _spreadButton.Click += (_, _) => SpreadEqually();
        _varianceButton.Click += (_, _) => new BudgetVarWin().Show();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _activateButton, _closeBudgetButton, _addLineButton, _removeLineButton, _spreadButton, _varianceButton, _refreshButton, _closeButton })
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
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _budgetsHeader.FontWeight = FontWeights.SemiBold;
        _budgetsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_budgetsHeader, Dock.Top);
        listPanel.Children.Add(_budgetsHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Budget.Name", nameof(Budget.Name), 170));
        _grid.Columns.Add(TextCol("Budget.FiscalYear", nameof(Budget.FiscalYearLabel), 80, center: true));
        _grid.Columns.Add(TextCol("Budget.BudgetAgainst", nameof(Budget.BudgetAgainst), 100));
        _grid.Columns.Add(TextCol("Budget.Status", nameof(Budget.Status), 80));
        _grid.Columns.Add(TextCol("Budget.ActionAnnual", nameof(Budget.ActionAnnual), 90));
        _grid.Columns.Add(TextCol("Budget.ActionMonthly", nameof(Budget.ActionMonthly), 90));
        _grid.SelectionChanged += (_, _) => OnBudgetSelected();
        listPanel.Children.Add(_grid);
        Grid.SetRow(listPanel, 0);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var editor = CreateFormGrid(1);
        AddFormField(_labels, editor, 0, 0, "Budget.Name", _name);
        AddFormField(_labels, editor, 1, 0, "Budget.FiscalYear", _fiscalYear);
        AddFormField(_labels, editor, 2, 0, "Budget.BudgetAgainst", _against);
        AddFormField(_labels, editor, 3, 0, "Budget.Status", _status);
        AddFormField(_labels, editor, 4, 0, "Budget.ActionAnnual", _actionAnnual);
        AddFormField(_labels, editor, 5, 0, "Budget.ActionMonthly", _actionMonthly);
        AddFormField(_labels, editor, 6, 0, "Budget.Notes", _notes);
        _against.ItemsSource = BudgetAgainstKinds.All;
        _against.SelectedItem = BudgetAgainstKinds.CostCenter;
        _against.SelectionChanged += (_, _) => RefreshDimensionChoices();
        _status.ItemsSource = BudgetStatuses.All;
        _status.SelectedItem = BudgetStatuses.Draft;
        _actionAnnual.ItemsSource = BudgetActions.All;
        _actionAnnual.SelectedItem = BudgetActions.Stop;
        _actionMonthly.ItemsSource = BudgetActions.All;
        _actionMonthly.SelectedItem = BudgetActions.Warn;
        var editorScroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(editorScroll, 0);
        Grid.SetColumn(editorScroll, 1);
        content.Children.Add(editorScroll);

        _linesHeader.FontWeight = FontWeights.SemiBold;
        _linesHeader.Margin = new Thickness(0, 10, 0, 6);
        Grid.SetRow(_linesHeader, 1);
        Grid.SetColumnSpan(_linesHeader, 2);
        content.Children.Add(_linesHeader);

        var linesPanel = new Grid();
        linesPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5, GridUnitType.Star) });
        linesPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        ConfigureGrid(_linesGrid);
        _linesGrid.Columns.Add(TextCol("Budget.Account", nameof(BudgetLine.AccountCode), 110));
        _linesGrid.Columns.Add(TextCol("Budget.Dimension", nameof(BudgetLine.DimensionCode), 130));
        _linesGrid.Columns.Add(TextCol("Budget.AnnualAmount", nameof(BudgetLine.AnnualAmount), 120, numeric: true));
        _linesGrid.SelectionChanged += (_, _) => OnLineSelected();
        var linesHost = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        linesHost.Children.Add(_linesGrid);
        Grid.SetColumn(linesHost, 0);
        linesPanel.Children.Add(linesHost);

        var rightPanel = new StackPanel();
        var lineEditor = CreateFormGrid(1);
        AddFormField(_labels, lineEditor, 0, 0, "Budget.Account", _lineAccount);
        AddFormField(_labels, lineEditor, 1, 0, "Budget.Dimension", _lineDimension);
        AddFormField(_labels, lineEditor, 2, 0, "Budget.AnnualAmount", _lineAmount);
        rightPanel.Children.Add(lineEditor);
        _distributionHeader.FontWeight = FontWeights.SemiBold;
        _distributionHeader.Margin = new Thickness(0, 10, 0, 6);
        rightPanel.Children.Add(_distributionHeader);
        var months = new Grid();
        for (var r = 0; r < 4; r++)
            months.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < 3; c++)
            months.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 12; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 6) };
            _monthLabels[i] = new TextBlock();
            _monthBoxes[i] = new TextBox();
            var monthIndex = i;
            _monthBoxes[i].TextChanged += (_, _) => OnMonthEdited(monthIndex);
            cell.Children.Add(_monthLabels[i]);
            cell.Children.Add(_monthBoxes[i]);
            Grid.SetRow(cell, i / 3);
            Grid.SetColumn(cell, i % 3);
            months.Children.Add(cell);
        }
        rightPanel.Children.Add(months);
        var rightScroll = new ScrollViewer { Content = rightPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(rightScroll, 1);
        linesPanel.Children.Add(rightScroll);
        Grid.SetRow(linesPanel, 2);
        Grid.SetColumnSpan(linesPanel, 2);
        content.Children.Add(linesPanel);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Budget.Title");
        _budgetsHeader.Text = T("Budget.Title");
        _linesHeader.Text = T("Budget.Lines");
        _distributionHeader.Text = T("Budget.Distribution");
        for (var i = 0; i < 12; i++)
            _monthLabels[i].Text = Tf("Budget.Month", i + 1);
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _activateButton, _closeBudgetButton, _addLineButton, _removeLineButton, _spreadButton, _varianceButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    // ------------------------------------------------------------------
    // Data flow
    // ------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null || _companyId == Guid.Empty)
            {
                _statusBar.Text = T("Budget.ServiceUnavailable");
                return;
            }
            _fiscalYears = (await _service.GetFiscalYearsAsync(_companyId)).ToList();
            _fiscalYear.ItemsSource = _fiscalYears.Select(y => y.Label).ToList();
            _accounts = (await _service.GetAccountsAsync(_companyId)).ToList();
            _lineAccount.ItemsSource = _accounts.Select(a => $"{a.Code} — {a.Name}").ToList();
            _costCenters = (await _service.GetCostCentersAsync(_companyId)).ToList();
            _projects = (await _service.GetProjectsAsync(_companyId)).ToList();
            RefreshDimensionChoices();

            var rows = await _service.GetAllAsync(_companyId);
            _budgets.Clear();
            _budgets.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _budgets;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BudgetWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshDimensionChoices()
    {
        var against = _against.SelectedItem as string ?? BudgetAgainstKinds.CostCenter;
        if (against == BudgetAgainstKinds.CostCenter)
        {
            _lineDimension.ItemsSource = _costCenters.Select(c => $"{c.Code} — {c.Name}").ToList();
            _lineDimension.IsEnabled = true;
        }
        else if (against == BudgetAgainstKinds.Project)
        {
            _lineDimension.ItemsSource = _projects.Select(p => $"{p.Code} — {p.Name}").ToList();
            _lineDimension.IsEnabled = true;
        }
        else
        {
            _lineDimension.ItemsSource = null;
            _lineDimension.IsEnabled = false;
        }
    }

    private void OnBudgetSelected()
    {
        if (_grid.SelectedItem is not Budget budget)
            return;
        _selectedId = budget.Id;
        _name.Text = budget.Name;
        var yearIndex = _fiscalYears.FindIndex(y => y.Id == budget.FiscalYearId);
        _fiscalYear.SelectedIndex = yearIndex;
        _against.SelectedItem = budget.BudgetAgainst;
        _status.SelectedItem = budget.Status;
        _actionAnnual.SelectedItem = budget.ActionAnnual;
        _actionMonthly.SelectedItem = budget.ActionMonthly;
        _notes.Text = budget.Notes ?? string.Empty;
        _lines.Clear();
        _lines.AddRange(budget.Lines.Select(CloneLine));
        _linesGrid.ItemsSource = null;
        _linesGrid.ItemsSource = _lines;
        ClearLineEditor();
        UpdateStatusBar();
    }

    private void OnLineSelected()
    {
        if (_linesGrid.SelectedItem is not BudgetLine line)
            return;
        _selectedLineId = line.Id;
        var accountIndex = _accounts.FindIndex(a => a.Id == line.AccountId);
        _lineAccount.SelectedIndex = accountIndex;
        var against = _against.SelectedItem as string ?? BudgetAgainstKinds.CostCenter;
        if (against == BudgetAgainstKinds.CostCenter)
            _lineDimension.SelectedIndex = _costCenters.FindIndex(c => c.Id == line.CostCenterId);
        else if (against == BudgetAgainstKinds.Project)
            _lineDimension.SelectedIndex = _projects.FindIndex(p => p.Id == line.ProjectId);
        _lineAmount.Text = line.AnnualAmount.ToString("0.##", CultureInfo.CurrentCulture);
        LoadDistribution(line);
    }

    private void LoadDistribution(BudgetLine line)
    {
        _loadingDistribution = true;
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var value = line.PeriodAmounts.TryGetValue(i + 1, out var amount)
                    ? amount
                    : EqualShare(line.AnnualAmount, i);
                _monthBoxes[i].Text = value.ToString("0.##", CultureInfo.CurrentCulture);
            }
        }
        finally
        {
            _loadingDistribution = false;
        }
    }

    private void OnMonthEdited(int monthIndex)
    {
        if (_loadingDistribution || _selectedLineId is not Guid lineId)
            return;
        var line = _lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return;
        // The first manual edit turns the displayed (possibly equal-
        // split) values into the line's stored distribution.
        var amounts = new Dictionary<int, decimal>();
        for (var i = 0; i < 12; i++)
            amounts[i + 1] = ParseDecimalSilent(_monthBoxes[i].Text);
        line.PeriodAmounts = amounts;
    }

    private void SpreadEqually()
    {
        if (_selectedLineId is not Guid lineId)
            return;
        var line = _lines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return;
        line.AnnualAmount = ParseDecimal(_lineAmount.Text);
        line.PeriodAmounts = new Dictionary<int, decimal>();
        LoadDistribution(line);
    }

    private void AddLine()
    {
        try
        {
            var against = _against.SelectedItem as string ?? BudgetAgainstKinds.CostCenter;
            if (_lineAccount.SelectedIndex < 0 || _lineAccount.SelectedIndex >= _accounts.Count)
                throw new InvalidOperationException(T("Budget.SelectLine"));
            var account = _accounts[_lineAccount.SelectedIndex];
            string? centerId = null, centerCode = null, projectId = null, projectCode = null;
            if (against == BudgetAgainstKinds.CostCenter)
            {
                if (_lineDimension.SelectedIndex < 0 || _lineDimension.SelectedIndex >= _costCenters.Count)
                    throw new InvalidOperationException(T("Budget.SelectLine"));
                var center = _costCenters[_lineDimension.SelectedIndex];
                centerId = center.Id;
                centerCode = center.Code;
            }
            else if (against == BudgetAgainstKinds.Project)
            {
                if (_lineDimension.SelectedIndex < 0 || _lineDimension.SelectedIndex >= _projects.Count)
                    throw new InvalidOperationException(T("Budget.SelectLine"));
                var project = _projects[_lineDimension.SelectedIndex];
                projectId = project.Id;
                projectCode = project.Code;
            }

            var existing = _selectedLineId is Guid id ? _lines.FirstOrDefault(l => l.Id == id) : null;
            var line = existing ?? new BudgetLine { Id = Guid.NewGuid() };
            line.AccountId = account.Id;
            line.AccountCode = account.Code;
            line.AccountName = account.Name;
            line.CostCenterId = centerId;
            line.CostCenterCode = centerCode;
            line.ProjectId = projectId;
            line.ProjectCode = projectCode;
            line.AnnualAmount = ParseDecimal(_lineAmount.Text);
            if (existing is null)
            {
                _lines.Add(line);
                _selectedLineId = line.Id;
            }
            _linesGrid.ItemsSource = null;
            _linesGrid.ItemsSource = _lines;
            _linesGrid.SelectedItem = line;
            LoadDistribution(line);
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BudgetWin.AddLine");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveLine()
    {
        if (_selectedLineId is not Guid id)
            return;
        _lines.RemoveAll(l => l.Id == id);
        _linesGrid.ItemsSource = null;
        _linesGrid.ItemsSource = _lines;
        ClearLineEditor();
        UpdateStatusBar();
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null || _companyId == Guid.Empty)
                throw new InvalidOperationException(T("Budget.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Accounting.BudgetEdit))
                throw new InvalidOperationException(T("Budget.PermissionDenied"));
            if (_fiscalYear.SelectedIndex < 0 || _fiscalYear.SelectedIndex >= _fiscalYears.Count)
                throw new InvalidOperationException(T("Budget.SelectBudget"));
            var budget = new Budget
            {
                Id = _selectedId ?? Guid.NewGuid(),
                FiscalYearId = _fiscalYears[_fiscalYear.SelectedIndex].Id,
                Name = _name.Text.Trim(),
                BudgetAgainst = _against.SelectedItem as string ?? BudgetAgainstKinds.CostCenter,
                Status = _status.SelectedItem as string ?? BudgetStatuses.Draft,
                ActionAnnual = _actionAnnual.SelectedItem as string ?? BudgetActions.Stop,
                ActionMonthly = _actionMonthly.SelectedItem as string ?? BudgetActions.Warn,
                Notes = NullIfBlank(_notes.Text),
                Lines = _lines.Select(CloneLine).ToList()
            };
            await _service.SaveAsync(_companyId, budget, CurrentUserId);
            _selectedId = budget.Id;
            MessageBox.Show(this, T("Budget.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BudgetWin.SaveAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _companyId == Guid.Empty || !_selectedId.HasValue)
                return;
            if (!AppHost.Permissions.Has(PermCat.Accounting.BudgetEdit))
                throw new InvalidOperationException(T("Budget.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteAsync(_companyId, _selectedId.Value, CurrentUserId);
            _selectedId = null;
            ClearEditor();
            MessageBox.Show(this, T("Budget.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BudgetWin.DeleteAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SetStatusAsync(string status)
    {
        try
        {
            if (_service is null || _companyId == Guid.Empty || !_selectedId.HasValue)
            {
                MessageBox.Show(this, T("Budget.SelectBudget"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!AppHost.Permissions.Has(PermCat.Accounting.BudgetActivate))
                throw new InvalidOperationException(T("Budget.PermissionDenied"));
            await _service.SetStatusAsync(_companyId, _selectedId.Value, status, CurrentUserId);
            _status.SelectedItem = status;
            MessageBox.Show(this,
                status == BudgetStatuses.Active ? T("Budget.Activated") : T("Budget.Closed"),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BudgetWin.SetStatusAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _name.Clear();
        _fiscalYear.SelectedIndex = _fiscalYears.Count > 0 ? 0 : -1;
        _against.SelectedItem = BudgetAgainstKinds.CostCenter;
        _status.SelectedItem = BudgetStatuses.Draft;
        _actionAnnual.SelectedItem = BudgetActions.Stop;
        _actionMonthly.SelectedItem = BudgetActions.Warn;
        _notes.Clear();
        _lines.Clear();
        _linesGrid.ItemsSource = null;
        _linesGrid.ItemsSource = _lines;
        ClearLineEditor();
        UpdateStatusBar();
    }

    private void ClearLineEditor()
    {
        _selectedLineId = null;
        _linesGrid.SelectedItem = null;
        _lineAccount.SelectedIndex = -1;
        _lineDimension.SelectedIndex = -1;
        _lineAmount.Clear();
        _loadingDistribution = true;
        try
        {
            foreach (var box in _monthBoxes)
                box.Clear();
        }
        finally
        {
            _loadingDistribution = false;
        }
    }

    private void UpdateStatusBar()
    {
        var total = _lines.Sum(l => l.AnnualAmount);
        _statusBar.Text = $"{_budgets.Count} · {_lines.Count} {T("Budget.Lines")} · {total:N2}";
    }

    private static BudgetLine CloneLine(BudgetLine line) => new()
    {
        Id = line.Id,
        BudgetId = line.BudgetId,
        AccountId = line.AccountId,
        AccountCode = line.AccountCode,
        AccountName = line.AccountName,
        CostCenterId = line.CostCenterId,
        CostCenterCode = line.CostCenterCode,
        ProjectId = line.ProjectId,
        ProjectCode = line.ProjectCode,
        AnnualAmount = line.AnnualAmount,
        PeriodAmounts = new Dictionary<int, decimal>(line.PeriodAmounts)
    };

    /// <summary>The equal-split share of a month: annual / 12 for
    /// months 1-11, the remainder in month 12 so the displayed split
    /// always sums exactly to the annual amount.</summary>
    private static decimal EqualShare(decimal annual, int monthIndex)
    {
        var share = Math.Round(annual / 12m, 2, MidpointRounding.AwayFromZero);
        return monthIndex < 11 ? share : annual - share * 11m;
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

    private DataGridTextColumn TextCol(string key, string path, double width, bool numeric = false, bool center = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        if (numeric)
            column.ElementStyle = RightAlignStyle;
        else if (center)
            column.ElementStyle = CenterAlignStyle;
        _headers.Add((column, key));
        return column;
    }

    private static readonly Style RightAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) }
    };

    private static readonly Style CenterAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center) }
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

    private static decimal ParseDecimal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }

    private static decimal ParseDecimalSilent(string text)
        => decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
           decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v)
            ? v
            : 0m;
}
