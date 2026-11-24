using System;
using System.Collections.Generic;
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
/// POS sync / outbox window: a real viewer over outbound_messages
/// (migration 006) — channel, recipient, subject, status, attempts, last
/// error and schedule. Retry performs a real write: a FAILED row is reset to
/// QUEUED with attempts cleared so the sync engine picks it up again.
/// </summary>
public sealed class PosSyncWin : BaseWin
{
    protected override string? ScheduleTintName => "Harbor";
    private readonly OperationsService? _service;
    private readonly List<OutboundMessageRow> _rows = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBlock _statusBar = new();
    private readonly ComboBox _statusFilter = new();
    private readonly Button _retryButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public PosSyncWin()
    {
        _service = AppHost.Operations;
        Width = 1080;
        Height = 600;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Pos.SyncExecute))
        {
            MessageBox.Show(this, T("PosSync.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_retryButton, "PosSync.Retry");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _retryButton.Click += async (_, _) => await RetryAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_retryButton, _refreshButton, _closeButton));

        // Status filter strip (R5): All / Queued / Sent / Failed
        // over the loaded outbox rows.
        var filterStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var filterLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _labels.Add((filterLabel, "PosSync.Status"));
        filterStrip.Children.Add(filterLabel);
        _statusFilter.Width = 160;
        _statusFilter.Items.Add(new ComboBoxItem { Tag = "", Content = T("PosSync.All") });
        _statusFilter.Items.Add(new ComboBoxItem { Tag = "QUEUED", Content = T("PosSync.Queued") });
        _statusFilter.Items.Add(new ComboBoxItem { Tag = "SENT", Content = T("PosSync.Sent") });
        _statusFilter.Items.Add(new ComboBoxItem { Tag = "FAILED", Content = T("PosSync.Failed") });
        _statusFilter.SelectedIndex = 0;
        _statusFilter.SelectionChanged += (_, _) => ApplyFilter();
        filterStrip.Children.Add(_statusFilter);
        DockPanel.SetDock(filterStrip, Dock.Top);
        root.Children.Add(filterStrip);

        F2Ui.ConfigureGrid(_grid);
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.Channel", nameof(OutboundMessageRow.Channel), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.Recipient", nameof(OutboundMessageRow.Recipient), 190));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.Subject", nameof(OutboundMessageRow.Subject), 220));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.Status", nameof(OutboundMessageRow.Status), 90, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.Attempts", nameof(OutboundMessageRow.Attempts), 80, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.LastError", nameof(OutboundMessageRow.LastError), 240));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.Created", nameof(OutboundMessageRow.CreatedAt), 150, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosSync.NextAttempt", nameof(OutboundMessageRow.NextAttemptAt), 150, center: true));
        root.Children.Add(_grid);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("PosSync.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _retryButton, _refreshButton, _closeButton }, T);
        foreach (ComboBoxItem item in _statusFilter.Items)
        {
            item.Content = (item.Tag as string) switch
            {
                "QUEUED" => T("PosSync.Queued"),
                "SENT" => T("PosSync.Sent"),
                "FAILED" => T("PosSync.Failed"),
                _ => T("PosSync.All")
            };
        }
        UpdateStatus();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("PosSync.ServiceUnavailable");
                return;
            }
            var rows = await _service.GetOutboundMessagesAsync(CurrentCompanyCode);
            _rows.Clear();
            _rows.AddRange(rows);
            ApplyFilter();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSyncWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyFilter()
    {
        var status = (_statusFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        _grid.ItemsSource = null;
        _grid.ItemsSource = status.Length == 0
            ? _rows
            : _rows.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void UpdateStatus()
    {
        var failed = 0;
        foreach (var r in _rows)
            if (r.Status == "FAILED")
                failed++;
        _statusBar.Text = $"{_rows.Count} · {T("PosSync.Failed")}: {failed}";
    }

    private async Task RetryAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("PosSync.ServiceUnavailable"));
            if (_grid.SelectedItem is not OutboundMessageRow row)
                throw new InvalidOperationException(T("PosSync.SelectFailed"));
            if (row.Status != "FAILED")
                throw new InvalidOperationException(T("PosSync.SelectFailed"));
            await _service.RetryOutboundMessageAsync(row.Id);
            MessageBox.Show(this, T("PosSync.Requeued"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosSyncWin.RetryAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
