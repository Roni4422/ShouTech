// ========================================================================
// FILE: App.xaml.cs
// PROJECT: SHOUTECH ERP V10
// PURPOSE: Application bootstrap, DI container, lifecycle, unhandled
//          exception handling, custom title bar color, and schema bootstrap.
// ========================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShouTech.App.Common;
using ShouTech.App.Configuration;
using ShouTech.App.Localization;
using ShouTech.App.Services;
using ShouTech.App.Views;

namespace ShouTech.App
{
    // ========================================================================
    //
    // ========================================================================
    internal static class SchemaBootstrapper
    {
        private static readonly object _lock = new();
        private static readonly HashSet<string> _appliedDbs
            = new(StringComparer.OrdinalIgnoreCase);

        private const string SqliteResource = "ShouTech.App.dba.lite.sql";
        private const string PgResource     = "ShouTech.App.dba.pg.sql";

        /// <summary>
        /// </summary>
        public static void EnsureSchema()
        {
            try
            {
                var cs = DatabaseRuntime.GetConnectionString();
                if (string.IsNullOrWhiteSpace(cs))
                {
                    SafeLogWarning("No connection string — skipping schema bootstrap.");
                    return;
                }

                var dbKey = BuildDbKey(cs);

                lock (_lock)
                {
                    if (_appliedDbs.Contains(dbKey))
                        return;

                    bool isPg = DatabaseRuntime.IsPostgreSql();
                    var resName = isPg ? PgResource : SqliteResource;
                    var sql = LoadSql(resName);

                    if (string.IsNullOrWhiteSpace(sql))
                    {
                        SafeLogWarning($"Embedded resource not found or empty: {resName}");
                        return;
                    }

                    if (isPg)
                        ExecPg(cs, sql);
                    else
                        ExecSqlite(cs, sql);

                    _appliedDbs.Add(dbKey);
                    SafeLogInfo($"Schema applied: {(isPg ? "PostgreSQL" : "SQLite")} [{dbKey}]");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    AppHost.Logging?.LogError(
                        "SchemaBootstrapper",
                        "Schema bootstrap failed.",
                        ex);
                }
                catch { System.Diagnostics.Debug.WriteLine("Failed to log schema bootstrap failure"); }
                throw;
            }
        }

