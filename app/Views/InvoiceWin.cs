using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Interaction;
using ShouTech.App.Reporting;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Shared editor for sales, purchase, return, and related commercial documents.
/// Header / footer layout follows Phoenix-style invoice windows:
/// right payment & document fields, middle party/rep/warehouse, left cost centre & accounts,
/// footer totals (lines total, invoice value, paid / discount, payment, remaining / extra, net).
/// </summary>
public sealed class InvoiceWin : BaseWin, IWindowMode, IInfoContextProvider
{
    protected override string? ScheduleTintName => "Mint";

    private readonly InvDoc _doc;
    private readonly ObservableCollection<LineVm> _lines = new();

    // ---- Right header (document + payment) ----
    private readonly ComboBox _cmbPayment = new();
    private readonly TextBox _txtDocNo = new() { IsReadOnly = true };
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today };
    /// <summary>Y3: payment due date — purchase invoices only (migration
    /// 094). Left empty, SaveDraft derives it from the supplier's payment
    /// terms; the picked value is persisted on purchase_documents.due_date
    /// and drives the due-date payable aging.</summary>
    private readonly DatePicker _dtDueDate = new();
    private readonly TextBox _txtCurrency = new();
    private readonly TextBox _txtTaxRate = new();
    private readonly ComboBox _cmbTaxCode = new();
    private readonly DataGridComboBoxColumn _taxCodeColumn = new();

    // Grid-spec columns (owner spec 2026-11-24 15:07): the five core
    // columns are fixed in spec order (name, qty, unit, price, total);
    // the rest are optional and managed by the column chooser with
    // per-user persistence (WinPref).
    private DataGridTextColumn _descColumn = new();
    private DataGridTextColumn _qtyColumn = new();
    private DataGridTextColumn _unitColumn = new();
    private DataGridTextColumn _priceColumn = new();
    private DataGridTextColumn _lineTotalColumn = new();
    private DataGridTextColumn _codeColumn = new();
    private DataGridTextColumn _discountColumn = new();
    private DataGridTextColumn _taxRateColumn = new();
    private DataGridTextColumn _warehouseColumn = new();
    private DataGridTextColumn _notesColumn = new();
    private readonly Button _btnColumns = new();
    private readonly Button _btnCollect = new();
    private readonly TextBox _txtQuickSearch = new();
    private readonly TextBlock _lblAudit = new() { Margin = new Thickness(8, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<TaxSvc.TaxCodeRec> _taxCodes = Array.Empty<TaxSvc.TaxCodeRec>();
    private bool _suppressTaxSelectionChanged;
    private readonly TextBox _txtReceiptNo = new();
    private readonly TextBox _txtReference = new();
    private readonly TextBox _txtBranch = new();
    /// <summary>G4: quotation validity deadline — shown ONLY for the
    /// Quotation kind (the StockTransfer target-warehouse pattern) and
    /// round-tripped through InvDoc.ValidTill / InvRepo.</summary>
    private readonly DatePicker _dtValidTill = new();

    // ---- Middle header ----
    private readonly TextBox _txtPartyCode = new();
    private readonly TextBox _txtPartyName = new();
    private readonly TextBox _txtSalesRep = new();
    private readonly TextBox _txtWarehouse = new();
    /// <summary>Step 9: destination warehouse — StockTransfer mode only.
    /// Persisted on the document (migration 064) and consumed by
    /// StkPost.PostTransfer when the transfer posts.</summary>
    private readonly TextBox _txtTargetWarehouse = new();

    // ---- Left header (accounts) ----
    private readonly TextBox _txtCostCenter = new();
    private readonly TextBox _txtPartyAccount = new();
    private readonly TextBox _txtGoodsAccount = new();

    // ---- Export header (W5, migration 102) ----
    /// <summary>Export section: shown ONLY for sales invoices (the
    /// ValidTill / target-warehouse conditional-field pattern); the
    /// fields themselves appear only while the Export flag is on.
    /// Round-tripped through InvDoc / InvRepo.</summary>
    private readonly CheckBox _chkExport = new();
    private readonly TextBox _txtIncoterm = new();
    private readonly TextBox _txtPortLoading = new();
    private readonly TextBox _txtPortDischarge = new();
    private readonly TextBox _txtCountryOrigin = new();
    private readonly TextBox _txtCustomsRef = new();
    private readonly TextBox _txtVesselFlight = new();
    private readonly TextBox _txtGrossWeight = new();
    private readonly TextBox _txtNetWeight = new();
    private Grid? _exportFieldsPanel;
    private Grid? _exportSection;

    // ---- Live margin (W5) ----
    /// <summary>Per-line gross margin column + footer total. Cost comes
    /// from TaxSvc.GetMaterialCost (the pricing guard's valuation
    /// chain, fail-soft): a line without a resolvable cost shows —.
    /// No sales margin-visibility permission exists in the catalog, so
    /// the column shows for every user (owner decision pending).</summary>
    private readonly DataGridTextColumn _marginColumn = new();
    private readonly TextBox _txtTotalMargin = new() { IsReadOnly = true };

    // ---- Notes / grid / actions ----
    private readonly TextBox _txtNotes = new() { AcceptsReturn = true };
    private readonly DataGrid _grid = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnReverse = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnPdf = new();
    private readonly Button _btnAddLine = new();
    private readonly Button _btnRemoveLine = new();
    private readonly Button _btnFindMaterial = new();
    /// <summary>Phenix "voucher" - journal voucher after post.</summary>
    private readonly Button _btnVoucher = new();
    /// <summary>Phenix "original" - original invoice when opened from a linked document.</summary>
    private readonly Button _btnOriginal = new();
    private readonly Button _btnAttachments = new();
    /// <summary>W23 approval workflow (migration 098): submit / approve /
    /// reject for native sales invoices and quotations. The buttons act on
    /// the stored approval_state through InvRepo; posting itself enforces
    /// the company flag (SAL-7016) at the repository layer.</summary>
    private readonly Button _btnSubmitApproval = new();
    private readonly Button _btnApproveDoc = new();
    private readonly Button _btnRejectDoc = new();
    private string _approvalState = ShouTech.Domain.Entities.SalesApprovalState.NotRequired;

    // ---- Footer totals (Phenix-style three columns) ----
    private readonly TextBox _txtLinesTotal = new() { IsReadOnly = true };
    private readonly TextBox _txtInvoiceValue = new() { IsReadOnly = true };
    private readonly TextBox _txtPaid = new() { Text = "0" };
    private readonly TextBox _txtLinesDiscount = new() { Text = "0" };
    private readonly TextBox _txtPaymentValue = new() { Text = "0" };
    private readonly TextBox _txtRemaining = new() { IsReadOnly = true };
    private readonly TextBox _txtLinesExtra = new() { Text = "0" };
    private readonly TextBox _txtNet = new() { IsReadOnly = true };
    private readonly TextBlock _lblStatus = new() { Margin = new Thickness(8, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    private bool _syncingPayment;
    private bool _syncingDocumentDate;

    /// <summary>W5: live margin applies to selling documents priced
    /// against stock cost — sales invoices and quotations.</summary>
    private bool MarginApplies => _doc.Kind is InvDocKind.SalesInvoice or InvDocKind.Quotation;

    public InvoiceWin() : this(InvDocKind.SalesInvoice) { }

    public InvoiceWin(InvDocKind kind)
    {
        if (kind == InvDocKind.PosSale)
            throw new ArgumentException("Point-of-sale documents must use the POS checkout workflow.", nameof(kind));

        _doc = new InvDoc
        {
            Kind = kind,
            DefaultTaxRate = TaxSvc.GetDefaultRate(),
            CurrencyCode = UsrPref.Load().CurrencyCode,
            WarehouseName = InvDoc.DefaultWarehouseName(),
            GoodsAccountName = InvDoc.DefaultGoodsAccountName(kind),
            PaymentMethod = InvPaymentMethod.Cash,
            PartyAccountName = InvDoc.DefaultCashAccountName()
        };

        _taxCodes = TaxSvc.LoadCodes(_doc.DocDate);

        Width = 1100;
        Height = 700;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = WindowTitleFor(kind);

        BuildUi();
        _txtTaxRate.IsReadOnly = !TaxSvc.IsEnabled || _taxCodes.Count == 0;
        _cmbTaxCode.ItemsSource = _taxCodes;
        _cmbTaxCode.DisplayMemberPath = nameof(TaxSvc.TaxCodeRec.Name);
        _cmbTaxCode.SelectedValuePath = nameof(TaxSvc.TaxCodeRec.Code);
        _cmbTaxCode.IsEnabled = TaxSvc.IsEnabled && _taxCodes.Count > 0;
        if (TaxSvc.IsEnabled && string.IsNullOrWhiteSpace(_doc.TaxCode) && _taxCodes.Count > 0)
        {
            var defaultTax = _taxCodes.FirstOrDefault(x => x.Rate == _doc.DefaultTaxRate) ?? _taxCodes[0];
            _doc.TaxCode = defaultTax.Code;
            _doc.DefaultTaxRate = defaultTax.Rate;
        }
        _cmbTaxCode.SelectedValue = _doc.TaxCode;
        ApplyLanguageDirection();

        _doc.DocNo = InvRepo.NextDocNo(kind);
        _txtDocNo.Text = _doc.DocNo;
        _txtTaxRate.Text = _doc.DefaultTaxRate.ToString("0.##", CultureInfo.InvariantCulture);
        _txtCurrency.Text = _doc.CurrencyCode;
        _txtWarehouse.Text = _doc.WarehouseName;
        _txtGoodsAccount.Text = _doc.GoodsAccountName;
        _txtPartyAccount.Text = _doc.PartyAccountName;

        _lines.Add(new LineVm
        {
            TaxCode = _doc.TaxCode ?? string.Empty,
            TaxRate = _doc.DefaultTaxRate,
            TaxCalculationMode = _taxCodes.FirstOrDefault(x =>
                string.Equals(x.Code, _doc.TaxCode, StringComparison.OrdinalIgnoreCase))?.CalculationMode ?? "Exclusive",
            WarehouseName = _doc.WarehouseName
        });
        WireEvents();
        RefreshTotals();
    }

    /// <summary>
    /// Opens an EXISTING document by id (Step 9, sweep ORD-07 / I-8).
    /// The document is loaded from the database; when it cannot be
    /// found in the current company this constructor THROWS instead of
    /// silently presenting a blank new document under a fresh number —
    /// the failure mode that made DocSearch results open empty.
    /// </summary>
    public InvoiceWin(long docId, InvDocKind kind = InvDocKind.SalesInvoice) : this(kind)
    {
        var loaded = InvRepo.Load(docId, kind)
            ?? throw new InvalidOperationException($"Document {docId} ({kind}) was not found in the current company.");
        ApplyDoc(loaded);
    }

    /// <summary>The database id of the document this window actually
    /// loaded, or null for a new-document window. Lets an opener
    /// (DocSearch) prove the requested document is what opened.</summary>
    public long? LoadedDocumentId { get; private set; }

    /// <summary>Step 11 router identity (IWindowMode): the document kind
    /// plus the loaded document's identity — a window showing saved
    /// document 5 is not "the SalesInvoice window" for a request to open
    /// a NEW sales invoice, and a new-document window is not document 5.
    /// (A save updates LoadedDocumentId, so a saved window stops matching
    /// new-document requests and the next request opens a fresh form.)</summary>
    public string ModeKey => $"{_doc.Kind}:{(LoadedDocumentId?.ToString(CultureInfo.InvariantCulture) ?? "new")}";

    private void ApplyLanguageDirection()
    {
        try
        {
            var code = App.CurrentLanguageCode;
            LangDirSv.ApplyToWindow(this, code);
        }
        catch (Exception exL132)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL132, "InvoiceWin.ApplyLanguageDirection");
            // Direction is best-effort when localization host is unavailable.
        }
    }

    private static string WindowTitleFor(InvDocKind kind) => kind switch
    {
        InvDocKind.PurchaseInvoice => Tf("Invoice.PurchaseTitle", "Purchase Invoice"),
        InvDocKind.PurchaseReturn => Tf("Invoice.PurchaseReturnTitle", "Purchase Return"),
        InvDocKind.SalesInvoice => Tf("Invoice.SalesTitle", "Sales Invoice"),
        InvDocKind.SalesReturn => Tf("Invoice.SalesReturnTitle", "Sales Return"),
        InvDocKind.Quotation => Tf("Invoices.Quotation", "Quotation"),
        _ => Tf("Invoices.Invoice", "Invoice")
    };

    private void WireEvents()
    {
        _txtPartyCode.LostFocus += (_, _) => ResolveParty();
        _txtPartyName.LostFocus += (_, _) =>
        {
            if (_doc.PaymentMethod == InvPaymentMethod.Credit &&
                !string.IsNullOrWhiteSpace(_txtPartyName.Text))
            {
                _txtPartyAccount.Text = _txtPartyName.Text.Trim();
            }
        };

        _cmbPayment.SelectionChanged += (_, _) => OnPaymentMethodChanged();
        _dtDate.SelectedDateChanged += (_, _) =>
        {
            if (_syncingDocumentDate || _doc.Status != InvDocStatus.Draft)
                return;
            _doc.DocDate = _dtDate.SelectedDate ?? DateTime.Today;
            RefreshTaxCodes(_doc.DocDate);
            RefreshTotals();
        };

        void OnFooterEdit(object s, TextChangedEventArgs e) => RefreshTotalsFromFooterInputs();
        _txtLinesDiscount.TextChanged += OnFooterEdit;
        _txtLinesExtra.TextChanged += OnFooterEdit;
        _txtPaid.TextChanged += OnFooterEdit;
        _txtPaymentValue.TextChanged += (_, _) =>
        {
            // Payment value mirrors the paid amount when the user edits either.
            if (_txtPaymentValue.IsFocused)
                _txtPaid.Text = _txtPaymentValue.Text;
            RefreshTotalsFromFooterInputs();
        };
    }

    private void RefreshTaxCodes(DateTime asOfDate, bool preserveUnknown = false)
    {
        var codes = TaxSvc.LoadCodes(asOfDate).ToList();
        if (preserveUnknown &&
            !string.IsNullOrWhiteSpace(_doc.TaxCode) &&
            !codes.Any(x => string.Equals(x.Code, _doc.TaxCode, StringComparison.OrdinalIgnoreCase)))
        {
            codes.Add(new TaxSvc.TaxCodeRec(
                _doc.TaxCode,
                _doc.TaxCode + " (historical)",
                _doc.DefaultTaxRate,
                string.Empty,
                "Exclusive"));
        }

        if (preserveUnknown)
        {
            foreach (var line in _lines.Where(x => !string.IsNullOrWhiteSpace(x.TaxCode)))
            {
                if (codes.Any(x => string.Equals(x.Code, line.TaxCode, StringComparison.OrdinalIgnoreCase)))
                    continue;
                codes.Add(new TaxSvc.TaxCodeRec(
                    line.TaxCode,
                    line.TaxCode + " (historical)",
                    line.TaxRate,
                    string.Empty,
                    line.TaxCalculationMode));
            }
        }

        _taxCodes = codes.AsReadOnly();
        if (!preserveUnknown)
        {
            if (!string.IsNullOrWhiteSpace(_doc.TaxCode))
            {
                var selected = _taxCodes.FirstOrDefault(x =>
                    string.Equals(x.Code, _doc.TaxCode, StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    _doc.TaxCode = string.Empty;
                    _doc.DefaultTaxRate = 0m;
                }
                else
                {
                    _doc.DefaultTaxRate = selected.Rate;
                }
            }

            foreach (var line in _lines)
            {
                if (string.IsNullOrWhiteSpace(line.TaxCode))
                    continue;
                var selected = _taxCodes.FirstOrDefault(x =>
                    string.Equals(x.Code, line.TaxCode, StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    line.TaxCode = string.Empty;
                    line.TaxRate = 0m;
                    line.TaxCalculationMode = "Exclusive";
                    line.NotifyTotals();
                }
                else
                {
                    line.TaxRate = selected.Rate;
                    line.TaxCalculationMode = selected.CalculationMode;
                    line.NotifyTotals();
                }
            }
        }

        _cmbTaxCode.ItemsSource = _taxCodes;
        _taxCodeColumn.ItemsSource = _taxCodes;
        _cmbTaxCode.SelectedValue = _doc.TaxCode;
        var hasCurrentRules = TaxSvc.IsEnabled && codes.Count > 0 && codes.Any(x => !x.Name.EndsWith(" (historical)", StringComparison.Ordinal));
        _cmbTaxCode.IsEnabled = hasCurrentRules;
        _taxCodeColumn.IsReadOnly = !hasCurrentRules;
        _txtTaxRate.IsReadOnly = !hasCurrentRules;
        if (!hasCurrentRules && !preserveUnknown)
        {
            _doc.TaxCode = string.Empty;
            _doc.DefaultTaxRate = 0m;
            _txtTaxRate.Text = "0";
        }
        else
        {
            _txtTaxRate.Text = _doc.DefaultTaxRate.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    private void OnPaymentMethodChanged()
    {
        if (_syncingPayment) return;
        if (_cmbPayment.SelectedItem is not ComboBoxItem item) return;

        var tag = item.Tag?.ToString() ?? "Cash";
        _doc.PaymentMethod = tag switch
        {
            "Credit" => InvPaymentMethod.Credit,
            "Checks" => InvPaymentMethod.Checks,
            _ => InvPaymentMethod.Cash
        };

        switch (_doc.PaymentMethod)
        {
            case InvPaymentMethod.Cash:
                _txtPartyAccount.Text = InvDoc.DefaultCashAccountName();
                break;
            case InvPaymentMethod.Credit:
                // Clears the customer account; when a customer is selected it is filled with their name
                _txtPartyAccount.Text = string.IsNullOrWhiteSpace(_txtPartyName.Text)
                    ? string.Empty
                    : _txtPartyName.Text.Trim();
                break;
            case InvPaymentMethod.Checks:
                _txtPartyAccount.Text = InvDoc.DefaultBankAccountName();
                break;
        }

        // Always editable
        _txtPartyAccount.IsReadOnly = false;
    }

    private void BuildUi()
    {
        // Root: three star bands + action bar at the BOTTOM (layout rule).
        // Step 12 screen-fit: the old fixed 30% / 40% / 30% bands clipped
        // the header's payment column at EVERY size (its 8 rows need more
        // than 30% of any window) with no way to scroll. The header and
        // bottom bands are now scrollable star bands (3* / 2.5* / 2.5*):
        // at a large size everything shows at once; squeezed to the
        // 660x360 minimum, each band scrolls internally and the lines
        // grid keeps its own scrollbars — nothing is ever clipped.
        var root = new Grid { Margin = new Thickness(4) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) }); // header band (scrollable)
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.5, GridUnitType.Star) }); // lines grid
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.5, GridUnitType.Star) }); // bottom band (scrollable)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // action bar

        // ---- Toolbar ----
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0)
        };
        FlowAlign.End(toolbar);
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Invoice.Post");
        StyleButton(_btnReverse, "Invoice.Reverse");
        _btnReverse.IsEnabled = false;
        _btnReverse.Click += (_, _) => ReverseCurrentDocument();
        StyleButton(_btnDelete, "RecycleBin.DeleteToBin");
        _btnDelete.IsEnabled = false;
        _btnDelete.Click += (_, _) => DeleteCurrentDocument();
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnPdf, "Invoice.Pdf");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemoveLine, "Invoice.RemoveLine");
        StyleButton(_btnFindMaterial, "Invoice.FindMaterial");
        StyleButton(_btnCollect, "Invoice.Collect");
        _btnCollect.Click += (_, _) => OpenCollect();
        StyleButton(_btnSubmitApproval, "SalesDoc.SubmitApproval");
        StyleButton(_btnApproveDoc, "SalesDoc.Approve");
        StyleButton(_btnRejectDoc, "SalesDoc.Reject");
        _btnSubmitApproval.Click += (_, _) => SubmitApproval();
        _btnApproveDoc.Click += (_, _) => ApproveDocument();
        _btnRejectDoc.Click += (_, _) => RejectDocument();
        _btnSave.Click += (_, _) => SaveDraft();
        _btnPost.Click += (_, _) => PostDoc();
        _btnPrint.Click += async (_, _) => await PrintWithDesignAsync(pdf: false);
        _btnPdf.Click += async (_, _) => await PrintWithDesignAsync(pdf: true);
        _cmbTaxCode.SelectionChanged += (_, _) =>
        {
            if (_suppressTaxSelectionChanged) return;
            var selectedCode = Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture) ?? string.Empty;
            var selectedTax = _taxCodes.FirstOrDefault(x => string.Equals(x.Code, selectedCode, StringComparison.OrdinalIgnoreCase));
            if (selectedTax is null) return;
            _doc.TaxCode = selectedTax.Code;
            _doc.DefaultTaxRate = selectedTax.Rate;
            _txtTaxRate.Text = selectedTax.Rate.ToString("0.##", CultureInfo.InvariantCulture);
            foreach (var line in _lines)
            {
                line.TaxCode = selectedTax.Code;
                line.TaxRate = selectedTax.Rate;
                line.TaxCalculationMode = selectedTax.CalculationMode;
            }
            RefreshTotals();
        };
        _btnAddLine.Click += (_, _) => AddInvoiceLine();
        _btnRemoveLine.Click += (_, _) => RemoveSelectedInvoiceLine();
        _btnFindMaterial.Click += (_, _) => PickMaterial();
        foreach (var b in new[] { _btnSave, _btnPost, _btnReverse, _btnDelete, _btnPrint, _btnPdf, _btnCollect, _btnAddLine, _btnRemoveLine, _btnFindMaterial, _btnSubmitApproval, _btnApproveDoc, _btnRejectDoc })
            toolbar.Children.Add(b);
        // Explorer-style bottom command bar: flat, transparent, subtle 1px top divider.
        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetRow(actionBar, 3);
        root.Children.Add(actionBar);

        // ---- Top 30%: header fields (compact, no large gaps) ----
        var headerHost = new Border
        {
            Child = new ScrollViewer { Content = BuildHeaderPanel(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 0, 2),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        try { headerHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); } catch (Exception exL279) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL279, "InvoiceWin.BuildUi"); headerHost.BorderBrush = Brushes.LightGray; }
        try { headerHost.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); } catch (Exception exL280) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL280, "InvoiceWin.BuildUi"); }
        Grid.SetRow(headerHost, 0);
        root.Children.Add(headerHost);

        // ---- Middle 40%: lines grid ----
        ConfigureGrid();
        // Column-chooser strip: the Columns button sits at the END of
        // the grid's header strip (spec item 4), end-aligned so it
        // mirrors with the window.
        var gridDock = new DockPanel();
        // Strip above the grid: quick search (F3) at the START side,
        // the column-chooser button at the END of the header strip
        // (owner spec items 3-4 + the Phenix F3 reference); both
        // sides mirror with the window's FlowDirection.
        var chooserStrip = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = false };
        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var lblSearch = new TextBlock
        {
            Text = Tf("Invoice.QuickSearch", "Quick search (F3)"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        _txtQuickSearch.Width = 220;
        _txtQuickSearch.KeyDown += QuickSearch_KeyDown;
        searchPanel.Children.Add(lblSearch);
        searchPanel.Children.Add(_txtQuickSearch);
        DockPanel.SetDock(searchPanel, Dock.Left);
        chooserStrip.Children.Add(searchPanel);
        var colPanel = new StackPanel { Orientation = Orientation.Horizontal };
        StyleButton(_btnColumns, "Common.Columns");
        _btnColumns.Click += OnColumnsClick;
        colPanel.Children.Add(_btnColumns);
        DockPanel.SetDock(colPanel, Dock.Right);
        chooserStrip.Children.Add(colPanel);
        DockPanel.SetDock(chooserStrip, Dock.Top);
        gridDock.Children.Add(chooserStrip);
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.F3)
            {
                _txtQuickSearch.Focus();
                _txtQuickSearch.SelectAll();
                e.Handled = true;
            }
        };
        gridDock.Children.Add(_grid);
        var gridHost = new Border
        {
            Child = gridDock,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(2)
        };
        try { gridHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); } catch (Exception exL294) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL294, "InvoiceWin.BuildUi"); gridHost.BorderBrush = Brushes.Gray; }
        try { gridHost.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); } catch (Exception exL295) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL295, "InvoiceWin.BuildUi"); }
        Grid.SetRow(gridHost, 1);
        root.Children.Add(gridHost);

        // ---- Bottom 30%: totals + notes + voucher/original ----
        var bottom = new Grid { Margin = new Thickness(0) };
        bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bottom.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var footer = BuildFooterPanel();
        Grid.SetRow(footer, 0);
        bottom.Children.Add(footer);

        var cmdBar = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = true };
        var cmdRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        FlowAlign.End(cmdRight);
        StyleButton(_btnVoucher, "Invoice.Voucher");
        StyleButton(_btnOriginal, "Invoice.Original");
        StyleButton(_btnAttachments, "PurchaseAttachments.Open");
        _btnAttachments.Visibility = _doc.Kind is InvDocKind.PurchaseInvoice or InvDocKind.PurchaseReturn or InvDocKind.SalesInvoice ? Visibility.Visible : Visibility.Collapsed;
        _btnAttachments.Click += async (_, _) => await OpenPurchaseAttachmentsAsync();
        _btnVoucher.Content = Tf("Invoice.Voucher", "Voucher");
        _btnOriginal.Content = Tf("Invoice.Original", "Original");
        _btnVoucher.IsEnabled = false;
        _btnOriginal.IsEnabled = false;
        _btnVoucher.Click += (_, _) => OpenVoucher();
        _btnOriginal.Click += (_, _) => OpenOriginal();
        cmdRight.Children.Add(_btnVoucher);
        cmdRight.Children.Add(_btnOriginal);
        cmdRight.Children.Add(_btnAttachments);
        DockPanel.SetDock(cmdRight, Dock.Right);
        cmdBar.Children.Add(cmdRight);
        _lblStatus.VerticalAlignment = VerticalAlignment.Center;
        cmdBar.Children.Add(_lblStatus);
        cmdBar.Children.Add(_lblAudit);
        Grid.SetRow(cmdBar, 1);
        bottom.Children.Add(cmdBar);

        _txtNotes.Height = ShouTech.App.Services.VisuId.MultilineFieldHeight;
        _txtNotes.AcceptsReturn = true;
        _txtNotes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // Notes keep the side-label scheme: label column + stretching field.
        var notesPanel = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        notesPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ShouTech.App.Services.VisuId.FormLabelWidth) });
        notesPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var notesLabel = new TextBlock
        {
            Text = Tf("Invoice.Notes", "Notes"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 8, 0)
        };
        notesLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        notesPanel.Children.Add(notesLabel);
        ApplyFieldStyle(_txtNotes);
        Grid.SetColumn(_txtNotes, 1);
        notesPanel.Children.Add(_txtNotes);
        Grid.SetRow(notesPanel, 2);
        bottom.Children.Add(notesPanel);

        var bottomHost = new Border
        {
            Child = new ScrollViewer { Content = bottom, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            Padding = new Thickness(2),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
        try { bottomHost.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); } catch (Exception exL356) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL356, "InvoiceWin.BuildUi"); bottomHost.BorderBrush = Brushes.LightGray; }
        try { bottomHost.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); } catch (Exception exL357) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL357, "InvoiceWin.BuildUi"); }
        Grid.SetRow(bottomHost, 2);
        root.Children.Add(bottomHost);

        Content = root;
    }

    /// <summary>
    /// Phenix-like compact header: 3 columns × tight rows, uniform control size, no large gaps.
    /// </summary>
    private DockPanel BuildHeaderPanel()
    {
        const double gap = 4;

        // Heights and padding come from the central styles (ControlHeight
        // 32); fields only opt into stretching inside their form cell.
        static void Size(Control c)
        {
            c.Width = double.NaN; // stretch within cell
            c.MinWidth = 60;
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
            c.VerticalContentAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(0);
        }

        Size(_cmbPayment);
        Size(_dtDate);
        Size(_dtValidTill);
        Size(_dtDueDate);
        Size(_txtCurrency);
        Size(_txtTaxRate);
        Size(_cmbTaxCode);
        Size(_txtReceiptNo);
        Size(_txtReference);
        Size(_txtBranch);
        Size(_txtPartyCode);
        Size(_txtPartyName);
        Size(_txtSalesRep);
        Size(_txtWarehouse);
        Size(_txtTargetWarehouse);
        Size(_txtCostCenter);
        Size(_txtPartyAccount);
        Size(_txtGoodsAccount);

        BuildPaymentCombo();

        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Payment / document column — a Quotation carries a ninth row:
        // its Valid Till date (G4; other kinds never show the field).
        // Purchase invoices likewise carry a ninth row: the payment due
        // date (Y3, migration 094, renumbered 109).
        var isPurchaseInvoice = _doc.Kind == InvDocKind.PurchaseInvoice;
        var payment = new Grid { Margin = new Thickness(gap) };
        var paymentRows = _doc.Kind == InvDocKind.Quotation || isPurchaseInvoice ? 9 : 8;
        for (var i = 0; i < paymentRows; i++)
            payment.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(payment, 0, Tf("Invoice.PaymentMethod", "Payment method"), _cmbPayment);
        AddLabeledCell(payment, 1, Tf("Invoice.Date", "Date"), _dtDate);
        AddLabeledCell(payment, 2, Tf("Invoice.Currency", "Currency"), _txtCurrency);
        AddLabeledCell(payment, 3, Tf("Invoice.TaxRate", "Tax %"), _txtTaxRate);
        AddLabeledCell(payment, 4, Tf("Invoice.ReceiptNo", "Receipt no."), _txtReceiptNo);
        AddLabeledCell(payment, 5, Tf("Invoice.Reference", "Reference"), _txtReference);
        AddLabeledCell(payment, 6, Tf("Invoice.Branch", "Branch"), _txtBranch);
        AddLabeledCell(payment, 7, Tf("Invoice.TaxSystem", "Tax system"), _cmbTaxCode);
        if (_doc.Kind == InvDocKind.Quotation)
            AddLabeledCell(payment, 8, Tf("Invoice.ValidTill", "Valid till"), _dtValidTill);
        if (isPurchaseInvoice)
            AddLabeledCell(payment, 8, Tf("Invoice.DueDate", "Due date"), _dtDueDate);

        // Party / warehouse column
        var party = new Grid { Margin = new Thickness(gap) };
        for (var i = 0; i < 4; i++)
            party.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(party, 0, Tf(PartyCodeKey(), "Supplier code"), _txtPartyCode);
        AddLabeledCell(party, 1, Tf(PartyNameKey(), "Supplier"), _txtPartyName);
        AddLabeledCell(party, 2, Tf("Invoice.SalesRep", "Sales rep"), _txtSalesRep);
        AddLabeledCell(party, 3, Tf(_doc.Kind == InvDocKind.StockTransfer ? "Invoice.SourceWarehouse" : "Invoice.Warehouse", "Warehouse"), _txtWarehouse);

        // Accounts column — a StockTransfer document carries a fourth
        // row: its destination warehouse (Step 9).
        var accounts = new Grid { Margin = new Thickness(gap) };
        var accountRows = _doc.Kind == InvDocKind.StockTransfer ? 4 : 3;
        for (var i = 0; i < accountRows; i++)
            accounts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(accounts, 0, Tf("Invoice.CostCenter", "Cost center"), _txtCostCenter);
        AddLabeledCell(accounts, 1, Tf(PartyAccountLabelKey(), "Supplier account"), _txtPartyAccount);
        AddLabeledCell(accounts, 2, Tf("Invoice.GoodsAccount", "Goods account"), _txtGoodsAccount);
        if (_doc.Kind == InvDocKind.StockTransfer)
            AddLabeledCell(accounts, 3, Tf("Invoice.TargetWarehouse", "Destination Warehouse"), _txtTargetWarehouse);

        Grid.SetColumn(accounts, 0);
        Grid.SetColumn(party, 1);
        Grid.SetColumn(payment, 2);
        grid.Children.Add(accounts);
        grid.Children.Add(party);
        grid.Children.Add(payment);

        var panel = new DockPanel { LastChildFill = true };
        var recordBar = CreateRecordNumberBar(_txtDocNo, NavigateDocuments);
        DockPanel.SetDock(recordBar, Dock.Top);
        panel.Children.Add(recordBar);
        // The export section rides under the header columns inside the
        // same scrollable header band (sales invoices only).
        var headerStack = new StackPanel();
        headerStack.Children.Add(grid);
        headerStack.Children.Add(BuildExportSection());
        panel.Children.Add(headerStack);
        return panel;
    }

    /// <summary>
    /// W5 export section: an "Export invoice" flag plus the eight
    /// shipment/customs fields in the header's three-column scheme.
    /// The whole section exists only for sales invoices; the fields
    /// show only while the flag is on.
    /// </summary>
    private Grid BuildExportSection()
    {
        foreach (var tb in new[] { _txtIncoterm, _txtPortLoading, _txtPortDischarge, _txtCountryOrigin, _txtCustomsRef, _txtVesselFlight, _txtGrossWeight, _txtNetWeight })
        {
            tb.Width = double.NaN;
            tb.MinWidth = 60;
            tb.HorizontalAlignment = HorizontalAlignment.Stretch;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Margin = new Thickness(0);
        }

        _chkExport.Content = Tf("Invoice.ExportInvoice", "Export invoice");
        _chkExport.Margin = new Thickness(0, 2, 0, 6);
        _chkExport.Checked += (_, _) => UpdateExportVisibility();
        _chkExport.Unchecked += (_, _) => UpdateExportVisibility();

        var fields = new Grid { Margin = new Thickness(0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        const double gap = 4;
        var colPorts = new Grid { Margin = new Thickness(gap) };
        for (var i = 0; i < 3; i++)
            colPorts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colPorts, 0, Tf("Invoice.Incoterm", "Incoterm"), _txtIncoterm);
        AddLabeledCell(colPorts, 1, Tf("Invoice.PortOfLoading", "Port of loading"), _txtPortLoading);
        AddLabeledCell(colPorts, 2, Tf("Invoice.PortOfDischarge", "Port of discharge"), _txtPortDischarge);

        var colCustoms = new Grid { Margin = new Thickness(gap) };
        for (var i = 0; i < 3; i++)
            colCustoms.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colCustoms, 0, Tf("Invoice.CountryOfOrigin", "Country of origin"), _txtCountryOrigin);
        AddLabeledCell(colCustoms, 1, Tf("Invoice.CustomsRef", "Customs reference"), _txtCustomsRef);
        AddLabeledCell(colCustoms, 2, Tf("Invoice.VesselFlight", "Vessel / flight"), _txtVesselFlight);

        var colWeights = new Grid { Margin = new Thickness(gap) };
        for (var i = 0; i < 2; i++)
            colWeights.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colWeights, 0, Tf("Invoice.GrossWeight", "Gross weight"), _txtGrossWeight);
        AddLabeledCell(colWeights, 1, Tf("Invoice.NetWeight", "Net weight"), _txtNetWeight);

        Grid.SetColumn(colWeights, 0);
        Grid.SetColumn(colCustoms, 1);
        Grid.SetColumn(colPorts, 2);
        fields.Children.Add(colWeights);
        fields.Children.Add(colCustoms);
        fields.Children.Add(colPorts);
        _exportFieldsPanel = fields;

        var section = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_chkExport, 0);
        section.Children.Add(_chkExport);
        Grid.SetRow(fields, 1);
        section.Children.Add(fields);
        _exportSection = section;
        UpdateExportVisibility();
        return section;
    }

    private void UpdateExportVisibility()
    {
        if (_exportSection is not null)
            _exportSection.Visibility = _doc.Kind == InvDocKind.SalesInvoice
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (_exportFieldsPanel is not null)
            _exportFieldsPanel.Visibility = _chkExport.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// Side label + control in one form row of the host group grid: the
    /// label sits in the group's shared label column (FormLabelWidth) and
    /// the control stretches in the field column, so labels and field
    /// edges align down the whole group (Phenix rule — labels beside the
    /// field, never above it).
    /// </summary>
    private void AddLabeledCell(Grid host, int row, string label, FrameworkElement control)
    {
        if (host.ColumnDefinitions.Count == 0)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ShouTech.App.Services.VisuId.FormLabelWidth) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            // Keep the label-to-field gap on the inner edge in both
            // directions: label is column 0, editor is column 1.
            Margin = this.FlowDirection == FlowDirection.RightToLeft
                ? new Thickness(8, 0, 0, 8)
                : new Thickness(0, 0, 8, 8)
        };
        labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        host.Children.Add(labelBlock);

        ApplyFieldStyle(control);
        control.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        host.Children.Add(control);
    }

    private void BuildPaymentCombo()
    {
        var idx = _cmbPayment.SelectedIndex;
        _cmbPayment.Items.Clear();
        _cmbPayment.Items.Add(new ComboBoxItem { Content = Tf("Invoice.PaymentCash", "Cash"), Tag = "Cash" });
        _cmbPayment.Items.Add(new ComboBoxItem { Content = Tf("Invoice.PaymentCredit", "Credit"), Tag = "Credit" });
        _cmbPayment.Items.Add(new ComboBoxItem { Content = Tf("Invoice.PaymentChecks", "Checks"), Tag = "Checks" });
        _cmbPayment.SelectedIndex = idx >= 0 && idx < 3 ? idx : 0;
    }

    /// <summary>Footer totals: three equal columns, uniform fields, tight spacing.</summary>
    private Grid BuildFooterPanel()
    {
        // Heights and padding come from the central styles (ControlHeight
        // 32, applied by AddLabeledCell); totals only opt into stretching.
        foreach (var tb in new[] { _txtLinesTotal, _txtInvoiceValue, _txtPaid, _txtLinesDiscount, _txtPaymentValue, _txtRemaining, _txtLinesExtra, _txtNet, _txtTotalMargin })
        {
            tb.Width = double.NaN;
            tb.MinWidth = 70;
            tb.HorizontalAlignment = HorizontalAlignment.Stretch;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Margin = new Thickness(0);
        }

        // The net is the document's hero number (Phenix totals band):
        // it alone takes the central grand-total treatment.
        _txtNet.SetResourceReference(FrameworkElement.StyleProperty, "VisuGrandTotalStyle");

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var colExtra = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        colExtra.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        colExtra.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        colExtra.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colExtra, 0, Tf("Invoice.LinesExtra", "Lines extra"), _txtLinesExtra);
        AddLabeledCell(colExtra, 1, Tf("Invoice.Net", "Net"), _txtNet);
        // W5 live margin total (sales invoices / quotations only).
        if (MarginApplies)
            AddLabeledCell(colExtra, 2, Tf("Invoice.TotalMargin", "Gross margin"), _txtTotalMargin);

        var colDisc = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        for (var i = 0; i < 3; i++)
            colDisc.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colDisc, 0, Tf("Invoice.LinesDiscount", "Lines discount"), _txtLinesDiscount);
        AddLabeledCell(colDisc, 1, Tf("Invoice.PaymentValue", "Payment amount"), _txtPaymentValue);
        AddLabeledCell(colDisc, 2, Tf("Invoice.Remaining", "Remaining"), _txtRemaining);

        var colTotals = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        for (var i = 0; i < 3; i++)
            colTotals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colTotals, 0, Tf("Invoice.LinesTotal", "Lines total"), _txtLinesTotal);
        AddLabeledCell(colTotals, 1, Tf("Invoice.InvoiceValue", "Invoice value"), _txtInvoiceValue);
        AddLabeledCell(colTotals, 2, Tf("Invoice.Paid", "Total paid"), _txtPaid);

        Grid.SetColumn(colExtra, 0);
        Grid.SetColumn(colDisc, 1);
        Grid.SetColumn(colTotals, 2);
        grid.Children.Add(colExtra);
        grid.Children.Add(colDisc);
        grid.Children.Add(colTotals);
        return grid;
    }

    private void ConfigureGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.SelectionChanged += (_, _) => PublishInfoSelection();
        _grid.CanUserAddRows = false;
        _grid.CanUserResizeRows = false;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        _grid.AlternationCount = 2;
        _grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _grid.Margin = new Thickness(0);
        try
        {
            _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
            _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
            _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
            _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
            _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
            _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");
        }
        catch (Exception exL546)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL546, "InvoiceWin.ConfigureGrid");
        }

        // Shared grid policy: virtualization, row numbering, keyboard/context
        // behavior and the window's table palette.
        GridEngine.ApplyStandard(_grid, "TableSurfaceBrush", "TableAlternateRowBrush");

        _grid.ItemsSource = _lines;
        _grid.Columns.Clear();

        // Six fixed invoice columns: material, first unit, quantity,
        // unit price, calculated line total and line statement.
        _descColumn = TextCol(nameof(LineVm.Description), 180, Tf("Invoice.ColMaterial", "Material"));
        _grid.Columns.Add(_descColumn);
        _unitColumn = TextCol(nameof(LineVm.Unit), 70, Tf("Invoice.ColUnit", "Unit"));
        _grid.Columns.Add(_unitColumn);
        _qtyColumn = TextCol(nameof(LineVm.QtyText), 60, Tf("Invoice.ColQty", "Qty"));
        _grid.Columns.Add(_qtyColumn);
        _priceColumn = TextCol(nameof(LineVm.UnitPriceText), 90, Tf("Invoice.ColUnitPrice", "Unit price"));
        _grid.Columns.Add(_priceColumn);
        _lineTotalColumn = new DataGridTextColumn
        {
            Header = Tf("Invoice.ColLineTotal", "Line total"),
            Binding = new Binding(nameof(LineVm.LineTotalText)) { Mode = BindingMode.OneWay },
            Width = 100,
            IsReadOnly = true
        };
        _grid.Columns.Add(_lineTotalColumn);
        _notesColumn = TextCol(nameof(LineVm.Notes), 150, Tf("Invoice.ColNotes", "Statement"));
        _grid.Columns.Add(_notesColumn);

        // Optional columns (spec item 4) — shown/hidden and ordered
        // through the column chooser, persisted per user (WinPref).
        _codeColumn = TextCol(nameof(LineVm.MaterialCode), 80, Tf("Invoice.ColCode", "Code"));
        _grid.Columns.Add(_codeColumn);
        _discountColumn = TextCol(nameof(LineVm.DiscountPctText), 60, Tf("Invoice.ColDiscount", "Disc %"));
        _grid.Columns.Add(_discountColumn);
        _taxCodeColumn.Header = Tf("Invoice.ColTaxCode", "Tax system");
        _taxCodeColumn.SelectedValueBinding = new Binding(nameof(LineVm.TaxCode)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        _taxCodeColumn.SelectedValuePath = nameof(TaxSvc.TaxCodeRec.Code);
        _taxCodeColumn.DisplayMemberPath = nameof(TaxSvc.TaxCodeRec.Name);
        _taxCodeColumn.ItemsSource = _taxCodes;
        _taxCodeColumn.Width = 125;
        _taxCodeColumn.IsReadOnly = !TaxSvc.IsEnabled || _taxCodes.Count == 0;
        /* Keep the tax-code column identity stable: DisplayIndex changes
           when users reorder columns, so event logic must not depend on it. */
        _grid.Columns.Add(_taxCodeColumn);
        _taxRateColumn = TextCol(nameof(LineVm.TaxRateText), 60, Tf("Invoice.ColTax", "Tax %"));
        _taxRateColumn.IsReadOnly = !TaxSvc.IsEnabled || _taxCodes.Count == 0;
        _grid.Columns.Add(_taxRateColumn);
        // W5 live margin: (net unit price − current valuation cost) ×
        // qty per line, recomputed on every edit; — when no cost can
        // be resolved. Sales invoices and quotations only.
        _marginColumn.Header = Tf("Invoice.ColMargin", "Margin");
        _marginColumn.Binding = new Binding(nameof(LineVm.MarginText)) { Mode = BindingMode.OneWay };
        _marginColumn.Width = 85;
        _marginColumn.IsReadOnly = true;
        _marginColumn.Visibility = Visibility.Collapsed;
        _grid.Columns.Add(_marginColumn);
        _warehouseColumn = TextCol(nameof(LineVm.WarehouseName), 110, Tf("Invoice.ColWarehouse", "Warehouse"));
        _grid.Columns.Add(_warehouseColumn);
        // Every other optional column starts hidden; the chooser
        // (visibility + placement) reveals them per user.
        _codeColumn.Visibility = Visibility.Collapsed;
        _discountColumn.Visibility = Visibility.Collapsed;
        _taxCodeColumn.Visibility = Visibility.Collapsed;
        _taxRateColumn.Visibility = Visibility.Collapsed;
        _warehouseColumn.Visibility = Visibility.Collapsed;
        _marginColumn.Visibility = Visibility.Collapsed;

        ApplySavedColumnLayout();

        _grid.CellEditEnding += OnCellEditEnding;
        _grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = FindGridRow(e.OriginalSource as DependencyObject);
            if (row?.Item is LineVm line)
            {
                _grid.SelectedItem = line;
                var cell = FindVisualCell(e.OriginalSource as DependencyObject);
                if (cell is not null)
                    _grid.CurrentCell = new DataGridCellInfo(line, cell.Column);
            }
        };
        _grid.ContextMenuOpening += (_, _) => _grid.ContextMenu = BuildInvoiceLineContextMenu();
        _grid.ContextMenu = BuildInvoiceLineContextMenu();
    }

    private sealed record InvoicePriceOption(string Level, string ResourceKey, string Fallback);

    private List<InvoicePriceOption> InvoicePriceOptions()
    {
        var options = new List<InvoicePriceOption>
        {
            new("Sale", "Invoice.PriceSale", "Sale price"),
            new("Retail", "Invoice.PriceRetail", "Retail price"),
            new("Wholesale", "Invoice.PriceWholesale", "Wholesale price"),
            new("HalfWholesale", "Invoice.PriceHalfWholesale", "Half-wholesale price"),
            new("Special", "Invoice.PriceSpecial", "Special price"),
            new("Distributor", "Invoice.PriceDistributor", "Distributor price"),
            new("Export", "Invoice.PriceExport", "Export price"),
            new("Online", "Invoice.PriceOnline", "Online price"),
            new("Purchase", "Invoice.PricePurchase", "Purchase price"),
            new("LastPurchase", "Invoice.PriceLastPurchase", "Last purchase price")
        };
        if (_doc.IsSalesSide)
            options.Insert(8, new InvoicePriceOption("Customer", "Invoice.PriceCustomer", "Customer price"));
        return options;
    }

    private ContextMenu BuildInvoiceLineContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string key, string fallback, Action action, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = Tf(key, fallback),
                IsEnabled = enabled,
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 21,
                FontSize = 12
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        Add("Invoice.CtxAddLine", "New line", AddInvoiceLine,
            _doc.Status == InvDocStatus.Draft);
        Add("Invoice.CtxDeleteLine", "Delete line", RemoveSelectedInvoiceLine,
            _doc.Status == InvDocStatus.Draft && _grid.SelectedItem is LineVm);
        Add("Invoice.CtxDuplicateLine", "Duplicate line", DuplicateSelectedInvoiceLine,
            _doc.Status == InvDocStatus.Draft && _grid.SelectedItem is LineVm);
        menu.Items.Add(new Separator { Margin = new Thickness(0, 1, 0, 1) });
        Add("Invoice.CtxCopySelected", "Copy selected", CopySelectedInvoiceLine,
            _grid.SelectedItem is LineVm);
        Add("Invoice.CtxCopyAll", "Copy all", CopyAllInvoiceLines, _lines.Count > 0);

        var pastePrices = new MenuItem
        {
            Header = Tf("Invoice.CtxPastePrices", "Paste prices"),
            Padding = new Thickness(8, 2, 8, 2),
            MinHeight = 21,
            FontSize = 12,
            IsEnabled = _doc.Status == InvDocStatus.Draft &&
                        _grid.SelectedItem is LineVm &&
                        _lines.Any(x => x.MaterialId is > 0 || !string.IsNullOrWhiteSpace(x.MaterialCode))
        };
        foreach (var option in InvoicePriceOptions())
        {
            var child = new MenuItem
            {
                Header = Tf(option.ResourceKey, option.Fallback),
                Tag = option.Level,
                Padding = new Thickness(8, 2, 8, 2),
                MinHeight = 21,
                FontSize = 12
            };
            child.Click += (_, _) => ApplyInvoicePrice(option.Level, Tf(option.ResourceKey, option.Fallback));
            pastePrices.Items.Add(child);
        }
        menu.Items.Add(pastePrices);
        menu.Items.Add(new Separator { Margin = new Thickness(0, 1, 0, 1) });
        Add("Invoice.CtxDeleteEmpty", "Delete empty lines", DeleteEmptyInvoiceLines,
            _doc.Status == InvDocStatus.Draft && _lines.Any(IsEmptyInvoiceLine));
        Add("Invoice.CtxRecalculate", "Recalculate totals", RefreshTotals);
        return menu;
    }

    private void AddInvoiceLine()
    {
        if (_doc.Status != InvDocStatus.Draft) return;
        var line = new LineVm
        {
            TaxCode = Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture) ?? string.Empty,
            TaxRate = ParseDec(_txtTaxRate.Text, TaxSvc.GetDefaultRate()),
            TaxCalculationMode = _taxCodes.FirstOrDefault(x =>
                string.Equals(x.Code, Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))?.CalculationMode ?? "Exclusive",
            WarehouseName = _txtWarehouse.Text.Trim()
        };
        var selectedIndex = _grid.SelectedItem is LineVm selected ? _lines.IndexOf(selected) : -1;
        var insertAt = selectedIndex >= 0 ? selectedIndex + 1 : _lines.Count;
        _lines.Insert(insertAt, line);
        _grid.SelectedItem = line;
        _grid.CurrentCell = new DataGridCellInfo(line, _descColumn);
        _grid.ScrollIntoView(line, _descColumn);
        RefreshTotals();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _grid.Focus();
            _grid.BeginEdit();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RemoveSelectedInvoiceLine()
    {
        if (_doc.Status != InvDocStatus.Draft || _grid.SelectedItem is not LineVm line) return;
        _lines.Remove(line);
        RefreshTotals();
    }

    private void DuplicateSelectedInvoiceLine()
    {
        if (_doc.Status != InvDocStatus.Draft || _grid.SelectedItem is not LineVm source) return;
        var copy = LineVm.FromLine(source.ToLine());
        var index = _lines.IndexOf(source);
        _lines.Insert(index + 1, copy);
        _grid.SelectedItem = copy;
        _grid.CurrentCell = new DataGridCellInfo(copy, _descColumn);
        RefreshTotals();
    }

    private static bool IsEmptyInvoiceLine(LineVm line)
        => line.MaterialId is null && string.IsNullOrWhiteSpace(line.MaterialCode) &&
           string.IsNullOrWhiteSpace(line.Description);

    private void DeleteEmptyInvoiceLines()
    {
        if (_doc.Status != InvDocStatus.Draft) return;
        foreach (var line in _lines.Where(IsEmptyInvoiceLine).ToList())
            _lines.Remove(line);
        RefreshTotals();
    }

    private static string Tsv(string? value)
        => (value ?? string.Empty).Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");

    private void CopySelectedInvoiceLine()
    {
        if (_grid.SelectedItem is not LineVm line) return;
        try
        {
            Clipboard.SetText(string.Join("\t", new[]
            {
                Tsv(line.MaterialCode), Tsv(line.Description), Tsv(line.Unit),
                Tsv(line.QtyText), Tsv(line.UnitPriceText), Tsv(line.DiscountPctText),
                Tsv(line.TaxRateText), Tsv(line.Notes)
            }));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.CopySelectedInvoiceLine");
        }
    }

    private void CopyAllInvoiceLines()
    {
        if (_lines.Count == 0) return;
        try
        {
            var rows = new List<string>
            {
                string.Join("\t", new[]
                {
                    Tf("Invoice.ColCode", "Code"), Tf("Invoice.ColMaterial", "Material"),
                    Tf("Invoice.ColUnit", "Unit"), Tf("Invoice.ColQty", "Qty"),
                    Tf("Invoice.ColUnitPrice", "Unit price"), Tf("Invoice.ColDiscount", "Discount %"),
                    Tf("Invoice.ColTax", "Tax %"), Tf("Invoice.ColNotes", "Notes")
                })
            };
            rows.AddRange(_lines.Select(line => string.Join("\t", new[]
            {
                Tsv(line.MaterialCode), Tsv(line.Description), Tsv(line.Unit),
                Tsv(line.QtyText), Tsv(line.UnitPriceText), Tsv(line.DiscountPctText),
                Tsv(line.TaxRateText), Tsv(line.Notes)
            })));
            Clipboard.SetText(string.Join(Environment.NewLine, rows));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.CopyAllInvoiceLines");
        }
    }

    private void ApplyInvoicePrice(string priceLevel, string priceLabel)
    {
        if (_doc.Status != InvDocStatus.Draft || _grid.SelectedItem is not LineVm selected)
            return;

        var scope = MessageBox.Show(
            this,
            Tf("Invoice.ApplyPriceScope", "Apply this price to all invoice lines? Choose No to apply it only to the selected line."),
            priceLabel,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (scope == MessageBoxResult.Cancel) return;

        var targets = scope == MessageBoxResult.Yes
            ? _lines.ToList()
            : new List<LineVm> { selected };
        var updated = 0;
        var missing = 0;
        foreach (var line in targets)
        {
            if (line.MaterialId is not > 0 && string.IsNullOrWhiteSpace(line.MaterialCode))
            {
                missing++;
                continue;
            }
            var price = TaxSvc.ResolveExplicitPrice(
                _txtPartyCode.Text.Trim(), line.MaterialId, line.MaterialCode,
                priceLevel, line.Unit, _doc.CurrencyCode,
                _dtDate.SelectedDate ?? _doc.DocDate);
            if (price is not decimal value || value <= 0m)
            {
                missing++;
                continue;
            }
            line.UnitPriceText = value.ToString("0.####", CultureInfo.InvariantCulture);
            line.NotifyTotals();
            updated++;
        }
        RefreshTotals();
        _lblStatus.Text = Tf("Invoice.PriceApplied", "Price applied to {0} line(s); no matching price was found for {1} line(s).")
            .Replace("{0}", updated.ToString(CultureInfo.InvariantCulture))
            .Replace("{1}", missing.ToString(CultureInfo.InvariantCulture));
    }

    private static DataGridRow? FindGridRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is DataGridRow row) return row;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static DataGridCell? FindVisualCell(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is DataGridCell cell) return cell;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Optional columns: chooser + per-user persistence (spec item 4).
    // The chooser button sits at the END of the grid's header strip
    // (built in BuildUi); the five core columns never enter the
    // chooser — they are fixed by the spec. Every optional column is
    // backed by a real LineVm field; nothing is invented.
    // ------------------------------------------------------------------

    private sealed record OptionalColumn(string Key, DataGridColumn Column, string HeaderKey);

    private List<OptionalColumn> OptionalColumns() => new()
    {
        new("MaterialCode", _codeColumn, "Invoice.ColCode"),
        new("DiscountPct", _discountColumn, "Invoice.ColDiscount"),
        new("TaxCode", _taxCodeColumn, "Invoice.ColTaxCode"),
        new("TaxRate", _taxRateColumn, "Invoice.ColTax"),
        new("Margin", _marginColumn, "Invoice.ColMargin"),
        new("Warehouse", _warehouseColumn, "Invoice.ColWarehouse"),
    };

    private string ColumnScope()
    {
        var user = AppState.CurrentUser?.Username ?? "anonymous";
        var company = AppState.CurrentCompanyCode ?? "no-company";
        return $"{company}/{user}/Inv:{_doc.Kind}";
    }

    private void ApplySavedColumnLayout()
    {
        var saved = WinPref.LoadReportColumns(ColumnScope());
        if (saved is null || saved.Count == 0)
            return;
        var optionals = OptionalColumns().ToDictionary(o => o.Key, StringComparer.Ordinal);
        foreach (var rec in saved.OrderBy(r => r.Order))
        {
            if (!optionals.TryGetValue(rec.Key, out var opt))
                continue;
            // The margin column only exists on sales/quotation docs.
            if (opt.Key == "Margin" && !MarginApplies)
                continue;
            opt.Column.Visibility = rec.Visible ? Visibility.Visible : Visibility.Collapsed;
            opt.Column.DisplayIndex = 6 + rec.Order;
            if (rec.Width is > 30 and < 600)
                opt.Column.Width = rec.Width;
        }
    }

    private void OnColumnsClick(object sender, RoutedEventArgs e)
    {
        var optionals = OptionalColumns()
            .Where(o => o.Key != "Margin" || MarginApplies)
            .OrderBy(o => o.Column.DisplayIndex)
            .ToList();
        var items = optionals.Select(o => new ColChsWin.Item
        {
            Key = o.Key,
            Header = T(o.HeaderKey),
            Visible = o.Column.Visibility == Visibility.Visible,
            Locked = false
        });
        var dialog = new ColChsWin(items) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var recs = new List<WinPref.ColumnRec>();
        foreach (var (item, idx) in dialog.Result.Select((item, idx) => (item, idx)))
        {
            var opt = optionals.First(o => o.Key == item.Key);
            opt.Column.Visibility = item.Visible ? Visibility.Visible : Visibility.Collapsed;
            opt.Column.DisplayIndex = 6 + idx;
            recs.Add(new WinPref.ColumnRec
            {
                Key = item.Key,
                Visible = item.Visible,
                Order = idx,
                Width = opt.Column.ActualWidth
            });
        }
        WinPref.SaveReportColumns(ColumnScope(), recs);
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is LineVm line)
            {
                // Material entry (spec item 3): a commit in the NAME
                // or CODE cell resolves the material first, so the
                // price resolution below sees the linked material.
                if (ReferenceEquals(e.Column, _descColumn))
                    ResolveMaterialEntry(line, line.Description);
                else if (ReferenceEquals(e.Column, _codeColumn))
                    ResolveMaterialEntry(line, line.MaterialCode);

                if (ReferenceEquals(e.Column, _taxCodeColumn) && TaxSvc.IsEnabled)
                {
                    var selectedTax = _taxCodes.FirstOrDefault(x =>
                        string.Equals(x.Code, line.TaxCode, StringComparison.OrdinalIgnoreCase));
                    line.TaxRate = selectedTax?.Rate ?? 0m;
                    line.TaxCalculationMode = selectedTax?.CalculationMode ?? "Exclusive";
                }

                // W7a: a sales line whose material was just entered with
                // no price yet is filled from the ONE price resolver
                // (contract hook -> customer price -> price list by
                // party tier -> tier column -> material card); the
                // provenance shows in the status line, and the automatic
                // discount % from discount_rules is applied when higher.
                if (_doc.IsSalesSide && !string.IsNullOrWhiteSpace(line.MaterialCode)
                    && ParseDec(line.UnitPriceText, 0) == 0)
                {
                    line.MaterialId = CardStore.ResolveMaterial(line.MaterialCode)?.Id ?? line.MaterialId;
                    var resolution = TaxSvc.ResolveSalesPrice(
                        _txtPartyCode.Text.Trim(), line.MaterialId, line.MaterialCode,
                        ParseDec(line.QtyText, 1), line.Unit, _doc.CurrencyCode);
                    if (resolution is not null && resolution.Price > 0)
                    {
                        line.UnitPriceText = resolution.Price.ToString("0.####", CultureInfo.InvariantCulture);
                        if (resolution.DiscountPct > ParseDec(line.DiscountPctText, 0))
                            line.DiscountPctText = resolution.DiscountPct.ToString("0.##", CultureInfo.InvariantCulture);
                        _lblStatus.Text = Tf("Pricing.Resolved", "Price {0} from {1}")
                            .Replace("{0}", resolution.Price.ToString("0.####", CultureInfo.InvariantCulture))
                            .Replace("{1}", resolution.Source);
                    }
                }

                line.NotifyTotals();
                RefreshTotals();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ResolveParty()
    {
        (long? Id, string Name)? found;
        try
        {
            found = TaxSvc.ResolveParty(_txtPartyCode.Text, customer: _doc.IsSalesSide);
        }
        catch (InvalidOperationException ex)
        {
            // W1: blacklisted party — clear rejection, party not applied.
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (found is null) return;
        _doc.PartyId = found.Value.Id;
        _txtPartyName.Text = found.Value.Name;
        _doc.PartyName = found.Value.Name;
        _doc.PartyCode = _txtPartyCode.Text.Trim();

        if (_doc.PaymentMethod == InvPaymentMethod.Credit)
            _txtPartyAccount.Text = found.Value.Name;
        PublishInfoSelection();
    }

    // ---- Global Information Window provider (INFO_WINDOW_STUDY) ----
    // The current line's material (with the document's party as
    // context, so last-price-to-this-customer resolves); with no line
    // selected, the document's party itself. No projected balance
    // here: invoice lines are mid-edit in mixed units — the
    // projection is a POS-surface behavior (PosWin).

    public InfoRef? CurrentInfoRef
        => _grid.SelectedItem is LineVm line && line.MaterialId is > 0
            ? new InfoRef(InfoKind.Material, Id: line.MaterialId)
            : _doc.PartyId is > 0
                ? new InfoRef(_doc.Kind.IsSales() ? InfoKind.Customer : InfoKind.Supplier, Id: _doc.PartyId)
                : null;

    public InfoContext? CurrentInfoContext
        => _doc.PartyId is > 0
            ? new InfoContext(
                PartyLedgerId: _doc.PartyId,
                PartyKind: _doc.Kind.IsSales() ? "C" : "S",
                WarehouseId: _doc.WarehouseId)
            : null;

    private void RefreshTotalsFromFooterInputs()
    {
        _doc.LinesDiscountAmount = ParseDec(_txtLinesDiscount.Text, 0);
        _doc.LinesExtraAmount = ParseDec(_txtLinesExtra.Text, 0);
        _doc.PaidAmount = ParseDec(_txtPaid.Text, 0);
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        // Keep doc lines in sync for computed properties
        _doc.Lines = _lines.Select(l => l.ToLine()).ToList();
        _doc.LinesDiscountAmount = ParseDec(_txtLinesDiscount.Text, 0);
        _doc.LinesExtraAmount = ParseDec(_txtLinesExtra.Text, 0);
        _doc.PaidAmount = ParseDec(_txtPaid.Text, 0);

        _txtLinesTotal.Text = FormatAmt(_doc.LinesTotal);
        _txtInvoiceValue.Text = FormatAmt(_doc.InvoiceValue);
        _txtRemaining.Text = FormatAmt(_doc.RemainingAmount);
        _txtNet.Text = FormatAmt(_doc.NetAmount);
        if (!_txtPaymentValue.IsFocused)
            _txtPaymentValue.Text = FormatAmt(_doc.PaidAmount);
        RefreshMarginDisplay();

        _lblStatus.Text = _doc.Status switch
        {
            InvDocStatus.Posted => Tf("Invoice.StatusPosted", "Posted"),
            InvDocStatus.Posting => Tf("Invoice.StatusPosting", "Posting — reconciliation required"),
            InvDocStatus.Cancelled => Tf("Invoice.StatusCancelled", "Cancelled"),
            _ => Tf("Invoice.StatusDraft", "Draft")
        };
    }

    private static string FormatAmt(decimal v)
        => v.ToString("N3", CultureInfo.CurrentCulture);

    /// <summary>
    /// W5 live margin refresh: resolves each line's valuation cost
    /// ONCE per material (cached on the line view-model) through the
    /// fail-soft TaxSvc facade, then restates the footer total from
    /// the lines that have a cost. Lines without a resolvable cost
    /// keep the — placeholder and contribute nothing; when NO line
    /// has a cost the total is — too. Never throws into editing.
    /// </summary>
    private void RefreshMarginDisplay()
    {
        if (!MarginApplies) return;
        try
        {
            var anyCost = false;
            var total = 0m;
            foreach (var line in _lines)
            {
                var materialId = line.MaterialId ?? CardStore.ResolveMaterial(line.MaterialCode)?.Id;
                if (materialId is > 0 && line.CostMaterialId != materialId)
                {
                    line.MaterialId = materialId;
                    line.CostMaterialId = materialId;
                    line.UnitCost = TaxSvc.GetMaterialCost(materialId);
                    line.NotifyTotals();
                }
                if (line.MarginAmount is decimal margin)
                {
                    anyCost = true;
                    total += margin;
                }
            }
            _txtTotalMargin.Text = anyCost ? FormatAmt(total) : "—";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.RefreshMarginDisplay");
            _txtTotalMargin.Text = "—";
        }
    }

    /// <summary>W5: an export weight as entered — blank or unparsable
    /// means "not recorded" (null), never a fabricated zero.</summary>
    private static decimal? ParseWeight(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v)) return v;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
        return null;
    }

    /// <summary>Print / PDF through the company's default print
    /// design for this document kind (Invoice Designer, B3); no
    /// design = the built-in layout. Resolution failures degrade to
    /// the built-in layout inside DocDesignRepo (reported there).</summary>
    private async Task PrintWithDesignAsync(bool pdf)
    {
        try
        {
            var doc = CaptureDoc();
            var (design, address) = await DocDesignRepo.ResolveForPrintAsync(doc.Kind, CurrentCompanyCode);
            if (pdf)
                InvPrn.ExportPdf(doc, CurrentCompanyDisplay, design, address);
            else
                InvPrn.Print(doc, CurrentCompanyDisplay, design, address);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.PrintWithDesignAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private InvDoc CaptureDoc()
    {
        _doc.DocNo = _txtDocNo.Text.Trim();
        _doc.DocDate = _dtDate.SelectedDate ?? DateTime.Today;
        _doc.DueDate = _doc.Kind == InvDocKind.PurchaseInvoice ? _dtDueDate.SelectedDate?.Date : null;
        _doc.PartyCode = _txtPartyCode.Text.Trim();
        _doc.PartyName = _txtPartyName.Text.Trim();
        _doc.CurrencyCode = string.IsNullOrWhiteSpace(_txtCurrency.Text)
            ? UsrPref.Load().CurrencyCode
            : _txtCurrency.Text.Trim().ToUpperInvariant();
        var taxEnabled = TaxSvc.IsEnabled && _taxCodes.Count > 0;
        _doc.TaxCode = taxEnabled
            ? Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
        _doc.DefaultTaxRate = taxEnabled
            ? ParseDec(_txtTaxRate.Text, TaxSvc.GetDefaultRate())
            : 0m;
        _doc.Notes = _txtNotes.Text;
        // G4: Valid Till is captured only for quotations (the only kind
        // that shows the field); clearing the picker clears the date.
        if (_doc.Kind == InvDocKind.Quotation)
            _doc.ValidTill = _dtValidTill.SelectedDate;
        // W5: export fields are captured only for sales invoices (the
        // only kind that shows the section).
        if (_doc.Kind == InvDocKind.SalesInvoice)
        {
            _doc.IsExport = _chkExport.IsChecked == true;
            _doc.Incoterm = _txtIncoterm.Text.Trim();
            _doc.PortOfLoading = _txtPortLoading.Text.Trim();
            _doc.PortOfDischarge = _txtPortDischarge.Text.Trim();
            _doc.CountryOfOrigin = _txtCountryOrigin.Text.Trim();
            _doc.CustomsRef = _txtCustomsRef.Text.Trim();
            _doc.VesselFlight = _txtVesselFlight.Text.Trim();
            _doc.GrossWeight = ParseWeight(_txtGrossWeight.Text);
            _doc.NetWeight = ParseWeight(_txtNetWeight.Text);
        }
        _doc.ReceiptNo = _txtReceiptNo.Text.Trim();
        _doc.Reference = _txtReference.Text.Trim();
        _doc.BranchName = _txtBranch.Text.Trim();
        _doc.SalesRepName = _txtSalesRep.Text.Trim();
        _doc.WarehouseName = string.IsNullOrWhiteSpace(_txtWarehouse.Text)
            ? InvDoc.DefaultWarehouseName()
            : _txtWarehouse.Text.Trim();
        _doc.CostCenter = _txtCostCenter.Text.Trim();
        _doc.PartyAccountName = _txtPartyAccount.Text.Trim();
        _doc.GoodsAccountName = string.IsNullOrWhiteSpace(_txtGoodsAccount.Text)
            ? InvDoc.DefaultGoodsAccountName(_doc.Kind)
            : _txtGoodsAccount.Text.Trim();
        _doc.LinesDiscountAmount = ParseDec(_txtLinesDiscount.Text, 0);
        _doc.LinesExtraAmount = ParseDec(_txtLinesExtra.Text, 0);
        _doc.PaidAmount = ParseDec(_txtPaid.Text, 0);
        _doc.Lines = _lines.Select(l =>
            {
                var line = l.ToLine();
                if (!taxEnabled)
                {
                    line.TaxCode = string.Empty;
                    line.TaxRate = 0m;
                }
                return line;
            })
            .Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.Qty != 0)
            .ToList();
        // Resolve typed codes/names to ids so stock posting and reloads work:
        // lines used to persist as free text with MaterialId/WarehouseId null.
        // The visible code/name is the source of truth; a previously loaded id
        // is only the fallback when the text no longer resolves.
        _doc.WarehouseId = (int?)CardStore.ResolveWarehouseId(_doc.WarehouseName) ?? _doc.WarehouseId;
        // Step 9: a transfer's destination resolves the same way and
        // persists with the document (migration 064); clearing the
        // field clears the destination instead of keeping a stale id.
        if (_doc.Kind == InvDocKind.StockTransfer)
        {
            _doc.TargetWarehouseName = _txtTargetWarehouse.Text.Trim();
            _doc.TargetWarehouseId = string.IsNullOrWhiteSpace(_doc.TargetWarehouseName)
                ? null
                : (int?)CardStore.ResolveWarehouseId(_doc.TargetWarehouseName) ?? _doc.TargetWarehouseId;
        }
        foreach (var line in _doc.Lines)
        {
            line.MaterialId = CardStore.ResolveMaterial(line.MaterialCode)?.Id ?? line.MaterialId;
            line.WarehouseId = (int?)CardStore.ResolveWarehouseId(line.WarehouseName) ?? line.WarehouseId ?? _doc.WarehouseId;
        }
        _doc.RenumberLines();
        return _doc;
    }

    private void ApplyDoc(InvDoc doc)
    {
        LoadedDocumentId = doc.DocId;
        _doc.DocId = doc.DocId;
        _doc.Kind = doc.Kind;
        _doc.TargetWarehouseId = doc.TargetWarehouseId;
        _doc.TargetWarehouseName = doc.TargetWarehouseName;
        _doc.DocNo = doc.DocNo;
        _doc.DocDate = doc.DocDate;
        _doc.DueDate = doc.DueDate;
        _doc.PartyId = doc.PartyId;
        _doc.PartyCode = doc.PartyCode;
        _doc.PartyName = doc.PartyName;
        _doc.WarehouseId = doc.WarehouseId;
        _doc.WarehouseName = string.IsNullOrWhiteSpace(doc.WarehouseName)
            ? InvDoc.DefaultWarehouseName()
            : doc.WarehouseName;
        _doc.CurrencyCode = doc.CurrencyCode;
        _doc.DefaultTaxRate = doc.DefaultTaxRate;
        _doc.TaxCode = doc.TaxCode ?? string.Empty;
        _doc.Notes = doc.Notes;
        _doc.Status = doc.Status;
        _doc.PaymentMethod = doc.PaymentMethod;
        _doc.ReceiptNo = doc.ReceiptNo;
        _doc.Reference = doc.Reference;
        _doc.BranchName = doc.BranchName;
        _doc.SalesRepName = doc.SalesRepName;
        _doc.CostCenter = doc.CostCenter;
        _doc.PartyAccountName = doc.PartyAccountName;
        _doc.GoodsAccountName = string.IsNullOrWhiteSpace(doc.GoodsAccountName)
            ? InvDoc.DefaultGoodsAccountName(doc.Kind)
            : doc.GoodsAccountName;
        _doc.LinesDiscountAmount = doc.LinesDiscountAmount;
        _doc.LinesExtraAmount = doc.LinesExtraAmount;
        _doc.PaidAmount = doc.PaidAmount;
        _doc.ValidTill = doc.ValidTill;
        _dtValidTill.SelectedDate = doc.ValidTill;
        // W5: export header round-trip (sales invoices).
        _doc.IsExport = doc.IsExport;
        _doc.Incoterm = doc.Incoterm;
        _doc.PortOfLoading = doc.PortOfLoading;
        _doc.PortOfDischarge = doc.PortOfDischarge;
        _doc.CountryOfOrigin = doc.CountryOfOrigin;
        _doc.CustomsRef = doc.CustomsRef;
        _doc.VesselFlight = doc.VesselFlight;
        _doc.GrossWeight = doc.GrossWeight;
        _doc.NetWeight = doc.NetWeight;
        _chkExport.IsChecked = doc.IsExport;
        _txtIncoterm.Text = doc.Incoterm;
        _txtPortLoading.Text = doc.PortOfLoading;
        _txtPortDischarge.Text = doc.PortOfDischarge;
        _txtCountryOrigin.Text = doc.CountryOfOrigin;
        _txtCustomsRef.Text = doc.CustomsRef;
        _txtVesselFlight.Text = doc.VesselFlight;
        _txtGrossWeight.Text = doc.GrossWeight?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        _txtNetWeight.Text = doc.NetWeight?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
        UpdateExportVisibility();
        _doc.OriginalDocId = doc.OriginalDocId;
        _doc.OriginalDocKind = doc.OriginalDocKind;
        _doc.OriginalDocNo = doc.OriginalDocNo;
        _doc.JournalDocNo = doc.JournalDocNo;

        _txtDocNo.Text = doc.DocNo;
        _syncingDocumentDate = true;
        try { _dtDate.SelectedDate = doc.DocDate; }
        finally { _syncingDocumentDate = false; }
        try { _dtDueDate.SelectedDate = doc.DueDate; }
        catch (ArgumentOutOfRangeException ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.LoadDocument.DueDate");
            _dtDueDate.SelectedDate = null;
        }
        _txtPartyCode.Text = doc.PartyCode;
        _txtPartyName.Text = doc.PartyName;
        _txtCurrency.Text = doc.CurrencyCode;
        _txtTaxRate.Text = doc.DefaultTaxRate.ToString("0.##", CultureInfo.InvariantCulture);
        _txtNotes.Text = doc.Notes ?? string.Empty;
        _txtReceiptNo.Text = doc.ReceiptNo;
        _txtReference.Text = doc.Reference;
        _txtBranch.Text = doc.BranchName;
        _txtSalesRep.Text = doc.SalesRepName;
        _txtWarehouse.Text = _doc.WarehouseName;
        _txtTargetWarehouse.Text = doc.TargetWarehouseName;
        _txtCostCenter.Text = doc.CostCenter;
        _txtPartyAccount.Text = doc.PartyAccountName;
        _txtGoodsAccount.Text = _doc.GoodsAccountName;
        _txtLinesDiscount.Text = FormatAmt(doc.LinesDiscountAmount);
        _txtLinesExtra.Text = FormatAmt(doc.LinesExtraAmount);
        _txtPaid.Text = FormatAmt(doc.PaidAmount);

        _syncingPayment = true;
        try
        {
            _cmbPayment.SelectedIndex = doc.PaymentMethod switch
            {
                InvPaymentMethod.Credit => 1,
                InvPaymentMethod.Checks => 2,
                _ => 0
            };
        }
        finally
        {
            _syncingPayment = false;
        }

        _lines.Clear();
        foreach (var line in doc.Lines)
            _lines.Add(LineVm.FromLine(line));
        if (_lines.Count == 0)
            _lines.Add(new LineVm
            {
                TaxCode = doc.TaxCode ?? string.Empty,
                TaxRate = doc.DefaultTaxRate,
                TaxCalculationMode = _taxCodes.FirstOrDefault(x =>
                    string.Equals(x.Code, doc.TaxCode, StringComparison.OrdinalIgnoreCase))?.CalculationMode ?? "Exclusive",
                WarehouseName = _doc.WarehouseName
            });

        RefreshTaxCodes(doc.DocDate, preserveUnknown: doc.Status != InvDocStatus.Draft);
        _suppressTaxSelectionChanged = true;
        try { _cmbTaxCode.SelectedValue = _doc.TaxCode; }
        finally { _suppressTaxSelectionChanged = false; }

        var locked = doc.Status is InvDocStatus.Posted or InvDocStatus.Posting or InvDocStatus.Cancelled;
        _btnSave.IsEnabled = !locked;
        _btnReverse.IsEnabled = locked && doc.Kind is InvDocKind.SalesInvoice or InvDocKind.PurchaseInvoice or InvDocKind.PosSale;
        if (_btnReverse.IsEnabled)
        {
            try { _btnReverse.IsEnabled = !InvRepo.HasCompletedReversal(doc.DocId, doc.Kind); }
            catch (Exception exL743) {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL743, "InvoiceWin.ApplyDoc"); _btnReverse.IsEnabled = false; }
        }
        _btnDelete.IsEnabled = doc.DocId > 0;
        _btnPost.IsEnabled = !locked;
        // Collect (Phenix reference): receipt allocation against a
        // POSTED sales invoice, through the existing PayRecWin path.
        _btnCollect.Visibility = doc.Kind == InvDocKind.SalesInvoice ? Visibility.Visible : Visibility.Collapsed;
        _btnCollect.IsEnabled = doc.Kind == InvDocKind.SalesInvoice && doc.Status == InvDocStatus.Posted;
        _lblAudit.Text = doc.Status == InvDocStatus.Posted && !string.IsNullOrWhiteSpace(doc.PostedBy)
            ? Tf("Invoice.PostedByLine", "Posted by {0} at {1}", doc.PostedBy, doc.PostedAt ?? string.Empty)
            : string.Empty;
        _grid.IsReadOnly = locked;
        UpdateVoucherOriginalButtons();
        RefreshApprovalButtons(doc);
        RefreshTotals();
    }

    /// <summary>
    /// W23: approval buttons exist only for native sales invoices and
    /// quotations; their enablement mirrors the stored approval_state and
    /// the kind's APPROVE permission. The repository re-validates every
    /// transition, so a stale enablement can never approve by itself.
    /// </summary>
    private void RefreshApprovalButtons(InvDoc doc)
    {
        var applies = doc.Kind is InvDocKind.SalesInvoice or InvDocKind.Quotation;
        var visibility = applies ? Visibility.Visible : Visibility.Collapsed;
        _btnSubmitApproval.Visibility = visibility;
        _btnApproveDoc.Visibility = visibility;
        _btnRejectDoc.Visibility = visibility;
        if (!applies) return;
        try
        {
            _approvalState = doc.DocId > 0
                ? InvRepo.GetSalesApprovalState(doc.DocId)
                : ShouTech.Domain.Entities.SalesApprovalState.NotRequired;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.RefreshApprovalButtons");
            _approvalState = ShouTech.Domain.Entities.SalesApprovalState.NotRequired;
        }
        var draft = doc.Status == InvDocStatus.Draft;
        _btnSubmitApproval.IsEnabled = draft
            && _approvalState is ShouTech.Domain.Entities.SalesApprovalState.NotRequired or ShouTech.Domain.Entities.SalesApprovalState.Rejected;
        var canApprove = AppHost.Permissions.Has(doc.Kind == InvDocKind.Quotation
            ? PermCat.Sales.QuotationApprove
            : PermCat.Sales.InvoiceApprove);
        _btnApproveDoc.IsEnabled = draft
            && _approvalState == ShouTech.Domain.Entities.SalesApprovalState.Pending && canApprove;
        _btnRejectDoc.IsEnabled = _btnApproveDoc.IsEnabled;
    }

    private void SubmitApproval()
    {
        if (_doc.DocId <= 0) { ShowWarn("OrderDoc.SaveFirst"); return; }
        try
        {
            InvRepo.SubmitSalesForApproval(_doc.DocId, CurrentUserId);
            RefreshApprovalButtons(_doc);
            ShowInfo("SalesDoc.Submitted");
        }
        catch (AppError ex) { ShowCodedError(ex, "InvoiceWin.SubmitApproval"); }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.SubmitApproval");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApproveDocument()
    {
        if (_doc.DocId <= 0) { ShowWarn("OrderDoc.SaveFirst"); return; }
        try
        {
            InvRepo.ApproveSalesDocument(_doc.DocId, CurrentUserId);
            RefreshApprovalButtons(_doc);
            ShowInfo("SalesDoc.Approved");
        }
        catch (AppError ex) { ShowCodedError(ex, "InvoiceWin.ApproveDocument"); }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.ApproveDocument");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RejectDocument()
    {
        if (_doc.DocId <= 0) { ShowWarn("OrderDoc.SaveFirst"); return; }
        var input = PromptDialog.Show(this, T("SalesDoc.RejectReason"), (T("SalesDoc.RejectReason"), string.Empty));
        var reason = input is { Length: > 0 } ? input[0] : null;
        if (string.IsNullOrWhiteSpace(reason)) { ShowWarn("SalesDoc.RejectReasonRequired"); return; }
        try
        {
            InvRepo.RejectSalesDocument(_doc.DocId, CurrentUserId, reason);
            RefreshApprovalButtons(_doc);
            ShowInfo("SalesDoc.Rejected");
        }
        catch (AppError ex) { ShowCodedError(ex, "InvoiceWin.RejectDocument"); }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.RejectDocument");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Coded business blocks get the localized catalog message
    /// with its code (the PostDoc pattern).</summary>
    private void ShowCodedError(AppError ex, string context)
    {
        AppHost.Errors.Report(ex.Code, ex, context);
        var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
        var body = info.Message;
        if (!string.IsNullOrWhiteSpace(info.Hint))
            body += $"{Environment.NewLine}{Environment.NewLine}{info.Hint}";
        body += $"{Environment.NewLine}{Environment.NewLine}(Error {info.Code})";
        MessageBox.Show(this, body, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// Voucher: enabled only after the invoice is posted (journal voucher exists).
    /// Original: enabled only when this window was opened from a linked document
    /// (return / drill-down) that points to an original invoice; disabled for a primary open.
    /// </summary>
    private void UpdateVoucherOriginalButtons()
    {
        var posted = _doc.Status == InvDocStatus.Posted;
        _btnVoucher.IsEnabled = posted && _doc.DocId > 0;
        _btnOriginal.IsEnabled = _doc.OriginalDocId is > 0;
    }

    private async Task OpenPurchaseAttachmentsAsync()
    {
        if (_doc.Kind is not (InvDocKind.PurchaseInvoice or InvDocKind.PurchaseReturn or InvDocKind.SalesInvoice) || LoadedDocumentId is not long documentId)
        {
            MessageBox.Show(this, T("OrderDoc.SaveFirst"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (AppHost.PurchaseAttachments is null || AppHost.JournalStore is null)
        {
            MessageBox.Show(this, T("PurchaseAttachments.MissingService"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
            // W23: sales invoices use the generalized SALDOC owner on the
            // same attachment store/window (migration 098); purchase kinds
            // keep the legacy purchase-document path.
            if (_doc.Kind == InvDocKind.SalesInvoice)
                new PurchaseAttachmentsWin(companyId, ShouTech.Domain.Entities.AttachmentOwnerKind.SalesDocument,
                    documentId.ToString(CultureInfo.InvariantCulture), CurrentUserId) { Owner = this }.ShowDialog();
            else
                new PurchaseAttachmentsWin(companyId, documentId, CurrentUserId) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenVoucher()
    {
        if (_doc.Status != InvDocStatus.Posted)
        {
            ShowWarn("Invoice.VoucherRequiresPost");
            return;
        }

        try
        {
            // Prefer stored journal number; fall back to invoice doc no as reference.
            var journalNo = string.IsNullOrWhiteSpace(_doc.JournalDocNo)
                ? _doc.DocNo
                : _doc.JournalDocNo;

            var win = new JrnWin();
            win.Owner = this;
            // Best-effort: set document number field if the public surface allows it.
            try
            {
                var field = win.GetType().GetField("_txtDocNo",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field?.GetValue(win) is TextBox txt)
                    txt.Text = journalNo;
            }
            catch (Exception exL788)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL788, "InvoiceWin.OpenVoucher");
                // JrnWin may load interactively if reflection surface changes.
            }

            win.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.OpenVoucher");
            MessageBox.Show(this,
                Tf("Invoice.VoucherOpenFailed", "Could not open the journal voucher.") + " " + ex.Message,
                Tf("Common.Error", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenOriginal()
    {
        if (_doc.OriginalDocId is not long originalId || originalId <= 0)
        {
            ShowWarn("Invoice.NoOriginal");
            return;
        }

        try
        {
            var kind = _doc.OriginalDocKind ?? (_doc.IsSalesSide ? InvDocKind.SalesInvoice : InvDocKind.PurchaseInvoice);
            var win = new InvoiceWin(originalId, kind)
            {
                Owner = this
            };
            win.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.OpenOriginal");
            MessageBox.Show(this,
                Tf("Invoice.OriginalOpenFailed", "Could not open the original invoice.") + " " + ex.Message,
                Tf("Common.Error", "Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void NavigateDocuments(int offset)
    {
        try
        {
            if (_doc.DocId <= 0 || _doc.Status == InvDocStatus.Draft)
            {
                var answer = MessageBox.Show(this, T("RecordNavigation.DiscardChanges"),
                    T("RecordNavigation.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }

            var targetId = InvRepo.FindAdjacentDocumentId(_doc.DocId, _doc.Kind, offset);
            if (targetId is not long id) return;
            var target = InvRepo.Load(id, _doc.Kind);
            if (target is not null) ApplyDoc(target);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.NavigateDocuments");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// G20: sales-document RBAC, following the CshWin/PayRecWin pattern
    /// (deny + report, never silent). Sales-side kinds check the
    /// SALES.INVOICE.* keys (quotations their SALES.QUOTATION.* create /
    /// update keys); other kinds keep their existing behaviour.
    /// Returns false when the action must not proceed.
    /// </summary>
    private bool EnsureSalesPermission(string action, string operation)
    {
        if (!_doc.IsSalesSide) return true;
        var permission = action switch
        {
            "create" => _doc.Kind == InvDocKind.Quotation ? PermCat.Sales.QuotationCreate : PermCat.Sales.InvoiceCreate,
            "update" => _doc.Kind == InvDocKind.Quotation ? PermCat.Sales.QuotationUpdate : PermCat.Sales.InvoiceUpdate,
            "post" => PermCat.Sales.InvoicePost,
            "reverse" => PermCat.Sales.InvoiceReverse,
            _ => action
        };
        if (AppHost.Permissions.Has(permission)) return true;
        var denied = new UnauthorizedAccessException($"Permission {permission} is required for {operation}.");
        AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, denied, $"InvoiceWin.{operation}");
        MessageBox.Show(this, T("Invoice.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>
    /// Phase 09 (W7b) commission generation for the NATIVE posting path:
    /// InvRepo is application code with no service-level post-posting
    /// event, so the window — which already calls services after a
    /// successful post — is the least invasive seam. Sales invoices
    /// generate Sales/Profit entries; sales returns generate the
    /// negative adjustments. FAIL-SOFT by design: generation runs after
    /// the post has committed, catches everything, and reports through
    /// the error catalog — a commission failure never breaks posting.
    /// Generation is idempotent, so a repost/retry cannot double-pay.
    /// </summary>
    private static async void GenerateCommissionsFailSoft(long docId, InvDocKind kind)
    {
        try
        {
            var service = AppHost.Commissions;
            if (service is null) return;
            var user = AppState.CurrentUser?.Username ?? Environment.UserName;
            if (kind == InvDocKind.SalesInvoice)
                await service.GenerateForInvoiceAsync(CurrentCompanyCode, docId, user);
            else if (kind == InvDocKind.SalesReturn)
                await service.GenerateForReturnAsync(CurrentCompanyCode, docId, user);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.GenerateCommissionsFailSoft");
        }
    }

    private void SaveDraft()
    {
        if (!EnsureSalesPermission(_doc.DocId > 0 ? "update" : "create", "SaveDraft")) return;
        try
        {
            ResolveParty();
            var doc = CaptureDoc();
            if (doc.Lines.Count == 0) { ShowWarn("Invoice.NeedLines"); return; }
            InvRepo.SaveDraft(doc);
            LoadedDocumentId = doc.DocId;
            _txtDocNo.Text = doc.DocNo;
            ShowInfo("Invoice.Saved");
            RefreshTotals();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.SaveDraft");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Minimal modal prompt (the ChqListWin/OrdWin pattern), used
    /// for the mandatory rejection reason.</summary>
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
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            FlowAlign.End(buttons);
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

    private void PostDoc()
    {
        if (!EnsureSalesPermission("post", "PostDoc")) return;
        // Posting a never-saved document saves it first (below), so the
        // create right is required for that path as well.
        if (_doc.DocId <= 0 && !EnsureSalesPermission("create", "PostDoc")) return;
        if (!AskConfirm("Invoice.ConfirmPost")) return;
        TryPost(null);
    }

    private void TryPost(ShouTech.Domain.Entities.CreditOverrideContext? creditOverride)
    {
        try
        {
            ResolveParty();
            var doc = CaptureDoc();
            if (doc.DocId <= 0) InvRepo.SaveDraft(doc);
            // Batch/serial capture (Phase B 054): tracked lines are
            // captured in the picker before posting; cancelling the
            // picker aborts the post and keeps the draft.
            var deliveryLinked = doc.Kind == InvDocKind.SalesInvoice
                && InvRepo.IsDeliveryLinkedSalesInvoice(doc.DocId);
            // The delivery note already captured stock batches/serials and
            // issued the stock. Its invoice posts receivable, revenue and the
            // linked delivery's COGS, so do not ask for a second stock capture.
            var captures = deliveryLinked ? null : BatchSerialCaptureSvc.CaptureForPosting(doc, this);
            if (!deliveryLinked && captures is null) return;
            InvRepo.Post(doc.DocId, doc.Kind, AppState.CurrentUser?.Username ?? Environment.UserName, captures, creditOverride: creditOverride);
            doc.Status = InvDocStatus.Posted;
            GenerateCommissionsFailSoft(doc.DocId, doc.Kind);
            // Reload so JournalDocNo assigned during post is visible to the voucher button.
            var reloaded = InvRepo.Load(doc.DocId, doc.Kind);
            if (reloaded is not null)
            {
                _doc.JournalDocNo = reloaded.JournalDocNo;
                _doc.Status = reloaded.Status;
            }
            _btnSave.IsEnabled = false;
            _btnPost.IsEnabled = false;
            _grid.IsReadOnly = true;
            UpdateVoucherOriginalButtons();
            ShowInfo("Invoice.Posted");
            RefreshTotals();
        }
        catch (AppError ex) when (creditOverride is null
            && ex.Code == ErrorCodes.CreditLimitExceeded
            && _doc.Kind == InvDocKind.SalesInvoice
            && AppHost.Permissions.Has(PermCat.Sales.CreditOverride))
        {
            // W23 carry-over (W1 wired this in OrdWin only): the posting
            // user holds SALES.CREDIT.OVERRIDE — offer the audited override
            // instead of a dead-end ACC-5006 block. PartyCreditGuard
            // records actor + amounts in credit_overrides on the retry.
            if (MessageBox.Show(this, $"{ex.Message}\n\n{T("OrderDoc.CreditOverrideConfirm")}", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            TryPost(new ShouTech.Domain.Entities.CreditOverrideContext(CurrentUserId, null, null));
        }
        catch (AppError ex)
        {
            // Coded business blocks (e.g. ACC-5006 credit limit): show the
            // localized catalog message with its numbers, not raw text.
            AppHost.Errors.Report(ex.Code, ex, "InvoiceWin.PostDoc");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            var body = info.Message;
            if (!string.IsNullOrWhiteSpace(info.Hint))
                body += $"{Environment.NewLine}{Environment.NewLine}{info.Hint}";
            body += $"{Environment.NewLine}{Environment.NewLine}(Error {info.Code})";
            MessageBox.Show(this, body, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (StkPost.StockTransferException ex)
        {
            // Step 9: transfer-engine blocks (INV-TRF-*) get the same
            // localized captions WhsWin uses for the same codes.
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.PostDoc");
            var key = ex.ErrorCode switch
            {
                "INV-TRF-004" => "Inventory.TransferInsufficientStock",
                "INV-TRF-002" => "Inventory.TransferFailed",
                _ => "Inventory.TransferInvalid"
            };
            MessageBox.Show(this, $"{Tf(key, ex.Message)}\n[{ex.ErrorCode}]", T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.PostDoc");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReverseCurrentDocument()
    {
        if (_doc.DocId <= 0) return;
        if (!EnsureSalesPermission("reverse", "ReverseCurrentDocument")) return;
        if (!AskConfirm("Invoice.ConfirmReverse")) return;
        try
        {
            var reason = Tf("Invoice.ReverseReason", "User requested reversal from invoice window");
            InvRepo.ReversePostedInvoice(
                _doc.DocId,
                _doc.Kind,
                reason,
                AppState.CurrentUser?.Username ?? Environment.UserName);
            ShowInfo("Invoice.Reversed");
            _btnReverse.IsEnabled = false;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.ReverseCurrentDocument");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Soft delete (SOFT_DELETE_STUDY): the document moves to
    /// the recycle bin; DocBinSvc reverses its posting effects while it
    /// is binned and reinstates them on restore. The window closes —
    /// the document no longer exists in working lists.</summary>
    private void DeleteCurrentDocument()
    {
        if (_doc.DocId <= 0) return;
        if (!AskConfirm("RecycleBin.ConfirmDeleteDocument")) return;
        try
        {
            DocBinSvc.SoftDeleteDocument(
                _doc.DocId,
                _doc.Kind,
                AppState.CurrentUser?.Username ?? Environment.UserName);
            ShowInfo("RecycleBin.MovedToBin");
            Close();
        }
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "InvoiceWin.DeleteCurrentDocument");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            MessageBox.Show(this, $"{info.Message} (Error {info.Code})", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "InvoiceWin.DeleteCurrentDocument");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------
    // Material entry (owner grid spec item 3): typing a code, a
    // barcode, an exact name, or PART of a name in the NAME or CODE
    // cell resolves the material immediately when the match is
    // unambiguous (TaxSvc.MatchMaterial) and fills the line (default
    // unit, tax rate; price via the sales resolver below / the card
    // price on the purchase side). No match — or several — opens the
    // material directory as the search window, pre-filtered with the
    // typed text. The Find Material button runs the same search.
    // ------------------------------------------------------------------

    private void QuickSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        var text = _txtQuickSearch.Text.Trim();
        if (text.Length == 0)
            return;
        // Same entry pipeline as typing in the grid (spec item 3):
        // a unique/partial match fills the new line at once; no
        // match opens the material directory search.
        var line = new LineVm
        {
            TaxCode = Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture) ?? string.Empty,
            TaxRate = ParseDec(_txtTaxRate.Text, TaxSvc.GetDefaultRate()),
            TaxCalculationMode = _taxCodes.FirstOrDefault(x =>
                string.Equals(x.Code, Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))?.CalculationMode ?? "Exclusive",
            WarehouseName = _txtWarehouse.Text.Trim()
        };
        _lines.Add(line);
        ResolveMaterialEntry(line, text);
        if (line.MaterialId is > 0)
        {
            _txtQuickSearch.Clear();
            _grid.SelectedItem = line;
            RefreshTotals();
        }
    }

    private void OpenCollect()
    {
        // Receipt + allocation for this invoice live in PayRecWin
        // (the canonical receipts path); Collect opens it in
        // context instead of duplicating an allocation surface.
        var win = new PayRecWin { Owner = this };
        win.ShowDialog();
    }

    private void ResolveMaterialEntry(LineVm line, string? typedText)
    {
        var text = typedText?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return;
        if (string.Equals(text, line.LastMaterialText, StringComparison.Ordinal))
            return;
        line.LastMaterialText = text;

        // Composite scale labels (§3.4): a weighed label resolves by
        // its extracted material code, and the embedded weight fills
        // the line quantity — the same pre-pass the POS scan box runs.
        var (scanCode, scanQty) = TaxSvc.ParseScan(text);
        var match = TaxSvc.MatchMaterial(scanCode);
        if (match is { Kind: TaxSvc.MaterialMatchKind.Exact or TaxSvc.MaterialMatchKind.UniquePartial })
        {
            if (match.Id != line.MaterialId)
                FillLineFromMatch(line, match);
            if (scanQty is decimal weighedQty)
                line.QtyText = weighedQty.ToString("0.####", CultureInfo.InvariantCulture);
            return;
        }

        // Use the shared SerWin lookup, pre-filtered by the typed fragment
        // or scanned code. Selection remains scoped to the current company.
        var picked = SerWin.PickMaterial(this, scanQty is not null ? scanCode : text);
        if (picked is not null)
        {
            var selected = TaxSvc.MatchMaterial(picked.Code);
            if (selected is not null)
                FillLineFromMatch(line, selected);
            else
            {
                line.MaterialId = picked.Id;
                line.MaterialCode = picked.Code;
                line.Description = picked.Name;
                line.LastMaterialText = picked.Name;
            }
            if (scanQty is decimal pickedQty)
                line.QtyText = pickedQty.ToString("0.####", CultureInfo.InvariantCulture);
        }
        else if (line.MaterialId is not > 0)
        {
            _lblStatus.Text = Tf("Invoice.MaterialNotMatched", "Material not matched: {0}", text);
        }
    }

    private void ApplyPick(LineVm line, MatDirListItem pick)
    {
        var match = TaxSvc.MatchMaterial(pick.Code);
        if (match is { Kind: TaxSvc.MaterialMatchKind.Exact })
        {
            FillLineFromMatch(line, match);
            return;
        }
        line.MaterialId = pick.Id;
        line.MaterialCode = pick.Code;
        line.Description = pick.DisplayName;
        line.LastMaterialText = pick.DisplayName;
    }

    private void FillLineFromMatch(LineVm line, TaxSvc.MaterialMatch match)
    {
        line.MaterialId = match.Id;
        line.MaterialCode = match.Code;
        line.Description = match.Name;
        line.LastMaterialText = match.Name;
        if (!string.IsNullOrWhiteSpace(match.Unit))
            line.Unit = match.Unit;
        if (TaxSvc.IsEnabled && match.TaxRate > 0)
            line.TaxRate = match.TaxRate;
        // Price: on the sales side the W7a resolver below fills it
        // from the party's pricing (card price is its last fallback);
        // on the purchase side the card price is the entry default.
        if (!_doc.IsSalesSide && ParseDec(line.UnitPriceText, 0) == 0 && match.Price > 0)
            line.UnitPriceText = match.Price.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private void PickMaterial()
    {
        // The Find Material button opens the same directory search
        // as an unmatched typed entry (previously a focus-only stub).
        if (_grid.SelectedItem is not LineVm line)
        {
            if (_lines.Count == 0)
                _lines.Add(new LineVm
            {
                TaxCode = Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture) ?? string.Empty,
                TaxRate = ParseDec(_txtTaxRate.Text, TaxSvc.GetDefaultRate()),
                TaxCalculationMode = _taxCodes.FirstOrDefault(x =>
                    string.Equals(x.Code, Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))?.CalculationMode ?? "Exclusive"
            });
            line = _lines[^1];
            _grid.SelectedItem = line;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(line.WarehouseName))
                line.WarehouseName = _txtWarehouse.Text.Trim();
            var pick = MatDirWin.ShowPicker(this, line.Description);
            if (pick is not null)
            {
                ApplyPick(line, pick);
                _grid.CurrentCell = new DataGridCellInfo(line, _qtyColumn);
                _grid.BeginEdit();
            }
            _grid.Focus();
        }
        catch (Exception exL922)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL922, "InvoiceWin.PickMaterial");
            // Grid may not be ready during early load.
        }
    }

    private string PartyCodeKey() => _doc.IsSalesSide ? "Invoice.CustomerCode" : "Invoice.SupplierCode";
    private string PartyNameKey() => _doc.IsSalesSide ? "Invoice.CustomerName" : "Invoice.SupplierName";
    private string PartyAccountLabelKey() => _doc.IsSalesSide ? "Invoice.CustomerAccount" : "Invoice.SupplierAccount";

    private static string GetHeaderBrushKey(InvDocKind kind) => kind switch
    {
        InvDocKind.PurchaseInvoice or InvDocKind.PurchaseReturn => "AccentPrimary",
        InvDocKind.SalesInvoice or InvDocKind.SalesReturn => "AccentPrimary",
        _ => "AccentPrimary"
    };

    private static DataGridTextColumn TextCol(string path, double width, string? headerKey = null)
    {
        var col = new DataGridTextColumn
        {
            Binding = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width
        };
        if (!string.IsNullOrEmpty(headerKey))
            col.Header = headerKey; // BaseWin localization may rewrite headers when present
        return col;
    }


    private static TextBlock SectionTitle(string key)
        => new()
        {
            Text = key,
            FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(12),
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 0, 0, 4),
            Opacity = 0.85
        };

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.Content = Tf(key, key);
        button.MinWidth = VisuId.ButtonMinWidth;
        button.Margin = new Thickness(0, 0, 8, 0);
        try { button.SetResourceReference(StyleProperty, "VisuButtonStyle"); } catch (Exception exL983) {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, exL983, "InvoiceWin.StyleButton"); /* theme optional */ }
    }

    private static decimal ParseDec(string? text, decimal fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v)) return v;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
        return fallback;
    }

    /// <summary>
    /// Translate with an optional English fallback when the key is still missing from Lang JSON.
    /// Does not hide BaseWin.T(string).
    /// </summary>
    private static string Tf(string key, string? fallback = null)
    {
        var translated = T(key);
        if (!string.IsNullOrEmpty(translated) && !string.Equals(translated, key, StringComparison.Ordinal))
            return translated;
        return fallback ?? key;
    }

    /// <inheritdoc />
    protected override void ApplyLocalizedText()
    {
        Title = Tf(_doc.Kind switch
        {
            InvDocKind.SalesReturn => "Invoice.SalesReturnTitle",
            InvDocKind.PurchaseInvoice => "Invoice.PurchaseTitle",
            InvDocKind.PurchaseReturn => "Invoice.PurchaseReturnTitle",
            InvDocKind.StockIn => "Invoice.StockInTitle",
            InvDocKind.StockOut => "Invoice.StockOutTitle",
            InvDocKind.StockTransfer => "Invoice.StockTransferTitle",
            InvDocKind.OpeningStock => "Invoice.OpeningStockTitle",
            InvDocKind.InventoryCount => "Invoice.InventoryCountTitle",
            InvDocKind.Quotation => "Invoice.QuotationTitle",
            _ => "Invoice.SalesTitle"
        }, WindowTitleFor(_doc.Kind));

        foreach (var btn in new[] { _btnSave, _btnPost, _btnReverse, _btnPrint, _btnPdf, _btnCollect, _btnAddLine, _btnRemoveLine, _btnFindMaterial, _btnVoucher, _btnOriginal, _btnAttachments, _btnColumns })
        {
            if (btn.Tag is string key)
                btn.Content = Tf(key, btn.Content?.ToString());
        }

        _btnVoucher.Content = Tf("Invoice.Voucher", "Voucher");
        _btnOriginal.Content = Tf("Invoice.Original", "Original");

        // Rebuild payment combo captions without losing selection
        var payIdx = _cmbPayment.SelectedIndex;
        BuildPaymentCombo();
        if (payIdx >= 0 && payIdx < _cmbPayment.Items.Count)
            _cmbPayment.SelectedIndex = payIdx;

        // Headers are rewritten by column IDENTITY, never by index:
        // the chooser changes DisplayIndex, so index-based header
        // assignment would mislabel columns after a reorder.
        _descColumn.Header = Tf("Invoice.ColMaterial", "Material");
        _qtyColumn.Header = Tf("Invoice.ColQty", "Qty");
        _unitColumn.Header = Tf("Invoice.ColUnit", "Unit");
        _priceColumn.Header = Tf("Invoice.ColUnitPrice", "Unit price");
        _lineTotalColumn.Header = Tf("Invoice.ColLineTotal", "Line total");
        _codeColumn.Header = Tf("Invoice.ColCode", "Code");
        _discountColumn.Header = Tf("Invoice.ColDiscount", "Disc %");
        _taxCodeColumn.Header = Tf("Invoice.ColTaxCode", "Tax system");
        _taxRateColumn.Header = Tf("Invoice.ColTax", "Tax %");
        _marginColumn.Header = Tf("Invoice.ColMargin", "Margin");
        _warehouseColumn.Header = Tf("Invoice.ColWarehouse", "Warehouse");
        _notesColumn.Header = Tf("Invoice.ColNotes", "Statement");
        _chkExport.Content = Tf("Invoice.ExportInvoice", "Export invoice");

        RefreshTotals();
        _grid.ContextMenu = BuildInvoiceLineContextMenu();
    }

    // ---- Line view-model ----
    private sealed class LineVm : INotifyPropertyChanged
    {
        private string _materialCode = string.Empty;
        private string _description = string.Empty;
        private string _unit = string.Empty;
        private string _qtyText = "1";
        private string _unitPriceText = "0";
        private string _discountPctText = "0";
        private string _taxRateText = "0";
        private string _taxCode = string.Empty;
        private string _warehouseName = string.Empty;
        private string _notes = string.Empty;

        public long? MaterialId { get; set; }

        /// <summary>UI state only (never persisted): the last typed
        /// material text the entry matcher already processed for this
        /// line, so tabbing through a cell does not re-run the match
        /// or re-open the search for the same text.</summary>
        public string? LastMaterialText { get; set; }
        public int? WarehouseId { get; set; }
        public string MaterialCode { get => _materialCode; set { _materialCode = value; OnPropertyChanged(); } }
        public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnPropertyChanged(); } }
        public string QtyText { get => _qtyText; set { _qtyText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string UnitPriceText { get => _unitPriceText; set { _unitPriceText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string DiscountPctText { get => _discountPctText; set { _discountPctText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string TaxRateText { get => _taxRateText; set { _taxRateText = value; OnPropertyChanged(); NotifyTotals(); } }
        public string TaxCode { get => _taxCode; set { _taxCode = value ?? string.Empty; OnPropertyChanged(); } }
        public string TaxCalculationMode { get; set; } = "Exclusive";
        public string WarehouseName { get => _warehouseName; set { _warehouseName = value; OnPropertyChanged(); } }
        public string Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }

        public decimal TaxRate
        {
            get => ParseDec(_taxRateText, 0);
            set => TaxRateText = value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public string LineTotalText
        {
            get
            {
                var line = ToLine();
                return line.LineTotal.ToString("N3", CultureInfo.CurrentCulture);
            }
        }

        // ---- Live margin (W5) ----
        /// <summary>Current valuation cost per base unit, resolved
        /// through TaxSvc.GetMaterialCost; null = unavailable.</summary>
        public decimal? UnitCost { get; set; }
        /// <summary>The material the cached <see cref="UnitCost"/>
        /// belongs to; a different material re-resolves the cost.</summary>
        public long? CostMaterialId { get; set; }

        /// <summary>(Net unit price − cost) × qty, or null when the
        /// cost is unavailable. Net = price after the line discount,
        /// the same basis the pricing guard's margin rule uses.</summary>
        public decimal? MarginAmount
        {
            get
            {
                if (UnitCost is not decimal cost) return null;
                var net = ParseDec(UnitPriceText, 0) * (1m - ParseDec(DiscountPctText, 0) / 100m);
                return (net - cost) * ParseDec(QtyText, 0);
            }
        }

        public string MarginText => MarginAmount is decimal margin
            ? margin.ToString("N2", CultureInfo.CurrentCulture)
            : "—";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyTotals()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotalText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarginText)));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public InvLine ToLine() => new()
        {
            MaterialId = MaterialId,
            MaterialCode = MaterialCode,
            Description = Description,
            Unit = Unit,
            Qty = ParseDec(QtyText, 0),
            UnitPrice = ParseDec(UnitPriceText, 0),
            DiscountPct = ParseDec(DiscountPctText, 0),
            TaxCode = TaxCode,
            TaxCalculationMode = TaxCalculationMode,
            TaxRate = ParseDec(TaxRateText, 0),
            WarehouseId = WarehouseId,
            WarehouseName = WarehouseName,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim()
        };

        public static LineVm FromLine(InvLine line) => new()
        {
            MaterialId = line.MaterialId,
            MaterialCode = line.MaterialCode,
            Description = line.Description,
            Unit = line.Unit,
            QtyText = line.Qty.ToString("0.###", CultureInfo.InvariantCulture),
            UnitPriceText = line.UnitPrice.ToString("0.####", CultureInfo.InvariantCulture),
            DiscountPctText = line.DiscountPct.ToString("0.##", CultureInfo.InvariantCulture),
            TaxCode = line.TaxCode,
            TaxCalculationMode = line.TaxCalculationMode,
            TaxRateText = line.TaxRate.ToString("0.##", CultureInfo.InvariantCulture),
            WarehouseId = line.WarehouseId,
            WarehouseName = line.WarehouseName,
            Notes = line.Notes ?? string.Empty
        };

        private static decimal ParseDec(string? text, decimal fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var v)) return v;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }
    }
}
