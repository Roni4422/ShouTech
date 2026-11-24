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
using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

public enum OrdKind
{
    Sales,
    Purchase,
    RequestForQuotation
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
    // P3 (purchase kinds only): currency + rate, payment terms, incoterm.
    private readonly TextBox _txtCurrency = new();
    private readonly TextBox _txtRate = new();
    private readonly TextBox _txtPaymentTerms = new();
    private readonly TextBox _txtIncoterm = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnConvert = new();
    private readonly Button _btnReceipt = new();
    private readonly Button _btnAttachments = new();
    /// <summary>W23 approval workflow (migration 098) for sales orders:
    /// submit / approve / reject through OrderService; posting enforces
    /// the company approval flag in the repository (SAL-7016).</summary>
    private readonly Button _btnSubmitApproval = new();
    private readonly Button _btnApproveDoc = new();
    private readonly Button _btnRejectDoc = new();
    private string _approvalState = SalesApprovalState.NotRequired;
    private readonly Button _btnRequisitions = new();
    private readonly Button _btnClose = new();
    private readonly Button _btnCancel = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemLine = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnCompareQuotes = new();
    private readonly Button _btnAddSupplierQuote = new();
    private readonly Button _btnAmend = new();
    private readonly Button _btnInvitations = new();

    private bool _isReceiving;
    private bool _isConverting;

    private readonly TextBlock _lblTotal = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<OrderLineVm> _lines = new();

    private long? _docId;
    private long? _partyId;
    private string _status = CommercialDocumentStatus.Draft;
    private string? _cancelReason;
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

    private bool IsRfq => _kind == OrdKind.RequestForQuotation;

    private void OpenQuotationComparison()
    {
        if (_docId is null) return;
        var window = new SupplierQuotationCompareWin(_docId.Value) { Owner = this };
        window.ShowDialog();
    }

