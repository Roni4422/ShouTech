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

/// <summary>
/// Quality alerts window (Phase B 056, design section 5's QcAlertWin —
/// the Odoo Quality Alert pattern): every failed inspection, overdue
/// pending inspection follow-up and Warn-mode gate pass lands here as
/// a tracked alert with a team workflow (Open → InProgress → Closed).
/// Closing requires a root cause and a corrective action, enforced by
/// <see cref="QualityService"/>. The overdue-inspection list itself
/// lives in QcInspectWin's worklist (its status line counts overdue
/// Draft inspections); this window is the alert record.
/// </summary>
public sealed class QcAlertWin : BaseWin
{
    protected override string? ScheduleTintName => "Garnet";

    private readonly QualityService? _service;
    private readonly List<QualityAlert> _alerts = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly CheckBox _openOnly = new();
    private readonly TextBlock _alertsHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _message = new();
    private readonly TextBox _rootCause = new();
    private readonly TextBox _corrective = new();
    private readonly TextBox _preventive = new();
    private readonly ComboBox _status = new();
    private readonly TextBox _owner = new();
    private readonly Button _saveButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;

    public QcAlertWin()
    {
        _service = AppHost.Quality;
        Width = 1000;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Quality.AlertView))
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
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _saveButton.Click += async (_, _) => await SaveAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _saveButton, _refreshButton, _closeButton })
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
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var listHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _alertsHeader.FontWeight = FontWeights.SemiBold;
        listHeader.Children.Add(_alertsHeader);
        _openOnly.Margin = new Thickness(16, 0, 0, 0);
        _openOnly.IsChecked = true;
        _openOnly.Checked += async (_, _) => await RefreshAsync();
        _openOnly.Unchecked += async (_, _) => await RefreshAsync();
        listHeader.Children.Add(_openOnly);
        DockPanel.SetDock(listHeader, Dock.Top);
        listPanel.Children.Add(listHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Quality.Col.AlertType", nameof(QualityAlert.AlertType), 110));
        _grid.Columns.Add(TextCol("Quality.Col.Material", nameof(QualityAlert.MaterialCode), 90));
        _grid.Columns.Add(TextCol("Quality.Col.Message", nameof(QualityAlert.Message), 260));
        _grid.Columns.Add(TextCol("Quality.Col.DueDate", nameof(QualityAlert.DueDate), 90));
        _grid.Columns.Add(TextCol("Quality.Col.Status", nameof(QualityAlert.Status), 90));
        _grid.SelectionChanged += (_, _) => OnAlertSelected();
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Quality.Col.Message", _message);
        AddFormField(_labels, form, 1, 0, "Quality.RootCause", _rootCause);
        AddFormField(_labels, form, 2, 0, "Quality.CorrectiveAction", _corrective);
        AddFormField(_labels, form, 3, 0, "Quality.PreventiveAction", _preventive);
        AddFormField(_labels, form, 4, 0, "Quality.Col.Status", _status);
        AddFormField(_labels, form, 5, 0, "Quality.Owner", _owner);
        _message.IsReadOnly = true;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.MinHeight = 48;
        _rootCause.TextWrapping = TextWrapping.Wrap;
        _corrective.TextWrapping = TextWrapping.Wrap;
        _preventive.TextWrapping = TextWrapping.Wrap;
        _status.ItemsSource = QualityAlertStatuses.All;
        _status.SelectedItem = QualityAlertStatuses.Open;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        content.Children.Add(formScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Quality.AlertsTitle");
        _alertsHeader.Text = T("Quality.AlertsTitle");
        _openOnly.Content = T("Quality.OpenOnly");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _saveButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Quality.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetAlertsAsync(CurrentCompanyCode, _openOnly.IsChecked == true);
            _alerts.Clear();
            _alerts.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _alerts;
            _statusBar.Text = Tf("Quality.AlertsStatus", _alerts.Count,
                _alerts.Count(a => a.Status == QualityAlertStatuses.Open));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcAlertWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnAlertSelected()
    {
        if (_grid.SelectedItem is not QualityAlert alert)
            return;
        _selectedId = alert.Id;
        _message.Text = alert.Message ?? string.Empty;
        _rootCause.Text = alert.RootCause ?? string.Empty;
        _corrective.Text = alert.CorrectiveAction ?? string.Empty;
        _preventive.Text = alert.PreventiveAction ?? string.Empty;
        _status.SelectedItem = alert.Status;
        _owner.Text = alert.Owner ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Quality.SelectAlert"));
            if (!AppHost.Permissions.Has(PermCat.Quality.AlertEdit))
                throw new InvalidOperationException(T("Quality.PermissionDenied"));
            var alert = _alerts.First(a => a.Id == id);
            alert.RootCause = NullIfBlank(_rootCause.Text);
            alert.CorrectiveAction = NullIfBlank(_corrective.Text);
            alert.PreventiveAction = NullIfBlank(_preventive.Text);
            alert.Status = _status.SelectedItem as string ?? QualityAlertStatuses.Open;
            alert.Owner = NullIfBlank(_owner.Text);
            await _service.SaveAlertAsync(CurrentCompanyCode, alert, CurrentUserId);
            _statusBar.Text = T("Quality.AlertSaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "QcAlertWin.SaveAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Helpers (BudgetWin pattern)
    // ------------------------------------------------------------------

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

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        _headers.Add((column, key));
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

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
