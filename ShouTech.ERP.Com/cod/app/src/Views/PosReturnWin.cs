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
    protected override string? ScheduleTintName => "Peach";
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

    private PosOriginalBill? _original;

    public PosReturnWin(Guid? sessionId)
    {
        _sessionId = sessionId;
        Width = 980;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += (_, _) =>
        {
            if (!AppHost.Permissions.Has(PermCat.Pos.InvoiceRefund))
            {
                ShowWarn("POS.PermissionDenied");
                Close();
            }
        };
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_loadButton, "POS.LoadBill");
        F2Ui.StyleButton(_postButton, "POS.PostReturn");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _loadButton.Click += async (_, _) => await LoadBillAsync();
        _postButton.Click += (_, _) => PostReturn();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_loadButton, _postButton, _closeButton));

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

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("POS.ReturnTitle");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _loadButton, _postButton, _closeButton }, T);
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
        if (_original is null) { _returnTotal.Text = string.Empty; return; }
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
        _returnTotal.Text = $"{T("POS.ReturnTotal")}: {F2Ui.Fmt(total)}";
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
}
