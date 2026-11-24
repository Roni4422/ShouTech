using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Creates a partial supplier debit note against a posted supplier invoice.
/// Return quantities are checked in the UI and revalidated transactionally
/// by InvRepo.Post against all previously posted returns.
/// </summary>
public sealed class PurchaseReturnWin : BaseWin
{
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly TextBox _invoiceNo = new();
    private readonly TextBox _reason = new();
    private readonly TextBox _creditNoteRef = new();
    private readonly TextBlock _invoiceInfo = new();
    private readonly TextBlock _returnTotal = new();
    private readonly TextBlock _statusBar = new();
    private readonly ObservableCollection<ReturnRow> _rows = new();
    private readonly DataGrid _grid = new();
    private readonly Button _loadButton = new();
    private readonly Button _postButton = new();
    private readonly Button _closeButton = new();

    private InvDoc? _original;

    public PurchaseReturnWin()
    {
        Width = 1000;
        Height = 620;
        // Screen-fit cap (verify_screenfit.py): <= 684x384. The lines
        // grid is the flexible band and scrolls internally; the form and
        // summary bands are slim and the bottom bar stays always visible.
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_loadButton, "PurchaseReturn.LoadInvoice");
        F2Ui.StyleButton(_postButton, "PurchaseReturn.PostReturn");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _loadButton.Click += (_, _) => LoadInvoice();
        _postButton.Click += (_, _) => PostReturn();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_loadButton, _postButton, _closeButton));

        var content = new DockPanel();
        var form = F2Ui.CreateFormGrid(2);
        form.Margin = new Thickness(0, 0, 0, 6);
        F2Ui.AddField(_labels, form, 0, 0, "PurchaseReturn.OriginalInvoice", _invoiceNo);
        F2Ui.AddField(_labels, form, 0, 1, "PurchaseReturn.Reason", _reason);
        F2Ui.AddField(_labels, form, 1, 0, "PurchaseReturn.CreditNoteRef", _creditNoteRef);
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);

        var summary = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _invoiceInfo.FontWeight = FontWeights.SemiBold;
        summary.Children.Add(_invoiceInfo);
        summary.Children.Add(_returnTotal);
        DockPanel.SetDock(summary, Dock.Top);
        content.Children.Add(summary);

        F2Ui.ConfigureGrid(_grid);
        _grid.ItemsSource = _rows;
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.Code", nameof(ReturnRow.Code), 105));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.Description", nameof(ReturnRow.Description), 2));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.Unit", nameof(ReturnRow.Unit), 65, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.Invoiced", nameof(ReturnRow.Invoiced), 85, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.AlreadyReturned", nameof(ReturnRow.AlreadyReturned), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.Returnable", nameof(ReturnRow.Returnable), 95, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.ReturnQty", nameof(ReturnRow.ReturnQty), 95, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PurchaseReturn.Price", nameof(ReturnRow.Price), 90, numeric: true));
        _grid.IsReadOnly = false;
        foreach (var column in _grid.Columns)
            column.IsReadOnly = (column as DataGridTextColumn)?.Binding is not Binding binding
                || binding.Path?.Path != nameof(ReturnRow.ReturnQty);
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTotals));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("PurchaseReturn.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _loadButton, _postButton, _closeButton }, T);
    }

    private void LoadInvoice()
    {
        try
        {
            var invoiceNo = _invoiceNo.Text.Trim();
            if (string.IsNullOrWhiteSpace(invoiceNo))
                throw new InvalidOperationException(T("PurchaseReturn.InvoiceRequired"));

            _original = InvRepo.LoadByNumber(invoiceNo, InvDocKind.PurchaseInvoice)
                ?? throw new InvalidOperationException(T("PurchaseReturn.InvoiceNotFound"));
            if (_original.Status != InvDocStatus.Posted)
                throw new InvalidOperationException(T("PurchaseReturn.PostedInvoiceRequired"));

            var returned = InvRepo.GetAlreadyReturnedPurchaseQuantities(_original.DocId);
            _rows.Clear();
            foreach (var line in _original.Lines)
            {
                var already = returned.GetValueOrDefault(line.LineNo);
                _rows.Add(new ReturnRow
                {
                    LineNo = line.LineNo,
                    Code = line.MaterialCode,
                    Description = line.Description,
                    Unit = line.Unit ?? string.Empty,
                    Invoiced = line.Qty,
                    AlreadyReturned = already,
                    Returnable = Math.Max(0m, line.Qty - already),
                    ReturnQty = Math.Max(0m, line.Qty - already),
                    Price = line.UnitPrice
                });
            }

            _invoiceInfo.Text = $"{_original.DocNo} · {_original.DocDate:d} · {_original.PartyName} · {F2Ui.Fmt(_original.GrandTotal)} {_original.CurrencyCode}";
            UpdateTotals();
            _statusBar.Text = $"{_rows.Count}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseReturnWin.LoadInvoice");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTotals()
    {
        if (_original is null)
        {
            _returnTotal.Text = string.Empty;
            return;
        }

        var quantities = SelectedQuantities();
        var selectedLines = _original.Lines.Where(line =>
            quantities.TryGetValue(line.LineNo, out var quantity) && quantity > 0m).ToArray();
        var subtotal = selectedLines.Sum(line => ScaledLine(line, quantities[line.LineNo]).LineSubtotal);
        var tax = selectedLines.Sum(line => ScaledLine(line, quantities[line.LineNo]).LineTax);
        var discount = PosTenderMath.ProRata(_original.LinesDiscountAmount, subtotal, LinesSubtotal(_original));
        var extra = PosTenderMath.ProRata(_original.LinesExtraAmount, subtotal, LinesSubtotal(_original));
        var total = decimal.Round(subtotal - discount + extra + tax, 4, MidpointRounding.AwayFromZero);
        _returnTotal.Text = $"{T("PurchaseReturn.Total")}: {F2Ui.Fmt(total)} {_original.CurrencyCode}";
    }

    private static InvLine ScaledLine(InvLine source, decimal quantity)
        => new()
        {
            Qty = quantity,
            UnitPrice = source.UnitPrice,
            DiscountPct = source.DiscountPct,
            TaxRate = source.TaxRate,
            TaxCalculationMode = source.TaxCalculationMode
        };

    private static decimal LinesSubtotal(InvDoc invoice)
        => invoice.Lines.Sum(line => line.LineSubtotal);

    private Dictionary<int, decimal> SelectedQuantities()
        => _rows.Where(row => row.ReturnQty > 0m).ToDictionary(row => row.LineNo, row => row.ReturnQty);

    private void PostReturn()
    {
        try
        {
            if (_original is null)
                throw new InvalidOperationException(T("PurchaseReturn.LoadInvoiceFirst"));
            if (string.IsNullOrWhiteSpace(_reason.Text))
                throw new InvalidOperationException(T("PurchaseReturn.ReasonRequired"));

            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var quantities = SelectedQuantities();
            if (quantities.Count == 0)
                throw new InvalidOperationException(T("PurchaseReturn.SelectQuantity"));

            foreach (var row in _rows)
            {
                if (row.ReturnQty < 0m || row.ReturnQty > row.Returnable)
                    throw new InvalidOperationException(T("PurchaseReturn.ExceedsReturnable"));
            }
            if (!AskConfirm("PurchaseReturn.Confirm"))
                return;

            var selected = _original.Lines
                .Where(line => quantities.TryGetValue(line.LineNo, out var quantity) && quantity > 0m)
                .OrderBy(line => line.LineNo)
                .ToArray();
            var selectedSubtotal = selected.Sum(line => ScaledLine(line, quantities[line.LineNo]).LineSubtotal);

            var returnDoc = new InvDoc
            {
                Kind = InvDocKind.PurchaseReturn,
                DocNo = InvRepo.NextDocNo(InvDocKind.PurchaseReturn),
                DocDate = DateTime.Today,
                PartyId = _original.PartyId,
                PartyCode = _original.PartyCode,
                PartyName = _original.PartyName,
                WarehouseId = _original.WarehouseId,
                WarehouseName = _original.WarehouseName,
                CurrencyCode = _original.CurrencyCode,
                ExchangeRate = _original.ExchangeRate,
                TaxCode = _original.TaxCode,
                DefaultTaxRate = _original.DefaultTaxRate,
                ReceiptNo = _original.ReceiptNo,
                // I5: the debit note carries its OWN supplier credit-note
                // reference (typed here), never the original invoice's.
                Reference = string.Empty,
                SupplierCreditNoteRef = _creditNoteRef.Text.Trim(),
                BranchName = _original.BranchName,
                SalesRepName = _original.SalesRepName,
                CostCenter = _original.CostCenter,
                PartyAccountName = _original.PartyAccountName,
                GoodsAccountName = _original.GoodsAccountName,
                LinesDiscountAmount = PosTenderMath.ProRata(
                    _original.LinesDiscountAmount, selectedSubtotal, LinesSubtotal(_original)),
                LinesExtraAmount = PosTenderMath.ProRata(
                    _original.LinesExtraAmount, selectedSubtotal, LinesSubtotal(_original)),
                PaidAmount = 0m,
                PaymentMethod = InvPaymentMethod.Credit,
                OriginalDocId = _original.DocId,
                OriginalDocKind = InvDocKind.PurchaseInvoice,
                OriginalDocNo = _original.DocNo,
                Notes = $"{T("PurchaseReturn.Reason")}: {_reason.Text.Trim()}",
                Lines = selected.Select(line => new InvLine
                {
                    LineNo = line.LineNo,
                    MaterialId = line.MaterialId,
                    MaterialCode = line.MaterialCode,
                    Description = line.Description,
                    Unit = line.Unit,
                    Qty = quantities[line.LineNo],
                    UnitPrice = line.UnitPrice,
                    DiscountPct = line.DiscountPct,
                    TaxCode = line.TaxCode,
                    TaxRate = line.TaxRate,
                    TaxCalculationMode = line.TaxCalculationMode,
                    WarehouseId = line.WarehouseId,
                    WarehouseName = line.WarehouseName
                }).ToList()
            };

            var originalLineNumbers = returnDoc.Lines.Select(line => line.LineNo).ToArray();
            InvRepo.SaveDraft(returnDoc);
            InvRepo.RestoreReturnLineNumbers(returnDoc.DocId, InvDocKind.PurchaseReturn, originalLineNumbers);
            for (var index = 0; index < returnDoc.Lines.Count; index++)
                returnDoc.Lines[index].LineNo = originalLineNumbers[index];

            var captures = BatchSerialCaptureSvc.CaptureForPosting(returnDoc, this);
            if (captures is null)
                return;
            InvRepo.Post(returnDoc.DocId, InvDocKind.PurchaseReturn, CurrentUserId, captures);

            ShowInfo("PurchaseReturn.Posted");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseReturnWin.PostReturn");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class ReturnRow
    {
        public int LineNo { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public decimal Invoiced { get; init; }
        public decimal AlreadyReturned { get; init; }
        public decimal Returnable { get; init; }
        public decimal ReturnQty { get; set; }
        public decimal Price { get; init; }
    }
}
