using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>Creates, posts and reverses supplier landed-cost vouchers.
/// Completion (migration 108): receipts are picked from the posted-receipt
/// list (several receipts per voucher, per-line inclusion), and each charge
/// carries a payee and a credit mode (Payable / AlreadyBooked).</summary>
public sealed class PurchaseLandedCostWin : BaseWin
{
    private Guid _companyId;
    private readonly TextBox _number = new(), _currency = new(),
        _rate = new() { Text = "1" }, _voucherId = new(), _reversalNumber = new(), _reason = new();
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly DatePicker _reversalDate = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _receiptPicker = new();
    private readonly DataGrid _grid = new();
    private readonly DataGrid _linesGrid = new();
    private readonly DataGrid _historyGrid = new();
    private readonly TextBox _search = new();
    private readonly ObservableCollection<ChargeRow> _charges = new();
    private readonly ObservableCollection<ReceiptLineRow> _receiptLines = new();
    private readonly ObservableCollection<ReceiptOption> _receiptOptions = new();
    private readonly ObservableCollection<PayeeOption> _payeeOptions = new();
    private readonly ObservableCollection<LinkedDocOption> _linkableOptions = new();
    private readonly ObservableCollection<VoucherRow> _historyRows = new();
    private readonly HashSet<long> _addedReceipts = new();
    private readonly TextBlock _status = new();
    private readonly Button _add = new(), _remove = new(), _create = new(), _post = new(), _reverse = new(), _refresh = new(), _loadSelected = new(), _close = new(),
        _addReceipt = new(), _clearReceipts = new();

