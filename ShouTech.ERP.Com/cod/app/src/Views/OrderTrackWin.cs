using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Order tracking (Orders.TrackOrders + Orders.OrderReceiptDelivery):
/// every posted order with its fulfilment % (posted delivery/receipt notes
/// linked in document_links) and billing % (posted invoices), computed live
/// by OrderService.GetProgressAsync. Double-click opens the order.
/// </summary>
public sealed class OrderTrackWin : BaseWin
{
    private readonly ComboBox _cmbKind = new();
    private readonly Button _btnRefresh = new();
    private readonly Button _btnOpen = new();
    private readonly Button _btnClose = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _lblStatus = new();
    private readonly ObservableCollection<TrackRow> _rows = new();
    private Guid _companyId;

    public OrderTrackWin()
    {
        Width = 920;
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
        StyleButton(_btnRefresh, "Common.Refresh");
        StyleButton(_btnOpen, "DocSearch.Open");
        StyleButton(_btnClose, "Common.Close");
        _btnRefresh.Click += async (_, _) => await LoadAsync();
        _btnOpen.Click += (_, _) => OpenSelected();
        _btnClose.Click += (_, _) => Close();
        foreach (var b in new[] { _btnRefresh, _btnOpen, _btnClose })
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
        footer.Child = _lblStatus;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Filter on the shared 3-pair scheme (pair 0 used) so its edges
        // align with the other list windows' filters.
        var filters = CreateFormGrid(3);
        filters.Margin = new Thickness(0, 0, 0, 8);
        _cmbKind.ItemsSource = new[] { "SO", "PO" };
        _cmbKind.SelectedIndex = 0;
        _cmbKind.SelectionChanged += async (_, _) => await LoadAsync();
        AddFormField(filters, 0, 0, "DocSearch.Kind", _cmbKind);
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
        _grid.Columns.Add(TextCol(nameof(TrackRow.DocNo), 110));
        _grid.Columns.Add(TextCol(nameof(TrackRow.DateText), 95));
        _grid.Columns.Add(TextCol(nameof(TrackRow.PartyText), 230));
        _grid.Columns.Add(TextCol(nameof(TrackRow.TotalText), 110));
        _grid.Columns.Add(TextCol(nameof(TrackRow.StatusText), 95));
        _grid.Columns.Add(TextCol(nameof(TrackRow.FulfilledText), 105));
        _grid.Columns.Add(TextCol(nameof(TrackRow.BilledText), 105));
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
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _rows.Clear();
        if (AppHost.Orders is null || _companyId == Guid.Empty)
        {
            _lblStatus.Text = T("OrderDoc.ServiceUnavailable");
            return;
        }
        try
        {
            var kind = _cmbKind.SelectedItem as string ?? "SO";
            var side = kind == "SO" ? CommercialDocSide.Sales : CommercialDocSide.Purchase;
            // The tracking schedule shows every matching order — no row cap.
            var orders = await AppHost.Orders.SearchAsync(side, _companyId, kind, CommercialDocumentStatus.Posted, null, null, null, int.MaxValue);
            foreach (var order in orders)
            {
                var progress = await AppHost.Orders.GetProgressAsync(side, order.DocId);
                _rows.Add(new TrackRow
                {
                    DocId = order.DocId,
                    Side = side,
                    DocKind = order.DocKind,
                    DocNo = order.DocNo,
                    DateText = order.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    PartyText = string.IsNullOrWhiteSpace(order.PartyCode) ? order.PartyName ?? "" : $"{order.PartyCode} - {order.PartyName}",
                    TotalText = order.GrandTotal.ToString("N2", CultureInfo.InvariantCulture),
                    StatusText = progress.PerFulfilled >= 100m && progress.PerBilled >= 100m
                        ? T("OrderDoc.Completed")
                        : T("OrderDoc.Open"),
                    FulfilledText = progress.PerFulfilled.ToString("N1", CultureInfo.InvariantCulture) + "%",
                    BilledText = progress.PerBilled.ToString("N1", CultureInfo.InvariantCulture) + "%"
                });
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
        if (_grid.SelectedItem is not TrackRow row) return;
        var ord = new OrdWin(row.DocKind == CommercialDocKinds.SalesOrder ? OrdKind.Sales : OrdKind.Purchase);
        ord.Show();
        _ = ord.LoadOrderAsync(row.DocId);
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("OrderDoc.TrackTitle");
        if (_grid.Columns.Count >= 7)
        {
            _grid.Columns[0].Header = T("OrderDoc.OrderNo");
            _grid.Columns[1].Header = T("OrderDoc.Date");
            _grid.Columns[2].Header = T("OrderDoc.Partner");
            _grid.Columns[3].Header = T("Invoice.Total");
            _grid.Columns[4].Header = T("OrderDoc.Status");
            _grid.Columns[5].Header = T("OrderDoc.Fulfilled");
            _grid.Columns[6].Header = T("OrderDoc.Billed");
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

    private sealed class TrackRow
    {
        public long DocId { get; set; }
        public CommercialDocSide Side { get; set; }
        public string DocKind { get; set; } = "";
        public string DocNo { get; set; } = "";
        public string DateText { get; set; } = "";
        public string PartyText { get; set; } = "";
        public string TotalText { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string FulfilledText { get; set; } = "";
        public string BilledText { get; set; } = "";
    }
}
