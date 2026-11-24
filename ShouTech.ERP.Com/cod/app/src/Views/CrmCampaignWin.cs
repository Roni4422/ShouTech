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
/// CRM campaigns window (sales Level 3, migration 105, workstream
/// W10): the campaigns master (name, kind, window, budget, target
/// notes, status) plus the outcome roll-up — leads, opportunities and
/// won value per campaign, aggregated on the campaign_id links of
/// crm_leads / crm_opportunities. Leads and opportunities pick their
/// campaign in their own card windows (the campaign combo there);
/// this window is the master + reporting surface, in the CommWin /
/// CrmLeadWin shape (tabs, bottom action bar, read-only grids).
/// RBAC: CRM.CAMPAIGN.VIEW gates the window, CRM.CAMPAIGN.EDIT the
/// edits. A separate master window (not a section inside CrmLeadWin /
/// CrmOppWin) because those are single-record card windows with no
/// list surface to host a master grid + roll-up.
/// </summary>
public sealed class CrmCampaignWin : BaseWin
{
    protected override string? ScheduleTintName => "Rose";

    private readonly CrmService? _service;
    private readonly List<CrmCampaign> _campaigns = new();
    private readonly List<CrmCampaignOutcome> _outcomes = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TabControl _tabs = new();
    private readonly TabItem _campaignsTab = new();
    private readonly TabItem _outcomesTab = new();
    private readonly TextBlock _statusBar = new();

    private readonly DataGrid _grid = new();
    private readonly TextBox _name = new();
    private readonly ComboBox _kind = new();
    private readonly DatePicker _start = new();
    private readonly DatePicker _end = new();
    private readonly TextBox _budget = new();
    private readonly ComboBox _status = new();
    private readonly TextBox _targetNotes = new();
    private Guid? _selectedId;

