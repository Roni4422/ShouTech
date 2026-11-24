using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ShouTech.App.Configuration;
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
public sealed class InvoiceWin : BaseWin, IWindowMode
{
    protected override string? ScheduleTintName => "Mint";

    private readonly InvDoc _doc;
    private readonly ObservableCollection<LineVm> _lines = new();

    // ---- Right header (document + payment) ----
    private readonly ComboBox _cmbPayment = new();
    private readonly TextBox _txtDocNo = new() { IsReadOnly = true };
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today };
    private readonly TextBox _txtCurrency = new();
    private readonly TextBox _txtTaxRate = new();
    private readonly ComboBox _cmbTaxCode = new();
    private readonly DataGridComboBoxColumn _taxCodeColumn = new();
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

    // ---- Notes / grid / actions ----
    private readonly TextBox _txtNotes = new() { AcceptsReturn = true };
    private readonly DataGrid _grid = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnReverse = new();
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
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0)
        };
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Invoice.Post");
        StyleButton(_btnReverse, "Invoice.Reverse");
        _btnReverse.IsEnabled = false;
        _btnReverse.Click += (_, _) => ReverseCurrentDocument();
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnPdf, "Invoice.Pdf");
        StyleButton(_btnAddLine, "Invoice.AddLine");
        StyleButton(_btnRemoveLine, "Invoice.RemoveLine");
        StyleButton(_btnFindMaterial, "Invoice.FindMaterial");
        _btnSave.Click += (_, _) => SaveDraft();
        _btnPost.Click += (_, _) => PostDoc();
        _btnPrint.Click += (_, _) => InvPrn.Print(CaptureDoc(), CurrentCompanyDisplay);
        _btnPdf.Click += (_, _) => InvPrn.ExportPdf(CaptureDoc(), CurrentCompanyDisplay);
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
        _btnAddLine.Click += (_, _) =>
        {
            _lines.Add(new LineVm
            {
                TaxCode = Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture) ?? string.Empty,
                TaxRate = ParseDec(_txtTaxRate.Text, TaxSvc.GetDefaultRate()),
                TaxCalculationMode = _taxCodes.FirstOrDefault(x =>
                    string.Equals(x.Code, Convert.ToString(_cmbTaxCode.SelectedValue, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))?.CalculationMode ?? "Exclusive",
                WarehouseName = _txtWarehouse.Text.Trim()
            });
            RefreshTotals();
        };
        _btnRemoveLine.Click += (_, _) =>
        {
            if (_grid.SelectedItem is LineVm line)
            {
                _lines.Remove(line);
                RefreshTotals();
            }
        };
        _btnFindMaterial.Click += (_, _) => PickMaterial();
        foreach (var b in new[] { _btnSave, _btnPost, _btnReverse, _btnPrint, _btnPdf, _btnAddLine, _btnRemoveLine, _btnFindMaterial })
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
        var gridHost = new Border
        {
            Child = _grid,
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
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleButton(_btnVoucher, "Invoice.Voucher");
        StyleButton(_btnOriginal, "Invoice.Original");
        StyleButton(_btnAttachments, "PurchaseAttachments.Open");
        _btnAttachments.Visibility = _doc.Kind is InvDocKind.PurchaseInvoice or InvDocKind.PurchaseReturn ? Visibility.Visible : Visibility.Collapsed;
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
        var payment = new Grid { Margin = new Thickness(gap) };
        var paymentRows = _doc.Kind == InvDocKind.Quotation ? 9 : 8;
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
        panel.Children.Add(grid);
        return panel;
    }

    /// <summary>
    /// Side label + control in one form row of the host group grid: the
    /// label sits in the group's shared label column (FormLabelWidth) and
    /// the control stretches in the field column, so labels and field
    /// edges align down the whole group (Phenix rule — labels beside the
    /// field, never above it).
    /// </summary>
    private static void AddLabeledCell(Grid host, int row, string label, FrameworkElement control)
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
            Margin = new Thickness(0, 0, 8, 8)
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
        foreach (var tb in new[] { _txtLinesTotal, _txtInvoiceValue, _txtPaid, _txtLinesDiscount, _txtPaymentValue, _txtRemaining, _txtLinesExtra, _txtNet })
        {
            tb.Width = double.NaN;
            tb.MinWidth = 70;
            tb.HorizontalAlignment = HorizontalAlignment.Stretch;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Margin = new Thickness(0);
        }

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var colExtra = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        colExtra.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        colExtra.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabeledCell(colExtra, 0, Tf("Invoice.LinesExtra", "Lines extra"), _txtLinesExtra);
        AddLabeledCell(colExtra, 1, Tf("Invoice.Net", "Net"), _txtNet);

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
        _grid.CanUserAddRows = false;
        _grid.CanUserResizeRows = false;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
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

        _grid.ItemsSource = _lines;
        _grid.Columns.Clear();
        _grid.Columns.Add(TextCol(nameof(LineVm.MaterialCode), 80, Tf("Invoice.ColCode", "Code")));
        _grid.Columns.Add(TextCol(nameof(LineVm.Description), 180, Tf("Invoice.ColMaterial", "Material")));
        _grid.Columns.Add(TextCol(nameof(LineVm.QtyText), 70, Tf("Invoice.ColQty", "Qty")));
        _grid.Columns.Add(TextCol(nameof(LineVm.Unit), 60, Tf("Invoice.ColUnit", "Unit")));
        _grid.Columns.Add(TextCol(nameof(LineVm.UnitPriceText), 80, Tf("Invoice.ColUnitPrice", "Unit price")));
        _grid.Columns.Add(TextCol(nameof(LineVm.DiscountPctText), 60, Tf("Invoice.ColDiscount", "Disc %")));
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
        var taxRateColumn = TextCol(nameof(LineVm.TaxRateText), 60, Tf("Invoice.ColTax", "Tax %"));
        taxRateColumn.IsReadOnly = !TaxSvc.IsEnabled || _taxCodes.Count == 0;
        _grid.Columns.Add(taxRateColumn);
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Tf("Invoice.ColLineTotal", "Line total"),
            Binding = new Binding(nameof(LineVm.LineTotalText)) { Mode = BindingMode.OneWay },
            Width = 90,
            IsReadOnly = true
        });
        _grid.Columns.Add(TextCol(nameof(LineVm.WarehouseName), 110, Tf("Invoice.ColWarehouse", "Warehouse")));

        _grid.CellEditEnding += OnCellEditEnding;
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e.Row.Item is LineVm line)
            {
                if (ReferenceEquals(e.Column, _taxCodeColumn) && TaxSvc.IsEnabled)
                {
                    var selectedTax = _taxCodes.FirstOrDefault(x =>
                        string.Equals(x.Code, line.TaxCode, StringComparison.OrdinalIgnoreCase));
                    line.TaxRate = selectedTax?.Rate ?? 0m;
                    line.TaxCalculationMode = selectedTax?.CalculationMode ?? "Exclusive";
                }

                line.NotifyTotals();
                RefreshTotals();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ResolveParty()
    {
        var found = TaxSvc.ResolveParty(_txtPartyCode.Text, customer: _doc.IsSalesSide);
        if (found is null) return;
        _doc.PartyId = found.Value.Id;
        _txtPartyName.Text = found.Value.Name;
        _doc.PartyName = found.Value.Name;
        _doc.PartyCode = _txtPartyCode.Text.Trim();

        if (_doc.PaymentMethod == InvPaymentMethod.Credit)
            _txtPartyAccount.Text = found.Value.Name;
    }

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

    private InvDoc CaptureDoc()
    {
        _doc.DocNo = _txtDocNo.Text.Trim();
        _doc.DocDate = _dtDate.SelectedDate ?? DateTime.Today;
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
        _doc.OriginalDocId = doc.OriginalDocId;
        _doc.OriginalDocKind = doc.OriginalDocKind;
        _doc.OriginalDocNo = doc.OriginalDocNo;
        _doc.JournalDocNo = doc.JournalDocNo;

        _txtDocNo.Text = doc.DocNo;
        _syncingDocumentDate = true;
        try { _dtDate.SelectedDate = doc.DocDate; }
        finally { _syncingDocumentDate = false; }
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
        _btnPost.IsEnabled = !locked;
        _grid.IsReadOnly = locked;
        UpdateVoucherOriginalButtons();
        RefreshTotals();
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
        if (_doc.Kind is not (InvDocKind.PurchaseInvoice or InvDocKind.PurchaseReturn) || LoadedDocumentId is not long documentId)
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

    private void PostDoc()
    {
        if (!EnsureSalesPermission("post", "PostDoc")) return;
        // Posting a never-saved document saves it first (below), so the
        // create right is required for that path as well.
        if (_doc.DocId <= 0 && !EnsureSalesPermission("create", "PostDoc")) return;
        if (!AskConfirm("Invoice.ConfirmPost")) return;
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
            InvRepo.Post(doc.DocId, doc.Kind, AppState.CurrentUser?.Username ?? Environment.UserName, captures);
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

    private void PickMaterial()
    {
        // Ensure a line is selected; focus description cell for manual entry.
        // Dedicated material search dialog can replace this when MatCard lookup is wired.
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
            if (_grid.Columns.Count > 1)
                _grid.CurrentCell = new DataGridCellInfo(line, _grid.Columns[1]);
            _grid.BeginEdit();
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

        foreach (var btn in new[] { _btnSave, _btnPost, _btnReverse, _btnPrint, _btnPdf, _btnAddLine, _btnRemoveLine, _btnFindMaterial, _btnVoucher, _btnOriginal, _btnAttachments })
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

        if (_grid.Columns.Count >= 10)
        {
            _grid.Columns[0].Header = Tf("Invoice.ColCode", "Code");
            _grid.Columns[1].Header = Tf("Invoice.ColMaterial", "Material");
            _grid.Columns[2].Header = Tf("Invoice.ColQty", "Qty");
            _grid.Columns[3].Header = Tf("Invoice.ColUnit", "Unit");
            _grid.Columns[4].Header = Tf("Invoice.ColUnitPrice", "Unit price");
            _grid.Columns[5].Header = Tf("Invoice.ColDiscount", "Disc %");
            _grid.Columns[6].Header = Tf("Invoice.ColTaxCode", "Tax system");
            _grid.Columns[7].Header = Tf("Invoice.ColTax", "Tax %");
            _grid.Columns[8].Header = Tf("Invoice.ColLineTotal", "Line total");
            _grid.Columns[9].Header = Tf("Invoice.ColWarehouse", "Warehouse");
        }

        RefreshTotals();
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

        public long? MaterialId { get; set; }
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

        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyTotals()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LineTotalText)));

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
            WarehouseName = WarehouseName
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
            WarehouseName = line.WarehouseName
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
