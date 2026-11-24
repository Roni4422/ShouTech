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
/// Creates a partial customer credit note against a posted sales invoice.
/// Return quantities are checked in the UI and revalidated transactionally
/// by InvRepo.Post against all previously posted returns.
/// </summary>
public sealed class SalesReturnWin : BaseWin
{
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly TextBox _invoiceNo = new();
    private readonly TextBox _reason = new();
    /// <summary>G18: cash-refund inputs. Refund amount 0 (the default)
    /// keeps the historical pure-credit return; a positive amount with
    /// a Cash/Card/Checks method is wired into the return document's
    /// PaidAmount/PaymentMethod and InvRepo posts the settlement leg.</summary>
    private readonly ComboBox _refundMethod = new();
    private readonly TextBox _refundAmount = new() { Text = "0" };
    private readonly TextBlock _invoiceInfo = new();
    private readonly TextBlock _returnTotal = new();
    private readonly TextBlock _statusBar = new();
    private readonly ObservableCollection<ReturnRow> _rows = new();
    private readonly DataGrid _grid = new();
    private readonly Button _loadButton = new();
    private readonly Button _postButton = new();
    private readonly Button _closeButton = new();

    private InvDoc? _original;

    public SalesReturnWin()
    {
        Width = 1000;
        Height = 620;
        MinWidth = 700;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        Loaded += OnLoaded;
    }

