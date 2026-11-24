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

using ShouTech.Domain;
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
                catch (Exception exL93)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL93, "App.xaml.EnsureSchema");
                }
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
            catch (Exception exL142)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL142, "App.xaml.BuildDbKey");
            }

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
            catch (Exception exL162)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL162, "App.xaml.LoadSql");
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
            try { AppHost.Logging?.LogInfo("SchemaBootstrapper", msg); } catch (Exception exL204) {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL204, "App.xaml.SafeLogInfo"); }
        }

        private static void SafeLogWarning(string msg)
        {
            try { AppHost.Logging?.LogWarning("SchemaBootstrapper", msg); } catch (Exception exL209) {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL209, "App.xaml.SafeLogWarning"); }
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
            catch (Exception exL239)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL239, "App.xaml.SetApplicationLanguage");
            }

            try
            {
                if (Current is App app)
                    app.Localization.SetLanguage(langCode);
            }
            catch (Exception exL246)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL246, "App.xaml.SetApplicationLanguage");
            }

            void ApplyUi()
            {
                try { LangDirSv.ApplyToAllWindows(langCode); } catch (Exception exL250) {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL250, "App.xaml.SetApplicationLanguage"); }
                try { UiLoczr.ScheduleApplyToAllWindows(); } catch (Exception exL251) {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL251, "App.xaml.SetApplicationLanguage"); }
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
            catch (Exception exL267)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL267, "App.xaml.SetApplicationLanguage");
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
            catch (Exception exL312)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL312, "App.xaml.OnStartup");
            }

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

                // F1 help for windows that do NOT derive from BaseWin
                // (startup dialogs): BaseWin windows route their own
                // F1 and mark it handled; this class handler covers
                // the rest so no window has a dead F1 key (owner
                // help decision 2026-11-24: every window maps to a
                // help topic — see HelpWin / docs/REDUCE_PLAN.md).
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    UIElement.PreviewKeyDownEvent,
                    new KeyEventHandler(GlobalWindowHelpKeyDown),
                    handledEventsToo: false);
            }
            catch (Exception exL329)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL329, "App.xaml.OnStartup");
            }

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
            catch (Exception exL363)
            {
                AppHost.Errors.Report(ErrorCodes.SettingsLoadFailed, exL363, "App.xaml.OnStartup.LoadPreferences");
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

            // Install the Options-governed display formats program-wide
            // (OPTWIN_SECTIONS §3.2): every quantity/price/date the
            // program prints or shows follows NumFmt.Current from here
            // on; Options saves refresh it live (OptWin.Save).
            NumFmt.Current = pref.ToNumFmtSettings();

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
                    try { AppHost.Logging?.LogWarning("Audit", "Delete-audit bootstrap deferred until a company database is available: " + auditEx.Message); } catch (Exception exL393) {
                        AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL393, "App.xaml.OnStartup"); }
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
                catch (Exception exL405)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL405, "App.xaml.OnStartup");
                }

                try
                {
                    MessageBox.Show(
                        "Failed to initialize database schema:\n\n" + ex.Message,
                        "ShouTech ERP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch (Exception exL415)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL415, "App.xaml.OnStartup");
                }

                try { Shutdown(1); } catch (Exception exL417) {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL417, "App.xaml.OnStartup"); }
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
            catch (Exception exL431)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL431, "App.xaml.OnStartup");
            }

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
                    var info = ResolveErrorForDialog(ex, ErrorCodes.AppDomainUnhandled);

                    AppHost.Logging.LogError(
                        "Global",
                        info.Code,
                        $"Unhandled AppDomain exception. IsTerminating={args.IsTerminating}",
                        ex);

                    AppHost.Logging.LogLifecycle(
                        "Unhandled exception",
                        ex.GetType().FullName ?? ex.GetType().Name);

                    ShowErrorDialog(info, "Unexpected Error");
                }
                else
                {
                    AppHost.Logging.LogError(
                        "Global",
                        ErrorCodes.AppDomainUnhandled,
                        $"Unhandled AppDomain exception object. IsTerminating={args.IsTerminating}");
                }
            }
            catch (Exception exL471)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL471, "App.xaml.CurrentDomain_UnhandledException");
            }
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
                var taskCode = AppHost.Errors.Classify(args.Exception);
                if (taskCode == ErrorCodes.Unknown)
                    taskCode = ErrorCodes.UnobservedTaskException;

                AppHost.Logging.LogError(
                    "Global",
                    taskCode,
                    "Unobserved task exception.",
                    args.Exception);

                args.SetObserved();
            }
            catch (Exception exL495)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL495, "App.xaml.TaskScheduler_UnobservedTaskException");
            }
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
            catch (Exception exL509)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL509, "App.xaml.ApplyWesternDigitPresentation");
            }
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
            catch (Exception exL545)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL545, "App.xaml.GlobalWesternDigitTextInput");
            }
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
                            catch (Exception exL585)
                            {
                                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL585, "App.xaml.GlobalWindowLoaded");
                            }
                        }));
                }
            }
            catch (Exception exL589)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL589, "App.xaml.GlobalWindowLoaded");
            }
        }

        /// <summary>
        /// F1 routing for non-BaseWin windows (owner help decision
        /// 2026-11-24): every window maps to a help topic — the
        /// topic key is the window's class name by convention —
        /// and HelpWin answers gracefully (topic page or the
        /// topic-pending note over the manual), never a dead key.
        /// </summary>
        private static void GlobalWindowHelpKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key != Key.F1 || e.IsRepeat || sender is not Window window || window is HelpWin)
                    return;
                e.Handled = true;
                var help = new HelpWin(window.GetType().Name) { Owner = window };
                help.ShowDialog();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "App.GlobalWindowHelpKeyDown");
            }
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
            catch (Exception exL620)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL620, "App.xaml.ApplyCustomTitleBarColor");
                // Older Windows versions don't support this - ignore.
            }
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
            catch (Exception exL639)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL639, "App.xaml.GlobalWindowUnloaded");
            }
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
                var info = ResolveErrorForDialog(args.Exception, ErrorCodes.DispatcherUnhandled);

                AppHost.Logging.LogError(
                    "Global",
                    info.Code,
                    "Dispatcher unhandled exception.",
                    args.Exception);

                ShowErrorDialog(info, "Application Error");
            }
            catch (Exception exL661)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL661, "App.xaml.Dispatcher_UnhandledException");
            }

            args.Handled = true;
        }

        /// <summary>
        /// Resolves the catalog entry a global handler logs and shows:
        /// an <see cref="AppError"/> resolves by its own code, any other
        /// exception by classification, and anything unclassifiable by
        /// the handler's fallback code (SYS-0002 / SYS-0003). The raw
        /// exception message is never shown for a known error; it stays
        /// in the log.
        /// </summary>
        private static ErrorInfo ResolveErrorForDialog(Exception exception, string fallbackCode)
        {
            if (exception is AppError appError)
                return AppHost.Errors.Resolve(appError.Code, new List<object>(appError.Args).ToArray());

            var classified = AppHost.Errors.Classify(exception);
            return AppHost.Errors.Resolve(
                classified == ErrorCodes.Unknown ? fallbackCode : classified);
        }

        private static void ShowErrorDialog(ErrorInfo info, string titlePrefix)
        {
            var body = info.Message;

            if (!string.IsNullOrWhiteSpace(info.Hint))
                body += $"{Environment.NewLine}{Environment.NewLine}{info.Hint}";

            body += $"{Environment.NewLine}{Environment.NewLine}(Error {info.Code})";

            MessageBox.Show(
                body,
                $"{titlePrefix} — {info.Code}",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
                catch (Exception exL738)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL738, "App.xaml.RunStartupSequence");
                    accepted = false;
                }

                if (!accepted)
                {
                    try { Shutdown(); } catch (Exception exL745) {
                        AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL745, "App.xaml.RunStartupSequence"); }
                    return;
                }

                pref.Lang = chosen;
                pref.LangSet = true;

                SetApplicationLanguage(chosen);

                try { pref.Save(); } catch (Exception exL754) {
                    AppHost.Errors.Report(ErrorCodes.SettingsSaveFailed, exL754, "App.xaml.RunStartupSequence.SavePreferences"); }
            }
            // else: the language is already adopted in OnStartup - no need to set it again.

            try
            {
                ShowSplashScreen();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "App.xaml.RunStartupSequence");
                try
                {
                    MessageBox.Show(
                        "Startup error: " + ex.Message,
                        "ShouTech ERP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch (Exception exL772)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL772, "App.xaml.RunStartupSequence");
                }

                try
                {
                    ShowCompanySelection();
                }
                catch (Exception exL778)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL778, "App.xaml.RunStartupSequence");
                    try { ShowLogin(); }
                    catch (Exception exL781) {
                        AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL781, "App.xaml.RunStartupSequence"); Shutdown(); }
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
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "App.xaml.ShowSplashScreen");
                try
                {
                    MessageBox.Show(
                        "Splash failed: " + ex.Message,
                        "ShouTech ERP",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch (Exception exL810)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL810, "App.xaml.ShowSplashScreen");
                }

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
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "App.xaml.ShowLogin");
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
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "App.xaml.ShowMainApp");
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
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "App.xaml.RefreshSchema");
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
            catch (Exception exL975)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL975, "App.xaml.OnExit");
            }

            try
            {
                AppHost.Audit.LogSystemStopAsync()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exL983)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL983, "App.xaml.OnExit");
                // Shutdown must never be interrupted by audit logging.
            }

            try
            {
                AppHost.OnlineCoordinator?.StopCoordinator();

                if (AppHost.OnlineCoordinator is IDisposable disposableCoordinator)
                    disposableCoordinator.Dispose();

                if (_serviceProvider is IDisposable disposableProvider)
                    disposableProvider.Dispose();
            }
            catch (Exception exL998)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL998, "App.xaml.OnExit");
                // Shutdown must never be interrupted by cleanup errors.
            }

            try
            {
                AppHost.Logging.LogLifecycle(
                    "Application exit completed",
                    $"ExitCode={e.ApplicationExitCode}");
            }
            catch (Exception exL1009)
            {
                AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, exL1009, "App.xaml.OnExit");
            }

            base.OnExit(e);
        }
    }
}