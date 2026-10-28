using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Printing;

namespace ShouTech.App.Services;

/// <summary>Per-user cash drawer printer configuration and ESC/POS drawer kick.</summary>
public static class CashDrawerService
{
    private sealed class Settings { public string PrinterName { get; set; } = string.Empty; }
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShouTech", "pos-settings.json");

    public static string SelectedPrinterName
    {
        get { lock (Gate) return Load().PrinterName; }
    }

    public static IReadOnlyList<string> GetInstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections })
                .Select(q => { var name = q.Name; q.Dispose(); return name; })
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    public static void SavePrinter(string? printerName)
    {
        lock (Gate)
        {
            var settings = new Settings { PrinterName = printerName?.Trim() ?? string.Empty };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
        }
    }

    public static bool TryOpenDrawer(out string message)
    {
        var printer = SelectedPrinterName;
        if (string.IsNullOrWhiteSpace(printer))
        {
            message = "No cash drawer printer is selected. Open Tools > Options and select the printer connected to the cash drawer.";
            return false;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!OpenPrinter(printer, out handle, IntPtr.Zero))
                throw new InvalidOperationException($"Windows could not open printer '{printer}' (error {Marshal.GetLastWin32Error()}).");
            var doc = new DOC_INFO_1 { pDocName = "ShouTech POS - Open Cash Drawer", pOutputFile = null, pDataType = "RAW" };
            if (StartDocPrinter(handle, 1, ref doc) == 0) throw new InvalidOperationException("Could not start the raw printer job.");
            try
            {
                if (!StartPagePrinter(handle)) throw new InvalidOperationException("Could not start the printer page.");
                try
                {
                    // ESC p m t1 t2: standard pulse for a cash drawer connected to a receipt printer.
                    byte[] command = { 0x1B, 0x70, 0x00, 0x32, 0xFA };
                    if (!WritePrinter(handle, command, command.Length, out var written) || written != command.Length)
                        throw new InvalidOperationException("The printer did not accept the cash-drawer command.");
                }
                finally { EndPagePrinter(handle); }
            }
            finally { EndDocPrinter(handle); }
            message = $"Cash-drawer open command sent to '{printer}'.";
            return true;
        }
        catch (Exception ex) { message = $"Unable to open cash drawer: {ex.Message}"; return false; }
        finally { if (handle != IntPtr.Zero) ClosePrinter(handle); }
    }

    private static Settings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings() : new Settings(); }
        catch { return new Settings(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1 { [MarshalAs(UnmanagedType.LPWStr)] public string pDocName; [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile; [MarshalAs(UnmanagedType.LPWStr)] public string pDataType; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 pDocInfo);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);
}
