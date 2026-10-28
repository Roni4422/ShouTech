using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ShouTech.App.Common;

namespace ShouTech.App.Views;

/// <summary>
/// Phase 7B - "Design barcode" (Inventory > Barcode).
/// A mini label-design page: a style toolbar at the top controls label size,
/// fonts, colors, and visible elements, with a live preview of the barcode
/// label rendered with the real Code-128 encoder. The design persists to a
/// local config file and the designed label can be printed directly.
/// </summary>
public sealed class BcdDesWin : BaseWin
{
    private readonly TextBox _txtWidth = new() { Width = 60, Text = "240" };
    private readonly TextBox _txtHeight = new() { Width = 60, Text = "150" };
    private readonly TextBox _txtModuleWidth = new() { Width = 45, Text = "2" };
    private readonly TextBox _txtBarHeight = new() { Width = 45, Text = "44" };
    private readonly ComboBox _cbFont = new() { Width = 120 };
    private readonly ComboBox _cbFontSize = new() { Width = 55 };
    private readonly CheckBox _chkBold = new() { Content = "B", FontWeight = FontWeights.Bold, IsChecked = true };
    private readonly ComboBox _cbTextColor = new() { Width = 100 };
    private readonly ComboBox _cbBarColor = new() { Width = 100 };
    private readonly ComboBox _cbBackground = new() { Width = 100 };
    private readonly CheckBox _chkShowName = new() { Content = "Name", IsChecked = true };
    private readonly CheckBox _chkShowPrice = new() { Content = "Price", IsChecked = true };
    private readonly CheckBox _chkShowCode = new() { Content = "Code", IsChecked = true };
    private readonly Button _btnSave = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnReset = new();
    private readonly Button _btnPrint = new();

    private readonly TextBox _txtName = new() { Width = 150, Text = "Sample Product" };
    private readonly TextBox _txtPrice = new() { Width = 70, Text = "25.00" };
    private readonly TextBox _txtCode = new() { Width = 150, Text = "6281001234567" };

    private readonly Border _label = new();
    private readonly TextBlock _prevName = new();
    private readonly TextBlock _prevPrice = new();
    private readonly StackPanel _prevBars = new();
    private readonly TextBlock _prevCode = new();

    private static string DesignFolder =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShouTech");

    private static string DesignFile =>
        System.IO.Path.Combine(DesignFolder, "barcode-design.cfg");

    public BcdDesWin()
    {
        Width = 880;
        Height = 620;
        MinWidth = 720;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        LoadDesign();
        UpdatePreview();
    }

    protected override void ApplyLocalizedText()
    {
        Title = L("Inventory.DesignBarcode", "Design and print barcode");
        _btnSave.Content = L("BarcodeDesign.SaveDesign", "Save design");
        _btnLoad.Content = L("BarcodeDesign.LoadDesign", "Load design");
        _btnReset.Content = T("Common.Refresh");
        _btnPrint.Content = T("Common.Print");
        _chkBold.Content = T("BarcodeDesign.Bold");
        _chkShowName.Content = T("BarcodeDesign.ShowName");
        _chkShowPrice.Content = T("BarcodeDesign.ShowPrice");
        _chkShowCode.Content = T("BarcodeDesign.ShowCode");
    }

    private static string L(string key, string fallback)
    {
        try
        {
            var s = T(key);
            return string.IsNullOrWhiteSpace(s) || string.Equals(s, key, StringComparison.Ordinal)
                ? fallback
                : s;
        }
        catch
        {
            return fallback;
        }
    }

    // ------------------------------------------------------------------
    // UI
    // ------------------------------------------------------------------

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        foreach (var combo in new[] { _cbFont, _cbFontSize, _cbTextColor, _cbBarColor, _cbBackground })
            combo.SetResourceReference(StyleProperty, "VisuComboBoxStyle");

        root.Children.Add(BuildToolbar());

