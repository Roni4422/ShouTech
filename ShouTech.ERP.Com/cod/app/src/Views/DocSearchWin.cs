using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Commercial document search (RS-5/RS-6): one window serves invoice,
/// quotation and order searches from the ribbon. Filters by kind, status,
/// free text and date range over sales_documents / purchase_documents;
/// Open loads THE selected document into its owning window — the row's
/// database id flows through (InvoiceWin(docId, kind) for
/// invoices/quotations, OrdWin.LoadOrderAsync for orders); Step 9 closed
/// the sweep defect (ORD-07 / I-8) where Open built a blank new
/// InvoiceWin and discarded the id. Quotations can be marked Lost and
/// converted to a sales order here (real writes through OrderService).
/// </summary>
public sealed class DocSearchWin : BaseWin, IWindowMode
{
    protected override string? ScheduleTintName => "Denim";
    public enum DocSearchMode
    {
        Invoices,
        Quotations,
        Orders
    }

    private readonly DocSearchMode _mode;

    /// <summary>The search mode this window was opened with (Step 11
    /// router identity: Invoices / Quotations / Orders).</summary>
    public DocSearchMode Mode => _mode;
    public string ModeKey => _mode.ToString();

    private readonly ComboBox _cmbStatus = new();
    private readonly TextBox _txtSearch = new();
    private readonly DatePicker _dtFrom = new();
    private readonly DatePicker _dtTo = new();
    private readonly Button _btnSearch = new();
    private readonly Button _btnOpen = new();
    private readonly Button _btnSetLost = new();
    private readonly Button _btnConvertOrder = new();
    private readonly Button _btnClose = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _lblStatus = new();
    private readonly ObservableCollection<DocRow> _rows = new();
    private Guid _companyId;

