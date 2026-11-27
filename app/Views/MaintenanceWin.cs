using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ShouTech.App.Services;
using ShouTech.Application.Services;

namespace ShouTech.App.Views;

public sealed class MaintenanceWin : BaseWin
{
    protected override string? ScheduleTintName => "Keel";

    private static readonly string[] IntegrityMetrics = { "foreign_key_violations", "invalid_indexes", "unvalidated_constraints" };
    private readonly OperationsService? _service;
    private readonly UniformGrid _checks = new() { Columns = 2 };
    private readonly TextBlock _statusBar = new();
    private readonly TextBlock _checksTitle = new();
    private readonly TextBlock _historyLink = new() { TextDecorations = TextDecorations.Underline, Foreground = Brushes.RoyalBlue, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _runButton = new();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Width = 190, Height = 16, Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _progressText = new() { MinWidth = 48, VerticalAlignment = VerticalAlignment.Center };
    private bool _running;

    public MaintenanceWin()
    {
        _service = AppHost.Operations;
        Width = 900;
        Height = 560;
        MinWidth = 720;
        MinHeight = 440;
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

        PopulateCheckCatalog();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var status = F2Ui.StatusBar(_statusBar);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        F2Ui.StyleButton(_runButton, "Maintenance.RunChecks");
        _runButton.Click += async (_, _) => await ConfirmBackupAndRunAsync();
        var bottomBar = F2Ui.BottomBar(_runButton);
        if (bottomBar.Child is StackPanel commands)
        {
            commands.Children.Add(_progress);
            commands.Children.Add(_progressText);
        }
        DockPanel.SetDock(bottomBar, Dock.Bottom);
        root.Children.Add(bottomBar);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _checksTitle.Margin = new Thickness(0, 0, 0, 8);
        _checksTitle.FontWeight = FontWeights.SemiBold;
        _historyLink.Text = T("Maintenance.History");
        _historyLink.MouseLeftButtonUp += (_, _) => new MaintenanceHistoryWin { Owner = this }.ShowDialog();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_historyLink, Dock.Right);
        heading.Children.Add(_historyLink);
        heading.Children.Add(_checksTitle);
        Grid.SetRow(heading, 0);
        content.Children.Add(heading);

        _checks.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(_checks, 1);
        content.Children.Add(_checks);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.Title");
        _checksTitle.Text = T("Maintenance.CheckListTitle");
        _historyLink.Text = T("Maintenance.History");
        if (_checks.Children.Count > 0)
            PopulateCheckCatalog();
        F2Ui.ApplyTexts(new List<(TextBlock Label, string Key)>(),
            new List<(DataGridColumn, string)>(), new List<Button> { _runButton }, T);
        _progressText.Text = Tf("Maintenance.ChecksProgress", (int)_progress.Value);
    }

    private void PopulateCheckCatalog()
    {
        _checks.Children.Clear();
        foreach (var key in new[]
        {
            "Maintenance.CheckDatabaseConnection",
            "Maintenance.CheckDatabaseIntegrity",
            "Maintenance.CheckMigrationLedger",
            "Maintenance.CheckTrialBalance"
        })
        {
            _checks.Children.Add(new TextBlock
            {
                Text = T(key),
                Margin = new Thickness(8, 8, 16, 8),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Black
            });
        }
    }

    private async Task ConfirmBackupAndRunAsync()
    {
        if (_running)
            return;

        var prompt = new MaintenanceBackupPromptWin { Owner = this };
        var decision = prompt.ShowDialog();
        if (decision is null)
            return;

        if (decision.Value)
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
                if (!await AppHost.Backup.ValidateBackupAsync(backupPath))
                    throw new InvalidOperationException(T("Maintenance.BackupVerifyFailed"));
                _statusBar.Text = Tf("Maintenance.BackupCreated", backupPath);
            }
            catch (Exception ex)
            {
                _statusBar.Text = T("Maintenance.BackupFailed");
                MessageBox.Show(this, Tf("Maintenance.BackupFailedDetails", ex.Message),
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
                _runButton.IsEnabled = true;
                return;
            }
        }

