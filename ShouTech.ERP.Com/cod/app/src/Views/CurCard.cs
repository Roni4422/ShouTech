using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Currency card (ribbon chunk B): the currency master is persisted through
/// <see cref="ShouTech.Application.Services.MasterCardService"/>, and the rate
/// grid is the real dated history from currency_rates (newest first). Add Rate
/// saves a dated rate row; the company base currency rate is enforced as 1.
/// </summary>
public sealed class CurCard : BaseWin
{
    protected override string? ScheduleTintName => "Peridot";
    private readonly TextBox _txtCode = new();
    private readonly TextBox _txtNameAr = new();
    private readonly TextBox _txtNameEn = new();
    private readonly TextBox _txtSymbol = new();
    private readonly TextBox _txtFraction = new();
    private readonly TextBox _txtRate = new();
    private readonly TextBox _txtMinRate = new();
    private readonly TextBox _txtMaxRate = new();
    private readonly DatePicker _rateDate = new();
    private readonly CheckBox _chkActive = new() { VerticalAlignment = VerticalAlignment.Center, IsChecked = true };

    private readonly Button _btnLoad = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnAddRate = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<RateRow> _rates = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private Guid? _loadedId;

    public CurCard()
    {
        Width = 860;
        Height = 500;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.CurrencyView))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    /// <summary>Loads the currency with the given code, including its rate history.</summary>
    public async Task LoadByCodeAsync(string code)
    {
        try
        {
            var service = AppHost.MasterCards;
            if (service is null)
            {
                MessageBox.Show(this, T("MasterCards.ServiceUnavailable"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var currency = await service.GetCurrencyByCodeAsync(CurrentCompanyCode, code);
            if (currency is null)
            {
                MessageBox.Show(this, T("MasterCards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ShowCurrency(currency);
            await RefreshRatesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnLoad, "MasterCards.Load");
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");
        StyleButton(_btnAddRate, "MasterCards.AddRate");

        _btnLoad.Click += async (_, _) => await LoadByCodeAsync(_txtCode.Text.Trim());
        _btnNew.Click += (_, _) => ResetCard();
        _btnSave.Click += async (_, _) => await SaveAsync();
        _btnDelete.Click += async (_, _) => await DeleteAsync();
        _btnAddRate.Click += async (_, _) => await AddRateAsync();

        foreach (var b in new[] { _btnLoad, _btnNew, _btnSave, _btnDelete, _btnAddRate })
            toolbar.Children.Add(b);

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

        var gridRoot = new Grid();
        // Step 12 screen-fit: star-proportioned split (was a fixed
        // 330-DIP card column) so the card and the rates grid share the
        // 660-wide minimum proportionally; the card form scrolls (below).
        gridRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        gridRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        gridRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });

        var leftCard = new Border
        {
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        leftCard.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        leftCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        // Side card = a one-pair form grid: side labels, fields stretch to
        // the card width, edges aligned down the whole card (Phenix rule).
        var cardForm = CreateFormGrid(1);
        AddFormField(cardForm, 0, 0, "CurrencyCard.Code", _txtCode);
        AddFormField(cardForm, 1, 0, "CurrencyCard.Symbol", _txtSymbol);
        AddFormField(cardForm, 2, 0, "CurrencyCard.NameAr", _txtNameAr);
        AddFormField(cardForm, 3, 0, "CurrencyCard.NameEn", _txtNameEn);
        AddFormField(cardForm, 4, 0, "CurrencyCard.Fraction", _txtFraction);
        _chkActive.Content = T("MasterCards.Active");
        _chkActive.Tag = "MasterCards.Active";
        AddFormElement(cardForm, 5, 0, _chkActive);
        AddFormField(cardForm, 6, 0, "MasterCards.RateDate", _rateDate);
        AddFormField(cardForm, 7, 0, "CurrencyCard.Rate", _txtRate);
        AddFormField(cardForm, 8, 0, "CurrencyCard.MinRate", _txtMinRate);
        AddFormField(cardForm, 9, 0, "CurrencyCard.MaxRate", _txtMaxRate);

        leftCard.Child = new ScrollViewer { Content = cardForm, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(leftCard, 0);
        gridRoot.Children.Add(leftCard);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.CanUserDeleteRows = false;
        _grid.IsReadOnly = true;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");

        _grid.ItemsSource = _rates;
        _grid.Columns.Add(TextCol("MasterCards.RateDate", nameof(RateRow.DateText), 110));
        _grid.Columns.Add(TextCol("CurrencyCard.Rate", nameof(RateRow.RateText), 110));
        _grid.Columns.Add(TextCol("CurrencyCard.MinRate", nameof(RateRow.MinText), 110));
        _grid.Columns.Add(TextCol("CurrencyCard.MaxRate", nameof(RateRow.MaxText), 110));

        Grid.SetColumn(_grid, 2);
        gridRoot.Children.Add(_grid);

        root.Children.Add(gridRoot);
        Content = root;
    }

    private void ShowCurrency(Currency currency)
    {
        _loadedId = currency.Id;
        _txtCode.Text = currency.Code;
        _txtNameEn.Text = currency.NameEn;
        _txtNameAr.Text = currency.NameAr ?? string.Empty;
        _txtSymbol.Text = currency.Symbol ?? string.Empty;
        _txtFraction.Text = currency.FractionName ?? string.Empty;
        _chkActive.IsChecked = currency.IsActive;
    }

    private async Task RefreshRatesAsync()
    {
        var service = AppHost.MasterCards;
        _rates.Clear();
        if (service is null || string.IsNullOrWhiteSpace(_txtCode.Text))
            return;
        var rows = await service.GetRatesAsync(CurrentCompanyCode, _txtCode.Text.Trim());
        foreach (var row in rows)
            _rates.Add(RateRow.From(row));
        if (rows.Count > 0)
        {
            _txtRate.Text = rows[0].Rate.ToString("0.####", CultureInfo.InvariantCulture);
            _txtMinRate.Text = rows[0].MinRate?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
            _txtMaxRate.Text = rows[0].MaxRate?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Accounting.CurrencyManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            var currency = new Currency
            {
                Id = _loadedId ?? Guid.NewGuid(),
                Code = _txtCode.Text.Trim(),
                NameEn = _txtNameEn.Text.Trim(),
                NameAr = string.IsNullOrWhiteSpace(_txtNameAr.Text) ? null : _txtNameAr.Text.Trim(),
                Symbol = string.IsNullOrWhiteSpace(_txtSymbol.Text) ? null : _txtSymbol.Text.Trim(),
                FractionName = string.IsNullOrWhiteSpace(_txtFraction.Text) ? null : _txtFraction.Text.Trim(),
                IsActive = _chkActive.IsChecked == true
            };
            await service.SaveCurrencyAsync(CurrentCompanyCode, currency, CurrentUserId);
            _loadedId = currency.Id;
            _txtCode.Text = currency.Code;
            MessageBox.Show(this, T("CurrencyCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (!_loadedId.HasValue)
                return;
            if (!AppHost.Permissions.Has(PermCat.Accounting.CurrencyManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await service.DeleteCurrencyAsync(CurrentCompanyCode, _loadedId.Value, CurrentUserId);
            MessageBox.Show(this, T("MasterCards.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            ResetCard();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task AddRateAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Accounting.ExchangeRateManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (_loadedId is null)
                throw new InvalidOperationException(T("MasterCards.SaveCurrencyFirst"));
            var rate = new CurrencyRate
            {
                CurrencyCode = _txtCode.Text.Trim(),
                RateDate = _rateDate.SelectedDate.HasValue ? DateOnly.FromDateTime(_rateDate.SelectedDate.Value) : DateOnly.FromDateTime(DateTime.Today),
                Rate = ParseDecimal(_txtRate.Text, "CurrencyCard.Rate"),
                MinRate = ParseNullableDecimal(_txtMinRate.Text),
                MaxRate = ParseNullableDecimal(_txtMaxRate.Text)
            };
            string? baseCurrency = null;
            try { baseCurrency = AppCfgSvc.Current.Currency.ExchangeRates.BaseCurrency; }
            catch { /* configuration not loaded yet: the > 0 rule still applies */ }
            await service.AddRateAsync(CurrentCompanyCode, rate, CurrentUserId, baseCurrency);
            MessageBox.Show(this, T("MasterCards.RateSaved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshRatesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetCard()
    {
        _loadedId = null;
        _txtCode.Text = string.Empty;
        _txtNameAr.Text = string.Empty;
        _txtNameEn.Text = string.Empty;
        _txtSymbol.Text = string.Empty;
        _txtFraction.Text = string.Empty;
        _txtRate.Text = string.Empty;
        _txtMinRate.Text = string.Empty;
        _txtMaxRate.Text = string.Empty;
        _rateDate.SelectedDate = DateTime.Today;
        _chkActive.IsChecked = true;
        _rates.Clear();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CurrencyCard.Title");
        _chkActive.Content = T("MasterCards.Active");
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        RefreshLocalizedContent(Content as DependencyObject);
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        if (parent is CheckBox check && check.Tag is string cKey)
            check.Content = T(cKey);
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

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width),
            Header = T(key)
        };
        _headers.Add((column, key));
        return column;
    }

    private static decimal ParseDecimal(string text, string fieldKey)
    {
        if (decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException($"{T(fieldKey)}: invalid number.");
    }

    private static decimal? ParseNullableDecimal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }

    /// <summary>Read-only display row for one persisted currency_rates entry.</summary>
    private sealed class RateRow
    {
        public string DateText { get; init; } = string.Empty;
        public string RateText { get; init; } = string.Empty;
        public string MinText { get; init; } = string.Empty;
        public string MaxText { get; init; } = string.Empty;

        public static RateRow From(CurrencyRate rate) => new()
        {
            DateText = rate.RateDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            RateText = rate.Rate.ToString("0.####", CultureInfo.InvariantCulture),
            MinText = rate.MinRate?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
            MaxText = rate.MaxRate?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty
        };
    }
}
