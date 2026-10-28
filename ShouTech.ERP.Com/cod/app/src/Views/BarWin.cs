using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Common;

namespace ShouTech.App.Views;

/// <summary>
/// "Create barcode" (Inventory > Barcode) — Phase 7B role 3.
/// Creates a barcode label for ONE material (picked from the materials
/// card or entered manually) and prints the requested number of copies.
/// The preview and print output use the real Code-128 encoder.
/// </summary>
public sealed class BarWin : BaseWin
{
    private readonly TextBox _txtItemCode = new() { Width = 130, Height = 26, Text = "ITEM-001" };
    private readonly TextBox _txtItemName = new() { Width = 230, Height = 26, Text = "Sample Product" };
    private readonly TextBox _txtBarcode = new() { Width = 180, Height = 26, Text = "6281001234567" };
    private readonly TextBox _txtPrice = new() { Width = 100, Height = 26, Text = "25.00" };
    private readonly ComboBox _cbType = new() { Width = 120, Height = 26 };
    private readonly CheckBox _chkIncludePrice = new() { IsChecked = true, Margin = new Thickness(0, 4, 10, 4) };
    private readonly CheckBox _chkIncludeName = new() { IsChecked = true, Margin = new Thickness(0, 4, 10, 4) };
    private readonly TextBox _txtCopies = new() { Width = 70, Height = 26, Text = "1" };

    private readonly Button _btnGen = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnPick = new();

    private readonly Border _previewCard = new();
    private readonly TextBlock _prevName = new();
    private readonly TextBlock _prevPrice = new();
    private readonly StackPanel _prevBarcodeBars = new();
    private readonly TextBlock _prevCode = new();