    public PurchaseLandedCostWin()
    {
        Title = T("PurchaseLandedCost.Title");
        Width = 1000; Height = 620; MinWidth = 660; MinHeight = 372;
        _grid.ItemsSource = _charges;
        _linesGrid.ItemsSource = _receiptLines;
        _historyGrid.ItemsSource = _historyRows;
        _receiptPicker.ItemsSource = _receiptOptions;
        BuildUi();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (AppHost.PurchaseLandedCosts is null || AppHost.JournalStore is null)
        {
            MessageBox.Show(this, T("PurchaseLandedCost.ServiceUnavailable"), Title);
            Close();
            return;
        }
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.VIEW")
            && !AppHost.Permissions.Has("PUR.LANDEDCOST.EDIT")
            && !AppHost.Permissions.Has("PUR.LANDEDCOST.POST")
            && !AppHost.Permissions.Has("PUR.LANDEDCOST.REVERSE"))
        {
            MessageBox.Show(this, T("PurchaseLandedCost.PermissionDenied"), Title);
            Close();
            return;
        }
        try
        {
            _companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
            if (_companyId == Guid.Empty) throw new InvalidOperationException("Company context is unavailable.");
            _charges.Add(new ChargeRow { Code = "FREIGHT", Description = "Freight", Amount = "0", Basis = "Value" });
            ApplyPermissions();
            await LoadPickersAsync();
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private async Task LoadPickersAsync()
    {
        _receiptOptions.Clear();
        foreach (var receipt in await AppHost.PurchaseLandedCosts!.ListReceiptsAsync(_companyId))
            _receiptOptions.Add(new ReceiptOption(receipt));
        _payeeOptions.Clear();
        _payeeOptions.Add(new PayeeOption { PartyId = null, Name = T("PurchaseLandedCost.ReceiptSupplier") });
        foreach (var party in await AppHost.PurchaseLandedCosts.ListPayeesAsync(_companyId))
            _payeeOptions.Add(new PayeeOption
            {
                PartyId = party.PartyId,
                Name = string.IsNullOrWhiteSpace(party.Code) ? party.Name : $"{party.Code} - {party.Name}"
            });
        _linkableOptions.Clear();
        _linkableOptions.Add(new LinkedDocOption { DocumentId = null, Name = T("PurchaseLandedCost.NoLinkedDocument") });
        foreach (var doc in await AppHost.PurchaseLandedCosts.ListLinkableDocumentsAsync(_companyId))
            _linkableOptions.Add(new LinkedDocOption
            {
                DocumentId = doc.DocumentId,
                Name = $"{doc.DocumentNumber} • {doc.DocumentDate:yyyy-MM-dd} • {doc.SupplierName} • {doc.GrandTotal:N4}"
            });
    }

    /// <summary>Re-applies every user-visible string when the shell language changes.</summary>
    protected override void ApplyLocalizedText()
    {
        Title = T("PurchaseLandedCost.Title");
        StyleButton(_add, "PurchaseLandedCost.AddCharge");
        StyleButton(_remove, "PurchaseLandedCost.RemoveCharge");
        StyleButton(_create, "PurchaseLandedCost.CreateDraft");
        StyleButton(_post, "PurchaseLandedCost.Post");
        StyleButton(_reverse, "PurchaseLandedCost.Reverse");
        StyleButton(_refresh, "PurchaseLandedCost.Refresh");
        StyleButton(_loadSelected, "PurchaseLandedCost.LoadSelected");
        StyleButton(_close, "PurchaseLandedCost.Close");
        StyleButton(_addReceipt, "PurchaseLandedCost.AddReceipt");
        StyleButton(_clearReceipts, "PurchaseLandedCost.ClearReceipts");
        if (_payeeOptions.Count > 0 && _payeeOptions[0].PartyId is null)
            _payeeOptions[0].Name = T("PurchaseLandedCost.ReceiptSupplier");
        if (_linkableOptions.Count > 0 && _linkableOptions[0].DocumentId is null)
            _linkableOptions[0].Name = T("PurchaseLandedCost.NoLinkedDocument");
        var chargeKeys = new[] { "PurchaseLandedCost.ChargeCode", "PurchaseLandedCost.Description", "PurchaseLandedCost.Amount", "PurchaseLandedCost.Basis", "PurchaseLandedCost.ManualAmounts", "PurchaseLandedCost.ExpenseAccount", "PurchaseLandedCost.Payee", "PurchaseLandedCost.CreditMode", "PurchaseLandedCost.LinkedDocument" };
        for (var i = 0; i < chargeKeys.Length && i < _grid.Columns.Count; i++) _grid.Columns[i].Header = T(chargeKeys[i]);
        var lineKeys = new[] { "PurchaseLandedCost.ColInclude", "PurchaseLandedCost.ColReceipt", "PurchaseLandedCost.ColLineId", "PurchaseLandedCost.ColLineNo", "PurchaseLandedCost.ColMaterial", "PurchaseLandedCost.ColQty", "PurchaseLandedCost.ColGoodsAmount" };
        for (var i = 0; i < lineKeys.Length && i < _linesGrid.Columns.Count; i++) _linesGrid.Columns[i].Header = T(lineKeys[i]);
        var historyKeys = new[] { "PurchaseLandedCost.ColNumber", "PurchaseLandedCost.ColReceipt", "PurchaseLandedCost.ColDate", "PurchaseLandedCost.ColAmount", "PurchaseLandedCost.ColStatus" };
        for (var i = 0; i < historyKeys.Length && i < _historyGrid.Columns.Count; i++) _historyGrid.Columns[i].Header = T(historyKeys[i]);
        LocalizeLabels(this);
    }

    private static void LocalizeLabels(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is TextBlock textBlock && textBlock.Tag is string key) textBlock.Text = T(key);
            LocalizeLabels(node);
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var status = new Border { Child = _status, Padding = new Thickness(6), BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // Action buttons live at the BOTTOM of the window (layout rule).
        var toolbar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        FlowAlign.End(toolbar);
        StyleButton(_add, "PurchaseLandedCost.AddCharge");
        StyleButton(_remove, "PurchaseLandedCost.RemoveCharge");
        StyleButton(_create, "PurchaseLandedCost.CreateDraft");
        StyleButton(_post, "PurchaseLandedCost.Post");
        StyleButton(_reverse, "PurchaseLandedCost.Reverse");
        StyleButton(_refresh, "PurchaseLandedCost.Refresh");
        StyleButton(_loadSelected, "PurchaseLandedCost.LoadSelected");
        StyleButton(_close, "PurchaseLandedCost.Close");
        StyleButton(_addReceipt, "PurchaseLandedCost.AddReceipt");
        StyleButton(_clearReceipts, "PurchaseLandedCost.ClearReceipts");
        toolbar.Children.Add(new TextBlock { Text = T("PurchaseLandedCost.Search"), Tag = "PurchaseLandedCost.Search", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        _search.Width = 150;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_search);
        _add.Click += (_, _) => _charges.Add(new ChargeRow { Code = "OTHER", Description = "", Amount = "0", Basis = "Value" });
        _remove.Click += (_, _) => { if (_grid.SelectedItem is ChargeRow row) _charges.Remove(row); };
        _create.Click += async (_, _) => await CreateDraftAsync();
        _post.Click += async (_, _) => await PostAsync();
        _reverse.Click += async (_, _) => await ReverseAsync();
        _refresh.Click += async (_, _) => await RefreshHistoryAsync();
        _loadSelected.Click += LoadSelectedVoucher;
        _close.Click += (_, _) => Close();
        _addReceipt.Click += async (_, _) => await AddReceiptAsync();
        _clearReceipts.Click += (_, _) => { _receiptLines.Clear(); _addedReceipts.Clear(); };
        foreach (var button in new[] { _add, _remove, _create, _post, _reverse, _refresh, _loadSelected, _close }) toolbar.Children.Add(button);
        DockPanel.SetDock(toolbar, Dock.Bottom);
        root.Children.Add(toolbar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(130) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 2; i++) header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddField(header, 0, 0, "PurchaseLandedCost.Number", _number);
        AddField(header, 1, 0, "PurchaseLandedCost.Date", _date);
        AddField(header, 2, 0, "PurchaseLandedCost.Currency", _currency);
        AddField(header, 3, 0, "PurchaseLandedCost.ExchangeRate", _rate);
        AddField(header, 0, 1, "PurchaseLandedCost.VoucherId", _voucherId);
        AddField(header, 1, 1, "PurchaseLandedCost.ReversalNo", _reversalNumber);
        AddField(header, 2, 1, "PurchaseLandedCost.ReversalDate", _reversalDate);
        Grid.SetRow(header, 0);
        body.Children.Add(header);

        var picker = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pickerPanel = new StackPanel { Margin = new Thickness(5, 0, 5, 0) };
        pickerPanel.Children.Add(new TextBlock { Text = T("PurchaseLandedCost.SelectReceipt"), Tag = "PurchaseLandedCost.SelectReceipt", Margin = new Thickness(0, 0, 0, 3) });
        _receiptPicker.DisplayMemberPath = nameof(ReceiptOption.Display);
        pickerPanel.Children.Add(_receiptPicker);
        Grid.SetColumn(pickerPanel, 0);
        picker.Children.Add(pickerPanel);
        _addReceipt.VerticalAlignment = VerticalAlignment.Bottom;
        _clearReceipts.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_addReceipt, 1);
        Grid.SetColumn(_clearReceipts, 2);
        picker.Children.Add(_addReceipt);
        picker.Children.Add(_clearReceipts);
        Grid.SetRow(picker, 1);
        body.Children.Add(picker);

        _linesGrid.AutoGenerateColumns = false;
        _linesGrid.CanUserAddRows = false;
        _linesGrid.IsReadOnly = false;
        _linesGrid.SelectionMode = DataGridSelectionMode.Single;
        _linesGrid.Columns.Add(new DataGridCheckBoxColumn { Header = T("PurchaseLandedCost.ColInclude"), Binding = new Binding(nameof(ReceiptLineRow.Include)), Width = 60 });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColReceipt"), Binding = new Binding(nameof(ReceiptLineRow.ReceiptNo)), Width = 110, IsReadOnly = true });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColLineId"), Binding = new Binding(nameof(ReceiptLineRow.LineId)), Width = 70, IsReadOnly = true });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColLineNo"), Binding = new Binding(nameof(ReceiptLineRow.LineNo)), Width = 60, IsReadOnly = true });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColMaterial"), Binding = new Binding(nameof(ReceiptLineRow.Material)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), IsReadOnly = true });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColQty"), Binding = new Binding(nameof(ReceiptLineRow.Quantity)), Width = 80, IsReadOnly = true });
        _linesGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColGoodsAmount"), Binding = new Binding(nameof(ReceiptLineRow.Amount)), Width = 110, IsReadOnly = true });
        Grid.SetRow(_linesGrid, 2);
        body.Children.Add(_linesGrid);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.IsReadOnly = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ChargeCode"), Binding = new Binding(nameof(ChargeRow.Code)), Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.Description"), Binding = new Binding(nameof(ChargeRow.Description)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.Amount"), Binding = new Binding(nameof(ChargeRow.Amount)), Width = 100 });
        var basisColumn = new DataGridComboBoxColumn
        {
            Header = T("PurchaseLandedCost.Basis"),
            ItemsSource = new[] { "Value", "Quantity", "Weight", "Volume", "Manual" },
            SelectedItemBinding = new Binding(nameof(ChargeRow.Basis)),
            Width = 110
        };
        _grid.Columns.Add(basisColumn);
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("PurchaseLandedCost.ManualAmounts"),
            Binding = new Binding(nameof(ChargeRow.ManualAmounts)),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = T("PurchaseLandedCost.ExpenseAccount"),
            Binding = new Binding(nameof(ChargeRow.ExpenseAccountCode)),
            Width = 110
        });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = T("PurchaseLandedCost.Payee"),
            ItemsSource = _payeeOptions,
            DisplayMemberPath = nameof(PayeeOption.Name),
            SelectedValuePath = nameof(PayeeOption.PartyId),
            SelectedValueBinding = new Binding(nameof(ChargeRow.PayeeId)),
            Width = 150
        });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = T("PurchaseLandedCost.CreditMode"),
            ItemsSource = new[] { LandedCostCreditModes.Payable, LandedCostCreditModes.Linked },
            SelectedItemBinding = new Binding(nameof(ChargeRow.CreditMode)),
            Width = 110
        });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = T("PurchaseLandedCost.LinkedDocument"),
            ItemsSource = _linkableOptions,
            DisplayMemberPath = nameof(LinkedDocOption.Name),
            SelectedValuePath = nameof(LinkedDocOption.DocumentId),
            SelectedValueBinding = new Binding(nameof(ChargeRow.LinkedDocumentId)),
            Width = 200
        });
        Grid.SetRow(_grid, 3);
        body.Children.Add(_grid);

        _historyGrid.AutoGenerateColumns = false;
        _historyGrid.IsReadOnly = true;
        _historyGrid.CanUserAddRows = false;
        _historyGrid.SelectionMode = DataGridSelectionMode.Single;
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColNumber"), Binding = new Binding(nameof(VoucherRow.Number)), Width = 130 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColReceipt"), Binding = new Binding(nameof(VoucherRow.Receipt)), Width = 130 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColDate"), Binding = new Binding(nameof(VoucherRow.Date)), Width = 110 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColAmount"), Binding = new Binding(nameof(VoucherRow.Amount)), Width = 110 });
        _historyGrid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ColStatus"), Binding = new Binding(nameof(VoucherRow.Status)), Width = 110 });
        Grid.SetRow(_historyGrid, 4);
        body.Children.Add(_historyGrid);

        var reversal = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        reversal.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddField(reversal, 0, 0, "PurchaseLandedCost.Reason", _reason);
        Grid.SetRow(reversal, 5);
        body.Children.Add(reversal);

        // Scroll guard: the whole body scrolls vertically instead of
        // clipping when the window is shrunk to the screen-fit minimum.
        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(scroll);
        Content = root;
        ApplyPermissions();
    }

    private static void AddField(Grid grid, int column, int row, string key, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(5) };
        panel.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 3) });
        control.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(control);
        Grid.SetColumn(panel, column);
        Grid.SetRow(panel, row);
        grid.Children.Add(panel);
    }

    private void ApplyPermissions()
    {
        _create.IsEnabled = AppHost.Permissions.Has("PUR.LANDEDCOST.EDIT");
        _post.IsEnabled = AppHost.Permissions.Has("PUR.LANDEDCOST.POST");
        _reverse.IsEnabled = AppHost.Permissions.Has("PUR.LANDEDCOST.REVERSE");
        _refresh.IsEnabled = _loadSelected.IsEnabled = AppHost.Permissions.Has("PUR.LANDEDCOST.VIEW");
        _add.IsEnabled = _remove.IsEnabled = _create.IsEnabled;
        _addReceipt.IsEnabled = _clearReceipts.IsEnabled = _create.IsEnabled;
        _grid.IsReadOnly = !_create.IsEnabled;
        _linesGrid.IsReadOnly = !_create.IsEnabled;
    }

    private async Task AddReceiptAsync()
    {
        if (_receiptPicker.SelectedItem is not ReceiptOption option) return;
        if (!_addedReceipts.Add(option.Receipt.ReceiptDocumentId)) return;
        try
        {
            var lines = await AppHost.PurchaseLandedCosts!.ListReceiptLinesAsync(_companyId, option.Receipt.ReceiptDocumentId);
            foreach (var line in lines)
                _receiptLines.Add(new ReceiptLineRow
                {
                    Include = true,
                    ReceiptId = line.ReceiptDocumentId,
                    ReceiptNo = line.ReceiptDocumentNumber,
                    LineId = line.ReceiptLineId,
                    LineNo = line.LineNo,
                    Material = string.IsNullOrWhiteSpace(line.MaterialCode) ? line.Description : $"{line.MaterialCode} - {line.Description}",
                    Quantity = line.BaseQuantity.ToString("N4", CultureInfo.InvariantCulture),
                    Amount = line.BaseGoodsAmount.ToString("N4", CultureInfo.InvariantCulture)
                });
            if (string.IsNullOrWhiteSpace(_currency.Text)) _currency.Text = option.Receipt.CurrencyCode;
            if (_rate.Text == "1" && option.Receipt.ExchangeRate > 0m)
                _rate.Text = option.Receipt.ExchangeRate.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _addedReceipts.Remove(option.Receipt.ReceiptDocumentId);
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_companyId == Guid.Empty || !AppHost.Permissions.Has("PUR.LANDEDCOST.VIEW")) return;
        try
        {
            _historyRows.Clear();
            var rows = await AppHost.PurchaseLandedCosts!.ListAsync(_companyId, _search.Text);
            foreach (var row in rows)
                _historyRows.Add(new VoucherRow(row.Id, row.DocumentNumber, row.ReceiptDocumentNumber,
                    row.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    row.TotalAmount.ToString("N4", CultureInfo.InvariantCulture), row.Status));
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void LoadSelectedVoucher(object? sender, RoutedEventArgs e)
    {
        if (_historyGrid.SelectedItem is not VoucherRow row) return;
        _voucherId.Text = row.Id.ToString("D");
        _number.Text = row.Number;
        _reversalNumber.Text = row.Number + "-REV";
        _status.Text = row.Status;
    }

    private async Task CreateDraftAsync()
    {
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.EDIT")) return;
        try
        {
            _linesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            _linesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var included = _receiptLines.Where(x => x.Include).ToArray();
            if (included.Length == 0) throw new InvalidOperationException(T("PurchaseLandedCost.NoReceiptSelected"));
            var selections = included
                .GroupBy(x => x.ReceiptId)
                .Select(g => new PurchaseLandedCostReceiptSelection(
                    g.Key, g.Select(x => x.LineId).ToArray()))
                .ToArray();
            var includedLineIds = included.Select(x => x.LineId).ToHashSet();
            var firstReceipt = _receiptOptions.First(x => x.Receipt.ReceiptDocumentId == selections[0].ReceiptDocumentId).Receipt;
            if (string.IsNullOrWhiteSpace(_currency.Text)) _currency.Text = firstReceipt.CurrencyCode;
            if (!decimal.TryParse(_rate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate <= 0m)
                throw new InvalidOperationException(T("PurchaseLandedCost.InvalidInput"));
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var charges = _charges.Select(row =>
            {
                if (!decimal.TryParse(row.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                    throw new FormatException(T("PurchaseLandedCost.InvalidInput"));
                IReadOnlyDictionary<long, decimal>? manualAmounts = null;
                if (string.Equals(row.Basis, "Manual", StringComparison.OrdinalIgnoreCase))
                {
                    var parsed = new Dictionary<long, decimal>();
                    foreach (var token in (row.ManualAmounts ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var pair = token.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (pair.Length != 2
                            || !long.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineId)
                            || !includedLineIds.Contains(lineId)
                            || !decimal.TryParse(pair[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var allocation))
                            throw new FormatException(T("PurchaseLandedCost.InvalidManualAmounts"));
                        if (!parsed.TryAdd(lineId, allocation))
                            throw new FormatException(T("PurchaseLandedCost.InvalidManualAmounts"));
                    }
                    manualAmounts = parsed;
                }
                return new PurchaseLandedCostChargeDraft(row.Code, row.Description, amount, row.Basis, manualAmounts,
                    string.IsNullOrWhiteSpace(row.ExpenseAccountCode) ? null : row.ExpenseAccountCode.Trim(),
                    row.PayeeId,
                    string.IsNullOrWhiteSpace(row.CreditMode) ? LandedCostCreditModes.Payable : row.CreditMode,
                    row.LinkedDocumentId);
            }).ToArray();
            var id = await AppHost.PurchaseLandedCosts!.CreateDraftAsync(_companyId, selections,
                _number.Text.Trim(), DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today),
                string.IsNullOrWhiteSpace(_currency.Text) ? firstReceipt.CurrencyCode : _currency.Text.Trim(),
                rate, charges, CurrentUserId);
            _voucherId.Text = id.ToString("D");
            _status.Text = T("PurchaseLandedCost.DraftCreated") + " " + _number.Text.Trim();
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PostAsync()
    {
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.POST")) return;
        try
        {
            if (!Guid.TryParse(_voucherId.Text, out var id)) throw new InvalidOperationException(T("PurchaseLandedCost.NoVoucher"));
            var journal = await AppHost.PurchaseLandedCosts!.PostAsync(_companyId, id, CurrentUserId);
            _status.Text = T("PurchaseLandedCost.Posted") + " " + journal.ToString("D");
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ReverseAsync()
    {
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.REVERSE")) return;
        try
        {
            if (!Guid.TryParse(_voucherId.Text, out var id)) throw new InvalidOperationException(T("PurchaseLandedCost.NoVoucher"));
            var journal = await AppHost.PurchaseLandedCosts!.ReverseAsync(_companyId, id, _reversalNumber.Text.Trim(),
                DateOnly.FromDateTime(_reversalDate.SelectedDate ?? DateTime.Today), _reason.Text, CurrentUserId);
            _status.Text = T("PurchaseLandedCost.Reversed") + " " + journal.ToString("D");
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.Content = T(key);
        button.MinWidth = 82;
        button.Margin = new Thickness(3);
        button.Padding = new Thickness(8, 4, 8, 4);
    }

    private sealed record VoucherRow(Guid Id, string Number, string Receipt, string Date, string Amount, string Status);

    private sealed record ReceiptOption(PurchaseLandedCostReceiptOption Receipt)
    {
        public string Display => $"{Receipt.ReceiptDocumentNumber} • {Receipt.ReceiptDate:yyyy-MM-dd} • {Receipt.SupplierName}";
    }

    private sealed class PayeeOption
    {
        public long? PartyId { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class LinkedDocOption
    {
        public long? DocumentId { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class ReceiptLineRow
    {
        public bool Include { get; set; }
        public long ReceiptId { get; set; }
        public string ReceiptNo { get; set; } = "";
        public long LineId { get; set; }
        public int LineNo { get; set; }
        public string Material { get; set; } = "";
        public string Quantity { get; set; } = "";
        public string Amount { get; set; } = "";
    }

    private sealed class ChargeRow
    {
        public string Code { get; set; } = "";
        public string Description { get; set; } = "";
        public string Amount { get; set; } = "0";
        public string Basis { get; set; } = "Value";
        public string ManualAmounts { get; set; } = "";
        public string ExpenseAccountCode { get; set; } = "";
        public long? PayeeId { get; set; }
        public string CreditMode { get; set; } = LandedCostCreditModes.Payable;
        public long? LinkedDocumentId { get; set; }
    }
}
