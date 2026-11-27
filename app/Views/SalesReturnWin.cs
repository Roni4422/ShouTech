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
/// Creates a customer credit note (W6 completion): against a posted
/// sales invoice (Part I path), against a posted uninvoiced delivery
/// (1215 relief), or as an authorized no-invoice walk-in return
/// (SALES.RETURN.APPROVE). Every return carries a master return reason
/// (srt_return_reasons) and a per-line condition (Good / Quarantine /
/// Damaged) that routes its stock at posting. Returns that need an
/// inspection decision or — when the company flag is on — an approval
/// are first saved as drafts; the Inspect / Approve / Reject buttons
/// record those decisions (SrtRepo) and Post then completes.
/// Return quantities are checked in the UI and revalidated
/// transactionally by InvRepo.Post against all previously posted
/// returns (SAL-7021).
/// </summary>
public sealed class SalesReturnWin : BaseWin
{
    protected override string? ScheduleTintName => "Sable";
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly ComboBox _sourceMode = new();
    private readonly TextBlock _sourceLabel = new();
    private readonly TextBox _invoiceNo = new();
    private readonly ComboBox _reasonPicker = new();
    /// <summary>G18: cash-refund inputs. Refund amount 0 (the default)
    /// keeps the historical pure-credit return; a positive amount with
    /// a Cash/Card/Checks method is wired into the return document's
    /// PaidAmount/PaymentMethod and InvRepo posts the settlement leg.
    /// Disabled for delivery / no-invoice returns (nothing collected).</summary>
    private readonly ComboBox _refundMethod = new();
    private readonly TextBox _refundAmount = new() { Text = "0" };
    private readonly Button _pickPartyButton = new();
    private readonly TextBlock _partyName = new();
    private readonly TextBlock _invoiceInfo = new();
    private readonly TextBlock _returnTotal = new();
    private readonly TextBlock _rejectionLabel = new();
    private readonly TextBox _rejectionReason = new();
    private readonly TextBlock _statusBar = new();
    private readonly ObservableCollection<ReturnRow> _rows = new();
    private readonly DataGrid _grid = new();
    private readonly Button _loadButton = new();
    private readonly Button _addLineButton = new();
    private readonly Button _inspectButton = new();
    private readonly Button _approveButton = new();
    private readonly Button _rejectButton = new();
    private readonly Button _postButton = new();
    private readonly Button _closeButton = new();

    private InvDoc? _original;
    private SrtRepo.SrtDelivery? _delivery;
    private Party? _walkInParty;
    /// <summary>The built return document once it has been saved as a
    /// draft for inspection / approval; Post reuses it so the workflow
    /// stamps recorded by SrtRepo survive the re-save.</summary>
    private InvDoc? _pendingDoc;

