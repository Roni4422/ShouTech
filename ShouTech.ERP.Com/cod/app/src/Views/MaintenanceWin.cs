using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Maintenance window: runs the real DatabaseHealth check
/// (AppHost.DatabaseHealth.CheckAsync) and the provider integrity summary,
/// offers a real Backup Now through AppHost.Backup (IBkpServ.CreateBackupAsync)
/// and SQLite VACUUM/ANALYZE through OperationsService. On PostgreSQL the
/// VACUUM action is disabled with an honest explanation — nothing is faked.
/// B5 adds the two Al-Ameen-style audits that map to real engines:
/// Recalculate Costs (StockRecalculationService via OperationsService)
/// and a read-only trial-balance integrity audit (per-period posted
/// Dr=Cr, orphan lines, empty posted entries).
/// feat/recalc-costs-gl turns Recalculate Costs into a range-based
/// preview/approve flow WITH GL restatement: the button first runs a
/// preview over the picked range (default: the current open period)
/// listing every cost change and the GL adjustment it implies —
/// nothing posts from the preview — and the Post Adjustments button
/// (bottom bar) posts the approved adjustments as real journal
/// entries through the central GlPosting engine, each carrying the
/// recalculation run id. Already-posted documents' own entries are
/// never rewritten; the value delta is restated by the new entries.
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
    private readonly Button _postButton = new();
    private readonly Button _tbAuditButton = new();
    private readonly Button _closeButton = new();
    private readonly DatePicker _recalcFrom = new();
    private readonly TextBlock _recalcFromLabel = new();
    private readonly DatePicker _recalcTo = new();
    private readonly TextBlock _recalcToLabel = new();
    private readonly TextBlock _changesTitle = new();
    private readonly TextBlock _adjustmentsTitle = new();
    private readonly DataGrid _changesGrid = new();
    private readonly DataGrid _adjustmentsGrid = new();
    private readonly List<(DataGridColumn Column, string Key)> _gridHeaders = new();
    private RecalculationPreview? _preview;

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
        if (_service is not null)
        {
            try
            {
                // Default range: the current open fiscal period.
                var (rangeFrom, rangeTo) = await _service.GetRecalculationDefaultRangeAsync(CurrentCompanyCode);
                _recalcFrom.SelectedDate = rangeFrom.ToDateTime(TimeOnly.MinValue);
                _recalcTo.SelectedDate = rangeTo.ToDateTime(TimeOnly.MinValue);
            }
            catch (Exception ex)
            {
                _results.Items.Add(ex.Message);
            }
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_runButton, "Maintenance.RunChecks");
        F2Ui.StyleButton(_backupButton, "Maintenance.BackupNow");
        F2Ui.StyleButton(_vacuumButton, "Maintenance.Vacuum");
        F2Ui.StyleButton(_recalcButton, "Maintenance.RecalculateCosts");
        F2Ui.StyleButton(_postButton, "Maintenance.RecalcPostAdjustments");
        F2Ui.StyleButton(_tbAuditButton, "Maintenance.TbAudit");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _postButton.IsEnabled = false;
        _runButton.Click += async (_, _) => await RunChecksAsync();
        _backupButton.Click += async (_, _) => await BackupAsync();
        _vacuumButton.Click += async (_, _) => await VacuumAsync();
        _recalcButton.Click += async (_, _) => await RecalculateCostsAsync();
        _postButton.Click += async (_, _) => await PostAdjustmentsAsync();
        _tbAuditButton.Click += async (_, _) => await TrialBalanceAuditAsync();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_runButton, _backupButton, _vacuumButton, _recalcButton, _postButton, _tbAuditButton, _closeButton));

        var content = new DockPanel();
        _vacuumNote.Margin = new Thickness(0, 0, 0, 8);
        _vacuumNote.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(_vacuumNote, Dock.Top);
        content.Children.Add(_vacuumNote);

        // Recalculate Costs range row: the run covers [from, to];
        // changing either date discards any preview (it no longer
        // matches what would be approved).
        var recalcRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _recalcFromLabel.VerticalAlignment = VerticalAlignment.Center;
        _recalcFromLabel.Margin = new Thickness(0, 0, 8, 0);
        recalcRow.Children.Add(_recalcFromLabel);
        _recalcFrom.SelectedDate = new DateTime(DateTime.Today.Year, 1, 1);
        _recalcFrom.Width = 160;
        _recalcFrom.SelectedDateChanged += (_, _) => InvalidatePreview();
        recalcRow.Children.Add(_recalcFrom);
        _recalcToLabel.VerticalAlignment = VerticalAlignment.Center;
        _recalcToLabel.Margin = new Thickness(12, 0, 8, 0);
        recalcRow.Children.Add(_recalcToLabel);
        _recalcTo.SelectedDate = DateTime.Today;
        _recalcTo.Width = 160;
        _recalcTo.SelectedDateChanged += (_, _) => InvalidatePreview();
        recalcRow.Children.Add(_recalcTo);
        DockPanel.SetDock(recalcRow, Dock.Top);
        content.Children.Add(recalcRow);

        // Preview area: cost changes, then the GL adjustments they
        // imply, then the results log at a fixed height so the other
        // maintenance tools keep their room.
        var previewArea = new Grid();
        previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
        previewArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.8, GridUnitType.Star) });
        previewArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(104) });
        _changesTitle.Margin = new Thickness(0, 0, 0, 4);
        Grid.SetRow(_changesTitle, 0);
        previewArea.Children.Add(_changesTitle);
        BuildChangesGrid();
        Grid.SetRow(_changesGrid, 1);
        previewArea.Children.Add(_changesGrid);
        _adjustmentsTitle.Margin = new Thickness(0, 8, 0, 4);
        Grid.SetRow(_adjustmentsTitle, 2);
        previewArea.Children.Add(_adjustmentsTitle);
        BuildAdjustmentsGrid();
        Grid.SetRow(_adjustmentsGrid, 3);
        previewArea.Children.Add(_adjustmentsGrid);
        _results.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(_results, 4);
        previewArea.Children.Add(_results);
        content.Children.Add(previewArea);

        root.Children.Add(content);

        Content = root;
        ApplyLocalizedText();
    }

    private void BuildChangesGrid()
    {
        F2Ui.ConfigureGrid(_changesGrid);
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColMaterial", nameof(RecalcCostChange.MaterialCode), 90));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColName", nameof(RecalcCostChange.MaterialName), 150));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColWarehouse", nameof(RecalcCostChange.WarehouseId), 80, center: true));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColQty", nameof(RecalcCostChange.QtyOnHand), 90, numeric: true));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColOldCost", nameof(RecalcCostChange.OldAvgCost), 90, numeric: true));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColNewCost", nameof(RecalcCostChange.NewAvgCost), 90, numeric: true));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColOldValue", nameof(RecalcCostChange.OldValue), 100, numeric: true));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColNewValue", nameof(RecalcCostChange.NewValue), 100, numeric: true));
        _changesGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColDelta", nameof(RecalcCostChange.ValueDelta), 100, numeric: true));
    }

    private void BuildAdjustmentsGrid()
    {
        F2Ui.ConfigureGrid(_adjustmentsGrid);
        _adjustmentsGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColMaterial", nameof(RecalcAdjustment.MaterialCode), 90));
        _adjustmentsGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColName", nameof(RecalcAdjustment.MaterialName), 150));
        _adjustmentsGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColInventoryAccount", nameof(RecalcAdjustment.InventoryAccountCode), 130, center: true));
        _adjustmentsGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColOffsetAccount", nameof(RecalcAdjustment.OffsetAccountCode), 130, center: true));
        _adjustmentsGrid.Columns.Add(F2Ui.TextCol(_gridHeaders, "Maintenance.RecalcColAmount", nameof(RecalcAdjustment.Amount), 110, numeric: true));
    }

    /// <summary>Drops the current preview: its figures no longer
    /// describe what an approval would post.</summary>
    private void InvalidatePreview()
    {
        _preview = null;
        _postButton.IsEnabled = false;
        _changesGrid.ItemsSource = null;
        _adjustmentsGrid.ItemsSource = null;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Maintenance.Title");
        _recalcFromLabel.Text = T("Maintenance.RecalcFrom");
        _recalcToLabel.Text = T("Maintenance.RecalcTo");
        _changesTitle.Text = T("Maintenance.RecalcChangesTitle");
        _adjustmentsTitle.Text = T("Maintenance.RecalcAdjustmentsTitle");
        F2Ui.ApplyTexts(_labels, _gridHeaders, new[] { _runButton, _backupButton, _vacuumButton, _recalcButton, _postButton, _tbAuditButton, _closeButton }, T);
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
    /// Recalculate Costs preview (feat/recalc-costs-gl): runs the
    /// engine's dry-run over the picked range and shows every cost
    /// change plus the GL adjustment it implies. NOTHING posts from
    /// here — the engine refuses ranges ending before a later
    /// movement (INV-6018) and ranges touching a closed fiscal period
    /// (INV-6004); its cataloged error surfaces here unchanged.
    /// </summary>
    private async Task RecalculateCostsAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Maintenance.ServiceUnavailable"));
            if (_recalcFrom.SelectedDate is not DateTime fromPicked)
                throw new InvalidOperationException(T("Maintenance.RecalcFromRequired"));
            if (_recalcTo.SelectedDate is not DateTime toPicked)
                throw new InvalidOperationException(T("Maintenance.RecalcToRequired"));
            _recalcButton.IsEnabled = false;
            try
            {
                var preview = await _service.PreviewRecalculationAsync(
                    CurrentCompanyCode,
                    DateOnly.FromDateTime(fromPicked),
                    DateOnly.FromDateTime(toPicked));
                _preview = preview;
                _changesGrid.ItemsSource = preview.Changes;
                _adjustmentsGrid.ItemsSource = preview.Adjustments;
                _postButton.IsEnabled = preview.Changes.Count > 0;
                _results.Items.Add(Tf("Maintenance.RecalcPreviewSummary",
                    preview.MovementsReplayed, preview.Changes.Count, preview.Adjustments.Count,
                    F2Ui.Fmt(preview.TotalDelta)));
                if (preview.Changes.Count == 0)
                    _results.Items.Add(T("Maintenance.RecalcNoChanges"));
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
    /// Post Adjustments: the explicit approval step. After the user's
    /// confirmation the engine re-verifies the data still matches the
    /// preview (INV-6017 if it moved) and posts the adjustments as
    /// real journal entries linked to the run; the posted document
    /// numbers land in the results log. Any failure consumes the
    /// preview — a fresh preview is required before posting again.
    /// </summary>
    private async Task PostAdjustmentsAsync()
    {
        if (_service is null || _preview is null)
            return;
        var confirm = MessageBox.Show(this,
            Tf("Maintenance.RecalcPostConfirm", _preview.Adjustments.Count, F2Ui.Fmt(_preview.TotalDelta)),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;
        try
        {
            _postButton.IsEnabled = false;
            _recalcButton.IsEnabled = false;
            try
            {
                var applied = await _service.ApplyRecalculationAsync(
                    CurrentCompanyCode, _preview.FromDate, _preview.ToDate,
                    T("Maintenance.RecalcReason"), CurrentUserId,
                    DateOnly.FromDateTime(DateTime.Today), _preview);
                _results.Items.Add(Tf("Maintenance.RecalcPosted", applied.JobId, applied.PostedEntries.Count));
                foreach (var entry in applied.PostedEntries)
                    _results.Items.Add(Tf("Maintenance.RecalcPostedEntry", entry.MaterialCode, entry.DocumentNumber));
                _statusBar.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            }
            finally
            {
                _recalcButton.IsEnabled = true;
                InvalidatePreview();
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
