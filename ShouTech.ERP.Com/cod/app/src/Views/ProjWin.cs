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
using ShouTech.Domain.Entities;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Projects window (Phase 3): Explorer-style list + editor. The project list is
/// the main content, editing happens in the content area, tasks of the selected
/// project sit below, actions live in the Explorer-style bottom command bar,
/// and a slim status bar at the very bottom shows counts. All writes go
/// through <see cref="ProjectService"/> (validated, transactional, audited).
/// </summary>
public sealed class ProjWin : BaseWin
{
    private readonly ProjectService? _service;
    private readonly List<Project> _projects = new();
    private readonly List<ProjectTask> _tasks = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly DataGrid _taskGrid = new();
    private readonly TextBox _code = new();
    private readonly TextBox _name = new();
    private readonly TextBox _customerId = new();
    private readonly TextBox _customerName = new();
    private readonly ComboBox _status = new();
    private readonly DatePicker _start = new();
    private readonly DatePicker _end = new();
    private readonly TextBox _budget = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _taskName = new();
    private readonly ComboBox _taskStatus = new();
    private readonly DatePicker _taskDue = new();
    private readonly TextBox _taskAssigned = new();
    private readonly TextBox _taskNotes = new();
    private readonly TextBlock _projectsHeader = new();
    private readonly TextBlock _tasksHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _addTaskButton = new();
    private readonly Button _deleteTaskButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;
    private Guid? _selectedTaskId;