    public BarWin()
    {
        Width = 760;
        Height = 500;
        MinWidth = 660;
        MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        UpdatePreview();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        StyleButton(_btnPick, T("Invoice.FindMaterial"));
        StyleButton(_btnGen, T("Common.New"));
        StyleButton(_btnPrint, T("BarcodeTool.Print"));

        _btnPick.Click += (_, _) => PickMaterial();
        _btnPrint.Click += (_, _) => PrintCopies();
        _btnGen.Click += (_, _) =>
        {
            // A random but VALID EAN-13 — the checksum digit is computed,
            // so the generated value is genuinely scannable.
            _txtBarcode.Text = Code128.BuildEan13(
                "628",
                new Random().Next(100000, 999999));
            UpdatePreview();
        };

        toolbar.Children.Add(_btnPick);
        toolbar.Children.Add(_btnGen);
        toolbar.Children.Add(_btnPrint);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // 2. Main split layout
        var splitGrid = new Grid();
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var formPanel = new StackPanel { Margin = new Thickness(2) };

        var r1 = new WrapPanel();
        r1.Children.Add(Labeled("Invoice.Col.Code", _txtItemCode));
        r1.Children.Add(Labeled("BarcodeTool.Type", _cbType));
        formPanel.Children.Add(r1);

        formPanel.Children.Add(Labeled("Invoice.Col.Description", _txtItemName));

        var r2 = new WrapPanel();
        r2.Children.Add(Labeled("BarcodeTool.Value", _txtBarcode));
        r2.Children.Add(Labeled("Invoice.Col.SalePrice", _txtPrice));
        formPanel.Children.Add(r2);

        var r3 = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        r3.Children.Add(_chkIncludeName);
        r3.Children.Add(_chkIncludePrice);
        formPanel.Children.Add(r3);

        var r4 = new WrapPanel();
        r4.Children.Add(Labeled("BarcodeTool.Copies", _txtCopies));
        formPanel.Children.Add(r4);

        _txtItemName.TextChanged += (_, _) => UpdatePreview();
        _txtBarcode.TextChanged += (_, _) => UpdatePreview();
        _txtPrice.TextChanged += (_, _) => UpdatePreview();
        _chkIncludeName.Checked += (_, _) => UpdatePreview();
        _chkIncludeName.Unchecked += (_, _) => UpdatePreview();
        _chkIncludePrice.Checked += (_, _) => UpdatePreview();
        _chkIncludePrice.Unchecked += (_, _) => UpdatePreview();

        Grid.SetColumn(formPanel, 0);
        splitGrid.Children.Add(formPanel);

        // Right Preview Container
        var previewContainer = new Border
        {
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        previewContainer.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        previewContainer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var previewStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var previewLabel = new TextBlock
        {
            Text = T("BarcodeTool.Preview"),
            Tag = "BarcodeTool.Preview",
            Margin = new Thickness(0, 0, 0, 10),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        previewStack.Children.Add(previewLabel);

        _previewCard.Width = 240;
        _previewCard.Height = 150;
        _previewCard.Background = Brushes.White;
        _previewCard.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        _previewCard.BorderThickness = new Thickness(1.5);
        _previewCard.CornerRadius = new CornerRadius(5);
        _previewCard.Padding = new Thickness(10);

        var cardContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        _prevName.FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(13);
        _prevName.FontWeight = FontWeights.Bold;
        _prevName.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextPrimary", "#202124");
        _prevName.HorizontalAlignment = HorizontalAlignment.Center;
        _prevName.Margin = new Thickness(0, 0, 0, 4);
        cardContent.Children.Add(_prevName);

        _prevPrice.FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(14);
        _prevPrice.FontWeight = FontWeights.Bold;
        _prevPrice.SetResourceReference(TextBlock.ForegroundProperty, "InfoBrush");
        _prevPrice.HorizontalAlignment = HorizontalAlignment.Center;
        _prevPrice.Margin = new Thickness(0, 0, 0, 6);
        cardContent.Children.Add(_prevPrice);

        _prevBarcodeBars.Orientation = Orientation.Horizontal;
        _prevBarcodeBars.HorizontalAlignment = HorizontalAlignment.Center;
        _prevBarcodeBars.Margin = new Thickness(0, 0, 0, 4);
        cardContent.Children.Add(_prevBarcodeBars);

        _prevCode.FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11);
        _prevCode.FontFamily = ShouTech.App.Services.VisuId.ResolveFontFamily("MonospaceFontFamily", "Consolas");
        _prevCode.Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextPrimary", "#202124");
        _prevCode.HorizontalAlignment = HorizontalAlignment.Center;
        cardContent.Children.Add(_prevCode);

        _previewCard.Child = cardContent;
        previewStack.Children.Add(_previewCard);

        previewContainer.Child = previewStack;
        Grid.SetColumn(previewContainer, 2);
        splitGrid.Children.Add(previewContainer);

        root.Children.Add(splitGrid);
        Content = root;
    }

    // ------------------------------------------------------------------
    // Material picker
    // ------------------------------------------------------------------

    private void PickMaterial()
    {
        try
        {
            var material = SerWin.PickMaterial(this, _txtItemCode.Text);
            if (material == null)
                return;

            _txtItemCode.Text = material.Code;

            var name = string.Equals(
                App.CurrentLanguageCode,
                "ar",
                StringComparison.OrdinalIgnoreCase)
                ? material.Name
                : string.IsNullOrWhiteSpace(material.NameEn) ? material.Name : material.NameEn;
            _txtItemName.Text = name;

            if (string.IsNullOrWhiteSpace(_txtBarcode.Text))
                _txtBarcode.Text = material.Code;

            UpdatePreview();
        }
        catch
        {
            // The picker is optional; failures must not break the window.
        }
    }

    // ------------------------------------------------------------------
    // Preview (real Code-128)
    // ------------------------------------------------------------------

    private void UpdatePreview()
    {
        _prevName.Text = _chkIncludeName.IsChecked == true ? _txtItemName.Text : string.Empty;
        _prevPrice.Text = _chkIncludePrice.IsChecked == true ? _txtPrice.Text : string.Empty;
        _prevCode.Text = _txtBarcode.Text;

        _prevBarcodeBars.Children.Clear();
        var code = _txtBarcode.Text;
        if (string.IsNullOrWhiteSpace(code)) code = "12345678";

        // Real Code-128 render, compact module width to fit the 240pt card.
        _prevBarcodeBars.Children.Add(Code128.BuildVisual(
            code,
            moduleWidth: 0.9,
            barHeight: 44,
            quietZone: 4));
    }

    // ------------------------------------------------------------------
    // Printing
    // ------------------------------------------------------------------

    private void PrintCopies()
    {
        var code = (_txtBarcode.Text ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            MessageBox.Show(this, T("Messages.InvalidInput"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ = int.TryParse(_txtCopies.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var copies);
        if (copies < 1) copies = 1;
        if (copies > 500) copies = 500;

        var name = _chkIncludeName.IsChecked == true ? _txtItemName.Text : string.Empty;
        var priceText = _chkIncludePrice.IsChecked == true ? _txtPrice.Text : null;

        var printed = BarcodeLabelPrinter.PrintCopies(
            this,
            T("BarcodeTool.Print"),
            name,
            code,
            priceText,
            copies,
            columns: 3);

        if (printed)
        {
            MessageBox.Show(
                this,
                T("BarcodeTool.PrintSuccess"),
                T("BarcodeTool.Print"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    // ------------------------------------------------------------------
    // Localization helpers
    // ------------------------------------------------------------------

    protected override void ApplyLocalizedText()
    {
        Title = L("Inventory.CreateBarcode", "Create barcode");
        _chkIncludeName.Content = T("BarcodeTool.IncludeName");
        _chkIncludePrice.Content = T("BarcodeTool.IncludePrice");

        // Only symbologies the engine actually renders are offered —
        // never show an option that prints something else.
        _cbType.Items.Clear();
        _cbType.Items.Add("Code 128");
        _cbType.SelectedIndex = 0;

        RefreshLocalizedContent(Content as DependencyObject);
        UpdatePreview();
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

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(VisualTreeHelper.GetChild(parent, i));
    }

    private static StackPanel Labeled(string key, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 6) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 2), Opacity = 0.85 });
        if (control is TextBox textBox)
            textBox.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        else if (control is ComboBox comboBox)
            comboBox.SetResourceReference(StyleProperty, "VisuComboBoxStyle");
        sp.Children.Add(control);
        return sp;
    }

    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 5, 0);
        btn.Padding = new Thickness(9, 3, 9, 3);
        btn.MinWidth = 76;
        btn.Height = 28;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }
}
