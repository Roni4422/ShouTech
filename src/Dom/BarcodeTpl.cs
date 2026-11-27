using System;
using System.Globalization;

namespace ShouTech.Domain;

/// <summary>
/// A composite (parsed) barcode template (OPTWIN_SECTIONS §3.4):
/// scale-printed labels whose digits split into a material-code
/// segment and an embedded-weight segment at fixed positions, so a
/// scan resolves to material + weight at once. Positions are
/// 1-based, matching how the segments are read off the label.
/// Pure logic — the store (BcdTplSvc) and the scan consumers
/// (TaxSvc.ParseScan -> POS scan box and invoice grid entry) build
/// on it, and the Options editor's live test field runs this exact
/// parser, so what the owner tests is what the till will do.
/// </summary>
public sealed record BarcodeTemplate(
    string Prefix,
    int TotalLength,
    int CodeStart,
    int CodeLength,
    int WeightIntStart,
    int WeightIntLength,
    int WeightFracStart,
    int WeightFracLength)
{
    /// <summary>Structural validation for the editor's save: the
    /// prefix is non-empty, every segment lies inside the total
    /// length, and no two segments overlap.</summary>
    public bool IsValid(out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(Prefix)) { reason = "prefix"; return false; }
        if (TotalLength < 4 || TotalLength > 64) { reason = "length"; return false; }
        var segments = new[]
        {
            (CodeStart, CodeLength, "code"),
            (WeightIntStart, WeightIntLength, "weight-int"),
            (WeightFracStart, WeightFracLength, "weight-frac")
        };
        foreach (var (start, length, name) in segments)
        {
            if (start < 1 || length < 1 || start + length - 1 > TotalLength)
            {
                reason = name;
                return false;
            }
        }
        for (var i = 0; i < segments.Length; i++)
            for (var j = i + 1; j < segments.Length; j++)
            {
                var (s1, l1, _) = segments[i];
                var (s2, l2, _) = segments[j];
                if (s1 < s2 + l2 && s2 < s1 + l1)
                {
                    reason = "overlap";
                    return false;
                }
            }
        return true;
    }

    /// <summary>Parses one scanned string. A hit requires: exact
    /// total length, the prefix, all-digit segments, and a positive
    /// weight. The material code keeps its leading zeros (it is a
    /// code, not a number). Weight = integer segment + fraction
    /// segment / 10^fraction-length.</summary>
    public bool TryParse(string? scanned, out string code, out decimal weight)
    {
        code = string.Empty;
        weight = 0m;
        if (string.IsNullOrWhiteSpace(scanned))
            return false;
        var text = scanned.Trim();
        if (!IsValid(out _))
            return false;
        if (text.Length != TotalLength)
            return false;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var codeSegment = Segment(text, CodeStart, CodeLength);
        var intSegment = Segment(text, WeightIntStart, WeightIntLength);
        var fracSegment = Segment(text, WeightFracStart, WeightFracLength);
        if (!AllDigits(codeSegment) || !AllDigits(intSegment) || !AllDigits(fracSegment))
            return false;

        var intPart = long.Parse(intSegment, CultureInfo.InvariantCulture);
        var fracPart = long.Parse(fracSegment, CultureInfo.InvariantCulture);
        var scale = 1m;
        for (var i = 0; i < WeightFracLength; i++) scale *= 10m;
        weight = intPart + fracPart / scale;
        if (weight <= 0m)
            return false;
        code = codeSegment;
        return true;
    }

    private static string Segment(string text, int start, int length) =>
        text.Substring(start - 1, length);

    private static bool AllDigits(string value)
    {
        if (value.Length == 0) return false;
        foreach (var ch in value)
            if (ch < '0' || ch > '9') return false;
        return true;
    }
}
