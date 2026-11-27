using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Safe, read-only maintenance diagnostics. Any future repair/repost action
/// must be exposed as a separate, explicitly confirmed operation.
/// </summary>
public sealed class MaintenanceWin : BaseWin
{
    protected override string? ScheduleTintName => "Keel";

    private readonly OperationsService? _service;
    private readonly ListBox _checks = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBlock _checksTitle = new();
    private readonly TextBlock _pendingNote = new();
    private readonly Button _runButton = new();
    private bool _running;
    private int _failedChecks;

    public MaintenanceWin()
    {
        _service = AppHost.Operations;
        Width = 860;
        Height = 600;
        MinWidth = 660;
        MinHeight = 420;
        BuildUi();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.System.Maintenance))
        {
            MessageBox.Show(this, T("Maintenance.PermissionDenied"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        // Do not run diagnostics automatically when the window opens.
        // The user must explicitly start them after the backup decision.
        PopulateCheckCatalog();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var statusBar = F2Ui.StatusBar(_statusBar);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);

        F2Ui.StyleButton(_runButton, "Maintenance.RunChecks");
        _runButton.Click += async (_, _) => await ConfirmBackupAndRunAsync();
        var bottomBar = F2Ui.BottomBar(_runButton);
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        root.Children.Add(bottomBar);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _checksTitle.Margin = new Thickness(0, 0, 0, 6);
        _checksTitle.FontWeight = FontWeights.SemiBold;
        Grid.SetRow(_checksTitle, 0);
        content.Children.Add(_checksTitle);

        _checks.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(_checks, 1);
        content.Children.Add(_checks);

        _pendingNote.Margin = new Thickness(0, 6, 0, 6);
        _pendingNote.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_pendingNote, 2);
        content.Children.Add(_pendingNote);

        _results.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(_results, 3);
        content.Children.Add(_results);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.Title");
        _checksTitle.Text = T("Maintenance.CheckListTitle");
        _pendingNote.Text = T("Maintenance.PendingAudits");
        F2Ui.ApplyTexts(new List<(TextBlock Label, string Key)>(),
            new List<(DataGridColumn, string)>(), new[] { _runButton }, T);
    }

    private void PopulateCheckCatalog()
    {
        _checks.Items.Clear();
        _checks.Items.Add(T("Maintenance.CheckDatabaseConnection"));
        _checks.Items.Add(T("Maintenance.CheckDatabaseIntegrity"));
        _checks.Items.Add(T("Maintenance.CheckMigrationLedger"));
        _checks.Items.Add(T("Maintenance.CheckTrialBalance"));
    }

    private async Task ConfirmBackupAndRunAsync()
    {
        if (_running)
            return;

        _results.Items.Clear();
        var decision = MessageBox.Show(this, T("Maintenance.BackupPrompt"),
            T("Maintenance.BackupPromptTitle"), MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning, MessageBoxResult.Yes);

        if (decision == MessageBoxResult.Cancel)
            return;

        if (decision == MessageBoxResult.Yes)
        {
            try
            {
                if (AppHost.Backup is null)
                    throw new InvalidOperationException(T("Maintenance.BackupUnavailable"));
                if (!AppHost.Permissions.Has(PermCat.Backup.Create))
                    throw new InvalidOperationException(T("Maintenance.BackupPermissionDenied"));

                _runButton.IsEnabled = false;
                _statusBar.Text = T("Maintenance.CreatingBackup");
                var backupPath = await AppHost.Backup.CreateBackupAsync(CurrentCompanyCode);
                _results.Items.Add(Tf("Maintenance.BackupCreated", backupPath));
            }
            catch (Exception ex)
            {
                _statusBar.Text = T("Maintenance.BackupFailed");
                MessageBox.Show(this, Tf("Maintenance.BackupFailedDetails", ex.Message),
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
                _runButton.IsEnabled = true;
                return; // Never proceed when the requested backup failed.
            }
        }

        await RunChecksAsync();
    }

    private async Task RunChecksAsync()
    {
        if (_running)
            return;

        _running = true;
        _runButton.IsEnabled = false;
        _failedChecks = 0;
        _statusBar.Text = T("Maintenance.ChecksRunning");

        try
        {
            await RunCheckAsync("Maintenance.CheckDatabaseConnection", async () =>
            {
                if (AppHost.DatabaseHealth is not { } health)
                    throw new InvalidOperationException(T("Maintenance.HealthUnavailable"));
                var result = await health.CheckAsync();
                if (!result.IsAvailable)
                    throw new InvalidOperationException(result.Message);
                return Tf("Maintenance.DatabaseConnectionResult", result.Provider,
                    result.Latency?.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) ?? "-");
            });

            await RunCheckAsync("Maintenance.CheckDatabaseIntegrity", async () =>
            {
                if (_service is null)
                    throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
                var summary = await _service.GetIntegritySummaryAsync();
                if (summary.Contains("integrity_check:", StringComparison.OrdinalIgnoreCase)
                    && !summary.Contains("integrity_check: ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(summary);
                return summary;
            });

            await RunCheckAsync("Maintenance.CheckMigrationLedger", async () =>
            {
                if (_service is null)
                    throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
                var schema = await _service.GetSchemaVersionAsync();
                return Tf("Maintenance.MigrationLedgerResult", schema.AppliedCount,
                    string.IsNullOrWhiteSpace(schema.HighestMigrationId) ? "-" : schema.HighestMigrationId);
            });

            await RunCheckAsync("Maintenance.CheckTrialBalance", async () =>
            {
                if (_service is null)
                    throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
                var audit = await _service.GetTrialBalanceIntegrityAsync(CurrentCompanyCode);
                foreach (var period in audit.Periods)
                {
                    var periodName = string.IsNullOrWhiteSpace(period.Period)
                        ? T("Maintenance.NoPeriod") : period.Period;
                    _results.Items.Add(Tf("Maintenance.TbPeriodLine", periodName, period.Entries,
                        F2Ui.Fmt(period.Debit), F2Ui.Fmt(period.Credit),
                        period.IsBalanced ? T("Maintenance.TbBalanced") : T("Maintenance.TbUnbalanced")));
                }
                _results.Items.Add(Tf("Maintenance.TbOrphans", audit.OrphanLineCount));
                _results.Items.Add(Tf("Maintenance.TbNoLines", audit.PostedEntriesWithoutLines));
                if (!audit.IsClean)
                    throw new InvalidOperationException(T("Maintenance.TbFailed"));
                return T("Maintenance.TbOk");
            });

            _statusBar.Text = _failedChecks == 0
                ? T("Maintenance.ChecksCompleted")
                : Tf("Maintenance.ChecksCompletedWithErrors", _failedChecks);
        }
        finally
        {
            _running = false;
            _runButton.IsEnabled = true;
        }
    }

    private async Task RunCheckAsync(string labelKey, Func<Task<string>> check)
    {
        try
        {
            var detail = await check();
            _results.Items.Add(Tf("Maintenance.CheckPassed", T(labelKey), detail));
        }
        catch (Exception ex)
        {
            _failedChecks++;
            _results.Items.Add(Tf("Maintenance.CheckFailed", T(labelKey), ex.Message));
        }
    }
}
