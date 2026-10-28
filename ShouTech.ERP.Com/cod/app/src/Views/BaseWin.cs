using System;
using System.Windows;
using ShouTech.App.Services;
using WpfApp = System.Windows.Application;

namespace ShouTech.App.Views;

/// <summary>
/// Mandatory abstract base class for every secondary window in ShouTech ERP.
///
/// Enforces the shell contract automatically for all derived windows:
///   1. Language        - re-localizes live when the shell language changes.
///   2. Flow direction  - RTL/LTR derived from the active language.
///   3. Central theme   - background/foreground resolve through DynamicResource.
///   4. User preferences- window geometry is persisted per user + company.
///   5. Context         - current company and current user are one property away.
///
/// Derived windows must override <see cref="ApplyLocalizedText"/> and route every
/// user-visible string through <see cref="T(string)"/>. No literal UI text is
/// allowed in any derived window.
/// </summary>
public abstract class BaseWin : Window
{
    private bool _geometryWired;
    private EventHandler? _langHandler;

    /// <summary>Stable key used to store per-user geometry. Defaults to the class name.</summary>
    protected virtual string PrefKey => GetType().Name;

    /// <summary>When false, geometry persistence is skipped (e.g. modal dialogs).</summary>
    protected virtual bool PersistGeometry => true;

    protected BaseWin()
    {
        // Central theme: brushes resolve through the application resource dictionaries
        // so any theme or accent change propagates without code behind.
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "ApplicationFontFamily");
        SetResourceReference(FontSizeProperty, "ApplicationFontSize");

        // Set owner to main window if available and not self
        if (WpfApp.Current?.MainWindow != null && WpfApp.Current.MainWindow != this)
        {
            try
            {
                Owner = WpfApp.Current.MainWindow;
            }
            catch
            {
                // Window owner assignment should never block window initialization
            }
        }

        Loaded += OnBaseLoaded;
        Closed += OnBaseClosed;
    }

    /// <summary>Localization service owned by the application shell.</summary>
    protected static ILocSvc? LocSvc => (WpfApp.Current as App)?.Localization;

    /// <summary>Translates a resource key using the active language.</summary>
    protected static string T(string key) => LocSvc?.GetString(key) ?? key;

    /// <summary>Localized caption of the active company (empty before login).</summary>
    protected static string CurrentCompanyDisplay => AppState.CurrentCompanyDisplayName;

    /// <summary>Display name of the currently authenticated user.</summary>
    protected static string CurrentUserDisplay => AppState.CurrentUser?.FullName ?? string.Empty;

    /// <summary>Active company code for database connection context.</summary>
    protected static string CurrentCompanyCode => AppState.CurrentCompanyCode ?? string.Empty;

    /// <summary>Active database provider (sqlite or postgresql).</summary>
    protected static string CurrentDatabaseProvider =>
        DatabaseRuntime.IsPostgreSql() ? "postgresql" : "sqlite";

    /// <summary>Active user ID for audit and context purposes.</summary>
    protected static string CurrentUserId => AppState.CurrentUser?.Username ?? string.Empty;

    /// <summary>
    /// Applies every user-visible string of the window. Called once on load and
    /// again automatically whenever the shell language changes at runtime.
    /// </summary>
    protected abstract void ApplyLocalizedText();

    /// <summary>Applies RTL/LTR according to the active language code.</summary>
    protected void ApplyFlowDirection()
    {
        var code = LocSvc?.GetCurrentLanguage() ?? App.CurrentLanguageCode;
        LangDirSv.ApplyToWindow(this, code);
        
        // Apply Arabic RTL or other LTR based on language
        if (code != null && code.StartsWith("ar", StringComparison.OrdinalIgnoreCase))
        {
            FlowDirection = FlowDirection.RightToLeft;
        }
        else
        {
            FlowDirection = FlowDirection.LeftToRight;
        }
    }

    private void OnBaseLoaded(object sender, RoutedEventArgs e)
    {
        ApplyFlowDirection();
        ApplyLocalizedText();
        HookLanguageChanged();
        WireGeometryPersistence();
    }

    private void HookLanguageChanged()
    {
        var loc = LocSvc;
        if (loc == null || _langHandler != null)
            return;

        _langHandler = (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyFlowDirection();
            ApplyLocalizedText();
            OnLanguageChanged();
        }));
        loc.LanguageChanged += _langHandler;
    }

    /// <summary>Optional hook for derived windows after a live language switch.</summary>
    protected virtual void OnLanguageChanged()
    {
    }

    private void WireGeometryPersistence()
    {
        if (_geometryWired || !PersistGeometry)
            return;

        _geometryWired = true;
        WinPref.RestoreGeometry(this, GeometryScope());
        Closing += OnGeometryClosing;
    }

    private void OnGeometryClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            WinPref.SaveGeometry(this, GeometryScope());
        }
        catch
        {
            // Geometry persistence must never block a window from closing.
        }
    }

    private string GeometryScope()
    {
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        var company = AppState.CurrentCompanyCode ?? "no-company";
        return $"{company}/{user}/{PrefKey}";
    }

    private void OnBaseClosed(object? sender, EventArgs e)
    {
        if (LocSvc != null && _langHandler != null)
            LocSvc.LanguageChanged -= _langHandler;
        _langHandler = null;
    }

    /// <summary>Shows a localized information message box owned by this window.</summary>
    protected void ShowInfo(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Info"),
            MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Shows a localized warning message box owned by this window.</summary>
    protected void ShowWarn(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Warning"),
            MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Shows a localized error message box owned by this window.</summary>
    protected void ShowError(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Error"),
            MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>Asks a localized yes/no question; returns true on Yes.</summary>
    protected bool AskConfirm(string messageKey)
        => MessageBox.Show(this, T(messageKey), T("Common.Confirm"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>
    /// Formats an integer using Western digits (0-9) only.
    /// Use for all card codes, document numbers, and quantities shown in code fields.
    /// </summary>
    protected static string FormatWestern(long value)
        => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a position label as "current / total" with Western digits.
    /// </summary>
    protected static string FormatPosition(int index1Based, int total)
        => index1Based.ToString(System.Globalization.CultureInfo.InvariantCulture)
         + " / "
         + total.ToString(System.Globalization.CultureInfo.InvariantCulture);



}
