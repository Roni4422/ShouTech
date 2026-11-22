using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Evaluates posted supplier quotations linked to one posted RFQ and awards
/// the selected quotation by creating a linked draft purchase order.
/// </summary>
public sealed class SupplierQuotationCompareWin : BaseWin
{
    private readonly long _rfqId;
    private readonly DataGrid _offersGrid = new();
    private readonly DataGrid _linesGrid = new();
    private readonly TextBlock _status = new();
    private readonly Button _refresh = new();
    private readonly Button _award = new();
    private readonly Button _close = new();
    private IReadOnlyList<SupplierQuotationOffer> _offers = Array.Empty<SupplierQuotationOffer>();

    public SupplierQuotationCompareWin(long rfqId)
    {
        if (rfqId <= 0) throw new ArgumentOutOfRangeException(nameof(rfqId));
        _rfqId = rfqId;
        Width = 1180;
        Height = 720;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildUi();
        ApplyLocalizedText();
        Loaded += OnLoaded;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Purchasing.SupplierQuotationComparison.Title");
        _refresh.Content = T("Common.Refresh");
        _award.Content = T("Purchasing.SupplierQuotationComparison.Award");
        _close.Content = T("Common.Close");
        LocalizeColumns(_offersGrid);
        LocalizeColumns(_linesGrid);

    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Purchasing.OrderView))
        {
            MessageBox.Show(this, T("OrderDoc.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        Title = T("Purchasing.SupplierQuotationComparison.Title");
        var root = new DockPanel { Margin = new Thickness(12) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        F2Ui.StyleButton(_refresh, "Common.Refresh");
        F2Ui.StyleButton(_award, "Purchasing.SupplierQuotationComparison.Award");
        F2Ui.StyleButton(_close, "Common.Close");
        _refresh.Click += async (_, _) => await RefreshAsync();
        _award.Click += async (_, _) => await AwardAsync();
        _close.Click += (_, _) => Close();
        footer.Children.Add(_award);
        footer.Children.Add(_refresh);
        footer.Children.Add(_close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ConfigureGrid(_offersGrid);
        ConfigureGrid(_linesGrid);
        _offersGrid.AutoGeneratingColumn += (_, args) =>
        {
            if (args.PropertyName == nameof(SupplierQuotationOffer.Lines))
            {
                args.Cancel = true;
                return;
            }
            args.Column.Header = T("Purchasing.SupplierQuotationComparison.Columns." + args.PropertyName);
        };
        _linesGrid.AutoGeneratingColumn += (_, args) =>
            args.Column.Header = T("Purchasing.SupplierQuotationComparison.Columns." + args.PropertyName);
        _offersGrid.SelectionChanged += (_, _) =>
        {
            if (_offersGrid.SelectedItem is SupplierQuotationOffer selected)
                _linesGrid.ItemsSource = selected.Lines;
            else
                _linesGrid.ItemsSource = null;
        };
        Grid.SetRow(_offersGrid, 0);
        Grid.SetRow(_linesGrid, 1);
        content.Children.Add(_offersGrid);
        content.Children.Add(_linesGrid);
        root.Children.Add(content);
        Content = root;
    }

    private void LocalizeColumns(DataGrid grid)
    {
        foreach (var column in grid.Columns)
        {
            if (column is DataGridBoundColumn bound && bound.Binding is Binding binding &&
                !string.IsNullOrWhiteSpace(binding.Path?.Path))
                column.Header = T("Purchasing.SupplierQuotationComparison.Columns." + binding.Path.Path);
        }
    }

    private static void ConfigureGrid(DataGrid grid)
    {
        grid.AutoGenerateColumns = true;
        grid.IsReadOnly = true;
        grid.CanUserAddRows = false;
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.Margin = new Thickness(0, 4, 0, 8);
        grid.HeadersVisibility = DataGridHeadersVisibility.Column;
    }

    private async Task RefreshAsync()
    {
        if (AppHost.Orders is null)
        {
            _status.Text = T("Common.OperationUnavailable");
            return;
        }
        try
        {
            _offers = await AppHost.Orders.CompareSupplierQuotationsAsync(_rfqId);
            _offersGrid.ItemsSource = _offers;
            _offersGrid.SelectedItem = _offers.Count == 0 ? null : _offers[0];
            _status.Text = _offers.Count == 0
                ? T("Purchasing.SupplierQuotationComparison.NoOffers")
                : Tf("Purchasing.SupplierQuotationComparison.OfferCount", _offers.Count);
            _award.IsEnabled = _offers.Count > 0;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "SupplierQuotationCompareWin.RefreshAsync");
            _status.Text = T("Common.OperationUnavailable");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task AwardAsync()
    {
        if (_offersGrid.SelectedItem is not SupplierQuotationOffer selected || AppHost.Orders is null)
        {
            MessageBox.Show(this, T("Purchasing.SupplierQuotationComparison.SelectOffer"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!AppHost.Permissions.Has(PermCat.Purchasing.OrderConvert))
        {
            MessageBox.Show(this, T("OrderDoc.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(this,
            Tf("Purchasing.SupplierQuotationComparison.ConfirmAward", selected.SupplierName, selected.DocNo),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var result = await AppHost.Orders.CreatePurchaseOrderFromSupplierQuotationAsync(
                selected.DocId, DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
            MessageBox.Show(this,
                Tf("Purchasing.SupplierQuotationComparison.AwardCreated", result.PurchaseOrderNo),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "SupplierQuotationCompareWin.AwardAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