        /// <summary>
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _appliedDbs.Clear();
            }
        }

        /// <summary>
        /// </summary>
        public static void Reset(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return;

            var dbKey = BuildDbKey(connectionString);
            lock (_lock)
            {
                _appliedDbs.Remove(dbKey);
            }
        }

        // --------------------------------------------------------------------
        private static string BuildDbKey(string connectionString)
        {
            try
            {
                var m = Regex.Match(
                    connectionString,
                    @"(?:Data\s+Source|Filename)\s*=\s*([^;]+)",
                    RegexOptions.IgnoreCase);

                if (m.Success)
                    return "sqlite:" + m.Groups[1].Value.Trim().ToLowerInvariant();

                m = Regex.Match(
                    connectionString,
                    @"(?:Database|Initial\s+Catalog)\s*=\s*([^;]+)",
                    RegexOptions.IgnoreCase);

                if (m.Success)
                    return "pg:" + m.Groups[1].Value.Trim().ToLowerInvariant();
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Database fingerprint probe failed", ex); }

            // Fallback
            return "cs:" + connectionString.GetHashCode().ToString(
                "X8",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // --------------------------------------------------------------------
        private static string LoadSql(string resourceName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) return string.Empty;

                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        // --------------------------------------------------------------------
        private static void ExecSqlite(string connectionString, string sql)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText =
                    "PRAGMA foreign_keys = ON;" +
                    "PRAGMA journal_mode = WAL;" +
                    "PRAGMA synchronous = NORMAL;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 180;
            cmd.ExecuteNonQuery();
        }

        // --------------------------------------------------------------------
        private static void ExecPg(string connectionString, string sql)
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 300;
            cmd.ExecuteNonQuery();
        }

        // --------------------------------------------------------------------
        private static void SafeLogInfo(string msg)
        {
            try { AppHost.Logging?.LogInfo("SchemaBootstrapper", msg); } catch { System.Diagnostics.Debug.WriteLine("SafeLogInfo failed"); }
        }

        private static void SafeLogWarning(string msg)
        {
            try { AppHost.Logging?.LogWarning("SchemaBootstrapper", msg); } catch { System.Diagnostics.Debug.WriteLine("SafeLogWarning failed"); }
        }
    }

    // ========================================================================
    //  Application
    // ========================================================================
    public partial class App : System.Windows.Application
    {
        private IServiceProvider? _serviceProvider;

        // ====================================================================
        // GLOBAL LOCALIZATION SERVICE
        // ====================================================================
        public ILocSvc Localization { get; } = new LocSvc();

        // ====================================================================
        // CURRENT LANGUAGE
        // ====================================================================
        public static string CurrentLanguageCode { get; private set; } = "en";

        // ====================================================================
        // APPLICATION LANGUAGE
        // ====================================================================
        public static void SetApplicationLanguage(string? langCode)
        {
            langCode = NormalizeLanguageCode(langCode);
            CurrentLanguageCode = langCode;

            try { LangDirSv.ApplyCulture(langCode); }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "ApplyCulture failed during language change", ex); }

            try
            {
                if (Current is App app)
                    app.Localization.SetLanguage(langCode);
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Localization.SetLanguage failed", ex); }

            void ApplyUi()
            {
                try { LangDirSv.ApplyToAllWindows(langCode); } catch (Exception ex) { AppHost.Logging?.LogError("App", "LangDirSv.ApplyToAllWindows failed", ex); }
                try { UiLoczr.ScheduleApplyToAllWindows(); } catch (Exception ex) { AppHost.Logging?.LogError("App", "UiLoczr.ScheduleApplyToAllWindows failed", ex); }
            }

            try
            {
                if (Current?.Dispatcher != null)
                {
                    Current.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(ApplyUi));
                }
                else
                {
                    ApplyUi();
                }
            }
            catch
            {
                ApplyUi();
            }
        }

        // ====================================================================
        // LANGUAGE NORMALIZATION
        // ====================================================================
        private static string NormalizeLanguageCode(string? langCode)
        {
            var code = (langCode ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            return code switch
            {
                "ar" or "ar-sa" or "ar-kw" or "ar-ae" or "ar-eg" => "ar",
                "en" or "en-us" or "en-gb"                       => "en",
                "fr" or "fr-fr"                                  => "fr",
                "tr" or "tr-tr"                                  => "tr",
                _                                                => "en"
            };
        }

        // ====================================================================
        // APPLICATION STARTUP
        // ====================================================================
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // ---------- Global Western-digit policy ----------
            // Keep the selected UI language and RTL/LTR layout unchanged, but
            // render and accept digits as 0-9 throughout the application.
            try
            {
                EventManager.RegisterClassHandler(
                    typeof(TextBox),
                    TextCompositionManager.PreviewTextInputEvent,
                    new TextCompositionEventHandler(GlobalWesternDigitTextInput),
                    handledEventsToo: true);
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Western-digit handler registration failed", ex); }

            // ---------- Global window logging + direction ----------
            try
            {
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(GlobalWindowLoaded),
                    handledEventsToo: true);

                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.UnloadedEvent,
                    new RoutedEventHandler(GlobalWindowUnloaded),
                    handledEventsToo: true);
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Window lifecycle handler registration failed", ex); }

            // ---------- Global exception handlers ----------
            AppDomain.CurrentDomain.UnhandledException +=
                CurrentDomain_UnhandledException;

            DispatcherUnhandledException +=
                Dispatcher_UnhandledException;

            TaskScheduler.UnobservedTaskException +=
                TaskScheduler_UnobservedTaskException;

            // ═══════════════════════════════════════════════════════════════
            // 1) Read the preferences and adopt the language **before** any other initialization.
            //
            //    This ensures App.CurrentLanguageCode is correct before:
            //      - building the SchemaBootstrapper
            //      • AppHost.Initialize
            //      - opening any window (Splash / StpWiz / ComWin / LogWin / MWin)
            //
            //    Any window opened later inherits the correct direction via
            //    GlobalWindowLoaded + LangDirSv + VisuId.ApplyWindow.
            // ═══════════════════════════════════════════════════════════════
            UsrPref pref;

            try
            {
                pref = UsrPref.Load();

                if (string.IsNullOrWhiteSpace(pref.Lang))
                    pref.Lang = "en";

                pref.Lang = NormalizeLanguageCode(pref.Lang);
            }
            catch
            {
                pref = new UsrPref
                {
                    Lang = "en",
                    LangSet = false
                };
            }

            // Adopt the language immediately (before any window).
            // SetApplicationLanguage sets CurrentLanguageCode + Culture +
            // Localization.SetLanguage (raises LanguageChanged) + ApplyToAllWindows.
            SetApplicationLanguage(pref.Lang);

            // ---------- Dependency Injection ----------
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            try
            {
                SchemaBootstrapper.EnsureSchema();
                // Install database-level audit triggers before any business screen can mutate data.
                // A missing/unselected company database must not prevent the application from starting.
                try
                {
                    RecycleBinSvc.InitializeAuditInfrastructureAsync().GetAwaiter().GetResult();
                }
                catch (Exception auditEx)
                {
                    try { AppHost.Logging?.LogWarning("Audit", "Delete-audit bootstrap deferred until a company database is available: " + auditEx.Message); } catch { System.Diagnostics.Debug.WriteLine("Delete-audit bootstrap warning failed"); }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    AppHost.Logging?.LogError(
                        "App",
                        "Schema bootstrap failed at startup.",
                        ex);
                }
                catch { System.Diagnostics.Debug.WriteLine("Startup schema failure log failed"); }

                try
                {
                    MessageBox.Show(
                        "Failed to initialize database schema:\n\n" + ex.Message,
                        "ShouTech ERP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch (Exception ex2) { AppHost.Logging?.LogError("App", "Startup failure dialog failed", ex2); }

                try { Shutdown(1); } catch (Exception ex2) { AppHost.Logging?.LogError("App", "Shutdown after startup failure failed", ex2); }
                return;
            }

            // ---------- Initialize Application Host ----------
            AppHost.Initialize();

            try
            {
                AppHost.Logging?.LogLifecycle(
                    "Application startup",
                    $"Args={e.Args.Length}; BaseDirectory={AppDomain.CurrentDomain.BaseDirectory}; " +
                    $"Lang={CurrentLanguageCode}");
            }
            catch { System.Diagnostics.Debug.WriteLine("Startup lifecycle log failed"); }

            // ---------- Run startup sequence ----------
            // Pass pref so the file is not read again and the language stays consistent.
            RunStartupSequence(pref);
        }

        // ====================================================================
        // DOMAIN / PROCESS UNHANDLED EXCEPTION
        // ====================================================================
        private static void CurrentDomain_UnhandledException(
            object sender,
            UnhandledExceptionEventArgs args)
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                {
                    AppHost.Logging.LogError(
                        "Global",
                        $"Unhandled AppDomain exception. IsTerminating={args.IsTerminating}",
                        ex);

                    AppHost.Logging.LogLifecycle(
                        "Unhandled exception",
                        ex.GetType().FullName ?? ex.GetType().Name);

                    MessageBox.Show(
                        ex.Message,
                        "Unexpected Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                else
                {
                    AppHost.Logging.LogError(
                        "Global",
                        $"Unhandled AppDomain exception object. IsTerminating={args.IsTerminating}");
                }
            }
            catch { System.Diagnostics.Debug.WriteLine("AppDomain unhandled-exception handler failed"); }
        }

        // ====================================================================
        // UNOBSERVED TASK EXCEPTION
        // ====================================================================
        private static void TaskScheduler_UnobservedTaskException(
            object? sender,
            UnobservedTaskExceptionEventArgs args)
        {
            try
            {
                AppHost.Logging.LogError(
                    "Global",
                    "Unobserved task exception.",
                    args.Exception);

                args.SetObserved();
            }
            catch { System.Diagnostics.Debug.WriteLine("Unobserved-task handler failed"); }
        }

        // ====================================================================
        // GLOBAL WESTERN-DIGIT POLICY
        // ====================================================================
        private static void ApplyWesternDigitPresentation(Window window)
        {
            try
            {
                NumberSubstitution.SetCultureSource(window, NumberCultureSource.Override);
                NumberSubstitution.SetCultureOverride(window, CultureInfo.GetCultureInfo("en-US"));
                NumberSubstitution.SetSubstitution(window, NumberSubstitutionMethod.European);
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Western digit presentation failed", ex); }
        }

        private static string ConvertToWesternDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= '\u0660' && chars[i] <= '\u0669')
                    chars[i] = (char)('0' + chars[i] - '\u0660');
                else if (chars[i] >= '\u06F0' && chars[i] <= '\u06F9')
                    chars[i] = (char)('0' + chars[i] - '\u06F0');
            }

            return new string(chars);
        }

        private static void GlobalWesternDigitTextInput(object sender, TextCompositionEventArgs e)
        {
            try
            {
                if (sender is not TextBox textBox ||
                    Window.GetWindow(textBox) is MatCard ||
                    string.IsNullOrEmpty(e.Text))
                    return;

                var western = ConvertToWesternDigits(e.Text);
                if (string.Equals(western, e.Text, StringComparison.Ordinal)) return;

                e.Handled = true;
                var start = textBox.SelectionStart;
                textBox.SelectedText = western;
                textBox.CaretIndex = start + western.Length;
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Western digit text input failed", ex); }
        }

        // ====================================================================
        // GLOBAL WINDOW LOGGING + DIRECTION
        // ====================================================================
        private static void GlobalWindowLoaded(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                if (sender is Window window)
                {
                    ApplyWesternDigitPresentation(window);
                    // THE shell-context application point: every window —
                    // BaseWin-derived or Window-derived — receives the
                    // shell's CURRENT language/direction + context stamps
                    // here, resolved at open time from the central holders
                    // (see Services/ShellContext.cs).
                    ShellContext.ApplyToWindow(window);

                    AppHost.Logging.LogLifecycle(
                        "Window loaded",
                        $"Type={window.GetType().FullName}; " +
                        $"Title={window.Title}; " +
                        $"Main={ReferenceEquals(window, Current?.MainWindow)}; " +
                        $"Flow={window.FlowDirection}; " +
                        $"Lang={CurrentLanguageCode}; " +
                        $"Context={ShellContext.Describe(window)}");

                    if (window is not SplWin)
                        ApplyCustomTitleBarColor(window);

                    // Re-apply the shell context once the visual tree is
                    // fully built. This is what makes the custom chrome
                    // (VisuWindowStyle) adopt the correct direction even
                    // when the window was already created before the
                    // language changed.
                    window.Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(() =>
                        {
                            try
                            {
                                ShellContext.ApplyToWindow(window);
                            }
                            catch (Exception ex) { AppHost.Logging?.LogError("App", "Deferred shell-context apply failed", ex); }
                        }));
                }
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "GlobalWindowLoaded failed", ex); }
        }

        // ====================================================================
        // UNIFIED NATIVE TITLE BAR COLOR (Windows 11)
        // ====================================================================
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR    = 36;

        private static void ApplyCustomTitleBarColor(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int captionColor = 0x8B7D60;
                var result = DwmSetWindowAttribute(
                    hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                if (result != 0) return;

                int textColor = 0xFFFFFF;
                var textResult = DwmSetWindowAttribute(
                    hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
                if (textResult != 0) return;
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Title bar color apply failed", ex); }
        }

        private static void GlobalWindowUnloaded(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                if (sender is Window window)
                {
                    AppHost.Logging.LogLifecycle(
                        "Window unloaded",
                        $"Type={window.GetType().FullName}; Title={window.Title}");
                }
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "GlobalWindowUnloaded failed", ex); }
        }

        // ====================================================================
        // WPF DISPATCHER EXCEPTION
        // ====================================================================
        private static void Dispatcher_UnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs args)
        {
            try
            {
                AppHost.Logging.LogError(
                    "Global",
                    "Dispatcher unhandled exception.",
                    args.Exception);

                MessageBox.Show(
                    args.Exception.Message,
                    "Application Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex) { AppHost.Logging?.LogError("App", "Error dialog display failed", ex); }

            args.Handled = true;
        }

        // ====================================================================
        // DEPENDENCY INJECTION REGISTRATION
        // ====================================================================
        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ICompanyRegistryService, CoRegSvc>();
            services.AddSingleton<ICoRegSvc, CoRegSvc>();

            services.AddTransient<Lang>();
            services.AddTransient<SplWin>();
            services.AddTransient<ComWin>();
            services.AddTransient<LogWin>();
            services.AddTransient<MWin>();
        }

        // ====================================================================
        // STARTUP SEQUENCE
        // ====================================================================
        private void RunStartupSequence(UsrPref pref)
        {
            // The language was already set in OnStartup (SetApplicationLanguage).
            // Only the no-saved-language case (FirstRun) is handled here.
            if (!pref.LangSet)
            {
                bool accepted = false;
                string chosen = "en";

                try
                {
                    var langWin =
                        _serviceProvider?.GetService<Lang>()
                        ?? new Lang();

                    accepted = langWin.ShowDialog() == true;

                    if (accepted)
                        chosen = NormalizeLanguageCode(langWin.Selected);
                }
                catch
                {
                    accepted = false;
                }

                if (!accepted)
                {
                    try { Shutdown(); } catch (Exception ex) { AppHost.Logging?.LogError("App", "Shutdown after license rejection failed", ex); }
                    return;
                }

                pref.Lang = chosen;
                pref.LangSet = true;

                SetApplicationLanguage(chosen);

                try { pref.Save(); } catch (Exception ex) { AppHost.Logging?.LogError("App", "Preference save after language selection failed", ex); }
            }
            // else: the language is already adopted in OnStartup - no need to set it again.

            try
            {
                ShowSplashScreen();
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(
                        "Startup error: " + ex.Message,
                        "ShouTech ERP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch (Exception ex2) { AppHost.Logging?.LogError("App", "Warning dialog display failed", ex2); }

                try
                {
                    ShowCompanySelection();
                }
                catch
                {
                    try { ShowLogin(); }
                    catch { Shutdown(); }
                }
            }
        }

        // ====================================================================
        // SPLASH SCREEN
        // ====================================================================
        public void ShowSplashScreen()
        {
            try
            {
                var splash =
                    _serviceProvider?.GetService<SplWin>()
                    ?? new SplWin();

                splash.Show();
                splash.Activate();
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(
                        "Splash failed: " + ex.Message,
                        "ShouTech ERP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch (Exception ex2) { AppHost.Logging?.LogError("App", "Warning dialog display failed", ex2); }

                ShowCompanySelection();
            }
        }

        // ====================================================================
        // COMPANY SELECTION
        // ====================================================================
        public void ShowCompanySelection()
        {
            if (_serviceProvider is null)
                throw new InvalidOperationException(
                    "The dependency injection container has not been initialized.");

            foreach (Window window in Current.Windows)
            {
                if (window is ComWin existing)
                {
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;

                    if (!existing.IsVisible)
                        existing.Show();

                    existing.Activate();
                    existing.Focus();

                    return;
                }
            }

            var companyWindow = _serviceProvider.GetRequiredService<ComWin>();

            companyWindow.Show();
            companyWindow.Activate();
            companyWindow.Focus();
        }

        // ====================================================================
        // ====================================================================
        public void ShowLogin()
        {
            if (_serviceProvider is null)
                throw new InvalidOperationException(
                    "The dependency injection container has not been initialized.");

            try
            {
                SchemaBootstrapper.EnsureSchema();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not prepare the company database:\n\n" + ex.Message,
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            foreach (Window window in Current.Windows)
            {
                if (window is LogWin existing)
                {
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;

                    if (!existing.IsVisible)
                        existing.Show();

                    existing.Activate();
                    existing.Focus();

                    return;
                }
            }

            var loginWindow = _serviceProvider.GetRequiredService<LogWin>();

            loginWindow.Show();
            loginWindow.Activate();
            loginWindow.Focus();
        }

        // ====================================================================
        // ====================================================================
        public void ShowMainApp()
        {
            if (_serviceProvider is null)
                throw new InvalidOperationException(
                    "The dependency injection container has not been initialized.");

            try
            {
                SchemaBootstrapper.EnsureSchema();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not prepare the company database:\n\n" + ex.Message,
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            foreach (Window window in Current.Windows)
            {
                if (window is MWin existing)
                {
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;

                    if (!existing.IsVisible)
                        existing.Show();

                    MainWindow = existing;

                    existing.Activate();
                    existing.Focus();

                    return;
                }
            }

            var mainWindow = _serviceProvider.GetRequiredService<MWin>();

            MainWindow = mainWindow;

            mainWindow.Show();
            mainWindow.Activate();
            mainWindow.Focus();
        }

        // ====================================================================
        // ====================================================================
        public static void RefreshSchema()
        {
            try
            {
                SchemaBootstrapper.Reset();
                SchemaBootstrapper.EnsureSchema();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not reinitialize the schema:\n\n" + ex.Message,
                    "ShouTech ERP",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ====================================================================
        // APPLICATION EXIT
        // ====================================================================
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                AppHost.Logging.LogLifecycle(
                    "Application exit requested",
                    $"ExitCode={e.ApplicationExitCode}; ShutdownMode={ShutdownMode}");
            }
            catch { System.Diagnostics.Debug.WriteLine("Exit lifecycle log failed"); }

            try
            {
                AppHost.Audit.LogSystemStopAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            { System.Diagnostics.Debug.WriteLine("Exit audit flush failed"); }

            try
            {
                AppHost.OnlineCoordinator?.StopCoordinator();

                if (AppHost.OnlineCoordinator is IDisposable disposableCoordinator)
                    disposableCoordinator.Dispose();

                if (_serviceProvider is IDisposable disposableProvider)
                    disposableProvider.Dispose();
            }
            catch
            { System.Diagnostics.Debug.WriteLine("Service provider dispose failed"); }

            try
            {
                AppHost.Logging.LogLifecycle(
                    "Application exit completed",
                    $"ExitCode={e.ApplicationExitCode}");
            }
            catch { System.Diagnostics.Debug.WriteLine("Exit completion log failed"); }

            base.OnExit(e);
        }
    }
}