    private string DocKindCode => _kind switch
    {
        OrdKind.Sales => CommercialDocKinds.SalesOrder,
        OrdKind.RequestForQuotation => CommercialDocKinds.RequestForQuotation,
        _ => CommercialDocKinds.PurchaseOrder
    };
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
        StyleButton(_btnAttachments, "PurchaseAttachments.Open");
        StyleButton(_btnRequisitions, "PurchaseRequisition.Open");
        _btnRequisitions.Visibility = _kind == OrdKind.Purchase ? Visibility.Visible : Visibility.Collapsed;
        // W23: attachments generalized (migration 098) — sales orders use
        // the SALDOC owner on the same store; the button now serves both.
        StyleButton(_btnSubmitApproval, "SalesDoc.SubmitApproval");
        StyleButton(_btnApproveDoc, "SalesDoc.Approve");
        StyleButton(_btnRejectDoc, "SalesDoc.Reject");
        _btnSubmitApproval.Visibility = _kind == OrdKind.Sales ? Visibility.Visible : Visibility.Collapsed;
        _btnApproveDoc.Visibility = _kind == OrdKind.Sales ? Visibility.Visible : Visibility.Collapsed;
        _btnRejectDoc.Visibility = _kind == OrdKind.Sales ? Visibility.Visible : Visibility.Collapsed;
        _btnSubmitApproval.Click += async (_, _) => await SubmitApprovalAsync();
        _btnApproveDoc.Click += async (_, _) => await ApproveDocumentAsync();
        _btnRejectDoc.Click += async (_, _) => await RejectDocumentAsync();
        _btnReceipt.Visibility = _kind == OrdKind.Purchase ? Visibility.Visible : Visibility.Collapsed;
        _btnConvert.Visibility = IsRfq ? Visibility.Collapsed : Visibility.Visible;
        StyleButton(_btnClose, "OrderDoc.CloseOrder");
        StyleButton(_btnCancel, "OrderDoc.CancelOrder");
        StyleButton(_btnAmend, "OrderDoc.Amend");
        _btnAmend.Visibility = _kind == OrdKind.Purchase ? Visibility.Visible : Visibility.Collapsed;
        _btnAmend.Click += async (_, _) => await AmendAsync();
        StyleButton(_btnInvitations, "OrderDoc.Invitations");
        _btnInvitations.Visibility = IsRfq ? Visibility.Visible : Visibility.Collapsed;
        _btnInvitations.Click += async (_, _) => await ShowInvitationsAsync();
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemLine, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnCompareQuotes, "Purchasing.SupplierQuotationComparison.Open");
        StyleButton(_btnAddSupplierQuote, "Purchasing.SupplierQuotationEntry.Open");
        _btnAddSupplierQuote.Visibility = IsRfq ? Visibility.Visible : Visibility.Collapsed;
        _btnAddSupplierQuote.Click += (_, _) =>
        {
            if (_docId is null || _status != CommercialDocumentStatus.Posted)
            {
                ShowWarningMessage(T("Purchasing.SupplierQuotationComparison.PostRfqFirst"));
                return;
            }
            var entry = new SupplierQuotationEntryWin(_docId.Value) { Owner = this };
            if (entry.ShowDialog() == true)
                OpenQuotationComparison();
        };
        _btnCompareQuotes.Visibility = IsRfq ? Visibility.Visible : Visibility.Collapsed;
        _btnCompareQuotes.Click += (_, _) =>
        {
            if (_docId is null || _status != CommercialDocumentStatus.Posted)
            {
                ShowWarningMessage(T("Purchasing.SupplierQuotationComparison.PostRfqFirst"));
                return;
            }
            var window = new SupplierQuotationCompareWin(_docId.Value) { Owner = this };
            window.ShowDialog();
        };

        _btnNew.Click += async (_, _) => await ResetOrderAsync();
        _btnLoad.Click += async (_, _) => await LoadByNumberAsync();
        _btnSave.Click += async (_, _) => await SaveAsync();
        _btnPost.Click += async (_, _) => await PostAsync();
        _btnConvert.Click += async (_, _) => await ConvertAsync();
        _btnReceipt.Click += async (_, _) => await ReceiveAsync();
        _btnAttachments.Click += (_, _) => OpenAttachments();
        _btnRequisitions.Click += (_, _) => new PurchaseRequisitionWin { Owner = this }.ShowDialog();
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

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnPost, _btnConvert, _btnReceipt, _btnAttachments, _btnSubmitApproval, _btnApproveDoc, _btnRejectDoc, _btnRequisitions, _btnAddSupplierQuote, _btnCompareQuotes, _btnInvitations, _btnClose, _btnCancel, _btnAmend, _btnAddLine, _btnRemLine, _btnPrint })
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
        AddFormField(headerForm, 0, 2, IsRfq ? "OrderDoc.ValidTill" : "OrderDoc.DeliveryDate", _dtDeliveryDate);
        AddFormField(headerForm, 0, 3, _kind == OrdKind.Sales ? "Invoice.CustomerCode" : "Invoice.SupplierCode", _txtPartyCode);
        AddFormField(headerForm, 1, 0, _kind == OrdKind.Sales ? "Invoice.CustomerName" : "Invoice.SupplierName", _txtPartyName, spanPairs: 2);
        AddFormField(headerForm, 1, 2, "Invoice.Warehouse", _txtWhs);
        AddFormField(headerForm, 2, 0, "Invoice.Notes", _txtNotes, spanPairs: 4);
        if (_kind != OrdKind.Sales)
        {
            // P3: PO/RFQ header completion — currency + exchange rate
            // are editable here (previously forced to the user-pref
            // currency with rate 0), plus payment terms and incoterm.
            AddFormField(headerForm, 3, 0, "Invoice.Currency", _txtCurrency);
            AddFormField(headerForm, 3, 1, "OrderDoc.ExchangeRate", _txtRate);
            AddFormField(headerForm, 3, 2, "OrderDoc.PaymentTerms", _txtPaymentTerms);
            AddFormField(headerForm, 3, 3, "OrderDoc.Incoterm", _txtIncoterm);
        }
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
        (long? Id, string Name)? found;
        try
        {
            found = TaxSvc.ResolveParty(_txtPartyCode.Text.Trim(), _kind == OrdKind.Sales);
        }
        catch (InvalidOperationException ex)
        {
            // W1: blacklisted party — clear rejection, party not applied.
            ShowWarningMessage(ex.Message);
            _partyId = null;
            return;
        }
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

    private void ResolveMaterial(OrderLineVm line)
    {
        var found = TaxSvc.ResolveMaterial(line.ItemCode.Trim());
        if (found is { } material)
        {
            line.MaterialId = material.Id;
            if (string.IsNullOrWhiteSpace(line.Description)) line.Description = material.Name;
            if (line.UnitPrice <= 0m) line.UnitPrice = material.Price;
            if (string.IsNullOrWhiteSpace(line.Unit) || line.Unit == "PCS") line.Unit = string.IsNullOrWhiteSpace(material.Unit) ? line.Unit : material.Unit;
            if (line.TaxRate <= 0m) line.TaxRate = material.TaxRate;

            // W7a: sales lines consult the ONE price resolver (contract
            // hook -> customer price -> price list by party tier -> tier
            // column -> material card); the provenance is shown in the
            // status line so the user can see where the price came from.
            if (_kind == OrdKind.Sales)
            {
                var resolution = TaxSvc.ResolveSalesPrice(
                    _txtPartyCode.Text.Trim(), material.Id, line.ItemCode.Trim(),
                    line.Qty <= 0m ? 1m : line.Qty, line.Unit);
                if (resolution is not null)
                {
                    if (resolution.Price > 0m) line.UnitPrice = resolution.Price;
                    if (resolution.DiscountPct > line.DiscountPct) line.DiscountPct = resolution.DiscountPct;
                    _lblStatus.Text = T("Pricing.Resolved")
                        .Replace("{0}", resolution.Price.ToString("0.####", CultureInfo.InvariantCulture))
                        .Replace("{1}", resolution.Source);
                }
            }
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
                : (IsRfq ? "RFQ-" : _kind == OrdKind.Sales ? "SO-" : "PO-") + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            _dtDate.SelectedDate = DateTime.Today;
            _dtDeliveryDate.SelectedDate = DateTime.Today.AddDays(7);
            _txtPartyCode.Text = string.Empty;
            _txtPartyName.Text = string.Empty;
            _txtNotes.Text = string.Empty;
            _txtCurrency.Text = UsrPref.Load().CurrencyCode;
            _txtRate.Text = string.Empty;
            _txtPaymentTerms.Text = string.Empty;
            _txtIncoterm.Text = string.Empty;
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
        if (!IsRfq && doc.Status == CommercialDocumentStatus.Posted)
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
            _approvalState = doc.ApprovalState;
            _cancelReason = doc.CancelReason;
            _txtDocNo.Text = doc.DocNo;
            _dtDate.SelectedDate = doc.DocDate.ToDateTime(TimeOnly.MinValue);
            _dtDeliveryDate.SelectedDate = (IsRfq ? doc.ValidTill : doc.DeliveryDate)?.ToDateTime(TimeOnly.MinValue);
            _txtPartyCode.Text = doc.PartyCode ?? string.Empty;
            _txtPartyName.Text = doc.PartyName ?? string.Empty;
            _txtWhs.Text = doc.WarehouseName ?? string.Empty;
            _txtNotes.Text = doc.Notes ?? string.Empty;
            _txtCurrency.Text = doc.CurrencyCode;
            _txtRate.Text = doc.ExchangeRate > 0m ? doc.ExchangeRate.ToString(CultureInfo.InvariantCulture) : string.Empty;
            _txtPaymentTerms.Text = doc.PaymentTerms ?? string.Empty;
            _txtIncoterm.Text = doc.Incoterm ?? string.Empty;
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
                // Progress is keyed by source line; the material match is
                // only a fallback for rows loaded before line identity.
                var match = progress.Lines.FirstOrDefault(p => p.LineNo == line.LineNo)
                    ?? progress.Lines.FirstOrDefault(p =>
                        (p.MaterialId is not null && p.MaterialId == line.MaterialId) ||
                        (p.MaterialId is null && p.MaterialCode == line.ItemCode));
                if (match is not null)
                {
                    line.FulfilledQty = match.FulfilledQty;
                    line.BilledQty = match.BilledQty;
                }
            }
            // Sales orders show the localized derived chain status in
            // the status slot; purchase orders keep the raw status and
            // ride the P5 derived PO status (from the stored
            // percentages) as its own segment.
            var statusText = _kind == OrdKind.Purchase ? StatusText(_status) : DerivedStatusText(progress.DerivedStatus);
            var derived = _kind == OrdKind.Purchase ? $" | {T("OrderDoc.DerivedStatus")}: {progress.DerivedStatus}" : string.Empty;
            _lblStatus.Text = $"{T("OrderDoc.Status")}: {statusText} | {T("OrderDoc.Fulfilled")}: {progress.PerFulfilled:N1}% | {T("OrderDoc.Billed")}: {progress.PerBilled:N1}%{derived}";
            // Supplier advances (Y1, migration 110): a PO shows its
            // advance total / consumed / remaining, read from the
            // voucher links + advance allocations (display only).
            if (_kind == OrdKind.Purchase && !IsRfq)
            {
                var advances = PartyLedgerSvc.GetPoAdvanceSummary(docId);
                if (advances.Total > 0m)
                    _lblStatus.Text += $" | {Tf("OrderDoc.AdvancesSummary", advances.Total.ToString("N2", CultureInfo.InvariantCulture), advances.Consumed.ToString("N2", CultureInfo.InvariantCulture), advances.Remaining.ToString("N2", CultureInfo.InvariantCulture))}";
            }
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
            // W7a: an authorized price/discount override holder saves
            // below-limit lines with an audit row instead of a block
            // (the context is null for everyone else — hard block).
            var priceOverride = _kind == OrdKind.Sales
                && (AppHost.Permissions.Has(PermCat.Sales.PriceOverride)
                    || AppHost.Permissions.Has(PermCat.Sales.DiscountOverride))
                ? new ShouTech.Domain.Entities.PriceOverrideContext(CurrentUserId,
                    AppHost.Permissions.Has(PermCat.Sales.PriceOverride),
                    AppHost.Permissions.Has(PermCat.Sales.DiscountOverride))
                : null;
            if (_docId is null)
            {
                _docId = await AppHost.Orders.CreateOrderAsync(draft, CurrentUserId, priceOverride);
            }
            else
            {
                await AppHost.Orders.UpdateOrderAsync(_docId.Value, draft, CurrentUserId, priceOverride);
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
        if (!IsRfq && string.IsNullOrWhiteSpace(_txtPartyCode.Text) && string.IsNullOrWhiteSpace(_txtPartyName.Text))
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

        // P3: purchase kinds take currency + rate from the header
        // fields (rate empty = Step 7 convention 0 = master fallback,
        // never fabricated to 1). Sales keeps the user-pref currency.
        var currency = _kind == OrdKind.Sales
            ? UsrPref.Load().CurrencyCode
            : string.IsNullOrWhiteSpace(_txtCurrency.Text) ? UsrPref.Load().CurrencyCode : _txtCurrency.Text.Trim().ToUpperInvariant();
        var rate = 0m;
        if (_kind != OrdKind.Sales && !string.IsNullOrWhiteSpace(_txtRate.Text)
            && !decimal.TryParse(_txtRate.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out rate))
            throw new InvalidOperationException(T("OrderDoc.ExchangeRate"));
        var deliveryOrValid = _dtDeliveryDate.SelectedDate is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        return new CommercialDocumentDraft(
            _companyId,
            DocKindCode,
            _txtDocNo.Text.Trim(),
            DateOnly.FromDateTime(_dtDate.SelectedDate ?? DateTime.Today),
            _partyId,
            string.IsNullOrWhiteSpace(_txtPartyName.Text) ? null : _txtPartyName.Text.Trim(),
            warehouseId,
            currency,
            rate,
            string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim(),
            lines,
            string.IsNullOrWhiteSpace(_txtPartyCode.Text) ? null : _txtPartyCode.Text.Trim(),
            IsRfq ? null : deliveryOrValid,
            IsRfq ? deliveryOrValid : null,
            null,
            null,
            null,
            string.IsNullOrWhiteSpace(_txtWhs.Text) ? null : _txtWhs.Text.Trim(),
            // P4: the header delivery/required-by date defaults the
            // per-line required-by (the repository snapshots factors).
            RequiredByDate: _kind == OrdKind.Purchase ? deliveryOrValid : null,
            PaymentTerms: _kind == OrdKind.Sales || string.IsNullOrWhiteSpace(_txtPaymentTerms.Text) ? null : _txtPaymentTerms.Text.Trim(),
            Incoterm: _kind == OrdKind.Sales || string.IsNullOrWhiteSpace(_txtIncoterm.Text) ? null : _txtIncoterm.Text.Trim());
    }

    private async Task PostAsync()
    {
        var permission = _kind == OrdKind.Sales ? PermKey.SalesPost : PermKey.PurchPost;
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
            await AppHost.Orders.PostOrderAsync(Side, _docId.Value, CurrentUserId);
            _status = CommercialDocumentStatus.Posted;
            UpdateEditState();
            RefreshTotals();
            if (!IsRfq) await RefreshProgressAsync(_docId.Value);
        }
        catch (ShouTech.Domain.AppError ex) when ((ex.Code is ShouTech.Domain.ErrorCodes.PriceBelowMinimum
                or ShouTech.Domain.ErrorCodes.MarginBelowMinimum
                or ShouTech.Domain.ErrorCodes.DiscountExceedsLimit)
            && _kind == OrdKind.Sales
            && AppHost.Permissions.Has(PermCat.Sales.PriceOverride))
        {
            // W7a: the posting user holds SALES.PRICE.OVERRIDE — offer
            // the audited override; the guard records actor + values in
            // spr_price_overrides on the retry.
            if (MessageBox.Show(this, $"{ex.Message}\n\n{T("Pricing.OverrideConfirm")}", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                await AppHost.Orders.PostOrderAsync(Side, _docId.Value, CurrentUserId, null,
                    new ShouTech.Domain.Entities.PriceOverrideContext(CurrentUserId,
                        AppHost.Permissions.Has(PermCat.Sales.PriceOverride),
                        AppHost.Permissions.Has(PermCat.Sales.DiscountOverride)));
                _status = CommercialDocumentStatus.Posted;
                UpdateEditState();
                RefreshTotals();
                if (!IsRfq) await RefreshProgressAsync(_docId.Value);
            }
            catch (Exception retryEx)
            {
                ShowWarningMessage(retryEx.Message);
            }
        }
        catch (ShouTech.Domain.AppError ex) when (ex.Code == ShouTech.Domain.ErrorCodes.CreditLimitExceeded
            && _kind == OrdKind.Sales
            && AppHost.Permissions.Has(PermCat.Sales.CreditOverride))
        {
            // W1: the posting user holds SALES.CREDIT.OVERRIDE — offer the
            // audited override instead of a dead-end block. The guard
            // records actor + amounts in credit_overrides on the retry.
            if (MessageBox.Show(this, $"{ex.Message}\n\n{T("OrderDoc.CreditOverrideConfirm")}", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                await AppHost.Orders.PostOrderAsync(Side, _docId.Value, CurrentUserId,
                    new ShouTech.Domain.Entities.CreditOverrideContext(CurrentUserId, null, null));
                _status = CommercialDocumentStatus.Posted;
                UpdateEditState();
                RefreshTotals();
                if (!IsRfq) await RefreshProgressAsync(_docId.Value);
            }
            catch (Exception retryEx)
            {
                ShowWarningMessage(retryEx.Message);
            }
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    private async Task ConvertAsync()
    {
        var canConvert = _kind == OrdKind.Sales
            ? AppHost.Permissions.Has(PermCat.Sales.OrderConvert)
            : AppHost.Permissions.HasAll(
                PermCat.Purchasing.OrderConvert,
                PermCat.Purchasing.InvoiceCreate,
                PermCat.Purchasing.InvoicePost);
        if (!canConvert)
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (IsRfq)
        {
            ShowWarningMessage(T("OrderDoc.RfqCannotConvert"));
            return;
        }
        if (AppHost.Orders is null || _docId is null)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        if (_isConverting || _isReceiving) return;
        _isConverting = true;
        UpdateEditState();
        try
        {
            string invoiceDocNo;
            if (_kind == OrdKind.Purchase)
            {
                var captured = CaptureSupplierInvoiceLines();
                if (captured is null) return;
                var (supplierInvoiceLines, supplierBillNo, billDate) = captured.Value;
                var result = await AppHost.Orders.CreatePurchaseInvoiceAsync(
                    _companyId, _docId.Value, DateOnly.FromDateTime(DateTime.Today),
                    supplierInvoiceLines, CurrentUserId, supplierBillNo, billDate);
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
        finally
        {
            _isConverting = false;
            UpdateEditState();
        }
    }

    private void OpenAttachments()
    {
        if (_docId is null || _companyId == Guid.Empty)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        // W23: purchase orders keep the legacy owner; sales orders open
        // the generalized SALDOC owner on the same attachment store.
        if (_kind == OrdKind.Sales)
        {
            new PurchaseAttachmentsWin(_companyId, AttachmentOwnerKind.SalesDocument,
                _docId.Value.ToString(CultureInfo.InvariantCulture), CurrentUserId)
            {
                Owner = this
            }.ShowDialog();
            return;
        }
        new PurchaseAttachmentsWin(_companyId, _docId.Value, CurrentUserId)
        {
            Owner = this
        }.ShowDialog();
    }

    // ------------------------------------------- approval workflow (W23)

    private async Task SubmitApprovalAsync()
    {
        if (AppHost.Orders is null || _docId is null)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        try
        {
            await AppHost.Orders.SubmitSalesForApprovalAsync(_docId.Value, CurrentUserId);
            _approvalState = SalesApprovalState.Pending;
            UpdateEditState();
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    private async Task ApproveDocumentAsync()
    {
        if (AppHost.Orders is null || _docId is null)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        try
        {
            await AppHost.Orders.ApproveSalesDocumentAsync(_docId.Value, CurrentUserId, AppHost.Permissions.Has);
            _approvalState = SalesApprovalState.Approved;
            UpdateEditState();
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    private async Task RejectDocumentAsync()
    {
        if (AppHost.Orders is null || _docId is null)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }
        var input = PromptDialog.Show(this, T("SalesDoc.RejectReason"),
            (T("SalesDoc.RejectReason"), string.Empty));
        var reason = input is { Length: > 0 } ? input[0] : null;
        if (string.IsNullOrWhiteSpace(reason))
        {
            ShowWarningMessage(T("SalesDoc.RejectReasonRequired"));
            return;
        }
        try
        {
            await AppHost.Orders.RejectSalesDocumentAsync(_docId.Value, CurrentUserId, reason, AppHost.Permissions.Has);
            _approvalState = SalesApprovalState.Rejected;
            UpdateEditState();
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    private async Task ReceiveAsync()
    {
        if (_kind != OrdKind.Purchase || !AppHost.Permissions.Has(PermKey.PurchPost))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        if (AppHost.Orders is null || _docId is null || _status != CommercialDocumentStatus.Posted)
        {
            ShowWarningMessage(T("OrderDoc.SaveFirst"));
            return;
        }

        if (_isReceiving || _isConverting) return;
        _isReceiving = true;
        UpdateEditState();
        try
        {
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
                .Select(line => new ReceiptLineVm
                {
                    SourceLineNo = line.LineNo,
                    MaterialId = line.MaterialId,
                    ItemCode = line.ItemCode,
                    Description = line.Description,
                    Unit = line.Unit,
                    Quantity = line.Qty - line.FulfilledQty,
                    UnitPrice = line.UnitPrice,
                    DiscountPct = line.DiscountPct,
                    WarehouseId = receiptWarehouseId
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
                Header = T("OrderDoc.PurchaseOrderLine"),
                Binding = new Binding(nameof(ReceiptLineVm.SourceLineNo)),
                IsReadOnly = true,
                Width = 70
            });
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
            // Q2: rejected qty + reason per line (recorded on the
            // receipt line; never counts as received).
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = T("OrderDoc.RejectedQty"),
                Binding = new Binding(nameof(ReceiptLineVm.RejectedQty))
                {
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                    StringFormat = "0.###"
                },
                Width = 95
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = T("OrderDoc.RejectReason"),
                Binding = new Binding(nameof(ReceiptLineVm.RejectReason))
                {
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                Width = 150
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
            // Q3: the receipt date is user-settable (default today) and
            // the supplier delivery-note number is captured here and
            // persisted on the receipt header.
            var receiptDatePicker = new DatePicker { SelectedDate = DateTime.Today, MinWidth = 130, Margin = new Thickness(0, 0, 12, 0) };
            var deliveryNoteBox = new TextBox { MinWidth = 170 };
            var receiptHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            receiptHeader.Children.Add(new TextBlock { Text = T("OrderDoc.ReceiptDate"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            receiptHeader.Children.Add(receiptDatePicker);
            receiptHeader.Children.Add(new TextBlock { Text = T("OrderDoc.DeliveryNoteNo"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            receiptHeader.Children.Add(deliveryNoteBox);
            DockPanel.SetDock(receiptHeader, Dock.Top);
            content.Children.Add(receiptHeader);
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
                    line.Quantity, line.UnitPrice, line.DiscountPct, 0m, line.WarehouseId, line.SourceLineNo,
                    RejectedQty: line.RejectedQty,
                    RejectReason: string.IsNullOrWhiteSpace(line.RejectReason) ? null : line.RejectReason.Trim()))
                .ToList();
            if (receiptLines.Count == 0)
            {
                ShowWarningMessage(T("OrderDoc.NoRemainingReceipt"));
                return;
            }

            // Q3: the receipt posts on the date the user picked in the
            // dialog (default today), not a forced today.
            var captureDate = DateOnly.FromDateTime(receiptDatePicker.SelectedDate ?? DateTime.Today);
            var deliveryNoteNo = string.IsNullOrWhiteSpace(deliveryNoteBox.Text) ? null : deliveryNoteBox.Text.Trim();
            var captureDoc = new InvDoc
            {
                Kind = InvDocKind.StockIn,
                DocDate = captureDate.ToDateTime(TimeOnly.MinValue),
                WarehouseId = receiptWarehouseId is long selectedWarehouse ? checked((int)selectedWarehouse) : null,
                Lines = receiptLines.Select((line, index) => new InvLine
                {
                    LineNo = line.SourceLineNo ?? index + 1,
                    MaterialId = line.MaterialId,
                    MaterialCode = line.MaterialCode ?? string.Empty,
                    Description = line.Description,
                    Unit = line.Unit ?? string.Empty,
                    Qty = line.Qty,
                    UnitPrice = line.UnitPrice,
                    DiscountPct = line.DiscountPct,
                    WarehouseId = line.WarehouseId is long lineWarehouse ? checked((int)lineWarehouse) : null
                }).ToList()
            };
            var captures = BatchSerialCaptureSvc.CaptureForPosting(captureDoc, this);
            if (captures is null) return;
            receiptLines = receiptLines.Select(line =>
            {
                var capture = line.SourceLineNo is int sourceLine && captures.TryGetValue(sourceLine, out var found)
                    ? found
                    : null;
                return capture is null ? line : line with
                {
                    BatchAllocations = capture.Batches.Select(batch => new CommercialBatchAllocationDraft(
                        batch.BatchNo, batch.Qty, batch.ExpiryDate, batch.MfgDate, batch.SupplierBatchNo)).ToArray(),
                    Serials = capture.Serials.Select(serial => new CommercialSerialSelectionDraft(
                        serial.SerialNo, serial.BatchNo, serial.WarrantyExpiry)).ToArray(),
                    AllowExpiredBatchIssue = capture.AllowExpiredBatchIssue
                };
            }).ToList();

            try
            {
                var (_, receiptDocNo) = await AppHost.Orders.CreatePurchaseReceiptAsync(
                    _companyId, _docId.Value, captureDate, receiptLines, CurrentUserId, deliveryNoteNo);
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
        finally
        {
            _isReceiving = false;
            UpdateEditState();
        }
    }

    private (List<CommercialLineDraft> Lines, string? SupplierBillNo, DateOnly? BillDate)? CaptureSupplierInvoiceLines()
    {
        var taxEnabled = TaxSvc.IsEnabled;
        var rows = _lines
            .Where(line => line.FulfilledQty - line.BilledQty > 0m)
            .Select(line => new SupplierInvoiceLineVm
            {
                MaterialId = line.MaterialId,
                LineNo = line.LineNo,
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
            Header = T("OrderDoc.PurchaseOrderLine"),
            Binding = new Binding(nameof(SupplierInvoiceLineVm.LineNo)),
            IsReadOnly = true,
            Width = 65
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
            // Screen-fit cap (verify_screenfit.py): <= 684 wide. The grid
            // fills the dialog and scrolls horizontally at the minimum if
            // its fixed columns exceed the available width.
            MinWidth = 684,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };
        var content = new DockPanel { Margin = new Thickness(12) };
        // I1: the supplier's own bill number + bill date are captured
        // here and persisted on the chain invoice (uniqueness enforced
        // with PUR-1011 in the repository).
        var billNoBox = new TextBox { MinWidth = 160, Margin = new Thickness(0, 0, 12, 0) };
        var billDatePicker = new DatePicker { SelectedDate = DateTime.Today, MinWidth = 130 };
        var billHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        billHeader.Children.Add(new TextBlock { Text = T("OrderDoc.BillNo"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        billHeader.Children.Add(billNoBox);
        billHeader.Children.Add(new TextBlock { Text = T("OrderDoc.BillDate"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        billHeader.Children.Add(billDatePicker);
        DockPanel.SetDock(billHeader, Dock.Top);
        content.Children.Add(billHeader);
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
                line.Quantity, line.UnitPrice, line.DiscountPct, line.TaxRate, warehouseId, line.LineNo))
            .ToList();
        if (result.Count == 0)
        {
            ShowWarningMessage(T("OrderDoc.NoReceivedToInvoice"));
            return null;
        }
        return (result,
            string.IsNullOrWhiteSpace(billNoBox.Text) ? null : billNoBox.Text.Trim(),
            billDatePicker.SelectedDate is { } billDate ? DateOnly.FromDateTime(billDate) : null);
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
            {
                await AppHost.Orders.CloseOrderAsync(Side, _docId.Value, CurrentUserId);
            }
            else
            {
                // Documented cancellation (sales-cycle spec sections
                // 3/14): no cancellation without a recorded reason.
                var input = PromptDialog.Show(this, T("OrderDoc.CancelReason"),
                    (T("OrderDoc.CancelReason"), string.Empty));
                var reason = input is { Length: > 0 } ? input[0] : null;
                if (string.IsNullOrWhiteSpace(reason))
                {
                    ShowWarningMessage(T("OrderDoc.CancelReasonRequired"));
                    return;
                }
                await AppHost.Orders.CancelOrderAsync(Side, _docId.Value, CurrentUserId, reason: reason);
                _cancelReason = reason;
            }
            _status = newStatus;
            UpdateEditState();
            RefreshTotals();
        }
        catch (Exception ex)
        {
            ShowWarningMessage(ex.Message);
        }
    }

    /// <summary>Minimal modal prompt for the cancellation reason, following
    /// the ChqListWin PromptDialog pattern.</summary>
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
            var boxes = new System.Collections.Generic.List<TextBox>();
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


    /// <summary>P6: ERPNext-style amend — a cancelled PO is copied to a
    /// new draft PO linked to the original; this window then loads the
    /// copy for editing.</summary>
    private async Task AmendAsync()
    {
        if (AppHost.Orders is null || _docId is null || _kind != OrdKind.Purchase
            || _status != CommercialDocumentStatus.Cancelled)
            return;
        if (!AppHost.Permissions.Has(PermCat.Purchasing.OrderCreate))
        {
            ShowWarningMessage(T("OrderDoc.PermissionDenied"));
            return;
        }
        try
        {
            var (newId, newNo) = await AppHost.Orders.AmendPurchaseOrderAsync(
                _companyId, _docId.Value, DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
            _lblStatus.Text = $"{T("OrderDoc.AmendedCreated")}: {newNo}";
            await LoadOrderAsync(newId);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.Amend");
            ShowWarningMessage(ex.Message);
        }
    }

    /// <summary>
    /// P1: RFQ supplier invitations — list the invited suppliers with
    /// their status (Invited / Quoted / Declined), invite more by
    /// supplier code, or decline the selected invitation. Creating an
    /// SQ for an invited supplier flips the row to Quoted (service).
    /// </summary>
    private async Task ShowInvitationsAsync()
    {
        if (AppHost.Orders is null || _docId is null || !IsRfq) return;
        var orders = AppHost.Orders;
        var rows = new ObservableCollection<RfqInvitation>();
        async Task Reload()
        {
            rows.Clear();
            foreach (var invitation in await orders.GetRfqInvitationsAsync(_companyId, _docId.Value))
                rows.Add(invitation);
        }
        try { await Reload(); }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.InvitationsLoad");
            ShowWarningMessage(ex.Message);
            return;
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            IsReadOnly = true,
            ItemsSource = rows,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(0, 0, 0, 10)
        };
        grid.Columns.Add(new DataGridTextColumn { Header = T("Invoice.SupplierCode"), Binding = new Binding(nameof(RfqInvitation.PartyCode)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = T("Invoice.SupplierName"), Binding = new Binding(nameof(RfqInvitation.PartyName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = T("OrderDoc.InvitationStatus"), Binding = new Binding(nameof(RfqInvitation.Status)), Width = 110 });

        var dialog = new Window
        {
            Owner = this,
            Title = T("OrderDoc.InvitedSuppliers"),
            Width = 640,
            Height = 380,
            MinWidth = 520,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };
        var content = new DockPanel { Margin = new Thickness(12) };
        var invitePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var supplierCode = new TextBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        var invite = new Button { Content = T("OrderDoc.InviteSupplier"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        var decline = new Button { Content = T("OrderDoc.DeclineInvitation"), MinWidth = 90 };
        invitePanel.Children.Add(new TextBlock { Text = T("Invoice.SupplierCode"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        invitePanel.Children.Add(supplierCode);
        invitePanel.Children.Add(invite);
        invitePanel.Children.Add(decline);
        DockPanel.SetDock(invitePanel, Dock.Top);
        content.Children.Add(invitePanel);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = T("Buttons.Cancel"), MinWidth = 100, IsCancel = true };
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        content.Children.Add(grid);
        dialog.Content = content;

        invite.Click += async (_, _) =>
        {
            var found = TaxSvc.ResolveParty(supplierCode.Text.Trim(), false);
            if (found is not { } party || party.Id is not long partyId)
            {
                ShowWarningMessage(T("OrderDoc.NotFound"));
                return;
            }
            try
            {
                await orders.InviteSuppliersToRfqAsync(_companyId, _docId.Value,
                    new[] { (partyId, (string?)supplierCode.Text.Trim(), (string?)party.Name) }, CurrentUserId);
                supplierCode.Clear();
                await Reload();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.InviteSupplier");
                ShowWarningMessage(ex.Message);
            }
        };
        decline.Click += async (_, _) =>
        {
            if (grid.SelectedItem is not RfqInvitation selected) return;
            try
            {
                await orders.DeclineRfqInvitationAsync(_companyId, _docId.Value, selected.PartyId, CurrentUserId);
                await Reload();
            }
            catch (Exception ex)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "OrdWin.DeclineInvitation");
                ShowWarningMessage(ex.Message);
            }
        };
        dialog.ShowDialog();
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
        var postPermission = _kind == OrdKind.Sales ? PermKey.SalesPost : PermKey.PurchPost;
        _btnPost.IsEnabled = editable && _docId is not null && AppHost.Permissions.Has(postPermission);
        _btnConvert.IsEnabled = !IsRfq && !_isConverting && !_isReceiving && _status == CommercialDocumentStatus.Posted;
        _btnReceipt.IsEnabled = !_isReceiving && !_isConverting && _kind == OrdKind.Purchase && _status == CommercialDocumentStatus.Posted && _docId is not null;
        _btnClose.IsEnabled = _status == CommercialDocumentStatus.Posted;
        _btnCancel.IsEnabled = _status is CommercialDocumentStatus.Draft or CommercialDocumentStatus.Posted;
        // P6: Amend is offered exactly for a cancelled purchase order.
        _btnAmend.IsEnabled = _kind == OrdKind.Purchase && _status == CommercialDocumentStatus.Cancelled && _docId is not null;
        _btnInvitations.IsEnabled = IsRfq && _docId is not null;
        _txtCurrency.IsEnabled = editable;
        _txtRate.IsEnabled = editable;
        _txtPaymentTerms.IsEnabled = editable;
        _txtIncoterm.IsEnabled = editable;
        _btnAddLine.IsEnabled = editable;
        _btnRemLine.IsEnabled = editable;
        // W23 approval workflow: submit from Draft (NotRequired/Rejected),
        // approve/reject only while Pending and only with the kind's
        // APPROVE permission (the service re-checks it).
        _btnSubmitApproval.IsEnabled = _kind == OrdKind.Sales && _docId is not null
            && _status == CommercialDocumentStatus.Draft
            && _approvalState is SalesApprovalState.NotRequired or SalesApprovalState.Rejected;
        var canApproveOrder = AppHost.Permissions.Has(PermCat.Sales.OrderApprove);
        _btnApproveDoc.IsEnabled = _kind == OrdKind.Sales && _docId is not null
            && _status == CommercialDocumentStatus.Draft
            && _approvalState == SalesApprovalState.Pending && canApproveOrder;
        _btnRejectDoc.IsEnabled = _btnApproveDoc.IsEnabled;
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
            _lblStatus.Text = _status == CommercialDocumentStatus.Cancelled && !string.IsNullOrWhiteSpace(_cancelReason)
                ? $"{T("OrderDoc.Status")}: {StatusText(_status)} | {T("OrderDoc.CancelReason")}: {_cancelReason}"
                : $"{T("OrderDoc.Status")}: {StatusText(_status)}";
    }

    /// <summary>Localized text for the derived chain status computed by
    /// OrderProgress (raw statuses fall through to StatusText).</summary>
    private static string DerivedStatusText(string derivedStatus) => derivedStatus switch
    {
        SalesChainDerivedStatus.ToDeliverAndBill => T("OrderDoc.ToDeliverAndBill"),
        SalesChainDerivedStatus.ToDeliver => T("OrderDoc.ToDeliver"),
        SalesChainDerivedStatus.ToBill => T("OrderDoc.ToBill"),
        SalesChainDerivedStatus.Completed => T("OrderDoc.Completed"),
        _ => StatusText(derivedStatus)
    };

    private static string StatusText(string status) => status switch
    {
        CommercialDocumentStatus.Posted => T("Invoice.StatusPosted"),
        CommercialDocumentStatus.Closed => T("OrderDoc.StatusClosed"),
        CommercialDocumentStatus.Cancelled => T("Invoice.StatusCancelled"),
        CommercialDocumentStatus.Expired => T("OrderDoc.StatusExpired"),
        CommercialDocumentStatus.Lost => T("OrderDoc.StatusLost"),
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
            Text = T(IsRfq ? "OrderDoc.TitleRfq" : _kind == OrdKind.Sales ? "OrderDoc.TitleSales" : "OrderDoc.TitlePurchase"),
            FontSize = 20,
            FontWeight = FontWeights.Bold
        });
        panel.Children.Add(new TextBlock { Text = $"{T("OrderDoc.OrderNo")}: {_txtDocNo.Text}    {T("OrderDoc.Date")}: {_dtDate.SelectedDate:yyyy-MM-dd}    {T(IsRfq ? "OrderDoc.ValidTill" : "OrderDoc.DeliveryDate")}: {_dtDeliveryDate.SelectedDate:yyyy-MM-dd}", Margin = new Thickness(0, 8, 0, 2) });
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
        Title = T(IsRfq ? "OrderDoc.TitleRfq" : _kind == OrdKind.Sales ? "OrderDoc.TitleSales" : "OrderDoc.TitlePurchase");
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
        public int LineNo { get; init; }
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
        public int SourceLineNo { get; init; }
        public long? MaterialId { get; init; }
        public string ItemCode { get; init; } = "";
        public string Description { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; init; }
        public decimal DiscountPct { get; init; }
        public long? WarehouseId { get; init; }
        // Q2: rejected at the gate (recorded, never received).
        public decimal RejectedQty { get; set; }
        public string RejectReason { get; set; } = "";
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
