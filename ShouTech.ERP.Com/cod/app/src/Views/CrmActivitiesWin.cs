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
/// CRM activities worklist (Phase B 057, design section 4.2's
/// CrmActivitiesWin): every follow-up (call / meeting / email / task)
/// against a lead, an opportunity or a party, with due dates. The
/// Overdue filter is the overdue surface the design requires: Planned
/// activities whose due moment has passed, straight from
/// CrmService.GetOverdueActivitiesAsync. Completing an activity stamps
/// it Done with its result notes and it leaves the open worklist.
/// </summary>
public sealed class CrmActivitiesWin : BaseWin
{
    protected override string? ScheduleTintName => "Coral";

    private readonly CrmService? _service;
    private readonly List<CrmActivity> _activities = new();
    private readonly List<CrmLead> _leads = new();
    private readonly List<CrmOpportunity> _opportunities = new();
    private readonly List<CrmPartyOption> _parties = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _activitiesHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly CheckBox _openOnly = new();
    private readonly CheckBox _overdueOnly = new();
    private readonly TextBox _subject = new();
    private readonly ComboBox _type = new();
    private readonly DatePicker _dueDate = new();
    private readonly TextBox _dueTime = new();
    private readonly ComboBox _targetKind = new();
    private readonly ComboBox _target = new();
    private readonly ComboBox _status = new();
    private readonly TextBox _assignedTo = new();
    private readonly TextBox _resultNotes = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _completeButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;
    private bool _loadingTargets;

