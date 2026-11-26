using System;
using System.Windows;
using ShouTech.App.Configuration;

namespace ShouTech.App.Services;

/// <summary>
/// THE shell-context application point for every window in the application.
///
/// Professional ERPs propagate the shell's live context to every window:
/// language + direction, company, theme + accent, currency and number/date
/// formats, and the current user/session. This class is the single routine
/// that applies that context, and it is invoked for EVERY window from
/// exactly two places:
///
///   1. <see cref="App"/>'s global Window.Loaded class handler — covers
///      every window class, including the ones that derive from
///      System.Windows.Window directly (shell, splash, login, company
///      picker, setup wizard, preferences, data tools...).
///   2. <see cref="Views.BaseWin"/> — the base class of all secondary
///      windows, which additionally re-applies on live language changes.
///
/// Rules enforced here:
///   * Every value is resolved AT CALL TIME from the central holders
///     (App.CurrentLanguageCode / LocSvc, AppState, UsrPref freshly loaded,
///     AppHost.Visual). Nothing is cached at application startup and
///     nothing is hardcoded per window, so a window opened after the user
///     switched language, accent, or company sees the NEW context.
///   * Company data context: the connection-bound AppHost services
///     (Journal, Projects, Assets, Reconciliation, AccountingStructure,
///     DatabaseHealth) are recreated by AppHost.RebindDatabaseContext()
///     whenever the shell switches company (ComWin calls it right after
///     rewriting the runtime connection), and all other services resolve
///     DatabaseRuntime.GetConnectionString() per call — so no window can
///     keep reading the previous company's database after a switch.
///   * Live updates for already-open windows: direction is re-applied by
///     App.SetApplicationLanguage (LangDirSv.ApplyToAllWindows) and by
///     BaseWin's LanguageChanged hook; accent/theme propagate through the
///     application-level resources that ApplyAccentInternal replaces
///     (every DynamicResource consumer updates in place). The attached
///     stamps below are refreshed on both events so a window's recorded
///     context is never stale either. Windows that snapshot brushes in
///     code at construction time (VisuId.ResolveBrushResource) keep the
///     accent they were opened with until reopened — see
///     docs/window-field-persistence-audit.md for the per-window classification.
/// </summary>
public static class ShellContext
{
    // ================================================================
    //  Attached context stamps (what the window was opened under;
    //  refreshed centrally when language/accent changes live)
    // ================================================================

    public static readonly DependencyProperty CompanyCodeProperty =
        DependencyProperty.RegisterAttached(
            "CompanyCode", typeof(string), typeof(ShellContext),
            new PropertyMetadata(null));