    private readonly DataGrid _outcomeGrid = new();

    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public CrmCampaignWin()
    {
        _service = AppHost.Crm;
        Width = 1080;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Crm.CampaignView))
        {
            MessageBox.Show(this, T("Campaign.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _refreshButton, _closeButton })
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

        _campaignsTab.Content = BuildCampaignsTab();
        _outcomesTab.Content = BuildOutcomesTab();
        _tabs.Items.Add(_campaignsTab);
        _tabs.Items.Add(_outcomesTab);
        root.Children.Add(_tabs);

        Content = root;
        ApplyLocalizedText();
    }

    private DockPanel BuildCampaignsTab()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });

        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Campaign.Col.Name", nameof(CrmCampaign.Name), 170));
        _grid.Columns.Add(TextCol("Campaign.Col.Kind", nameof(CrmCampaign.Kind), 90));
        _grid.Columns.Add(TextCol("Campaign.Col.Status", nameof(CrmCampaign.Status), 90));
        _grid.Columns.Add(TextCol("Campaign.Col.Budget", nameof(CrmCampaign.Budget), 90));
        _grid.Columns.Add(TextCol("Campaign.Col.Start", nameof(CrmCampaign.StartDate), 95));
        _grid.Columns.Add(TextCol("Campaign.Col.End", nameof(CrmCampaign.EndDate), 95));
        _grid.SelectionChanged += (_, _) => OnCampaignSelected();
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Campaign.Col.Name", _name);
        AddFormField(_labels, form, 1, 0, "Campaign.Col.Kind", _kind);
        AddFormField(_labels, form, 2, 0, "Campaign.Col.Start", _start);
        AddFormField(_labels, form, 3, 0, "Campaign.Col.End", _end);
        AddFormField(_labels, form, 4, 0, "Campaign.Col.Budget", _budget);
        AddFormField(_labels, form, 5, 0, "Campaign.Col.Status", _status);
        AddFormField(_labels, form, 6, 0, "Campaign.TargetNotes", _targetNotes);
        _kind.ItemsSource = CrmCampaignKinds.All;
        _kind.SelectedItem = CrmCampaignKinds.Marketing;
        _status.ItemsSource = CrmCampaignStatuses.All;
        _status.SelectedItem = CrmCampaignStatuses.Planned;
        _targetNotes.TextWrapping = TextWrapping.Wrap;
        _targetNotes.AcceptsReturn = true;
        _targetNotes.MinHeight = 56;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        content.Children.Add(formScroll);

        panel.Children.Add(content);
        return panel;
    }

    private DockPanel BuildOutcomesTab()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };
        ConfigureGrid(_outcomeGrid);
        _outcomeGrid.Columns.Add(TextCol("Campaign.Col.Name", nameof(CrmCampaignOutcome.Name), 200));
        _outcomeGrid.Columns.Add(TextCol("Campaign.Col.Kind", nameof(CrmCampaignOutcome.Kind), 100));
        _outcomeGrid.Columns.Add(TextCol("Campaign.Col.Status", nameof(CrmCampaignOutcome.Status), 100));
        _outcomeGrid.Columns.Add(TextCol("Campaign.Col.Budget", nameof(CrmCampaignOutcome.Budget), 100));
        _outcomeGrid.Columns.Add(TextCol("Campaign.Col.Leads", nameof(CrmCampaignOutcome.LeadCount), 80));
        _outcomeGrid.Columns.Add(TextCol("Campaign.Col.Opportunities", nameof(CrmCampaignOutcome.OpportunityCount), 110));
        _outcomeGrid.Columns.Add(TextCol("Campaign.Col.WonValue", nameof(CrmCampaignOutcome.WonValue), 130));
        panel.Children.Add(_outcomeGrid);
        return panel;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Campaign.Title");
        _campaignsTab.Header = T("Campaign.Campaigns");
        _outcomesTab.Header = T("Campaign.Outcomes");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    // ------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------
    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Crm.ServiceUnavailable"); return; }
            var rows = await _service.GetCampaignsAsync(CurrentCompanyCode);
            _campaigns.Clear();
            _campaigns.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _campaigns;
            var outcomes = await _service.GetCampaignOutcomesAsync(CurrentCompanyCode);
            _outcomes.Clear();
            _outcomes.AddRange(outcomes);
            _outcomeGrid.ItemsSource = null;
            _outcomeGrid.ItemsSource = _outcomes;
            _statusBar.Text = Tf("Campaign.StatusLine", _campaigns.Count);
        }
        catch (Exception ex) { Report(ex, "CrmCampaignWin.RefreshAsync"); }
    }

    private void OnCampaignSelected()
    {
        if (_grid.SelectedItem is not CrmCampaign campaign) return;
        _selectedId = campaign.Id;
        _name.Text = campaign.Name;
        _kind.SelectedItem = campaign.Kind;
        _start.SelectedDate = ParseDate(campaign.StartDate);
        _end.SelectedDate = ParseDate(campaign.EndDate);
        _budget.Text = campaign.Budget.ToString(CultureInfo.InvariantCulture);
        _status.SelectedItem = campaign.Status;
        _targetNotes.Text = campaign.TargetNotes ?? string.Empty;
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _name.Text = string.Empty;
        _kind.SelectedItem = CrmCampaignKinds.Marketing;
        _start.SelectedDate = null;
        _end.SelectedDate = null;
        _budget.Text = "0";
        _status.SelectedItem = CrmCampaignStatuses.Planned;
        _targetNotes.Text = string.Empty;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Crm.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Crm.CampaignEdit))
                throw new InvalidOperationException(T("Campaign.PermissionDenied"));
            var existing = _selectedId is Guid id ? _campaigns.FirstOrDefault(c => c.Id == id) : null;
            var campaign = existing ?? new CrmCampaign();
            campaign.Name = _name.Text.Trim();
            campaign.Kind = _kind.SelectedItem as string ?? CrmCampaignKinds.Marketing;
            campaign.StartDate = DateText(_start);
            campaign.EndDate = DateText(_end);
            campaign.Budget = ParseDecimal(_budget.Text);
            campaign.Status = _status.SelectedItem as string ?? CrmCampaignStatuses.Planned;
            campaign.TargetNotes = NullIfBlank(_targetNotes.Text);
            await _service.SaveCampaignAsync(CurrentCompanyCode, campaign, CurrentUserId);
            _selectedId = campaign.Id;
            _statusBar.Text = T("Campaign.Saved");
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "CrmCampaignWin.SaveAsync"); }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Campaign.SelectCampaign"));
            if (!AppHost.Permissions.Has(PermCat.Crm.CampaignEdit))
                throw new InvalidOperationException(T("Campaign.PermissionDenied"));
            await _service.DeleteCampaignAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "CrmCampaignWin.DeleteAsync"); }
    }

    // ------------------------------------------------------------------
    // Helpers (CommWin pattern)
    // ------------------------------------------------------------------
    private void Report(Exception ex, string context)
    {
        AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, context);
        MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

    private static string? DateText(DatePicker picker)
        => picker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static decimal ParseDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
}
