using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Creates and posts partial delivery notes from posted sales orders.
/// The repository revalidates the source-order quantity at posting time.
/// </summary>
public sealed class SalesDeliveryWin : BaseWin
{
    private readonly TextBox _txtOrderNo = new();
    private readonly TextBox _txtDeliveryNo = new() { IsReadOnly = true };
    private readonly DatePicker _dtDelivery = new() { SelectedDate = DateTime.Today };
    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnCreate = new();
    private readonly Button _btnPost = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _lblStatus = new();
    private readonly ObservableCollection<DeliveryLineVm> _lines = new();
    private readonly List<(TextBlock Label, string Key)> _headerLabels = new();

    private Guid _companyId;
    private long? _orderId;
    private long? _deliveryId;
    private string _deliveryStatus = CommercialDocumentStatus.Draft;
    private bool _busy;

    public SalesDeliveryWin()
    {
        Title = T("SalesDelivery.Title");
        Width = 1040;
        Height = 650;
        MinWidth = 760;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        Loaded += OnLoaded;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("SalesDelivery.Title");
        _btnNew.Content = T("Common.New");
        _btnLoad.Content = T("OrderDoc.Load");
        _btnCreate.Content = T("SalesDelivery.CreateDraft");
        _btnPost.Content = T("OrderDoc.Post");

        foreach (var (label, key) in _headerLabels)
            label.Text = T(key);

        var headers = new[]
        {
            "SalesDelivery.ItemCode",
            "SalesDelivery.Description",
            "SalesDelivery.Unit",
            "SalesDelivery.Ordered",
            "SalesDelivery.Delivered",
            "SalesDelivery.Remaining",
            "SalesDelivery.ToDeliver"
        };
        for (var index = 0; index < _grid.Columns.Count && index < headers.Length; index++)
            _grid.Columns[index].Header = T(headers[index]);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderView))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            Close();
            return;
        }

        if (AppHost.JournalStore is null)
        {
            ShowWarningMessage(T("OrderDoc.ServiceUnavailable"));
            Close();
            return;
        }

        try
        {
            _companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.OnLoaded");
            ShowWarningMessage(T("OrderDoc.ServiceUnavailable"));
            Close();
            return;
        }

        ResetWindow();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "OrderDoc.Load");
        StyleButton(_btnCreate, "SalesDelivery.CreateDraft");
        StyleButton(_btnPost, "OrderDoc.Post");
        _btnNew.Click += (_, _) => ResetWindow();
        _btnLoad.Click += async (_, _) => await LoadOrderAsync();
        _btnCreate.Click += async (_, _) => await CreateDeliveryAsync();
        _btnPost.Click += async (_, _) => await PostDeliveryAsync();
        actions.Children.Add(_btnNew);
        actions.Children.Add(_btnLoad);
        actions.Children.Add(_btnCreate);
        actions.Children.Add(_btnPost);

        var actionBorder = new Border
        {
            Child = actions,
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        actionBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBorder, Dock.Bottom);
        root.Children.Add(actionBorder);

        var footer = new Border
        {
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = _lblStatus
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddHeaderField(header, 0, "SalesDelivery.OrderNo", _txtOrderNo);
        AddHeaderField(header, 1, "SalesDelivery.DeliveryNo", _txtDeliveryNo);
        AddHeaderField(header, 2, "SalesDelivery.Date", _dtDelivery);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.IsReadOnly = false;
        _grid.ItemsSource = _lines;
        _grid.AlternationCount = 2;
        _grid.Columns.Add(TextColumn("ItemCode", "SalesDelivery.ItemCode", 115, true));
        _grid.Columns.Add(TextColumn("Description", "SalesDelivery.Description", 2, true));
        _grid.Columns.Add(TextColumn("Unit", "SalesDelivery.Unit", 75, true));
        _grid.Columns.Add(TextColumn("OrderedQty", "SalesDelivery.Ordered", 95, true));
        _grid.Columns.Add(TextColumn("DeliveredQty", "SalesDelivery.Delivered", 95, true));
        _grid.Columns.Add(TextColumn("RemainingQty", "SalesDelivery.Remaining", 95, true));
        _grid.Columns.Add(TextColumn("QtyToDeliver", "SalesDelivery.ToDeliver", 110, false));
        Grid.SetRow(_grid, 1);
        layout.Children.Add(_grid);

        root.Children.Add(layout);
        Content = root;
        UpdateState();
    }

    private void AddHeaderField(Grid grid, int column, string labelKey, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var label = new TextBlock { Text = T(labelKey), Margin = new Thickness(0, 0, 0, 4) };
        _headerLabels.Add((label, labelKey));
        panel.Children.Add(label);
        control.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(control);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static DataGridTextColumn TextColumn(string path, string headerKey, double width, bool readOnly)
        => new()
        {
            Header = T(headerKey),
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width <= 2 ? new DataGridLength(width, DataGridLengthUnitType.Star) : new DataGridLength(width),
            IsReadOnly = readOnly
        };

    private static void StyleButton(Button button, string key)
    {
        button.Content = T(key);
        button.Tag = key;
        button.Margin = new Thickness(0, 0, 8, 0);
        button.MinWidth = VisuId.ButtonMinWidth;
        button.SetResourceReference(FrameworkElement.StyleProperty, "VisuButtonStyle");
    }

    private void ResetWindow()
    {
        _orderId = null;
        _deliveryId = null;
        _deliveryStatus = CommercialDocumentStatus.Draft;
        _txtOrderNo.Clear();
        _txtDeliveryNo.Clear();
        _dtDelivery.SelectedDate = DateTime.Today;
        _lines.Clear();
        _lblStatus.Text = T("SalesDelivery.SelectOrder");
        UpdateState();
    }

    private async Task LoadOrderAsync()
    {
        if (AppHost.Orders is null || _companyId == Guid.Empty)
        {
            ShowWarningMessage(T("OrderDoc.ServiceUnavailable"));
            return;
        }

        var orderNo = _txtOrderNo.Text.Trim();
        if (string.IsNullOrWhiteSpace(orderNo))
        {
            ShowWarningMessage(T("SalesDelivery.OrderRequired"));
            return;
        }

        try
        {
            var order = await AppHost.Orders.FindByNumberAsync(
                CommercialDocSide.Sales, _companyId, CommercialDocKinds.SalesOrder, orderNo);
            if (order is null)
            {
                ShowWarningMessage(T("OrderDoc.NotFound"));
                return;
            }
            if (order.Status != CommercialDocumentStatus.Posted)
            {
                ShowWarningMessage(T("SalesDelivery.PostedOrderRequired"));
                return;
            }

            var progress = await AppHost.Orders.GetProgressAsync(CommercialDocSide.Sales, order.DocId);
            _orderId = order.DocId;
            _deliveryId = null;
            _txtDeliveryNo.Clear();
            _lines.Clear();

            for (var index = 0; index < order.Lines.Count; index++)
            {
                var source = order.Lines[index];
                var state = index < progress.Lines.Count ? progress.Lines[index] : null;
                var delivered = state?.FulfilledQty ?? 0m;
                var remaining = Math.Max(0m, source.Qty - delivered);
                _lines.Add(new DeliveryLineVm
                {
                    MaterialId = source.MaterialId,
                    ItemCode = source.MaterialCode ?? string.Empty,
                    Description = source.Description,
                    Unit = source.Unit ?? string.Empty,
                    OrderedQty = source.Qty,
                    DeliveredQty = delivered,
                    RemainingQty = remaining,
                    QtyToDeliver = remaining
                });
            }

            _lblStatus.Text = $"{T("SalesDelivery.OrderLoaded")}: {order.DocNo}";
            UpdateState();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.LoadOrder");
            ShowWarningMessage(ex.Message);
        }
    }

    private async Task CreateDeliveryAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (_busy || _orderId is null || AppHost.Orders is null)
        {
            ShowWarningMessage(T("SalesDelivery.SelectOrder"));
            return;
        }

        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            var selected = _lines.Where(line => line.QtyToDeliver > 0m).ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException(T("SalesDelivery.QuantityRequired"));
            if (selected.Any(line => line.MaterialId is null))
                throw new InvalidOperationException(T("SalesDelivery.MaterialRequired"));

            foreach (var line in selected)
            {
                if (line.QtyToDeliver > line.RemainingQty)
                    throw new InvalidOperationException(T("SalesDelivery.ExceedsRemaining"));
            }

            var quantities = selected
                .GroupBy(line => line.MaterialId!.Value)
                .ToDictionary(group => group.Key, group => group.Sum(line => line.QtyToDeliver));

            _busy = true;
            UpdateState();
            var order = await AppHost.Orders.FindByIdAsync(CommercialDocSide.Sales, _orderId.Value);
            if (order is null)
                throw new InvalidOperationException(T("OrderDoc.NotFound"));

            var result = await AppHost.Orders.CreateSalesDeliveryAsync(
                _orderId.Value,
                DateOnly.FromDateTime(_dtDelivery.SelectedDate ?? DateTime.Today),
                CurrentUserId,
                quantities,
                order.WarehouseId);

            _deliveryId = result.DeliveryId;
            _deliveryStatus = CommercialDocumentStatus.Draft;
            _txtDeliveryNo.Text = result.DeliveryDocNo;
            _lblStatus.Text = $"{T("SalesDelivery.DraftCreated")}: {result.DeliveryDocNo}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.CreateDelivery");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private async Task PostDeliveryAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (_busy || _deliveryId is null || AppHost.Orders is null)
        {
            ShowWarningMessage(T("SalesDelivery.SaveFirst"));
            return;
        }

        try
        {
            _busy = true;
            UpdateState();
            await AppHost.Orders.PostSalesDeliveryAsync(_deliveryId.Value, CurrentUserId);
            _deliveryStatus = CommercialDocumentStatus.Posted;
            _lblStatus.Text = $"{T("SalesDelivery.Posted")}: {_txtDeliveryNo.Text}";
            if (_orderId is not null)
            {
                var progress = await AppHost.Orders.GetProgressAsync(CommercialDocSide.Sales, _orderId.Value);
                for (var index = 0; index < _lines.Count && index < progress.Lines.Count; index++)
                {
                    _lines[index].DeliveredQty = progress.Lines[index].FulfilledQty;
                    _lines[index].RemainingQty = Math.Max(0m, _lines[index].OrderedQty - _lines[index].DeliveredQty);
                    _lines[index].QtyToDeliver = _lines[index].RemainingQty;
                }
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.PostDelivery");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private void UpdateState()
    {
        _btnLoad.IsEnabled = !_busy;
        var canStartDelivery = _deliveryId is null || _deliveryStatus == CommercialDocumentStatus.Posted;
        _btnCreate.IsEnabled = !_busy && _orderId is not null && canStartDelivery && _lines.Any(line => line.RemainingQty > 0m);
        _btnPost.IsEnabled = !_busy && _deliveryId is not null && _deliveryStatus == CommercialDocumentStatus.Draft;
        _grid.IsEnabled = !_busy && _orderId is not null && canStartDelivery;
    }

    private sealed class DeliveryLineVm : INotifyPropertyChanged
    {
        private decimal _qtyToDeliver;
        public event PropertyChangedEventHandler? PropertyChanged;
        public long? MaterialId { get; init; }
        public string ItemCode { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public decimal OrderedQty { get; init; }
        private decimal _deliveredQty;
        private decimal _remainingQty;
        public decimal DeliveredQty
        {
            get => _deliveredQty;
            set
            {
                _deliveredQty = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DeliveredQty)));
            }
        }
        public decimal RemainingQty
        {
            get => _remainingQty;
            set
            {
                _remainingQty = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingQty)));
            }
        }
        public decimal QtyToDeliver
        {
            get => _qtyToDeliver;
            set
            {
                _qtyToDeliver = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QtyToDeliver)));
            }
        }
    }
}
