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

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>Retail cashier workspace. Final invoice posting remains in the existing invoice workflow.</summary>
public sealed class PosWin : BaseWin
{
    protected override string? ScheduleTintName => "Peach";
    private static readonly Brush Ink = ShouTech.App.Services.VisuId.ResolveBrushResource("PosInkBrush", "#172B3A");
    private static readonly Brush Muted = ShouTech.App.Services.VisuId.ResolveBrushResource("PosMutedBrush", "#687985");
    private static readonly Brush Teal = ShouTech.App.Services.VisuId.ResolveBrushResource("PosTealBrush", "#087F8C");
    private static readonly Brush Canvas = ShouTech.App.Services.VisuId.ResolveBrushResource("PosCanvasBrush", "#F3F6F8");
    private static readonly Brush PosBorderBrush = ShouTech.App.Services.VisuId.ResolveBrushResource("PosBorderBrush", "#DCE5EA");
    private readonly TextBox _scan = new() { MinWidth = 180, FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(20), Height = 48, Padding = new Thickness(12, 7, 12, 7), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ObservableCollection<PosLine> _cart = new();
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow };
    private readonly TextBlock _subtotal = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(16), Foreground = Ink };
    private readonly TextBlock _tax = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(16), Foreground = Ink };
    private readonly TextBlock _total = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(30), FontWeight = FontWeights.Bold, Foreground = ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF") };
    private readonly TextBlock _count = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(13), Foreground = Muted };
    private readonly TextBlock _status = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(13), TextWrapping = TextWrapping.Wrap, Foreground = Muted };
    private readonly System.Collections.Generic.Dictionary<string, Button> _paymentButtons = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedPaymentMethod = "Cash";
    // Build POS 066: the open session this till sells against, the
    // terminal it runs on, the attached customer (walk-in when null),
    // the bill discount, and the touch product strip.
    private ShouTech.Domain.Entities.PosSession? _session;
    private string _terminal = "T1";
    private int? _activeWarehouseId;
    private string? _productSearchTerm;
    private long? _partyId;
    private string _partyCode = string.Empty;
    private string _partyName = string.Empty;
    private readonly TextBlock _sessionLabel = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), FontWeight = FontWeights.Bold, Foreground = Muted };
    private readonly TextBox _customerCode = new() { MinWidth = 110, Height = 34 };
    private readonly TextBlock _customerName = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _billDiscount = new() { Text = "0", Width = 90, Height = 32 };
    private readonly TextBlock _discountLabel = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(13), Foreground = Muted };
    private readonly WrapPanel _productStrip = new() { Orientation = Orientation.Horizontal };
    private readonly Border _productCard = new() { Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF"), BorderBrush = PosBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 14), Visibility = Visibility.Collapsed, MaxHeight = 190 };
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
        Width = 1360; Height = 820; MinWidth = 660; MinHeight = 360; // Step 12 screen-fit: was 1080x680 — larger than the 50% cap at EVERY profile (sweep worst offender). Touch targets keep the TouchControlHeight family; at the minimum the summary rail scrolls and the cart grid scrolls (see BuildUi).
        Background = Canvas;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildUi();
        PreviewKeyDown += HandleHotkeys;
        Loaded += async (_, _) => { RefreshContextStatus(); await InitSessionAsync(); _scan.Focus(); };
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
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: brand, clear instructions, and barcode-first workflow.
        var header = new Border { Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF"), BorderBrush = PosBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14) };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Step 12: brand sizes to content so the scan field keeps room at the minimum width
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

        var shortcut = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        FlowAlign.End(shortcut);
        FlowAlign.End(_sessionLabel);
        shortcut.Children.Add(_sessionLabel);
        shortcut.Children.Add(new TextBlock { Text = "READY TO SCAN", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = Teal, HorizontalAlignment = HorizontalAlignment.Right });
        shortcut.Children.Add(new TextBlock { Text = "F2 Scan   ·   F12 Checkout", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted, Margin = new Thickness(0, 5, 0, 0) });
        Grid.SetColumn(shortcut, 2); headerGrid.Children.Add(shortcut);
        header.Child = headerGrid; Grid.SetRow(header, 0); root.Children.Add(header);

        // Touch product strip (Build POS 066): product buttons built
        // from the materials' own data; shown for browsing and for
        // name-search results, hidden while scanning.
        _productCard.Child = new ScrollViewer { Content = _productStrip, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(_productCard, 1); root.Children.Add(_productCard);

        // Main work area: fast editable cart on the left, always-visible totals on the right.
        var body = new Grid();
        // Step 12: star split (was a fixed 320-DIP summary rail) — the
        // rail keeps its designed ~1/4 share at any width, MinWidth 250.
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
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
        _grid.Columns.Add(new DataGridTextColumn { Header = "BARCODE", Binding = new System.Windows.Data.Binding(nameof(PosLine.Code)), IsReadOnly = true, Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "PRODUCT", Binding = new System.Windows.Data.Binding(nameof(PosLine.Name)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "STOCK", Binding = new System.Windows.Data.Binding(nameof(PosLine.AvailableQty)) { StringFormat = "N3", TargetNullValue = "—" }, IsReadOnly = true, Width = 78 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "QTY", Binding = new System.Windows.Data.Binding(nameof(PosLine.Qty)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, StringFormat = "N3" }, Width = 76 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "PRICE", Binding = new System.Windows.Data.Binding(nameof(PosLine.Price)) { StringFormat = "N2" }, IsReadOnly = true, Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "DISC %", Binding = new System.Windows.Data.Binding(nameof(PosLine.DiscountPct)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, StringFormat = "N2" }, Width = 70 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "TAX", Binding = new System.Windows.Data.Binding(nameof(PosLine.TaxAmount)) { StringFormat = "N2" }, IsReadOnly = true, Width = 80 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "LINE TOTAL", Binding = new System.Windows.Data.Binding(nameof(PosLine.Total)) { StringFormat = "N2" }, IsReadOnly = true, Width = 100 });
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() => { ClampDiscountValues(); UpdateTotals(); }));
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
        // Bill discount (Build POS 066): an absolute amount flowing
        // into the document's stored discount_amount — the step-5
        // stored totals carry it through GL, ledger and credit guard.
        var discountRow = new DockPanel { Margin = new Thickness(0, 0, 0, 11) };
        _discountLabel.Text = "Bill discount";
        _discountLabel.VerticalAlignment = VerticalAlignment.Center;
        discountRow.Children.Add(_discountLabel);
        FlowAlign.End(_billDiscount);
        _billDiscount.LostFocus += (_, _) => { ClampDiscountValues(); UpdateTotals(); };
        discountRow.Children.Add(_billDiscount);
        totals.Children.Add(discountRow);
        // Customer (Build POS 066): walk-in by default; a code resolves
        // on LostFocus exactly like the invoice party pattern, and the
        // named party is what checkout's on-account tender and the
        // step-4 credit guard see.
        var customerTitle = new TextBlock { Text = "CUSTOMER", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = Teal, Margin = new Thickness(0, 4, 0, 6) };
        totals.Children.Add(customerTitle);
        _customerCode.ToolTip = "Customer code (blank = walk-in)";
        _customerCode.LostFocus += (_, _) => ResolveCustomer();
        totals.Children.Add(_customerCode);
        totals.Children.Add(_customerName);
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
        // Build POS 066 till actions: products, session, hold/recall, return.
        Button Secondary(string caption)
        {
            var b = MakeButton(caption, ShouTech.App.Services.VisuId.ResolveBrushResource("PosRemoveBackgroundBrush", "#EEF3F5"), Ink, double.NaN, 38);
            b.HorizontalAlignment = HorizontalAlignment.Stretch; b.Margin = new Thickness(0, 0, 0, 7);
            actions.Children.Add(b);
            return b;
        }
        var productsBtn = Secondary("Products");
        productsBtn.Click += async (_, _) => await ToggleProductsAsync();
        var sessionBtn = Secondary("Session");
        sessionBtn.Click += (_, _) => { var win = new PosSessionWin { Owner = this }; win.ShowDialog(); _ = InitSessionAsync(); };
        var holdBtn = Secondary("Hold bill");
        holdBtn.Click += async (_, _) => await HoldBillAsync();
        var recallBtn = Secondary("Recall held");
        recallBtn.Click += async (_, _) => await RecallHeldAsync();
        var returnBtn = Secondary("POS return");
        returnBtn.Click += (_, _) => OpenReturn();
        var clear = MakeButton("Clear sale", ShouTech.App.Services.VisuId.ResolveBrushResource("PosClearBackgroundBrush", "#FFF0F0"), ShouTech.App.Services.VisuId.ResolveBrushResource("PosClearForegroundBrush", "#A33232"), double.NaN, 42);
        clear.HorizontalAlignment = HorizontalAlignment.Stretch; clear.Margin = new Thickness(0, 0, 0, 9);
        clear.Click += (_, _) => ClearSale(); actions.Children.Add(clear);
        var checkout = MakeButton("Review and complete sale  →", Teal, ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"), double.NaN, 54);
        checkout.FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(17); checkout.FontWeight = FontWeights.Bold; checkout.HorizontalAlignment = HorizontalAlignment.Stretch;
        checkout.ToolTip = "F12 — review the selected payment method and complete this POS sale.";
        checkout.Click += (_, _) => Checkout(); actions.Children.Add(checkout);
        _status.Margin = new Thickness(0, 12, 0, 0); actions.Children.Add(_status);
        Grid.SetRow(actions, 3); summaryLayout.Children.Add(actions);
        summary.Child = new ScrollViewer { Content = summaryLayout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetColumn(summary, 1); body.Children.Add(summary); // Step 12: rail scrolls at small heights instead of clipping the checkout actions

        Grid.SetRow(body, 2); root.Children.Add(body);

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
        statusBar.Child = statusGrid; Grid.SetRow(statusBar, 3); root.Children.Add(statusBar);
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
        FlowAlign.End(value); row.Children.Add(value);
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
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.AddScannedItem");
            LogPosEvent($"Material lookup exception. Company='{AppState.CurrentCompanyCode ?? ""}', Query='{code}', Error='{ex.GetType().Name}: {ex.Message}'.");
            _scan.Clear(); _scan.Focus(); return;
        }
        if (material is null)
        {
            // Search-by-name fallback (Build POS 066): the scan box is
            // also the search box — a miss on exact resolution runs the
            // contains-search and offers the matches as product buttons.
            LogPosEvent($"Material lookup miss. Company='{AppState.CurrentCompanyCode ?? ""}', Query='{code}'.");
            _ = ShowSearchResultsAsync(code);
            _scan.Focus(); return;
        }
        // Merge by material + unit (Build POS 066): the sweep found
        // the old merge keyed on the TYPED STRING, so scanning by name
        // after scanning by code duplicated the line, and re-scanning
        // after a unit change incremented the old unit's line.
        var existing = _cart.FirstOrDefault(x => x.MaterialId == material.Value.Id
            && string.Equals(x.Unit, material.Value.Unit, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Qty += 1;
        else
        {
            // W7a: the till consults the ONE price resolver too
            // (walk-in sale: no party, so customer/tier levels fall
            // through to the price list / material card) — the card
            // price from ResolveMaterial is only the fallback.
            var resolution = TaxSvc.ResolveSalesPrice(null, material.Value.Id, code, 1, material.Value.Unit);
            existing = new PosLine { MaterialId = material.Value.Id, Code = code, Name = material.Value.Name, Unit = material.Value.Unit, Price = resolution is not null && resolution.Price > 0 ? resolution.Price : material.Value.Price, DiscountPct = resolution?.DiscountPct ?? 0m, TaxRate = material.Value.TaxRate > 0 ? material.Value.TaxRate : TaxSvc.GetDefaultRate(), Qty = 1 };
            _cart.Add(existing);
        }
        _scan.Clear(); UpdateTotals(); SetStatus($"Added {material.Value.Name} to the sale."); _scan.Focus();
        _ = RefreshLineStockAsync(existing, material.Value.Id, code);
    }

    private async System.Threading.Tasks.Task RefreshLineStockAsync(PosLine line, long? materialId, string lookup)
    {
        try
        {
            if (materialId is not long id) return;
            var service = AppHost.Pos;
            if (service is null) return;
            var products = await service.SearchProductsAsync(CurrentCompanyCode, lookup, _activeWarehouseId, 12);
            var product = products.FirstOrDefault(x => x.MaterialId == id);
            if (product is null) return;
            line.AvailableQty = product.SellableQty;
            _grid.Items.Refresh();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.RefreshLineStockAsync");
        }
    }

    private decimal BillDiscountValue()
        => decimal.TryParse(_billDiscount.Text, System.Globalization.NumberStyles.Number, CultureInfo.CurrentCulture, out var value) && value > 0 ? value : 0m;

    private void UpdateTotals()
    {
        var currency = UsrPref.Load().CurrencyCode;
        var subtotal = _cart.Sum(x => x.Subtotal);
        var tax = _cart.Sum(x => x.TaxAmount);
        var total = subtotal - BillDiscountValue() + tax;
        if (total < 0) total = 0;
        _subtotal.Text = $"{subtotal:N2} {currency}";
        _tax.Text = $"{tax:N2} {currency}";
        _total.Text = $"{total:N2} {currency}";
        _count.Text = $"{_cart.Count} line(s)  ·  {_cart.Sum(x => x.Qty):N3} item(s)";
        if (_cart.Count == 0) SetStatus("Scan a barcode or enter an item code or name to begin.");
    }

    // ---- Build POS 066: session, customer, products, hold/recall, return ----

    private async System.Threading.Tasks.Task InitSessionAsync()
    {
        try
        {
            var service = AppHost.Pos;
            if (service is null) { _sessionLabel.Text = Local("NoSession", "NO SESSION"); return; }
            var settings = await service.GetSettingsAsync(CurrentCompanyCode);
            _terminal = settings.DefaultTerminalCode ?? "T1";
            _session = await service.GetOpenSessionAsync(CurrentCompanyCode, _terminal);
            var terminalWarehouseId = await service.GetTerminalWarehouseIdAsync(CurrentCompanyCode, _terminal);
            _activeWarehouseId = _session?.WarehouseId ?? terminalWarehouseId ?? settings.DefaultWarehouseId;
            _sessionLabel.Text = _session is null
                ? $"{Local("NoSession", "NO SESSION")} · {_terminal}"
                : $"{_session.SessionNo} · {_terminal} · {_session.Cashier}";
            if (_session is null && !settings.AllowSessionless)
                SetStatus(Local("StatusOpenSessionFirst", "Open a POS session before selling (Session button)."), true);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.InitSessionAsync");
        }
    }

    /// <summary>The document kind this terminal's sales post as
    /// (P4 binding): SAL when the terminal definition binds the
    /// sales-invoice type, otherwise the POS sale kind.</summary>
    private InvDocKind TerminalSaleKind()
    {
        try
        {
            var terminal = PosTermRepo.FindByCode(_terminal);
            return string.Equals(terminal?.SalesDocKind, "SAL", StringComparison.OrdinalIgnoreCase)
                ? InvDocKind.SalesInvoice
                : InvDocKind.PosSale;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.TerminalSaleKind");
            return InvDocKind.PosSale;
        }
    }

    private void ResolveCustomer()
    {
        var code = _customerCode.Text.Trim();
        if (code.Length == 0)
        {
            _partyId = null; _partyCode = string.Empty; _partyName = string.Empty;
            _customerName.Text = Local("WalkIn", "Walk-in");
            return;
        }
        (long? Id, string Name)? found;
        try
        {
            found = TaxSvc.ResolveParty(code, customer: true);
        }
        catch (InvalidOperationException ex)
        {
            // W1: blacklisted customer — clear rejection at POS too.
            _partyId = null; _partyCode = string.Empty; _partyName = string.Empty;
            _customerName.Text = ex.Message;
            return;
        }
        if (found is null)
        {
            _partyId = null; _partyCode = code; _partyName = string.Empty;
            _customerName.Text = Local("CustomerNotFoundShort", "Customer not found");
            return;
        }
        // Terminal customer-group scope (P4): when the terminal
        // definition scopes a customer group, a typed customer
        // outside it is not accepted at this till (the terminal's
        // own default customer is exempt).
        try
        {
            var terminal = PosTermRepo.FindByCode(_terminal);
            if (!string.IsNullOrWhiteSpace(terminal?.CustomerGroupCode)
                && !string.Equals(code, terminal.DefaultCustomerCode, StringComparison.OrdinalIgnoreCase))
            {
                var group = PosTermRepo.PartyGroupCodeOf(code);
                if (!string.Equals(group, terminal.CustomerGroupCode, StringComparison.OrdinalIgnoreCase))
                {
                    _partyId = null; _partyCode = string.Empty; _partyName = string.Empty;
                    _customerName.Text = Local("CustomerOutsideTerminalGroup", "This customer is outside the terminal's customer group.");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.ResolveCustomerGroupScope");
            _partyId = null; _partyCode = string.Empty; _partyName = string.Empty;
            _customerName.Text = Local("CustomerOutsideTerminalGroup", "This customer is outside the terminal's customer group.");
            return;
        }
        _partyId = found.Value.Id; _partyCode = code; _partyName = found.Value.Name;
        _customerName.Text = found.Value.Name;
    }

    /// <summary>Edit-time hygiene only (W9): out-of-range discount
    /// percentages are clamped while typing. AUTHORITY is no longer
    /// decided here — the numeric cashier limit + supervisor override
    /// gate runs at checkout (PosDiscountMath / PosDiscountGuard,
    /// migration 104), so a cashier may enter a discount and have a
    /// supervisor approve it, instead of the value being silently
    /// zeroed (the pre-W9 behaviour).</summary>
    private void ClampDiscountValues()
    {
        foreach (var line in _cart)
            if (line.DiscountPct < 0 || line.DiscountPct > 100) line.DiscountPct = 0;
        _grid.Items.Refresh();
    }

    /// <summary>The W9 discount-authority gate, run at checkout:
    /// measures the cart's discount (PosDiscountMath), resolves the
    /// cashier's ceiling (0 without POS.DISCOUNT.OVERRIDE, else the
    /// configured numeric limit, else uncapped), and — when the
    /// discount exceeds the ceiling — asks a supervisor to approve.
    /// Returns the approval to audit after posting, or null when no
    /// approval is needed. Blocked = true when the cashier must not
    /// proceed (over the ceiling, no supervisor approval).</summary>
    private async System.Threading.Tasks.Task<(PosDiscountApproval? Approval, bool Blocked)> DiscountGateAsync()
    {
        var measurement = ShouTech.Domain.Entities.PosDiscountMath.Measure(
            _cart.Select(x => (x.Qty, x.Price, x.DiscountPct)), BillDiscountValue());
        if (!measurement.HasDiscount) return (null, false);
        decimal? limit = null;
        var service = AppHost.Pos;
        if (service is not null)
            limit = await service.GetDiscountLimitPctAsync(CurrentCompanyCode, CurrentUserId);
        var ceiling = ShouTech.Domain.Entities.PosDiscountMath.Ceiling(
            AppHost.Permissions.Has(PermCat.Pos.DiscountOverride), limit);
        if (!ShouTech.Domain.Entities.PosDiscountMath.RequiresOverride(measurement, ceiling)) return (null, false);
        var supervisor = AskSupervisorOverride(measurement, ceiling);
        if (supervisor is null)
        {
            SetStatus(Local("StatusDiscountLimitBlocked", "The discount exceeds your limit and no supervisor approved it."), true);
            return (null, true);
        }
        return (new PosDiscountApproval(CurrentUserId, supervisor, ceiling, measurement), false);
    }

    /// <summary>The supervisor approval prompt: the supervisor's own
    /// username + password, verified by PosDiscountGuard (their
    /// credentials are checked, never stored; the till session does
    /// not change hands). Returns the supervisor's username.</summary>
    private string? AskSupervisorOverride(ShouTech.Domain.Entities.PosDiscountMath.Measurement measurement, decimal ceiling)
    {
        var dlg = new Window
        {
            Title = Local("SupervisorTitle", "Supervisor approval"), Width = 460, Height = 300,
            MinWidth = 420, MinHeight = 280, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FlowDirection = FlowDirection,
            Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF")
        };
        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(new TextBlock
        {
            Text = $"{Local("SupervisorExplain", "This discount exceeds the cashier limit.")}\n{Local("Discount", "Discount")}: {measurement.BindingPct:N2}% · {Local("Limit", "Limit")}: {ceiling:N2}%",
            TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, 0, 0, 12)
        });
        var userBox = new TextBox { Height = 34, Margin = new Thickness(0, 0, 0, 8) };
        var passBox = new PasswordBox { Height = 34, Margin = new Thickness(0, 0, 0, 12) };
        layout.Children.Add(new TextBlock { Text = Local("SupervisorUser", "Supervisor user"), Foreground = Muted });
        layout.Children.Add(userBox);
        layout.Children.Add(new TextBlock { Text = Local("SupervisorPassword", "Supervisor password"), Foreground = Muted });
        layout.Children.Add(passBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(buttons);
        var ok = MakeButton(Local("Approve", "Approve"), Teal, ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"), 110, 38);
        var cancel = MakeButton(Local("Cancel", "Cancel"), ShouTech.App.Services.VisuId.ResolveBrushResource("PosRemoveBackgroundBrush", "#EEF3F5"), Ink, 90, 38);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        string? approvedBy = null;
        cancel.Click += (_, _) => dlg.Close();
        ok.Click += (_, _) =>
        {
            try
            {
                if (PosDiscountGuard.VerifySupervisor(userBox.Text.Trim(), passBox.Password))
                {
                    approvedBy = userBox.Text.Trim();
                    dlg.DialogResult = true;
                    dlg.Close();
                }
                else
                    SetStatus(Local("StatusSupervisorDenied", "Supervisor approval failed: the user is not authorized or the password is wrong."), true);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.AskSupervisorOverride");
                SetStatus(ex.Message, true);
            }
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        layout.Children.Add(buttons);
        dlg.Content = layout;
        dlg.ShowDialog();
        return approvedBy;
    }

    private async System.Threading.Tasks.Task ToggleProductsAsync()
    {
        if (_productCard.Visibility == Visibility.Visible)
        {
            _productCard.Visibility = Visibility.Collapsed;
            return;
        }
        await ShowProductsAsync(null);
    }

    private async System.Threading.Tasks.Task ShowSearchResultsAsync(string term)
    {
        await ShowProductsAsync(term);
        SetStatus(Local("StatusPickProduct", "Pick the product from the results, or scan its barcode."));
    }

    private async System.Threading.Tasks.Task ShowProductsAsync(string? term)
    {
        _productSearchTerm = term;
        try
        {
            var service = AppHost.Pos;
            if (service is null) return;
            var products = await service.SearchProductsAsync(CurrentCompanyCode, term, _activeWarehouseId, 48);
            _productStrip.Children.Clear();
            foreach (var product in products)
            {
                var content = new StackPanel { Width = 132 };
                if (!string.IsNullOrWhiteSpace(product.ImagePath) && System.IO.File.Exists(product.ImagePath))
                {
                    try
                    {
                        var image = new System.Windows.Media.Imaging.BitmapImage(new Uri(product.ImagePath));
                        content.Children.Add(new Image { Source = image, Height = 44, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 4) });
                    }
                    catch (Exception exImg)
                    {
                        AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exImg, "PosWin.ShowProductsAsync.Image");
                    }
                }
                content.Children.Add(new TextBlock { Text = product.Name, FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text = $"{product.Price:N2}", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12), Foreground = Muted });
                content.Children.Add(new TextBlock { Text = $"{Local("StockOnHand", "On hand")}: {product.OnHandQty:N3}  |  {Local("StockReserved", "Reserved")}: {product.ReservedQty:N3}", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(10), Foreground = Muted, TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text = $"{Local("StockAvailable", "Available")}: {product.SellableQty:N3}", FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(11), FontWeight = FontWeights.Bold, Foreground = product.SellableQty > 0 ? Teal : Brushes.IndianRed });
                var button = MakeButton("", ShouTech.App.Services.VisuId.ResolveBrushResource("PosButtonBackgroundBrush", "#F4F7F8"), Ink, 140, 96);
                button.Content = content;
                button.Margin = new Thickness(0, 0, 8, 8);
                var captured = product;
                button.Click += (_, _) => AddProduct(captured);
                _productStrip.Children.Add(button);
            }
            _productCard.Visibility = products.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.ShowProductsAsync");
        }
    }

    private void AddProduct(ShouTech.Domain.Entities.PosProduct product)
    {
        var existing = _cart.FirstOrDefault(x => x.MaterialId == product.MaterialId
            && string.Equals(x.Unit, product.Unit, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { existing.Qty += 1; existing.AvailableQty = product.SellableQty; }
        else
        {
            // W7a: same resolver consultation as the scan path.
            var resolution = TaxSvc.ResolveSalesPrice(null, product.MaterialId, product.Code, 1, product.Unit);
            _cart.Add(new PosLine { MaterialId = product.MaterialId, Code = product.Code, Name = product.Name, Unit = product.Unit, Price = resolution is not null && resolution.Price > 0 ? resolution.Price : product.Price, DiscountPct = resolution?.DiscountPct ?? 0m, TaxRate = product.TaxRate > 0 ? product.TaxRate : TaxSvc.GetDefaultRate(), Qty = 1, AvailableQty = product.SellableQty });
        }
        UpdateTotals();
        SetStatus($"Added {product.Name} to the sale.");
        _scan.Focus();
    }

    private async System.Threading.Tasks.Task HoldBillAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Pos.HoldManage)) { SetStatus(Local("StatusHoldDenied", "Holding bills needs the POS hold permission."), true); return; }
            if (_cart.Count == 0) { SetStatus("Add at least one item before continuing.", true); return; }
            var service = AppHost.Pos ?? throw new InvalidOperationException(Local("ServiceUnavailable", "POS service unavailable."));
            var bill = new ShouTech.Domain.Entities.PosHeldBill
            {
                SessionId = _session?.Id,
                Cashier = CurrentUserDisplay,
                PartyId = _partyId, PartyCode = _partyCode, PartyName = _partyName,
                CurrencyCode = UsrPref.Load().CurrencyCode,
                BillDiscount = BillDiscountValue(),
                Lines = _cart.Select(x => new ShouTech.Domain.Entities.PosHeldLine
                {
                    MaterialId = x.MaterialId, MaterialCode = x.Code, Description = x.Name, Unit = x.Unit,
                    Qty = x.Qty, UnitPrice = x.Price, DiscountPct = x.DiscountPct, TaxRate = x.TaxRate
                }).ToList()
            };
            var held = await service.HoldBillAsync(CurrentCompanyCode, bill, CurrentUserId);
            _cart.Clear();
            _billDiscount.Text = "0";
            UpdateTotals();
            SetStatus($"{Local("StatusBillHeld", "Bill held")}: {held.HeldNo}");
            _scan.Focus();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.HoldBillAsync");
            SetStatus(ex.Message, true);
        }
    }

    private async System.Threading.Tasks.Task RecallHeldAsync()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Pos.HoldManage)) { SetStatus(Local("StatusHoldDenied", "Holding bills needs the POS hold permission."), true); return; }
            var service = AppHost.Pos ?? throw new InvalidOperationException(Local("ServiceUnavailable", "POS service unavailable."));
            var held = await service.GetHeldBillsAsync(CurrentCompanyCode);
            if (held.Count == 0) { SetStatus(Local("StatusNoHeldBills", "No held bills.")); return; }
            var dlg = new Window { Title = Local("RecallTitle", "Recall held bill"), Width = 640, Height = 400, MinWidth = 480, MinHeight = 300, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, FlowDirection = FlowDirection, Background = ShouTech.App.Services.VisuId.ResolveBrushResource("SurfaceBrush", "#FFFFFF") };
            var layout = new DockPanel { Margin = new Thickness(12) };
            var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, ItemsSource = held, Margin = new Thickness(0, 0, 0, 10) };
            grid.Columns.Add(new DataGridTextColumn { Header = Local("HeldNo", "Held no"), Binding = new System.Windows.Data.Binding(nameof(ShouTech.Domain.Entities.PosHeldBill.HeldNo)), Width = 120 });
            grid.Columns.Add(new DataGridTextColumn { Header = Local("HeldCustomer", "Customer"), Binding = new System.Windows.Data.Binding(nameof(ShouTech.Domain.Entities.PosHeldBill.PartyName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = Local("HeldTotal", "Total"), Binding = new System.Windows.Data.Binding(nameof(ShouTech.Domain.Entities.PosHeldBill.GrandTotal)) { StringFormat = "N2" }, Width = 110 });
            layout.Children.Add(grid);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            FlowAlign.End(buttons);
            DockPanel.SetDock(buttons, Dock.Bottom);
            var ok = MakeButton(Local("Recall", "Recall"), Teal, ShouTech.App.Services.VisuId.ResolveBrushResource("TextOnDarkBrush", "#FFFFFF"), 100, 38);
            var cancel = MakeButton(Local("Cancel", "Cancel"), ShouTech.App.Services.VisuId.ResolveBrushResource("PosRemoveBackgroundBrush", "#EEF3F5"), Ink, 90, 38);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += (_, _) => dlg.Close();
            ok.Click += async (_, _) =>
            {
                if (grid.SelectedItem is not ShouTech.Domain.Entities.PosHeldBill chosen) return;
                try
                {
                    var recalled = await service.RecallHeldBillAsync(CurrentCompanyCode, chosen.Id, CurrentUserId);
                    _cart.Clear();
                    foreach (var line in recalled.Lines)
                        _cart.Add(new PosLine { MaterialId = line.MaterialId, Code = line.MaterialCode ?? string.Empty, Name = line.Description, Unit = line.Unit ?? string.Empty, Qty = line.Qty, Price = line.UnitPrice, DiscountPct = line.DiscountPct, TaxRate = line.TaxRate });
                    _billDiscount.Text = recalled.BillDiscount.ToString(CultureInfo.InvariantCulture);
                    _partyId = recalled.PartyId; _partyCode = recalled.PartyCode ?? string.Empty; _partyName = recalled.PartyName ?? string.Empty;
                    _customerCode.Text = _partyCode; _customerName.Text = _partyName;
                    UpdateTotals();
                    SetStatus($"{Local("StatusBillRecalled", "Bill recalled")}: {recalled.HeldNo}");
                    dlg.DialogResult = true; dlg.Close();
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.RecallHeldAsync");
                    MessageBox.Show(dlg, ex.Message, dlg.Title, MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            layout.Children.Add(buttons);
            dlg.Content = layout;
            dlg.ShowDialog();
            _scan.Focus();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.RecallHeldAsync");
            SetStatus(ex.Message, true);
        }
    }

    private void OpenReturn()
    {
        var win = new PosReturnWin(_session?.Id) { Owner = this };
        win.ShowDialog();
        _scan.Focus();
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
        else if (command == '+')
        {
            // Price edits are an override (Build POS 066): the till's
            // price comes from the material card unless the cashier
            // holds POS.PRICE.OVERRIDE.
            if (!AppHost.Permissions.Has(PermCat.Pos.PriceOverride))
            { SetStatus(Local("StatusPriceDenied", "Price changes need the POS price override permission."), true); _scan.Focus(); return; }
            line.Price = value;
        }
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
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            FlowAlign.End(buttons);
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
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.OpenUnitChooser");
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
        catch (Exception exL466)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL466, "PosWin.LogPosEvent");
        }
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
            ["PRODUCT"] = Local("Product", "PRODUCT"), ["STOCK"] = Local("Stock", "STOCK"), ["QTY"] = Local("Qty", "QTY"), ["PRICE"] = Local("Price", "PRICE"),
            ["TAX"] = Local("Tax", "TAX"), ["LINE TOTAL"] = Local("LineTotal", "LINE TOTAL"),
            ["Order summary"] = Local("OrderSummary", "Order summary"), ["Review totals before checkout"] = Local("ReviewTotals", "Review totals before checkout"),
            ["Subtotal"] = Local("Subtotal", "Subtotal"), ["Tax"] = Local("Tax", "Tax"), ["TOTAL DUE"] = Local("TotalDue", "TOTAL DUE"),
            ["PAYMENT METHOD"] = Local("PaymentMethod", "PAYMENT METHOD"), ["CASHIER SHORTCUTS"] = Local("Shortcuts", "CASHIER SHORTCUTS"),
            ["Clear sale"] = Local("ClearSale", "Clear sale"), ["Review and complete sale  →"] = Local("ReviewCheckout", "Review and complete sale  →"),
            ["Cash"] = Local("Cash", "Cash"), ["Credit"] = Local("Credit", "Credit"), ["K-Net"] = Local("KNet", "K-Net"), ["Visa"] = Local("Visa", "Visa"),
            ["DISC %"] = Local("DiscPct", "DISC %"), ["Bill discount"] = Local("BillDiscount", "Bill discount"), ["CUSTOMER"] = Local("Customer", "CUSTOMER"),
            ["Products"] = Local("Products", "Products"), ["Session"] = Local("Session", "Session"), ["Hold bill"] = Local("HoldBill", "Hold bill"),
            ["Recall held"] = Local("RecallHeld", "Recall held"), ["POS return"] = Local("ReturnButton", "POS return"),
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
        catch (Exception exL548) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL548, "PosWin.GetDatabaseDisplayName"); return DatabaseRuntime.GetProvider(); }
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

    private async void Checkout()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_cart.Count == 0) { SetStatus("Add at least one item before continuing.", true); _scan.Focus(); return; }
        var invalid = _cart.FirstOrDefault(x => x.Qty <= 0 || x.Price < 0 || x.TaxRate < 0 || x.DiscountPct < 0 || x.DiscountPct > 100);
        if (invalid is not null)
        {
            SetStatus($"Check quantity, price, or tax for '{invalid.Name}'. Quantity must be greater than zero.", true);
            _grid.SelectedItem = invalid; _grid.ScrollIntoView(invalid); _grid.Focus(); return;
        }
        ClampDiscountValues();
        ResolveCustomer();
        if (_customerCode.Text.Trim().Length > 0 && _partyId is null)
        { SetStatus(Local("CustomerNotFoundShort", "Customer not found"), true); return; }

        // Discount-authority gate (W9, migration 104): the numeric
        // cashier limit binds here; above it a supervisor must have
        // approved, and the approval is audited by PosCheckoutWin
        // once the sale it approved has posted.
        PosDiscountApproval? discountApproval = null;
        try
        {
            var gate = await DiscountGateAsync();
            if (gate.Blocked) { _scan.Focus(); return; }
            discountApproval = gate.Approval;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosWin.Checkout.DiscountGate");
            SetStatus(ex.Message, true);
            return;
        }

        // Session gate (Build POS 066): the sale belongs to the open
        // session; without one, only an explicitly enabled session-less
        // mode + the bypass permission lets it through (PosService
        // throws POS-1001 / POS-1010 otherwise, and InvRepo re-checks
        // inside the posting transaction).
        Guid? sessionId = _session?.Id;
        string? sessionNo = _session?.SessionNo;
        if (_session is null)
        {
            try
            {
                var service = AppHost.Pos ?? throw new InvalidOperationException(Local("ServiceUnavailable", "POS service unavailable."));
                var allowed = await service.RequireSessionForSaleAsync(CurrentCompanyCode, _terminal,
                    AppHost.Permissions.Has(PermCat.Pos.SessionBypass));
                sessionId = allowed?.Id; sessionNo = allowed?.SessionNo;
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.PosNoOpenSession, ex, "PosWin.Checkout.SessionGate");
                SetStatus(ex.Message, true);
                return;
            }
        }

        if (_activeWarehouseId is not int warehouseId)
        {
            SetStatus(Local("WarehouseRequired", "Configure a warehouse for this POS terminal before selling."), true);
            return;
        }
        var saleKind = TerminalSaleKind();
        var doc = new InvDoc
        {
            Kind = saleKind,
            DocNo = InvRepo.NextDocNo(saleKind),
            DocDate = DateTime.Today,
            PartyId = _partyId,
            PartyCode = _partyCode,
            PartyName = _partyName,
            WarehouseId = warehouseId,
            CurrencyCode = _session?.CurrencyCode ?? UsrPref.Load().CurrencyCode,
            DefaultTaxRate = TaxSvc.GetDefaultRate(),
            LinesDiscountAmount = BillDiscountValue(),
            Notes = $"POS sale {DateTime.Now:yyyy-MM-dd HH:mm:ss}; user: {CurrentUserDisplay}; company: {CurrentCompanyDisplay} ({AppState.CurrentCompanyCode ?? ""}); session: {sessionNo ?? "session-less"}; terminal: {_terminal}.",
            Lines = _cart.Select((x, i) => new InvLine
            {
                LineNo = i + 1, MaterialId = x.MaterialId, MaterialCode = x.Code, Description = x.Name,
                Unit = x.Unit, Qty = x.Qty, UnitPrice = x.Price, DiscountPct = x.DiscountPct,
                TaxRate = x.TaxRate, WarehouseId = warehouseId
            }).ToList()
        };
        var checkout = new PosCheckoutWin(doc, sessionId, sessionNo, _selectedPaymentMethod, discountApproval)
        {
            Owner = Owner ?? this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (checkout.ShowDialog() == true)
        {
            _cart.Clear();
            _billDiscount.Text = "0";
            UpdateTotals();
            SetStatus(Local("StatusSalePosted", "POS sale posted."));
            if (_productCard.Visibility == Visibility.Visible)
                await ShowProductsAsync(_productSearchTerm);
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
        public decimal DiscountPct { get; set; }
        public decimal TaxRate { get; set; }
        public decimal? AvailableQty { get; set; }
        public decimal Subtotal => Qty * Price * (1m - DiscountPct / 100m);
        public decimal TaxAmount => Subtotal * TaxRate / 100m;
        public decimal Total => Subtotal + TaxAmount;
    }
}
