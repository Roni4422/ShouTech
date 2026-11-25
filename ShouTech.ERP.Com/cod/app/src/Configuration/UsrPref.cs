// ========================================================================
// FILE: Configuration/UsrPref.cs
// PROJECT: SHOUTECH ERP V10 - User Preferences & Window Persistence
// FRAMEWORK: .NET 8.0 / C# 12 (WPF)
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using ShouTech.App.Services;


using ShouTech.Domain;
namespace ShouTech.App.Configuration;

/// <summary>
/// Handles UI preferences, language, accent color, and window state.
/// </summary>
public sealed class UsrPref
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly string _path = AppPaths.UserPrefs;

    private static readonly string[] SupportedLanguageCodes = { "ar", "en", "fr", "tr" };

    private static readonly string[] SupportedCurrencyCodes = { "SYP", "SAR", "USD", "EUR", "TRY" };

    private static readonly string[] SupportedThemes = { "Light", "Dark" };

    private static readonly string[] SupportedDateFormats =
    {
        "dd/MM/yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "d/M/yyyy", "dd.MM.yyyy"
    };

    private static readonly string[] SupportedTimeFormats =
    {
        "HH:mm", "h:mm tt", "hh:mm tt", "H:mm"
    };

    private const int MaxDecimalDigits = 4;

    // ================================================================
    //  Language
    // ================================================================

    public string Lang { get; set; } = LanguageDirectionService.DefaultLanguageCode;

    public bool LangSet { get; set; }

    public string CurrencyCode { get; set; } = "SAR";

    public string Theme { get; set; } = "Light";

    /// <summary>
    /// </summary>
    public string AccentColor { get; set; } = "slate";

    // ================================================================
    //  Display
    // ================================================================

    public double FontSize { get; set; } = 13;

    /// <summary>Program font family (Options -> Font). Empty = the
    /// theme default (ApplicationFontFamily resource). Consumed by
    /// VisuId.ApplyShellVisualProperties for every window.</summary>
    public string FontFamily { get; set; } = string.Empty;

    public bool CompactMode { get; set; }

    public bool ShowToolBar { get; set; } = true;

    public bool ShowStatusBar { get; set; } = true;

    public bool EnableAnimations { get; set; } = true;

    // ================================================================
    //  Formatting
    // ================================================================

    /// <summary>
    /// </summary>
    public string DateFormat { get; set; } = "dd/MM/yyyy";

    /// <summary>
    /// Display number format. Western digits are mandatory across the application.
    /// </summary>
    public string NumberFormat { get; set; } = "Western";

    public int DecimalPlaces { get; set; } = 2;

    /// <summary>
    /// </summary>
    public int DecimalDigits { get; set; } = 2;

    public string ThousandSeparator { get; set; } = ",";

    // ---- Numbers, date & time (Options section, OPTWIN_SECTIONS
    // §3.2). Canonical display-precision settings; DecimalPlaces /
    // DecimalDigits above are legacy aliases kept in sync with
    // PriceDecimalPlaces by Normalize() for stored-JSON and reader
    // compatibility. Consumed program-wide through NumFmt.Current
    // (installed at startup and on every Options save).

    /// <summary>Fraction digits for quantities (default 3).</summary>
    public int QtyDecimalPlaces { get; set; } = 3;

    /// <summary>Fraction digits for prices and money (default 3 —
    /// the owner's evidenced Options value, defaults ruling
    /// 2026-11-24).</summary>
    public int PriceDecimalPlaces { get; set; } = 3;

    /// <summary>Group thousands with a separator in displayed
    /// numbers (default on).</summary>
    public bool GroupThousands { get; set; } = true;

    /// <summary>Keep trailing zeros up to the digit count (default
    /// on); off trims them in displayed numbers.</summary>
    public bool ShowTrailingZeros { get; set; } = true;

    /// <summary>Round displayed numbers to the digit count (default
    /// on); off truncates toward zero instead.</summary>
    public bool RoundDisplay { get; set; } = true;

    /// <summary>Time-of-day display pattern (default "h:mm tt" —
    /// the owner's evidenced Options value, defaults ruling
    /// 2026-11-24).</summary>
    public string TimeFormat { get; set; } = "h:mm tt";

    // ---- Printing (per-user copy counts; the company print
    // settings themselves live in print_settings, migration 125).
    // Consumed by PrintSetSvc.ApplyDialogSettings through the
    // invoice print path (invoices) and the JrnWin/CshWin print
    // paths (vouchers).

    /// <summary>Copies printed per invoice print (default 1).</summary>
    public int PrintInvoiceCopies { get; set; } = 1;

    /// <summary>Copies printed per voucher print (default 1).</summary>
    public int PrintVoucherCopies { get; set; } = 1;

    // ================================================================
    //  Behavior
    // ================================================================

    /// <summary>Ask before exiting the program (default off — the
    /// owner's evidenced Options state, defaults ruling
    /// 2026-11-24). Consumed by the shell exit path (RibbonNav).
    /// </summary>
    public bool ConfirmOnExit { get; set; }

    public bool ConfirmOnDelete { get; set; } = true;

    public int AutoSaveIntervalMinutes { get; set; } = 5;

    public bool EnableAutoComplete { get; set; } = true;

    public bool AutoFocusFirstField { get; set; } = true;

    public bool ShowToolTips { get; set; } = true;

    public int AutoLockMinutes { get; set; }

    // ================================================================
    //  Startup
    // ================================================================

    public bool AutoOpenLastCompany { get; set; } = true;

    public bool RememberPassword { get; set; }

    public bool SkipSplash { get; set; }

    /// <summary>True after the first company is created in the Setup Wizard.</summary>
    public bool CompanySetupComplete { get; set; }

    // ================================================================
    //  Window State
    // ================================================================

    public double WinW { get; set; } = 1400;

    public double WinH { get; set; } = 900;

    public double WinL { get; set; }

    public double WinT { get; set; }

    public WindowState WinState { get; set; } = WindowState.Maximized;

    public string LastTab { get; set; } = "File";

    public double RecycleBinLeft { get; set; } = -1;

    public double RecycleBinTop { get; set; } = -1;

    // ================================================================
    //  Recent Items
    // ================================================================

    public List<string> RecentCompanies { get; set; } = new();

    public List<string> RecentMaterials { get; set; } = new();

    public List<string> RecentDocuments { get; set; } = new();
	
	/// <summary>
	/// Determines whether the username field should be hidden on the next login.
	/// </summary>
	public bool HideUsernameOnNextLaunch { get; set; }

    // ================================================================
    //  Load / Save
    // ================================================================

    public static UsrPref Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                var prefs = JsonSerializer.Deserialize<UsrPref>(json, _jsonOptions);
                if (prefs != null)
                {
                    prefs.Normalize();
                    return prefs;
                }
            }
        }
        catch (Exception exL179)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsLoadFailed, exL179, "UsrPref.Load");
            // Fallback to defaults silently.
        }

        var fresh = new UsrPref();
        fresh.Normalize();
        return fresh;
    }
	
    public void Save()
    {
        try
        {
            Normalize();

            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(this, _jsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception exL202)
        {
            AppHost.Errors.Report(ErrorCodes.SettingsSaveFailed, exL202, "UsrPref.Save");
            // Ignore write errors during unexpected shutdown.
        }
    }

    // ================================================================
    // ================================================================

    /// <summary>
    /// </summary>
    public void Normalize()
    {
        bool valid;

        // Language — must be a bare supported code (ar/en/fr/tr).
        // Region-qualified values such as "fr-FR" do not map to a resource
        // file and are treated as malformed stored values.
        Lang = string.IsNullOrWhiteSpace(Lang)
            ? LangDirSv.DefaultLanguageCode
            : Lang.Trim().ToLowerInvariant();

        valid = false;
        foreach (var code in SupportedLanguageCodes)
            if (code == Lang) { valid = true; break; }
        if (!valid) Lang = LangDirSv.DefaultLanguageCode;

        // Theme
        valid = false;
        foreach (var t in SupportedThemes)
            if (string.Equals(t, Theme, StringComparison.OrdinalIgnoreCase)) { valid = true; break; }
        if (!valid) Theme = "Dark";

        // CurrencyCode — must be a known ISO-4217-style code (rejects "sar-random").
        if (!string.IsNullOrWhiteSpace(CurrencyCode))
            CurrencyCode = CurrencyCode.Trim().ToUpperInvariant();
        valid = false;
        foreach (var c in SupportedCurrencyCodes)
            if (c == CurrencyCode) { valid = true; break; }
        if (!valid) CurrencyCode = "SAR";

        // Accent
        var validAccents = new[]
        {
            "slate", "win11", "gray", "blue", "green", "red", "purple",
            "teal", "amber", "orange", "pink"
        };

        if (string.IsNullOrWhiteSpace(AccentColor))
            AccentColor = "slate";

        AccentColor = AccentColor.Trim().ToLowerInvariant();

        valid = false;
        foreach (var a in validAccents)
            if (a == AccentColor) { valid = true; break; }

        if (!valid) AccentColor = "slate";

        // Font size
        if (FontSize < 10) FontSize = 10;
        if (FontSize > 20) FontSize = 20;

        // Font family — free text (an installed family name); empty
        // means the theme default. Capped so a corrupt store cannot
        // carry an unbounded string into every window.
        FontFamily = (FontFamily ?? string.Empty).Trim();
        if (FontFamily.Length > 120) FontFamily = FontFamily[..120];

        // Decimal places
        if (DecimalPlaces < 0) DecimalPlaces = 0;
        if (DecimalPlaces > 6) DecimalPlaces = 6;

        // Decimal digits (display precision)
        if (DecimalDigits < 0) DecimalDigits = 0;
        if (DecimalDigits > MaxDecimalDigits) DecimalDigits = MaxDecimalDigits;

        // Numbers section — canonical precision settings.
        if (QtyDecimalPlaces < 0) QtyDecimalPlaces = 0;
        if (QtyDecimalPlaces > MaxDecimalDigits) QtyDecimalPlaces = MaxDecimalDigits;
        if (PriceDecimalPlaces < 0) PriceDecimalPlaces = 0;
        if (PriceDecimalPlaces > MaxDecimalDigits) PriceDecimalPlaces = MaxDecimalDigits;
        // Legacy aliases follow the canonical price precision.
        DecimalPlaces = PriceDecimalPlaces;
        DecimalDigits = PriceDecimalPlaces;

        // Print copies — at least one, at most nine.
        if (PrintInvoiceCopies < 1) PrintInvoiceCopies = 1;
        if (PrintInvoiceCopies > 9) PrintInvoiceCopies = 9;
        if (PrintVoucherCopies < 1) PrintVoucherCopies = 1;
        if (PrintVoucherCopies > 9) PrintVoucherCopies = 9;

        // TimeFormat — must be one of the supported patterns.
        if (string.IsNullOrWhiteSpace(TimeFormat))
        {
            TimeFormat = "h:mm tt";
        }
        else
        {
            var trimmedTime = TimeFormat.Trim();
            valid = false;
            foreach (var f in SupportedTimeFormats)
                if (f == trimmedTime) { valid = true; break; }
            TimeFormat = valid ? trimmedTime : "h:mm tt";
        }

        // AutoSave
        if (AutoSaveIntervalMinutes < 0) AutoSaveIntervalMinutes = 0;
        if (AutoSaveIntervalMinutes > 120) AutoSaveIntervalMinutes = 120;

        // AutoLock
        if (AutoLockMinutes < 0) AutoLockMinutes = 0;
        if (AutoLockMinutes > 480) AutoLockMinutes = 480;

        // Window
        if (WinW < 800) WinW = 800;
        if (WinH < 600) WinH = 600;
        if (WinW > 10000) WinW = 10000;
        if (WinH > 10000) WinH = 10000;

        // DateFormat — must be one of the supported .NET custom patterns.
        if (string.IsNullOrWhiteSpace(DateFormat))
        {
            DateFormat = "dd/MM/yyyy";
        }
        else
        {
            var trimmedFormat = DateFormat.Trim();
            valid = false;
            foreach (var f in SupportedDateFormats)
                if (f == trimmedFormat) { valid = true; break; }
            DateFormat = valid ? trimmedFormat : "dd/MM/yyyy";
        }

        // NumberFormat
        NumberFormat = "Western";

        // ThousandSeparator
        if (string.IsNullOrEmpty(ThousandSeparator))
            ThousandSeparator = ",";

        // Recent lists — max 10
        RecentCompanies ??= new List<string>();
        RecentMaterials ??= new List<string>();
        RecentDocuments ??= new List<string>();

        TrimRecent(RecentCompanies);
        TrimRecent(RecentMaterials);
        TrimRecent(RecentDocuments);
    }

    private static void TrimRecent(List<string> list)
    {
        const int max = 10;
        while (list.Count > max)
            list.RemoveAt(list.Count - 1);

        // Remove empty/duplicates
        list.RemoveAll(string.IsNullOrWhiteSpace);
    }

    // ================================================================
    //  Recent Items Helper
    // ================================================================

    /// <summary>
    /// </summary>
    public static void PushRecent(List<string> list, string value)
    {
        if (list == null || string.IsNullOrWhiteSpace(value)) return;
        value = value.Trim();

        list.RemoveAll(x =>
            string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

        list.Insert(0, value);

        while (list.Count > 10)
            list.RemoveAt(list.Count - 1);
    }

    // ================================================================
    //  Window State
    // ================================================================

    public void ApplyToWindow(Window win)
    {
        if (win is null) return;

        if (WinState != WindowState.Maximized)
        {
            if (WinW > 100) win.Width = WinW;
            if (WinH > 100) win.Height = WinH;

            if (IsPositionOnScreen(WinL, WinT, win.Width, win.Height))
            {
                win.Left = WinL;
                win.Top = WinT;
            }
        }

        win.WindowState = WinState == WindowState.Minimized
            ? WindowState.Normal
            : WinState;

        LanguageDirectionService.ApplyToWindow(win, Lang);
    }

    public void SaveFromWindow(Window win)
    {
        if (win is null) return;

        if (win.WindowState == WindowState.Normal)
        {
            WinW = win.Width;
            WinH = win.Height;
            WinL = win.Left;
            WinT = win.Top;
        }
        else if (win.RestoreBounds != Rect.Empty)
        {
            WinW = win.RestoreBounds.Width;
            WinH = win.RestoreBounds.Height;
            WinL = win.RestoreBounds.Left;
            WinT = win.RestoreBounds.Top;
        }

        WinState = win.WindowState == WindowState.Minimized
            ? WindowState.Normal
            : win.WindowState;
    }

    /// <summary>Builds the program-wide display-format settings
    /// (Domain NumFmt) from these preferences. Called at startup
    /// and on every Options save so the whole program's figures
    /// follow the Options window.</summary>
    public NumFmtSettings ToNumFmtSettings() => new(
        QtyDecimalPlaces,
        PriceDecimalPlaces,
        GroupThousands,
        ShowTrailingZeros,
        RoundDisplay,
        DateFormat,
        TimeFormat);

	public string? RememberedUsername { get; set; }
	
    private static bool IsPositionOnScreen(
        double left, double top, double width, double height)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = SystemParameters.VirtualScreenWidth;
        double virtualHeight = SystemParameters.VirtualScreenHeight;

        return left >= virtualLeft &&
               top >= virtualTop &&
               (left + width) <= (virtualLeft + virtualWidth) &&
               (top + height) <= (virtualTop + virtualHeight);
    }
}