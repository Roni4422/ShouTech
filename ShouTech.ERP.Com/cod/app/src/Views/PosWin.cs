using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Data.Common;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.App.Configuration;

namespace ShouTech.App.Views;

/// <summary>Retail cashier workspace. Final invoice posting remains in the existing invoice workflow.</summary>
public sealed class PosWin : BaseWin
{
    private static readonly Brush Ink = ShouTech.App.Services.VisuId.ResolveBrushResource("PosInkBrush", "#172B3A");
    private static readonly Brush Muted = ShouTech.App.Services.VisuId.ResolveBrushResource("PosMutedBrush", "#687985");
    private static readonly Brush Teal = ShouTech.App.Services.VisuId.ResolveBrushResource("PosTealBrush", "#087F8C");
    private static readonly Brush Canvas = ShouTech.App.Services.VisuId.ResolveBrushResource("PosCanvasBrush", "#F3F6F8");
    private static readonly Brush PosBorderBrush = ShouTech.App.Services.VisuId.ResolveBrushResource("PosBorderBrush", "#DCE5EA");
    private readonly TextBox _scan = new() { MinWidth = 260, FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(20), Height = 48, Padding = new Thickness(12, 7, 12, 7), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ObservableCollection<PosLine> _cart = new();
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow };
    private readonly TextBlock _subtotal = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(16), Foreground = Ink };
    private readonly TextBlock _tax = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(16), Foreground = Ink };
    private readonly TextBlock _total = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(30), FontWeight = FontWeights.Bold, Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF") };
    private readonly TextBlock _count = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(13), Foreground = Muted };
    private readonly TextBlock _status = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(13), TextWrapping = TextWrapping.Wrap, Foreground = Muted };
    private readonly System.Collections.Generic.Dictionary<string, Button> _paymentButtons = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedPaymentMethod = "Cash";
    private char? _shortcutCommand;
    private string _shortcutDigits = string.Empty;
    private readonly TextBlock _shortcutHint = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted };
    private readonly TextBlock _statusUser = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted };
    private readonly TextBlock _statusCompany = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted };
    private readonly TextBlock _statusDatabase = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted };
    private readonly TextBlock _statusClock = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<DataGridColumn, string> _columnOriginalHeaders = new();


    public PosWin()
    {
        Title = "ShouTech | Point of Sale";
        Width = 1360; Height = 820; MinWidth = 1080; MinHeight = 680;
        Background = Canvas;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildUi();
        PreviewKeyDown += HandleHotkeys;
        Loaded += (_, _) => { RefreshContextStatus(); _scan.Focus(); };
        _clockTimer.Tick += (_, _) => _statusClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        _clockTimer.Start();
        Closed += (_, _) => _clockTimer.Stop();
    }

    private void HandleHotkeys(object sender, KeyEventArgs e)
    {
        // Once a command prefix is entered, capture the following number until Enter.
        if (_shortcutCommand.HasValue)
        {
            if (e.Key == Key.Enter)
            {
                ApplyShortcutCommand(); e.Handled = true; return;
            }
            if (e.Key == Key.Escape)
            {
                _shortcutCommand = null; _shortcutDigits = string.Empty;
                _shortcutHint.Text = string.Empty; e.Handled = true; return;
            }
            if (e.Key == Key.Back)
            {
                if (_shortcutDigits.Length > 0) _shortcutDigits = _shortcutDigits[..^1];
                UpdateShortcutHint(); e.Handled = true; return;
            }
            var digit = KeyToDigit(e.Key);
            if (digit.HasValue) { _shortcutDigits += digit.Value; UpdateShortcutHint(); e.Handled = true; return; }
            if (e.Key is Key.OemPeriod or Key.Decimal)
            {
                if (!_shortcutDigits.Contains('.')) _shortcutDigits += '.';
                UpdateShortcutHint(); e.Handled = true; return;
            }
            // Do not let unrelated keys accidentally alter the selected line.
            return;
        }

        // Slash opens the unit chooser immediately. Asterisk and plus start numeric commands.
        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true; OpenUnitChooser(); return;
        }
        if ((e.Key == Key.Multiply || (e.Key == Key.D8 && Keyboard.Modifiers == ModifierKeys.Shift)) && _grid.SelectedItem is PosLine)
        {
            StartShortcutCommand('*'); e.Handled = true; return;
        }
        if ((e.Key == Key.Add || e.Key == Key.OemPlus && Keyboard.Modifiers == ModifierKeys.Shift) && _grid.SelectedItem is PosLine)
        {
            StartShortcutCommand('+'); e.Handled = true; return;
        }

        if (e.Key == Key.F2) { _scan.Focus(); _scan.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Delete && _grid.SelectedItem is PosLine selected)
        { _cart.Remove(selected); UpdateTotals(); e.Handled = true; }
        else if (e.Key == Key.Escape && _cart.Count > 0)
        {
            if (MessageBox.Show(this, "Clear all items from this sale?", "Clear sale", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { _cart.Clear(); UpdateTotals(); }
            e.Handled = true;
        }
        else if (e.Key == Key.F12) { Checkout(); e.Handled = true; }
        else if (e.Key == Key.Add && Keyboard.Modifiers == ModifierKeys.Control) { ChangeSelectedQty(1); e.Handled = true; }
        else if (e.Key == Key.Subtract && Keyboard.Modifiers == ModifierKeys.Control) { ChangeSelectedQty(-1); e.Handled = true; }
    }

    private void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: brand, clear instructions, and barcode-first workflow.
        var header = new Border { Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF"), BorderBrush = PosBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14) };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new TextBlock { Text = "SHOUTECH", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(22), FontWeight = FontWeights.ExtraBold, Foreground = Ink });
        brand.Children.Add(new TextBlock { Text = "POINT OF SALE", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = Teal, Margin = new Thickness(1, 1, 0, 0) });
        Grid.SetColumn(brand, 0); headerGrid.Children.Add(brand);

        _scan.ToolTip = "Scan barcode or enter item code, then press Enter (F2 to focus)";
        _scan.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddScannedItem(); e.Handled = true; } };
        var scanPanel = new Grid { VerticalAlignment = VerticalAlignment.Center };
        scanPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scanPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scanPanel.Children.Add(_scan);
        var add = MakeButton("Add item", Teal, ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"), 112, 48);
        add.Margin = new Thickness(8, 0, 0, 0); add.Click += (_, _) => AddScannedItem(); Grid.SetColumn(add, 1); scanPanel.Children.Add(add);
        Grid.SetColumn(scanPanel, 1); headerGrid.Children.Add(scanPanel);

        var shortcut = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        shortcut.Children.Add(new TextBlock { Text = "READY TO SCAN", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = Teal, HorizontalAlignment = HorizontalAlignment.Right });
        shortcut.Children.Add(new TextBlock { Text = "F2 Scan   ·   F12 Checkout", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted, Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetColumn(shortcut, 2); headerGrid.Children.Add(shortcut);
        header.Child = headerGrid; Grid.SetRow(header, 0); root.Children.Add(header);

        // Main work area: fast editable cart on the left, always-visible totals on the right.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        var cartCard = new Border { Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF"), BorderBrush = PosBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 0, 14, 0) };
        var cartLayout = new Grid();
        cartLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cartLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var cartHeader = new Grid { Margin = new Thickness(2, 0, 2, 12) };
        cartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cartTitle = new StackPanel();
        cartTitle.Children.Add(new TextBlock { Text = "Current sale", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(20), FontWeight = FontWeights.Bold, Foreground = Ink });
        cartTitle.Children.Add(_count);
        Grid.SetColumn(cartTitle, 0); cartHeader.Children.Add(cartTitle);
        var remove = MakeButton("Remove selected", ShouTech.App.Services.VisuId.ResolveBrushResource("PosRemoveBackgroundBrush", "#EEF3F5"), Ink, 142, 38);
        remove.Click += (_, _) => { if (_grid.SelectedItem is PosLine line) { _cart.Remove(line); UpdateTotals(); } _scan.Focus(); };
        Grid.SetColumn(remove, 1); cartHeader.Children.Add(remove);
        Grid.SetRow(cartHeader, 0); cartLayout.Children.Add(cartHeader);

        _grid.ItemsSource = _cart;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.Columns.Add(new DataGridTextColumn { Header = "BARCODE", Binding = new System.Windows.Data.Binding(nameof(PosLine.Code)), IsReadOnly = true, Width = 118 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "PRODUCT", Binding = new System.Windows.Data.Binding(nameof(PosLine.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "QTY", Binding = new System.Windows.Data.Binding(nameof(PosLine.Qty)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, StringFormat = "N3" }, Width = 82 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "PRICE", Binding = new System.Windows.Data.Binding(nameof(PosLine.Price)) { StringFormat = "N2" }, IsReadOnly = true, Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "TAX", Binding = new System.Windows.Data.Binding(nameof(PosLine.TaxAmount)) { StringFormat = "N2" }, IsReadOnly = true, Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "LINE TOTAL", Binding = new System.Windows.Data.Binding(nameof(PosLine.Total)) { StringFormat = "N2" }, IsReadOnly = true, Width = 112 });
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTotals));
        _grid.CurrentCellChanged += (_, _) => UpdateTotals();
        Grid.SetRow(_grid, 1); cartLayout.Children.Add(_grid);
        cartCard.Child = cartLayout; Grid.SetColumn(cartCard, 0); body.Children.Add(cartCard);

        var summary = new Border { Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF"), BorderBrush = PosBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18) };
        var summaryLayout = new Grid();
        summaryLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summaryLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        summaryLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        summaryLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var summaryTitle = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        summaryTitle.Children.Add(new TextBlock { Text = "Order summary", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(20), FontWeight = FontWeights.Bold, Foreground = Ink });
        summaryTitle.Children.Add(new TextBlock { Text = "Review totals before checkout", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetRow(summaryTitle, 0); summaryLayout.Children.Add(summaryTitle);

        var totals = new StackPanel();
        totals.Children.Add(TotalRow("Subtotal", _subtotal));
        totals.Children.Add(TotalRow("Tax", _tax));
        var rule = new Border { Height = 1, Background = PosBorderBrush, Margin = new Thickness(0, 13, 0, 13) }; totals.Children.Add(rule);
        var totalPanel = new Border { Background = Ink, CornerRadius = new CornerRadius(10), Padding = new Thickness(15, 14, 15, 15) };
        var totalLayout = new StackPanel();
        totalLayout.Children.Add(new TextBlock { Text = "TOTAL DUE", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("PosTotalLabelBrush", "#B9D4DC") });
        totalLayout.Children.Add(_total);
        totalPanel.Child = totalLayout; totals.Children.Add(totalPanel);

        var payTitle = new TextBlock { Text = "PAYMENT METHOD", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = Teal, Margin = new Thickness(0, 15, 0, 8) };
        totals.Children.Add(payTitle);
        var payGrid = new Grid();
        payGrid.ColumnDefinitions.Add(new ColumnDefinition()); payGrid.ColumnDefinitions.Add(new ColumnDefinition());
        payGrid.RowDefinitions.Add(new RowDefinition()); payGrid.RowDefinitions.Add(new RowDefinition());
        AddPaymentButton(payGrid, "Cash", 0, 0);
        AddPaymentButton(payGrid, "Credit", 1, 0);
        AddPaymentButton(payGrid, "K-Net", 0, 1);
        AddPaymentButton(payGrid, "Visa", 1, 1);
        totals.Children.Add(payGrid);
        UpdatePaymentButtons();
        Grid.SetRow(totals, 1); summaryLayout.Children.Add(totals);

        var note = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 14, 0, 14) };
        note.Children.Add(new TextBlock { Text = "CASHIER SHORTCUTS", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = Teal });
        note.Children.Add(new TextBlock { Text = "F2  Focus barcode field\n/  Choose item unit\n* number + Enter  Set selected quantity\n+ number + Enter  Set selected unit price\nDelete  Remove selected line\nEsc  Clear current sale", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted, LineHeight = 23, Margin = new Thickness(0, 7, 0, 0) });
        note.Children.Add(_shortcutHint);
        Grid.SetRow(note, 2); summaryLayout.Children.Add(note);

        var actions = new StackPanel();
        var clear = MakeButton("Clear sale", ShouTech.App.Services.VisuId.ResolveBrushResource("PosClearBackgroundBrush", "#FFF0F0"), ShouTech.App.Services.VisuId.ResolveBrushResource("PosClearForegroundBrush", "#A33232"), double.NaN, 42);
        clear.HorizontalAlignment = HorizontalAlignment.Stretch; clear.Margin = new Thickness(0, 0, 0, 9);
        clear.Click += (_, _) => ClearSale(); actions.Children.Add(clear);
        var checkout = MakeButton("Review and complete sale  →", Teal, ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"), double.NaN, 54);
        checkout.FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(17); checkout.FontWeight = FontWeights.Bold; checkout.HorizontalAlignment = HorizontalAlignment.Stretch;
        checkout.ToolTip = "F12 — review the selected payment method and complete this POS sale.";
        checkout.Click += (_, _) => Checkout(); actions.Children.Add(checkout);
        _status.Margin = new Thickness(0, 12, 0, 0); actions.Children.Add(_status);
        Grid.SetRow(actions, 3); summaryLayout.Children.Add(actions);
        summary.Child = summaryLayout; Grid.SetColumn(summary, 1); body.Children.Add(summary);

        Grid.SetRow(body, 1); root.Children.Add(body);

        var statusBar = new Border { Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF"), BorderBrush = PosBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 12, 0, 0) };
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_statusUser, 0); statusGrid.Children.Add(_statusUser);
        Grid.SetColumn(_statusCompany, 1); statusGrid.Children.Add(_statusCompany);
        Grid.SetColumn(_statusDatabase, 2); statusGrid.Children.Add(_statusDatabase);
        Grid.SetColumn(_statusClock, 3); statusGrid.Children.Add(_statusClock);
        statusBar.Child = statusGrid; Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);
        Content = root;
        UpdateTotals();
        ApplyLocalizedText();
    }

    private void AddPaymentButton(Grid grid, string method, int column, int row)
    {
        var button = MakeButton(method, ShouTech.App.Services.VisuId.ResolveBrushResource("PosButtonBackgroundBrush", "#F4F7F8"), Ink, double.NaN, 42);
        button.Margin = new Thickness(column == 0 ? 0 : 5, row == 0 ? 0 : 5, column == 0 ? 5 : 0, 0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.ToolTip = method == "Credit"
            ? "Customer-account sale; select a customer in the invoice before saving/posting."
            : $"Select {method} as the intended payment method.";
        button.Click += (_, _) =>
        {
            _selectedPaymentMethod = method;
            UpdatePaymentButtons();
            if (method.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            {
                var opened = CashDrawerService.TryOpenDrawer(out var drawerMessage);
                SetStatus($"Payment method selected: {method}. {drawerMessage}", !opened);
            }
            else
            {
                SetStatus($"Payment method selected: {method}.");
            }
        };
        _paymentButtons[method] = button;
        Grid.SetColumn(button, column); Grid.SetRow(button, row); grid.Children.Add(button);
    }

    private void UpdatePaymentButtons()
    {
        foreach (var pair in _paymentButtons)
        {
            var selected = pair.Key.Equals(_selectedPaymentMethod, StringComparison.OrdinalIgnoreCase);
            pair.Value.Background = selected ? Teal : ShouTech.App.Services.VisuId.ResolveBrushResource("PosButtonBackgroundBrush", "#F4F7F8");
            pair.Value.Foreground = selected ? ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF") : Ink;
            pair.Value.BorderThickness = selected ? new Thickness(2) : new Thickness(0);
            pair.Value.BorderBrush = selected ? ShouTech.App.Services.VisuId.ResolveBrushResource("PosTealDarkBrush", "#075E66") : Brushes.Transparent;
            var caption = Local(pair.Key == "K-Net" ? "KNet" : pair.Key, pair.Key);
            pair.Value.Content = selected ? $"✓  {caption}" : caption;
        }
    }

    private static StackPanel TotalRow(string label, TextBlock value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 11) };
        row.Children.Add(new TextBlock { Text = label, FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(15), Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(value, Dock.Right); value.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(value);
        return new StackPanel { Children = { row } };
    }

    private static Button MakeButton(string text, Brush background, Brush foreground, double width, double height)
    {
        var button = new Button { Content = text, Background = background, Foreground = foreground, BorderThickness = new Thickness(0), Padding = new Thickness(13, 7, 13, 7), Height = height, FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(14), FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand };
        if (!double.IsNaN(width)) button.Width = width;
        return button;
    }

    private void AddScannedItem()
    {
        var code = _scan.Text.Trim();
        if (code.Length == 0) return;
        (long? Id, string Name, decimal Price, string Unit, decimal TaxRate)? material;
        try { material = TaxSvc.ResolveMaterial(code); }
        catch (Exception ex)
        {
            LogPosEvent($"Material lookup exception. Company='{AppState.CurrentCompanyCode ?? ""}', Query='{code}', Error='{ex.GetType().Name}: {ex.Message}'.");
            _scan.Clear(); _scan.Focus(); return;
        }
        if (material is null)
        {
            LogPosEvent($"Material lookup miss. Company='{AppState.CurrentCompanyCode ?? ""}', Query='{code}'.");
            _scan.Clear(); _scan.Focus(); return;
        }
        var existing = _cart.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Qty += 1;
        else _cart.Add(new PosLine { MaterialId = material.Value.Id, Code = code, Name = material.Value.Name, Unit = material.Value.Unit, Price = material.Value.Price, TaxRate = material.Value.TaxRate > 0 ? material.Value.TaxRate : TaxSvc.GetDefaultRate(), Qty = 1 });
        _scan.Clear(); UpdateTotals(); SetStatus($"Added {material.Value.Name} to the sale."); _scan.Focus();
    }

    private void UpdateTotals()
    {
        var currency = UsrPref.Load().CurrencyCode;
        var subtotal = _cart.Sum(x => x.Subtotal);
        var tax = _cart.Sum(x => x.TaxAmount);
        var total = subtotal + tax;
        _subtotal.Text = $"{subtotal:N2} {currency}";
        _tax.Text = $"{tax:N2} {currency}";
        _total.Text = $"{total:N2} {currency}";
        _count.Text = $"{_cart.Count} line(s)  ·  {_cart.Sum(x => x.Qty):N3} item(s)";
        if (_cart.Count == 0) SetStatus("Scan a barcode or enter an item code or name to begin.");
    }

    private static int? KeyToDigit(Key key)
    {
        var code = (int)key;
        var d0 = (int)Key.D0;
        var n0 = (int)Key.NumPad0;
        if (code >= d0 && code <= d0 + 9) return code - d0;
        if (code >= n0 && code <= n0 + 9) return code - n0;
        return null;
    }

    private void StartShortcutCommand(char command)
    {
        _shortcutCommand = command;
        _shortcutDigits = string.Empty;
        _shortcutHint.Text = command == '*' ? "Quantity: type a number, then Enter (Esc to cancel)." : "Unit price: type a number, then Enter (Esc to cancel).";
        _scan.Focus();
    }

    private void UpdateShortcutHint() => _shortcutHint.Text = $"{_shortcutCommand}{_shortcutDigits}  — press Enter to apply, Esc to cancel";

    private void ApplyShortcutCommand()
    {
        var command = _shortcutCommand;
        _shortcutCommand = null;
        var raw = _shortcutDigits;
        _shortcutDigits = string.Empty;
        _shortcutHint.Text = string.Empty;
        if (_grid.SelectedItem is not PosLine line)
        { SetStatus("Select a material line first.", true); return; }
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0 || (command == '*' && value <= 0))
        { SetStatus("Enter a valid positive quantity or non-negative price.", true); return; }
        if (command == '*') line.Qty = value;
        else if (command == '+') line.Price = value;
        _grid.Items.Refresh();
        UpdateTotals();
        SetStatus(command == '*' ? $"Quantity set to {value} for {line.Name}." : $"Unit price set to {value:N2} for {line.Name}.");
        _grid.SelectedItem = line;
        _scan.Focus();
    }

    private void OpenUnitChooser()
    {
        if (_grid.SelectedItem is not PosLine line)
        { SetStatus("Select a material line first.", true); return; }
        if (line.MaterialId is not long materialId)
        { SetStatus("The selected material has no valid identifier.", true); return; }

        try
        {
            var material = MatRepo.LoadById(materialId);
            var units = new System.Collections.Generic.List<MatUnitRow>();
            if (!string.IsNullOrWhiteSpace(material?.UnitsJson))
                units = JsonSerializer.Deserialize<System.Collections.Generic.List<MatUnitRow>>(material.UnitsJson) ?? new();
            if (units.Count == 0 && material != null)
            {
                void AddLegacy(string? name, decimal factor, decimal price, string? barcode)
                {
                    if (!string.IsNullOrWhiteSpace(name)) units.Add(new MatUnitRow { LineNo = units.Count + 1, UnitName = name, Factor = factor > 0 ? factor : 1m, PriceSale = price > 0 ? price : null, Barcode = barcode ?? string.Empty });
                }
                AddLegacy(material.Unit1, material.Unit1Factor, material.PriceRetail, material.Unit1Barcode);
                AddLegacy(material.Unit2, material.Unit2Factor, 0, material.Unit2Barcode);
                AddLegacy(material.Unit3, material.Unit3Factor, 0, material.Unit3Barcode);
                AddLegacy(material.Unit4, material.Unit4Factor, 0, material.Unit4Barcode);
                AddLegacy(material.Unit5, material.Unit5Factor, 0, material.Unit5Barcode);
            }
            units = units.Where(u => !string.IsNullOrWhiteSpace(u.UnitName)).ToList();
            if (units.Count == 0)
            { SetStatus("No units are configured for this material.", true); return; }

            var dlg = new Window { Title = Local("UnitChooserTitle", "Choose material unit"), Width = 520, Height = 360, MinWidth = 420, MinHeight = 260, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize, Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF"), FlowDirection = FlowDirection };
            var layout = new DockPanel { Margin = new Thickness(14) };
            var prompt = new TextBlock { Text = $"{Local("ChooseUnitFor", "Select a unit for")} {line.Name}", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(15), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(prompt, Dock.Top); layout.Children.Add(prompt);
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, ItemsSource = units, Margin = new Thickness(0, 0, 0, 10) };
            grid.Columns.Add(new DataGridTextColumn { Header = Local("Unit", "Unit"), Binding = new System.Windows.Data.Binding(nameof(MatUnitRow.UnitName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = Local("Equivalent", "Equivalent"), Binding = new System.Windows.Data.Binding(nameof(MatUnitRow.Factor)) { StringFormat = "N3" }, Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = Local("UnitPrice", "Unit price"), Binding = new System.Windows.Data.Binding(nameof(MatUnitRow.PriceSale)) { StringFormat = "N2" }, Width = 110 });
            grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is MatUnitRow chosen) { ApplyChosenUnit(line, chosen); dlg.DialogResult = true; dlg.Close(); } };
            DockPanel.SetDock(grid, Dock.Top); layout.Children.Add(grid);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = MakeButton(Local("UseUnit", "Use unit"), Teal, ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"), 100, 36);
            ok.Click += (_, _) => { if (grid.SelectedItem is MatUnitRow chosen) { ApplyChosenUnit(line, chosen); dlg.DialogResult = true; dlg.Close(); } };
            var cancel = MakeButton(Local("Cancel", "Cancel"), ShouTech.App.Services.VisuId.ResolveBrushResource("PosRemoveBackgroundBrush", "#EEF3F5"), Ink, 90, 36); cancel.Margin = new Thickness(8, 0, 0, 0); cancel.Click += (_, _) => dlg.Close();
            buttons.Children.Add(ok); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
            dlg.Content = layout;
            grid.SelectedIndex = Math.Max(0, units.FindIndex(u => string.Equals(u.UnitName, line.Unit, StringComparison.OrdinalIgnoreCase)));
            dlg.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && grid.SelectedItem is MatUnitRow chosen) { ApplyChosenUnit(line, chosen); dlg.DialogResult = true; dlg.Close(); e.Handled = true; } };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            LogPosEvent($"Unit chooser failed for material '{line.Code}': {ex.GetType().Name}: {ex.Message}");
            SetStatus("Unable to load units for the selected material. See application log.", true);
        }
    }

    private void ApplyChosenUnit(PosLine line, MatUnitRow unit)
    {
        line.Unit = unit.UnitName;
        if (unit.PriceSale.HasValue && unit.PriceSale.Value >= 0) line.Price = unit.PriceSale.Value;
        _grid.Items.Refresh();
        UpdateTotals();
        SetStatus($"Unit '{unit.UnitName}' selected for {line.Name}.");
        _scan.Focus();
    }

    private static void LogPosEvent(string message)
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var logDir = Path.Combine(baseDir, "log");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "log.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [POS] {message}{Environment.NewLine}");
        }
        catch { /* Logging must never block checkout. */ }
    }

    protected override void ApplyLocalizedText()
    {
        Title = Local("Title", "ShouTech | Point of Sale");
        // The cashier UI is built in code, so localize its visual text nodes and grid headers
        // whenever the active language changes; BaseWin applies RTL/LTR to the whole window.
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["POINT OF SALE"] = Local("Subtitle", "POINT OF SALE"), ["READY TO SCAN"] = Local("Ready", "READY TO SCAN"),
            ["F2 Scan   ·   F12 Checkout"] = Local("Hotkeys", "F2 Scan   ·   F12 Checkout"),
            ["Add item"] = Local("AddItem", "Add item"), ["Current sale"] = Local("CurrentSale", "Current sale"),
            ["Remove selected"] = Local("RemoveSelected", "Remove selected"), ["BARCODE"] = Local("Barcode", "BARCODE"),
            ["PRODUCT"] = Local("Product", "PRODUCT"), ["QTY"] = Local("Qty", "QTY"), ["PRICE"] = Local("Price", "PRICE"),
            ["TAX"] = Local("Tax", "TAX"), ["LINE TOTAL"] = Local("LineTotal", "LINE TOTAL"),
            ["Order summary"] = Local("OrderSummary", "Order summary"), ["Review totals before checkout"] = Local("ReviewTotals", "Review totals before checkout"),
            ["Subtotal"] = Local("Subtotal", "Subtotal"), ["Tax"] = Local("Tax", "Tax"), ["TOTAL DUE"] = Local("TotalDue", "TOTAL DUE"),
            ["PAYMENT METHOD"] = Local("PaymentMethod", "PAYMENT METHOD"), ["CASHIER SHORTCUTS"] = Local("Shortcuts", "CASHIER SHORTCUTS"),
            ["Clear sale"] = Local("ClearSale", "Clear sale"), ["Review and complete sale  →"] = Local("ReviewCheckout", "Review and complete sale  →"),
            ["Cash"] = Local("Cash", "Cash"), ["Credit"] = Local("Credit", "Credit"), ["K-Net"] = Local("KNet", "K-Net"), ["Visa"] = Local("Visa", "Visa"),
            ["F2  Focus barcode field\n/  Choose item unit\n* number + Enter  Set selected quantity\n+ number + Enter  Set selected unit price\nDelete  Remove selected line\nEsc  Clear current sale"] = Local("ShortcutDetails", "F2  Focus barcode field\n/  Choose item unit\n* number + Enter  Set selected quantity\n+ number + Enter  Set selected unit price\nDelete  Remove selected line\nEsc  Clear current sale")
        };
        LocalizeTree(Content as DependencyObject, map);
        foreach (var pair in _paymentButtons)
        {
            var caption = Local(pair.Key == "K-Net" ? "KNet" : pair.Key, pair.Key);
            pair.Value.Content = pair.Key.Equals(_selectedPaymentMethod, StringComparison.OrdinalIgnoreCase) ? $"✓  {caption}" : caption;
        }
        _grid.FlowDirection = FlowDirection;
        _scan.ToolTip = Local("ScanTip", "Scan barcode or enter item code or name, then press Enter (F2 to focus)");
        RefreshContextStatus();
    }

    private static string Local(string key, string fallback)
    {
        var value = T("POS." + key);
        return string.IsNullOrWhiteSpace(value) || value == "POS." + key ? fallback : value;
    }

    private void LocalizeTree(DependencyObject? node, Dictionary<string, string> map)
    {
        if (node == null) return;
        if (node is TextBlock tb)
        {
            if (tb.Tag is not string original) { original = tb.Text; tb.Tag = original; }
            if (map.TryGetValue(original, out var translated)) tb.Text = translated;
        }
        if (node is Button b && b != null && b.Content is string content)
        {
            if (b.Tag is not string original) { original = content; b.Tag = original; }
            if (map.TryGetValue(original, out var translated)) b.Content = translated;
        }
        if (node is DataGrid dg)
            foreach (var col in dg.Columns)
            {
                if (!_columnOriginalHeaders.TryGetValue(col, out var header)) { header = Convert.ToString(col.Header, CultureInfo.InvariantCulture) ?? string.Empty; _columnOriginalHeaders[col] = header; }
                if (map.TryGetValue(header, out var translated)) col.Header = translated;
            }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) LocalizeTree(VisualTreeHelper.GetChild(node, i), map);
    }

    private void RefreshContextStatus()
    {
        _statusUser.Text = $"{Local("StatusUser", "User")}: {CurrentUserDisplay}";
        _statusCompany.Text = $"{Local("StatusCompany", "Company")}: {CurrentCompanyDisplay} ({AppState.CurrentCompanyCode ?? "-"})";
        _statusDatabase.Text = $"{Local("StatusDatabase", "Database")}: {GetDatabaseDisplayName()}";
        _statusClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    }

    private static string GetDatabaseDisplayName()
    {
        try
        {
            var cs = DatabaseRuntime.GetConnectionString();
            if (string.IsNullOrWhiteSpace(cs)) return "-";
            var builder = new DbConnectionStringBuilder { ConnectionString = cs };
            foreach (var key in new[] { "Data Source", "DataSource", "Filename", "Host", "Server", "Initial Catalog", "Database" })
                if (builder.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-";
            return DatabaseRuntime.GetProvider();
        }
        catch { return DatabaseRuntime.GetProvider(); }
    }

    private void ChangeSelectedQty(int delta)
    {
        if (_grid.SelectedItem is not PosLine line) return;
        var next = line.Qty + delta;
        if (next <= 0) { _cart.Remove(line); }
        else line.Qty = next;
        UpdateTotals();
    }

    private void ClearSale()
    {
        if (_cart.Count == 0) return;
        if (MessageBox.Show(this, "Clear all items from this sale?", "Clear sale", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        { _cart.Clear(); UpdateTotals(); SetStatus("Sale cleared."); }
        _scan.Focus();
    }

    private void SetStatus(string message, bool error = false)
    {
        var localized = message switch
        {
            "Scan a barcode or enter an item code or name to begin." => Local("StatusScanPrompt", message),
            "Select a material line first." => Local("StatusSelectLine", message),
            "No units are configured for this material." => Local("StatusNoUnits", message),
            "Unable to load units for the selected material. See application log." => Local("StatusUnitError", message),
            "Add at least one item before continuing." => Local("StatusEmptySale", message),
            _ => message
        };
        _status.Text = localized;
        _status.Foreground = error ? Brushes.Firebrick : Muted;
    }

    private void Checkout()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_cart.Count == 0) { SetStatus("Add at least one item before continuing.", true); _scan.Focus(); return; }
        var invalid = _cart.FirstOrDefault(x => x.Qty <= 0 || x.Price < 0 || x.TaxRate < 0);
        if (invalid is not null)
        {
            SetStatus($"Check quantity, price, or tax for '{invalid.Name}'. Quantity must be greater than zero.", true);
            _grid.SelectedItem = invalid; _grid.ScrollIntoView(invalid); _grid.Focus(); return;
        }
        var lines = _cart.Select((x, i) => new InvLine { LineNo = i + 1, MaterialId = x.MaterialId, MaterialCode = x.Code, Description = x.Name, Unit = x.Unit, Qty = x.Qty, UnitPrice = x.Price, TaxRate = x.TaxRate }).ToList();
        var contextNote = $"POS payment method selected: {_selectedPaymentMethod}. POS sale created {DateTime.Now:yyyy-MM-dd HH:mm:ss}; user: {CurrentUserDisplay}; company: {CurrentCompanyDisplay} ({AppState.CurrentCompanyCode ?? ""}); database: {GetDatabaseDisplayName()}.";
        var checkout = new PosCheckoutWin(lines, _selectedPaymentMethod, contextNote)
        {
            Owner = Owner ?? this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (checkout.ShowDialog() == true)
        {
            _cart.Clear();
            UpdateTotals();
            SetStatus(Local("StatusSalePosted", "POS sale posted."));
            _scan.Focus();
        }
    }

    private sealed class PosLine
    {
        public long? MaterialId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Qty { get; set; }
        public decimal Price { get; set; }
        public decimal TaxRate { get; set; }
        public decimal Subtotal => Qty * Price;
        public decimal TaxAmount => Subtotal * TaxRate / 100m;
        public decimal Total => Subtotal + TaxAmount;
    }
}
