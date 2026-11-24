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
/// Maintenance window: runs the real DatabaseHealth check
/// (AppHost.DatabaseHealth.CheckAsync) and the provider integrity summary,
/// offers a real Backup Now through AppHost.Backup (IBkpServ.CreateBackupAsync)
/// and SQLite VACUUM/ANALYZE through OperationsService. On PostgreSQL the
/// VACUUM action is disabled with an honest explanation — nothing is faked.
/// B5 adds the two Al-Ameen-style audits that map to real engines:
/// Recalculate Costs (StockRecalculationService via OperationsService,
/// from the picked date, whole company) and a read-only trial-balance
/// integrity audit (per-period posted Dr=Cr, orphan lines, empty
/// posted entries).
/// </summary>
public sealed class MaintenanceWin : BaseWin
{
    protected override string? ScheduleTintName => "Keel";
    private readonly OperationsService? _service;
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _vacuumNote = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _runButton = new();
    private readonly Button _backupButton = new();
    private readonly Button _vacuumButton = new();
    private readonly Button _recalcButton = new();
    private readonly Button _tbAuditButton = new();
    private readonly Button _closeButton = new();
    private readonly DatePicker _recalcFrom = new();
    private readonly TextBlock _recalcFromLabel = new();

    public MaintenanceWin()
    {
        _service = AppHost.Operations;
        Width = 860;
        Height = 560;
        MinWidth = 660;
        MinHeight = 360;
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
        F2Ui.StyleButton(_recalcButton, "Maintenance.RecalculateCosts");
        F2Ui.StyleButton(_tbAuditButton, "Maintenance.TbAudit");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _runButton.Click += async (_, _) => await RunChecksAsync();
        _backupButton.Click += async (_, _) => await BackupAsync();
        _vacuumButton.Click += async (_, _) => await VacuumAsync();
        _recalcButton.Click += async (_, _) => await RecalculateCostsAsync();
        _tbAuditButton.Click += async (_, _) => await TrialBalanceAuditAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_runButton, _backupButton, _vacuumButton, _recalcButton, _tbAuditButton, _closeButton));

        var content = new DockPanel();
        _vacuumNote.Margin = new Thickness(0, 0, 0, 8);
        _vacuumNote.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_vacuumNote, Dock.Top);
        content.Children.Add(_vacuumNote);

        // Recalculate Costs scope row: replay from this date onward.
        var recalcRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _recalcFromLabel.VerticalAlignment = VerticalAlignment.Center;
        _recalcFromLabel.Margin = new Thickness(0, 0, 8, 0);
        recalcRow.Children.Add(_recalcFromLabel);
        _recalcFrom.SelectedDate = new DateTime(DateTime.Today.Year, 1, 1);
        _recalcFrom.Width = 160;
        recalcRow.Children.Add(_recalcFrom);
        DockPanel.SetDock(recalcRow, Dock.Top);
        content.Children.Add(recalcRow);

        content.Children.Add(_results);
        root.Children.Add(content);

        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.Title");
        _recalcFromLabel.Text = T("Maintenance.RecalcFrom");
        F2Ui.ApplyTexts(_labels, new List<(DataGridColumn, string)>(), new[] { _runButton, _backupButton, _vacuumButton, _recalcButton, _tbAuditButton, _closeButton }, T);
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
                _results.Items.Add($"{T("Maintenance.Latency")}: {(result.Latency.HasValue ? result.Latency.Value.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms" : "-")}");
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

    /// <summary>
    /// Recalculate Costs (B5): replays the company's stock valuation
    /// from the picked date onward. The engine records the job and
    /// refuses ranges touching a closed fiscal period; its cataloged
    /// error surfaces here unchanged. GL entries are NOT restated
    /// (documented engine behavior).
    /// </summary>
    private async Task RecalculateCostsAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
            if (_recalcFrom.SelectedDate is not DateTime picked)
                throw new InvalidOperationException(T("Maintenance.RecalcFromRequired"));
            _recalcButton.IsEnabled = false;
            try
            {
                var outcome = await _service.RecalculateCostsAsync(
                    CurrentCompanyCode,
                    DateOnly.FromDateTime(picked),
                    T("Maintenance.RecalcReason"),
                    CurrentUserId);
                _results.Items.Add(Tf("Maintenance.RecalcResult",
                    outcome.MovementsReplayed, F2Ui.Fmt(outcome.ValueDelta), outcome.JobId));
                _statusBar.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            }
            finally
            {
                _recalcButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Trial-balance integrity audit (B5, read-only): per-period
    /// posted Dr=Cr lines, then every anomaly in detail — unbalanced
    /// entries, orphan lines, posted entries without lines.
    /// </summary>
    private async Task TrialBalanceAuditAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
            _tbAuditButton.IsEnabled = false;
            try
            {
                var audit = await _service.GetTrialBalanceIntegrityAsync(CurrentCompanyCode);
                _results.Items.Add($"--- {T("Maintenance.TbAudit")} ---");
                foreach (var period in audit.Periods)
                {
                    var name = string.IsNullOrWhiteSpace(period.Period) ? T("Maintenance.NoPeriod") : period.Period;
                    _results.Items.Add(Tf("Maintenance.TbPeriodLine",
                        name, period.Entries, F2Ui.Fmt(period.Debit), F2Ui.Fmt(period.Credit),
                        period.IsBalanced ? T("Maintenance.TbBalanced") : T("Maintenance.TbUnbalanced")));
                }
                foreach (var entry in audit.UnbalancedEntries)
                {
                    _results.Items.Add(Tf("Maintenance.TbEntryLine",
                        entry.EntryId, entry.EntryDate, F2Ui.Fmt(entry.Debit), F2Ui.Fmt(entry.Credit)));
                }
                _results.Items.Add(Tf("Maintenance.TbOrphans", audit.OrphanLineCount));
                _results.Items.Add(Tf("Maintenance.TbNoLines", audit.PostedEntriesWithoutLines));
                _results.Items.Add(audit.IsClean ? T("Maintenance.TbOk") : T("Maintenance.TbFailed"));
                _statusBar.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            }
            finally
            {
                _tbAuditButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
