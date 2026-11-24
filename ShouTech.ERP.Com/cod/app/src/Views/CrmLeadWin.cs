using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Configuration;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// CRM leads window (Phase B 057, design section 4.2's CrmLeadWin):
/// lead list + card editor on the PartyManagerWin pattern. A lead is
/// its own entity (decision C1) — nothing here writes parties. The
/// Convert action (CRM.LEAD.CONVERT) runs CrmService.ConvertLeadAsync:
/// ONE transaction that creates the party in the unified master,
/// creates the opportunity at the first Open stage, and stamps both
/// links on the lead. Converted leads are read-only in this window.
/// </summary>
public sealed class CrmLeadWin : BaseWin
{
    protected override string? ScheduleTintName => "Orchid";

    private readonly CrmService? _service;
    private readonly List<CrmLead> _leads = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _leadsHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBox _name = new();
    private readonly TextBox _organization = new();
    private readonly TextBox _email = new();
    private readonly TextBox _phone = new();
    private readonly TextBox _mobile = new();
    private readonly TextBox _address = new();
    private readonly ComboBox _source = new();
    private readonly TextBox _campaign = new();
    private readonly ComboBox _status = new();
    private readonly ComboBox _rep = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _convertedParty = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _convertButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;

    public CrmLeadWin()
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
        if (!AppHost.Permissions.Has(PermCat.Crm.LeadView))
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
        StyleButton(_convertButton, "Crm.Convert");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _convertButton.Click += async (_, _) => await ConvertAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _convertButton, _deleteButton, _refreshButton, _closeButton })
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
        _leadsHeader.FontWeight = FontWeights.SemiBold;
        _leadsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_leadsHeader, Dock.Top);
        listPanel.Children.Add(_leadsHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Crm.Col.LeadName", nameof(CrmLead.LeadName), 150));
        _grid.Columns.Add(TextCol("Crm.Col.Organization", nameof(CrmLead.Organization), 140));
        _grid.Columns.Add(TextCol("Crm.Col.Source", nameof(CrmLead.Source), 90));
        _grid.Columns.Add(TextCol("Crm.Col.Status", nameof(CrmLead.Status), 90));
        _grid.Columns.Add(TextCol("Crm.Col.Rep", nameof(CrmLead.AssignedRepCode), 80));
        _grid.SelectionChanged += (_, _) => OnLeadSelected();
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Crm.Col.LeadName", _name);
        AddFormField(_labels, form, 1, 0, "Crm.Col.Organization", _organization);
        AddFormField(_labels, form, 2, 0, "Crm.Email", _email);
        AddFormField(_labels, form, 3, 0, "Crm.Phone", _phone);
        AddFormField(_labels, form, 4, 0, "Crm.Mobile", _mobile);
        AddFormField(_labels, form, 5, 0, "Crm.Address", _address);
        AddFormField(_labels, form, 6, 0, "Crm.Col.Source", _source);
        AddFormField(_labels, form, 7, 0, "Crm.Campaign", _campaign);
        AddFormField(_labels, form, 8, 0, "Crm.Col.Status", _status);
        AddFormField(_labels, form, 9, 0, "Crm.Col.Rep", _rep);
        AddFormField(_labels, form, 10, 0, "Crm.Notes", _notes);
        AddFormField(_labels, form, 11, 0, "Crm.ConvertedParty", _convertedParty);
        _source.ItemsSource = CrmSources.All;
        _source.IsEditable = true;
        _status.ItemsSource = CrmLeadStatuses.All;
        _status.SelectedItem = CrmLeadStatuses.New;
        _rep.IsEditable = true;
        _notes.TextWrapping = TextWrapping.Wrap;
        _notes.MinHeight = 44;
        _convertedParty.IsReadOnly = true;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        content.Children.Add(formScroll);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Crm.LeadsTitle");
        _leadsHeader.Text = T("Crm.LeadsTitle");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _convertButton, _deleteButton, _refreshButton, _closeButton })
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
            var rows = await _service.GetLeadsAsync(CurrentCompanyCode);
            _leads.Clear();
            _leads.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _leads;
            var reps = await _service.GetRepOptionsAsync(CurrentCompanyCode);
            _rep.ItemsSource = reps.Select(r => r.Code).ToList();
            _statusBar.Text = Tf("Crm.LeadsStatus", _leads.Count,
                _leads.Count(l => l.Status == CrmLeadStatuses.Converted));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmLeadWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnLeadSelected()
    {
        if (_grid.SelectedItem is not CrmLead lead)
            return;
        _selectedId = lead.Id;
        _name.Text = lead.LeadName;
        _organization.Text = lead.Organization ?? string.Empty;
        _email.Text = lead.Email ?? string.Empty;
        _phone.Text = lead.Phone ?? string.Empty;
        _mobile.Text = lead.Mobile ?? string.Empty;
        _address.Text = lead.Address ?? string.Empty;
        _source.Text = lead.Source ?? string.Empty;
        _campaign.Text = lead.Campaign ?? string.Empty;
        _status.SelectedItem = lead.Status;
        _rep.Text = lead.AssignedRepCode ?? string.Empty;
        _notes.Text = lead.Notes ?? string.Empty;
        _convertedParty.Text = lead.ConvertedPartyId?.ToString("D") ?? string.Empty;
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _name.Text = string.Empty;
        _organization.Text = string.Empty;
        _email.Text = string.Empty;
        _phone.Text = string.Empty;
        _mobile.Text = string.Empty;
        _address.Text = string.Empty;
        _source.Text = string.Empty;
        _campaign.Text = string.Empty;
        _status.SelectedItem = CrmLeadStatuses.New;
        _rep.Text = string.Empty;
        _notes.Text = string.Empty;
        _convertedParty.Text = string.Empty;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Crm.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Crm.LeadEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var existing = _selectedId is Guid id ? _leads.FirstOrDefault(l => l.Id == id) : null;
            if (existing?.Status == CrmLeadStatuses.Converted)
                throw new InvalidOperationException(T("Crm.LeadConvertedReadOnly"));
            var lead = existing ?? new CrmLead();
            lead.LeadName = _name.Text.Trim();
            lead.Organization = NullIfBlank(_organization.Text);
            lead.Email = NullIfBlank(_email.Text);
            lead.Phone = NullIfBlank(_phone.Text);
            lead.Mobile = NullIfBlank(_mobile.Text);
            lead.Address = NullIfBlank(_address.Text);
            lead.Source = NullIfBlank(_source.Text);
            lead.Campaign = NullIfBlank(_campaign.Text);
            lead.Status = _status.SelectedItem as string ?? CrmLeadStatuses.New;
            lead.AssignedRepCode = NullIfBlank(_rep.Text);
            lead.Notes = NullIfBlank(_notes.Text);
            await _service.SaveLeadAsync(CurrentCompanyCode, lead, CurrentUserId);
            _selectedId = lead.Id;
            _statusBar.Text = T("Crm.LeadSaved");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmLeadWin.SaveAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ConvertAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectLead"));
            if (!AppHost.Permissions.Has(PermCat.Crm.LeadConvert))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            var lead = _leads.FirstOrDefault(l => l.Id == id)
                ?? throw new InvalidOperationException(T("Crm.SelectLead"));

            // Duplicate detection (gap G1/G2): when the lead's email or
            // phone already belongs to an existing PARTY, offer to link
            // that party instead of creating a duplicate. A duplicate
            // LEAD (or no match) keeps the normal path, where the
            // service's CRM-9007 check is the backstop.
            var duplicate = await _service.FindLeadDuplicateAsync(CurrentCompanyCode, lead);
            CrmConversionResult result;
            if (duplicate is { SourceKind: "Party" })
            {
                var choice = MessageBox.Show(this,
                    Tf("Crm.LinkExistingParty", duplicate.Describe()),
                    T("Crm.LinkExistingTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                result = choice == MessageBoxResult.Yes
                    ? await _service.ConvertLeadToExistingPartyAsync(CurrentCompanyCode, id, duplicate.MatchId, CurrentUserId,
                        UsrPref.Load().CurrencyCode)
                    : await _service.ConvertLeadAsync(CurrentCompanyCode, id, CurrentUserId,
                        UsrPref.Load().CurrencyCode);
            }
            else
            {
                result = await _service.ConvertLeadAsync(CurrentCompanyCode, id, CurrentUserId,
                    UsrPref.Load().CurrencyCode);
            }
            _statusBar.Text = Tf("Crm.ConvertedLine", result.PartyCode);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, "CrmLeadWin.ConvertAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Crm.SelectLead"));
            if (!AppHost.Permissions.Has(PermCat.Crm.LeadEdit))
                throw new InvalidOperationException(T("Crm.PermissionDenied"));
            await _service.DeleteLeadAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "CrmLeadWin.DeleteAsync");
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
