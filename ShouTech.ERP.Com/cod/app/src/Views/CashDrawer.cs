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
    private readonly ComboBox _printers = new() { MinWidth = 380, Height = 34, Margin = new Thickness(0, 8, 0, 14) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _save = new() { Content = "Save", MinWidth = 100, Height = 36, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _test = new() { Content = "Test / Open drawer", MinWidth = 150, Height = 36, Margin = new Thickness(0, 0, 8, 0) };

    public CashDrawerOptionsWin()
    {
        Title = "POS Cash Drawer Options";
        Width = 570; Height = 270; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "Cash drawer printer", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(17), FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "Choose the receipt printer physically connected to the cash drawer. The printer must support an ESC/POS drawer-kick command.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) });
        root.Children.Add(_printers);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _test.Click += (_, _) => { var ok = CashDrawerService.TryOpenDrawer(out var message); _status.Text = message; _status.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource(ok ? "SuccessBrush" : "ErrorBrush", ok ? "#198754" : "#C0392B"); };
        _save.Click += (_, _) => { CashDrawerService.SavePrinter(_printers.SelectedItem as string); _status.Text = "Cash drawer printer setting saved."; _status.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("SuccessBrush", "#198754"); };
        var close = new Button { Content = "Close", MinWidth = 80, Height = 36 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(_test); buttons.Children.Add(_save); buttons.Children.Add(close);
        root.Children.Add(buttons); root.Children.Add(_status);
        Content = root;
        var names = CashDrawerService.GetInstalledPrinters();
        _printers.ItemsSource = names;
        var selected = CashDrawerService.SelectedPrinterName;
        if (!string.IsNullOrWhiteSpace(selected) && names.Contains(selected, StringComparer.OrdinalIgnoreCase)) _printers.SelectedItem = names.First(n => n.Equals(selected, StringComparison.OrdinalIgnoreCase));
        else if (names.Count > 0) _printers.SelectedIndex = 0;
        else _status.Text = "No Windows printers were found. Install the receipt printer first, then reopen this screen.";
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        // Current language/RTL is inherited from BaseWin; retain simple English fallback until option keys are added to all language packs.
        var title = T("POS.CashDrawerOptionsTitle");
        Title = title == "POS.CashDrawerOptionsTitle" ? "POS Cash Drawer Options" : title;
    }
}
