using System;
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
/// Captures one supplier response against every line of a posted RFQ.
/// The response is posted only after validation by OrderService.
/// </summary>
public sealed class SupplierQuotationEntryWin : BaseWin
{
    private readonly long _rfqId;
    private readonly TextBox _supplierId = new();
    private readonly TextBox _supplierCode = new();
    private readonly TextBox _supplierName = new();
    private readonly TextBox _supplierReference = new();
    private readonly TextBox _currency = new() { Text = "USD" };
    private readonly TextBox _exchangeRate = new() { Text = "1" };
    private readonly TextBox _terms = new();
    private readonly DatePicker _quotationDate = new() { SelectedDate = DateTime.Today };
    private readonly DatePicker _validTill = new() { SelectedDate = DateTime.Today.AddDays(14) };
    private readonly DatePicker _deliveryDate = new();
    private readonly DataGrid _linesGrid = new();
    private readonly ObservableCollection<QuotationLineVm> _lines = new();
    private readonly TextBlock _status = new();
    private readonly Button _selectSupplier = new();
    private readonly Button _savePost = new();
    private readonly Button _close = new();
    private bool _busy;
    private readonly System.Collections.Generic.List<(TextBlock Label, string Key)> _fieldLabels = new();

    public SupplierQuotationEntryWin(long rfqId)
    {
        if (rfqId <= 0) throw new ArgumentOutOfRangeException(nameof(rfqId));
        _rfqId = rfqId;
        Width = 1120;
        Height = 720;
        MinWidth = 760;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildUi();
        ApplyLocalizedText();
        Loaded += OnLoaded;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Purchasing.SupplierQuotationEntry.Title");
        _selectSupplier.Content = T("Purchasing.SupplierQuotationEntry.SelectSupplier");
        _savePost.Content = T("Purchasing.SupplierQuotationEntry.SaveAndPost");
        _close.Content = T("Common.Close");
        foreach (var child in _fieldLabels)
            child.Label.Text = T(child.Key);
        var keys = new[] { "Line", "Item", "Description", "Quantity", "Unit", "UnitPrice", "DiscountPct", "TaxRate" };
        for (var i = 0; i < Math.Min(keys.Length, _linesGrid.Columns.Count); i++)
            _linesGrid.Columns[i].Header = T("Purchasing.SupplierQuotationEntry." + keys[i]);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Purchasing.OrderCreate))
        {
            MessageBox.Show(this, T("OrderDoc.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        if (AppHost.Orders is null)
        {
            _status.Text = T("Common.OperationUnavailable");
            return;
        }
        try
        {
            var rfq = await AppHost.Orders.FindByIdAsync(CommercialDocSide.Purchase, _rfqId);
            if (rfq is null || rfq.DocKind != CommercialDocKinds.RequestForQuotation ||
                rfq.Status != CommercialDocumentStatus.Posted)
                throw new InvalidOperationException("A posted request for quotation is required.");
            _currency.Text = rfq.CurrencyCode;
            _exchangeRate.Text = rfq.ExchangeRate.ToString(CultureInfo.CurrentCulture);
            foreach (var line in rfq.Lines.OrderBy(x => x.LineNo))
                _lines.Add(new QuotationLineVm
                {
                    SourceLineNo = line.LineNo,
                    MaterialId = line.MaterialId,
                    MaterialCode = line.MaterialCode ?? string.Empty,
                    Description = line.Description,
                    Unit = line.Unit,
                    Quantity = line.Qty,
                    UnitPrice = 0m,
                    DiscountPct = 0m,
                    TaxRate = line.TaxRate
                });
            _linesGrid.ItemsSource = _lines;
            _status.Text = Tf("Purchasing.SupplierQuotationEntry.SourceLines", _lines.Count);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "SupplierQuotationEntryWin.OnLoaded");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void BuildUi()
    {
        Title = T("Purchasing.SupplierQuotationEntry.Title");
        var root = new DockPanel { Margin = new Thickness(12) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        F2Ui.StyleButton(_selectSupplier, "Purchasing.SupplierQuotationEntry.SelectSupplier");
        F2Ui.StyleButton(_savePost, "Purchasing.SupplierQuotationEntry.SaveAndPost");
        _supplierId.IsReadOnly = true;
        _supplierCode.IsReadOnly = true;
        _supplierName.IsReadOnly = true;
        _selectSupplier.Click += (_, _) => SelectSupplier();
        F2Ui.StyleButton(_close, "Common.Close");
        _savePost.Click += async (_, _) => await SaveAndPostAsync();
        _close.Click += (_, _) => Close();
        footer.Children.Add(_savePost);
        footer.Children.Add(_close);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        var form = CreateFormGrid(2);
        form.Margin = new Thickness(0, 0, 0, 8);
        F2Ui.AddField(_fieldLabels, form, 0, 0, "Purchasing.SupplierQuotationEntry.SelectSupplier", _selectSupplier);
        F2Ui.AddField(_fieldLabels, form, 0, 1, "Purchasing.SupplierQuotationEntry.SupplierId", _supplierId);
        F2Ui.AddField(_fieldLabels, form, 1, 0, "Purchasing.SupplierQuotationEntry.SupplierCode", _supplierCode);
        F2Ui.AddField(_fieldLabels, form, 1, 1, "Purchasing.SupplierQuotationEntry.SupplierName", _supplierName);
        F2Ui.AddField(_fieldLabels, form, 2, 0, "Purchasing.SupplierQuotationEntry.Reference", _supplierReference);
        F2Ui.AddField(_fieldLabels, form, 2, 1, "Purchasing.SupplierQuotationEntry.Currency", _currency);
        F2Ui.AddField(_fieldLabels, form, 3, 0, "Purchasing.SupplierQuotationEntry.ExchangeRate", _exchangeRate);
        F2Ui.AddField(_fieldLabels, form, 3, 1, "Purchasing.SupplierQuotationEntry.Date", _quotationDate);
        F2Ui.AddField(_fieldLabels, form, 4, 0, "Purchasing.SupplierQuotationEntry.ValidTill", _validTill);
        F2Ui.AddField(_fieldLabels, form, 4, 1, "Purchasing.SupplierQuotationEntry.DeliveryDate", _deliveryDate);
        F2Ui.AddField(_fieldLabels, form, 5, 0, "Purchasing.SupplierQuotationEntry.Terms", _terms, 2);
        DockPanel.SetDock(form, Dock.Top);
        root.Children.Add(form);

        _linesGrid.AutoGenerateColumns = false;
        _linesGrid.CanUserAddRows = false;
        _linesGrid.IsReadOnly = false;
        _linesGrid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.Line"), Binding = new Binding(nameof(QuotationLineVm.SourceLineNo)), IsReadOnly = true, Width = 55 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.Item"), Binding = new Binding(nameof(QuotationLineVm.MaterialCode)), IsReadOnly = true, Width = 110 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.Description"), Binding = new Binding(nameof(QuotationLineVm.Description)), IsReadOnly = true, Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.Quantity"), Binding = new Binding(nameof(QuotationLineVm.Quantity)) { StringFormat = "N4" }, IsReadOnly = true, Width = 95 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.Unit"), Binding = new Binding(nameof(QuotationLineVm.Unit)), IsReadOnly = true, Width = 70 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.UnitPrice"), Binding = new Binding(nameof(QuotationLineVm.UnitPrice)) { StringFormat = "N4", UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 110 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.DiscountPct"), Binding = new Binding(nameof(QuotationLineVm.DiscountPct)) { StringFormat = "N2", UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 95 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("Purchasing.SupplierQuotationEntry.TaxRate"), Binding = new Binding(nameof(QuotationLineVm.TaxRate)) { StringFormat = "N2", UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 85 });
        root.Children.Add(_linesGrid);
        Content = root;
    }

    private void SelectSupplier()
    {
        var picker = new PartySearchWin { KindFilter = PartyKinds.Supplier, Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedParty is not { } supplier ||
            supplier.Kind != PartyKinds.Supplier || supplier.LedgerId <= 0)
            return;
        _supplierId.Text = supplier.LedgerId.ToString(CultureInfo.InvariantCulture);
        _supplierCode.Text = supplier.Code;
        _supplierName.Text = supplier.NameEn;
    }

    private async Task SaveAndPostAsync()
    {
        if (_busy || AppHost.Orders is null) return;
        _linesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        _linesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!long.TryParse(_supplierId.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var supplierId) || supplierId <= 0 ||
            string.IsNullOrWhiteSpace(_supplierCode.Text) || string.IsNullOrWhiteSpace(_supplierName.Text) ||
            string.IsNullOrWhiteSpace(_supplierReference.Text) ||
            !decimal.TryParse(_exchangeRate.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var exchangeRate) ||
            exchangeRate <= 0m || _lines.Count == 0 || _lines.Any(x => x.UnitPrice < 0m || x.DiscountPct is < 0m or > 100m || x.TaxRate is < 0m or > 100m))
        {
            MessageBox.Show(this, T("Purchasing.SupplierQuotationEntry.InvalidInput"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_quotationDate.SelectedDate is not { } quoteDate || _validTill.SelectedDate is not { } expiry || expiry.Date < quoteDate.Date)
        {
            MessageBox.Show(this, T("Purchasing.SupplierQuotationEntry.InvalidDates"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var currency = _currency.Text.Trim().ToUpperInvariant();
        if (currency.Length != 3)
        {
            MessageBox.Show(this, T("Purchasing.SupplierQuotationEntry.InvalidCurrency"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _busy = true;
        _savePost.IsEnabled = false;
        try
        {
            var lines = _lines.Select(x => new CommercialLineDraft(
                x.MaterialId, x.MaterialCode, x.Description, x.Unit, x.Quantity,
                x.UnitPrice, x.DiscountPct, x.TaxRate, SourceLineNo: x.SourceLineNo)).ToArray();
            var result = await AppHost.Orders.CreateSupplierQuotationFromRfqAsync(
                _rfqId, supplierId, _supplierCode.Text.Trim(), _supplierName.Text.Trim(),
                _supplierReference.Text.Trim(), DateOnly.FromDateTime(quoteDate),
                DateOnly.FromDateTime(expiry), currency, exchangeRate, lines, CurrentUserId,
                _deliveryDate.SelectedDate is { } delivery ? DateOnly.FromDateTime(delivery) : null,
                _terms.Text.Trim());
            await AppHost.Orders.PostOrderAsync(CommercialDocSide.Purchase, result.QuotationId, CurrentUserId);
            MessageBox.Show(this,
                Tf("Purchasing.SupplierQuotationEntry.Created", result.QuotationNo),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "SupplierQuotationEntryWin.SaveAndPostAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _savePost.IsEnabled = true;
        }
    }

    private sealed class QuotationLineVm
    {
        public int SourceLineNo { get; set; }
        public long? MaterialId { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Unit { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal DiscountPct { get; set; }
        public decimal TaxRate { get; set; }
    }
}
