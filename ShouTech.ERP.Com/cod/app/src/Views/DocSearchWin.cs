using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Commercial document search (RS-5/RS-6): one window serves invoice,
/// quotation and order searches from the ribbon. Filters by kind, status,
/// free text and date range over sales_documents / purchase_documents;
/// Open loads the document into its owning window (InvoiceWin for
/// invoices/quotations, OrdWin for orders). Quotations can be marked Lost
/// here (real status write through OrderService).
/// </summary>
public sealed class DocSearchWin : BaseWin
{
    public enum DocSearchMode
    {
        Invoices,
        Quotations,
        Orders
    }

    private readonly DocSearchMode _mode;
    private readonly ComboBox _cmbStatus = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtSearch = new() { Width = 200, Height = 26 };
    private readonly DatePicker _dtFrom = new() { Width = 120, Height = 26 };
    private readonly DatePicker _dtTo = new() { Width = 120, Height = 26 };
    private readonly Button _btnSearch = new();
    private readonly Button _btnOpen = new();
    private readonly Button _btnSetLost = new();
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
        MinWidth = 760;
        MinHeight = 420;
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
        StyleButton(_btnClose, "Common.Close");
        _btnSearch.Click += async (_, _) => await SearchAsync();
        _btnOpen.Click += (_, _) => OpenSelected();
        _btnSetLost.Click += async (_, _) => await SetLostAsync();
        _btnClose.Click += (_, _) => Close();
        foreach (var b in new[] { _btnSearch, _btnOpen, _btnSetLost, _btnClose })
            toolbar.Children.Add(b);
        _btnSetLost.Visibility = _mode == DocSearchMode.Quotations ? Visibility.Visible : Visibility.Collapsed;

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

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _cmbStatus.ItemsSource = new[] { "", CommercialDocumentStatus.Draft, CommercialDocumentStatus.Posted, CommercialDocumentStatus.Closed, CommercialDocumentStatus.Cancelled, CommercialDocumentStatus.Lost };
        _cmbStatus.SelectedIndex = 0;
        filters.Children.Add(Labeled("DocSearch.Status", _cmbStatus));
        filters.Children.Add(Labeled("DocSearch.Search", _txtSearch));
        filters.Children.Add(Labeled("DocSearch.From", _dtFrom));
        filters.Children.Add(Labeled("DocSearch.To", _dtTo));
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
            catch
            {
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
            var status = _cmbStatus.SelectedItem as string;
            var from = _dtFrom.SelectedDate is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
            var to = _dtTo.SelectedDate is { } t ? DateOnly.FromDateTime(t) : (DateOnly?)null;
            var search = string.IsNullOrWhiteSpace(_txtSearch.Text) ? null : _txtSearch.Text.Trim();
            foreach (var (side, kind) in Queries())
            {
                var docs = await AppHost.Orders.SearchAsync(side, _companyId, kind,
                    string.IsNullOrEmpty(status) ? null : status, search, from, to, 500);
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
            _lblStatus.Text = ex.Message;
        }
    }

    private void OpenSelected()
    {
        if (_grid.SelectedItem is not DocRow row) return;
        Window win;
        if (row.DocKind is CommercialDocKinds.SalesOrder or CommercialDocKinds.PurchaseOrder)
        {
            var ord = new OrdWin(row.DocKind == CommercialDocKinds.SalesOrder ? OrdKind.Sales : OrdKind.Purchase);
            win = ord;
            win.Show();
            _ = ord.LoadOrderAsync(row.DocId);
            return;
        }
        var invoiceKind = row.DocKind switch
        {
            CommercialDocKinds.SalesInvoice => InvDocKind.SalesInvoice,
            CommercialDocKinds.PurchaseInvoice => InvDocKind.PurchaseInvoice,
            CommercialDocKinds.SalesQuotation => InvDocKind.Quotation,
            _ => InvDocKind.SalesInvoice
        };
        win = new InvoiceWin(invoiceKind);
        win.Show();
    }

    private async Task SetLostAsync()
    {
        if (_grid.SelectedItem is not DocRow row || AppHost.Orders is null) return;
        var confirm = MessageBox.Show(this, T("DocSearch.ConfirmLost"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            await AppHost.Orders.MarkQuotationLostAsync(row.DocId, CurrentUserId);
            await SearchAsync();
        }
        catch (Exception ex)
        {
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

    private static StackPanel Labeled(string key, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
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
