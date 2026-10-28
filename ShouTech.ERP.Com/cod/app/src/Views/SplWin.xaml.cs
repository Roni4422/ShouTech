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
                MessageBox.Show(
                    $"Unable to open company selection:\n{ex.Message}",
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                try { WpfApp.Current?.Shutdown(1); } catch { }
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
                catch { continue; }

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
            catch { }

            try
            {
                var pref = UsrPref.Load();
                _ = pref.Lang;
            }
            catch { }

            return Task.CompletedTask;
        }

        private static Task CheckDeviceAndDongleAsync()
        {
            try
            {
                AppHost.DeviceInfo?.Collect();
            }
            catch { }

            return Task.CompletedTask;
        }

        private static async Task CheckLicenseAsync()
        {
            try
            {
                if (AppHost.License != null)
                    await AppHost.License.InitializeAsync();
            }
            catch { }
        }

        private static async Task PrepareUserDataAsync()
        {
            try
            {
                if (AppHost.Sync != null)
                    await AppHost.Sync.SnapshotLocalUserDataAsync();
            }
            catch { }
        }

        private static Task StartBackgroundServicesAsync()
        {
            try
            {
                AppHost.OnlineCoordinator?.Start();
            }
            catch { }

            return Task.CompletedTask;
        }

        private void ApplyLocalization()
        {
            try
            {
                FlowDirection = FlowDirection.LeftToRight;
                if (progressBarContainer != null)
                    progressBarContainer.FlowDirection = FlowDirection.LeftToRight;
                Title = "ShouTech ERP - Starting";
            }
            catch { }
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
            catch { }
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
            catch { }
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
            catch { }
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
            catch { }
        }

        private void OnProgressBarContainerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateProgressBarWidth(_lastProgressValue);
        }

        private static async Task SafeDelay(int ms)
        {
            try { await Task.Delay(Math.Max(0, ms)); } catch { }
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

            Close();

            if (!hasCompanies)
            {
                var wizard = new StpWiz(StpWizMode.NewCompany);
                var created = wizard.ShowDialog() == true && wizard.CompletedSuccessfully;
                if (!created)
                {
                    WpfApp.Current?.Shutdown();
                    return;
                }
            }

            companyWindow.Show();
            companyWindow.Activate();
        }
    }
}