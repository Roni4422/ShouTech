using System;
using System.Globalization;

namespace ShouTech.Domain;

/// <summary>
/// The program-wide number/date/time display settings governed by
/// Options -> Numbers, date &amp; time (OPTWIN_SECTIONS §3.2). Pure
/// data: the app layer builds it from UsrPref and installs it as
/// <see cref="NumFmt.Current"/> at startup and on every Options
/// save, so every consumer formats identically — the Options window
/// controls the whole program's figures, not one window's.
/// </summary>
public sealed record NumFmtSettings(
    int QtyDigits,
    int PriceDigits,
    bool GroupThousands,
    bool ShowTrailingZeros,
    bool RoundDisplay,
    string DatePattern,
    string TimePattern)
{
    /// <summary>Built-in defaults — identical to the UsrPref
    /// defaults, so behaviour before any Options save is the
    /// historic one (qty 3, price 2, grouped, zeros shown,
    /// dd/MM/yyyy, HH:mm).</summary>
    public static NumFmtSettings Defaults { get; } = new(
        QtyDigits: 3,
        PriceDigits: 2,
        GroupThousands: true,
        ShowTrailingZeros: true,
        RoundDisplay: true,
        DatePattern: "dd/MM/yyyy",
        TimePattern: "HH:mm");
}

/// <summary>
/// Central display formatter. Every printed or shown quantity,
/// price, money amount, percent, date and time of day goes through
/// here with <see cref="Current"/>; explicit-settings overloads
/// exist for the Options live sample and for headless proofs.
/// Culture is always invariant — Western digits are mandatory
/// program-wide (UsrPref.NumberFormat). Amounts stored or posted
/// are never touched: this governs DISPLAY precision only.
/// </summary>
public static class NumFmt
{
    private static NumFmtSettings _current = NumFmtSettings.Defaults;

    /// <summary>The live settings. The app layer replaces this from
    /// UsrPref at startup and on Options save; it is never read from
    /// disk here, so headless consumers (harnesses, print pipeline)
    /// see a stable, testable value.</summary>
    public static NumFmtSettings Current
    {
        get => _current;
        set => _current = value ?? NumFmtSettings.Defaults;
    }

    // ---------------------------------------------------------------
    // Pure cores (explicit settings — the testable surface)
    // ---------------------------------------------------------------

    /// <summary>Formats a value with an explicit digit count and
    /// the settings' grouping / trailing-zero / rounding rules.
    /// Rounding on = round half away from zero to the shown digits;
    /// off = truncate toward zero (the value is shown, never
    /// rounded up).</summary>
    public static string FormatValue(decimal value, int digits, NumFmtSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        digits = Math.Clamp(digits, 0, 6);
        var v = value;
        if (digits <= 6)
        {
            var factor = 1m;
            for (var i = 0; i < digits; i++) factor *= 10m;
            v = settings.RoundDisplay
                ? Math.Round(v, digits, MidpointRounding.AwayFromZero)
                : Math.Truncate(v * factor) / factor;
        }

        var intPart = settings.GroupThousands ? "#,##0" : "0";
        string format;
        if (digits == 0)
            format = intPart;
        else if (settings.ShowTrailingZeros)
            format = intPart + "." + new string('0', digits);
        else
            // Keep at least one fractional digit, trim the rest.
            format = intPart + ".0" + new string('#', digits - 1);
        return v.ToString(format, CultureInfo.InvariantCulture);
    }

    public static string FormatDate(DateTime value, string pattern) =>
        value.ToString(string.IsNullOrWhiteSpace(pattern) ? "dd/MM/yyyy" : pattern,
            CultureInfo.InvariantCulture);

    public static string FormatTimeOfDay(DateTime value, string pattern) =>
        value.ToString(string.IsNullOrWhiteSpace(pattern) ? "HH:mm" : pattern,
            CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------
    // Live wrappers (governed by Current)
    // ---------------------------------------------------------------

    public static string FormatQty(decimal value) =>
        FormatValue(value, Current.QtyDigits, Current);

    public static string FormatPrice(decimal value) =>
        FormatValue(value, Current.PriceDigits, Current);

    /// <summary>Money amounts use the price digit count — one
    /// precision for every amount the program prints or shows.</summary>
    public static string FormatMoney(decimal value) =>
        FormatValue(value, Current.PriceDigits, Current);

    /// <summary>Percentages show one fractional digit through the
    /// same grouping/zero/rounding rules.</summary>
    public static string FormatPercent(decimal value) =>
        FormatValue(value, 1, Current);

    public static string FormatDate(DateTime value) =>
        FormatDate(value, Current.DatePattern);

    public static string FormatTimeOfDay(DateTime value) =>
        FormatTimeOfDay(value, Current.TimePattern);
}
