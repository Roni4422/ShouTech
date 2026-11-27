using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Activity Log window: two read-only tabs — Security events
/// (security_audit_log, migration 029) and Journal audit events
/// (journal_audit_events, 001 core) — with a date-range filter. Data comes
/// through OperationsService; nothing here writes.
/// </summary>
public sealed class ActivityLogWin : BaseWin
{
    protected override string? ScheduleTintName => "Olive";
    private readonly OperationsService? _service;
    private readonly List<SecurityAuditRow> _security = new();
    private readonly List<JournalAuditRow> _journal = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DatePicker _from = new();
    private readonly DatePicker _to = new();
    private readonly TextBox _filterText = new();
    private readonly TabControl _tabs = new();
    private readonly TabItem _securityTab = new();
    private readonly TabItem _journalTab = new();
    private readonly DataGrid _securityGrid = new();
    private readonly DataGrid _journalGrid = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public ActivityLogWin()
    {
        _service = AppHost.Operations;
        Width = 1120;
        Height = 640;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Audit.View) && !AppHost.Permissions.Has(PermCat.Audit.SecurityEventsView))
        {
            MessageBox.Show(this, T("ActivityLog.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _from.SelectedDate = DateTime.Today.AddDays(-30);
        _to.SelectedDate = DateTime.Today;
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_refreshButton, _closeButton));

        var content = new DockPanel();
        var filter = F2Ui.CreateFormGrid(2);
        filter.Margin = new Thickness(0, 0, 0, 8);
        F2Ui.AddField(_labels, filter, 0, 0, "ActivityLog.From", _from);
        F2Ui.AddField(_labels, filter, 0, 1, "ActivityLog.To", _to);
        _filterText.Width = 220;
        _filterText.TextChanged += (_, _) => ApplyFilter();
        F2Ui.AddField(_labels, filter, 1, 0, "ActivityLog.Filter", _filterText);
        DockPanel.SetDock(filter, Dock.Top);
        content.Children.Add(filter);

        F2Ui.ConfigureGrid(_securityGrid);
        _securityGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.Time", nameof(SecurityAuditRow.OccurredAt), 160, center: true));
        _securityGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.User", nameof(SecurityAuditRow.Username), 140));
        _securityGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.Event", nameof(SecurityAuditRow.EventType), 180));
        _securityGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.Details", nameof(SecurityAuditRow.Details), 460));
        _securityTab.Content = _securityGrid;
        _tabs.Items.Add(_securityTab);

        F2Ui.ConfigureGrid(_journalGrid);
        _journalGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.Time", nameof(JournalAuditRow.EventAt), 160, center: true));
        _journalGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.Entry", nameof(JournalAuditRow.EntryId), 200));
        _journalGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.Event", nameof(JournalAuditRow.EventType), 90, center: true));
        _journalGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.User", nameof(JournalAuditRow.Actor), 140));
        _journalGrid.Columns.Add(F2Ui.TextCol(_headers, "ActivityLog.Details", nameof(JournalAuditRow.Details), 380));
        _journalTab.Content = _journalGrid;
        _tabs.Items.Add(_journalTab);

        content.Children.Add(_tabs);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("ActivityLog.Title");
        _securityTab.Header = T("ActivityLog.SecurityTab");
        _journalTab.Header = T("ActivityLog.JournalTab");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _refreshButton, _closeButton }, T);
        UpdateStatus();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("ActivityLog.ServiceUnavailable");
                return;
            }
            var from = _from.SelectedDate is DateTime f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
            var to = _to.SelectedDate is DateTime t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
            var security = await _service.GetSecurityAuditAsync(CurrentCompanyCode, from, to);
            var journal = await _service.GetJournalAuditAsync(CurrentCompanyCode, from, to);
            _security.Clear();
            _security.AddRange(security);
            _journal.Clear();
            _journal.AddRange(journal);
            ApplyFilter();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "ActivityLogWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Client-side user/event/details filter over the
    /// loaded rows (R5 — the audit-review filter shape the
    /// benchmarks converge on).</summary>
    private void ApplyFilter()
    {
        var text = _filterText.Text.Trim();
        bool Hit(string? value) => text.Length == 0 || (value?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
        _securityGrid.ItemsSource = null;
        _securityGrid.ItemsSource = text.Length == 0
            ? _security
            : _security.Where(r => Hit(r.Username) || Hit(r.EventType) || Hit(r.Details)).ToList();
        _journalGrid.ItemsSource = null;
        _journalGrid.ItemsSource = text.Length == 0
            ? _journal
            : _journal.Where(r => Hit(r.Actor) || Hit(r.EventType) || Hit(r.EntryId) || Hit(r.Details)).ToList();
    }

    // The selected tab is part of the user's window state
    // (WINSTATE hooks): reopening lands on the tab last used.
    protected override void OnRestoreWindowState()
    {
        if (int.TryParse(PrefGet("Tab"), out var tab) && tab >= 0 && tab < _tabs.Items.Count)
            _tabs.SelectedIndex = tab;
    }

    protected override void OnSaveWindowState()
        => PrefSet("Tab", _tabs.SelectedIndex.ToString(CultureInfo.InvariantCulture));

    private void UpdateStatus()
        => _statusBar.Text = $"{T("ActivityLog.SecurityTab")}: {_security.Count} · {T("ActivityLog.JournalTab")}: {_journal.Count}";
}
