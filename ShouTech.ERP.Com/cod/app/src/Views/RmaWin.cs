using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// RMA requests window (sales Level 3, migration 105, workstream
/// W10): the front door of the returns machinery — a customer asks to
/// return goods, the request is decided, and an Approved request
/// converts into a DRAFT sales return in SalesReturnWin's world.
/// A separate small window (not a section inside the already-large
/// SalesReturnWin): the RMA list + decision workflow is a distinct
/// surface with its own lifecycle, while CONVERSION deliberately
/// reuses the W6 draft paths in SrtRepo/InvRepo — this window never
/// writes a return document itself.
/// RBAC: SALES.RETURN.VIEW gates the window, SALES.RETURN.CREATE the
/// creation, and SALES.RETURN.APPROVE (enforced inside SrtRepo) the
/// Approve / Reject / Convert actions — no new permission keys.
/// </summary>
public sealed class RmaWin : BaseWin
{
    protected override string? ScheduleTintName => "Linden";

    private readonly List<SrtRepo.RmaRequest> _requests = new();
    private readonly ObservableCollection<RmaLineEdit> _editLines = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly TextBlock _statusBar = new();

    private readonly ComboBox _statusFilter = new();
    private readonly DataGrid _grid = new();

    private readonly ComboBox _sourceKind = new();
    private readonly TextBox _sourceDocNo = new();
    private readonly TextBox _partyId = new();
    private readonly TextBox _partyName = new();
    private readonly ComboBox _reason = new();
    private readonly TextBox _notes = new();
    private readonly DataGrid _lineGrid = new();
    private long? _sourceDocId;
    private string _loadedSourceKind = string.Empty;

    private readonly Button _loadSourceButton = new();
    private readonly Button _createButton = new();
    private readonly Button _approveButton = new();
    private readonly Button _rejectButton = new();
    private readonly Button _convertButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    /// <summary>Editable RMA line row (the create form's grid).</summary>
    public sealed class RmaLineEdit
    {
        public int SourceLineNo { get; set; }
        public long? MaterialId { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal Qty { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public RmaWin()
    {
        Width = 1120;
        Height = 660;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.ReturnView))
        {
            MessageBox.Show(this, T("Rma.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        _reason.ItemsSource = SrtRepo.ListReasons();
        _reason.DisplayMemberPath = "Code";
        Refresh();
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
        StyleButton(_createButton, "Rma.Create");
        StyleButton(_approveButton, "Rma.Approve");
        StyleButton(_rejectButton, "Rma.Reject");
        StyleButton(_convertButton, "Rma.Convert");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _createButton.Click += (_, _) => Create();
        _approveButton.Click += (_, _) => Decide(approve: true);
        _rejectButton.Click += (_, _) => Decide(approve: false);
        _convertButton.Click += (_, _) => Convert();
        _refreshButton.Click += (_, _) => Refresh();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _createButton, _approveButton, _rejectButton, _convertButton, _refreshButton, _closeButton })
            bar.Children.Add(b);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        root.Children.Add(barBorder);

        var filterBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _statusFilter.Width = 140;
        _statusFilter.ItemsSource = new[]
        {
            string.Empty, SrtRepo.RmaStatuses.Requested, SrtRepo.RmaStatuses.Approved,
            SrtRepo.RmaStatuses.Rejected, SrtRepo.RmaStatuses.Converted
        };
        _statusFilter.SelectedItem = string.Empty;
        _statusFilter.SelectionChanged += (_, _) => Refresh();
        filterBar.Children.Add(_statusFilter);
        DockPanel.SetDock(filterBar, Dock.Top);
        root.Children.Add(filterBar);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 190 });

        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Rma.Col.No", nameof(SrtRepo.RmaRequest.RmaNo), 90));
        _grid.Columns.Add(TextCol("Rma.Col.Party", nameof(SrtRepo.RmaRequest.PartyName), 170));
        _grid.Columns.Add(TextCol("Rma.Col.SourceKind", nameof(SrtRepo.RmaRequest.SourceKind), 80));
        _grid.Columns.Add(TextCol("Rma.Col.SourceDoc", nameof(SrtRepo.RmaRequest.SourceDocNo), 110));
        _grid.Columns.Add(TextCol("Rma.Col.Reason", nameof(SrtRepo.RmaRequest.ReasonCode), 110));
        _grid.Columns.Add(TextCol("Rma.Col.Status", nameof(SrtRepo.RmaRequest.Status), 95));
        _grid.Columns.Add(TextCol("Rma.Col.Lines", "Lines.Count", 60));
        _grid.Columns.Add(TextCol("Rma.Col.ReturnDoc", nameof(SrtRepo.RmaRequest.LinkedReturnDocId), 90));
        Grid.SetRow(_grid, 0);
        content.Children.Add(_grid);

