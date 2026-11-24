using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Infrastructure;

namespace ShouTech.App.Views;

/// <summary>
/// Batch/serial capture for one document line (Phase B 054, design
/// section 2 capture points). Shown at posting time for every tracked
/// line of a document (InvoiceWin / PosCheckoutWin via
/// BatchSerialCaptureSvc):
///   * receipt, batch-tracked: editable batch rows (number, expiry,
///     manufacturing date, supplier batch number, quantity);
///   * issue, batch-tracked: the warehouse's batches in FEFO/FIFO
///     order with the auto-allocation prefilled — the user confirms or
///     overrides per batch (the override is what gets recorded);
///   * serial-tracked: one serial per line of text (scan or type);
///     issues are prefilled with the oldest in-stock serials.
/// Quantities are in the document line's unit; StkPost restates batch
/// quantities per base unit with the line's unit equivalent. The
/// window only collects; the engine validates and records.
/// </summary>
public sealed class BatchSerialPickerWin : BaseWin
{
    protected override string? ScheduleTintName => "Bracken";
    public sealed class BatchRow
    {
        public string BatchNo { get; set; } = string.Empty;
        public string ExpiryDate { get; set; } = string.Empty;
        public string MfgDate { get; set; } = string.Empty;
        public string SupplierBatchNo { get; set; } = string.Empty;
        public decimal OnHand { get; set; }
        public decimal Qty { get; set; }
    }

    private static readonly char[] SerialSeparators = { '\r', '\n' };

    private readonly decimal _batchRequiredQty;
    private readonly int _serialRequiredCount;
    private readonly bool _isReceipt;
    private readonly bool _canOverrideExpired;

    private readonly TextBlock _statusBar = new();
    private readonly TextBlock _header = new();
    private readonly DataGrid _batchGrid = new();
    private readonly ObservableCollection<BatchRow> _batchRows = new();
    private readonly TextBox _serialBox = new();
    private readonly TextBlock _serialHint = new();
    private readonly CheckBox _expiredOverride = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();

    public List<BatchAllocationRequest> ResultBatches { get; } = new();
    public List<SerialSelection> ResultSerials { get; } = new();
    public bool ResultAllowExpired { get; private set; }