    public DocSearchWin(DocSearchMode mode = DocSearchMode.Orders)
    {
        _mode = mode;
        Width = 900;
        Height = 540;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        ApplyLocalizedText();
        Loaded += OnLoaded;
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnSearch, "Common.Search");
        StyleButton(_btnOpen, "DocSearch.Open");
        StyleButton(_btnSetLost, "DocSearch.SetLost");
        StyleButton(_btnConvertOrder, "DocSearch.ConvertToOrder");
        StyleButton(_btnClose, "Common.Close");
        _btnSearch.Click += async (_, _) => await SearchAsync();
        _btnOpen.Click += (_, _) => OpenSelected();
        _btnSetLost.Click += async (_, _) => await SetLostAsync();
        _btnConvertOrder.Click += async (_, _) => await ConvertToOrderAsync();
        _btnClose.Click += (_, _) => Close();
        foreach (var b in new[] { _btnSearch, _btnOpen, _btnSetLost, _btnConvertOrder, _btnClose })
            toolbar.Children.Add(b);
        _btnSetLost.Visibility = _mode == DocSearchMode.Quotations ? Visibility.Visible : Visibility.Collapsed;
        _btnConvertOrder.Visibility = _mode == DocSearchMode.Quotations ? Visibility.Visible : Visibility.Collapsed;

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
        footer.Child = _lblStatus;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Filter bar = one shared column grid (4 pairs), side labels,
        // equal quarters aligned to one grid (Phenix rule).
        var filters = CreateFormGrid(4);
        filters.Margin = new Thickness(0, 0, 0, 8);
        _cmbStatus.ItemsSource = new[] { "", CommercialDocumentStatus.Draft, CommercialDocumentStatus.Posted, CommercialDocumentStatus.Closed, CommercialDocumentStatus.Cancelled, CommercialDocumentStatus.Lost, CommercialDocumentStatus.Expired };
        _cmbStatus.SelectedIndex = 0;
        AddFormField(filters, 0, 0, "DocSearch.Status", _cmbStatus);
        AddFormField(filters, 0, 1, "DocSearch.Search", _txtSearch);
        AddFormField(filters, 0, 2, "DocSearch.From", _dtFrom);
        AddFormField(filters, 0, 3, "DocSearch.To", _dtTo);
        Grid.SetRow(filters, 0);
        body.Children.Add(filters);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.IsReadOnly = true;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");
        _grid.ItemsSource = _rows;
        _grid.Columns.Add(TextCol(nameof(DocRow.DocNo), 110));
        _grid.Columns.Add(TextCol(nameof(DocRow.KindText), 90));
        _grid.Columns.Add(TextCol(nameof(DocRow.DateText), 95));
        _grid.Columns.Add(TextCol(nameof(DocRow.PartyText), 240));
        _grid.Columns.Add(TextCol(nameof(DocRow.TotalText), 110));
        _grid.Columns.Add(TextCol(nameof(DocRow.StatusText), 100));
        _grid.Columns.Add(TextCol(nameof(DocRow.DeliveryText), 110));
        _grid.MouseDoubleClick += (_, _) => OpenSelected();
        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppHost.JournalStore is not null)
        {
            try
            {
                _companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
            }
            catch (Exception ex)
            {
                // Step 9 (sweep ORD-13 pattern): company resolution
                // failures are reported centrally, never swallowed.
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "DocSearchWin.OnLoaded");
                _companyId = Guid.Empty;
            }
        }
        await SearchAsync();
    }

    private (CommercialDocSide Side, string? Kind)[] Queries()
    {
        return _mode switch
        {
            DocSearchMode.Invoices => new[]
            {
                (CommercialDocSide.Sales, (string?)CommercialDocKinds.SalesInvoice),
                (CommercialDocSide.Purchase, (string?)CommercialDocKinds.PurchaseInvoice)
            },
            DocSearchMode.Quotations => new[]
            {
                (CommercialDocSide.Sales, (string?)CommercialDocKinds.SalesQuotation)
            },
            _ => new[]
            {
                (CommercialDocSide.Sales, (string?)CommercialDocKinds.SalesOrder),
                (CommercialDocSide.Purchase, (string?)CommercialDocKinds.PurchaseOrder)
            }
        };
    }

    private async Task SearchAsync()
    {
        _rows.Clear();
        if (AppHost.Orders is null || _companyId == Guid.Empty)
        {
            _lblStatus.Text = T("OrderDoc.ServiceUnavailable");
            return;
        }
        try
        {
            if (_mode == DocSearchMode.Quotations)
            {
                // Listing quotations first sweeps Posted quotations whose
                // validity has passed into Expired, so the schedule never
                // shows a dead quotation as still Posted. A sweep failure
                // is reported centrally and does not block the listing.
                try
                {
                    await AppHost.Orders.ExpirePostedQuotationsAsync(
                        _companyId, DateOnly.FromDateTime(DateTime.Today));
                }
                catch (Exception sweepEx)
                {
                    AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, sweepEx, "DocSearchWin.ExpireQuotations");
                }
            }
            var status = _cmbStatus.SelectedItem as string;
            var from = _dtFrom.SelectedDate is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
            var to = _dtTo.SelectedDate is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
            var search = string.IsNullOrWhiteSpace(_txtSearch.Text) ? null : _txtSearch.Text.Trim();
            foreach (var (side, kind) in Queries())
            {
                // The search schedule shows every matching document — no row cap.
                var docs = await AppHost.Orders.SearchAsync(side, _companyId, kind,
                    string.IsNullOrEmpty(status) ? null : status, search, from, to, int.MaxValue);
                foreach (var doc in docs)
                {
                    _rows.Add(new DocRow
                    {
                        DocId = doc.DocId,
                        Side = side,
                        DocKind = doc.DocKind,
                        DocNo = doc.DocNo,
                        KindText = doc.DocKind,
                        DateText = doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        PartyText = string.IsNullOrWhiteSpace(doc.PartyCode) ? doc.PartyName ?? "" : $"{doc.PartyCode} - {doc.PartyName}",
                        TotalText = doc.GrandTotal.ToString("N2", CultureInfo.InvariantCulture),
                        StatusText = doc.Status,
                        DeliveryText = (doc.DeliveryDate ?? doc.ValidTill)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""
                    });
                }
            }
            _lblStatus.Text = $"{T("DocSearch.Results")}: {_rows.Count}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "DocSearchWin.Search");
            _lblStatus.Text = ex.Message;
        }
    }

    private void OpenSelected()
    {
        if (_grid.SelectedItem is not DocRow row) return;
        try
        {
            if (row.DocKind is CommercialDocKinds.SalesOrder or CommercialDocKinds.PurchaseOrder)
            {
                var ord = new OrdWin(row.DocKind == CommercialDocKinds.SalesOrder ? OrdKind.Sales : OrdKind.Purchase)
                { Owner = this };
                ord.Show();
                _ = ord.LoadOrderAsync(row.DocId);
                return;
            }
            // Step 9 (ORD-07 / I-8): the selected document's id flows
            // into InvoiceWin's loading constructor — the window shows
            // THAT document, loaded from the database. Kinds with no
            // InvoiceWin surface (chain delivery/receipt notes) refuse
            // honestly instead of falling through to a blank sales
            // invoice, which is what the old switch did for every
            // unmapped kind.
            var invoiceKind = row.DocKind switch
            {
                CommercialDocKinds.SalesInvoice => (InvDocKind?)InvDocKind.SalesInvoice,
                CommercialDocKinds.PurchaseInvoice => (InvDocKind?)InvDocKind.PurchaseInvoice,
                CommercialDocKinds.SalesQuotation => (InvDocKind?)InvDocKind.Quotation,
                "SRT" => (InvDocKind?)InvDocKind.SalesReturn,
                "PRT" => (InvDocKind?)InvDocKind.PurchaseReturn,
                _ => null
            };
            if (invoiceKind is null)
            {
                AppHost.Errors.Report(ErrorCodes.NavigationFailed,
                    new InvalidOperationException($"Document kind '{row.DocKind}' cannot be opened from search."),
                    "DocSearchWin.OpenSelected");
                _lblStatus.Text = $"{T("DocSearch.OpenUnsupported")}: {row.DocNo}";
                return;
            }
            var win = new InvoiceWin(row.DocId, invoiceKind.Value) { Owner = this };
            if (win.LoadedDocumentId != row.DocId)
            {
                // Defensive: the loading constructor already throws
                // when the document is missing, so this can only trip
                // if a future load path regresses to a blank fallback.
                win.Close();
                AppHost.Errors.Report(ErrorCodes.NavigationFailed,
                    new InvalidOperationException($"Document {row.DocId} did not load into the invoice window."),
                    "DocSearchWin.OpenSelected");
                _lblStatus.Text = $"{T("DocSearch.OpenUnsupported")}: {row.DocNo}";
                return;
            }
            win.Show();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NavigationFailed, ex, "DocSearchWin.OpenSelected");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Step 9 (sweep DS-02): the quotation → sales-order link of the
    /// chain, wired to the existing OrderService.ConvertQuotationToOrderAsync
    /// (posted, unexpired quotations only; the created order is a DRAFT
    /// the user posts from OrdWin). Before this, the service had no UI
    /// caller anywhere and a quotation could never become an order.
    /// </summary>
    private async Task ConvertToOrderAsync()
    {
        if (_grid.SelectedItem is not DocRow row || AppHost.Orders is null) return;
        if (!AppHost.Permissions.Has(PermCat.Sales.QuotationConvert))
        {
            MessageBox.Show(this, T("OrderDoc.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var orderId = await AppHost.Orders.ConvertQuotationToOrderAsync(
                row.DocId, DateOnly.FromDateTime(DateTime.Today), CurrentUserId);
            _lblStatus.Text = $"{T("DocSearch.ConvertedToOrder")}: {orderId}";
            await SearchAsync();
        }
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "DocSearchWin.ConvertToOrder");
            var info = AppHost.Errors.Resolve(ex.Code, ex.Args.ToArray());
            MessageBox.Show(this, $"{info.Message} (Error {info.Code})", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "DocSearchWin.ConvertToOrder");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SetLostAsync()
    {
        if (_grid.SelectedItem is not DocRow row || AppHost.Orders is null) return;
        if (!AppHost.Permissions.Has(PermCat.Sales.QuotationConvert))
        {
            MessageBox.Show(this, T("OrderDoc.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(this, T("DocSearch.ConfirmLost"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            await AppHost.Orders.MarkQuotationLostAsync(row.DocId, CurrentUserId);
            await SearchAsync();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "DocSearchWin.SetLost");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void ApplyLocalizedText()
    {
        Title = _mode switch
        {
            DocSearchMode.Invoices => T("DocSearch.TitleInvoices"),
            DocSearchMode.Quotations => T("DocSearch.TitleQuotations"),
            _ => T("DocSearch.TitleOrders")
        };
        if (_grid.Columns.Count >= 7)
        {
            _grid.Columns[0].Header = T("OrderDoc.OrderNo");
            _grid.Columns[1].Header = T("DocSearch.Kind");
            _grid.Columns[2].Header = T("OrderDoc.Date");
            _grid.Columns[3].Header = T("OrderDoc.Partner");
            _grid.Columns[4].Header = T("Invoice.Total");
            _grid.Columns[5].Header = T("OrderDoc.Status");
            _grid.Columns[6].Header = T("OrderDoc.DeliveryDate");
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
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
    }


    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 8, 0);
        btn.MinWidth = VisuId.ButtonMinWidth;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private static DataGridTextColumn TextCol(string path, double width)
        => new()
        {
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            Width = width,
            IsReadOnly = true
        };

    private sealed class DocRow
    {
        public long DocId { get; set; }
        public CommercialDocSide Side { get; set; }
        public string DocKind { get; set; } = "";
        public string DocNo { get; set; } = "";
        public string KindText { get; set; } = "";
        public string DateText { get; set; } = "";
        public string PartyText { get; set; } = "";
        public string TotalText { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string DeliveryText { get; set; } = "";
    }
}
