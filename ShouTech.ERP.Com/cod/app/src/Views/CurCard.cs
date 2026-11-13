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

    private readonly TextBox _txtCode = new() { Width = 90, Height = 26, Text = "USD" };
    private readonly TextBox _txtNameAr = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtNameEn = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtSymbol = new() { Width = 70, Height = 26, Text = "$" };
    private readonly TextBox _txtFraction = new() { Width = 120, Height = 26 };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnAddRate = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<ShouTech.Domain.Entities.Currency> _currencies = new();

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
        Loaded += async (_, _) => await LoadCurrenciesAsync();
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
        _btnSave.Click += async (_, _) => await SaveCurrencyAsync();
        _btnAddRate.Visibility = Visibility.Collapsed;

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

        _grid.ItemsSource = _currencies;
        _grid.SelectionChanged += (_, _) => PopulateSelectedCurrency();
        _grid.Columns.Add(TextCol(nameof(ShouTech.Domain.Entities.Currency.Code), 80));
        _grid.Columns.Add(TextCol(nameof(ShouTech.Domain.Entities.Currency.NameEnglish), 180));
        _grid.Columns.Add(TextCol(nameof(ShouTech.Domain.Entities.Currency.Symbol), 70));

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
        _grid.SelectedItem = null;
    }

    private async Task LoadCurrenciesAsync()
    {
        try
        {
            var service = ShouTech.App.Services.AppHost.Currency;
            if (service is null)
                throw new InvalidOperationException(T("CurrencyCard.DatabaseUnavailable"));

            var currencies = await service.GetCurrenciesAsync(includeInactive: true);
            _currencies.Clear();
            foreach (var currency in currencies)
                _currencies.Add(currency);
        }
        catch (Exception ex)
        {
            ShouTech.App.Services.AppHost.Logging?.LogError("CurrencyCard", "Currency list load failed.", ex);
            MessageBox.Show(this, T("CurrencyCard.LoadFailed") + Environment.NewLine + ex.Message,
                T("Application.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PopulateSelectedCurrency()
    {
        if (_grid.SelectedItem is not ShouTech.Domain.Entities.Currency currency)
            return;

        _txtCode.Text = currency.Code;
        _txtNameEn.Text = currency.NameEnglish;
        _txtNameAr.Text = currency.NameArabic ?? string.Empty;
        _txtSymbol.Text = currency.Symbol ?? string.Empty;
        _txtFraction.Text = currency.FractionNameEnglish ?? string.Empty;
    }

    private async Task SaveCurrencyAsync()
    {
        try
        {
            var service = ShouTech.App.Services.AppHost.Currency;
            if (service is null)
                throw new InvalidOperationException(T("CurrencyCard.DatabaseUnavailable"));

            var currency = new ShouTech.Domain.Entities.Currency(
                _txtCode.Text.Trim(),
                _txtNameEn.Text.Trim(),
                string.IsNullOrWhiteSpace(_txtNameAr.Text) ? null : _txtNameAr.Text.Trim(),
                string.IsNullOrWhiteSpace(_txtSymbol.Text) ? null : _txtSymbol.Text.Trim(),
                string.IsNullOrWhiteSpace(_txtFraction.Text) ? null : _txtFraction.Text.Trim(),
                null,
                2,
                true);

            await service.SaveCurrencyAsync(currency);
            await LoadCurrenciesAsync();
            ShouTech.App.Services.AppHost.Logging?.LogOperation(
                "CurrencyCard",
                "CurrencySaved",
                $"CurrencyCode={currency.Code.Trim().ToUpperInvariant()}");

            MessageBox.Show(this, T("CurrencyCard.SaveSuccess"), T("Application.Success"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShouTech.App.Services.AppHost.Logging?.LogError(
                "CurrencyCard",
                $"Currency save failed | CurrencyCode={_txtCode.Text.Trim()}",
                ex);
            MessageBox.Show(this, T("CurrencyCard.SaveFailed") + Environment.NewLine + ex.Message,
                T("Application.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CurrencyCard.Title");
        if (_grid.Columns.Count >= 3)
        {
            _grid.Columns[0].Header = T("CurrencyCard.Code");
            _grid.Columns[1].Header = T("CurrencyCard.NameEn");
            _grid.Columns[2].Header = T("CurrencyCard.Symbol");
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

}