    public BatchSerialPickerWin(
        string materialLabel,
        bool isReceipt,
        decimal batchRequiredQty,
        IReadOnlyList<BatchBalanceRow>? availableBatches,
        IReadOnlyList<BatchAllocationRequest>? suggestion,
        int serialRequiredCount,
        IReadOnlyList<string>? inStockSerials,
        bool canOverrideExpired)
    {
        _batchRequiredQty = batchRequiredQty;
        _serialRequiredCount = serialRequiredCount;
        _isReceipt = isReceipt;
        _canOverrideExpired = canOverrideExpired;

        Width = 760;
        Height = 520;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _header.Text = materialLabel;
        _header.FontSize = 14;
        _header.Margin = new Thickness(0, 0, 0, 8);

        if (batchRequiredQty > 0)
        {
            if (isReceipt)
            {
                _batchRows.Add(new BatchRow { Qty = batchRequiredQty });
            }
            else
            {
                var suggested = suggestion?
                    .GroupBy(s => s.BatchNo, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in availableBatches ?? Array.Empty<BatchBalanceRow>())
                {
                    if (b.QtyOnHand <= 0) continue;
                    _batchRows.Add(new BatchRow
                    {
                        BatchNo = b.BatchNo,
                        ExpiryDate = b.ExpiryDate ?? string.Empty,
                        MfgDate = b.MfgDate ?? string.Empty,
                        SupplierBatchNo = b.SupplierBatchNo ?? string.Empty,
                        OnHand = b.QtyOnHand,
                        Qty = suggested.TryGetValue(b.BatchNo, out var q) ? q : 0m
                    });
                }
            }
            _batchGrid.ItemsSource = _batchRows;
            _batchGrid.AutoGenerateColumns = false;
            _batchGrid.CanUserAddRows = isReceipt;
            _batchGrid.CanUserDeleteRows = isReceipt;
            _batchGrid.MinHeight = 150;
            _batchGrid.Columns.Add(new DataGridTextColumn
            {
                Header = T("BatchSerial.BatchNo"),
                Binding = new System.Windows.Data.Binding(nameof(BatchRow.BatchNo)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(130),
                IsReadOnly = !isReceipt
            });
            _batchGrid.Columns.Add(new DataGridTextColumn
            {
                Header = T("BatchSerial.ExpiryDate"),
                Binding = new System.Windows.Data.Binding(nameof(BatchRow.ExpiryDate)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(105),
                IsReadOnly = !isReceipt
            });
            _batchGrid.Columns.Add(new DataGridTextColumn
            {
                Header = T("BatchSerial.MfgDate"),
                Binding = new System.Windows.Data.Binding(nameof(BatchRow.MfgDate)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(105),
                IsReadOnly = !isReceipt
            });
            _batchGrid.Columns.Add(new DataGridTextColumn
            {
                Header = T("BatchSerial.SupplierBatchNo"),
                Binding = new System.Windows.Data.Binding(nameof(BatchRow.SupplierBatchNo)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(130),
                IsReadOnly = !isReceipt
            });
            if (!isReceipt)
            {
                _batchGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = T("BatchSerial.OnHand"),
                    Binding = new System.Windows.Data.Binding(nameof(BatchRow.OnHand)) { StringFormat = "N3" },
                    Width = new DataGridLength(90),
                    IsReadOnly = true
                });
            }
            _batchGrid.Columns.Add(new DataGridTextColumn
            {
                Header = T("BatchSerial.Qty"),
                Binding = new System.Windows.Data.Binding(nameof(BatchRow.Qty)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, StringFormat = "N3" },
                Width = new DataGridLength(90)
            });
        }

        if (serialRequiredCount > 0)
        {
            _serialHint.Text = Tf("BatchSerial.SerialsHint", serialRequiredCount);
            _serialBox.AcceptsReturn = true;
            _serialBox.MinHeight = 84;
            _serialBox.Margin = new Thickness(0, 4, 0, 0);
            if (!isReceipt && inStockSerials is not null)
                _serialBox.Text = string.Join(Environment.NewLine, inStockSerials.Take(serialRequiredCount));
        }

        _expiredOverride.Content = T("BatchSerial.ExpiredOverride");
        _expiredOverride.Margin = new Thickness(0, 8, 0, 0);
        _expiredOverride.Visibility = canOverrideExpired && !isReceipt
            ? Visibility.Visible : Visibility.Collapsed;

        F2Ui.StyleButton(_okButton, "Common.OK");
        F2Ui.StyleButton(_cancelButton, "Common.Cancel");
        _okButton.Click += (_, _) => OnOk();
        _cancelButton.Click += (_, _) => { DialogResult = false; Close(); };

        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));
        root.Children.Add(F2Ui.BottomBar(_okButton, _cancelButton));
        var content = new StackPanel();
        content.Children.Add(_header);
        if (batchRequiredQty > 0)
        {
            var required = new TextBlock
            {
                Text = Tf("BatchSerial.Required", batchRequiredQty.ToString("N3", CultureInfo.InvariantCulture)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            content.Children.Add(required);
            content.Children.Add(_batchGrid);
        }
        if (serialRequiredCount > 0)
        {
            content.Children.Add(_serialHint);
            content.Children.Add(_serialBox);
        }
        content.Children.Add(_expiredOverride);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override bool PersistGeometry => false;

    protected override void ApplyLocalizedText()
    {
        Title = T("BatchSerial.Title");
        if (_okButton.Tag is string okKey) _okButton.Content = T(okKey);
        if (_cancelButton.Tag is string cancelKey) _cancelButton.Content = T(cancelKey);
    }

    private void OnOk()
    {
        try
        {
            ResultBatches.Clear();
            ResultSerials.Clear();
            if (_batchRequiredQty > 0)
            {
                _batchGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                _batchGrid.CommitEdit(DataGridEditingUnit.Row, true);
                var rows = _batchRows
                    .Where(r => !string.IsNullOrWhiteSpace(r.BatchNo) && r.Qty > 0)
                    .ToList();
                if (rows.Sum(r => r.Qty) != _batchRequiredQty)
                {
                    _statusBar.Text = T("BatchSerial.InvalidAllocations");
                    return;
                }
                foreach (var r in rows)
                {
                    ResultBatches.Add(new BatchAllocationRequest(
                        r.BatchNo.Trim(), r.Qty,
                        string.IsNullOrWhiteSpace(r.ExpiryDate) ? null : r.ExpiryDate.Trim(),
                        string.IsNullOrWhiteSpace(r.MfgDate) ? null : r.MfgDate.Trim(),
                        string.IsNullOrWhiteSpace(r.SupplierBatchNo) ? null : r.SupplierBatchNo.Trim()));
                }
            }
            if (_serialRequiredCount > 0)
            {
                var serials = _serialBox.Text
                    .Split(SerialSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (serials.Count != _serialRequiredCount
                    || serials.Distinct(StringComparer.OrdinalIgnoreCase).Count() != serials.Count)
                {
                    _statusBar.Text = Tf("BatchSerial.InvalidSerials", _serialRequiredCount);
                    return;
                }
                foreach (var s in serials)
                    ResultSerials.Add(new SerialSelection(s));
            }
            ResultAllowExpired = _expiredOverride.IsChecked == true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "BatchSerialPickerWin.OnOk");
            _statusBar.Text = ex.Message;
        }
    }
}
