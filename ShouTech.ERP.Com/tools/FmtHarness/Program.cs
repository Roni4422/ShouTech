// FmtHarness — Options-governed display formats proof
// (OPTWIN_SECTIONS §3.2, Landing 1).
//   A: the formatter core honours the digit split (quantity vs
//      price), grouping, trailing zeros and round-vs-truncate.
//   B: the REAL print consumer (DocDesign.FormatLineValue) follows
//      NumFmt.Current — changing the installed settings changes
//      what the invoice print renders, and switching back restores
//      it (the Options window controls the program's figures).
// (The UsrPref -> NumFmtSettings mapping is a one-line record
// construction in UsrPref.ToNumFmtSettings; it is not exercised
// here because the UsrPref type itself carries WPF window-state
// members and cannot load in a headless Linux process — it is
// covered by the Windows verification run instead.)
// Sections run isolated so a headless type-load limitation in one
// surface reports as that section's failure, not a process crash.
using ShouTech.Domain;

var pass = 0;
var fail = 0;
void Report(string name, bool condition, string detail = "")
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} - {name}{(condition || detail.Length == 0 ? "" : " | " + detail)}");
    if (condition) pass++; else fail++;
}

void RunSection(string title, Action<Action<string, bool, string>> section)
{
    try { section((n, c, d) => Report(n, c, d)); }
    catch (Exception ex) { Report($"{title} section executes headlessly", false, ex.GetType().Name + ": " + ex.Message); }
}

RunSection("A", check =>
{
    void Eq(string name, string actual, string expected) =>
        check(name, actual == expected, $"expected '{expected}', got '{actual}'");

    var d = NumFmtSettings.Defaults;
    Eq("A1 qty digits default to 3, grouped, rounded",
        NumFmt.FormatValue(1234.56789m, d.QtyDigits, d), "1,234.568");
    Eq("A2 price digits default to 2",
        NumFmt.FormatValue(1234.56789m, d.PriceDigits, d), "1,234.57");
    Eq("A3 grouping off removes the separator",
        NumFmt.FormatValue(1234.5m, 2, d with { GroupThousands = false }), "1234.50");
    Eq("A4 trailing zeros off trims to the significant fraction",
        NumFmt.FormatValue(2.5m, 3, d with { ShowTrailingZeros = false }), "2.5");
    Eq("A5 trailing zeros off keeps a significant fraction intact",
        NumFmt.FormatValue(2.05m, 3, d with { ShowTrailingZeros = false }), "2.05");
    Eq("A6 rounding off truncates toward zero",
        NumFmt.FormatValue(1.239m, 2, d with { RoundDisplay = false }), "1.23");
    Eq("A7 rounding on rounds half away from zero",
        NumFmt.FormatValue(1.239m, 2, d), "1.24");
    Eq("A8 zero digits truncates the fraction entirely when rounding is off",
        NumFmt.FormatValue(2.9m, 0, d with { RoundDisplay = false }), "2");
    Eq("A9 date follows the chosen pattern (dd/MM/yyyy)",
        NumFmt.FormatDate(new DateTime(2026, 2, 3), "dd/MM/yyyy"), "03/02/2026");
    Eq("A10 date follows the chosen pattern (yyyy-MM-dd)",
        NumFmt.FormatDate(new DateTime(2026, 2, 3), "yyyy-MM-dd"), "2026-02-03");
    Eq("A11 time follows the chosen pattern (h:mm tt)",
        NumFmt.FormatTimeOfDay(new DateTime(2026, 1, 1, 14, 5, 0), "h:mm tt"), "2:05 PM");
    Eq("A12 time follows the chosen pattern (HH:mm)",
        NumFmt.FormatTimeOfDay(new DateTime(2026, 1, 1, 14, 5, 0), "HH:mm"), "14:05");
});

RunSection("B", check => PrintConsumerChecks.Run(check));
RunSection("D", check => PrintSettingsChecks.RunComposer(check));
RunSection("E", check => PrintSettingsChecks.RunDb(check));

Console.WriteLine($"== FmtHarness: {pass} PASS / {fail} FAIL ==");
return fail == 0 ? 0 : 1;
