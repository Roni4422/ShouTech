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
using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Creates and posts partial delivery notes from posted sales orders.
/// The repository revalidates the source-order quantity at posting time.
///
/// Picking (sales cycle W4, spec §4) lives in THIS window rather than a
/// new one: it is the only surface that already loads a posted order
/// with per-source-line remaining quantities — the exact input a
/// picking is staged from — and the confirmed picking feeds the same
/// grid's ToDeliver quantities, so warehouse and delivery work stay in
/// one place. The picking buttons sit in the same bottom command bar.
/// </summary>
public sealed class SalesDeliveryWin : BaseWin
{
    private readonly TextBox _txtOrderNo = new();
    private readonly TextBox _txtDeliveryNo = new() { IsReadOnly = true };
    private readonly DatePicker _dtDelivery = new() { SelectedDate = DateTime.Today };
    private readonly TextBox _txtDeliveryCost = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnCreate = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnInvoice = new();
    private readonly Button _btnConsolidated = new();
    private readonly Button _btnReverse = new();
    private readonly Button _btnCancelDraft = new();
    private readonly Button _btnPickCreate = new();
    private readonly Button _btnPickConfirm = new();
    private readonly Button _btnPickCancel = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _lblStatus = new();
    private readonly ObservableCollection<DeliveryLineVm> _lines = new();
    private readonly List<(TextBlock Label, string Key)> _headerLabels = new();

    private Guid _companyId;
    private long? _orderId;
    private long? _deliveryId;
    private string _deliveryStatus = CommercialDocumentStatus.Draft;
    private long? _pickingId;
    private string? _pickingStatus;
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
        _btnInvoice.Content = T("SalesDelivery.CreateInvoice");
        _btnConsolidated.Content = T("SalesDelivery.ConsolidatedInvoice");
        _btnReverse.Content = T("SalesDelivery.Reverse");
        _btnCancelDraft.Content = T("SalesDelivery.CancelDraft");
        _btnPickCreate.Content = T("SalesDelivery.CreatePicking");
        _btnPickConfirm.Content = T("SalesDelivery.ConfirmPicking");
        _btnPickCancel.Content = T("SalesDelivery.CancelPicking");

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
            "SalesDelivery.ToDeliver",
            "SalesDelivery.Picked"
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
        StyleButton(_btnInvoice, "SalesDelivery.CreateInvoice");
        StyleButton(_btnConsolidated, "SalesDelivery.ConsolidatedInvoice");
        StyleButton(_btnReverse, "SalesDelivery.Reverse");
        StyleButton(_btnCancelDraft, "SalesDelivery.CancelDraft");
        StyleButton(_btnPickCreate, "SalesDelivery.CreatePicking");
        StyleButton(_btnPickConfirm, "SalesDelivery.ConfirmPicking");
        StyleButton(_btnPickCancel, "SalesDelivery.CancelPicking");
        _btnNew.Click += (_, _) => ResetWindow();
        _btnLoad.Click += async (_, _) => await LoadOrderAsync();
        _btnCreate.Click += async (_, _) => await CreateDeliveryAsync();
        _btnPost.Click += async (_, _) => await PostDeliveryAsync();
        _btnInvoice.Click += async (_, _) => await CreateInvoiceAsync();
        _btnConsolidated.Click += async (_, _) => await CreateConsolidatedInvoiceAsync();
        _btnReverse.Click += async (_, _) => await ReverseDeliveryAsync();
        _btnCancelDraft.Click += async (_, _) => await CancelDraftAsync();
        _btnPickCreate.Click += async (_, _) => await CreatePickingAsync();
        _btnPickConfirm.Click += async (_, _) => await ConfirmPickingAsync();
        _btnPickCancel.Click += async (_, _) => await CancelPickingAsync();
        actions.Children.Add(_btnNew);
        actions.Children.Add(_btnLoad);
        actions.Children.Add(_btnCreate);
        actions.Children.Add(_btnPost);
        actions.Children.Add(_btnInvoice);
        actions.Children.Add(_btnConsolidated);
        actions.Children.Add(_btnReverse);
        actions.Children.Add(_btnCancelDraft);
        actions.Children.Add(_btnPickCreate);
        actions.Children.Add(_btnPickConfirm);
        actions.Children.Add(_btnPickCancel);

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
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddHeaderField(header, 0, "SalesDelivery.OrderNo", _txtOrderNo);
        AddHeaderField(header, 1, "SalesDelivery.DeliveryNo", _txtDeliveryNo);
        AddHeaderField(header, 2, "SalesDelivery.Date", _dtDelivery);
        AddHeaderField(header, 3, "SalesDelivery.DeliveryCost", _txtDeliveryCost);
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
        _grid.Columns.Add(TextColumn("PickedQty", "SalesDelivery.Picked", 100, false));
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
        _pickingId = null;
        _pickingStatus = null;
        _txtOrderNo.Clear();
        _txtDeliveryNo.Clear();
        _txtDeliveryCost.Clear();
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
                var state = progress.Lines.FirstOrDefault(p => p.LineNo == source.LineNo)
                            ?? (index < progress.Lines.Count ? progress.Lines[index] : null);
                var delivered = state?.FulfilledQty ?? 0m;
                var remaining = Math.Max(0m, source.Qty - delivered);
                _lines.Add(new DeliveryLineVm
                {
                    SourceLineNo = source.LineNo,
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

            // A confirmed picking for this order feeds delivery
            // creation: the picked quantities become the default
            // ToDeliver quantities (the manual path stays editable).
            _pickingId = null;
            _pickingStatus = null;
            var picking = await AppHost.Orders.FindConfirmedPickingAsync(_companyId, order.DocId);
            if (picking is not null)
            {
                ApplyPicking(picking);
                _lblStatus.Text = $"{T("SalesDelivery.OrderLoaded")}: {order.DocNo} · {T("SalesDelivery.PickingApplied")}";
            }
            UpdateState();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.LoadOrder");
            ShowWarningMessage(ex.Message);
        }
    }

