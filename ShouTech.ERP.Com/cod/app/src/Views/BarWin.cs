using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Common;
using ShouTech.App.Services;
using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// "Create barcode" (Inventory > Barcode) — Phase 7B role 3.
/// Creates a barcode label for ONE material (picked from the materials
/// card or entered manually) and prints the requested number of copies.
/// The preview and print output use the real Code-128 encoder.
/// </summary>
public sealed class BarWin : BaseWin
{
    private readonly TextBox _txtItemCode = new();
    private readonly TextBox _txtItemName = new();
    private readonly TextBox _txtBarcode = new();
    private readonly TextBox _txtPrice = new() { Text = "0.00" };
    private readonly ComboBox _cbType = new();
    private readonly CheckBox _chkIncludePrice = new() { IsChecked = true, Margin = new Thickness(0, 4, 10, 4) };
    private readonly CheckBox _chkIncludeName = new() { IsChecked = true, Margin = new Thickness(0, 4, 10, 4) };
    private readonly TextBox _txtCopies = new() { Text = "1" };

    private readonly Button _btnGen = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnPick = new();
    private readonly Button _btnSave = new();

    // The material the barcode/price belong to (set by the picker; a typed
    // code is re-resolved at save time so the save never targets a stale pick).
    private long? _materialId;
    private string _pickedCode = string.Empty;

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
        var toolbar = new WrapPanel { Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnPick, T("Invoice.FindMaterial"));
        StyleButton(_btnGen, T("Common.New"));
        StyleButton(_btnSave, T("BarcodeTool.SaveToMaterial"));
        StyleButton(_btnPrint, T("BarcodeTool.Print"));

        _btnPick.Click += (_, _) => PickMaterial();
        _btnSave.Click += (_, _) => SaveToMaterial();
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
        toolbar.Children.Add(_btnSave);
        toolbar.Children.Add(_btnPrint);
        // Explorer-style bottom command bar: flat, transparent, subtle 1px top divider.
        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBar, Dock.Bottom);
        root.Children.Add(actionBar);

        // 2. Main split layout
        var splitGrid = new Grid();
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left panel = a one-pair form grid: side labels, fields stretch to
        // the panel width, edges aligned down the whole panel (Phenix rule).
        var formPanel = CreateFormGrid(1);
        formPanel.Margin = new Thickness(2);
        AddFormField(formPanel, 0, 0, "Invoice.Col.Code", _txtItemCode);
        AddFormField(formPanel, 1, 0, "BarcodeTool.Type", _cbType);
        AddFormField(formPanel, 2, 0, "Invoice.Col.Description", _txtItemName);
        AddFormField(formPanel, 3, 0, "BarcodeTool.Value", _txtBarcode);
        AddFormField(formPanel, 4, 0, "Invoice.Col.SalePrice", _txtPrice);
        AddFormElement(formPanel, 5, 0, _chkIncludeName);
        AddFormElement(formPanel, 6, 0, _chkIncludePrice);
        AddFormField(formPanel, 7, 0, "BarcodeTool.Copies", _txtCopies);

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

            _materialId = material.Id;
            _pickedCode = material.Code;
            _txtItemCode.Text = material.Code;

            var name = string.Equals(
                App.CurrentLanguageCode,
                "ar",
                StringComparison.OrdinalIgnoreCase)
                ? material.Name
                : string.IsNullOrWhiteSpace(material.NameEn) ? material.Name : material.NameEn;
            _txtItemName.Text = name;

            // Load the material's persisted barcode and retail price so the
            // window edits the real card values, not blanks.
            if (!string.IsNullOrWhiteSpace(material.Unit1Barcode))
                _txtBarcode.Text = material.Unit1Barcode;
            _txtPrice.Text = material.PriceRetail.ToString("0.00", CultureInfo.InvariantCulture);

            UpdatePreview();
        }
        catch (Exception ex)
        {
            // The picker is optional; failures must not break the window,
            // but they are logged instead of silently swallowed.
            AppHost.Logging?.LogError("BarcodeCreate", "Material picker failed", ex);
        }
    }

    /// <summary>
    /// Persists the barcode and retail price onto the material card
    /// (materials.unit1_barcode / price_retail) through MatRepo — the same
    /// full-record save BcdGenWin uses, so no other material field is lost.
    /// Before this, the window printed labels but never saved anything.
    /// </summary>
    private void SaveToMaterial()
    {
        if (!AppHost.Permissions.Has(PermCat.Inventory.ItemUpdate))
        {
            ShowWarn("Cards.PermissionDenied");
            return;
        }

        var code = _txtItemCode.Text.Trim();
        long? id = _materialId.HasValue && string.Equals(code, _pickedCode, StringComparison.OrdinalIgnoreCase)
            ? _materialId
            : CardStore.ResolveMaterial(code)?.Id;
        if (id is null)
        {
            ShowWarn("BarcodeTool.MaterialNotFound");
            return;
        }
        if (!decimal.TryParse(_txtPrice.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var price)
            && !decimal.TryParse(_txtPrice.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out price))
        {
            ShowWarn("Cards.InvalidAmount");
            return;
        }

        try
        {
            var full = MatRepo.LoadById(id.Value, includeChildren: true);
            if (full is null)
            {
                ShowWarn("Cards.NotFound");
                return;
            }
            full.Unit1Barcode = string.IsNullOrWhiteSpace(_txtBarcode.Text) ? null : _txtBarcode.Text.Trim();
            full.PriceRetail = price;
            MatRepo.Save(full, isNew: false);
            _materialId = full.Id;
            _pickedCode = full.Code;
            ShowInfo("BarcodeTool.SavedToMaterial");
        }
        catch (Exception ex)
        {
            AppHost.Logging?.LogError("BarcodeCreate", $"Save barcode to material failed | Code={code}", ex);
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        catch (Exception ex)
        {
            AppHost.Logging?.LogError("BarWin", $"Localization lookup failed | Key={key}", ex);
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


    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 8, 0);
        btn.MinWidth = VisuId.ButtonMinWidth;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }
}
