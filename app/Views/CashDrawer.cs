using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>Choose the Windows receipt printer connected to the POS cash drawer.</summary>
public sealed class CashDrawerOptionsWin : BaseWin
{
    private readonly TextBlock _heading = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(17), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) };
    private readonly ComboBox _printers = new() { Margin = new Thickness(0, 8, 0, 14) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _save = new() { MinWidth = ShouTech.App.Services.VisuId.PrimaryButtonMinWidth, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _test = new() { MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _close = new() { MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth };

    public CashDrawerOptionsWin()
    {
        Width = 570; Height = 300; ResizeMode = ResizeMode.NoResize; // Step 12: 270 clipped ~9 DIP of content on every screen (sweep MINOR); 300 fits the cap
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(_heading);
        root.Children.Add(_hint);
        root.Children.Add(_printers);
        root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(buttons);
        _test.Click += (_, _) => { var ok = CashDrawerService.TryOpenDrawer(out var message); _status.Text = message; _status.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource(ok ? "SuccessBrush" : "ErrorBrush", ok ? "#198754" : "#C0392B"); };
        _save.Click += (_, _) => { CashDrawerService.SavePrinter(_printers.SelectedItem as string); _status.Text = L("POS.CashDrawerSaved", "Cash drawer printer setting saved."); _status.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("SuccessBrush", "#198754"); };
        _close.Click += (_, _) => Close();
        buttons.Children.Add(_test); buttons.Children.Add(_save); buttons.Children.Add(_close);
        root.Children.Add(buttons);
        Content = root;
        var names = CashDrawerService.GetInstalledPrinters();
        _printers.ItemsSource = names;
        var selected = CashDrawerService.SelectedPrinterName;
        if (!string.IsNullOrWhiteSpace(selected) && names.Contains(selected, StringComparer.OrdinalIgnoreCase)) _printers.SelectedItem = names.First(n => n.Equals(selected, StringComparison.OrdinalIgnoreCase));
        else if (names.Count > 0) _printers.SelectedIndex = 0;
        else _status.Text = L("POS.CashDrawerNoPrinters", "No Windows printers were found. Install the receipt printer first, then reopen this screen.");
        ApplyLocalizedText();
    }

    private static string L(string key, string fallback)
    {
        var value = T(key);
        return value == key ? fallback : value;
    }

    protected override void ApplyLocalizedText()
    {
        Title = L("POS.CashDrawerOptionsTitle", "POS Cash Drawer Options");
        _heading.Text = L("POS.CashDrawerPrinter", "Cash drawer printer");
        _hint.Text = L("POS.CashDrawerPrinterHint", "Choose the receipt printer physically connected to the cash drawer. The printer must support an ESC/POS drawer-kick command.");
        _test.Content = L("POS.CashDrawerTest", "Test / Open drawer");
        _save.Content = T("Common.Save");
        _close.Content = T("Common.Close");
    }
}
