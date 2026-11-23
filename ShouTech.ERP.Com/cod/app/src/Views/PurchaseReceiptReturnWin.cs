using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>
/// Returns received but not yet invoiced goods to the supplier.
/// The repository revalidates quantities and posts stock/GRNI atomically.
/// </summary>
public sealed class PurchaseReceiptReturnWin : BaseWin
{
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly TextBox _receiptNo = new();
    private readonly TextBox _reason = new();
    private readonly TextBlock _receiptInfo = new();
    private readonly TextBlock _status = new();
    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<ReturnLineVm> _lines = new();
    private readonly Button _load = new();
    private readonly Button _post = new();
    private readonly Button _close = new();
    private Guid _companyId;
    private long? _receiptId;
    private string _loadedReceiptNo = string.Empty;
    private string _loadedSupplierName = string.Empty;
    private bool _busy;

    public PurchaseReceiptReturnWin()
    {
        Width = 1120;
        Height = 660;
        MinWidth = 760;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        ApplyLocalizedText();
        Loaded += OnLoaded;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Purchasing.ReceiptReturn.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _load, _post, _close }, T);
        _receiptInfo.Text = _receiptId is null ? string.Empty : Tf("Purchasing.ReceiptReturn.Loaded", _loadedReceiptNo, _loadedSupplierName);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Purchasing.OrderCreate))
        {
            MessageBox.Show(this, T("OrderDoc.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        try
        {
            if (AppHost.JournalStore is null)
                throw new InvalidOperationException(T("Common.OperationUnavailable"));
            _companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
            if (_companyId == Guid.Empty)
                throw new InvalidOperationException(T("Common.OperationUnavailable"));
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "PurchaseReceiptReturnWin.OnLoaded");
            _status.Text = T("Common.OperationUnavailable");
            _post.IsEnabled = false;
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        F2Ui.StyleButton(_load, "Purchasing.ReceiptReturn.LoadReceipt");
        F2Ui.StyleButton(_post, "Purchasing.ReceiptReturn.PostReturn");
        F2Ui.StyleButton(_close, "Common.Close");
        _post.IsEnabled = false;
        _load.Click += async (_, _) => await LoadReceiptAsync();
        _post.Click += async (_, _) => await PostReturnAsync();
        _close.Click += (_, _) => Close();
        buttons.Children.Add(_load);
        buttons.Children.Add(_post);
        buttons.Children.Add(_close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var content = new DockPanel();
        var form = F2Ui.CreateFormGrid(2);
        form.Margin = new Thickness(0, 0, 0, 8);
        F2Ui.AddField(_labels, form, 0, 0, "Purchasing.ReceiptReturn.ReceiptNo", _receiptNo);
        F2Ui.AddField(_labels, form, 0, 1, "Purchasing.ReceiptReturn.Reason", _reason);
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);
        _receiptInfo.Margin = new Thickness(0, 0, 0, 8);
        _receiptInfo.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(_receiptInfo, Dock.Top);
        content.Children.Add(_receiptInfo);

        F2Ui.ConfigureGrid(_grid);
        _grid.ItemsSource = _lines;
        _grid.IsReadOnly = false;
        AddColumn("Purchasing.ReceiptReturn.Line", nameof(ReturnLineVm.LineNo), 60, true);
        AddColumn("Purchasing.ReceiptReturn.Item", nameof(ReturnLineVm.MaterialCode), 110, true);
        AddColumn("Purchasing.ReceiptReturn.Description", nameof(ReturnLineVm.Description), 2, true);
        AddColumn("Purchasing.ReceiptReturn.Received", nameof(ReturnLineVm.ReceivedQty), 90, true, "N4");
        AddColumn("Purchasing.ReceiptReturn.Invoiced", nameof(ReturnLineVm.InvoicedQty), 90, true, "N4");
        AddColumn("Purchasing.ReceiptReturn.Returned", nameof(ReturnLineVm.ReturnedQty), 90, true, "N4");
        AddColumn("Purchasing.ReceiptReturn.Returnable", nameof(ReturnLineVm.ReturnableQty), 100, true, "N4");
        AddColumn("Purchasing.ReceiptReturn.ReturnQty", nameof(ReturnLineVm.ReturnQty), 105, false, "N4");
        AddColumn("Purchasing.ReceiptReturn.BatchAllocations", nameof(ReturnLineVm.BatchAllocationsText), 180, false);
        AddColumn("Purchasing.ReceiptReturn.Serials", nameof(ReturnLineVm.SerialsText), 180, false);
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateStatus));
        content.Children.Add(_grid);
        root.Children.Add(content);
        Content = root;
    }

    private void AddColumn(string key, string path, double width, bool readOnly, string? format = null)
    {
        var binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        if (format is not null) binding.StringFormat = format;
        var columnWidth = width == 2d ? new DataGridLength(2, DataGridLengthUnitType.Star) : new DataGridLength(width);
        var column = new DataGridTextColumn { Binding = binding, Width = columnWidth, IsReadOnly = readOnly };
        _headers.Add((column, key));
        _grid.Columns.Add(column);
    }

    private async Task LoadReceiptAsync()
    {
        if (_busy || AppHost.Orders is null || _companyId == Guid.Empty) return;
        if (string.IsNullOrWhiteSpace(_receiptNo.Text))
        {
            _status.Text = T("Purchasing.ReceiptReturn.ReceiptRequired");
            return;
        }
        _busy = true;
        _load.IsEnabled = false;
        try
        {
            var receipt = await AppHost.Orders.FindByNumberAsync(
                CommercialDocSide.Purchase, _companyId, CommercialDocKinds.PurchaseReceipt, _receiptNo.Text.Trim());
            if (receipt is null || receipt.Status != CommercialDocumentStatus.Posted)
                throw new InvalidOperationException(T("Purchasing.ReceiptReturn.PostedReceiptRequired"));
            var available = await AppHost.Orders.GetPurchaseReceiptReturnableLinesAsync(_companyId, receipt.DocId);
            _receiptId = receipt.DocId;
            _loadedReceiptNo = receipt.DocNo;
            _loadedSupplierName = receipt.PartyName ?? receipt.PartyCode ?? string.Empty;
            _lines.Clear();
            foreach (var line in available)
                _lines.Add(new ReturnLineVm
                {
                    ReceiptLineNo = line.LineNo,
                    LineNo = line.LineNo,
                    MaterialId = line.MaterialId,
                    MaterialCode = line.MaterialCode ?? string.Empty,
                    Description = line.Description,
                    Unit = line.Unit,
                    ReceivedQty = line.ReceivedQty,
                    InvoicedQty = line.InvoicedQty,
                    ReturnedQty = line.ReturnedQty,
                    ReturnableQty = line.ReturnableQty,
                    UnitPrice = line.UnitPrice,
                    DiscountPct = line.DiscountPct,
                    WarehouseId = line.WarehouseId
                });
            _receiptInfo.Text = Tf("Purchasing.ReceiptReturn.Loaded", _loadedReceiptNo, _loadedSupplierName);
            _post.IsEnabled = _lines.Any(x => x.ReturnableQty > 0m);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseReceiptReturnWin.LoadReceiptAsync");
            _receiptId = null;
            _lines.Clear();
            _receiptInfo.Text = string.Empty;
            _loadedReceiptNo = string.Empty;
            _loadedSupplierName = string.Empty;
            _status.Text = T("Common.OperationUnavailable");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _load.IsEnabled = true;
        }
    }

    private async Task PostReturnAsync()
    {
        if (_busy || _receiptId is not long receiptId || AppHost.Orders is null) return;
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _lines.Where(x => x.ReturnQty > 0m).ToArray();
        if (string.IsNullOrWhiteSpace(_reason.Text) || selected.Length == 0 || selected.Any(x => x.ReturnQty < 0m || x.ReturnQty > x.ReturnableQty))
        {
            _status.Text = T("Purchasing.ReceiptReturn.InvalidQuantities");
            return;
        }
        var confirm = MessageBox.Show(this, T("Purchasing.ReceiptReturn.ConfirmPost"),
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        _post.IsEnabled = false;
        try
        {
            var drafts = selected.Select(x => new CommercialLineDraft(
                x.MaterialId, x.MaterialCode, x.Description, x.Unit, x.ReturnQty,
                x.UnitPrice, x.DiscountPct, 0m, x.WarehouseId, x.ReceiptLineNo,
                ParseBatchAllocations(x.BatchAllocationsText),
                ParseSerials(x.SerialsText))).ToArray();
            var result = await AppHost.Orders.CreatePurchaseReceiptReturnAsync(
                _companyId, receiptId, DateOnly.FromDateTime(DateTime.Today), drafts, CurrentUserId, _reason.Text.Trim());
            MessageBox.Show(this,
                Tf("Purchasing.ReceiptReturn.Created", result.ReturnDocNo),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            _receiptId = null;
            _lines.Clear();
            _receiptInfo.Text = string.Empty;
            _loadedReceiptNo = string.Empty;
            _loadedSupplierName = string.Empty;
            _status.Text = string.Empty;
            _receiptNo.Clear();
            _reason.Clear();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseReceiptReturnWin.PostReturnAsync");
            _status.Text = T("Common.OperationUnavailable");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _post.IsEnabled = _receiptId is not null && _lines.Any(x => x.ReturnableQty > 0m);
        }
    }

    private void UpdateStatus()
    {
        var total = _lines.Sum(x => Math.Max(0m, x.ReturnQty));
        _status.Text = Tf("Purchasing.ReceiptReturn.Total", total);
    }

    private static IReadOnlyList<CommercialBatchAllocationDraft>? ParseBatchAllocations(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = new List<CommercialBatchAllocationDraft>();
        foreach (var token in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0])
                || !decimal.TryParse(pair[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var qty)
                || qty <= 0m)
                throw new FormatException(T("Purchasing.ReceiptReturn.InvalidTracking"));
            result.Add(new CommercialBatchAllocationDraft(pair[0], qty));
        }
        return result;
    }

    private static IReadOnlyList<CommercialSerialSelectionDraft>? ParseSerials(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var serials = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (serials.Any(string.IsNullOrWhiteSpace)
            || serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Length)
            throw new FormatException(T("Purchasing.ReceiptReturn.InvalidTracking"));
        return serials.Select(serial => new CommercialSerialSelectionDraft(serial)).ToArray();
    }

    private sealed class ReturnLineVm
    {
        public int ReceiptLineNo { get; set; }
        public int LineNo { get; set; }
        public long? MaterialId { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Unit { get; set; }
        public decimal ReceivedQty { get; set; }
        public decimal InvoicedQty { get; set; }
        public decimal ReturnedQty { get; set; }
        public decimal ReturnableQty { get; set; }
        public decimal ReturnQty { get; set; }
        public string BatchAllocationsText { get; set; } = string.Empty;
        public string SerialsText { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public decimal DiscountPct { get; set; }
        public long? WarehouseId { get; set; }
    }
}
