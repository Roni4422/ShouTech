using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ShouTech.App.Services;

namespace ShouTech.App.Reporting;

/// <summary>
/// Invoice design template system.
///
/// Every invoice type owns a three-letter file extension; the extension itself
/// identifies the invoice family. Template files are encrypted containers
/// (<see cref="XptCry"/>, kind = InvoiceTemplate) so a design can never be
/// read or altered outside a licensed ShouTech ERP installation.
/// The decrypted payload exists in memory only - never written to disk.
/// </summary>
public static class InvTpl
{
    /// <summary>Supported invoice families and their three-letter extensions.</summary>
    public static readonly IReadOnlyDictionary<string, string> Extensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sal"] = "SalesInvoice",
            ["srt"] = "SalesReturn",
            ["pur"] = "PurchaseInvoice",
            ["prt"] = "PurchaseReturn",
            ["pos"] = "PosInvoice",
            ["qut"] = "PriceQuote",
            ["trn"] = "TransferNote",
            ["ord"] = "OrderInvoice",
            ["pin"] = "StockIn",
            ["pout"] = "StockOut",
            ["opn"] = "OpeningStock",
            ["cnt"] = "StockCount"
        };

    /// <summary>Returns the extension (without dot) for an invoice family, or null.</summary>
    public static string? ExtensionFor(string invoiceFamily)
    {
        foreach (var (ext, family) in Extensions)
        {
            if (string.Equals(family, invoiceFamily, StringComparison.OrdinalIgnoreCase))
                return ext;
        }
        return null;
    }

    /// <summary>Validates that a path carries one of the registered extensions.</summary>
    public static bool IsTemplateFile(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return Extensions.ContainsKey(ext);
    }

    // ------------------------------------------------------------------
    // Model
    // ------------------------------------------------------------------

    /// <summary>Root model of an invoice design template.</summary>
    public sealed class Template
    {
        public string Family { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Version { get; set; } = 1;
        public bool Rtl { get; set; } = true;
        public PageSetup Page { get; set; } = new();
        public List<Element> Elements { get; set; } = new();
    }

    public sealed class PageSetup
    {
        public double WidthMm { get; set; } = 210;
        public double HeightMm { get; set; } = 297;
        public double MarginTopMm { get; set; } = 10;
        public double MarginBottomMm { get; set; } = 10;
        public double MarginLeftMm { get; set; } = 10;
        public double MarginRightMm { get; set; } = 10;
    }

    /// <summary>One design element (text, field, table, line, image, barcode).</summary>
    public sealed class Element
    {
        public string Kind { get; set; } = string.Empty;      // text|field|table|line|image|barcode
        public string? BindKey { get; set; }                  // data field binding key
        public string? TextKey { get; set; }                  // localization key for static text
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string? FontFamily { get; set; }
        public double FontSize { get; set; } = 11;
        public bool Bold { get; set; }
        public string? Align { get; set; }                    // start|center|end
        public Dictionary<string, string>? Options { get; set; }
    }

    // ------------------------------------------------------------------
    // Encrypted persistence
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Serializes and encrypts a template to disk (atomic write).</summary>
    public static void Save(string path, Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!IsTemplateFile(path))
            throw new ArgumentException("Template path must use a registered extension.", nameof(path));

        var json = JsonSerializer.SerializeToUtf8Bytes(template, JsonOpts);
        XptCry.EncryptToFile(path, json, XptCry.KindInvoiceTemplate);
    }

    /// <summary>Decrypts a template file. The plaintext never touches disk.</summary>
    public static Template Load(string path)
    {
        if (!IsTemplateFile(path))
            throw new ArgumentException("Template path must use a registered extension.", nameof(path));

        var json = XptCry.DecryptFromFile(path, XptCry.KindInvoiceTemplate);
        return JsonSerializer.Deserialize<Template>(json, JsonOpts)
               ?? throw new XptCry.XptCryException("Template payload is empty or invalid.");
    }
}