        var createPanel = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var form = CreateFormGrid(2);
        AddFormField(_labels, form, 0, 0, "Rma.SourceKind", _sourceKind);
        AddFormField(_labels, form, 0, 1, "Rma.SourceDoc", _sourceDocNo);
        AddFormField(_labels, form, 1, 0, "Rma.PartyId", _partyId);
        AddFormField(_labels, form, 1, 1, "Rma.Col.Party", _partyName);
        AddFormField(_labels, form, 2, 0, "Rma.Col.Reason", _reason);
        AddFormField(_labels, form, 2, 1, "Rma.Notes", _notes);
        _sourceKind.ItemsSource = new[] { "INV", "DLV", "NONE" };
        _sourceKind.SelectedItem = "INV";
        var formBar = new StackPanel { Orientation = Orientation.Horizontal,  Margin = new Thickness(0, 4, 0, 4) };
        FlowAlign.End(formBar);
        StyleButton(_loadSourceButton, "Rma.LoadSource");
        _loadSourceButton.Click += (_, _) => LoadSource();
        formBar.Children.Add(_loadSourceButton);
        var formDock = new DockPanel();
        DockPanel.SetDock(form, Dock.Top);
        formDock.Children.Add(form);
        DockPanel.SetDock(formBar, Dock.Top);
        formDock.Children.Add(formBar);
        DockPanel.SetDock(formDock, Dock.Top);
        createPanel.Children.Add(formDock);

        ConfigureLineGrid(_lineGrid);
        _lineGrid.Columns.Add(TextCol("Rma.Col.SourceLine", nameof(RmaLineEdit.SourceLineNo), 80));
        _lineGrid.Columns.Add(TextCol("Rma.Col.Material", nameof(RmaLineEdit.MaterialCode), 120));
        _lineGrid.Columns.Add(TextCol("Rma.Col.Description", nameof(RmaLineEdit.Description), 220));
        _lineGrid.Columns.Add(TextCol("Rma.Col.Unit", nameof(RmaLineEdit.Unit), 70));
        _lineGrid.Columns.Add(TextCol("Rma.Col.Qty", nameof(RmaLineEdit.Qty), 80));
        _lineGrid.Columns.Add(TextCol("Rma.Col.Price", nameof(RmaLineEdit.UnitPrice), 90));
        _lineGrid.ItemsSource = _editLines;
        createPanel.Children.Add(_lineGrid);
        Grid.SetRow(createPanel, 1);
        content.Children.Add(createPanel);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Rma.Title");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _loadSourceButton, _createButton, _approveButton, _rejectButton, _convertButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    // ------------------------------------------------------------------
    // Data + actions
    // ------------------------------------------------------------------
    private void Refresh()
    {
        try
        {
            var rows = SrtRepo.ListRma(NullIfBlank(_statusFilter.SelectedItem as string));
            _requests.Clear();
            _requests.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _requests;
            _statusBar.Text = Tf("Rma.StatusLine", _requests.Count);
        }
        catch (Exception ex) { Report(ex, "RmaWin.Refresh"); }
    }

    private void LoadSource()
    {
        try
        {
            var kind = _sourceKind.SelectedItem as string ?? "INV";
            var docNo = NullIfBlank(_sourceDocNo.Text) ?? throw new InvalidOperationException(T("Rma.SourceRequired"));
            _editLines.Clear();
            _sourceDocId = null;
            _loadedSourceKind = string.Empty;
            if (kind == "INV")
            {
                var doc = InvRepo.LoadByNumber(docNo, InvDocKind.SalesInvoice)
                    ?? throw new InvalidOperationException(T("Rma.SourceNotFound"));
                if (doc.Status != InvDocStatus.Posted)
                    throw new InvalidOperationException(T("Rma.SourceNotPosted"));
                _sourceDocId = doc.DocId;
                _partyId.Text = doc.PartyId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                _partyName.Text = doc.PartyName;
                foreach (var line in doc.Lines)
                    _editLines.Add(new RmaLineEdit
                    {
                        SourceLineNo = line.LineNo,
                        MaterialId = line.MaterialId,
                        MaterialCode = line.MaterialCode,
                        Description = line.Description,
                        Unit = line.Unit,
                        Qty = line.Qty,
                        UnitPrice = line.UnitPrice
                    });
            }
            else if (kind == "DLV")
            {
                var delivery = SrtRepo.LoadDelivery(docNo)
                    ?? throw new InvalidOperationException(T("Rma.SourceNotFound"));
                _sourceDocId = delivery.DocId;
                _partyId.Text = delivery.PartyId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                _partyName.Text = delivery.PartyName;
                foreach (var line in delivery.Lines)
                    _editLines.Add(new RmaLineEdit
                    {
                        SourceLineNo = line.LineNo,
                        MaterialId = line.MaterialId,
                        MaterialCode = line.MaterialCode,
                        Description = line.Description,
                        Unit = line.Unit,
                        Qty = line.Qty,
                        UnitPrice = line.UnitPrice
                    });
            }
            else
            {
                throw new InvalidOperationException(T("Rma.NoSourceToLoad"));
            }
            _loadedSourceKind = kind;
            _statusBar.Text = Tf("Rma.SourceLoadedStatus", _editLines.Count);
        }
        catch (Exception ex) { Report(ex, "RmaWin.LoadSource"); }
    }

