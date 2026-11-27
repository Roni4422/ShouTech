using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain.Entities;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// Payment Reconciliation window (Phase 3): matches posted payment entries
/// from the party ledger against open sales/purchase documents. Two schedule
/// grids side by side (Explorer list logic), the allocation amount in the
/// content area, Allocate in the Explorer-style bottom command bar, and a
/// slim status bar with counts/totals. The allocation itself runs in ONE
/// repository transaction that re-validates both balances before writing.
///
/// W8 (migration 103): the document grid is an editable ALLOCATION PLAN —
/// Suggest fills it FIFO by due date for the selected payment's party
/// (one-shot multi-invoice distribution), the user edits the Alloc /
/// DocAmount / Discount cells, and Allocate saves every filled row
/// through the same service path. Alloc is the payment-currency amount
/// consumed from the payment; DocAmount is the document-currency amount
/// settled on a CROSS-CURRENCY document (blank = same as Alloc);
/// Discount is a per-document settlement discount (document currency),
/// gated by SALES.SETTLEMENT.DISCOUNT through the audited
/// PermServ.Require, whose denials land in sau_document_events.
/// </summary>
public sealed class PayRecWin : BaseWin
{
    protected override string? ScheduleTintName => "Lilac";

    private readonly ReconciliationService? _service;
    private readonly List<OpenDocumentRow> _documents = new();
    private readonly List<DocAllocRow> _docRows = new();
    private readonly List<UnallocatedPaymentRow> _payments = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly ComboBox _partyKind = new();
    private readonly DataGrid _docGrid = new();
    private readonly DataGrid _payGrid = new();
    private readonly TextBox _amount = new();
    private readonly TextBox _note = new();
    private readonly TextBlock _docsHeader = new();
    private readonly TextBlock _paysHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly Button _allocateButton = new();
    private readonly Button _suggestButton = new();
    private readonly Button _unallocateButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    /// <summary>One editable allocation-plan row over an open document.</summary>
    private sealed class DocAllocRow
    {
        public required OpenDocumentRow Doc { get; init; }
        public string DocNo => Doc.DocNo;
        public DateOnly DocDate => Doc.DocDate;
        public DateOnly DueDate => Doc.FifoDate;
        public string? PartyName => Doc.PartyName;
        public string CurrencyCode => Doc.CurrencyCode;
        public decimal Total => Doc.Total;
        public decimal Paid => Doc.Paid;
        public decimal Allocated => Doc.Allocated;
        public decimal OpenAmount => Doc.OpenAmount;
        /// <summary>Payment-currency amount to allocate from the payment.</summary>
        public decimal AllocAmount { get; set; }
        /// <summary>Document-currency amount settled (cross-currency only;
        /// 0 = same as AllocAmount).</summary>
        public decimal DocAmount { get; set; }
        /// <summary>Settlement discount in document currency.</summary>
        public decimal DiscountAmount { get; set; }
    }