        // Main split: settings (left) + preview (right).
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var settingsCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1)
        };
        settingsCard.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        settingsCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        settingsCard.Child = BuildSettings();
        Grid.SetColumn(settingsCard, 0);
        split.Children.Add(settingsCard);

        var previewCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1)
        };
        previewCard.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        previewCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var previewHost = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        previewHost.Content = BuildPreview();
        previewCard.Child = previewHost;
        Grid.SetColumn(previewCard, 2);
        split.Children.Add(previewCard);

        root.Children.Add(split);
        Content = root;
    }

    private WrapPanel BuildToolbar()
    {
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

        StyleButton(_btnSave, L("BarcodeDesign.SaveDesign", "Save design"));
        _btnSave.Click += (_, _) => SaveDesign();
        toolbar.Children.Add(_btnSave);

        StyleButton(_btnLoad, L("BarcodeDesign.LoadDesign", "Load design"));
        _btnLoad.Click += (_, _) => { LoadDesign(); UpdatePreview(); };
        toolbar.Children.Add(_btnLoad);

        StyleButton(_btnReset, T("Common.Refresh"));
        _btnReset.Click += (_, _) => { ResetDesign(); UpdatePreview(); };
        toolbar.Children.Add(_btnReset);

        StyleButton(_btnPrint, T("Common.Print"));
        _btnPrint.Click += (_, _) => PrintDesignedLabel();
        toolbar.Children.Add(_btnPrint);

        return toolbar;
    }

    private StackPanel BuildSettings()
    {
        var panel = new StackPanel();

        // Label size.
        var sizeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        sizeRow.Children.Add(MakeLabel(L("BarcodeDesign.LabelSize", "Label size:")));
        _txtWidth.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        sizeRow.Children.Add(_txtWidth);
        sizeRow.Children.Add(MakeLabel("x"));
        _txtHeight.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        sizeRow.Children.Add(_txtHeight);
        panel.Children.Add(sizeRow);

        // Barcode size (module width + bar height).
        var barcodeSizeRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        barcodeSizeRow.Children.Add(MakeLabel(L("BarcodeDesign.BarSize", "Barcode size:")));
        _txtModuleWidth.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        _txtModuleWidth.TextChanged += (_, _) => UpdatePreview();
        barcodeSizeRow.Children.Add(MakeLabel(L("BarcodeDesign.BarWidth", "width")));
        barcodeSizeRow.Children.Add(_txtModuleWidth);
        _txtBarHeight.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        _txtBarHeight.TextChanged += (_, _) => UpdatePreview();
        barcodeSizeRow.Children.Add(MakeLabel(L("BarcodeDesign.BarHeight", "height")));
        barcodeSizeRow.Children.Add(_txtBarHeight);
        panel.Children.Add(barcodeSizeRow);

        // Font family + size + bold.
        var fontRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        fontRow.Children.Add(MakeLabel(L("BarcodeDesign.Font", "Font:")));
        foreach (var family in new[]
                 { "Cairo", "Somar Sans", "Alameen Sans", "Segoe UI", "Tahoma", "Arial", "Consolas", "Times New Roman" })
            _cbFont.Items.Add(family);
        _cbFont.SelectedIndex = 0;
        fontRow.Children.Add(_cbFont);

        foreach (var size in new[] { "10", "12", "13", "14", "16", "18", "22" })
            _cbFontSize.Items.Add(size);
        _cbFontSize.SelectedItem = "13";
        fontRow.Children.Add(MakeLabel(L("BarcodeDesign.FontSize", "Size:")));
        fontRow.Children.Add(_cbFontSize);
        _chkBold.Margin = new Thickness(8, 0, 0, 0);
        fontRow.Children.Add(_chkBold);
        panel.Children.Add(fontRow);

        // Colors.
        var colorRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        colorRow.Children.Add(MakeLabel(L("BarcodeDesign.TextColor", "Text:")));
        foreach (var name in new[] { "Black", "Dark Blue", "Dark Red", "Forest Green", "Dim Gray" })
            _cbTextColor.Items.Add(name);
        _cbTextColor.SelectedIndex = 0;
        colorRow.Children.Add(_cbTextColor);
        colorRow.Children.Add(MakeLabel(L("BarcodeDesign.BarColor", "Bars:")));
        foreach (var name in new[] { "Black", "Dark Blue", "Dark Red", "Brown" })
            _cbBarColor.Items.Add(name);
        _cbBarColor.SelectedIndex = 0;
        colorRow.Children.Add(_cbBarColor);
        panel.Children.Add(colorRow);

        var bgRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bgRow.Children.Add(MakeLabel(L("BarcodeDesign.Background", "Background:")));
        foreach (var name in new[] { "White", "Ivory", "Light Yellow", "White Smoke" })
            _cbBackground.Items.Add(name);
        _cbBackground.SelectedIndex = 0;
        bgRow.Children.Add(_cbBackground);
        panel.Children.Add(bgRow);

        // Elements.
        var elementRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        elementRow.Children.Add(MakeLabel(L("BarcodeDesign.Elements", "Elements:")));
        elementRow.Children.Add(_chkShowName);
        _chkShowPrice.Margin = new Thickness(10, 0, 0, 0);
        elementRow.Children.Add(_chkShowPrice);
        _chkShowCode.Margin = new Thickness(10, 0, 0, 0);
        elementRow.Children.Add(_chkShowCode);
        panel.Children.Add(elementRow);

        // Sample content.
        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8)
        };
        sep.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        panel.Children.Add(sep);

        panel.Children.Add(MakeLabel(L("BarcodeDesign.Sample", "Sample content:")));
        var nameRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        _txtName.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        _txtName.TextChanged += (_, _) => UpdatePreview();
        nameRow.Children.Add(MakeLabel(T("MatCard.Name") + ":"));
        nameRow.Children.Add(_txtName);
        panel.Children.Add(nameRow);

        var priceRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        _txtPrice.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        _txtPrice.TextChanged += (_, _) => UpdatePreview();
        priceRow.Children.Add(MakeLabel(T("Invoice.Col.SalePrice") + ":"));
        priceRow.Children.Add(_txtPrice);
        panel.Children.Add(priceRow);

        var codeRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        _txtCode.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        _txtCode.TextChanged += (_, _) => UpdatePreview();
        codeRow.Children.Add(MakeLabel(T("BarcodeTool.Value") + ":"));
        codeRow.Children.Add(_txtCode);
        panel.Children.Add(codeRow);

        // Live updates from all styling controls.
        _txtWidth.TextChanged += (_, _) => UpdatePreview();
        _txtHeight.TextChanged += (_, _) => UpdatePreview();
        _cbFont.SelectionChanged += (_, _) => UpdatePreview();
        _cbFontSize.SelectionChanged += (_, _) => UpdatePreview();
        _chkBold.Checked += (_, _) => UpdatePreview();
        _chkBold.Unchecked += (_, _) => UpdatePreview();
        _cbTextColor.SelectionChanged += (_, _) => UpdatePreview();
        _cbBarColor.SelectionChanged += (_, _) => UpdatePreview();
        _cbBackground.SelectionChanged += (_, _) => UpdatePreview();
        _chkShowName.Checked += (_, _) => UpdatePreview();
        _chkShowName.Unchecked += (_, _) => UpdatePreview();
        _chkShowPrice.Checked += (_, _) => UpdatePreview();
        _chkShowPrice.Unchecked += (_, _) => UpdatePreview();
        _chkShowCode.Checked += (_, _) => UpdatePreview();
        _chkShowCode.Unchecked += (_, _) => UpdatePreview();

        return panel;
    }

    private StackPanel BuildPreview()
    {
        var host = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var caption = new TextBlock
        {
            Text = T("BarcodeTool.Preview"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        host.Children.Add(caption);

        _label.BorderThickness = new Thickness(1.5);
        _label.CornerRadius = new CornerRadius(5);
        _label.Padding = new Thickness(10);

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _prevName.HorizontalAlignment = HorizontalAlignment.Center;
        _prevName.Margin = new Thickness(0, 0, 0, 4);
        content.Children.Add(_prevName);

        _prevPrice.HorizontalAlignment = HorizontalAlignment.Center;
        _prevPrice.Margin = new Thickness(0, 0, 0, 6);
        content.Children.Add(_prevPrice);

        _prevBars.Orientation = Orientation.Horizontal;
        _prevBars.HorizontalAlignment = HorizontalAlignment.Center;
        _prevBars.Margin = new Thickness(0, 0, 0, 4);
        content.Children.Add(_prevBars);

        _prevCode.FontFamily = ShouTech.App.Services.VisuId.ResolveFontFamily("MonospaceFontFamily", "Consolas");
        _prevCode.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(_prevCode);

        _label.Child = content;
        host.Children.Add(_label);
        return host;
    }

    // ------------------------------------------------------------------
    // Live preview (real Code-128 rendering)
    // ------------------------------------------------------------------

    private void UpdatePreview()
    {
        // Label geometry.
        _ = double.TryParse(_txtWidth.Text, out var w);
        _ = double.TryParse(_txtHeight.Text, out var h);
        _label.Width = w > 40 ? w : 240;
        _label.Height = h > 30 ? h : 150;
        _label.Background = ResolveBrush(_cbBackground.SelectedItem as string, Brushes.White);
        _label.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");

        // Font styling.
        var family = new FontFamily((_cbFont.SelectedItem as string) ?? "Segoe UI");
        _ = double.TryParse(_cbFontSize.SelectedItem as string, out var size);
        if (size <= 0) size = 13;

        var textColor = ResolveBrush(_cbTextColor.SelectedItem as string, Brushes.Black);
        var barColor = ResolveBrush(_cbBarColor.SelectedItem as string, Brushes.Black);

        _prevName.Text = _chkShowName.IsChecked == true ? _txtName.Text : string.Empty;
        _prevName.FontFamily = family;
        _prevName.FontSize = size;
        _prevName.FontWeight = _chkBold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        _prevName.Foreground = textColor;

        _prevPrice.Text = _chkShowPrice.IsChecked == true ? _txtPrice.Text : string.Empty;
        _prevPrice.FontFamily = family;
        _prevPrice.FontSize = size + 1;
        _prevPrice.FontWeight = FontWeights.Bold;
        _prevPrice.Foreground = textColor;

        _prevCode.Text = _chkShowCode.IsChecked == true ? _txtCode.Text : string.Empty;
        _prevCode.FontSize = Math.Max(9, size - 2);
        _prevCode.Foreground = textColor;

        // Real Code-128 bars with the chosen bar/background colors.
        _prevBars.Children.Clear();
        var code = _txtCode.Text;
        if (string.IsNullOrWhiteSpace(code)) code = "12345678";

        _ = double.TryParse(_txtModuleWidth.Text, out var moduleWidth);
        if (moduleWidth <= 0) moduleWidth = 2;
        moduleWidth = Math.Min(moduleWidth, 4);

        _ = double.TryParse(_txtBarHeight.Text, out var barHeight);
        if (barHeight <= 0) barHeight = 44;

        _prevBars.Children.Add(Code128.BuildVisual(
            code,
            moduleWidth,
            barHeight,
            quietZone: 4,
            barBrush: barColor,
            backgroundBrush: Brushes.Transparent));
    }

    // ------------------------------------------------------------------
    // Printing the designed label
    // ------------------------------------------------------------------

    private void PrintDesignedLabel()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var page = BuildStyledLabel(includeMargins: true);
            dialog.PrintVisual(page, Title);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Builds a printable visual of the label using the current design.</summary>
    private System.Windows.Controls.StackPanel BuildStyledLabel(bool includeMargins)
    {
        var page = new StackPanel
        {
            Background = Brushes.White,
            Margin = includeMargins ? new Thickness(16) : default
        };

        var label = new Border
        {
            Background = ResolveBrush(_cbBackground.SelectedItem as string, Brushes.White),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        label.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");

        _ = double.TryParse(_txtWidth.Text, out var w);
        _ = double.TryParse(_txtHeight.Text, out var h);
        if (w > 40) label.Width = w;
        if (h > 30) label.MinHeight = h;

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var family = new FontFamily((_cbFont.SelectedItem as string) ?? "Segoe UI");
        _ = double.TryParse(_cbFontSize.SelectedItem as string, out var size);
        if (size <= 0) size = 13;
        var textColor = ResolveBrush(_cbTextColor.SelectedItem as string, Brushes.Black);
        var barColor = ResolveBrush(_cbBarColor.SelectedItem as string, Brushes.Black);

        if (_chkShowName.IsChecked == true && !string.IsNullOrWhiteSpace(_txtName.Text))
        {
            content.Children.Add(new TextBlock
            {
                Text = _txtName.Text,
                FontFamily = family,
                FontSize = size,
                FontWeight = _chkBold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal,
                Foreground = textColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        if (_chkShowPrice.IsChecked == true && !string.IsNullOrWhiteSpace(_txtPrice.Text))
        {
            content.Children.Add(new TextBlock
            {
                Text = _txtPrice.Text,
                FontFamily = family,
                FontSize = size + 1,
                FontWeight = FontWeights.Bold,
                Foreground = textColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        _ = double.TryParse(_txtModuleWidth.Text, out var moduleWidth);
        if (moduleWidth <= 0) moduleWidth = 2;
        _ = double.TryParse(_txtBarHeight.Text, out var barHeight);
        if (barHeight <= 0) barHeight = 56;

        content.Children.Add(Code128.BuildVisual(
            _txtCode.Text,
            Math.Min(moduleWidth, 4),
            barHeight,
            quietZone: 8,
            barBrush: barColor));

        if (_chkShowCode.IsChecked == true && !string.IsNullOrWhiteSpace(_txtCode.Text))
        {
            content.Children.Add(new TextBlock
            {
                Text = _txtCode.Text,
                FontFamily = ShouTech.App.Services.VisuId.ResolveFontFamily("MonospaceFontFamily", "Consolas"),
                FontSize = Math.Max(9, size - 2),
                Foreground = textColor,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        label.Child = content;
        page.Children.Add(label);

        return page;
    }

    // ------------------------------------------------------------------
    // Design persistence
    // ------------------------------------------------------------------

    private static Brush ResolveBrush(string? name, Brush fallback) => name switch
    {
        "Black" => Brushes.Black,
        "Dark Blue" => Brushes.DarkBlue,
        "Dark Red" => Brushes.DarkRed,
        "Forest Green" => Brushes.ForestGreen,
        "Dim Gray" => Brushes.DimGray,
        "Brown" => Brushes.Brown,
        "White" => Brushes.White,
        "Ivory" => Brushes.Ivory,
        "Light Yellow" => Brushes.LightYellow,
        "White Smoke" => Brushes.WhiteSmoke,
        _ => fallback
    };

    private void SaveDesign()
    {
        try
        {
            Directory.CreateDirectory(DesignFolder);
            File.WriteAllLines(DesignFile, new[]
            {
                "width=" + _txtWidth.Text,
                "height=" + _txtHeight.Text,
                "moduleWidth=" + _txtModuleWidth.Text,
                "barHeight=" + _txtBarHeight.Text,
                "font=" + (_cbFont.SelectedItem ?? string.Empty),
                "fontSize=" + (_cbFontSize.SelectedItem ?? string.Empty),
                "bold=" + (_chkBold.IsChecked == true),
                "textColor=" + (_cbTextColor.SelectedItem ?? string.Empty),
                "barColor=" + (_cbBarColor.SelectedItem ?? string.Empty),
                "background=" + (_cbBackground.SelectedItem ?? string.Empty),
                "showName=" + (_chkShowName.IsChecked == true),
                "showPrice=" + (_chkShowPrice.IsChecked == true),
                "showCode=" + (_chkShowCode.IsChecked == true),
                "sampleName=" + _txtName.Text,
                "samplePrice=" + _txtPrice.Text,
                "sampleCode=" + _txtCode.Text
            });

            MessageBox.Show(
                this,
                T("Messages.SaveSuccess"),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShouTech.App.Services.AppHost.Logging?.LogError("BarcodeDesign", "Failed to save barcode design configuration.", ex);
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadDesign()
    {
        try
        {
            if (!File.Exists(DesignFile))
                return;

            foreach (var line in File.ReadAllLines(DesignFile))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                    continue;

                var key = parts[0];
                var value = parts[1];

                switch (key)
                {
                    case "width": _txtWidth.Text = value; break;
                    case "height": _txtHeight.Text = value; break;
                    case "moduleWidth": _txtModuleWidth.Text = value; break;
                    case "barHeight": _txtBarHeight.Text = value; break;
                    case "font": SelectItem(_cbFont, value); break;
                    case "fontSize": SelectItem(_cbFontSize, value); break;
                    case "bold": _chkBold.IsChecked = value == "True"; break;
                    case "textColor": SelectItem(_cbTextColor, value); break;
                    case "barColor": SelectItem(_cbBarColor, value); break;
                    case "background": SelectItem(_cbBackground, value); break;
                    case "showName": _chkShowName.IsChecked = value == "True"; break;
                    case "showPrice": _chkShowPrice.IsChecked = value == "True"; break;
                    case "showCode": _chkShowCode.IsChecked = value == "True"; break;
                    case "sampleName": _txtName.Text = value; break;
                    case "samplePrice": _txtPrice.Text = value; break;
                    case "sampleCode": _txtCode.Text = value; break;
                }
            }
        }
        catch
        {
            // A corrupt design file must never crash the window.
        }
    }

    private static void SelectItem(ComboBox combo, string value)
    {
        var index = combo.Items.IndexOf(value);
        if (index >= 0)
            combo.SelectedIndex = index;
    }

    private void ResetDesign()
    {
        _txtWidth.Text = "240";
        _txtHeight.Text = "150";
        _txtModuleWidth.Text = "2";
        _txtBarHeight.Text = "44";
        _cbFont.SelectedIndex = 0;
        _cbFontSize.SelectedItem = "13";
        _chkBold.IsChecked = true;
        _cbTextColor.SelectedIndex = 0;
        _cbBarColor.SelectedIndex = 0;
        _cbBackground.SelectedIndex = 0;
        _chkShowName.IsChecked = true;
        _chkShowPrice.IsChecked = true;
        _chkShowCode.IsChecked = true;
        _txtName.Text = "Sample Product";
        _txtPrice.Text = "25.00";
        _txtCode.Text = "6281001234567";
    }

    private static TextBlock MakeLabel(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return block;
    }

    private static void StyleButton(Button btn, string text)
    {
        btn.Content = text;
        btn.Margin = new Thickness(0, 0, 6, 0);
        btn.Padding = new Thickness(10, 4, 10, 4);
        btn.Height = 28;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }
}
