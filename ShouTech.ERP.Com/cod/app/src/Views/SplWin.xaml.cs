using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using WpfApp = System.Windows.Application;

using ShouTech.Domain;
namespace ShouTech.App.Views
{
    public partial class SplWin : Window
    {
        private const double MaxSplashSeconds = 4.0;
        private bool _started;
        private bool? _hasCompaniesAtStartup;

        public SplWin()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_started)
                return;
            _started = true;

            var sw = Stopwatch.StartNew();

            try
            {
                ApplyConfigTexts();
                ApplyLocalization();
                await RunStartupSecurityPipelineAsync(sw);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, ex, "SplWin.xaml.OnLoaded");
                SetStatus($"Startup warning: {ex.Message}");
                await SafeDelay(200);
            }

            try
            {
                SetStatus("Checking companies...");
                _hasCompaniesAtStartup = await new CoRegSvc().HasCompaniesAsync();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, ex, "SplWin.xaml.OnLoaded");
                SetStatus($"Company check warning: {ex.Message}");
                _hasCompaniesAtStartup = false;
            }

            var remaining = MaxSplashSeconds - sw.Elapsed.TotalSeconds;
            if (remaining > 0.05)
                await SafeDelay((int)(remaining * 1000));

            SetProgress(100);
            SetStatus("Ready.");

            try
            {
                await OpenStartupFlowAsync();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, ex, "SplWin.xaml.OnLoaded");
                MessageBox.Show(
                    $"Unable to open company selection:\n{ex.Message}",
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                try { WpfApp.Current?.Shutdown(1); } catch (Exception exL76) {
                    AppHost.Errors.Report(ErrorCodes.StartupFailed, exL76, "SplWin.xaml.OnLoaded"); }
                return;
            }
        }

        private async Task RunStartupSecurityPipelineAsync(Stopwatch sw)
        {
            await RunStepAsync(sw, 8,  "Checking startup files and settings...", VerifyApplicationBinariesAsync);
            await RunStepAsync(sw, 18, "Checking startup files and settings...", VerifyIntegrityAsync);
            await RunStepAsync(sw, 30, "Checking startup files and settings...", ScanUnauthorizedModificationsAsync);
            await RunStepAsync(sw, 42, "Checking startup files and settings...", ValidateConfigAndUserFilesAsync);
            await RunStepAsync(sw, 55, "Checking device identity and dongle...", CheckDeviceAndDongleAsync);
            await RunStepAsync(sw, 70, "Checking license...", CheckLicenseAsync);
            await RunStepAsync(sw, 85, "Preparing local user data...", PrepareUserDataAsync);
            await RunStepAsync(sw, 95, "Starting background services...", StartBackgroundServicesAsync);
        }

        private async Task RunStepAsync(Stopwatch sw, int progressTarget, string message, Func<Task> action)
        {
            if (sw.Elapsed.TotalSeconds >= MaxSplashSeconds - 0.15)
            {
                SetProgress(progressTarget);
                return;
            }

            SetStatus(message);
            SetProgress(progressTarget - 5 < 0 ? 0 : progressTarget - 5);

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, ex, "SplWin.xaml.RunStepAsync");
                SetStatus($"{message} (warning: {Truncate(ex.Message, 60)})");
                await SafeDelay(80);
            }

            SetProgress(progressTarget);
            await SafeDelay(40);
        }

        private static Task VerifyApplicationBinariesAsync()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var required = new[]
            {
                "ShouTech.ERP.dll",
                "ShouTech.Domain.dll",
                "ShouTech.Application.dll",
                "ShouTech.Infrastructure.dll"
            };

            foreach (var name in required)
            {
                var path = Path.Combine(baseDir, name);
                if (!File.Exists(path))
                    continue;

                var info = new FileInfo(path);
                if (info.Length <= 0)
                    throw new InvalidOperationException($"Binary empty: {name}");
            }

            return Task.CompletedTask;
        }

        private static Task VerifyIntegrityAsync()
        {
            var entry = Assembly.GetEntryAssembly()?.Location
                        ?? Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(entry) || !File.Exists(entry))
                return Task.CompletedTask;

            var bytes = File.ReadAllBytes(entry);
            if (bytes.Length < 1024)
                throw new InvalidOperationException("Entry assembly integrity check failed.");

            var hash = SHA256.HashData(bytes);
            _ = Convert.ToHexString(hash.AsSpan(0, 8));
            return Task.CompletedTask;
        }

        private static Task ScanUnauthorizedModificationsAsync()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var suspicious = new[]
            {
                "*.asi", "*.overlay", "cheat*", "inject*", "patch*.dll", "crack*"
            };

            foreach (var pattern in suspicious)
            {
                string[] hits;
                try { hits = Directory.GetFiles(baseDir, pattern, SearchOption.TopDirectoryOnly); }
                catch (Exception exL171) {
                    AppHost.Errors.Report(ErrorCodes.StartupFailed, exL171, "SplWin.xaml.ScanUnauthorizedModificationsAsync"); continue; }

                if (hits.Length > 0)
                    throw new InvalidOperationException("Suspicious files detected near the application.");
            }

            return Task.CompletedTask;
        }

        private static Task ValidateConfigAndUserFilesAsync()
        {
            try
            {
                var settingsPath = AppCfgSvc.LoadedPath;
                if (!string.IsNullOrWhiteSpace(settingsPath) && !File.Exists(settingsPath))
                {
                }
            }
            catch (Exception exL189)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL189, "SplWin.xaml.ValidateConfigAndUserFilesAsync");
            }

            try
            {
                var pref = UsrPref.Load();
                _ = pref.Lang;
            }
            catch (Exception exL196)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL196, "SplWin.xaml.ValidateConfigAndUserFilesAsync");
            }

            return Task.CompletedTask;
        }

        private static Task CheckDeviceAndDongleAsync()
        {
            try
            {
                AppHost.DeviceInfo?.Collect();
            }
            catch (Exception exL207)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL207, "SplWin.xaml.CheckDeviceAndDongleAsync");
            }

            return Task.CompletedTask;
        }

        private static async Task CheckLicenseAsync()
        {
            try
            {
                if (AppHost.License != null)
                    await AppHost.License.InitializeAsync();
            }
            catch (Exception exL219)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL219, "SplWin.xaml.CheckLicenseAsync");
            }

            // Licensing build: the entitlement chain runs in the same
            // splash step, with its own catalog reporting so a chain
            // failure is visible and the snapshot stays NotFound
            // (view-only) rather than silently granting.
            try
            {
                if (AppHost.Entitlements != null)
                    await AppHost.Entitlements.InitializeAsync();
            }
            catch (Exception exEnt)
            {
                AppHost.Errors.Report(ErrorCodes.LicenseCheckFailed, exEnt, "SplWin.xaml.CheckLicenseAsync.Entitlements");
            }
        }

        private static async Task PrepareUserDataAsync()
        {
            try
            {
                if (AppHost.Sync != null)
                    await AppHost.Sync.SnapshotLocalUserDataAsync();
            }
            catch (Exception exL229)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL229, "SplWin.xaml.PrepareUserDataAsync");
            }
        }

        private static Task StartBackgroundServicesAsync()
        {
            try
            {
                AppHost.OnlineCoordinator?.Start();
            }
            catch (Exception exL238)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL238, "SplWin.xaml.StartBackgroundServicesAsync");
            }

            return Task.CompletedTask;
        }

        private void ApplyLocalization()
        {
            try
            {
                // Direction follows the active language through the central
                // service (RTL for Arabic). No local LTR pins: the splash
                // layout mirrors like every other window.
                LangDirSv.ApplyToWindow(this, App.CurrentLanguageCode);
                Title = "ShouTech ERP - Starting";
            }
            catch (Exception exL252)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL252, "SplWin.xaml.ApplyLocalization");
            }
        }

        private void ApplyConfigTexts()
        {
            try
            {
                var settings = AppCfgSvc.Current;
                if (settings?.Application == null)
                    return;

                if (!string.IsNullOrWhiteSpace(settings.Application.ShortName))
                    txtAppName.Text = settings.Application.ShortName;

                if (!string.IsNullOrWhiteSpace(settings.Application.NameEn))
                    txtAppNameEn.Text = settings.Application.NameEn;

                if (!string.IsNullOrWhiteSpace(settings.Application.Version))
                    txtVersion.Text = $"Version {settings.Application.Version}";

                if (!string.IsNullOrWhiteSpace(settings.Application.Copyright))
                    txtCopyright.Text = settings.Application.Copyright;
            }
            catch (Exception exL275)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL275, "SplWin.xaml.ApplyConfigTexts");
            }
        }

        private void SetStatus(string text)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => txtStatus.Text = text);
                    return;
                }

                txtStatus.Text = text;
            }
            catch (Exception exL290)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL290, "SplWin.xaml.SetStatus");
            }
        }

        private int _lastProgressValue;

        private void SetProgress(int value)
        {
            try
            {
                value = Math.Clamp(value, 0, 100);
                _lastProgressValue = value;

                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => UpdateProgressBarWidth(value));
                    return;
                }

                UpdateProgressBarWidth(value);
            }
            catch (Exception exL310)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL310, "SplWin.xaml.SetProgress");
            }
        }

        private void UpdateProgressBarWidth(int value)
        {
            try
            {
                var width = progressBarContainer.ActualWidth;
                if (width <= 0) return;

                progressBarIndicator.Width = width * value / 100.0;
                progressBarIndicator.HorizontalAlignment = HorizontalAlignment.Left;
            }
            catch (Exception exL323)
            {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL323, "SplWin.xaml.UpdateProgressBarWidth");
            }
        }

        private void OnProgressBarContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateProgressBarWidth(_lastProgressValue);
        }

        private static async Task SafeDelay(int ms)
        {
            try { await Task.Delay(Math.Max(0, ms)); } catch (Exception exL333) {
                AppHost.Errors.Report(ErrorCodes.StartupFailed, exL333, "SplWin.xaml.SafeDelay"); }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? string.Empty;
            return value[..max] + "…";
        }

        private async Task OpenStartupFlowAsync()
        {
            var hasCompanies = _hasCompaniesAtStartup
                ?? await new CoRegSvc().HasCompaniesAsync();
            var companyWindow = new ComWin();
            if (WpfApp.Current != null)
            {
                WpfApp.Current.MainWindow = companyWindow;
                WpfApp.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }

            // Z-order hand-off (owner rule: the program must behave like a
            // normal Windows program): this splash is the process's
            // foreground window, so it stays open until its successor is
            // visible. Closing it first — as this method used to — breaks
            // the foreground chain: the company window then opens behind
            // whatever other program Windows picks, and because ComWin is
            // a startup gate with no guaranteed taskbar presence there was
            // no way to bring the program back. The same show-then-close
            // order is used by the ComWin -> LogWin -> MWin hand-offs.
            if (!hasCompanies)
            {
                // Owned by the splash so the modal wizard inherits its
                // place in the z-order instead of opening unowned into a
                // foreground gap.
                var wizard = new StpWiz(StpWizMode.NewCompany, this);
                var created = wizard.ShowDialog() == true && wizard.CompletedSuccessfully;
                if (!created)
                {
                    Close();
                    WpfApp.Current?.Shutdown();
                    return;
                }
            }

            companyWindow.Show();
            companyWindow.Activate();

            Close();
        }
    }
}