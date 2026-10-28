using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ShouTech.App.Views;

/// <summary>
/// </summary>
public sealed class CurCard : BaseWin
{

    /// <summary>
    /// Prevents a false success message when this screen has no database persistence wired.
    /// This intentionally does not claim to save any values.
    /// </summary>
    private void ShowPersistenceUnavailable()
    {
        const string errorCode = "APP-2010";
        const string context = "This window has no database persistence implementation.";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var exception = new NotSupportedException(context);

        // Errors are routed to errors.log; the operational trace goes to log.log.
        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowPersistence",
            $"[{definition.Code}] {definition.Name} | Window={GetType().Name} | {context}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowPersistence",
            "PersistenceUnavailable",
            $"Code={definition.Code} | Window={GetType().Name}");

        MessageBox.Show(
            this,
            $"[{definition.Code}] {definition.Message}\n{context}\nلم يتم حفظ أي بيانات.",
            "العملية غير متاحة",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }


    private readonly TextBox _txtCode = new() { Width = 90, Height = 26, Text = "USD" };
    private readonly TextBox _txtNameAr = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtNameEn = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtSymbol = new() { Width = 70, Height = 26, Text = "$" };
    private readonly TextBox _txtFraction = new() { Width = 120, Height = 26 };
    private readonly TextBox _txtRate = new() { Width = 90, Height = 26, Text = "3.7500" };
    private readonly TextBox _txtMinRate = new() { Width = 90, Height = 26, Text = "3.7400" };
    private readonly TextBox _txtMaxRate = new() { Width = 90, Height = 26, Text = "3.7600" };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnAddRate = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<RateHistoryVm> _rates = new();

    public CurCard()
    {
        Width = 820;
        Height = 480;
        MinWidth = 720;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnAddRate, "Invoice.AddLine");

        _btnNew.Click += (_, _) => ResetCard();
        _btnSave.Click += (_, _) => ShowPersistenceUnavailable();
        _btnAddRate.Click += (_, _) =>
        {
            var r = new RateHistoryVm { Date = DateTime.Today, Rate = 3.75m };
            _rates.Insert(0, r);
            _grid.SelectedItem = r;
        };

        foreach (var b in new[] { _btnNew, _btnSave, _btnAddRate })
            toolbar.Children.Add(b);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // 2. Main Body: Split Left and Right
        var gridRoot = new Grid();
        gridRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        gridRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        gridRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftCard = new Border
        {
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        leftCard.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        leftCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var cardPanel = new StackPanel();
        var cr1 = new WrapPanel();
        cr1.Children.Add(Labeled("CurrencyCard.Code", _txtCode));
        cr1.Children.Add(Labeled("CurrencyCard.Symbol", _txtSymbol));
        cardPanel.Children.Add(cr1);

        cardPanel.Children.Add(Labeled("CurrencyCard.Name", _txtNameAr));
        cardPanel.Children.Add(Labeled("CurrencyCard.NameEn", _txtNameEn));
        cardPanel.Children.Add(Labeled("CurrencyCard.Fraction", _txtFraction));

        var cr2 = new WrapPanel();
        cr2.Children.Add(Labeled("CurrencyCard.Rate", _txtRate));
        cr2.Children.Add(Labeled("CurrencyCard.MinRate", _txtMinRate));
        cr2.Children.Add(Labeled("CurrencyCard.MaxRate", _txtMaxRate));
        cardPanel.Children.Add(cr2);

        leftCard.Child = cardPanel;
        Grid.SetColumn(leftCard, 0);
        gridRoot.Children.Add(leftCard);

        // Right DataGrid
        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");

        _grid.ItemsSource = _rates;
        _grid.Columns.Add(TextCol(nameof(RateHistoryVm.DateText), 100));
        _grid.Columns.Add(TextCol(nameof(RateHistoryVm.RateText), 90));
        _grid.Columns.Add(TextCol(nameof(RateHistoryVm.BuyRateText), 90));
        _grid.Columns.Add(TextCol(nameof(RateHistoryVm.SellRateText), 90));
        _grid.Columns.Add(TextCol(nameof(RateHistoryVm.Notes), 120));

        Grid.SetColumn(_grid, 2);
        gridRoot.Children.Add(_grid);

        root.Children.Add(gridRoot);
        Content = root;
    }

    private void ResetCard()
    {
        _txtCode.Text = "USD";
        _txtNameAr.Text = string.Empty;
        _txtNameEn.Text = string.Empty;
        _txtSymbol.Text = "$";
        _txtFraction.Text = string.Empty;
        _txtRate.Text = "3.7500";
        _txtMinRate.Text = "3.7400";
        _txtMaxRate.Text = "3.7600";
        _rates.Clear();
        _rates.Add(new RateHistoryVm { Date = DateTime.Today, Rate = 3.7500m, BuyRate = 3.7450m, SellRate = 3.7550m, Notes = "Daily Bulletin" });
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CurrencyCard.Title");
        if (_grid.Columns.Count >= 5)
        {
            _grid.Columns[0].Header = T("Invoice.Date");
            _grid.Columns[1].Header = T("CurrencyCard.Rate");
            _grid.Columns[2].Header = T("Invoice.Col.PurchasePrice");
            _grid.Columns[3].Header = T("Invoice.Col.SalePrice");
            _grid.Columns[4].Header = T("CostCenter.Notes");
        }
        RefreshLocalizedContent(Content as DependencyObject);
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
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 5) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 2), Opacity = 0.85 });
        if (control is TextBox textBox)
            textBox.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
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

    private static DataGridTextColumn TextCol(string path, double width)
        => new()
        {
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width
        };

    private sealed class RateHistoryVm : INotifyPropertyChanged
    {
        private DateTime _date = DateTime.Today;
        private decimal _rate, _buyRate, _sellRate;
        private string _notes = "";

        public DateTime Date { get => _date; set { _date = value; OnChanged(); OnChanged(nameof(DateText)); } }
        public string DateText { get => _date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); set { if (DateTime.TryParse(value, out var d)) Date = d; } }
        public decimal Rate { get => _rate; set { _rate = value; OnChanged(nameof(RateText)); } }
        public decimal BuyRate { get => _buyRate; set { _buyRate = value; OnChanged(nameof(BuyRateText)); } }
        public decimal SellRate { get => _sellRate; set { _sellRate = value; OnChanged(nameof(SellRateText)); } }
        public string Notes { get => _notes; set { _notes = value; OnChanged(); } }

        public string RateText { get => _rate.ToString("0.####", CultureInfo.InvariantCulture); set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Rate = v; } }
        public string BuyRateText { get => _buyRate == 0 ? "" : _buyRate.ToString("0.####", CultureInfo.InvariantCulture); set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) BuyRate = v; } }
        public string SellRateText { get => _sellRate == 0 ? "" : _sellRate.ToString("0.####", CultureInfo.InvariantCulture); set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) SellRate = v; } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