    private void Create()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Sales.ReturnCreate))
                throw new InvalidOperationException(T("Rma.PermissionDenied"));
            var kind = _sourceKind.SelectedItem as string ?? "INV";
            if (kind != "NONE" && _loadedSourceKind != kind)
                throw new InvalidOperationException(T("Rma.LoadSourceFirst"));
            if (_reason.SelectedItem is not SrtRepo.SrtReason reason)
                throw new InvalidOperationException(T("Rma.ReasonRequired"));
            _lineGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            _lineGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var lines = _editLines.Where(l => l.Qty > 0m).Select(l => new SrtRepo.RmaLine(
                l.SourceLineNo, l.MaterialId, l.MaterialCode?.Trim() ?? string.Empty,
                l.Description?.Trim() ?? string.Empty, l.Unit?.Trim() ?? string.Empty,
                l.Qty, l.UnitPrice)).ToList();
            var id = SrtRepo.CreateRma(
                ParseLongOrNull(_partyId.Text), _partyName.Text.Trim(),
                kind, kind == "NONE" ? null : _sourceDocId,
                kind == "NONE" ? string.Empty : _sourceDocNo.Text.Trim(),
                reason.Code, NullIfBlank(_notes.Text), lines, CurrentUserId);
            _statusBar.Text = Tf("Rma.CreatedStatus", $"RMA-{id}");
            _editLines.Clear();
            _loadedSourceKind = string.Empty;
            Refresh();
        }
        catch (Exception ex) { Report(ex, "RmaWin.Create"); }
    }

    private void Decide(bool approve)
    {
        try
        {
            if (_grid.SelectedItem is not SrtRepo.RmaRequest rma)
                throw new InvalidOperationException(T("Rma.SelectRequest"));
            SrtRepo.DecideRma(rma.RmaId, approve, CurrentUserId, NullIfBlank(_notes.Text));
            Refresh();
        }
        catch (Exception ex) { Report(ex, "RmaWin.Decide"); }
    }

    private void Convert()
    {
        try
        {
            if (_grid.SelectedItem is not SrtRepo.RmaRequest rma)
                throw new InvalidOperationException(T("Rma.SelectRequest"));
            var docId = SrtRepo.ConvertRma(rma.RmaId, CurrentUserId);
            _statusBar.Text = Tf("Rma.ConvertedStatus", rma.RmaNo, docId);
            Refresh();
        }
        catch (Exception ex) { Report(ex, "RmaWin.Convert"); }
    }

    // ------------------------------------------------------------------
    // Helpers (CommWin pattern)
    // ------------------------------------------------------------------
    private void Report(Exception ex, string context)
    {
        AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, context);
        MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

    private static void ConfigureLineGrid(DataGrid grid)
    {
        grid.AutoGenerateColumns = false;
        grid.CanUserAddRows = true;
        grid.CanUserDeleteRows = true;
        grid.IsReadOnly = false;
        grid.CanUserSortColumns = false;
        grid.CanUserReorderColumns = true;
        grid.CanUserResizeColumns = true;
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.RowHeaderWidth = 0;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        grid.AlternationCount = 2;
        DataGridTableBehavior.SetEnableStandardContextMenu(grid, true);
    }

    private DataGridTextColumn TextCol(string key, string path, double width)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        };
        _headers.Add((column, key));
        return column;
    }

    private static void StyleButton(Button button, string key)
    {
        button.Tag = key;
        button.MinWidth = VisuId.ButtonMinWidth;
        button.MinHeight = VisuId.ControlHeight;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Padding = new Thickness(14, 0, 14, 0);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? ParseLongOrNull(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