    private string Mode => (_sourceMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "INV";
    private SrtRepo.SrtReason? SelectedReason => _reasonPicker.SelectedItem as SrtRepo.SrtReason;

    public SalesReturnWin()
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
        F2Ui.StyleButton(_addLineButton, "SalesReturn.NoInvoiceMode");
        F2Ui.StyleButton(_inspectButton, "SalesReturn.Inspect");
        F2Ui.StyleButton(_approveButton, "SalesReturn.Approve");
        F2Ui.StyleButton(_rejectButton, "SalesReturn.Reject");
        F2Ui.StyleButton(_postButton, "SalesReturn.PostReturn");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _loadButton.Click += (_, _) => LoadSource();
        _addLineButton.Click += (_, _) => AddWalkInLine();
        _inspectButton.Click += (_, _) => RecordInspection();
        _approveButton.Click += (_, _) => DecideApproval(approve: true);
        _rejectButton.Click += (_, _) => DecideApproval(approve: false);
        _postButton.Click += (_, _) => PostReturn();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_loadButton, _addLineButton, _inspectButton, _approveButton, _rejectButton, _postButton, _closeButton));

        var content = new DockPanel();
        var form = F2Ui.CreateFormGrid(2);
        form.Margin = new Thickness(0, 0, 0, 6);
        // Row 0: source mode + source document number (label follows
        // the mode; in no-invoice mode the number box is disabled and
        // the customer picker on row 2 is used instead).
        _sourceMode.Items.Add(new ComboBoxItem { Tag = "INV" });
        _sourceMode.Items.Add(new ComboBoxItem { Tag = "DLV" });
        _sourceMode.Items.Add(new ComboBoxItem { Tag = "NONE" });
        _sourceMode.SelectedIndex = 0;
        _sourceMode.SelectionChanged += (_, _) => OnModeChanged();
        F2Ui.AddField(_labels, form, 0, 0, "SalesReturn.OriginalInvoice", _sourceMode);
        // The source-number field keeps its own label so its caption
        // can follow the mode (invoice no. vs delivery no.).
        _sourceLabel.VerticalAlignment = VerticalAlignment.Center;
        _sourceLabel.TextWrapping = TextWrapping.Wrap;
        _sourceLabel.Margin = new Thickness(0, 0, 8, 8);
        _sourceLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(_sourceLabel, 0);
        Grid.SetColumn(_sourceLabel, 2);
        form.Children.Add(_sourceLabel);
        Grid.SetRow(_invoiceNo, 0);
        Grid.SetColumn(_invoiceNo, 3);
        _invoiceNo.Margin = new Thickness(0, 0, 12, 8);
        form.Children.Add(_invoiceNo);
        // Row 1: reason picker (master) + refund method (G18).
        F2Ui.AddField(_labels, form, 1, 0, "SalesReturn.Reason", _reasonPicker);
        _reasonPicker.DisplayMemberPath = nameof(SrtRepo.SrtReason.NameEn);
        _reasonPicker.SelectionChanged += (_, _) => ApplyReasonDefaultCondition();
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Credit" });
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Cash" });
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Card" });
        _refundMethod.Items.Add(new ComboBoxItem { Tag = "Checks" });
        _refundMethod.SelectedIndex = 0;
        F2Ui.AddField(_labels, form, 1, 1, "SalesReturn.RefundMethod", _refundMethod);
        // Row 2: refund amount + walk-in customer picker.
        F2Ui.AddField(_labels, form, 2, 0, "SalesReturn.RefundAmount", _refundAmount);
        F2Ui.StyleButton(_pickPartyButton, "SalesReturn.PartyCode");
        _pickPartyButton.Click += (_, _) => PickWalkInParty();
        var partyPanel = new StackPanel { Orientation = Orientation.Horizontal };
        partyPanel.Children.Add(_pickPartyButton);
        _partyName.VerticalAlignment = VerticalAlignment.Center;
        _partyName.Margin = new Thickness(8, 0, 0, 0);
        partyPanel.Children.Add(_partyName);
        F2Ui.AddElement(form, 2, 1, new UserControl { Content = partyPanel });
        DockPanel.SetDock(form, Dock.Top);
        content.Children.Add(form);

        var summary = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _invoiceInfo.FontWeight = FontWeights.SemiBold;
        summary.Children.Add(_invoiceInfo);
        summary.Children.Add(_returnTotal);
        // Rejection reason entry (used by the Reject button; a reason
        // is mandatory for a rejection — SrtRepo revalidates).
        var rejectionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _rejectionLabel.VerticalAlignment = VerticalAlignment.Center;
        _rejectionLabel.Margin = new Thickness(0, 0, 8, 0);
        _rejectionLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        rejectionRow.Children.Add(_rejectionLabel);
        _rejectionReason.Width = 320;
        rejectionRow.Children.Add(_rejectionReason);
        summary.Children.Add(rejectionRow);
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
        _grid.Columns.Add(F2Ui.TextCol(_headers, "SalesReturn.Condition", nameof(ReturnRow.Condition), 95, center: true));
        _grid.IsReadOnly = false;
        RefreshGridEditability();
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateTotals));
        content.Children.Add(_grid);

        root.Children.Add(content);
        Content = root;
        LoadReasons();
        ApplyLocalizedText();
        OnModeChanged();
    }

    /// <summary>Editable cells: ReturnQty + Condition always; Code +
    /// Price only for walk-in lines (sourced lines must retain the
    /// source document's material and price — InvRepo revalidates).</summary>
    private void RefreshGridEditability()
    {
        var walkIn = Mode == "NONE";
        foreach (var column in _grid.Columns)
        {
            if ((column as DataGridTextColumn)?.Binding is not Binding binding) continue;
            column.IsReadOnly = binding.Path?.Path switch
            {
                nameof(ReturnRow.ReturnQty) or nameof(ReturnRow.Condition) => false,
                nameof(ReturnRow.Code) or nameof(ReturnRow.Price) => !walkIn,
                _ => true
            };
        }
    }

    private void LoadReasons()
    {
        try
        {
            _reasonPicker.ItemsSource = SrtRepo.ListReasons();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.LoadReasons");
            _reasonPicker.ItemsSource = Array.Empty<SrtRepo.SrtReason>();
        }
    }

    private void ApplyReasonDefaultCondition()
    {
        if (CurrentReason is not { } reason) return;
        foreach (var row in _rows)
            row.Condition = reason.DefaultCondition;
        _grid.Items.Refresh();
    }

    private void OnModeChanged()
    {
        _original = null;
        _delivery = null;
        _pendingDoc = null;
        _rows.Clear();
        _invoiceInfo.Text = string.Empty;
        _returnTotal.Text = string.Empty;
        var walkIn = Mode == "NONE";
        _invoiceNo.IsEnabled = !walkIn;
        _loadButton.IsEnabled = !walkIn;
        _addLineButton.IsEnabled = walkIn;
        _pickPartyButton.IsEnabled = walkIn;
        _refundMethod.IsEnabled = Mode == "INV";
        _refundAmount.IsEnabled = Mode == "INV";
        _sourceLabel.Text = Mode == "DLV" ? T("SalesReturn.SourceDelivery") : T("SalesReturn.OriginalInvoice");
        RefreshGridEditability();
        UpdateWorkflowButtons();
    }

    private void UpdateWorkflowButtons()
    {
        _inspectButton.IsEnabled = _pendingDoc is not null && _pendingDoc.InspectionStatus == "PendingInspection";
        var awaiting = _pendingDoc is not null && _pendingDoc.ApprovalStatus == "PendingApproval";
        _approveButton.IsEnabled = awaiting;
        _rejectButton.IsEnabled = awaiting;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("SalesReturn.Title");
        F2Ui.ApplyTexts(_labels, _headers,
            new[] { _loadButton, _addLineButton, _inspectButton, _approveButton, _rejectButton, _postButton, _closeButton, _pickPartyButton }, T);
        if (_sourceMode.Items.Count == 3)
        {
            if (_sourceMode.Items[0] is ComboBoxItem inv) inv.Content = T("SalesReturn.OriginalInvoice");
            if (_sourceMode.Items[1] is ComboBoxItem dlv) dlv.Content = T("SalesReturn.SourceDelivery");
            if (_sourceMode.Items[2] is ComboBoxItem none) none.Content = T("SalesReturn.NoInvoiceMode");
        }
        if (_refundMethod.Items.Count == 4)
        {
            if (_refundMethod.Items[0] is ComboBoxItem credit) credit.Content = T("SalesReturn.RefundCredit");
            if (_refundMethod.Items[1] is ComboBoxItem cash) cash.Content = T("SalesReturn.RefundCash");
            if (_refundMethod.Items[2] is ComboBoxItem card) card.Content = T("SalesReturn.RefundCard");
            if (_refundMethod.Items[3] is ComboBoxItem checks) checks.Content = T("SalesReturn.RefundChecks");
        }
        _sourceLabel.Text = Mode == "DLV" ? T("SalesReturn.SourceDelivery") : T("SalesReturn.OriginalInvoice");
        _rejectionLabel.Text = T("SalesReturn.RejectionReason");
        // Reason names follow the UI language (master-data pattern).
        _reasonPicker.DisplayMemberPath = string.Empty;
        _reasonPicker.ItemTemplate = null;
        try
        {
            var lang = App.CurrentLanguageCode;
            _reasonPicker.ItemsSource = SrtRepo.ListReasons()
                .Select(r => new ReasonItem(r, r.DisplayName(lang))).ToList();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.ApplyLocalizedText");
        }
    }

    private sealed record ReasonItem(SrtRepo.SrtReason Reason, string Display)
    {
        public override string ToString() => Display;
    }

    private SrtRepo.SrtReason? CurrentReason
        => (_reasonPicker.SelectedItem as ReasonItem)?.Reason ?? SelectedReason;

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

    private void LoadSource()
    {
        if (Mode == "DLV") { LoadDelivery(); return; }
        LoadInvoice();
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
            _pendingDoc = null;

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
                    Price = line.UnitPrice,
                    Condition = CurrentReason?.DefaultCondition ?? "Good"
                });
            }

            _invoiceInfo.Text = $"{_original.DocNo} · {_original.DocDate:d} · {_original.PartyName} · {F2Ui.Fmt(_original.GrandTotal)} {_original.CurrencyCode}";
            UpdateTotals();
            UpdateWorkflowButtons();
            _statusBar.Text = $"{_rows.Count}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.LoadInvoice");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadDelivery()
    {
        try
        {
            var deliveryNo = _invoiceNo.Text.Trim();
            if (string.IsNullOrWhiteSpace(deliveryNo))
                throw new InvalidOperationException(T("SalesReturn.InvoiceRequired"));

            _delivery = SrtRepo.LoadDelivery(deliveryNo)
                ?? throw new InvalidOperationException(T("SalesReturn.DeliveryNotFound"));
            _pendingDoc = null;

            var returned = SrtRepo.GetAlreadyReturnedForDelivery(_delivery.DocId);
            _rows.Clear();
            foreach (var line in _delivery.Lines)
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
                    Price = line.UnitPrice,
                    Condition = CurrentReason?.DefaultCondition ?? "Good"
                });
            }

            _invoiceInfo.Text = $"{_delivery.DocNo} · {_delivery.DocDate:d} · {_delivery.PartyName}";
            UpdateTotals();
            UpdateWorkflowButtons();
            _statusBar.Text = $"{_rows.Count}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.LoadDelivery");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PickWalkInParty()
    {
        try
        {
            var picker = new PartySearchWin { KindFilter = PartyKinds.Customer, Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedParty is not { } party || party.LedgerId <= 0)
                return;
            _walkInParty = party;
            _partyName.Text = $"{party.Code} · {party.NameEn}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.PickWalkInParty");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddWalkInLine()
    {
        _rows.Add(new ReturnRow
        {
            LineNo = _rows.Count + 1,
            Condition = CurrentReason?.DefaultCondition ?? "Good"
        });
        UpdateWorkflowButtons();
    }

    private void UpdateTotals()
    {
        var quantities = SelectedQuantities();
        if (Mode == "INV" && _original is not null)
        {
            var selectedLines = _original.Lines.Where(line =>
                quantities.TryGetValue(line.LineNo, out var quantity) && quantity > 0m).ToArray();
            var subtotal = selectedLines.Sum(line => ScaledLine(line, quantities[line.LineNo]).LineSubtotal);
            var tax = selectedLines.Sum(line => ScaledLine(line, quantities[line.LineNo]).LineTax);
            var discount = PosTenderMath.ProRata(_original.LinesDiscountAmount, subtotal, LinesSubtotal(_original));
            var extra = PosTenderMath.ProRata(_original.LinesExtraAmount, subtotal, LinesSubtotal(_original));
            var total = decimal.Round(subtotal - discount + extra + tax, 4, MidpointRounding.AwayFromZero);
            _returnTotal.Text = $"{T("SalesReturn.Total")}: {F2Ui.Fmt(total)} {_original.CurrencyCode}";
            return;
        }
        // Delivery + walk-in returns carry no header discount / tax.
        var plain = _rows.Where(row => row.ReturnQty > 0m).Sum(row => row.ReturnQty * row.Price);
        _returnTotal.Text = $"{T("SalesReturn.Total")}: {F2Ui.Fmt(plain)}";
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

    private static string NormalizeCondition(string? value)
        => value?.Trim() switch
        {
            "Quarantine" => "Quarantine",
            "Damaged" => "Damaged",
            _ => "Good"
        };

    /// <summary>Builds (but does not save) the return document for the
    /// current mode + grid state.</summary>
    private InvDoc BuildReturnDoc()
    {
        var reason = CurrentReason
            ?? throw new InvalidOperationException(T("SalesReturn.ReasonSelect"));
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var quantities = SelectedQuantities();
        if (quantities.Count == 0)
            throw new InvalidOperationException(T("SalesReturn.SelectQuantity"));
        foreach (var row in _rows)
        {
            if (Mode != "NONE" && (row.ReturnQty < 0m || row.ReturnQty > row.Returnable))
                throw new InvalidOperationException(T("SalesReturn.ExceedsReturnable"));
            if (Mode == "NONE" && row.ReturnQty < 0m)
                throw new InvalidOperationException(T("SalesReturn.ExceedsReturnable"));
        }

        var doc = new InvDoc
        {
            Kind = InvDocKind.SalesReturn,
            DocNo = _pendingDoc?.DocNo ?? string.Empty,
            DocId = _pendingDoc?.DocId ?? 0,
            DocDate = DateTime.Today,
            ReturnReasonCode = reason.Code,
            Notes = $"{T("SalesReturn.Reason")}: {reason.DisplayName(App.CurrentLanguageCode)}",
            Lines = _rows.Where(row => row.ReturnQty > 0m).Select(row => new InvLine
            {
                LineNo = row.LineNo,
                MaterialCode = row.Code,
                Description = row.Description,
                Unit = row.Unit,
                Qty = row.ReturnQty,
                UnitPrice = row.Price,
                ReturnCondition = NormalizeCondition(row.Condition)
            }).ToList()
        };
        if (_pendingDoc is not null)
        {
            doc.InspectionStatus = _pendingDoc.InspectionStatus;
            doc.ApprovalStatus = _pendingDoc.ApprovalStatus;
        }

        switch (Mode)
        {
            case "DLV":
            {
                if (_delivery is null)
                    throw new InvalidOperationException(T("SalesReturn.LoadInvoiceFirst"));
                doc.PartyId = _delivery.PartyId;
                doc.PartyCode = _delivery.PartyCode;
                doc.PartyName = _delivery.PartyName;
                doc.WarehouseId = _delivery.WarehouseId;
                doc.WarehouseName = _delivery.WarehouseName;
                doc.CurrencyCode = _delivery.CurrencyCode;
                doc.ExchangeRate = _delivery.ExchangeRate;
                doc.PaymentMethod = InvPaymentMethod.Credit;
                doc.OriginalDocId = _delivery.DocId;
                doc.OriginalDocKind = null;
                doc.OriginalDocNo = _delivery.DocNo;
                doc.ReturnSourceKind = "DLV";
                foreach (var line in doc.Lines)
                {
                    var source = _delivery.Lines.Single(l => l.LineNo == line.LineNo);
                    line.MaterialId = source.MaterialId;
                    line.DiscountPct = source.DiscountPct;
                    line.TaxRate = 0m;
                    line.WarehouseId = source.WarehouseId;
                    line.WarehouseName = source.WarehouseName;
                }
                break;
            }
            case "NONE":
            {
                if (_walkInParty is null)
                    throw new InvalidOperationException(T("SalesReturn.CustomerRequired"));
                doc.PartyId = _walkInParty.LedgerId;
                doc.PartyCode = _walkInParty.Code;
                doc.PartyName = _walkInParty.NameEn;
                doc.PaymentMethod = InvPaymentMethod.Credit;
                doc.NoInvoice = true;
                doc.ReturnSourceKind = "NONE";
                foreach (var line in doc.Lines)
                {
                    line.TaxRate = 0m;
                    if (string.IsNullOrWhiteSpace(line.MaterialCode))
                        throw new InvalidOperationException(T("SalesReturn.SelectQuantity"));
                }
                break;
            }
            default:
            {
                if (_original is null)
                    throw new InvalidOperationException(T("SalesReturn.LoadInvoiceFirst"));
                var refund = CaptureRefund();
                var selected = _original.Lines
                    .Where(line => quantities.TryGetValue(line.LineNo, out var quantity) && quantity > 0m)
                    .OrderBy(line => line.LineNo)
                    .ToArray();
                var selectedSubtotal = selected.Sum(line => ScaledLine(line, quantities[line.LineNo]).LineSubtotal);
                doc.PartyId = _original.PartyId;
                doc.PartyCode = _original.PartyCode;
                doc.PartyName = _original.PartyName;
                doc.WarehouseId = _original.WarehouseId;
                doc.WarehouseName = _original.WarehouseName;
                doc.CurrencyCode = _original.CurrencyCode;
                doc.ExchangeRate = _original.ExchangeRate;
                doc.TaxCode = _original.TaxCode;
                doc.DefaultTaxRate = _original.DefaultTaxRate;
                doc.ReceiptNo = _original.ReceiptNo;
                doc.Reference = _original.Reference;
                doc.BranchName = _original.BranchName;
                doc.SalesRepName = _original.SalesRepName;
                doc.CostCenter = _original.CostCenter;
                doc.PartyAccountName = _original.PartyAccountName;
                doc.GoodsAccountName = _original.GoodsAccountName;
                doc.LinesDiscountAmount = PosTenderMath.ProRata(
                    _original.LinesDiscountAmount, selectedSubtotal, LinesSubtotal(_original));
                doc.LinesExtraAmount = PosTenderMath.ProRata(
                    _original.LinesExtraAmount, selectedSubtotal, LinesSubtotal(_original));
                doc.PaidAmount = refund.Amount;
                doc.PaymentMethod = refund.Method;
                doc.OriginalDocId = _original.DocId;
                doc.OriginalDocKind = InvDocKind.SalesInvoice;
                doc.OriginalDocNo = _original.DocNo;
                doc.ReturnSourceKind = "INV";
                foreach (var line in doc.Lines)
                {
                    var source = _original.Lines.Single(l => l.LineNo == line.LineNo);
                    line.MaterialId = source.MaterialId;
                    line.Description = source.Description;
                    line.Unit = source.Unit;
                    line.UnitPrice = source.UnitPrice;
                    line.DiscountPct = source.DiscountPct;
                    line.TaxCode = source.TaxCode;
                    line.TaxRate = source.TaxRate;
                    line.TaxCalculationMode = source.TaxCalculationMode;
                    line.WarehouseId = source.WarehouseId;
                    line.WarehouseName = source.WarehouseName;
                }
                break;
            }
        }
        return doc;
    }

    /// <summary>Saves the built document as a draft through the path
    /// its mode requires, restoring source line numbers afterwards.</summary>
    private InvDoc SaveReturnDraft(InvDoc doc)
    {
        switch (Mode)
        {
            case "NONE":
                SrtRepo.CreateNoInvoiceReturn(doc);
                break;
            case "DLV":
                SrtRepo.SaveDeliveryReturn(doc);
                break;
            default:
                var originalLineNumbers = doc.Lines.Select(line => line.LineNo).ToArray();
                InvRepo.SaveDraft(doc);
                InvRepo.RestoreSalesReturnLineNumbers(doc.DocId, originalLineNumbers);
                for (var index = 0; index < doc.Lines.Count; index++)
                    doc.Lines[index].LineNo = originalLineNumbers[index];
                break;
        }
        // Reload so the stored workflow stamps (SaveDraft computes
        // PendingInspection / PendingApproval) are what the buttons
        // and the final Post see.
        return InvRepo.Load(doc.DocId, InvDocKind.SalesReturn) ?? doc;
    }

    private void RecordInspection()
    {
        try
        {
            if (_pendingDoc is null) return;
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var conditions = _pendingDoc.Lines.ToDictionary(
                line => line.LineNo,
                line => NormalizeCondition(_rows.FirstOrDefault(r => r.LineNo == line.LineNo)?.Condition ?? line.ReturnCondition));
            SrtRepo.RecordInspection(_pendingDoc.DocId, conditions, CurrentUserId);
            _pendingDoc = InvRepo.Load(_pendingDoc.DocId, InvDocKind.SalesReturn) ?? _pendingDoc;
            _pendingDoc.InspectionStatus = "Inspected";
            ShowInfo("SalesReturn.Inspected");
            UpdateWorkflowButtons();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.RecordInspection");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DecideApproval(bool approve)
    {
        try
        {
            if (_pendingDoc is null) return;
            if (!EnsurePermission(PermCat.Sales.ReturnApprove, approve ? "ApproveReturn" : "RejectReturn")) return;
            if (approve)
            {
                SrtRepo.Approve(_pendingDoc.DocId, CurrentUserId);
                _pendingDoc.ApprovalStatus = "Approved";
                ShowInfo("SalesReturn.Approved");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_rejectionReason.Text))
                    throw new InvalidOperationException(T("SalesReturn.RejectionReasonRequired"));
                SrtRepo.Reject(_pendingDoc.DocId, CurrentUserId, _rejectionReason.Text);
                _pendingDoc.ApprovalStatus = "Rejected";
                ShowInfo("SalesReturn.Rejected");
            }
            UpdateWorkflowButtons();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "SalesReturnWin.DecideApproval");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PostReturn()
    {
        // G20: creating the return document requires SALES.RETURN.CREATE.
        if (!EnsurePermission(PermCat.Sales.ReturnCreate, "PostReturn")) return;
        try
        {
            var returnDoc = _pendingDoc is null ? BuildReturnDoc() : RefreshPendingDoc();
            if (!AskConfirm("SalesReturn.Confirm"))
                return;

            returnDoc = SaveReturnDraft(returnDoc);
            _pendingDoc = returnDoc;
            UpdateWorkflowButtons();

            // W6 workflow stops: the draft stays saved and the window
            // stays open so the decision buttons can be used; pressing
            // Post again afterwards completes the posting.
            if (returnDoc.InspectionStatus == "PendingInspection")
            {
                MessageBox.Show(this, T("SalesReturn.Inspect"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (returnDoc.ApprovalStatus == "PendingApproval")
            {
                MessageBox.Show(this, T("SalesReturn.ApprovalPending"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (returnDoc.ApprovalStatus == "Rejected")
            {
                MessageBox.Show(this, T("SalesReturn.Rejected"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

    /// <summary>Rebuilds the pending draft's lines from the grid while
    /// keeping its document number and recorded workflow stamps.</summary>
    private InvDoc RefreshPendingDoc()
    {
        var fresh = BuildReturnDoc();
        fresh.DocId = _pendingDoc!.DocId;
        fresh.DocNo = _pendingDoc.DocNo;
        fresh.InspectionStatus = _pendingDoc.InspectionStatus;
        fresh.ApprovalStatus = _pendingDoc.ApprovalStatus;
        return fresh;
    }

    private sealed class ReturnRow
    {
        public int LineNo { get; init; }
        public string Code { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal Sold { get; init; }
        public decimal AlreadyReturned { get; init; }
        public decimal Returnable { get; init; }
        public decimal ReturnQty { get; set; }
        public decimal Price { get; set; }
        public string Condition { get; set; } = "Good";
    }
}