    public static readonly DependencyProperty LanguageCodeProperty =
        DependencyProperty.RegisterAttached(
            "LanguageCode", typeof(string), typeof(ShellContext),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AccentKeyProperty =
        DependencyProperty.RegisterAttached(
            "AccentKey", typeof(string), typeof(ShellContext),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CurrencyCodeProperty =
        DependencyProperty.RegisterAttached(
            "CurrencyCode", typeof(string), typeof(ShellContext),
            new PropertyMetadata(null));

    public static string? GetCompanyCode(DependencyObject obj) =>
        (string?)obj.GetValue(CompanyCodeProperty);

    public static void SetCompanyCode(DependencyObject obj, string? value) =>
        obj.SetValue(CompanyCodeProperty, value);

    public static string? GetLanguageCode(DependencyObject obj) =>
        (string?)obj.GetValue(LanguageCodeProperty);

    public static void SetLanguageCode(DependencyObject obj, string? value) =>
        obj.SetValue(LanguageCodeProperty, value);

    public static string? GetAccentKey(DependencyObject obj) =>
        (string?)obj.GetValue(AccentKeyProperty);

    public static void SetAccentKey(DependencyObject obj, string? value) =>
        obj.SetValue(AccentKeyProperty, value);

    public static string? GetCurrencyCode(DependencyObject obj) =>
        (string?)obj.GetValue(CurrencyCodeProperty);

    public static void SetCurrencyCode(DependencyObject obj, string? value) =>
        obj.SetValue(CurrencyCodeProperty, value);

    // ================================================================
    //  Live snapshot — resolved fresh from the central holders on
    //  every read; never cached.
    // ================================================================

    /// <summary>Current UI language code (en/ar/fr/tr) from the shell.</summary>
    public static string LanguageCode =>
        string.IsNullOrWhiteSpace(App.CurrentLanguageCode)
            ? UsrPref.Load().Lang
            : App.CurrentLanguageCode;

    /// <summary>True when the current language is a right-to-left one.</summary>
    public static bool IsRtl => LangDirSv.IsRtl(LanguageCode);

    /// <summary>Active company code (empty before a company is selected).</summary>
    public static string CompanyCode => AppState.CurrentCompanyCode ?? string.Empty;

    /// <summary>Active company display name.</summary>
    public static string CompanyDisplayName => AppState.CurrentCompanyDisplayName;

    /// <summary>Preferred currency code from the user preferences.</summary>
    public static string CurrencyCode => UsrPref.Load().CurrencyCode;

    /// <summary>Preferred date format pattern from the user preferences.</summary>
    public static string DateFormat => UsrPref.Load().DateFormat;

    /// <summary>Preferred decimal places from the user preferences.</summary>
    public static int DecimalPlaces => UsrPref.Load().DecimalPlaces;

    /// <summary>Current accent key from the visual identity service.</summary>
    public static string AccentKey =>
        AppHost.Visual?.GetCurrentAccent() ?? UsrPref.Load().AccentColor;

    /// <summary>Current theme name from the user preferences.</summary>
    public static string ThemeName => UsrPref.Load().Theme;

    /// <summary>Display name of the signed-in user (empty when signed out).</summary>
    public static string CurrentUserName => AppState.CurrentUser?.FullName ?? string.Empty;

    // ================================================================
    //  THE central routine
    // ================================================================

    /// <summary>
    /// Applies the shell's CURRENT context to a window: flow direction and
    /// alignment resources from the current language, plus the context
    /// stamps. Theme/accent need no per-window push — they live in the
    /// application resources (VisuId) and reach every window through
    /// DynamicResource; VisuId.ApplyWindow (called by window constructors
    /// and BaseWin) remains the styling entry point.
    /// Safe to call repeatedly and on any window class.
    /// </summary>
    public static void ApplyToWindow(Window window)
    {
        if (window is null)
            return;

        LangDirSv.ApplyToWindow(window, LanguageCode);
        Stamp(window);
        EnsureLiveSubscriptions();
    }

    /// <summary>Records the current context on the window (attached stamps).</summary>
    public static void Stamp(Window window)
    {
        if (window is null)
            return;

        SetCompanyCode(window, CompanyCode);
        SetLanguageCode(window, LanguageCode);
        SetAccentKey(window, AccentKey);
        SetCurrencyCode(window, CurrencyCode);
    }

    /// <summary>
    /// Refreshes the context stamps of every open window. Called centrally
    /// when the language or the accent changes so the recorded context of
    /// already-open windows stays truthful (their direction/theme are
    /// updated by the dedicated live paths — see class remarks).
    /// </summary>
    public static void RestampAllOpenWindows()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;

        foreach (Window window in app.Windows)
        {
            try { Stamp(window); }
            catch (Exception ex) { AppHost.Logging?.LogError("ShellContext", "Context restamp failed for an open window", ex); }
        }
    }

    /// <summary>One-line description of the context a window carries (diagnostics/audit).</summary>
    public static string Describe(Window window) =>
        $"Company={GetCompanyCode(window)}; Lang={GetLanguageCode(window)}; " +
        $"Accent={GetAccentKey(window)}; Currency={GetCurrencyCode(window)}";

    // ================================================================
    //  Live stamp refresh wiring (subscribed once, centrally)
    // ================================================================

    private static bool _liveSubscriptionsWired;

    private static void EnsureLiveSubscriptions()
    {
        if (_liveSubscriptionsWired)
            return;
        _liveSubscriptionsWired = true;

        try
        {
            if (System.Windows.Application.Current is App app)
                app.Localization.LanguageChanged += (_, _) => RestampAllOpenWindows();
        }
        catch (Exception ex) { AppHost.Logging?.LogError("ShellContext", "LanguageChanged subscription for context restamp failed", ex); }

        try
        {
            AppHost.Visual.AccentChanged += (_, _) => RestampAllOpenWindows();
        }
        catch (Exception ex) { AppHost.Logging?.LogError("ShellContext", "AccentChanged subscription for context restamp failed", ex); }
    }
}
