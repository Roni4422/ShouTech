using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

public enum OrdKind
{
    Sales,
    Purchase
}

/// <summary>
/// Sales / purchase order window (RS-5). Orders are commitment documents on
/// the commercial-document engine (sales_documents / purchase_documents):
/// they post no GL and no stock. Save/Post/Close/Cancel persist through
/// OrderService; Convert creates a posted invoice with only the remaining
/// unbilled quantities in a single transaction (invoice + stock through
/// the valuation engine + COGS + party ledger + GL + document link —
/// Step 9 closed the conversion's missing stock effect, sweep ORD-01).
/// Progress columns show fulfilled/billed quantities from
/// document_links, ERPNext-style.
/// </summary>
public sealed class OrdWin : BaseWin, IWindowMode
{
    protected override string? ScheduleTintName => "Sky";

    private readonly OrdKind _kind;

    /// <summary>The order kind this window was opened with (Step 11:
    /// the router's mode identity — Sales and Purchase orders must
    /// never reactivate each other).</summary>
    public OrdKind Kind => _kind;
    public string ModeKey => _kind.ToString();

    private readonly TextBox _txtDocNo = new();
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today };
    private readonly DatePicker _dtDeliveryDate = new() { SelectedDate = DateTime.Today.AddDays(7) };
    private readonly TextBox _txtPartyCode = new();
    private readonly TextBox _txtPartyName = new();
    private readonly TextBox _txtWhs = new() { Text = "WH-01" };
    private readonly TextBox _txtNotes = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnConvert = new();
    private readonly Button _btnReceipt = new();
    private readonly Button _btnClose = new();
    private readonly Button _btnCancel = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemLine = new();
    private readonly Button _btnPrint = new();

    private readonly TextBlock _lblTotal = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<OrderLineVm> _lines = new();

    private long? _docId;
    private long? _partyId;
    private string _status = CommercialDocumentStatus.Draft;
    private Guid _companyId;
    private bool _loading;

    public OrdWin(OrdKind kind = OrdKind.Sales)
    {
        _kind = kind;
        Width = 980;
        Height = 600;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        Loaded += OnLoaded;
    }

    private string DocKindCode => _kind == OrdKind.Sales ? CommercialDocKinds.SalesOrder : CommercialDocKinds.PurchaseOrder;
    private CommercialDocSide Side => _kind == OrdKind.Sales ? CommercialDocSide.Sales : CommercialDocSide.Purchase;
    private string ViewPermission => _kind == OrdKind.Sales ? PermCat.Sales.OrderView : PermCat.Purchasing.OrderView;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(ViewPermission))
        {
            MessageBox.Show(this, T("OrderDoc.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        if (AppHost.JournalStore is not null)
        {
            try
            {
                _companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.OnLoaded");
                _companyId = Guid.Empty;
            }
        }
        await ResetOrderAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "OrderDoc.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "OrderDoc.Post");
        StyleButton(_btnConvert, _kind == OrdKind.Sales ? "OrderDoc.ConvertToInvoice" : "OrderDoc.CreateSupplierInvoice");
        StyleButton(_btnReceipt, "OrderDoc.CreateReceipt");
        _btnReceipt.Visibility = _kind == OrdKind.Purchase ? Visibility.Visible : Visibility.Collapsed;
        StyleButton(_btnClose, "OrderDoc.CloseOrder");
        StyleButton(_btnCancel, "OrderDoc.CancelOrder");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemLine, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");

        _btnNew.Click += async (_, _) => await ResetOrderAsync();
        _btnLoad.Click += async (_, _) => await LoadByNumberAsync();
        _btnSave.Click += async (_, _) => await SaveAsync();
        _btnPost.Click += async (_, _) => await PostAsync();
        _btnConvert.Click += async (_, _) => await ConvertAsync();
        _btnReceipt.Click += async (_, _) => await ReceiveAsync();
        // Step 9 (ORD-03): Close checks the permission family of the
        // window's OWN kind — it previously hardcoded the SALES cancel
        // permission for purchase orders too, so a purchasing-only user
        // could not close a PO and a sales-only user could close one.
        _btnClose.Click += async (_, _) => await SetStatusAsync(CommercialDocumentStatus.Closed, _kind == OrdKind.Sales ? PermCat.Sales.OrderCancel : PermCat.Purchasing.OrderCancel);
        _btnCancel.Click += async (_, _) => await SetStatusAsync(CommercialDocumentStatus.Cancelled, _kind == OrdKind.Sales ? PermCat.Sales.OrderCancel : PermCat.Purchasing.OrderCancel);
        _btnPrint.Click += (_, _) => PrintOrder();

        _btnAddLine.Click += (_, _) =>
        {
            if (!IsEditable) return;
            var l = new OrderLineVm { LineNo = _lines.Count + 1, Unit = "PCS", Qty = 1, UnitPrice = 0 };
            _lines.Add(l);
            _grid.SelectedItem = l;
            _grid.ScrollIntoView(l);
            RefreshTotals();
        };

        _btnRemLine.Click += (_, _) =>
        {
            if (!IsEditable) return;
            if (_grid.SelectedItem is OrderLineVm selected && _lines.Count > 1)
            {
                _lines.Remove(selected);
                Renumber();
                RefreshTotals();
            }
        };

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnPost, _btnConvert, _btnReceipt, _btnClose, _btnCancel, _btnAddLine, _btnRemLine, _btnPrint })
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

        var footer = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var footerContent = new DockPanel();
        DockPanel.SetDock(_lblTotal, Dock.Left);
        DockPanel.SetDock(_lblStatus, Dock.Right);
        footerContent.Children.Add(_lblTotal);
        footerContent.Children.Add(_lblStatus);
        footer.Child = footerContent;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header = one shared column grid (4 pairs), side labels, edges
        // aligned across rows; party name spans two pairs, notes take a
        // full-width row (Phenix rule).
        var headerForm = CreateFormGrid(4);
        headerForm.Margin = new Thickness(0, 0, 0, 8);
        AddFormField(headerForm, 0, 0, "OrderDoc.OrderNo", _txtDocNo);
        AddFormField(headerForm, 0, 1, "OrderDoc.Date", _dtDate);
        AddFormField(headerForm, 0, 2, "OrderDoc.DeliveryDate", _dtDeliveryDate);
        AddFormField(headerForm, 0, 3, _kind == OrdKind.Sales ? "Invoice.CustomerCode" : "Invoice.SupplierCode", _txtPartyCode);
        AddFormField(headerForm, 1, 0, _kind == OrdKind.Sales ? "Invoice.CustomerName" : "Invoice.SupplierName", _txtPartyName, spanPairs: 2);
        AddFormField(headerForm, 1, 2, "Invoice.Warehouse", _txtWhs);
        AddFormField(headerForm, 2, 0, "Invoice.Notes", _txtNotes, spanPairs: 4);
        var headerPanel = headerForm;

        _txtPartyCode.LostFocus += (_, _) => ResolveParty();
        Grid.SetRow(headerPanel, 0);
        body.Children.Add(headerPanel);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");

        _grid.ItemsSource = _lines;
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.LineNo), 40, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.ItemCode), 105));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.Description), 210));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.Unit), 60));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.QtyText), 75));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.PriceText), 90));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.DiscountText), 70));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.TaxText), 60));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Binding = new Binding(nameof(OrderLineVm.LineTotalText)) { Mode = BindingMode.OneWay },
            Width = 100,
            IsReadOnly = true
        });
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.FulfilledText), 80, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(OrderLineVm.BilledText), 80, readOnly: true));

        _grid.CellEditEnding += (_, e) =>
        {
            if (e.Row.Item is OrderLineVm line && e.Column.DisplayIndex == 1)
                ResolveMaterial(line);
            Dispatcher.BeginInvoke(RefreshTotals);
        };

        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private bool IsEditable => _status == CommercialDocumentStatus.Draft;

    private void ResolveParty()
    {
        if (_loading) return;
        var found = TaxSvc.ResolveParty(_txtPartyCode.Text.Trim(), _kind == OrdKind.Sales);
        if (found is { } party)
        {
            _partyId = party.Id;
            if (string.IsNullOrWhiteSpace(_txtPartyName.Text))
                _txtPartyName.Text = party.Name;
        }
        else
        {
            _partyId = null;
        }
    }

    private static void ResolveMaterial(OrderLineVm line)
    {
        var found = TaxSvc.ResolveMaterial(line.ItemCode.Trim());
        if (found is { } material)
        {
            line.MaterialId = material.Id;
            if (string.IsNullOrWhiteSpace(line.Description)) line.Description = material.Name;
            if (line.UnitPrice <= 0m) line.UnitPrice = material.Price;
            if (string.IsNullOrWhiteSpace(line.Unit) || line.Unit == "PCS") line.Unit = string.IsNullOrWhiteSpace(material.Unit) ? line.Unit : material.Unit;
            if (line.TaxRate <= 0m) line.TaxRate = material.TaxRate;
        }
    }

    private async Task ResetOrderAsync()
    {
        _loading = true;
        try
        {
            _docId = null;
            _partyId = null;
            _status = CommercialDocumentStatus.Draft;
            _txtDocNo.Text = AppHost.Orders is not null && _companyId != Guid.Empty
                ? await AppHost.Orders.NextDocNoAsync(Side, _companyId, DocKindCode)
                : (_kind == OrdKind.Sales ? "SO-" : "PO-") + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            _dtDate.SelectedDate = DateTime.Today;
            _dtDeliveryDate.SelectedDate = DateTime.Today.AddDays(7);
            _txtPartyCode.Text = string.Empty;
            _txtPartyName.Text = string.Empty;
            _txtNotes.Text = string.Empty;
            _lines.Clear();
            _lines.Add(new OrderLineVm { LineNo = 1, Unit = "PCS", Qty = 1, UnitPrice = 0 });
        }
        finally
        {
            _loading = false;
        }
        UpdateEditState();
        RefreshTotals();
    }

    private async Task LoadByNumberAsync()
    {
        if (AppHost.Orders is null || _companyId == Guid.Empty)
        {
            ShowWarningMessage(T("OrderDoc.ServiceUnavailable"));
            return;
        }
        var docNo = _txtDocNo.Text.Trim();
        if (string.IsNullOrWhiteSpace(docNo)) return;
        try
        {
            var doc = await AppHost.Orders.FindByNumberAsync(Side, _companyId, DocKindCode, docNo);
            if (doc is null)
            {
                ShowWarningMessage(T("OrderDoc.NotFound"));
                return;
            }
            ApplySnapshot(doc);
            if (doc.Status == CommercialDocumentStatus.Posted)
                await RefreshProgressAsync(doc.DocId);
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    /// <summary>Loads an order by database id (used by search/tracking windows).</summary>
    public async Task LoadOrderAsync(long docId)
    {
        if (AppHost.Orders is null) return;
        var doc = await AppHost.Orders.FindByIdAsync(Side, docId);
        if (doc is null) return;
        ApplySnapshot(doc);
        if (doc.Status == CommercialDocumentStatus.Posted)
            await RefreshProgressAsync(doc.DocId);
    }

    private void ApplySnapshot(CommercialDocumentSnapshot doc)
    {
        _loading = true;
        try
        {
            _docId = doc.DocId;
            _partyId = doc.PartyId;
            _status = doc.Status;
            _txtDocNo.Text = doc.DocNo;
            _dtDate.SelectedDate = doc.DocDate.ToDateTime(TimeOnly.MinValue);
            _dtDeliveryDate.SelectedDate = doc.DeliveryDate?.ToDateTime(TimeOnly.MinValue);
            _txtPartyCode.Text = doc.PartyCode ?? string.Empty;
            _txtPartyName.Text = doc.PartyName ?? string.Empty;
            _txtWhs.Text = doc.WarehouseName ?? string.Empty;
            _txtNotes.Text = doc.Notes ?? string.Empty;
            _lines.Clear();
            foreach (var line in doc.Lines)
            {
                _lines.Add(new OrderLineVm
                {
                    LineNo = line.LineNo,
                    MaterialId = line.MaterialId,
                    ItemCode = line.MaterialCode ?? string.Empty,
                    Description = line.Description,
                    Unit = line.Unit ?? "PCS",
                    Qty = line.Qty,
                    UnitPrice = line.UnitPrice,
                    DiscountPct = line.DiscountPct,
                    TaxRate = line.TaxRate
                });
            }
            if (_lines.Count == 0)
                _lines.Add(new OrderLineVm { LineNo = 1, Unit = "PCS", Qty = 1 });
        }
        finally
        {
            _loading = false;
        }
        UpdateEditState();
        RefreshTotals();
    }

    private async Task RefreshProgressAsync(long docId)
    {
        if (AppHost.Orders is null) return;
        try
        {
            var progress = await AppHost.Orders.GetProgressAsync(Side, docId);
            foreach (var line in _lines)
            {
                var match = progress.Lines.FirstOrDefault(p =>
                    (p.MaterialId is not null && p.MaterialId == line.MaterialId) ||
                    (p.MaterialId is null && p.MaterialCode == line.ItemCode));
                if (match is not null)
                {
                    line.FulfilledQty = match.FulfilledQty;
                    line.BilledQty = match.BilledQty;
                }
            }
            _lblStatus.Text = $"{T("OrderDoc.Status")}: {StatusText(_status)} | {T("OrderDoc.Fulfilled")}: {progress.PerFulfilled:N1}% | {T("OrderDoc.Billed")}: {progress.PerBilled:N1}%";
        }
        catch (Exception ex)
        {
            // Progress is informational; the order itself already
            // loaded — but the failure is reported centrally (Step 9),
            // never swallowed silently.
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.RefreshProgress");
        }
    }

    private async Task SaveAsync()
    {
        if (AppHost.Orders is null || _companyId == Guid.Empty)
        {
            ShowWarningMessage(T("OrderDoc.ServiceUnavailable"));
            return;
        }
        var permission = _docId is null
            ? (_kind == OrdKind.Sales ? PermCat.Sales.OrderCreate : PermCat.Purchasing.OrderCreate)
            : (_kind == OrdKind.Sales ? PermCat.Sales.OrderUpdate : PermCat.Purchasing.OrderUpdate);
        if (!AppHost.Permissions.Has(permission))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        try
        {
            var draft = BuildDraft();
            if (_docId is null)
            {
                _docId = await AppHost.Orders.CreateOrderAsync(draft, CurrentUserId);
            }
            else
            {
                await AppHost.Orders.UpdateOrderAsync(_docId.Value, draft, CurrentUserId);
            }
            _lblStatus.Text = $"{T("OrderDoc.Status")}: {StatusText(_status)} | {T("OrderDoc.Saved")}";
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    private CommercialDocumentDraft BuildDraft()
    {
        ResolveParty();
        if (string.IsNullOrWhiteSpace(_txtPartyCode.Text) && string.IsNullOrWhiteSpace(_txtPartyName.Text))
            throw new InvalidOperationException(T("OrderDoc.PartyRequired"));
        // Step 9 (ORD-10): the warehouse text resolves to a real
        // warehouse id (the same CardStore resolution InvoiceWin uses),
        // carried on the header AND every line, so a converted invoice
        // inherits a stock warehouse identity instead of null — before
        // this, conversion stock (Step 9) would have had to guess.
        var warehouseId = CardStore.ResolveWarehouseId(_txtWhs.Text.Trim());
        var lines = _lines
            .Where(l => !string.IsNullOrWhiteSpace(l.ItemCode) || !string.IsNullOrWhiteSpace(l.Description))
            .Select(l => new CommercialLineDraft(
                l.MaterialId,
                string.IsNullOrWhiteSpace(l.ItemCode) ? null : l.ItemCode.Trim(),
                string.IsNullOrWhiteSpace(l.Description) ? l.ItemCode.Trim() : l.Description.Trim(),
                string.IsNullOrWhiteSpace(l.Unit) ? "PCS" : l.Unit.Trim(),
                l.Qty,
                l.UnitPrice,
                l.DiscountPct,
                l.TaxRate,
                warehouseId))
            .ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException(T("OrderDoc.LinesRequired"));

        return new CommercialDocumentDraft(
            _companyId,
            DocKindCode,
            _txtDocNo.Text.Trim(),
            DateOnly.FromDateTime(_dtDate.SelectedDate ?? DateTime.Today),
            _partyId,
            string.IsNullOrWhiteSpace(_txtPartyName.Text) ? null : _txtPartyName.Text.Trim(),
            warehouseId,
            UsrPref.Load().CurrencyCode,
            // Step 7 convention: 0 = no document rate (the currency_rates
            // master supplies it at posting; a foreign-currency document
            // with no rate anywhere fails with ACC-5015). The window has
            // no rate field, so it must not fabricate 1.
            0m,
            string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim(),
            lines,
            string.IsNullOrWhiteSpace(_txtPartyCode.Text) ? null : _txtPartyCode.Text.Trim(),
            _dtDeliveryDate.SelectedDate is { } delivery ? DateOnly.FromDateTime(delivery) : null,
            null,
            null,
            null,
            null,
            string.IsNullOrWhiteSpace(_txtWhs.Text) ? null : _txtWhs.Text.Trim());
    }

    private async Task PostAsync()
    {
        if (AppHost.Orders is null || _docId is null)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        try
        {
            await AppHost.Orders.PostOrderAsync(Side, _docId.Value, CurrentUserId);
            _status = CommercialDocumentStatus.Posted;
            UpdateEditState();
            RefreshTotals();
            await RefreshProgressAsync(_docId.Value);
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    private async Task ConvertAsync()
    {
        var permission = _kind == OrdKind.Sales ? PermCat.Sales.OrderConvert : PermCat.Purchasing.OrderConvert;
        if (!AppHost.Permissions.Has(permission))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (AppHost.Orders is null || _docId is null)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        try
        {
            string invoiceDocNo;
            if (_kind == OrdKind.Purchase)
            {
                var supplierInvoiceLines = CaptureSupplierInvoiceLines();
                if (supplierInvoiceLines is null) return;
                var result = await AppHost.Orders.CreatePurchaseInvoiceAsync(
                    _companyId, _docId.Value, DateOnly.FromDateTime(DateTime.Today),
                    supplierInvoiceLines, CurrentUserId);
                invoiceDocNo = result.InvoiceDocNo;
            }
            else
            {
                var result = await AppHost.Orders.ConvertToInvoiceAsync(
                    Side, _docId.Value, DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
                invoiceDocNo = result.InvoiceDocNo;
            }
            _lblStatus.Text = $"{T(_kind == OrdKind.Purchase ? "OrderDoc.SupplierInvoiceCreated" : "OrderDoc.Converted")}: {invoiceDocNo}";
            await RefreshProgressAsync(_docId.Value);
        }
        catch (AppError ex)
        {
            // Coded business blocks (e.g. ACC-5006 credit limit on the
            // converted invoice): localized catalog message, not raw text.
            AppHost.Errors.Report(ex.Code, ex, "OrdWin.Convert");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            ShowWarningMessage($"{info.Message} (Error {info.Code})");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.Convert");
            ShowWarningMessage(ex.Message);
        }
    }

    private async Task ReceiveAsync()
    {
        if (_kind != OrdKind.Purchase || !AppHost.Permissions.Has(PermCat.Purchasing.OrderConvert))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (AppHost.Orders is null || _docId is null || _status != CommercialDocumentStatus.Posted)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }

        long? receiptWarehouseId;
        try
        {
            receiptWarehouseId = CardStore.ResolveWarehouseId(_txtWhs.Text.Trim());
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.ReceiveWarehouse");
            ShowWarningMessage(ex.Message);
            return;
        }

        var rows = _lines
            .Where(line => line.Qty - line.FulfilledQty > 0m)
            .GroupBy(line => line.MaterialId)
            .Select(group =>
            {
                var line = group.First();
                return new ReceiptLineVm
                {
                    MaterialId = line.MaterialId,
                    ItemCode = line.ItemCode,
                    Description = line.Description,
                    Unit = line.Unit,
                    Quantity = group.Sum(item => item.Qty - item.FulfilledQty),
                    UnitPrice = line.UnitPrice,
                    DiscountPct = line.DiscountPct,
                    WarehouseId = receiptWarehouseId
                };
            })
            .ToList();
        if (rows.Count == 0)
        {
            ShowWarningMessage(T("OrderDoc.NoRemainingReceipt"));
            return;
        }
        if (rows.Any(line => line.MaterialId is null))
        {
            ShowWarningMessage(T("OrderDoc.MaterialRequiredForReceipt"));
            return;
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            ItemsSource = rows,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            Margin = new Thickness(0, 0, 0, 10)
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Code"),
            Binding = new Binding(nameof(ReceiptLineVm.ItemCode)),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Description"),
            Binding = new Binding(nameof(ReceiptLineVm.Description)),
            IsReadOnly = true,
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Unit"),
            Binding = new Binding(nameof(ReceiptLineVm.Unit)),
            IsReadOnly = true,
            Width = 75
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Qty"),
            Binding = new Binding(nameof(ReceiptLineVm.Quantity))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                StringFormat = "0.###"
            },
            Width = 110
        });

        var dialog = new Window
        {
            Owner = this,
            Title = T("OrderDoc.CreateReceipt"),
            Width = 760,
            Height = 420,
            MinWidth = 560,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };
        var content = new DockPanel { Margin = new Thickness(12) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var confirm = new Button { Content = T("Buttons.Save"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = T("Buttons.Cancel"), MinWidth = 100, IsCancel = true };
        confirm.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            dialog.DialogResult = true;
        };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        content.Children.Add(grid);
        dialog.Content = content;
        if (dialog.ShowDialog() != true) return;

        var receiptLines = rows
            .Where(line => line.Quantity > 0m)
            .Select(line => new CommercialLineDraft(
                line.MaterialId, line.ItemCode, line.Description, line.Unit,
                line.Quantity, line.UnitPrice, line.DiscountPct, 0m, line.WarehouseId))
            .ToList();
        if (receiptLines.Count == 0)
        {
            ShowWarningMessage(T("OrderDoc.NoRemainingReceipt"));
            return;
        }

        try
        {
            var (_, receiptDocNo) = await AppHost.Orders.CreatePurchaseReceiptAsync(
                _companyId, _docId.Value, DateOnly.FromDateTime(DateTime.Today), receiptLines, CurrentUserId);
            _lblStatus.Text = $"{T("OrderDoc.ReceiptCreated")}: {receiptDocNo}";
            await RefreshProgressAsync(_docId.Value);
            UpdateEditState();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.Receive");
            ShowWarningMessage(ex.Message);
        }
    }

    private List<CommercialLineDraft>? CaptureSupplierInvoiceLines()
    {
        var taxEnabled = TaxSvc.IsEnabled;
        var rows = _lines
            .Where(line => line.FulfilledQty - line.BilledQty > 0m)
            .Select(line => new SupplierInvoiceLineVm
            {
                MaterialId = line.MaterialId,
                ItemCode = line.ItemCode,
                Description = line.Description,
                Unit = line.Unit,
                Quantity = line.FulfilledQty - line.BilledQty,
                UnitPrice = line.UnitPrice,
                DiscountPct = line.DiscountPct,
                TaxRate = taxEnabled ? line.TaxRate : 0m
            })
            .ToList();
        if (rows.Count == 0)
        {
            ShowWarningMessage(T("OrderDoc.NoReceivedToInvoice"));
            return null;
        }
        if (rows.Any(line => line.MaterialId is null))
        {
            ShowWarningMessage(T("OrderDoc.MaterialRequiredForReceipt"));
            return null;
        }

        long? warehouseId;
        try
        {
            warehouseId = CardStore.ResolveWarehouseId(_txtWhs.Text.Trim());
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.SupplierInvoiceWarehouse");
            ShowWarningMessage(ex.Message);
            return null;
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            ItemsSource = rows,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            Margin = new Thickness(0, 0, 0, 10)
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Code"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.ItemCode)),
            IsReadOnly = true,
            Width = 95
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Description"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.Description)),
            IsReadOnly = true,
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Unit"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.Unit)),
            IsReadOnly = true,
            Width = 60
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Qty"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.Quantity))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                StringFormat = "0.###"
            },
            Width = 85
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.PurchasePrice"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.UnitPrice))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                StringFormat = "0.####"
            },
            Width = 105
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.Discount"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.DiscountPct))
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                StringFormat = "0.##"
            },
            Width = 85
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("Invoice.Col.TaxRate"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.TaxRate))
            {
                StringFormat = "0.##"
            },
            IsReadOnly = true,
            Visibility = taxEnabled ? Visibility.Visible : Visibility.Collapsed,
            Width = 75
        });

        var dialog = new Window
        {
            Owner = this,
            Title = T("OrderDoc.CreateSupplierInvoice"),
            Width = 980,
            Height = 460,
            MinWidth = 720,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };
        var content = new DockPanel { Margin = new Thickness(12) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var confirm = new Button { Content = T("Buttons.Save"), MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = T("Buttons.Cancel"), MinWidth = 100, IsCancel = true };
        confirm.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            dialog.DialogResult = true;
        };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        content.Children.Add(grid);
        dialog.Content = content;
        if (dialog.ShowDialog() != true) return null;

        var result = rows
            .Where(line => line.Quantity > 0m)
            .Select(line => new CommercialLineDraft(
                line.MaterialId, line.ItemCode, line.Description, line.Unit,
                line.Quantity, line.UnitPrice, line.DiscountPct, line.TaxRate, warehouseId))
            .ToList();
        if (result.Count == 0)
        {
            ShowWarningMessage(T("OrderDoc.NoReceivedToInvoice"));
            return null;
        }
        return result;
    }

    private async Task SetStatusAsync(string newStatus, string permission)
    {
        if (!AppHost.Permissions.Has(permission))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (AppHost.Orders is null || _docId is null)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        var confirm = MessageBox.Show(this, T("OrderDoc.ConfirmStatusChange"), Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            if (newStatus == CommercialDocumentStatus.Closed)
                await AppHost.Orders.CloseOrderAsync(Side, _docId.Value, CurrentUserId);
            else
                await AppHost.Orders.CancelOrderAsync(Side, _docId.Value, CurrentUserId);
            _status = newStatus;
            UpdateEditState();
            RefreshTotals();
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    private void UpdateEditState()
    {
        var editable = IsEditable;
        _grid.IsReadOnly = !editable;
        _txtPartyCode.IsEnabled = editable;
        _txtPartyName.IsEnabled = editable;
        _dtDate.IsEnabled = editable;
        _dtDeliveryDate.IsEnabled = editable;
        _txtWhs.IsEnabled = editable;
        _txtNotes.IsEnabled = editable;
        _btnSave.IsEnabled = editable;
        _btnPost.IsEnabled = editable && _docId is not null;
        _btnConvert.IsEnabled = _status == CommercialDocumentStatus.Posted;
        _btnReceipt.IsEnabled = _kind == OrdKind.Purchase && _status == CommercialDocumentStatus.Posted && _docId is not null;
        _btnClose.IsEnabled = _status == CommercialDocumentStatus.Posted;
        _btnCancel.IsEnabled = _status is CommercialDocumentStatus.Draft or CommercialDocumentStatus.Posted;
        _btnAddLine.IsEnabled = editable;
        _btnRemLine.IsEnabled = editable;
    }

    private void Renumber()
    {
        for (var i = 0; i < _lines.Count; i++)
            _lines[i].LineNo = i + 1;
    }

    private void RefreshTotals()
    {
        var total = _lines.Sum(l => l.LineTotal);
        var tax = _lines.Sum(l => l.LineTotal * l.TaxRate / 100m);
        _lblTotal.Text = $"{T("Invoice.Total")}: {total + tax:N2} {UsrPref.Load().CurrencyCode}";
        _lblTotal.FontWeight = FontWeights.Bold;
        if (_status != CommercialDocumentStatus.Posted)
            _lblStatus.Text = $"{T("OrderDoc.Status")}: {StatusText(_status)}";
    }

    private static string StatusText(string status) => status switch
    {
        CommercialDocumentStatus.Posted => T("Invoice.StatusPosted"),
        CommercialDocumentStatus.Closed => T("OrderDoc.StatusClosed"),
        CommercialDocumentStatus.Cancelled => T("Invoice.StatusCancelled"),
        _ => T("Invoice.StatusDraft")
    };

    // Shows an already-localized literal message as a warning. Deliberately
    // NOT named ShowError: BaseWin.ShowError takes a localization KEY and
    // shows the error dialog — this helper takes final text (T(...) results,
    // exception messages) and shows the warning dialog.
    private void ShowWarningMessage(string message)
        => MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ------------------------------------------------------------- printing

    private void PrintOrder()
    {
        var page = BuildPrintPage();
        var paginator = new SimplePaginator(page, new Size(793, 1122));
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, string.IsNullOrWhiteSpace(_txtDocNo.Text) ? Title : _txtDocNo.Text.Trim());
    }

    private Border BuildPrintPage()
    {
        var panel = new StackPanel { Margin = new Thickness(48) };
        panel.Children.Add(new TextBlock
        {
            Text = AppState.CurrentCompanyDisplayName,
            FontSize = 13,
            Opacity = 0.75
        });
        panel.Children.Add(new TextBlock
        {
            Text = T(_kind == OrdKind.Sales ? "OrderDoc.TitleSales" : "OrderDoc.TitlePurchase"),
            FontSize = 20,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock { Text = $"{T("OrderDoc.OrderNo")}: {_txtDocNo.Text}    {T("OrderDoc.Date")}: {_dtDate.SelectedDate:yyyy-MM-dd}    {T("OrderDoc.DeliveryDate")}: {_dtDeliveryDate.SelectedDate:yyyy-MM-dd}", Margin = new Thickness(0, 8, 0, 2) });
        panel.Children.Add(new TextBlock { Text = $"{T("OrderDoc.Partner")}: {_txtPartyCode.Text} - {_txtPartyName.Text}", Margin = new Thickness(0, 2, 0, 12) });

        var table = new Grid();
        foreach (var width in new[] { 40.0, 110.0, 240.0, 60.0, 70.0, 90.0, 100.0 })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        AddPrintRow(table, 0, "#", T("Invoice.Col.Code"), T("Invoice.Col.Description"), T("Invoice.Col.Unit"), T("Invoice.Col.Qty"), T(_kind == OrdKind.Sales ? "Invoice.Col.SalePrice" : "Invoice.Col.PurchasePrice"), T("Invoice.Col.LineTotal"), header: true);
        var row = 1;
        foreach (var line in _lines)
        {
            AddPrintRow(table, row++, line.LineNo.ToString(CultureInfo.InvariantCulture), line.ItemCode, line.Description, line.Unit,
                line.Qty.ToString("0.###", CultureInfo.InvariantCulture), line.UnitPrice.ToString("N2", CultureInfo.InvariantCulture), line.LineTotal.ToString("N2", CultureInfo.InvariantCulture));
        }
        panel.Children.Add(table);
        panel.Children.Add(new TextBlock { Text = _lblTotal.Text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
        panel.Children.Add(new TextBlock { Text = $"{T("OrderDoc.Status")}: {StatusText(_status)}", Margin = new Thickness(0, 24, 0, 0), Opacity = 0.7 });

        return new Border { Background = Brushes.White, Child = panel, Width = 793, Height = 1122 };
    }

    private static void AddPrintRow(Grid table, int rowIndex, string c0, string c1, string c2, string c3, string c4, string c5, string c6, bool header = false)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var values = new[] { c0, c1, c2, c3, c4, c5, c6 };
        for (var i = 0; i < values.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = values[i],
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(2, 3, 2, 3),
                FontSize = 12
            };
            Grid.SetRow(cell, rowIndex);
            Grid.SetColumn(cell, i);
            table.Children.Add(cell);
        }
    }

    private sealed class SimplePaginator : DocumentPaginator
    {
        private readonly FrameworkElement _page;
        private readonly Size _size;

        public SimplePaginator(FrameworkElement page, Size size)
        {
            _page = page;
            _size = size;
            _page.Measure(size);
            _page.Arrange(new Rect(size));
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => 1;
        public override Size PageSize { get => _size; set { } }
        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
            => new(new Border { Background = Brushes.White, Child = _page }, _size, new Rect(_size), new Rect(_size));
    }

    protected override void ApplyLocalizedText()
    {
        Title = T(_kind == OrdKind.Sales ? "OrderDoc.TitleSales" : "OrderDoc.TitlePurchase");
        if (_grid.Columns.Count >= 11)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("Invoice.Col.Code");
            _grid.Columns[2].Header = T("Invoice.Col.Description");
            _grid.Columns[3].Header = T("Invoice.Col.Unit");
            _grid.Columns[4].Header = T("Invoice.Col.Qty");
            _grid.Columns[5].Header = T(_kind == OrdKind.Sales ? "Invoice.Col.SalePrice" : "Invoice.Col.PurchasePrice");
            _grid.Columns[6].Header = T("Invoice.Col.Discount");
            _grid.Columns[7].Header = T("Invoice.Col.TaxRate");
            _grid.Columns[8].Header = T("Invoice.Col.LineTotal");
            _grid.Columns[9].Header = T("OrderDoc.Fulfilled");
            _grid.Columns[10].Header = T("OrderDoc.Billed");
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


    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 8, 0);
        btn.MinWidth = VisuId.ButtonMinWidth;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private static DataGridTextColumn TextCol(string path, double width, bool readOnly = false)
        => new()
        {
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width,
            IsReadOnly = readOnly
        };

    private sealed class SupplierInvoiceLineVm
    {
        public long? MaterialId { get; init; }
        public string ItemCode { get; init; } = "";
        public string Description { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal DiscountPct { get; set; }
        public decimal TaxRate { get; set; }
    }

    private sealed class ReceiptLineVm
    {
        public long? MaterialId { get; init; }
        public string ItemCode { get; init; } = "";
        public string Description { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; init; }
        public decimal DiscountPct { get; init; }
        public long? WarehouseId { get; init; }
    }

    private sealed class OrderLineVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _itemCode = "", _desc = "", _unit = "";
        private decimal _qty = 1, _price, _disc, _tax;
        private decimal _fulfilled, _billed;

        public long? MaterialId { get; set; }
        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(); } }
        public string Description { get => _desc; set { _desc = value; OnChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnChanged(); } }

        public decimal Qty { get => _qty; set { _qty = value; OnChanged(nameof(QtyText)); OnChanged(nameof(LineTotalText)); } }
        public decimal UnitPrice { get => _price; set { _price = value; OnChanged(nameof(PriceText)); OnChanged(nameof(LineTotalText)); } }
        public decimal DiscountPct { get => _disc; set { _disc = value; OnChanged(nameof(DiscountText)); OnChanged(nameof(LineTotalText)); } }
        public decimal TaxRate { get => _tax; set { _tax = value; OnChanged(nameof(TaxText)); OnChanged(nameof(LineTotalText)); } }
        public decimal FulfilledQty { get => _fulfilled; set { _fulfilled = value; OnChanged(nameof(FulfilledText)); } }
        public decimal BilledQty { get => _billed; set { _billed = value; OnChanged(nameof(BilledText)); } }

        public decimal LineTotal => _qty * _price * (1m - _disc / 100m);
        public string LineTotalText => LineTotal.ToString("N2", CultureInfo.InvariantCulture);
        public string FulfilledText => _fulfilled.ToString("0.###", CultureInfo.InvariantCulture);
        public string BilledText => _billed.ToString("0.###", CultureInfo.InvariantCulture);

        public string QtyText
        {
            get => _qty.ToString("0.###", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Qty = v; }
        }

        public string PriceText
        {
            get => _price.ToString("0.####", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) UnitPrice = v; }
        }

        public string DiscountText
        {
            get => _disc.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) DiscountPct = v; }
        }

        public string TaxText
        {
            get => _tax.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) TaxRate = v; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
