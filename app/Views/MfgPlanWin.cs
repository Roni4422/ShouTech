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
/// Manufacturing planning window (MRP-lite, migration 058): Run executes
/// PlanningService.RunPlanningAsync — demand from open sales orders and
/// reorder points, netted against valuation-engine stock and incoming
/// purchase orders, exploded through active BOMs — and shows the run's
/// planned production orders and purchase suggestions. Convert turns a
/// planned order into a real Draft production order (BOM exploded) and
/// a suggestion into a real Draft purchase order through the existing
/// document path. The feasibility panel answers "can we build X?" from
/// the SAME explosion planning uses (PlanningService.
/// CheckFeasibilityAsync), so the answer and the plan cannot disagree.
/// </summary>
public sealed class MfgPlanWin : BaseWin
{
    protected override string? ScheduleTintName => "Sorrel";

    private readonly PlanningService? _service;
    private readonly BomService? _bomService;
    private readonly List<(DataGridColumn Column, string Key)> _plannedHeaders = new();
    private readonly List<(DataGridColumn Column, string Key)> _suggestionHeaders = new();
    private readonly List<(DataGridColumn Column, string Key)> _feasibilityHeaders = new();

    private readonly ComboBox _runs = new();
    private readonly DataGrid _plannedGrid = new();
    private readonly DataGrid _suggestionGrid = new();
    private readonly DataGrid _feasibilityGrid = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBlock _feasibilityVerdict = new();
    private readonly ComboBox _feasibilityMaterial = new() { IsEditable = true };
    private readonly TextBox _feasibilityQty = new() { Text = "1" };
    private readonly Button _runButton = new();
    private readonly Button _convertPlannedButton = new();
    private readonly Button _convertSuggestionButton = new();
    private readonly Button _checkButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private PlanningRun? _currentRun;