    public CrmActivitiesWin()
    {
        _service = AppHost.Crm;
        Width = 1080;
        Height = 640;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Crm.ActivityView))
        {
            MessageBox.Show(this, T("Crm.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_completeButton, "Crm.Complete");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _completeButton.Click += async (_, _) => await CompleteAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _completeButton, _deleteButton, _refreshButton, _closeButton })
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
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });

        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var listHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _activitiesHeader.FontWeight = FontWeights.SemiBold;
        listHeader.Children.Add(_activitiesHeader);
        _openOnly.Margin = new Thickness(16, 0, 0, 0);
        _openOnly.IsChecked = true;
        _openOnly.Checked += async (_, _) => await RefreshAsync();
        _openOnly.Unchecked += async (_, _) => await RefreshAsync();
        listHeader.Children.Add(_openOnly);
        _overdueOnly.Margin = new Thickness(16, 0, 0, 0);
        _overdueOnly.Checked += async (_, _) => await RefreshAsync();
        _overdueOnly.Unchecked += async (_, _) => await RefreshAsync();
        listHeader.Children.Add(_overdueOnly);
        DockPanel.SetDock(listHeader, Dock.Top);
        listPanel.Children.Add(listHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Crm.Col.Subject", nameof(CrmActivity.Subject), 200));
        _grid.Columns.Add(TextCol("Crm.Col.ActivityType", nameof(CrmActivity.ActivityType), 80));
        _grid.Columns.Add(TextCol("Crm.Col.DueAt", nameof(CrmActivity.DueAt), 120));
        _grid.Columns.Add(TextCol("Crm.Col.Status", nameof(CrmActivity.Status), 80));
        _grid.Columns.Add(TextCol("Crm.Col.AssignedTo", nameof(CrmActivity.AssignedTo), 100));
        _grid.SelectionChanged += (_, _) => OnActivitySelected();
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Crm.Col.Subject", _subject);
        AddFormField(_labels, form, 1, 0, "Crm.Col.ActivityType", _type);
        AddFormField(_labels, form, 2, 0, "Crm.DueDate", _dueDate);
        AddFormField(_labels, form, 3, 0, "Crm.DueTime", _dueTime);
        AddFormField(_labels, form, 4, 0, "Crm.TargetKind", _targetKind);
        AddFormField(_labels, form, 5, 0, "Crm.Target", _target);
        AddFormField(_labels, form, 6, 0, "Crm.Col.Status", _status);
        AddFormField(_labels, form, 7, 0, "Crm.Col.AssignedTo", _assignedTo);
        AddFormField(_labels, form, 8, 0, "Crm.ResultNotes", _resultNotes);
        _type.ItemsSource = CrmActivityTypes.All;
        _type.SelectedItem = CrmActivityTypes.Task;
        _status.ItemsSource = CrmActivityStatuses.All;
        _status.SelectedItem = CrmActivityStatuses.Planned;
        _targetKind.ItemsSource = new[] { "Lead", "Opportunity", "Party" };
        _targetKind.SelectedItem = "Opportunity";
        _targetKind.SelectionChanged += (_, _) => PopulateTargets();
        _dueDate.SelectedDate = DateTime.Today;
        _dueTime.Text = "09:00";
        _resultNotes.TextWrapping = TextWrapping.Wrap;
        _resultNotes.MinHeight = 44;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        content.Children.Add(formScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Crm.ActivitiesTitle");
        _activitiesHeader.Text = T("Crm.ActivitiesTitle");
        _openOnly.Content = T("Crm.OpenOnly");
        _overdueOnly.Content = T("Crm.OverdueOnly");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _completeButton, _deleteButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Crm.ServiceUnavailable");
                return;
            }
            var leads = await _service.GetLeadsAsync(CurrentCompanyCode);
            _leads.Clear();
            _leads.AddRange(leads);
            var opps = await _service.GetOpportunitiesAsync(CurrentCompanyCode);
            _opportunities.Clear();
            _opportunities.AddRange(opps);
            var parties = await _service.GetPartyOptionsAsync(CurrentCompanyCode);
            _parties.Clear();
            _parties.AddRange(parties);
            PopulateTargets();

            IReadOnlyList<CrmActivity> rows;
            if (_overdueOnly.IsChecked == true)
            {
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                rows = await _service.GetOverdueActivitiesAsync(CurrentCompanyCode, now);
            }
            else
            {
                rows = await _service.GetActivitiesAsync(CurrentCompanyCode, _openOnly.IsChecked == true);
            }
            _activities.Clear();
            _activities.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _activities;
            var nowText = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            _statusBar.Text = Tf("Crm.ActivitiesStatus", _activities.Count,
                _activities.Count(a => a.IsOverdue(nowText)));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmActivitiesWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PopulateTargets()
    {
        if (_loadingTargets) return;
        var kind = _targetKind.SelectedItem as string ?? "Opportunity";
        _target.DisplayMemberPath = kind == "Party" ? nameof(CrmPartyOption.Name)
            : kind == "Lead" ? nameof(CrmLead.LeadName)
            : nameof(CrmOpportunity.Title);
        _target.ItemsSource = kind switch
        {
            "Lead" => _leads,
            "Party" => _parties,
            _ => _opportunities
        };
    }

    private void OnActivitySelected()
    {
        if (_grid.SelectedItem is not CrmActivity activity)
            return;
        _selectedId = activity.Id;
        _subject.Text = activity.Subject;
        _type.SelectedItem = activity.ActivityType;
        if (DateTime.TryParse(activity.DueAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
        {
            _dueDate.SelectedDate = due.Date;
            _dueTime.Text = due.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        _loadingTargets = true;
        _targetKind.SelectedItem = activity.LeadId is not null ? "Lead" : activity.PartyId is not null ? "Party" : "Opportunity";
        _loadingTargets = false;
        PopulateTargets();
        _target.SelectedItem = activity.LeadId is not null
            ? _leads.FirstOrDefault(l => l.Id == activity.LeadId)
            : activity.PartyId is not null
                ? _parties.FirstOrDefault(p => p.Id == activity.PartyId)
                : _opportunities.FirstOrDefault(o => o.Id == activity.OppId);
        _status.SelectedItem = activity.Status;
        _assignedTo.Text = activity.AssignedTo ?? string.Empty;
        _resultNotes.Text = activity.ResultNotes ?? string.Empty;
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _subject.Text = string.Empty;
        _type.SelectedItem = CrmActivityTypes.Task;
        _dueDate.SelectedDate = DateTime.Today;
        _dueTime.Text = "09:00";
        _status.SelectedItem = CrmActivityStatuses.Planned;
        _assignedTo.Text = string.Empty;
        _resultNotes.Text = string.Empty;
        _target.SelectedItem = null;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Crm.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Crm.ActivityEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var existing = _selectedId is Guid id ? _activities.FirstOrDefault(a => a.Id == id) : null;
            var activity = existing ?? new CrmActivity();
            activity.Subject = _subject.Text.Trim();
            activity.ActivityType = _type.SelectedItem as string ?? CrmActivityTypes.Task;
            var date = _dueDate.SelectedDate ?? DateTime.Today;
            var time = TimeSpan.TryParse(_dueTime.Text.Trim(), CultureInfo.InvariantCulture, out var parsed) ? parsed : new TimeSpan(9, 0, 0);
            activity.DueAt = date.Add(time).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            activity.Status = _status.SelectedItem as string ?? CrmActivityStatuses.Planned;
            activity.AssignedTo = NullIfBlank(_assignedTo.Text);
            activity.ResultNotes = NullIfBlank(_resultNotes.Text);
            activity.LeadId = null;
            activity.OppId = null;
            activity.PartyId = null;
            switch (_target.SelectedItem)
            {
                case CrmLead lead: activity.LeadId = lead.Id; break;
                case CrmOpportunity opp: activity.OppId = opp.Id; break;
                case CrmPartyOption party: activity.PartyId = party.Id; break;
                default: throw new InvalidOperationException(T("Crm.SelectTarget"));
            }
            await _service.SaveActivityAsync(CurrentCompanyCode, activity, CurrentUserId);
            _selectedId = activity.Id;
            _statusBar.Text = T("Crm.ActivitySaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmActivitiesWin.SaveAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CompleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectActivity"));
            if (!AppHost.Permissions.Has(PermCat.Crm.ActivityEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            await _service.CompleteActivityAsync(CurrentCompanyCode, id, NullIfBlank(_resultNotes.Text), CurrentUserId);
            _statusBar.Text = T("Crm.ActivityCompleted");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmActivitiesWin.CompleteAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectActivity"));
            if (!AppHost.Permissions.Has(PermCat.Crm.ActivityEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            await _service.DeleteActivityAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmActivitiesWin.DeleteAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Helpers (QcAlertWin pattern)
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
