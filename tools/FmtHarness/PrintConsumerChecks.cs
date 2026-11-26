using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;

/// <summary>Section B: the real invoice-print consumer
/// (DocDesign.FormatLineValue) under two installed NumFmt settings.
/// Isolated in its own class so its types load only when the
/// section runs.</summary>
internal static class PrintConsumerChecks
{
    public static void Run(Action<string, bool, string> check)
    {
        void Eq(string name, string actual, string expected) =>
            check(name, actual == expected, $"expected '{expected}', got '{actual}'");

        // LineSubtotal = round4(2.5 x 10126.499 x (1 - 5.06%)) = 24035.2454
        // LineTax      = round4(24035.2454 x 15%)               =  3605.2868
        // LineTotal    = 27640.5322
        var line = new InvLine
        {
            LineNo = 1,
            Qty = 2.5m,
            UnitPrice = 10126.499m,
            DiscountPct = 5.06m,
            TaxRate = 15m
        };

        NumFmt.Current = NumFmtSettings.Defaults;
        Eq("B1 print qty under defaults", DocDesign.FormatLineValue(line, "Qty"), "2.500");
        Eq("B2 print unit price under defaults", DocDesign.FormatLineValue(line, "UnitPrice"), "10,126.499");
        Eq("B3 print discount % under defaults (rounded)", DocDesign.FormatLineValue(line, "DiscountPct"), "5.1");
        Eq("B4 print line subtotal under defaults", DocDesign.FormatLineValue(line, "LineSubtotal"), "24,035.245");
        Eq("B5 print line total under defaults", DocDesign.FormatLineValue(line, "LineTotal"), "27,640.532");

        NumFmt.Current = new NumFmtSettings(
            QtyDigits: 0, PriceDigits: 3,
            GroupThousands: false, ShowTrailingZeros: false, RoundDisplay: false,
            DatePattern: "yyyy-MM-dd", TimePattern: "h:mm tt");
        Eq("B6 print qty follows the installed settings (0 digits, truncate)",
            DocDesign.FormatLineValue(line, "Qty"), "2");
        Eq("B7 print unit price follows the installed settings (3 digits, ungrouped)",
            DocDesign.FormatLineValue(line, "UnitPrice"), "10126.499");
        Eq("B8 print discount % follows the installed settings (truncated)",
            DocDesign.FormatLineValue(line, "DiscountPct"), "5.0");
        Eq("B9 print line subtotal follows the installed settings",
            DocDesign.FormatLineValue(line, "LineSubtotal"), "24035.245");

        NumFmt.Current = NumFmtSettings.Defaults;
        Eq("B10 restoring defaults restores the print output",
            DocDesign.FormatLineValue(line, "Qty"), "2.500");
    }
}
