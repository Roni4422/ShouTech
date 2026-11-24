using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// POS return window (Build POS 066): loads a posted POS bill by
/// number, shows sold / already-returned / returnable per line, takes
/// return quantities (validated against the original — POS-1006),
/// and a refund split (cash / card; the remainder is the on-account
/// part). Posting runs through PosPosting.CreatePosReturn — the
/// step-5 symmetric engine path, never a negative sale. Gated on
/// POS.INVOICE.REFUND; the return posts into the CURRENT open session.
/// </summary>
public sealed class PosReturnWin : BaseWin
{
    protected override string? ScheduleTintName => "Celadon";
    private readonly Guid? _sessionId;
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TextBox _docNo = new();
    private readonly TextBlock _billInfo = new();
    private readonly ObservableCollection<ReturnRow> _rows = new();
    private readonly DataGrid _grid = new();
    private readonly TextBox _cashRefund = new() { Text = "0" };
    private readonly TextBox _cardRefund = new() { Text = "0" };
    private readonly TextBox _reason = new();
    private readonly TextBlock _returnTotal = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _loadButton = new();
    private readonly Button _postButton = new();
    private readonly Button _closeButton = new();

    // Exchange mode (W9): the return panel doubles as an exchange —
    // new sale lines below, settled on the NET (new − returned).
    private readonly Button _exchangeButton = new();
    private readonly Button _postExchangeButton = new();
    private readonly DockPanel _exchangePanel = new();
    private readonly TextBox _newCode = new();
    private readonly TextBox _newQty = new() { Text = "1" };
    private readonly Button _addLineButton = new();
    private readonly ObservableCollection<ExchangeRow> _exchangeRows = new();
    private readonly DataGrid _exchangeGrid = new();
    private readonly TextBox _exCash = new() { Text = "0" };
    private readonly TextBox _exCashGiven = new() { Text = "0" };
    private readonly TextBox _exCard = new() { Text = "0" };
    private readonly TextBox _exOnAccount = new() { Text = "0" };
    private readonly TextBlock _netTotal = new();
    private bool _exchangeMode;
    private decimal _returnTotalValue;

    private PosOriginalBill? _original;

    public PosReturnWin(Guid? sessionId, string? initialDocNo = null)
    {
        _sessionId = sessionId;
        Width = 980;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += async (_, _) =>
        {
            if (!AppHost.Permissions.Has(PermCat.Pos.InvoiceRefund))
            {
                ShowWarn("POS.PermissionDenied");
                Close();
                return;
            }
            // Opened from the session-sales window (POS family P3)
            // with a bill chosen: prefill and load it directly.
            if (!string.IsNullOrWhiteSpace(initialDocNo))
            {
                _docNo.Text = initialDocNo;
                await LoadBillAsync();
            }
        };
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_loadButton, "POS.LoadBill");
        F2Ui.StyleButton(_postButton, "POS.PostReturn");
        F2Ui.StyleButton(_exchangeButton, "POS.ExchangeMode");
        F2Ui.StyleButton(_postExchangeButton, "POS.PostExchange");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _loadButton.Click += async (_, _) => await LoadBillAsync();
        _postButton.Click += (_, _) => PostReturn();
        _exchangeButton.Click += (_, _) => ToggleExchangeMode();
        _postExchangeButton.Click += (_, _) => PostExchange();
        _closeButton.Click += (_, _) => Close();
        _postExchangeButton.Visibility = Visibility.Collapsed;
        root.Children.Add(F2Ui.BottomBar(_loadButton, _postButton, _exchangeButton, _postExchangeButton, _closeButton));