    public ProjWin()
    {
        _service = AppHost.Projects;
        Width = 1180;
        Height = 740;
        MinWidth = 980;
        MinHeight = 620;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Projects.View))
        {
            MessageBox.Show(this, T("Projects.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // Slim status bar at the very bottom (Explorer style).
        var statusBorder = new Border
        {
            Child = _statusBar,
            Padding = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);

        // Explorer-style bottom command bar: flat, transparent, top divider.
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_addTaskButton, "Projects.AddTask");
        StyleButton(_deleteTaskButton, "Projects.DeleteTask");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _addTaskButton.Click += async (_, _) => await SaveTaskAsync();
        _deleteTaskButton.Click += async (_, _) => await DeleteTaskAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _addTaskButton, _deleteTaskButton, _refreshButton, _closeButton })
            bar.Children.Add(b);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        // Content: list + editor on top, tasks below.
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(170) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _projectsHeader.FontWeight = FontWeights.SemiBold;
        _projectsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_projectsHeader, Dock.Top);
        listPanel.Children.Add(_projectsHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Projects.Code", nameof(Project.Code), 90));
        _grid.Columns.Add(TextCol("Projects.Name", nameof(Project.Name), 200));
        _grid.Columns.Add(TextCol("Projects.Customer", nameof(Project.CustomerName), 140));
        _grid.Columns.Add(TextCol("Projects.Status", nameof(Project.Status), 90));
        _grid.Columns.Add(TextCol("Projects.StartDate", nameof(Project.StartDate), 95, center: true));
        _grid.Columns.Add(TextCol("Projects.EndDate", nameof(Project.EndDate), 95, center: true));
        _grid.Columns.Add(TextCol("Projects.Budget", nameof(Project.Budget), 110, numeric: true));
        _grid.SelectionChanged += async (_, _) => await OnProjectSelectedAsync();
        listPanel.Children.Add(_grid);
        Grid.SetRow(listPanel, 0);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var editor = new StackPanel();
        var row1 = new WrapPanel();
        row1.Children.Add(Labeled("Projects.Code", _code, 140));
        row1.Children.Add(Labeled("Projects.Status", _status, 150));
        editor.Children.Add(row1);
        editor.Children.Add(Labeled("Projects.Name", _name, 300));
        var row2 = new WrapPanel();
        row2.Children.Add(Labeled("Projects.CustomerId", _customerId, 110));
        row2.Children.Add(Labeled("Projects.Customer", _customerName, 180));
        editor.Children.Add(row2);
        var row3 = new WrapPanel();
        row3.Children.Add(Labeled("Projects.StartDate", _start, 140));
        row3.Children.Add(Labeled("Projects.EndDate", _end, 140));
        editor.Children.Add(row3);
        editor.Children.Add(Labeled("Projects.Budget", _budget, 140));
        editor.Children.Add(Labeled("Projects.Notes", _notes, 300));
        _status.ItemsSource = ProjectStatuses.All;
        _status.SelectedItem = ProjectStatuses.Planned;
        var editorScroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(editorScroll, 0);
        Grid.SetColumn(editorScroll, 1);
        content.Children.Add(editorScroll);

        _tasksHeader.FontWeight = FontWeights.SemiBold;
        _tasksHeader.Margin = new Thickness(0, 10, 0, 6);
        Grid.SetRow(_tasksHeader, 1);
        Grid.SetColumnSpan(_tasksHeader, 2);
        content.Children.Add(_tasksHeader);

        var taskPanel = new Grid();
        taskPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        taskPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        ConfigureGrid(_taskGrid);
        _taskGrid.Columns.Add(TextCol("Projects.TaskName", nameof(ProjectTask.Name), 220));
        _taskGrid.Columns.Add(TextCol("Projects.Status", nameof(ProjectTask.Status), 100));
        _taskGrid.Columns.Add(TextCol("Projects.DueDate", nameof(ProjectTask.DueDate), 100, center: true));
        _taskGrid.Columns.Add(TextCol("Projects.AssignedTo", nameof(ProjectTask.AssignedTo), 130));
        _taskGrid.SelectionChanged += (_, _) => OnTaskSelected();
        var taskGridHost = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        taskGridHost.Children.Add(_taskGrid);
        Grid.SetColumn(taskGridHost, 0);
        taskPanel.Children.Add(taskGridHost);
        var taskEditor = new StackPanel();
        taskEditor.Children.Add(Labeled("Projects.TaskName", _taskName, 300));
        var taskRow = new WrapPanel();
        taskRow.Children.Add(Labeled("Projects.Status", _taskStatus, 140));
        taskRow.Children.Add(Labeled("Projects.DueDate", _taskDue, 140));
        taskEditor.Children.Add(taskRow);
        taskEditor.Children.Add(Labeled("Projects.AssignedTo", _taskAssigned, 200));
        // Task notes — project_tasks.notes + repo support existed, but the
        // window never collected it (tasks always saved with null notes).
        taskEditor.Children.Add(Labeled("Projects.Notes", _taskNotes, 300));
        _taskStatus.ItemsSource = ProjectTaskStatuses.All;
        _taskStatus.SelectedItem = ProjectTaskStatuses.ToDo;
        Grid.SetColumn(taskEditor, 1);
        taskPanel.Children.Add(taskEditor);
        Grid.SetRow(taskPanel, 2);
        Grid.SetColumnSpan(taskPanel, 2);
        content.Children.Add(taskPanel);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Projects.Title");
        _projectsHeader.Text = T("Projects.Title");
        _tasksHeader.Text = T("Projects.Tasks");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _addTaskButton, _deleteTaskButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Projects.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetAllAsync(CurrentCompanyCode);
            _projects.Clear();
            _projects.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _projects;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ProjWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OnProjectSelectedAsync()
    {
        if (_grid.SelectedItem is not Project project)
            return;
        _selectedId = project.Id;
        _code.Text = project.Code;
        _name.Text = project.Name;
        _customerId.Text = project.CustomerId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _customerName.Text = project.CustomerName ?? string.Empty;
        _status.SelectedItem = project.Status;
        _start.SelectedDate = project.StartDate.HasValue ? project.StartDate.Value.ToDateTime(TimeOnly.MinValue) : null;
        _end.SelectedDate = project.EndDate.HasValue ? project.EndDate.Value.ToDateTime(TimeOnly.MinValue) : null;
        _budget.Text = project.Budget.ToString("0.##", CultureInfo.CurrentCulture);
        _notes.Text = project.Notes ?? string.Empty;
        ClearTaskEditor();
        await LoadTasksAsync();
    }

    private async Task LoadTasksAsync()
    {
        _tasks.Clear();
        _taskGrid.ItemsSource = null;
        if (_service is null || !_selectedId.HasValue)
            return;
        try
        {
            var rows = await _service.GetTasksAsync(CurrentCompanyCode, _selectedId.Value);
            _tasks.AddRange(rows);
            _taskGrid.ItemsSource = _tasks;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ProjWin.LoadTasksAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateStatusBar();
    }

    private void OnTaskSelected()
    {
        if (_taskGrid.SelectedItem is not ProjectTask task)
            return;
        _selectedTaskId = task.Id;
        _taskName.Text = task.Name;
        _taskStatus.SelectedItem = task.Status;
        _taskDue.SelectedDate = task.DueDate.HasValue ? task.DueDate.Value.ToDateTime(TimeOnly.MinValue) : null;
        _taskAssigned.Text = task.AssignedTo ?? string.Empty;
        _taskNotes.Text = task.Notes ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Projects.ServiceUnavailable"));
            var isNew = !_selectedId.HasValue;
            if (!AppHost.Permissions.Has(isNew ? PermCat.Projects.Create : PermCat.Projects.Update))
                throw new InvalidOperationException(T("Projects.PermissionDenied"));
            var project = new Project
            {
                Id = _selectedId ?? Guid.NewGuid(),
                Code = _code.Text.Trim(),
                Name = _name.Text.Trim(),
                CustomerId = ParseNullableLong(_customerId.Text),
                CustomerName = NullIfBlank(_customerName.Text),
                Status = _status.SelectedItem as string ?? ProjectStatuses.Planned,
                StartDate = ToDateOnly(_start.SelectedDate),
                EndDate = ToDateOnly(_end.SelectedDate),
                Budget = ParseDecimal(_budget.Text),
                Notes = NullIfBlank(_notes.Text)
            };
            await _service.SaveAsync(CurrentCompanyCode, project, CurrentUserId);
            _selectedId = project.Id;
            MessageBox.Show(this, T("Projects.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ProjWin.SaveAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || !_selectedId.HasValue)
                return;
            if (!AppHost.Permissions.Has(PermCat.Projects.Delete))
                throw new InvalidOperationException(T("Projects.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteAsync(CurrentCompanyCode, _selectedId.Value, CurrentUserId);
            _selectedId = null;
            ClearEditor();
            MessageBox.Show(this, T("Projects.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ProjWin.DeleteAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveTaskAsync()
    {
        try
        {
            if (_service is null || !_selectedId.HasValue)
                return;
            if (!AppHost.Permissions.Has(PermCat.Projects.TaskManage))
                throw new InvalidOperationException(T("Projects.PermissionDenied"));
            var task = new ProjectTask
            {
                Id = _selectedTaskId ?? Guid.NewGuid(),
                ProjectId = _selectedId.Value,
                Name = _taskName.Text.Trim(),
                Status = _taskStatus.SelectedItem as string ?? ProjectTaskStatuses.ToDo,
                DueDate = ToDateOnly(_taskDue.SelectedDate),
                AssignedTo = NullIfBlank(_taskAssigned.Text),
                Notes = NullIfBlank(_taskNotes.Text)
            };
            await _service.SaveTaskAsync(CurrentCompanyCode, task, CurrentUserId);
            ClearTaskEditor();
            await LoadTasksAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ProjWin.SaveTaskAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteTaskAsync()
    {
        try
        {
            if (_service is null || !_selectedTaskId.HasValue)
                return;
            if (!AppHost.Permissions.Has(PermCat.Projects.TaskManage))
                throw new InvalidOperationException(T("Projects.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteTaskAsync(CurrentCompanyCode, _selectedTaskId.Value, CurrentUserId);
            ClearTaskEditor();
            await LoadTasksAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ProjWin.DeleteTaskAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _code.Clear();
        _name.Clear();
        _customerId.Clear();
        _customerName.Clear();
        _status.SelectedItem = ProjectStatuses.Planned;
        _start.SelectedDate = null;
        _end.SelectedDate = null;
        _budget.Clear();
        _notes.Clear();
        _tasks.Clear();
        _taskGrid.ItemsSource = null;
        ClearTaskEditor();
        UpdateStatusBar();
    }

    private void ClearTaskEditor()
    {
        _selectedTaskId = null;
        _taskGrid.SelectedItem = null;
        _taskName.Clear();
        _taskStatus.SelectedItem = ProjectTaskStatuses.ToDo;
        _taskDue.SelectedDate = null;
        _taskAssigned.Clear();
        _taskNotes.Clear();
    }

    private void UpdateStatusBar()
    {
        var totalBudget = _projects.Sum(p => p.Budget);
        _statusBar.Text = $"{_projects.Count} · {T("Projects.Budget")}: {totalBudget:N2} · {_tasks.Count} {T("Projects.Tasks")}";
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

    private StackPanel Labeled(string key, Control control, double width = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        if (width > 0)
            panel.Width = width;
        var label = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _labels.Add((label, key));
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = 92;
        button.MinHeight = 32;
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Padding = new Thickness(12, 3, 12, 3);
    }

    private static DateOnly? ToDateOnly(DateTime? value)
        => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? ParseNullableLong(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ||
            long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Customer ID must be a whole number.");
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