    public PayRecWin()
    {
        _service = AppHost.Reconciliation;
        Width = 1180;
        Height = 700;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private string PartyKind => _partyKind.SelectedIndex == 1 ? "S" : "C";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.PaymentReconciliationView))
        {
            MessageBox.Show(this, T("PaymentReconciliation.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshAsync();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var statusBorder = new Border
        {
            Child = _statusBar,
            Padding = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        statusBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(statusBorder);

        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        StyleButton(_allocateButton, "PaymentReconciliation.Allocate");
        StyleButton(_suggestButton, "PaymentReconciliation.Suggest");
        StyleButton(_unallocateButton, "PaymentReconciliation.UnallocateAdvance");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _allocateButton.Click += async (_, _) => await AllocateAsync();
        _suggestButton.Click += (_, _) => SuggestAllocations();
        _unallocateButton.Click += async (_, _) => await UnallocateAdvanceAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        bar.Children.Add(_allocateButton);
        bar.Children.Add(_suggestButton);
        bar.Children.Add(_unallocateButton);
        bar.Children.Add(_refreshButton);
        bar.Children.Add(_closeButton);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        var content = new DockPanel();

        // Selector = one shared column grid (3 pairs), side labels,
        // aligned edges (Phenix rule).
        var selectorRow = CreateFormGrid(3);
        selectorRow.Margin = new Thickness(0, 0, 0, 8);
        _partyKind.Items.Add(new ComboBoxItem { Tag = "C" });
        _partyKind.Items.Add(new ComboBoxItem { Tag = "S" });
        _partyKind.SelectedIndex = 0;
        _partyKind.SelectionChanged += async (_, _) => await RefreshAsync();
        AddFormField(_labels, selectorRow, 0, 0, "PaymentReconciliation.PartyKind", _partyKind);
        AddFormField(_labels, selectorRow, 0, 1, "PaymentReconciliation.AllocationAmount", _amount);
        // Allocation note — payment_allocations.note exists in the schema and
        // the service accepts it, but the window never collected it.
        AddFormField(_labels, selectorRow, 0, 2, "PaymentReconciliation.Note", _note);
        DockPanel.SetDock(selectorRow, Dock.Top);
        content.Children.Add(selectorRow);

        var grids = new Grid();
        grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var docPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _docsHeader.FontWeight = FontWeights.SemiBold;
        _docsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_docsHeader, Dock.Top);
        docPanel.Children.Add(_docsHeader);
        ConfigureGrid(_docGrid);
        _docGrid.IsReadOnly = false;
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.DocNo", nameof(DocAllocRow.DocNo), 90));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Date", nameof(DocAllocRow.DocDate), 90, center: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.DueDate", nameof(DocAllocRow.DueDate), 90, center: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Party", nameof(DocAllocRow.PartyName), 140));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Currency", nameof(DocAllocRow.CurrencyCode), 70, center: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Total", nameof(DocAllocRow.Total), 100, numeric: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Paid", nameof(DocAllocRow.Paid), 90, numeric: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Allocated", nameof(DocAllocRow.Allocated), 90, numeric: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Open", nameof(DocAllocRow.OpenAmount), 100, numeric: true));
        _docGrid.Columns.Add(EditCol("PaymentReconciliation.AllocAmount", nameof(DocAllocRow.AllocAmount), 100));
        _docGrid.Columns.Add(EditCol("PaymentReconciliation.DocAmount", nameof(DocAllocRow.DocAmount), 100));
        _docGrid.Columns.Add(EditCol("PaymentReconciliation.Discount", nameof(DocAllocRow.DiscountAmount), 90));
        _docGrid.SelectionChanged += (_, _) => SuggestAmount();
        docPanel.Children.Add(_docGrid);
        Grid.SetColumn(docPanel, 0);
        grids.Children.Add(docPanel);

        var payPanel = new DockPanel();
        _paysHeader.FontWeight = FontWeights.SemiBold;
        _paysHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_paysHeader, Dock.Top);
        payPanel.Children.Add(_paysHeader);
        ConfigureGrid(_payGrid);
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Date", nameof(UnallocatedPaymentRow.EntryDate), 90, center: true));
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.DocNo", nameof(UnallocatedPaymentRow.DocNo), 90));
        // G17: the source kind distinguishes credit notes (SRT) from
        // cash receipts/payments (RCV/PAY/INS) in the source list.
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Kind", nameof(UnallocatedPaymentRow.DocKind), 70, center: true));
        // Step 4: the payments grid names its party and currency — rows
        // mix parties of the kind, and an unnamed payment cannot be
        // matched to a document responsibly (cash-payments MAJOR).
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Party", nameof(UnallocatedPaymentRow.PartyName), 140));
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Currency", nameof(UnallocatedPaymentRow.CurrencyCode), 70, center: true));
        // Supplier advances (migration 110) carry the PO they belong to;
        // the column is empty for normal payments.
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.AdvancePo", nameof(UnallocatedPaymentRow.PoDocNo), 90));
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Amount", nameof(UnallocatedPaymentRow.Amount), 110, numeric: true));
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Allocated", nameof(UnallocatedPaymentRow.Allocated), 90, numeric: true));
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Available", nameof(UnallocatedPaymentRow.Available), 100, numeric: true));
        _payGrid.SelectionChanged += (_, _) => SuggestAmount();
        payPanel.Children.Add(_payGrid);
        Grid.SetColumn(payPanel, 1);
        grids.Children.Add(payPanel);

        content.Children.Add(grids);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("PaymentReconciliation.Title");
        _docsHeader.Text = T("PaymentReconciliation.OpenDocuments");
        _paysHeader.Text = T("PaymentReconciliation.Payments");
        if (_partyKind.Items[0] is ComboBoxItem customer)
            customer.Content = T("PaymentReconciliation.Customer");
        if (_partyKind.Items[1] is ComboBoxItem supplier)
            supplier.Content = T("PaymentReconciliation.Supplier");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _allocateButton, _suggestButton, _unallocateButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        UpdateStatusBar();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("PaymentReconciliation.ServiceUnavailable");
                return;
            }
            var docs = await _service.GetOpenDocumentsAsync(CurrentCompanyCode, PartyKind);
            var pays = await _service.GetPaymentsAsync(CurrentCompanyCode, PartyKind);
            _documents.Clear();
            _documents.AddRange(docs);
            _docRows.Clear();
            _docRows.AddRange(docs.Select(d => new DocAllocRow { Doc = d }));
            _payments.Clear();
            _payments.AddRange(pays);
            _docGrid.ItemsSource = null;
            _docGrid.ItemsSource = _docRows;
            _payGrid.ItemsSource = null;
            _payGrid.ItemsSource = _payments;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PayRecWin.RefreshAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SuggestAmount()
    {
        if (_docGrid.SelectedItem is DocAllocRow row && _payGrid.SelectedItem is UnallocatedPaymentRow pay)
            _amount.Text = Math.Min(row.OpenAmount, pay.Available).ToString("0.##", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// W8 one-shot distribution: fills the plan grid FIFO by due date
    /// for the selected payment's party, up to the payment's available
    /// amount. Same-currency documents only (the service's SuggestFifo
    /// owns the rule); cross-currency rows stay for manual DocAmount
    /// entry. Nothing is saved until Allocate.
    /// </summary>
    private void SuggestAllocations()
    {
        try
        {
            if (_payGrid.SelectedItem is not UnallocatedPaymentRow pay)
                throw new InvalidOperationException(T("PaymentReconciliation.SelectPayment"));
            foreach (var row in _docRows)
            {
                row.AllocAmount = 0m;
                row.DocAmount = 0m;
            }
            var plan = ReconciliationService.SuggestFifo(_documents, pay.PartyId, pay.CurrencyCode, pay.Available);
            var byId = _docRows.ToDictionary(r => r.Doc.DocId);
            foreach (var (doc, amount) in plan)
                if (byId.TryGetValue(doc.DocId, out var row))
                    row.AllocAmount = amount;
            _docGrid.Items.Refresh();
            _statusBar.Text = $"{T("PaymentReconciliation.SuggestedOk")}: {plan.Count}";
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PayRecWin.SuggestAllocations");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task AllocateAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("PaymentReconciliation.ServiceUnavailable"));
            if (_payGrid.SelectedItem is not UnallocatedPaymentRow pay)
                throw new InvalidOperationException(T("PaymentReconciliation.SelectBoth"));
            var selected = _docGrid.SelectedItem as DocAllocRow;

            // Audited action-seam checks (W8): a denial is written to
            // sau_document_events by PermServ.Require before we refuse.
            if (!AppHost.Permissions.Require(PermCat.Accounting.PaymentReconciliationAllocate,
                    PartyKind == "C" ? "SALES" : "PURCHASE", null, selected?.Doc.DocId ?? 0, selected?.Doc.DocNo))
                throw new InvalidOperationException(T("PaymentReconciliation.PermissionDenied"));

            // Commit any in-progress cell edit before reading the plan.
            _docGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var plan = _docRows.Where(r => r.AllocAmount > 0).ToList();
            if (plan.Count == 0)
            {
                // Legacy single-document path: selected row + amount box.
                if (selected is null)
                    throw new InvalidOperationException(T("PaymentReconciliation.SelectBoth"));
                selected.AllocAmount = ParseDecimal(_amount.Text);
                plan.Add(selected);
            }

            var hasDiscount = plan.Any(r => r.DiscountAmount > 0);
            var discountAuthorized = false;
            if (hasDiscount)
            {
                var firstDiscounted = plan.First(r => r.DiscountAmount > 0);
                discountAuthorized = AppHost.Permissions.Require(PermCat.Sales.SettlementDiscount,
                    PartyKind == "C" ? "SALES" : "PURCHASE", null, firstDiscounted.Doc.DocId, firstDiscounted.Doc.DocNo);
                if (!discountAuthorized)
                    throw new InvalidOperationException(T("PaymentReconciliation.DiscountPermissionDenied"));
            }

            _allocateButton.IsEnabled = false;
            try
            {
                foreach (var row in plan)
                {
                    var cross = !string.Equals(row.Doc.CurrencyCode.Trim(), pay.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase);
                    decimal? docAmount = null;
                    if (cross)
                    {
                        if (row.DocAmount <= 0)
                            throw new InvalidOperationException(T("PaymentReconciliation.DocAmountRequired"));
                        docAmount = row.DocAmount;
                    }
                    await _service.AllocateAsync(CurrentCompanyCode, PartyKind, pay.LedgerId, row.Doc.DocTable, row.Doc.DocId,
                        row.AllocAmount, CurrentUserId,
                        string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim(),
                        discountAmount: row.DiscountAmount,
                        docAmount: docAmount,
                        settlementDiscountAuthorized: discountAuthorized);
                }
                _note.Clear();
            }
            finally
            {
                _allocateButton.IsEnabled = true;
            }
            MessageBox.Show(this, T("PaymentReconciliation.AllocatedOk"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("AllocateAsync", ex);
        }
    }

    /// <summary>Reverses the most recent advance allocation between the
    /// selected advance and the selected document (migration 110): the
    /// clearing entry is mirrored, paid_amount is released and the
    /// advance balance is restored, with a 079 audit row.</summary>
    private async Task UnallocateAdvanceAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("PaymentReconciliation.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Accounting.PaymentReconciliationAllocate))
                throw new InvalidOperationException(T("PaymentReconciliation.PermissionDenied"));
            if (_docGrid.SelectedItem is not OpenDocumentRow doc || _payGrid.SelectedItem is not UnallocatedPaymentRow pay || !pay.IsAdvance)
                throw new InvalidOperationException(T("PaymentReconciliation.SelectAdvancePair"));
            _unallocateButton.IsEnabled = false;
            try
            {
                await _service.UnallocateLatestAdvanceAsync(CurrentCompanyCode, pay.LedgerId, doc.DocTable, doc.DocId, CurrentUserId);
            }
            finally
            {
                _unallocateButton.IsEnabled = true;
            }
            MessageBox.Show(this, T("PaymentReconciliation.UnallocatedOk"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("UnallocateAdvanceAsync", ex);
        }
    }

    private void ReportFailure(string operation, Exception ex)
    {
        if (ex is AppError appError)
        {
            // Catalogued business blocks (e.g. PUR-1012 advance/PO
            // mismatch) show their localized catalog message + code.
            AppHost.Errors.Report(appError.Code, appError, $"PayRecWin.{operation}");
            var info = AppHost.Errors.Resolve(appError.Code, appError.Args.ToArray());
            var body = info.Message;
            if (!string.IsNullOrWhiteSpace(info.Hint))
                body += $"{Environment.NewLine}{Environment.NewLine}{info.Hint}";
            body += $"{Environment.NewLine}{Environment.NewLine}(Error {info.Code})";
            MessageBox.Show(this, body, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, $"PayRecWin.{operation}");
        MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void UpdateStatusBar()
    {
        var openTotal = _documents.Sum(d => d.OpenAmount);
        var availableTotal = _payments.Sum(p => p.Available);
        _statusBar.Text = $"{_documents.Count} {T("PaymentReconciliation.OpenDocuments")} · {T("PaymentReconciliation.Open")}: {openTotal:N2} · {_payments.Count} {T("PaymentReconciliation.Payments")} · {T("PaymentReconciliation.Available")}: {availableTotal:N2}";
    }

    private static void ConfigureGrid(DataGrid grid)
    {
        grid.AutoGenerateColumns = false;
        grid.CanUserAddRows = false;
        grid.CanUserDeleteRows = false;
        grid.IsReadOnly = true;
        grid.CanUserSortColumns = true;
        grid.CanUserReorderColumns = true;
        grid.CanUserResizeColumns = true;
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        // Excel-like grids (owner spec item 1): numbered row
        // headers on the read-only allocation grids too.
        GridEngine.ApplyStandard(grid, "TableSurfaceBrush", "TableAlternateRowBrush");
        grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        grid.AlternationCount = 2;
    }

    private DataGridTextColumn TextCol(string key, string path, double width, bool numeric = false, bool center = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width),
            IsReadOnly = true
        };
        if (numeric)
            column.ElementStyle = RightAlignStyle;
        else if (center)
            column.ElementStyle = CenterAlignStyle;
        _headers.Add((column, key));
        return column;
    }

    /// <summary>An editable plan cell (W8): two-way binding, right-aligned.</summary>
    private DataGridTextColumn EditCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(width),
            IsReadOnly = false
        };
        column.ElementStyle = RightAlignStyle;
        _headers.Add((column, key));
        return column;
    }

    private static readonly Style RightAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) }
    };

    private static readonly Style CenterAlignStyle = new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center) }
    };

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }

    private static decimal ParseDecimal(string text)
    {
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid allocation amount.");
    }
}