        await RunChecksAsync();
    }

    private Task RunChecksAsync()
    {
        if (_running)
            return Task.CompletedTask;

        _running = true;
        _runButton.IsEnabled = false;
        _progress.Value = 0;
        _progressText.Text = Tf("Maintenance.ChecksProgress", 0);
        _statusBar.Text = T("Maintenance.ChecksRunning");

        var progress = new Progress<double>(value =>
        {
            _progress.Value = Math.Clamp(value, 0, 100);
            _progressText.Text = Tf("Maintenance.ChecksProgress", (int)Math.Round(_progress.Value));
        });

        var checks = new List<(string LabelKey, Func<Task<string>> Execute)>
        {
            ("Maintenance.CheckDatabaseConnection", CheckDatabaseConnectionAsync),
            ("Maintenance.CheckDatabaseIntegrity", CheckDatabaseIntegrityAsync),
            ("Maintenance.CheckMigrationLedger", CheckMigrationLedgerAsync),
            ("Maintenance.CheckTrialBalance", CheckTrialBalanceAsync)
        };

        try
        {
            var window = new MaintenanceChecksWin(checks, progress) { Owner = this };
            window.ShowDialog();
            if (window.WasCancelled)
            {
                _statusBar.Text = T("Maintenance.ChecksCancelled");
            }
            else
            {
                _statusBar.Text = window.FailedCount == 0
                    ? T("Maintenance.ChecksCompleted")
                    : Tf("Maintenance.ChecksCompletedWithErrors", window.FailedCount);
                _progress.Value = 100;
                _progressText.Text = Tf("Maintenance.ChecksProgress", 100);
            }

            if (_service is not null)
            {
                try
                {
                    var reportNo = await _service.SaveMaintenanceReportAsync(
                        CurrentCompanyCode,
                        AppHost.Session.CurrentUser ?? "system",
                        DatabaseRuntime.GetProvider(),
                        window.WasCancelled ? "Cancelled" : "Completed",
                        window.CheckCount,
                        window.PassedCount,
                        0,
                        window.FailedCount,
                        window.ReportJson);
                    _statusBar.Text = Tf("Maintenance.ReportSaved", reportNo);
                }
                catch (Exception reportError)
                {
                    _statusBar.Text = T("Maintenance.ReportSaveFailed");
                    MessageBox.Show(this, Tf("Maintenance.ReportSaveFailed", reportError.Message),
                        Title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        finally
        {
            _running = false;
            _runButton.IsEnabled = true;
        }

        return Task.CompletedTask;
    }

    private async Task<string> CheckDatabaseConnectionAsync()
    {
        if (AppHost.DatabaseHealth is not { } health)
            throw new InvalidOperationException(T("Maintenance.HealthUnavailable"));
        var result = await health.CheckAsync();
        if (!result.IsAvailable)
            throw new InvalidOperationException(result.Message);
        return Tf("Maintenance.DatabaseConnectionResult", result.Provider,
            result.Latency?.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) ?? "-");
    }

    private async Task<string> CheckDatabaseIntegrityAsync()
    {
        if (_service is null)
            throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));

        var summary = await _service.GetIntegritySummaryAsync();
        if (!IntegritySummaryIsClean(summary))
            throw new InvalidOperationException(summary);
        return summary;
    }

    private async Task<string> CheckMigrationLedgerAsync()
    {
        if (_service is null)
            throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
        var schema = await _service.GetSchemaVersionAsync();
        return Tf("Maintenance.MigrationLedgerResult", schema.AppliedCount,
            string.IsNullOrWhiteSpace(schema.HighestMigrationId) ? "-" : schema.HighestMigrationId);
    }

    private async Task<string> CheckTrialBalanceAsync()
    {
        if (_service is null)
            throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));

        var audit = await _service.GetTrialBalanceIntegrityAsync(CurrentCompanyCode);
        if (!audit.IsClean)
            throw new InvalidOperationException(T("Maintenance.TbFailed"));

        var periods = new List<string>();
        foreach (var period in audit.Periods)
        {
            var periodName = string.IsNullOrWhiteSpace(period.Period)
                ? T("Maintenance.NoPeriod") : period.Period;
            periods.Add(Tf("Maintenance.TbPeriodLine", periodName, period.Entries,
                F2Ui.Fmt(period.Debit), F2Ui.Fmt(period.Credit),
                period.IsBalanced ? T("Maintenance.TbBalanced") : T("Maintenance.TbUnbalanced")));
        }
        periods.Add(T("Maintenance.TbOk"));
        periods.Add(Tf("Maintenance.TbOrphans", audit.OrphanLineCount));
        periods.Add(Tf("Maintenance.TbNoLines", audit.PostedEntriesWithoutLines));
        return string.Join(Environment.NewLine, periods);
    }

    private static bool IntegritySummaryIsClean(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return false;
        if (summary.Contains("integrity_check:", StringComparison.OrdinalIgnoreCase)
            && !summary.Contains("integrity_check: ok", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var metric in IntegrityMetrics)
        {
            var marker = metric + ":";
            var index = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;
            var value = summary[(index + marker.Length)..].TrimStart();
            var token = value.Split(new[] { ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != 0)
                return false;
        }
        return true;
    }
}

internal sealed class MaintenanceBackupPromptWin : BaseWin
{
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly Button _backupButton = new();
    private readonly Button _skipButton = new();

    protected override bool PersistGeometry => false;

    public MaintenanceBackupPromptWin()
    {
        Width = 460;
        Height = 220;
        MinWidth = 460;
        MinHeight = 220;
        MaxWidth = 460;
        MaxHeight = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildUi();
        Closing += (_, e) => { if (DialogResult is null) e.Cancel = true; };
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        F2Ui.StyleButton(_backupButton, "Maintenance.BackupPromptOk");
        F2Ui.StyleButton(_skipButton, "Maintenance.BackupPromptCancel");
        _backupButton.Click += (_, _) => DialogResult = true;
        _skipButton.Click += (_, _) => DialogResult = false;
        var commands = F2Ui.BottomBar(_skipButton, _backupButton);
        DockPanel.SetDock(commands, Dock.Bottom);
        root.Children.Add(commands);
        root.Children.Add(_message);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.BackupPromptTitle");
        _message.Text = T("Maintenance.BackupPrompt");
        F2Ui.ApplyTexts(new List<(TextBlock Label, string Key)>(),
            new List<(DataGridColumn, string)>(), new[] { _backupButton, _skipButton }, T);
    }
}