    /// <summary>Copies a picking's picked quantities into the grid:
    /// the Picked column always, and ToDeliver when the picking is
    /// confirmed (capped at what still remains to deliver).</summary>
    private void ApplyPicking(SalesPickingSnapshot picking)
    {
        _pickingId = picking.PickingId;
        _pickingStatus = picking.Status;
        var pickedByLine = picking.Lines.ToDictionary(line => line.SourceLineNo, line => line.PickedQty);
        foreach (var line in _lines)
        {
            if (!pickedByLine.TryGetValue(line.SourceLineNo, out var picked))
                continue;
            line.PickedQty = picked;
            if (picking.Status == SalesPickingStatus.Confirmed)
                line.QtyToDeliver = Math.Min(picked, line.RemainingQty);
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

            // Per-source-line quantities: duplicate-material order lines
            // deliver independently, so the grid rows key by source line
            // number, never collapsed by material.
            var quantities = selected
                .ToDictionary(line => line.SourceLineNo, line => line.QtyToDeliver);

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
                order.WarehouseId,
                deliveryCost: ParseMoney(_txtDeliveryCost.Text));

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

    private async Task CreateInvoiceAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (_busy || _deliveryId is null || _deliveryStatus != CommercialDocumentStatus.Posted || AppHost.Orders is null)
        {
            ShowWarningMessage(T("SalesDelivery.PostDeliveryFirst"));
            return;
        }

        try
        {
            _busy = true;
            UpdateState();
            var result = await AppHost.Orders.CreateSalesInvoiceFromDeliveryAsync(
                _deliveryId.Value,
                DateOnly.FromDateTime(DateTime.Today),
                CurrentUserId);
            _lblStatus.Text = $"{T("SalesDelivery.InvoiceCreated")}: {result.InvoiceDocNo}";
            var invoiceWindow = new InvoiceWin(result.InvoiceId, InvDocKind.SalesInvoice) { Owner = this };
            invoiceWindow.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.CreateInvoice");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    /// <summary>
    /// CONSOLIDATED INVOICE (W5): lists the loaded order's posted,
    /// not-fully-billed deliveries with checkboxes and creates ONE
    /// draft sales invoice from the selected ones (full remaining
    /// quantities per delivery), then opens it in InvoiceWin. The
    /// chain service enforces same-party/same-currency and per-line
    /// remaining quantities; per-delivery 1215 relief stays exact via
    /// the lines' source attribution.
    /// </summary>
    private async Task CreateConsolidatedInvoiceAsync()
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
            _busy = true;
            UpdateState();
            var deliveries = await AppHost.Orders.GetOrderDeliveriesAsync(_orderId.Value);
            var unbilled = deliveries.Where(d => d.RemainingQty > 0m).ToList();
            if (unbilled.Count == 0)
            {
                ShowWarningMessage(T("SalesDelivery.NoUnbilledDeliveries"));
                return;
            }

            var selectedIds = ConsolidatedPickerDialog.Show(this, unbilled);
            if (selectedIds is null || selectedIds.Count == 0)
                return;

            var result = await AppHost.Orders.CreateConsolidatedInvoiceAsync(
                selectedIds,
                DateOnly.FromDateTime(DateTime.Today),
                CurrentUserId);
            _lblStatus.Text = $"{T("SalesDelivery.ConsolidatedCreated")}: {result.InvoiceDocNo}";
            var invoiceWindow = new InvoiceWin(result.InvoiceId, InvDocKind.SalesInvoice) { Owner = this };
            invoiceWindow.Show();
        }
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "SalesDeliveryWin.CreateConsolidatedInvoice");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            ShowWarningMessage($"{info.Message} (Error {info.Code})");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.CreateConsolidatedInvoice");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    /// <summary>Checkbox picker for the order's unbilled deliveries
    /// (the PromptDialog modal pattern, multi-select).</summary>
    private static class ConsolidatedPickerDialog
    {
        public static List<long>? Show(Window owner, IReadOnlyList<OrderDeliveryBillingInfo> deliveries)
        {
            var dialog = new Window
            {
                Title = T("SalesDelivery.ConsolidatedInvoice"),
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            var boxes = new List<(CheckBox Box, long DeliveryId)>();
            foreach (var delivery in deliveries)
            {
                var box = new CheckBox
                {
                    Content = $"{delivery.DocNo} · {delivery.DocDate:yyyy-MM-dd} · {T("SalesDelivery.Remaining")}: {delivery.RemainingQty.ToString("0.###", CultureInfo.InvariantCulture)}",
                    IsChecked = true,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                boxes.Add((box, delivery.DeliveryDocId));
                panel.Children.Add(box);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = T("Common.OK"), MinWidth = 84, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancel = new Button { Content = T("Common.Cancel"), MinWidth = 84, IsCancel = true };
            ok.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            return dialog.ShowDialog() == true
                ? boxes.Where(b => b.Box.IsChecked == true).Select(b => b.DeliveryId).ToList()
                : null;
        }
    }

    /// <summary>
    /// Reverses the posted delivery note (sales cycle B4, gap G13):
    /// stock returns at the original movement cost, the delivery GL is
    /// compensated, and the document is cancelled with an audited
    /// reason. The repository blocks the reversal while a posted sales
    /// invoice is linked (SAL-7005).
    /// </summary>
    private async Task ReverseDeliveryAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (_busy || _deliveryId is null || _deliveryStatus != CommercialDocumentStatus.Posted || AppHost.Orders is null)
        {
            ShowWarningMessage(T("SalesDelivery.PostDeliveryFirst"));
            return;
        }

        var input = PromptDialog.Show(this, T("SalesDelivery.ReverseTitle"),
            (T("SalesDelivery.Reason"), string.Empty));
        if (input is null || string.IsNullOrWhiteSpace(input[0]))
            return;

        try
        {
            _busy = true;
            UpdateState();
            await AppHost.Orders.ReverseSalesChainDocumentAsync(_deliveryId.Value, input[0], CurrentUserId);
            _deliveryStatus = CommercialDocumentStatus.Cancelled;
            _lblStatus.Text = $"{T("SalesDelivery.Reversed")}: {_txtDeliveryNo.Text}";
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
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "SalesDeliveryWin.ReverseDelivery");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            ShowWarningMessage($"{info.Message} (Error {info.Code})");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.ReverseDelivery");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    /// <summary>Cancels the draft delivery note (no posting effects exist yet).</summary>
    private async Task CancelDraftAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (_busy || _deliveryId is null || _deliveryStatus != CommercialDocumentStatus.Draft || AppHost.Orders is null)
            return;

        var confirm = MessageBox.Show(this, T("SalesDelivery.ConfirmCancelDraft"), Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _busy = true;
            UpdateState();
            await AppHost.Orders.CancelOrderAsync(CommercialDocSide.Sales, _deliveryId.Value, CurrentUserId);
            _deliveryStatus = CommercialDocumentStatus.Cancelled;
            _lblStatus.Text = $"{T("SalesDelivery.Cancelled")}: {_txtDeliveryNo.Text}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.CancelDraft");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    /// <summary>Stages a draft picking for the loaded order; its lines
    /// arrive pre-filled with the remaining-to-deliver quantities.</summary>
    private async Task CreatePickingAsync()
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
            _busy = true;
            UpdateState();
            var pickingId = await AppHost.Orders.CreatePickingFromOrderAsync(_orderId.Value, CurrentUserId);
            var picking = await AppHost.Orders.GetPickingAsync(pickingId);
            if (picking is not null)
                ApplyPicking(picking);
            _lblStatus.Text = $"{T("SalesDelivery.PickingCreated")}: #{pickingId}";
        }
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "SalesDeliveryWin.CreatePicking");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            ShowWarningMessage($"{info.Message} (Error {info.Code})");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.CreatePicking");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    /// <summary>Confirms the draft picking with the grid's Picked
    /// quantities. Over-picking a line beyond the remaining quantity
    /// is rejected by the repository with SAL-7012.</summary>
    private async Task ConfirmPickingAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (_busy || _pickingId is null || _pickingStatus != SalesPickingStatus.Draft || AppHost.Orders is null)
            return;

        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var picked = _lines.ToDictionary(line => line.SourceLineNo, line => line.PickedQty);
            _busy = true;
            UpdateState();
            await AppHost.Orders.ConfirmPickingAsync(_pickingId.Value, picked, CurrentUserId);
            _pickingStatus = SalesPickingStatus.Confirmed;
            foreach (var line in _lines)
                line.QtyToDeliver = Math.Min(line.PickedQty, line.RemainingQty);
            _lblStatus.Text = $"{T("SalesDelivery.PickingConfirmed")}: #{_pickingId}";
        }
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "SalesDeliveryWin.ConfirmPicking");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            ShowWarningMessage($"{info.Message} (Error {info.Code})");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.ConfirmPicking");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private async Task CancelPickingAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (_busy || _pickingId is null || _pickingStatus is null ||
            _pickingStatus == SalesPickingStatus.Cancelled || AppHost.Orders is null)
            return;

        try
        {
            _busy = true;
            UpdateState();
            await AppHost.Orders.CancelPickingAsync(_pickingId.Value, CurrentUserId);
            _pickingStatus = SalesPickingStatus.Cancelled;
            _lblStatus.Text = $"{T("SalesDelivery.PickingCancelled")}: #{_pickingId}";
            _pickingId = null;
            _pickingStatus = null;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesDeliveryWin.CancelPicking");
            ShowWarningMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private static decimal ParseMoney(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            if (value < 0m)
                throw new InvalidOperationException(T("SalesDelivery.DeliveryCostNegative"));
            return value;
        }
        throw new InvalidOperationException(T("SalesDelivery.DeliveryCostInvalid"));
    }

    private void ShowWarningMessage(string message)
        => MessageBox.Show(this, message, T("Common.Warning"),
            MessageBoxButton.OK, MessageBoxImage.Warning);

    private void UpdateState()
    {
        _btnLoad.IsEnabled = !_busy;
        _btnInvoice.IsEnabled = !_busy && _deliveryId is not null && _deliveryStatus == CommercialDocumentStatus.Posted;
        _btnConsolidated.IsEnabled = !_busy && _orderId is not null;
        _btnReverse.IsEnabled = !_busy && _deliveryId is not null && _deliveryStatus == CommercialDocumentStatus.Posted;
        _btnCancelDraft.IsEnabled = !_busy && _deliveryId is not null && _deliveryStatus == CommercialDocumentStatus.Draft;
        var canStartDelivery = _deliveryId is null || _deliveryStatus == CommercialDocumentStatus.Posted || _deliveryStatus == CommercialDocumentStatus.Cancelled;
        _btnCreate.IsEnabled = !_busy && _orderId is not null && canStartDelivery && _lines.Any(line => line.RemainingQty > 0m);
        _btnPost.IsEnabled = !_busy && _deliveryId is not null && _deliveryStatus == CommercialDocumentStatus.Draft;
        _grid.IsEnabled = !_busy && _orderId is not null && canStartDelivery;
        _btnPickCreate.IsEnabled = !_busy && _orderId is not null && _pickingId is null;
        _btnPickConfirm.IsEnabled = !_busy && _pickingId is not null && _pickingStatus == SalesPickingStatus.Draft;
        _btnPickCancel.IsEnabled = !_busy && _pickingId is not null && _pickingStatus != SalesPickingStatus.Cancelled;
    }

    /// <summary>Minimal modal prompt for the reversal reason (ChqListWin pattern).</summary>
    private static class PromptDialog
    {
        public static string[]? Show(Window owner, string title, params (string Label, string Value)[] fields)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            var boxes = new List<TextBox>();
            foreach (var (label, value) in fields)
            {
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
                var box = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 10) };
                boxes.Add(box);
                panel.Children.Add(box);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = T("Common.OK"), MinWidth = 84, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancel = new Button { Content = T("Common.Cancel"), MinWidth = 84, IsCancel = true };
            ok.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            return dialog.ShowDialog() == true ? boxes.Select(b => b.Text.Trim()).ToArray() : null;
        }
    }

    private sealed class DeliveryLineVm : INotifyPropertyChanged
    {
        private decimal _qtyToDeliver;
        public event PropertyChangedEventHandler? PropertyChanged;
        public int SourceLineNo { get; init; }
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
        private decimal _pickedQty;
        public decimal PickedQty
        {
            get => _pickedQty;
            set
            {
                _pickedQty = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PickedQty)));
            }
        }
    }
}