    /// <summary>G20: SALES.RETURN.VIEW gates the window on load
    /// (CshWin/PayRecWin pattern: deny + report, then close).</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (AppHost.Permissions.Has(PermCat.Sales.ReturnView)) return;
        var denied = new UnauthorizedAccessException($"Permission {PermCat.Sales.ReturnView} is required to open this window.");
        AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, denied, "SalesReturnWin.OnLoaded");
        MessageBox.Show(this, T("SalesReturn.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        Close();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_loadButton, "SalesReturn.LoadInvoice");
        F2Ui.StyleButton(_postButton, "SalesReturn.PostReturn");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _loadButton.Click += (_, _) => LoadInvoice();
        _postButton.Click += (_, _) => PostReturn();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_loadButton, _postButton, _closeButton));

        var content = new DockPanel();
        var form = F2Ui.CreateFormGrid(2);
        form.Margin = new Thickness(0, 0, 0, 6);
        F2Ui.AddField(_labels, form, 0, 0, "SalesReturn.OriginalInvoice", _invoiceNo);
        F2Ui.AddField(_labels, form, 0, 1, "SalesReturn.Reason", _reason);
        // G18: refund method + amount on a second form row (the action
        // buttons stay in the bottom bar, per the layout rule).
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Credit" });
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Cash" });
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Card" });
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Checks" });
        _refundMethod.SelectedIndex = 0;
        F2Ui.AddField(_labels, form, 1, 0, "SalesReturn.RefundMethod", _refundMethod);
        F2Ui.AddField(_labels, form, 1, 1, "SalesReturn.RefundAmount", _refundAmount);
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
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.Code", nameof(ReturnRow.Code), 105));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.Description", nameof(ReturnRow.Description), 2));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.Unit", nameof(ReturnRow.Unit), 65, center: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.Sold", nameof(ReturnRow.Sold), 85, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.AlreadyReturned", nameof(ReturnRow.AlreadyReturned), 110, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.Returnable", nameof(ReturnRow.Returnable), 95, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.ReturnQty", nameof(ReturnRow.ReturnQty), 95, numeric: true));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.Price", nameof(ReturnRow.Price), 90, numeric: true));
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
        Title = T("SalesReturn.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _loadButton, _postButton, _closeButton }, T);
        if (_refundMethod.Items.Count == 4)
        {
            if (_refundMethod.Items[0] is ComboBoxItem credit) credit.Content = T("SalesReturn.RefundCredit");
            if (_refundMethod.Items[1] is ComboBoxItem cash) cash.Content = T("SalesReturn.RefundCash");
            if (_refundMethod.Items[2] is ComboBoxItem card) card.Content = T("SalesReturn.RefundCard");
            if (_refundMethod.Items[3] is ComboBoxItem checks) checks.Content = T("SalesReturn.RefundChecks");
        }
    }

    /// <summary>G20 deny + report helper (CshWin/PayRecWin pattern).</summary>
    private bool EnsurePermission(string permission, string operation)
    {
        if (AppHost.Permissions.Has(permission)) return true;
        var denied = new UnauthorizedAccessException($"Permission {permission} is required for {operation}.");
        AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, denied, $"SalesReturnWin.{operation}");
        MessageBox.Show(this, T("SalesReturn.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private (decimal Amount, InvPaymentMethod Method) CaptureRefund()
    {
        var text = _refundAmount.Text.Trim();
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount)
            && !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            throw new InvalidOperationException(T("SalesReturn.RefundInvalid"));
        if (amount < 0m)
            throw new InvalidOperationException(T("SalesReturn.RefundInvalid"));
        var tag = (_refundMethod.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Credit";
        if (amount == 0m)
            return (0m, InvPaymentMethod.Credit);
        return tag switch
        {
            "Cash" => (amount, InvPaymentMethod.Cash),
            "Card" => (amount, InvPaymentMethod.Card),
            "Checks" => (amount, InvPaymentMethod.Checks),
            // A positive refund with the credit-only method chosen is a
            // contradiction — refuse instead of silently dropping it.
            _ => throw new InvalidOperationException(T("SalesReturn.RefundInvalid"))
        };
    }

    private void LoadInvoice()
    {
        try
        {
            var invoiceNo = _invoiceNo.Text.Trim();
            if (string.IsNullOrWhiteSpace(invoiceNo))
                throw new InvalidOperationException(T("SalesReturn.InvoiceRequired"));

            _original = InvRepo.LoadByNumber(invoiceNo, InvDocKind.SalesInvoice)
                ?? throw new InvalidOperationException(T("SalesReturn.InvoiceNotFound"));
            if (_original.Status != InvDocStatus.Posted)
                throw new InvalidOperationException(T("SalesReturn.PostedInvoiceRequired"));

            var returned = InvRepo.GetAlreadyReturnedQuantities(_original.DocId, InvDocKind.SalesInvoice);
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
                    Sold = line.Qty,
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
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.LoadInvoice");
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
        _returnTotal.Text = $"{T("SalesReturn.Total")}: {F2Ui.Fmt(total)} {_original.CurrencyCode}";
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
        // G20: creating the return document requires SALES.RETURN.CREATE.
        if (!EnsurePermission(PermCat.Sales.ReturnCreate, "PostReturn")) return;
        try
        {
            if (_original is null)
                throw new InvalidOperationException(T("SalesReturn.LoadInvoiceFirst"));
            if (string.IsNullOrWhiteSpace(_reason.Text))
                throw new InvalidOperationException(T("SalesReturn.ReasonRequired"));

            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var quantities = SelectedQuantities();
            if (quantities.Count == 0)
                throw new InvalidOperationException(T("SalesReturn.SelectQuantity"));

            foreach (var row in _rows)
            {
                if (row.ReturnQty < 0m || row.ReturnQty > row.Returnable)
                    throw new InvalidOperationException(T("SalesReturn.ExceedsReturnable"));
            }
            if (!AskConfirm("SalesReturn.Confirm"))
                return;

            // G18: the refund the user entered (0 = pure credit note).
            var refund = CaptureRefund();

            var selected = _original.Lines
                .Where(line => quantities.TryGetValue(line.LineNo, out var quantity) && quantity > 0m)
                .OrderBy(line => line.LineNo)
                .ToArray();
            var selectedSubtotal = selected.Sum(line => ScaledLine(line, quantities[line.LineNo]).LineSubtotal);

            var returnDoc = new InvDoc
            {
                Kind = InvDocKind.SalesReturn,
                DocNo = InvRepo.NextDocNo(InvDocKind.SalesReturn),
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
                Reference = _original.Reference,
                BranchName = _original.BranchName,
                SalesRepName = _original.SalesRepName,
                CostCenter = _original.CostCenter,
                PartyAccountName = _original.PartyAccountName,
                GoodsAccountName = _original.GoodsAccountName,
                LinesDiscountAmount = PosTenderMath.ProRata(
                    _original.LinesDiscountAmount, selectedSubtotal, LinesSubtotal(_original)),
                LinesExtraAmount = PosTenderMath.ProRata(
                    _original.LinesExtraAmount, selectedSubtotal, LinesSubtotal(_original)),
                PaidAmount = refund.Amount,
                PaymentMethod = refund.Method,
                OriginalDocId = _original.DocId,
                OriginalDocKind = InvDocKind.SalesInvoice,
                OriginalDocNo = _original.DocNo,
                Notes = $"{T("SalesReturn.Reason")}: {_reason.Text.Trim()}",
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
            InvRepo.RestoreSalesReturnLineNumbers(returnDoc.DocId, originalLineNumbers);
            for (var index = 0; index < returnDoc.Lines.Count; index++)
                returnDoc.Lines[index].LineNo = originalLineNumbers[index];

            var captures = BatchSerialCaptureSvc.CaptureForPosting(returnDoc, this);
            if (captures is null)
                return;
            // G20: posting the return requires SALES.RETURN.POST.
            if (!EnsurePermission(PermCat.Sales.ReturnPost, "PostReturn")) return;
            InvRepo.Post(returnDoc.DocId, InvDocKind.SalesReturn, CurrentUserId, captures);

            ShowInfo("SalesReturn.Posted");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.PostReturn");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class ReturnRow
    {
        public int LineNo { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public decimal Sold { get; init; }
        public decimal AlreadyReturned { get; init; }
        public decimal Returnable { get; init; }
        public decimal ReturnQty { get; set; }
        public decimal Price { get; init; }
    }
}
