using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.App.Localization;
using ShouTech.App.ViewModels;
using WpfApp = System.Windows.Application;

namespace ShouTech.App.Views;

/// <summary>
/// Backup window: freezes the main shell, closes secondary windows, creates STB mirror,
/// then restores UI life and rebuilds the dynamic backup index.
/// </summary>
public partial class BkpWin : Window
{
    private readonly ObservableCollection<BkpRow> _items = new();
    private bool _busy;
    private bool _shellFrozenByUs;

    public event EventHandler? BackupSucceeded;

    public BkpWin()
    {
        InitializeComponent();
        VisuId.ApplyWindow(this);
        lstBackups.ItemsSource = _items;
        Loaded += async (_, _) =>
        {
            ApplyLanguage();
            // Dynamic index every time the window opens or is shown again
            await RebuildIndexAsync();
        };
        IsVisibleChanged += async (_, e) =>
        {
            if (IsVisible && e.NewValue is true && !_busy)
                await RebuildIndexAsync();
        };
    }

    private static string Lang => App.CurrentLanguageCode ?? "en";

    private static string CompanyCode => AppState.CurrentCompanyCode?.Trim() ?? string.Empty;

    /// <summary>Localized string from Resources/Lang/*.json under Backup.*</summary>
    private static string L(string key, string fallback = "")
        => AppHost.Localization.GetString(
            "Backup." + key,
            string.IsNullOrEmpty(fallback) ? key : fallback);

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        try { return string.Format(System.Globalization.CultureInfo.CurrentCulture, fmt, args); }
        catch { return fmt; }
    }

    private void ApplyLanguage()
    {
        var isAr = Lang.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        FlowDirection = isAr ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        Title = L("Title", "Backup");
        txtTitle.Text = Title;
        txtCompany.Text = Lf("CompanyLine", "Company: {0}  |  Code: {1}",
            AppState.CurrentCompanyDisplayName ?? "", CompanyCode);
        txtInfo.Text = L("Info", "Encrypted STB mirror of the current company (database, users, preferences).");
        txtNotesLabel.Text = L("NoteLabel", "Backup note:");
        txtNotes.ToolTip = L("NoteTooltip", "Example: before delete the app");
        btnCreate.Content = L("CreateNow", "Create backup now");
        btnOpenFolder.Content = L("OpenFolder", "Open folder");
        btnRefresh.Content = L("Refresh", "Refresh");
        btnRestore.Content = L("RestoreSelected", "Restore selected");
        btnClose.Content = L("Close", "Close");
        txtProvider.Text = Lf("ProviderLine", "Format: STB  |  Provider: {0}", DatabaseRuntime.GetProvider());
        colName.Header = L("ColName", "Backup name");
        colDate.Header = L("ColDate", "Created");
        colSize.Header = L("ColSize", "Size");
        colStatus.Header = L("ColStatus", "Status");
    }

    /// <summary>
    /// Dynamic index: scan backup folder and list all .stb packages for the current company.
    /// </summary>
    private async Task RebuildIndexAsync(string? verifiedFilePath = null)
    {
        if (_busy)
            return;

        if (string.IsNullOrWhiteSpace(CompanyCode))
        {
            _items.Clear();
            txtStatus.Text = L("NoCompany", "No company is currently selected.");
            return;
        }

        try
        {
            SetBusy(true, freezeShell: false);
            txtStatus.Text = L("Indexing", "Indexing backups...");

            await Task.Yield();

            _items.Clear();
            var files = AppHost.Backup.ListBackups(CompanyCode);
            foreach (var item in files)
            {
                var row = new BkpRow(item);
                // Default display status for list (not yet checked this session)
                if (string.IsNullOrWhiteSpace(row.Status) || row.Status == "NotValidated")
                    row.Status = L("StatusNotChecked", "Not checked");

                if (!string.IsNullOrWhiteSpace(verifiedFilePath)
                    && string.Equals(row.FilePath, verifiedFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    row.IsValid = true;
                    row.Status = L("StatusValid", "Valid");
                }

                _items.Add(row);
            }
            LogBackup($"Index rebuilt. Company={CompanyCode}; Count={files.Count}");

            txtStatus.Text = Lf("IndexedCount", "{0} backup(s) indexed", _items.Count);
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogError("BkpWin", "Failed to index backups.", ex);
            txtStatus.Text = L("IndexFailed", "Could not index backups.");
        }
        finally
        {
            SetBusy(false, freezeShell: false);
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || string.IsNullOrWhiteSpace(CompanyCode))
            return;

        var notes = (txtNotes.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(notes))
        {
            notes = L("DefaultNote", "manual backup");
        }

        // Pre-flight: warn that secondary windows will close and main shell will freeze
        if (!ConfirmIsolateForSnapshot(isRestore: false))
        {
            LogBackup($"Create cancelled by user. Company={CompanyCode}");
            return;
        }

        string? createdPath = null;
        var succeeded = false;
        string? errorMessage = null;

        LogBackup(
            "Create started. Company=" +
            CompanyCode +
            "; Notes=" +
            notes +
            "; User=" +
            (AppState.CurrentUser?.Username ?? "-"));

        try
        {
            IsolateForSnapshot();

            LogBackup(
                "Shell isolated: secondary windows closed, main window frozen; " +
                "DB session will be suspended for consistent snapshot.");

            SetBusy(true, freezeShell: true);
            txtStatus.Text = L(
                "Creating",
                "Creating encrypted mirror of company data...");

            // Allow UI to paint freeze state
            await Task.Delay(120);
            await Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            createdPath = await AppHost.Backup.CreateBackupAsync(
                CompanyCode,
				notes);

            // Post-verify: file exists and validates as ShouTech STB
            if (string.IsNullOrWhiteSpace(createdPath) ||
                !File.Exists(createdPath))
            {
                errorMessage = L(
                    "FileNotFoundAfterCreate",
                    "Backup file was not found after creation.");
            }
            else
            {
                var valid = await AppHost.Backup.ValidateBackupAsync(
                    createdPath);

                if (!valid)
                {
                    errorMessage = L(
                        "CreateButInvalid",
                        "Backup was written but integrity validation failed.");
                }
                else
                {
                    succeeded = true;

                    LogBackup(
                        $"Create verified OK. Path={createdPath}");
                }
            }
        }
        catch (Exception ex)
        {
            LogBackupError(
                "Backup creation failed.",
                ex);

            errorMessage = ex.Message;
            succeeded = false;
        }
        finally
        {
            ReleaseIsolation();

            LogBackup(
                "Shell released: main window unfrozen; database session resumed.");

            SetBusy(false, freezeShell: false);
        }

        if (succeeded)
        {
            LogBackup(
                $"Create SUCCESS. Company={CompanyCode}; " +
                $"File={Path.GetFileName(createdPath)}");

            MessageBox.Show(
                L(
                    "CreateSuccess",
                    "Backup completed successfully"),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            txtNotes.Text = string.Empty;

            // Notify the exit workflow only after the STB package
            // has been created and integrity-validated successfully.
            BackupSucceeded?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            LogBackup(
                "Create FAILED. Company=" +
                CompanyCode +
                "; Reason=" +
                (errorMessage ?? "unknown"));

            MessageBox.Show(
                Lf(
                    "CreateFailed",
                    "Backup failed.\n\n{0}",
                    errorMessage ??
                    L(
                        "UnknownError",
                        "Unknown error.")),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        // Window returns to a fresh state with a live index
        await RebuildIndexAsync(
            succeeded ? createdPath : null);

        try
        {
            Activate();
            Focus();
        }
        catch
        {
            // Ignore activation failures.
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (lstBackups.SelectedItem is not BkpRow row)
        {
            MessageBox.Show(
                L("SelectBackup", "Please select a backup from the list."),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmIsolateForSnapshot(isRestore: true, backupName: row.FileName))
        {
            LogBackup($"Restore cancelled by user. File={row.FileName}");
            return;
        }

        var succeeded = false;
        string? errorMessage = null;

        LogBackup($"Restore started. Company={CompanyCode}; File={row.FileName}; Path={row.FilePath}");
        AppHost.Logging.LogOperation("Restore", "START", $"Company={CompanyCode}; File={row.FileName}");

        try
        {
            IsolateForSnapshot();
            AppHost.Logging.LogOperation("Restore", "ISOLATED", "Secondary windows closed; main shell frozen.");
            LogBackup("Shell isolated for restore: secondary windows closed, main window frozen; DB session will be suspended.");
            SetBusy(true, freezeShell: true);
            txtStatus.Text = L("Validating", "Validating backup identity and integrity...");

            await Task.Delay(120);

            var detailed = await AppHost.Backup.ValidateBackupDetailedAsync(row.FilePath);
            if (!detailed.IsValid)
            {
                row.IsValid = false;
                row.Status = L("StatusRejected", "Rejected");
                lstBackups.Items.Refresh();
                errorMessage = detailed.FailureReason
                    ?? (L("ValidationFailed", "Backup validation failed."));
            }
            else
            {
                row.IsValid = true;
                row.Status = L("StatusValid", "Valid");
                lstBackups.Items.Refresh();

                txtStatus.Text = L("Restoring", "Verified — decrypting and adopting mirror...");

                AppHost.Logging.LogOperation("Restore", "VALIDATED", $"File={row.FileName}");
                await AppHost.Backup.RestoreBackupAsync(row.FilePath);
                succeeded = true;
                AppHost.Logging.LogOperation("Restore", "ADOPTED", $"File={row.FileName}");
                LogBackup($"Restore adopted OK. File={row.FileName}");
            }
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogOperation("Restore", "FAILED", ex.Message);
            LogBackupError("Backup restore failed.", ex);
            errorMessage = ex.Message;
            succeeded = false;
        }
        finally
        {
            // Success hands off to a new login session. Failure deliberately keeps
            // the company session closed and leaves the main shell in recovery mode.
            if (succeeded)
            {
                ReleaseIsolation();
                LogBackup("Restore succeeded: shell isolation released for login handoff.");
            }
            else
            {
                try
                {
                    var main = WpfApp.Current?.Windows.OfType<MWin>().FirstOrDefault();
                    main?.EnterRestoreRecoveryMode();
                }
                catch (Exception ex)
                {
                    AppHost.Logging.LogError("BkpWin", "Failed to enter restore recovery mode.", ex);
                }
                LogBackup("Restore failed: company session remains closed; shell stays in recovery mode.");
            }
            SetBusy(false, freezeShell: false);
        }

        if (succeeded)
        {
            LogBackup($"Restore SUCCESS. Company={CompanyCode}; File={row.FileName} — opening login.");
            MessageBox.Show(
                L("RestoreSuccess", "Restore completed successfully.\n\nThe login window will open for the restored company."),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            OpenLoginAfterRestore();
            return;
        }

        LogBackup("Restore FAILED. Company=" + CompanyCode + "; File=" + row.FileName + "; Reason=" + (errorMessage ?? "unknown"));
        MessageBox.Show(
            Lf("RestoreFailed", "Restore failed.\n\n{0}\n\nCurrent company data was not replaced.", errorMessage ?? L("UnknownError", "Unknown error.")),
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        AppHost.Logging.LogOperation("Restore", "RECOVERY_MODE", "Main shell remains open and frozen; restore window stays available for diagnostics.");
        // Keep this window open after failure so the user can inspect the
        // operation status/log and explicitly close it. The main shell remains
        // in RestoreRecoveryMode as required by the restore state machine.
    }

    /// <summary>
    /// Small confirmation window: secondary windows will close and main shell will freeze.
    /// </summary>
    private bool ConfirmIsolateForSnapshot(bool isRestore, string? backupName = null)
    {
        var secondaryCount = CountSecondaryWindows();
        string secondary = secondaryCount > 0
            ? Lf("OpenSecondaryCount", "Open secondary windows: {0}\n\n", secondaryCount)
            : string.Empty;

        string body;
        if (isRestore)
        {
            var nameBlock = string.IsNullOrWhiteSpace(backupName) ? string.Empty : backupName + "\n\n";
            body = Lf("ConfirmRestore",
                "Restore encrypted backup?\n\n{0}To avoid conflicts with open operations:\n• All secondary windows will be closed\n• The main window will be frozen until the operation finishes\n• The current database session will be closed, then reopened after restore\n• The package will be verified as ShouTech, then adopted\n\n{1}Continue?",
                nameBlock, secondary);
        }
        else
        {
            body = Lf("ConfirmCreate",
                "Create a full encrypted company mirror (STB)?\n\nTo avoid conflicts with open operations:\n• All secondary windows will be closed\n• The main window will be frozen until the backup finishes\n• The current database session will be closed for a consistent snapshot, then reopened\n• A verified encrypted package will be written\n\n{0}Continue?",
                secondary);
        }

        var result = MessageBox.Show(
            body,
            Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }

    private static int CountSecondaryWindows()
    {
        try
        {
            return WpfApp.Current?.Windows
                .OfType<Window>()
                .Count(w => w != null
                            && w.IsVisible
                            && w is not MWin
                            && w is not BkpWin
                            && w is not LogWin) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Close secondary windows and freeze main shell so no concurrent company work runs during snapshot.
    /// </summary>
    private void IsolateForSnapshot()
    {
        CloseSecondaryWindows();
        FreezeMainShell();
    }

    private void ReleaseIsolation()
    {
        UnfreezeMainShell();
    }

    private void CloseSecondaryWindows()
    {
        try
        {
            var windows = WpfApp.Current?.Windows.OfType<Window>().ToList() ?? new System.Collections.Generic.List<Window>();
            foreach (var w in windows)
            {
                if (w == null || ReferenceEquals(w, this))
                    continue;
                if (w is MWin || w is LogWin || w is BkpWin)
                    continue;
                if (!w.IsVisible)
                    continue;
                try
                {
                    w.Close();
                }
                catch (Exception ex)
                {
                    AppHost.Logging.LogError("BkpWin", $"Failed to close secondary window {w.GetType().Name}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogError("BkpWin", "CloseSecondaryWindows failed", ex);
        }
    }

    private void FreezeMainShell()
    {
        try
        {
            var main = WpfApp.Current?.Windows.OfType<MWin>().FirstOrDefault(w => w.IsVisible)
                       ?? WpfApp.Current?.MainWindow as MWin;
            if (main == null)
                return;

            if (main.DataContext is MainShVM vm)
            {
                try { vm.FreezeForCompanySwitch(); }
                catch
                {
                    try { main.FreezeForCompanySwitch(); } catch { /* ignore */ }
                }
            }
            else
            {
                try { main.FreezeForCompanySwitch(); } catch { /* ignore */ }
            }

            main.IsEnabled = false;
            _shellFrozenByUs = true;
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogError("BkpWin", "FreezeMainShell failed", ex);
        }
    }

    private void UnfreezeMainShell()
    {
        if (!_shellFrozenByUs)
            return;

        try
        {
            var main = WpfApp.Current?.Windows.OfType<MWin>().FirstOrDefault()
                       ?? WpfApp.Current?.MainWindow as MWin;
            if (main == null)
            {
                _shellFrozenByUs = false;
                return;
            }

            main.IsEnabled = true;
            if (main.DataContext is MainShVM vm)
            {
                try { vm.UnfreezeAfterCompanyLogin(); }
                catch
                {
                    try { main.UnfreezeAfterCompanyLogin(); } catch { /* ignore */ }
                }
            }
            else
            {
                try { main.UnfreezeAfterCompanyLogin(); } catch { /* ignore */ }
            }

            try { main.Activate(); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogError("BkpWin", "UnfreezeMainShell failed", ex);
        }
        finally
        {
            _shellFrozenByUs = false;
        }
    }

    /// <summary>
    /// Clear session and open company selection / login after successful restore.
    /// </summary>
    private void OpenLoginAfterRestore()
    {
        try
        {
            AppHost.Logging.LogOperation("Restore", "LOGIN_HANDOFF_START", "Preparing fresh login for restored company.");

            AppState.CurrentUser = null;
            var code = AppState.CurrentCompanyCode;
            var name = AppState.CurrentCompanyName;
            var nameEn = AppState.CurrentCompanyNameEn;

            // IMPORTANT: never close the only live shell while ShutdownMode is
            // OnMainWindowClose. Keep the shell alive, frozen, and hand it to
            // LogWin as the inactive main window. This prevents restore from
            // terminating the entire process.
            WpfApp.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var main = WpfApp.Current.Windows.OfType<MWin>().FirstOrDefault(w => w.IsVisible)
                       ?? WpfApp.Current.MainWindow as MWin;
            if (main == null)
                throw new InvalidOperationException("Main shell was not found during restore login handoff.");

            main.IsEnabled = false;
            AppState.InactiveMainWindow = main;

            AppState.SetCompany(code, name, nameEn);
            WpfApp.Current.Properties["SelectedCompany"] = code ?? string.Empty;

            var loginWindow = new LogWin
            {
                Owner = main,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            // Main shell stays alive. Login owns the handoff; on successful
            // authentication LogWin reactivates the same shell.
            WpfApp.Current.MainWindow = main;
            loginWindow.Show();
            loginWindow.Activate();
            loginWindow.Focus();

            AppHost.Logging.LogOperation("Restore", "LOGIN_HANDOFF_READY", $"Company={code}; LoginWindow={loginWindow.GetType().Name}");
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogOperation("Restore", "LOGIN_HANDOFF_FAILED", ex.Message);
            AppHost.Logging.LogError("BkpWin", "Failed to open login after restore.", ex);
            MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CompanyCode))
            return;

        try
        {
            var path = AppHost.Backup.GetBackupDirectory(CompanyCode);
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppHost.Logging.LogError("BkpWin", "Failed to open backup directory.", ex);
            MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RebuildIndexAsync();

    private static void LogBackup(string message) =>
        AppHost.Logging.LogInfo("BkpWin", message);

    private static void LogBackupError(string message, Exception? ex = null) =>
        AppHost.Logging.LogError("BkpWin", message, ex);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            var confirm = MessageBox.Show(
                L("CloseWhileBusy", "An operation is running. Close anyway?"),
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
                return;
        }

        ReleaseIsolation();
        Close();
    }

    private void SetBusy(bool busy, bool freezeShell)
    {
        _busy = busy;
        btnCreate.IsEnabled = !busy;
        btnRefresh.IsEnabled = !busy;
        btnRestore.IsEnabled = !busy;
        btnOpenFolder.IsEnabled = !busy;
        btnClose.IsEnabled = !busy;
        txtNotes.IsEnabled = !busy;
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        // Keep backup window usable for status while shell is frozen
        if (freezeShell && busy)
            IsEnabled = true;
    }

    // ═══════════════════════════════════════════════════════════════
    //
    //
    // ═══════════════════════════════════════════════════════════════
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            var main = WpfApp.Current?.Windows
                .OfType<MWin>()
                .FirstOrDefault(w => w.IsVisible)
                ?? WpfApp.Current?.MainWindow as MWin;

            if (main != null && !main.IsEnabled)
            {
                main.IsEnabled = true;

                if (main.DataContext is MainShVM vm)
                {
                    try { vm.UnfreezeAfterCompanyLogin(); }
                    catch
                    {
                        try { main.UnfreezeAfterCompanyLogin(); } catch { }
                    }
                }
                else
                {
                    try { main.UnfreezeAfterCompanyLogin(); } catch { }
                }

                try { main.Activate(); } catch { }
            }
            else
            {
                try { ReleaseIsolation(); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { AppHost.Logging.LogError("BkpWin", "OnClosed unfreeze failed", ex); }
            catch { }
        }
        finally
        {
            _shellFrozenByUs = false;
        }

        base.OnClosed(e);
    }

    private sealed class BkpRow
    {
        private readonly BkpFileInfo _source;
        public BkpRow(BkpFileInfo source) => _source = source;
        public string FilePath => _source.FilePath;
        public string FileName => _source.FileName;
        public bool IsValid { get => _source.IsValid; set => _source.IsValid = value; }
        public string Status { get => _source.Status; set => _source.Status = value; }
        public string LocalDateText => _source.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string SizeText => FormatSize(_source.SizeBytes);
        private static string FormatSize(long bytes)
        {
            const double kb = 1024d;
            const double mb = kb * 1024d;
            const double gb = mb * 1024d;
            return bytes >= gb ? $"{bytes / gb:0.##} GB" : bytes >= mb ? $"{bytes / mb:0.##} MB" : bytes >= kb ? $"{bytes / kb:0.##} KB" : $"{bytes} B";
        }
    }
}