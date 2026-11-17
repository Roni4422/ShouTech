using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Maintenance window: runs the real DatabaseHealth check
/// (AppHost.DatabaseHealth.CheckAsync) and the provider integrity summary,
/// offers a real Backup Now through AppHost.Backup (IBkpServ.CreateBackupAsync)
/// and SQLite VACUUM/ANALYZE through OperationsService. On PostgreSQL the
/// VACUUM action is disabled with an honest explanation — nothing is faked.
/// </summary>
public sealed class MaintenanceWin : BaseWin
{
    private readonly OperationsService? _service;
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _vacuumNote = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _runButton = new();
    private readonly Button _backupButton = new();
    private readonly Button _vacuumButton = new();
    private readonly Button _closeButton = new();

    public MaintenanceWin()
    {
        _service = AppHost.Operations;
        Width = 860;
        Height = 560;
        MinWidth = 700;
        MinHeight = 440;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.System.Maintenance))
        {
            MessageBox.Show(this, T("Maintenance.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        var isPg = string.Equals(CurrentDatabaseProvider, "postgresql", StringComparison.OrdinalIgnoreCase);
        _vacuumButton.IsEnabled = !isPg;
        _vacuumNote.Text = isPg ? T("Maintenance.VacuumPgNote") : T("Maintenance.VacuumSqliteNote");
        await RunChecksAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_runButton, "Maintenance.RunChecks");
        F2Ui.StyleButton(_backupButton, "Maintenance.BackupNow");
        F2Ui.StyleButton(_vacuumButton, "Maintenance.Vacuum");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _runButton.Click += async (_, _) => await RunChecksAsync();
        _backupButton.Click += async (_, _) => await BackupAsync();
        _vacuumButton.Click += async (_, _) => await VacuumAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_runButton, _backupButton, _vacuumButton, _closeButton));

        var content = new DockPanel();
        _vacuumNote.Margin = new Thickness(0, 0, 0, 8);
        _vacuumNote.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_vacuumNote, Dock.Top);
        content.Children.Add(_vacuumNote);
        content.Children.Add(_results);
        root.Children.Add(content);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.Title");
        F2Ui.ApplyTexts(_labels, new List<(DataGridColumn, string)>(), new[] { _runButton, _backupButton, _vacuumButton, _closeButton }, T);
    }

    private async Task RunChecksAsync()
    {
        _results.Items.Clear();
        try
        {
            if (AppHost.DatabaseHealth is { } health)
            {
                var result = await health.CheckAsync();
                _results.Items.Add($"{T("Maintenance.Provider")}: {result.Provider}");
                _results.Items.Add($"{T("Maintenance.Available")}: {result.IsAvailable}");
                _results.Items.Add($"{T("Maintenance.Latency")}: {(result.Latency.HasValue ? result.Latency.Value.TotalMilliseconds.ToString("0") + " ms" : "-")}");
                _results.Items.Add($"{T("Maintenance.Message")}: {result.Message}");
            }
            else
            {
                _results.Items.Add(T("Maintenance.HealthUnavailable"));
            }

            if (_service is not null)
            {
                var summary = await _service.GetIntegritySummaryAsync();
                _results.Items.Add($"{T("Maintenance.Integrity")}: {summary}");
            }
            _statusBar.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task BackupAsync()
    {
        try
        {
            if (AppHost.Backup is null)
                throw new InvalidOperationException(T("Maintenance.BackupUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Backup.Create))
                throw new InvalidOperationException(T("Maintenance.PermissionDenied"));
            _backupButton.IsEnabled = false;
            try
            {
                var path = await AppHost.Backup.CreateBackupAsync(CurrentCompanyCode);
                _results.Items.Add($"{T("Maintenance.BackupCreated")}: {path}");
                _statusBar.Text = path;
            }
            finally
            {
                _backupButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task VacuumAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
            _vacuumButton.IsEnabled = false;
            try
            {
                await _service.VacuumAnalyzeAsync();
                _results.Items.Add(T("Maintenance.VacuumDone"));
            }
            finally
            {
                _vacuumButton.IsEnabled = true;
            }
            await RunChecksAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