        var content = new DockPanel();
        var form = F2Ui.CreateFormGrid(3);
        form.Margin = new Thickness(0, 0, 0, 4);
        F2Ui.AddField(_labels, form, 0, 0, "POS.OriginalBill", _docNo);
        F2Ui.AddField(_labels, form, 0, 1, "POS.RefundCash", _cashRefund);
        F2Ui.AddField(_labels, form, 0, 2, "POS.RefundCard", _cardRefund);
        F2Ui.AddField(_labels, form, 1, 0, "POS.ReturnReason", _reason, spanPairs: 2);
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);

        var info = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _billInfo.FontWeight = FontWeights.SemiBold;
        info.Children.Add(_billInfo);
        info.Children.Add(_returnTotal);
        DockPanel.SetDock(info, Dock.Top);
        content.Children.Add(info);

        F2Ui.ConfigureGrid(_grid);
        _grid.ItemsSource = _rows;
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Code", nameof(ReturnRow.Code), 110));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Product", nameof(ReturnRow.Description), 220));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Unit", nameof(ReturnRow.Unit), 70, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Sold", nameof(ReturnRow.Sold), 80, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.AlreadyReturned", nameof(ReturnRow.AlreadyReturned), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.ReturnQty", nameof(ReturnRow.ReturnQty), 90, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Price", nameof(ReturnRow.Price), 90, numeric: true));
        // Only the return quantity is editable; every other column is
        // the original bill's fact.
        _grid.IsReadOnly = false;
        foreach (var column in _grid.Columns)
            column.IsReadOnly = (column as DataGridTextColumn)?.Binding is not System.Windows.Data.Binding b
                || b.Path?.Path != nameof(ReturnRow.ReturnQty);
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTotals));
        content.Children.Add(_grid);

        BuildExchangePanel();
        DockPanel.SetDock(_exchangePanel, Dock.Bottom);
        _exchangePanel.Visibility = Visibility.Collapsed;
        content.Children.Add(_exchangePanel);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    /// <summary>The exchange panel (W9): new-sale lines (code + qty
    /// added through the same material/price resolution as the till)
    /// and the NET settlement boxes. A positive net is paid with
    /// these tenders; a negative net refunds through the return's
    /// own cash/card refund boxes above.</summary>
    private void BuildExchangePanel()
    {
        var addForm = F2Ui.CreateFormGrid(3);
        addForm.Margin = new Thickness(0, 8, 0, 4);
        F2Ui.AddField(_labels, addForm, 0, 0, "POS.ExchangeNewCode", _newCode);
        F2Ui.AddField(_labels, addForm, 0, 1, "POS.Col.Qty", _newQty);
        F2Ui.StyleButton(_addLineButton, "POS.AddItem");
        _addLineButton.Click += (_, _) => AddExchangeLine();
        var addCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        addCell.Children.Add(_addLineButton);
        Grid.SetRow(addCell, 0);
        Grid.SetColumn(addCell, 5);
        addForm.Children.Add(addCell);
        DockPanel.SetDock(addForm, Dock.Top);
        _exchangePanel.Children.Add(addForm);

        F2Ui.ConfigureGrid(_exchangeGrid);
        _exchangeGrid.Height = 110;
        _exchangeGrid.ItemsSource = _exchangeRows;
        _exchangeGrid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Code", nameof(ExchangeRow.Code), 110));
        _exchangeGrid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Product", nameof(ExchangeRow.Description), 220));
        _exchangeGrid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Unit", nameof(ExchangeRow.Unit), 70, center: true));
        _exchangeGrid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Qty", nameof(ExchangeRow.Qty), 80, numeric: true));
        _exchangeGrid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.Price", nameof(ExchangeRow.Price), 90, numeric: true));
        _exchangeGrid.Columns.Add(F2Ui.TextCol(_headers, "POS.Col.LineTotal", nameof(ExchangeRow.Total), 100, numeric: true));
        _exchangeGrid.IsReadOnly = false;
        foreach (var column in _exchangeGrid.Columns)
            column.IsReadOnly = (column as DataGridTextColumn)?.Binding is not System.Windows.Data.Binding b
                || b.Path?.Path != nameof(ExchangeRow.Qty);
        _exchangeGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTotals));
        DockPanel.SetDock(_exchangeGrid, Dock.Top);
        _exchangePanel.Children.Add(_exchangeGrid);

        var tenderForm = F2Ui.CreateFormGrid(4);
        tenderForm.Margin = new Thickness(0, 4, 0, 0);
        F2Ui.AddField(_labels, tenderForm, 0, 0, "POS.TenderCash", _exCash);
        F2Ui.AddField(_labels, tenderForm, 0, 1, "POS.TenderCashGiven", _exCashGiven);
        F2Ui.AddField(_labels, tenderForm, 0, 2, "POS.TenderCard", _exCard);
        F2Ui.AddField(_labels, tenderForm, 0, 3, "POS.TenderOnAccount", _exOnAccount);
        DockPanel.SetDock(tenderForm, Dock.Top);
        _exchangePanel.Children.Add(tenderForm);

        _netTotal.FontWeight = FontWeights.SemiBold;
        _netTotal.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(_netTotal, Dock.Top);
        _exchangePanel.Children.Add(_netTotal);
    }

    private void ToggleExchangeMode()
    {
        _exchangeMode = !_exchangeMode;
        _exchangePanel.Visibility = _exchangeMode ? Visibility.Visible : Visibility.Collapsed;
        _postButton.Visibility = _exchangeMode ? Visibility.Collapsed : Visibility.Visible;
        _postExchangeButton.Visibility = _exchangeMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateTotals();
    }

    private void AddExchangeLine()
    {
        try
        {
            var code = _newCode.Text.Trim();
            if (code.Length == 0) return;
            var qty = F2Ui.ParseDecimal(_newQty.Text);
            if (qty <= 0) { ShowWarn("POS.ExchangeNeedsQty"); return; }
            var material = TaxSvc.ResolveMaterial(code);
            if (material is null) { ShowWarn("POS.ExchangeItemNotFound"); return; }
            var existing = _exchangeRows.FirstOrDefault(x => x.MaterialId == material.Value.Id
                && string.Equals(x.Unit, material.Value.Unit, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) existing.Qty += qty;
            else
            {
                var resolution = TaxSvc.ResolveSalesPrice(_original?.PartyCode, material.Value.Id, code, qty, material.Value.Unit);
                _exchangeRows.Add(new ExchangeRow
                {
                    MaterialId = material.Value.Id, Code = code, Description = material.Value.Name,
                    Unit = material.Value.Unit, Qty = qty,
                    Price = resolution is not null && resolution.Price > 0 ? resolution.Price : material.Value.Price,
                    TaxRate = material.Value.TaxRate > 0 ? material.Value.TaxRate : TaxSvc.GetDefaultRate()
                });
            }
            _newCode.Clear();
            _exchangeGrid.Items.Refresh();
            UpdateTotals();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosReturnWin.AddExchangeLine");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private decimal ExchangeSaleTotal()
        => _exchangeRows.Sum(r => r.Total);

    private void PostExchange()
    {
        try
        {
            if (_original is null) { ShowWarn("POS.LoadBillFirst"); return; }
            var qty = SelectedQuantities();
            if (qty.Count == 0) { ShowWarn("POS.ReturnNeedsLines"); return; }
            if (_exchangeRows.Count == 0) { ShowWarn("POS.ExchangeNeedsLines"); return; }
            var net = PosTenderMath.ExchangeNet(ExchangeSaleTotal(), _returnTotalValue);
            var cashRefund = F2Ui.ParseDecimal(_cashRefund.Text);
            var cardRefund = F2Ui.ParseDecimal(_cardRefund.Text);
            if (net >= 0 && (cashRefund != 0m || cardRefund != 0m)) { ShowWarn("POS.ExchangeRefundOnlyWhenNegative"); return; }
            if (net < 0 && PosTenderMath.Round(cashRefund + cardRefund) != -net) { ShowWarn("POS.ExchangeRefundMustEqualExcess"); return; }
            if (!AskConfirm("POS.ConfirmExchange")) return;

            var warehouseId = _original.Lines.Select(l => l.WarehouseId).FirstOrDefault(w => w is not null);
            var newSale = new InvDoc
            {
                Kind = InvDocKind.PosSale,
                DocNo = InvRepo.NextDocNo(InvDocKind.PosSale),
                DocDate = DateTime.Today,
                CurrencyCode = _original.CurrencyCode,
                ExchangeRate = _original.ExchangeRate,
                WarehouseId = warehouseId,
                Notes = $"POS exchange of {_original.DocNo}. Reason: {_reason.Text.Trim()}".Trim(),
                Lines = _exchangeRows.Select((r, i) => new InvLine
                {
                    LineNo = i + 1, MaterialId = r.MaterialId, MaterialCode = r.Code, Description = r.Description,
                    Unit = r.Unit, Qty = r.Qty, UnitPrice = r.Price, TaxRate = r.TaxRate, WarehouseId = warehouseId
                }).ToList()
            };
            var netTenders = new List<PosTender>();
            var cash = F2Ui.ParseDecimal(_exCash.Text);
            var card = F2Ui.ParseDecimal(_exCard.Text);
            var account = F2Ui.ParseDecimal(_exOnAccount.Text);
            if (cash > 0)
            {
                var given = F2Ui.ParseDecimal(_exCashGiven.Text);
                netTenders.Add(new PosTender
                {
                    TenderType = PosTenderTypes.Cash, Amount = cash,
                    TenderedAmount = given > 0 ? given : cash,
                    ChangeAmount = Math.Max(0m, (given > 0 ? given : cash) - cash)
                });
            }
            if (card > 0) netTenders.Add(new PosTender { TenderType = PosTenderTypes.Card, Amount = card });
            if (account > 0) netTenders.Add(new PosTender { TenderType = PosTenderTypes.OnAccount, Amount = account });

            var result = PosPosting.CompleteExchange(
                _original, qty, newSale, netTenders, cashRefund, cardRefund,
                _sessionId, _reason.Text.Trim(), CurrentUserId,
                doc => BatchSerialCaptureSvc.CaptureForPosting(doc, this));
            if (result.SaleDoc.Status == InvDocStatus.Posted && result.ReturnDoc.Status == InvDocStatus.Posted)
            {
                ShowInfo("POS.ExchangePosted");
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosReturnWin.PostExchange");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("POS.ReturnTitle");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _loadButton, _postButton, _exchangeButton, _postExchangeButton, _addLineButton, _closeButton }, T);
    }

    private async Task LoadBillAsync()
    {
        try
        {
            var service = AppHost.Pos ?? throw new InvalidOperationException(T("POS.ServiceUnavailable"));
            _original = await service.GetOriginalBillAsync(CurrentCompanyCode, _docNo.Text.Trim());
            _rows.Clear();
            foreach (var line in _original.Lines)
                _rows.Add(new ReturnRow
                {
                    LineNo = line.LineNo, Code = line.MaterialCode, Description = line.Description,
                    Unit = line.Unit ?? string.Empty, Sold = line.QtySold,
                    AlreadyReturned = line.QtyAlreadyReturned, Price = line.UnitPrice
                });
            _billInfo.Text = $"{_original.DocNo} · {_original.DocDate} · {_original.PartyName} · {F2Ui.Fmt(_original.GrandTotal)} {_original.CurrencyCode}";
            // Default refund: the pro-rata collected part of the FULL
            // bill is the ceiling; per-return defaults are set when
            // quantities change (UpdateTotals).
            UpdateTotals();
            _statusBar.Text = $"{_rows.Count}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosReturnWin.LoadBillAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTotals()
    {
        if (_original is null) { _returnTotal.Text = string.Empty; _returnTotalValue = 0m; return; }
        var qty = SelectedQuantities();
        var subtotal = _original.Lines
            .Where(l => qty.TryGetValue(l.LineNo, out var q) && q > 0)
            .Sum(l => decimal.Round(qty[l.LineNo] * l.UnitPrice * (1m - l.DiscountPct / 100m), 4, MidpointRounding.AwayFromZero));
        var tax = _original.Lines
            .Where(l => qty.TryGetValue(l.LineNo, out var q) && q > 0)
            .Sum(l => decimal.Round(decimal.Round(qty[l.LineNo] * l.UnitPrice * (1m - l.DiscountPct / 100m), 4, MidpointRounding.AwayFromZero) * l.TaxRate / 100m, 4, MidpointRounding.AwayFromZero));
        var discount = PosTenderMath.ProRata(_original.DiscountAmount, subtotal, _original.LinesSubtotal);
        var extra = PosTenderMath.ProRata(_original.ExtraAmount, subtotal, _original.LinesSubtotal);
        var total = decimal.Round(subtotal - discount + extra + tax, 4, MidpointRounding.AwayFromZero);
        _returnTotalValue = total;
        _returnTotal.Text = $"{T("POS.ReturnTotal")}: {F2Ui.Fmt(total)}";
        if (_exchangeMode)
        {
            var net = PosTenderMath.ExchangeNet(ExchangeSaleTotal(), total);
            _netTotal.Text = $"{T("POS.ExchangeNet")}: {F2Ui.Fmt(net)} "
                + (net > 0 ? $"({T("POS.ExchangeNetPay")})" : net < 0 ? $"({T("POS.ExchangeNetRefund")})" : $"({T("POS.ExchangeNetZero")})");
        }
        // Default the refund split to the pro-rata collected part,
        // cash first (the drawer's refund), unless the cashier typed.
        var collected = PosTenderMath.ProRata(_original.PaidAmount, subtotal, _original.LinesSubtotal);
        if (decimal.TryParse(_cashRefund.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var cash) && cash == 0m
            && decimal.TryParse(_cardRefund.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var card) && card == 0m)
            _cashRefund.Text = collected.ToString("N2", CultureInfo.CurrentCulture);
    }

    private Dictionary<int, decimal> SelectedQuantities()
        => _rows.Where(r => r.ReturnQty > 0).ToDictionary(r => r.LineNo, r => r.ReturnQty);

    private void PostReturn()
    {
        try
        {
            if (_original is null) { ShowWarn("POS.LoadBillFirst"); return; }
            var qty = SelectedQuantities();
            if (qty.Count == 0) { ShowWarn("POS.ReturnNeedsLines"); return; }
            if (!AskConfirm("POS.ConfirmReturn")) return;
            var posted = PosPosting.CreatePosReturn(
                _original, qty,
                F2Ui.ParseDecimal(_cashRefund.Text), F2Ui.ParseDecimal(_cardRefund.Text),
                _sessionId, _reason.Text.Trim(), CurrentUserId,
                doc => BatchSerialCaptureSvc.CaptureForPosting(doc, this));
            if (posted.Status == InvDocStatus.Posted)
            {
                ShowInfo("POS.ReturnPosted");
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosReturnWin.PostReturn");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class ReturnRow
    {
        public int LineNo { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal Sold { get; set; }
        public decimal AlreadyReturned { get; set; }
        public decimal ReturnQty { get; set; }
        public decimal Price { get; set; }
    }

    /// <summary>One new-sale line of an exchange (W9): priced through
    /// the same resolver as the till; tax exclusive, the PosWin
    /// cart shape.</summary>
    private sealed class ExchangeRow
    {
        public long? MaterialId { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal Qty { get; set; }
        public decimal Price { get; set; }
        public decimal TaxRate { get; set; }
        public decimal Subtotal => decimal.Round(Qty * Price, 4, MidpointRounding.AwayFromZero);
        public decimal Total => Subtotal + decimal.Round(Subtotal * TaxRate / 100m, 4, MidpointRounding.AwayFromZero);
    }
}
