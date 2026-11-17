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
/// </summary>
public sealed class PayRecWin : BaseWin
{
    protected override string? ScheduleTintName => "Lilac";

    private readonly ReconciliationService? _service;
    private readonly List<OpenDocumentRow> _documents = new();
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
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    public PayRecWin()
    {
        _service = AppHost.Reconciliation;
        Width = 1180;
        Height = 700;
        MinWidth = 980;
        MinHeight = 580;
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

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_allocateButton, "PaymentReconciliation.Allocate");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _allocateButton.Click += async (_, _) => await AllocateAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        bar.Children.Add(_allocateButton);
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
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.DocNo", nameof(OpenDocumentRow.DocNo), 90));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Date", nameof(OpenDocumentRow.DocDate), 90, center: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Party", nameof(OpenDocumentRow.PartyName), 140));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Total", nameof(OpenDocumentRow.Total), 100, numeric: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Paid", nameof(OpenDocumentRow.Paid), 90, numeric: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Allocated", nameof(OpenDocumentRow.Allocated), 90, numeric: true));
        _docGrid.Columns.Add(TextCol("PaymentReconciliation.Open", nameof(OpenDocumentRow.OpenAmount), 100, numeric: true));
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
        // Step 4: the payments grid names its party and currency — rows
        // mix parties of the kind, and an unnamed payment cannot be
        // matched to a document responsibly (cash-payments MAJOR).
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Party", nameof(UnallocatedPaymentRow.PartyName), 140));
        _payGrid.Columns.Add(TextCol("PaymentReconciliation.Currency", nameof(UnallocatedPaymentRow.CurrencyCode), 70, center: true));
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
        foreach (var b in new[] { _allocateButton, _refreshButton, _closeButton })
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
            _payments.Clear();
            _payments.AddRange(pays);
            _docGrid.ItemsSource = null;
            _docGrid.ItemsSource = _documents;
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
        if (_docGrid.SelectedItem is OpenDocumentRow doc && _payGrid.SelectedItem is UnallocatedPaymentRow pay)
            _amount.Text = Math.Min(doc.OpenAmount, pay.Available).ToString("0.##", CultureInfo.CurrentCulture);
    }

    private async Task AllocateAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("PaymentReconciliation.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Accounting.PaymentReconciliationAllocate))
                throw new InvalidOperationException(T("PaymentReconciliation.PermissionDenied"));
            if (_docGrid.SelectedItem is not OpenDocumentRow doc || _payGrid.SelectedItem is not UnallocatedPaymentRow pay)
                throw new InvalidOperationException(T("PaymentReconciliation.SelectBoth"));
            var amount = ParseDecimal(_amount.Text);
            _allocateButton.IsEnabled = false;
            try
            {
                await _service.AllocateAsync(CurrentCompanyCode, PartyKind, pay.LedgerId, doc.DocTable, doc.DocId, amount, CurrentUserId,
                    string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim());
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
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PayRecWin.AllocateAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        grid.RowHeaderWidth = 0;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        grid.AlternationCount = 2;
        DataGridTableBehavior.SetEnableStandardContextMenu(grid, true);
    }

    private DataGridTextColumn TextCol(string key, string path, double width, bool numeric = false, bool center = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        if (numeric)
            column.ElementStyle = RightAlignStyle;
        else if (center)
            column.ElementStyle = CenterAlignStyle;
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