    public MfgPlanWin()
    {
        _service = AppHost.Planning;
        _bomService = AppHost.Boms;
        Width = 1080;
        Height = 640;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.PlanningView))
        {
            MessageBox.Show(this, T("MfgPlan.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        StyleButton(_runButton, "MfgPlan.Run");
        StyleButton(_convertPlannedButton, "MfgPlan.ConvertPlanned");
        StyleButton(_convertSuggestionButton, "MfgPlan.ConvertSuggestion");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _runButton.Click += async (_, _) => await RunAsync();
        _convertPlannedButton.Click += async (_, _) => await ConvertPlannedAsync();
        _convertSuggestionButton.Click += async (_, _) => await ConvertSuggestionAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _runButton, _convertPlannedButton, _convertSuggestionButton, _refreshButton, _closeButton })
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

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = T("MfgPlan.Runs"), Tag = "MfgPlan.Runs", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _runs.MinWidth = 260;
        _runs.SelectionChanged += (_, _) => ShowSelectedRun();
        top.Children.Add(_runs);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

        ConfigureGrid(_plannedGrid);
        _plannedGrid.Columns.Add(TextCol(_plannedHeaders, "Invoice.Col.Code", nameof(PlannedOrder.MaterialCode), 120));
        _plannedGrid.Columns.Add(TextCol(_plannedHeaders, "Invoice.Col.Description", nameof(PlannedOrder.MaterialName), 180));
        _plannedGrid.Columns.Add(TextCol(_plannedHeaders, "MfgOrder.Quantity", nameof(PlannedOrder.Qty), 90));
        _plannedGrid.Columns.Add(TextCol(_plannedHeaders, "MfgOrder.Unit", nameof(PlannedOrder.UnitCode), 60));
        _plannedGrid.Columns.Add(TextCol(_plannedHeaders, "MfgPlan.Col.DueDate", nameof(PlannedOrder.DueDate), 100));
        _plannedGrid.Columns.Add(TextCol(_plannedHeaders, "MfgPlan.Col.Source", nameof(PlannedOrder.Source), 120));
        _plannedGrid.Columns.Add(TextCol(_plannedHeaders, "Invoice.Status", nameof(PlannedOrder.Status), 90));
        var plannedPanel = new DockPanel();
        plannedPanel.Children.Add(SectionHeader("MfgPlan.PlannedOrders"));
        plannedPanel.Children.Add(_plannedGrid);
        Grid.SetRow(plannedPanel, 0);
        content.Children.Add(plannedPanel);

        ConfigureGrid(_suggestionGrid);
        _suggestionGrid.Columns.Add(TextCol(_suggestionHeaders, "Invoice.Col.Code", nameof(PurchaseSuggestion.MaterialCode), 120));
        _suggestionGrid.Columns.Add(TextCol(_suggestionHeaders, "Invoice.Col.Description", nameof(PurchaseSuggestion.MaterialName), 180));
        _suggestionGrid.Columns.Add(TextCol(_suggestionHeaders, "MfgOrder.Quantity", nameof(PurchaseSuggestion.Qty), 90));
        _suggestionGrid.Columns.Add(TextCol(_suggestionHeaders, "MfgOrder.Unit", nameof(PurchaseSuggestion.UnitCode), 60));
        _suggestionGrid.Columns.Add(TextCol(_suggestionHeaders, "MfgPlan.Col.Source", nameof(PurchaseSuggestion.Source), 120));
        _suggestionGrid.Columns.Add(TextCol(_suggestionHeaders, "Invoice.Status", nameof(PurchaseSuggestion.Status), 90));
        _suggestionGrid.Columns.Add(TextCol(_suggestionHeaders, "MfgPlan.Col.ConvertedDoc", nameof(PurchaseSuggestion.ConvertedDocNo), 110));
        var suggestionPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        suggestionPanel.Children.Add(SectionHeader("MfgPlan.Suggestions"));
        suggestionPanel.Children.Add(_suggestionGrid);
        Grid.SetRow(suggestionPanel, 1);
        content.Children.Add(suggestionPanel);

        var feasibilityPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var feasRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        feasRow.Children.Add(new TextBlock { Text = T("MfgPlan.Feasibility"), Tag = "MfgPlan.Feasibility", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        _feasibilityMaterial.MinWidth = 160;
        feasRow.Children.Add(_feasibilityMaterial);
        _feasibilityQty.Width = 80;
        _feasibilityQty.Margin = new Thickness(8, 0, 0, 0);
        feasRow.Children.Add(_feasibilityQty);
        StyleButton(_checkButton, "MfgPlan.Check");
        _checkButton.Click += async (_, _) => await CheckFeasibilityAsync();
        feasRow.Children.Add(_checkButton);
        _feasibilityVerdict.Margin = new Thickness(12, 0, 0, 0);
        _feasibilityVerdict.VerticalAlignment = VerticalAlignment.Center;
        _feasibilityVerdict.FontWeight = FontWeights.SemiBold;
        feasRow.Children.Add(_feasibilityVerdict);
        DockPanel.SetDock(feasRow, Dock.Top);
        feasibilityPanel.Children.Add(feasRow);
        ConfigureGrid(_feasibilityGrid);
        _feasibilityGrid.Columns.Add(TextCol(_feasibilityHeaders, "Invoice.Col.Code", nameof(FeasibilityRow.MaterialCode), 120));
        _feasibilityGrid.Columns.Add(TextCol(_feasibilityHeaders, "MfgPlan.Col.Required", nameof(FeasibilityRow.RequiredBaseQty), 100));
        _feasibilityGrid.Columns.Add(TextCol(_feasibilityHeaders, "MfgPlan.Col.OnHand", nameof(FeasibilityRow.OnHandBaseQty), 100));
        _feasibilityGrid.Columns.Add(TextCol(_feasibilityHeaders, "MfgPlan.Col.Incoming", nameof(FeasibilityRow.IncomingBaseQty), 100));
        _feasibilityGrid.Columns.Add(TextCol(_feasibilityHeaders, "MfgPlan.Col.Shortage", nameof(FeasibilityRow.ShortageBaseQty), 100));
        feasibilityPanel.Children.Add(_feasibilityGrid);
        Grid.SetRow(feasibilityPanel, 2);
        content.Children.Add(feasibilityPanel);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    private static TextBlock SectionHeader(string key)
    {
        var header = new TextBlock { Text = T(key), Tag = key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("MfgPlan.Title");
        foreach (var (column, key) in _plannedHeaders.Concat(_suggestionHeaders).Concat(_feasibilityHeaders))
            column.Header = T(key);
        foreach (var b in new[] { _runButton, _convertPlannedButton, _convertSuggestionButton, _checkButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        RefreshLocalizedContent(Content as DependencyObject);
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("MfgPlan.ServiceUnavailable");
                return;
            }
            if (_bomService is not null)
            {
                var materials = await _bomService.GetMaterialOptionsAsync(CurrentCompanyCode);
                _feasibilityMaterial.ItemsSource = materials.Select(m => m.Code).ToList();
            }
            var runs = await _service.GetRunsAsync(CurrentCompanyCode);
            _runs.ItemsSource = runs;
            _runs.DisplayMemberPath = nameof(PlanningRun.RunDate);
            if (runs.Count > 0)
                _runs.SelectedIndex = 0;
            else
            {
                _currentRun = null;
                _plannedGrid.ItemsSource = null;
                _suggestionGrid.ItemsSource = null;
                _statusBar.Text = T("MfgPlan.NoRuns");
            }
        }
        catch (Exception ex)
        {
            ReportFailure("Refresh", ex);
        }
    }

    private void ShowSelectedRun()
    {
        _currentRun = _runs.SelectedItem as PlanningRun;
        _plannedGrid.ItemsSource = _currentRun?.PlannedOrders;
        _suggestionGrid.ItemsSource = _currentRun?.Suggestions;
        if (_currentRun is not null)
            _statusBar.Text = Tf("MfgPlan.RunLine", _currentRun.RunDate, _currentRun.PlannedOrders.Count, _currentRun.Suggestions.Count);
    }

    private async Task RunAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("MfgPlan.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Manufacturing.PlanningRun))
                throw new InvalidOperationException(T("MfgPlan.PermissionDenied"));
            var run = await _service.RunPlanningAsync(CurrentCompanyCode, CurrentUserId);
            _statusBar.Text = Tf("MfgPlan.RunLine", run.RunDate, run.PlannedOrders.Count, run.Suggestions.Count);
            await RefreshAsync();
            _runs.SelectedItem = (_runs.ItemsSource as IEnumerable<PlanningRun>)?.FirstOrDefault(r => r.Id == run.Id);
        }
        catch (Exception ex)
        {
            ReportFailure("Run", ex);
        }
    }

    private async Task ConvertPlannedAsync()
    {
        try
        {
            if (_service is null || _plannedGrid.SelectedItem is not PlannedOrder planned)
                throw new InvalidOperationException(T("MfgPlan.SelectPlanned"));
            if (!AppHost.Permissions.Has(PermCat.Manufacturing.PlanningConvert))
                throw new InvalidOperationException(T("MfgPlan.PermissionDenied"));
            var orderId = await _service.ConvertPlannedOrderAsync(CurrentCompanyCode, planned.Id, CurrentUserId);
            _statusBar.Text = Tf("MfgPlan.ConvertedOrderLine", planned.MaterialCode, orderId.ToString("D"));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("ConvertPlanned", ex);
        }
    }

    private async Task ConvertSuggestionAsync()
    {
        try
        {
            if (_service is null || _suggestionGrid.SelectedItem is not PurchaseSuggestion suggestion)
                throw new InvalidOperationException(T("MfgPlan.SelectSuggestion"));
            if (!AppHost.Permissions.Has(PermCat.Manufacturing.PlanningConvert))
                throw new InvalidOperationException(T("MfgPlan.PermissionDenied"));
            var (docId, docNo) = await _service.ConvertSuggestionAsync(CurrentCompanyCode, suggestion.Id, CurrentUserId);
            _statusBar.Text = Tf("MfgPlan.ConvertedSuggestionLine", suggestion.MaterialCode, docNo);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("ConvertSuggestion", ex);
        }
    }

    private async Task CheckFeasibilityAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("MfgPlan.ServiceUnavailable"));
            var code = _feasibilityMaterial.Text.Trim();
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException(T("MfgPlan.SelectMaterial"));
            var qty = decimal.TryParse(_feasibilityQty.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
            if (qty <= 0)
                throw new InvalidOperationException(T("MfgPlan.QtyInvalid"));
            var result = await _service.CheckFeasibilityAsync(CurrentCompanyCode, code, qty);
            _feasibilityGrid.ItemsSource = result.Rows;
            _feasibilityVerdict.Text = result.CanBuild ? T("MfgPlan.CanBuild") : T("MfgPlan.CannotBuild");
        }
        catch (Exception ex)
        {
            ReportFailure("Feasibility", ex);
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        AppHost.Errors.Report(exception is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed,
            exception, $"MfgPlanWin.{operation}");
        MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

    private static DataGridTextColumn TextCol(List<(DataGridColumn Column, string Key)> headers, string key, string path, double width)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width),
            IsReadOnly = true
        };
        headers.Add((column, key));
        return column;
    }

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }
}
