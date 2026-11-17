using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShouTech.App.Services;

/// <summary>
/// English amount-in-words for printed vouchers (Chunk C). Currency-aware:
/// the major/minor unit names follow the ISO code (SAR Riyal/Halala,
/// USD Dollar/Cent, EUR Euro/Cent, GBP Pound/Penny, AED Dirham/Fils,
/// KWD Dinar/Fils, EGP Pound/Piaster); unknown codes fall back to the code
/// itself with "Minor" as the minor unit name. Output is English only —
/// printed legal wording must not depend on the UI language.
/// </summary>
public static class NumWords
{
    private static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine",
        "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen",
        "Seventeen", "Eighteen", "Nineteen"
    };

    private static readonly string[] Tens =
    {
        "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"
    };

    private static readonly (string Major, string Minor) DefaultUnits = ("", "Minor");

    private static readonly IReadOnlyDictionary<string, (string Major, string Minor)> Units =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["SAR"] = ("Riyal", "Halala"),
            ["USD"] = ("Dollar", "Cent"),
            ["EUR"] = ("Euro", "Cent"),
            ["GBP"] = ("Pound", "Penny"),
            ["AED"] = ("Dirham", "Fils"),
            ["KWD"] = ("Dinar", "Fils"),
            ["EGP"] = ("Pound", "Piaster"),
            ["JOD"] = ("Dinar", "Fils"),
            ["TRY"] = ("Lira", "Kurus"),
        };

    /// <summary>e.g. ToWords(1234.50m, "SAR") = "One Thousand Two Hundred Thirty-Four Riyals and Fifty Halalas Only".</summary>
    public static string ToWords(decimal amount, string currencyCode)
    {
        var code = string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim().ToUpperInvariant();
        var (major, minor) = Units.TryGetValue(code, out var units) ? units : (code, DefaultUnits.Item2);
        var rounded = decimal.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero);
        var whole = (long)decimal.Truncate(rounded);
        var fraction = (int)decimal.Round((rounded - whole) * 100m, 0, MidpointRounding.AwayFromZero);
        if (fraction == 100) { whole += 1; fraction = 0; }

        var words = IntegerToWords(whole) + " " + Plural(major, whole);
        if (fraction > 0)
            words += " and " + IntegerToWords(fraction) + " " + Plural(minor, fraction);
        return (amount < 0 ? "Minus " : string.Empty) + words + " Only";
    }

    public static string IntegerToWords(long value)
    {
        if (value == 0) return Ones[0];
        if (value < 0) return "Minus " + IntegerToWords(-value);
        var parts = new List<string>();
        var scales = new[] { (1_000_000_000L, "Billion"), (1_000_000L, "Million"), (1_000L, "Thousand") };
        var remaining = value;
        foreach (var (scale, name) in scales)
        {
            if (remaining < scale) continue;
            parts.Add(ThreeDigits((int)(remaining / scale)) + " " + name);
            remaining %= scale;
        }
        if (remaining > 0)
            parts.Add(ThreeDigits((int)remaining));
        return string.Join(" ", parts);
    }

    private static string ThreeDigits(int value)
    {
        var parts = new List<string>();
        if (value >= 100)
        {
            parts.Add(Ones[value / 100] + " Hundred");
            value %= 100;
        }
        if (value >= 20)
        {
            var ten = Tens[value / 10];
            var rest = value % 10;
            parts.Add(rest == 0 ? ten : ten + "-" + Ones[rest]);
        }
        else if (value > 0)
        {
            parts.Add(Ones[value]);
        }
        return string.Join(" ", parts);
    }

    private static string Plural(string unit, long value)
        => value == 1 ? unit : unit + "s";
}
