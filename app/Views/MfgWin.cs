using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Manufacturing order (RS-8 + staged workflow, migration 058). The
/// header links a BOM (Fetch explodes it into the lines), planned
/// start/end dates, a sales order, a cost center and split warehouses
/// (source / WIP / target / scrap, defaulting from the header
/// warehouse); the finished item and line items are picked from the
/// material master, not free-typed. Lines carry required vs consumed /
/// produced quantities, a per-line source warehouse, a BOUND standard
/// unit cost column (sweep MFG-4) and by-product lines with a cost
/// share. Workflow: Save (Draft) → Confirm → Issue (partial, repeated)
/// → Finish (partial receipts, by-products, scrap) → Close (variance
/// settled), or Cancel (full symmetric reversal). The legacy one-step
/// Post remains for simple Draft orders and shares the same valuation
/// engine + GlPosting as the staged path. Print follows the JrnWin
/// A4 pattern.
/// </summary>
public sealed class MfgWin : BaseWin
{
    protected override string? ScheduleTintName => "Green";

    private readonly TextBox _txtDocNo = new();
    private readonly DatePicker _dtDate = new() { SelectedDate = DateTime.Today };
    private readonly ComboBox _txtItem = new() { IsEditable = true };
    private readonly TextBox _txtItemName = new();
    private readonly TextBox _txtQty = new() { Text = "1" };
    private readonly TextBox _txtUnit = new() { Text = "PCS" };
    private readonly TextBox _txtWhs = new();
    private readonly TextBox _txtOverheads = new() { Text = "0.00" };
    private readonly TextBox _txtStdCost = new() { Text = "0.00" };
    private readonly TextBox _txtNotes = new();
    private readonly ComboBox _cmbBom = new() { IsEditable = true };
    private readonly DatePicker _dtPlannedStart = new();
    private readonly DatePicker _dtPlannedEnd = new();
    private readonly TextBox _txtSalesOrder = new();
    private readonly TextBox _txtCostCenter = new();
    private readonly TextBox _txtSourceWhs = new();
    private readonly TextBox _txtWipWhs = new();
    private readonly TextBox _txtTargetWhs = new();
    private readonly TextBox _txtScrapWhs = new();
    private readonly TextBox _txtFinishQty = new() { Text = "0" };

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnPost = new();
    private readonly Button _btnCancelOrder = new();
    private readonly Button _btnAddItem = new();
    private readonly Button _btnRemItem = new();
    private readonly Button _btnPrint = new();
    private readonly Button _btnFetchBom = new();
    private readonly Button _btnConfirm = new();
    private readonly Button _btnIssue = new();
    private readonly Button _btnFinish = new();
    private readonly Button _btnCloseOrder = new();

    private readonly List<Bom> _bomOptions = new();
    private readonly List<MfgMaterialOption> _materialOptions = new();
    private Guid? _bomId;
    private int? _bomVersion;
    private string _inputDocumentKind = "MATERIAL_ISSUE";
    private string _outputDocumentKind = "MATERIAL_RECEIPT";

    private readonly TextBlock _lblTotCost = new();
    private readonly TextBlock _lblUnitCost = new();
    private readonly TextBlock _lblStatus = new();

    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<MfgItemVm> _items = new();
    private Guid? _currentId;
    private string _status = MfgOrderStatuses.Draft;

    public MfgWin()
    {
        Width = 920;
        Height = 580;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        BuildUi();
        ApplyLocalizedText();
        ResetOrder();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.OrderView))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshShellOptionsAsync();
        if (!_currentId.HasValue)
            await RefreshNextDocNoAsync();
    }

    /// <summary>Loads the material + BOM pickers from the shell's live
    /// company context (BomService reads the current company's data).</summary>
    private async Task RefreshShellOptionsAsync()
    {
        try
        {
            if (AppHost.Boms is not { } boms || string.IsNullOrWhiteSpace(CurrentCompanyCode))
                return;
            var materials = await boms.GetMaterialOptionsAsync(CurrentCompanyCode);
            _materialOptions.Clear();
            _materialOptions.AddRange(materials);
            _txtItem.ItemsSource = _materialOptions.Select(m => m.Code).ToList();
            var bomList = await boms.GetAllAsync(CurrentCompanyCode, includeInactive: true);
            _bomOptions.Clear();
            _bomOptions.AddRange(bomList);
            _cmbBom.ItemsSource = _bomOptions.Select(b => b.PickerLabel).ToList();
        }
        catch (Exception ex)
        {
            ReportFailure("LoadPickers", ex);
        }
    }

    private async Task RefreshNextDocNoAsync()
    {
        try
        {
            if (AppHost.Manufacturing is { } service && !string.IsNullOrWhiteSpace(CurrentCompanyCode))
                _txtDocNo.Text = (await service.GetNextDocNoAsync(CurrentCompanyCode)).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // MFG-10a FIX: no silent catch — the failure is reported
            // through the central mechanism (the service still validates
            // again on save; a stale number is corrected there).
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "MfgWin.RefreshNextDocNo");
        }
    }

    /// <summary>Loads an existing order by its per-company document number (used by MfgListWin).</summary>
    public async void LoadOrder(int docNo)
    {
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            var order = await service.GetByDocNoAsync(CurrentCompanyCode, docNo)
                ?? throw new InvalidOperationException($"{T("MfgOrders.NotFound")}: {docNo}");
            _currentId = order.Id;
            _status = order.Status;
            _txtDocNo.Text = order.DocNo.ToString(CultureInfo.InvariantCulture);
            _dtDate.SelectedDate = order.DocDate.ToDateTime(TimeOnly.MinValue);
            _txtItem.Text = order.ItemCode;
            _txtItemName.Text = order.ItemName ?? string.Empty;
            _txtQty.Text = order.Qty.ToString("0.###", CultureInfo.InvariantCulture);
            _txtUnit.Text = order.UnitCode;
            _txtWhs.Text = order.WarehouseCode ?? string.Empty;
            _txtOverheads.Text = order.OverheadAmount.ToString("0.00", CultureInfo.InvariantCulture);
            _txtStdCost.Text = order.StandardUnitCost.ToString("0.00", CultureInfo.InvariantCulture);
            _txtNotes.Text = order.Notes ?? string.Empty;
            _bomId = order.BomId;
            _bomVersion = order.BomVersion;
            _cmbBom.Text = order.BomId is Guid selectedBomId
                ? _bomOptions.FirstOrDefault(b => b.Id == selectedBomId)?.PickerLabel ?? $"{order.ItemCode} v{order.BomVersion}"
                : string.Empty;
            _dtPlannedStart.SelectedDate = order.PlannedStartDate?.ToDateTime(TimeOnly.MinValue);
            _dtPlannedEnd.SelectedDate = order.PlannedEndDate?.ToDateTime(TimeOnly.MinValue);
            _txtSalesOrder.Text = order.SalesOrderNo ?? string.Empty;
            _txtCostCenter.Text = order.CostCenter ?? string.Empty;
            _txtSourceWhs.Text = order.SourceWarehouseCode ?? string.Empty;
            _txtWipWhs.Text = order.WipWarehouseCode ?? string.Empty;
            _txtTargetWhs.Text = order.TargetWarehouseCode ?? string.Empty;
            _txtScrapWhs.Text = order.ScrapWarehouseCode ?? string.Empty;
            _inputDocumentKind = order.InputDocumentKind;
            _outputDocumentKind = order.OutputDocumentKind;
            _txtFinishQty.Text = Math.Max(0m, order.Qty - order.ProducedQty).ToString("0.###", CultureInfo.InvariantCulture);
            _items.Clear();
            var lineNo = 0;
            foreach (var line in order.Lines)
                _items.Add(new MfgItemVm
                {
                    Id = line.Id,
                    LineNo = ++lineNo,
                    LineKind = line.LineKind,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName ?? string.Empty,
                    Unit = line.UnitCode ?? string.Empty,
                    Qty = line.Qty,
                    ConsumedQty = line.ConsumedQty,
                    ProducedQty = line.ProducedQty,
                    UnitCost = line.UnitCost,
                    StandardUnitCost = line.StandardUnitCost,
                    Warehouse = line.WarehouseCode ?? string.Empty,
                    DebitAccount = line.DebitAccountCode ?? string.Empty,
                    CreditAccount = line.CreditAccountCode ?? string.Empty,
                    CostCenter = line.CostCenter ?? string.Empty,
                    CostSharePct = line.CostSharePct
                });
            RefreshStageButtons();
            CalculateCost();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var toolbar = new WrapPanel();
        FlowAlign.End(toolbar);
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnPost, "Vouchers.Post");
        StyleButton(_btnCancelOrder, "MfgOrders.CancelOrder");
        StyleButton(_btnAddItem, "Invoice.AddLine");
        StyleButton(_btnRemItem, "Invoice.RemoveLine");
        StyleButton(_btnPrint, "Invoice.Print");
        StyleButton(_btnFetchBom, "MfgOrder.FetchBom");
        StyleButton(_btnConfirm, "MfgOrder.Confirm");
        StyleButton(_btnIssue, "MfgOrder.Issue");
        StyleButton(_btnFinish, "MfgOrder.Finish");
        StyleButton(_btnCloseOrder, "MfgOrder.CloseOrder");

        _btnNew.Click += (_, _) => { ResetOrder(); _ = RefreshNextDocNoAsync(); };
        _btnSave.Click += async (_, _) => await SaveOrderAsync();
        _btnPost.Click += async (_, _) => await PostOrderAsync();
        _btnCancelOrder.Click += async (_, _) => await CancelOrderAsync();
        _btnPrint.Click += (_, _) => PrintOrder();
        _btnFetchBom.Click += async (_, _) => await FetchBomAsync();
        _btnConfirm.Click += async (_, _) => await ConfirmOrderAsync();
        _btnIssue.Click += async (_, _) => await IssueOrderAsync();
        _btnFinish.Click += async (_, _) => await FinishOrderAsync();
        _btnCloseOrder.Click += async (_, _) => await CloseOrderAsync();
        _btnAddItem.Click += (_, _) =>
        {
            var item = new MfgItemVm { LineNo = _items.Count + 1, Unit = "KG", Qty = 1, UnitCost = 0 };
            _items.Add(item);
            _grid.SelectedItem = item;
            _grid.ScrollIntoView(item);
            CalculateCost();
        };
        _btnRemItem.Click += (_, _) =>
        {
            if (_grid.SelectedItem is MfgItemVm selected && _items.Count > 1)
            {
                _items.Remove(selected);
                Renumber();
                CalculateCost();
            }
        };

        foreach (var b in new[] { _btnNew, _btnSave, _btnPost, _btnCancelOrder, _btnAddItem, _btnRemItem, _btnPrint, _btnFetchBom, _btnConfirm, _btnIssue, _btnFinish, _btnCloseOrder })
            toolbar.Children.Add(b);

        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBar, Dock.Bottom);

        var footer = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var footerContent = new DockPanel();
        DockPanel.SetDock(_lblTotCost, Dock.Left);
        DockPanel.SetDock(_lblStatus, Dock.Right);
        footerContent.Children.Add(_lblTotCost);
        footerContent.Children.Add(_lblStatus);
        var footerStack = new StackPanel();
        footerStack.Children.Add(footerContent);
        footerStack.Children.Add(_lblUnitCost);
        footer.Child = footerStack;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(actionBar);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Compact shared header grid: every field row is occupied. The
        // memo uses one pair so the scrap warehouse and finish quantity
        // share its row instead of leaving two unused pairs below.
        var headerForm = CreateFormGrid(4);
        headerForm.Margin = new Thickness(0, 0, 0, 8);
        var bomPicker = new DockPanel { LastChildFill = true };
        var btnFindBom = new Button
        {
            Content = "…",
            Width = 34,
            MinWidth = 34,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = T("Common.Search")
        };
        btnFindBom.Click += (_, _) => ShowBomPicker();
        DockPanel.SetDock(btnFindBom, Dock.Right);
        bomPicker.Children.Add(btnFindBom);
        bomPicker.Children.Add(_cmbBom);
        AddFormField(headerForm, 0, 0, "MfgOrder.Bom", bomPicker);
        AddFormField(headerForm, 0, 1, "MfgOrder.OrderNo", _txtDocNo);
        AddFormField(headerForm, 0, 2, "MfgOrder.Date", _dtDate);
        AddFormField(headerForm, 0, 3, "MfgOrder.TargetMaterial", _txtItem);
        AddFormField(headerForm, 1, 0, "Invoice.Col.Description", _txtItemName);
        AddFormField(headerForm, 1, 1, "MfgOrder.Quantity", _txtQty);
        AddFormField(headerForm, 1, 2, "MfgOrder.Unit", _txtUnit);
        AddFormField(headerForm, 1, 3, "Invoice.Warehouse", _txtWhs);
        AddFormField(headerForm, 2, 0, "MfgOrder.ExtraCosts", _txtOverheads);
        AddFormField(headerForm, 2, 1, "MfgOrders.StandardUnitCost", _txtStdCost);
        AddFormField(headerForm, 2, 2, "Vouchers.Memo", _txtNotes);
        AddFormField(headerForm, 2, 3, "MfgOrder.ScrapWarehouse", _txtScrapWhs);
        AddFormField(headerForm, 3, 0, "MfgOrder.FinishQty", _txtFinishQty);
        AddFormField(headerForm, 3, 1, "MfgOrder.PlannedStart", _dtPlannedStart);
        AddFormField(headerForm, 3, 2, "MfgOrder.PlannedEnd", _dtPlannedEnd);
        AddFormField(headerForm, 3, 3, "MfgOrder.SalesOrder", _txtSalesOrder);
        AddFormField(headerForm, 4, 0, "MfgOrder.CostCenter", _txtCostCenter);
        AddFormField(headerForm, 4, 1, "MfgOrder.SourceWarehouse", _txtSourceWhs);
        AddFormField(headerForm, 4, 2, "MfgOrder.WipWarehouse", _txtWipWhs);
        AddFormField(headerForm, 4, 3, "MfgOrder.TargetWarehouse", _txtTargetWhs);
        _cmbBom.IsReadOnly = true;
        _txtItem.SelectionChanged += (_, _) => SyncFinishedItemName();

        // Manufacturing header fields use a tighter vertical rhythm than
        // the shared form default: keep the gap between rows at 2 device-independent pixels.
        foreach (var element in headerForm.Children.OfType<FrameworkElement>())
        {
            var margin = element.Margin;
            element.Margin = new Thickness(margin.Left, 0, margin.Right, 2);
        }

        // Keep input text usable in the compact header: one pixel of
        // inner padding on every field control, including the BOM picker.
        foreach (var control in new Control[]
        {
            _txtDocNo, _dtDate, _txtItem, _txtItemName, _txtQty, _txtUnit,
            _txtWhs, _txtOverheads, _txtStdCost, _txtNotes, _txtScrapWhs,
            _txtFinishQty, _cmbBom, _dtPlannedStart, _dtPlannedEnd,
            _txtSalesOrder, _txtCostCenter, _txtSourceWhs, _txtWipWhs,
            _txtTargetWhs, btnFindBom
        })
            control.Padding = new Thickness(1);

        Grid.SetRow(headerForm, 0);
        body.Children.Add(headerForm);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.AlternationCount = 2;
        _grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        _grid.SetResourceReference(DataGrid.ColumnHeaderStyleProperty, "VisuDataGridHeaderStyle");
        _grid.SetResourceReference(DataGrid.RowStyleProperty, "VisuDataGridRowStyle");
        _grid.SetResourceReference(DataGrid.CellStyleProperty, "VisuDataGridCellStyle");
        _grid.SetResourceReference(DataGrid.RowBackgroundProperty, "TableSurfaceBrush");
        _grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, "TableAlternateRowBrush");
        var lineView = new ListCollectionView(_items);
        lineView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MfgItemVm.Section)));
        _grid.ItemsSource = lineView;
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.LineNo), 45, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.LineKind), 80));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.ItemCode), 120));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.ItemName), 230));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.Unit), 70));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.QtyText), 90));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.ConsumedText), 90, readOnly: true));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.StageQtyText), 90));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.CostText), 100));
        // MFG-4 FIX: the standard unit cost column is BOUND (the VM
        // property existed for releases with no column binding it).
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.StdCostText), 100));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.Warehouse), 90));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.DebitAccount), 110));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.CreditAccount), 110));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.CostCenter), 100));
        _grid.Columns.Add(TextCol(nameof(MfgItemVm.ShareText), 70));
        _grid.Columns.Add(new DataGridTextColumn
        {
            Binding = new Binding(nameof(MfgItemVm.TotalCostText)) { Mode = BindingMode.OneWay },
            Width = 110,
            IsReadOnly = true
        });
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_grid.ItemsSource is ICollectionView view) view.Refresh();
            CalculateCost();
        });
        _txtQty.TextChanged += (_, _) => CalculateCost();
        _txtOverheads.TextChanged += (_, _) => CalculateCost();
        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        root.Children.Add(body);
        Content = root;
    }

    private void ShowBomPicker()
    {
        if (_bomOptions.Count == 0)
        {
            MessageBox.Show(this, T("MfgOrder.SelectBom"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = T("MfgOrder.Bom"),
            Owner = this,
            Width = 560,
            Height = 460,
            MinWidth = 420,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize
        };

        var layout = new DockPanel { Margin = new Thickness(12) };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), MinHeight = 30 };
        search.ToolTip = T("Common.Search");
        DockPanel.SetDock(search, Dock.Top);
        layout.Children.Add(search);

        var list = new ListBox
        {
            DisplayMemberPath = nameof(Bom.PickerLabel),
            ItemsSource = _bomOptions
                .OrderBy(x => x.MaterialName ?? x.MaterialCode, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.MaterialCode, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Version).ToList()
        };
        search.TextChanged += (_, _) =>
        {
            var term = search.Text.Trim();
            list.ItemsSource = _bomOptions.Where(x =>
                    string.IsNullOrWhiteSpace(term) ||
                    x.PickerLabel.Contains(term, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(x => x.MaterialName ?? x.MaterialCode, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.MaterialCode, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Version).ToList();
        };
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is Bom selected)
            {
                SelectBom(selected);
                dialog.DialogResult = true;
            }
        };
        layout.Children.Add(list);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var choose = new Button { Content = T("Common.OK"), MinWidth = 90, Margin = new Thickness(4, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            if (list.SelectedItem is not Bom selected)
                return;
            SelectBom(selected);
            dialog.DialogResult = true;
        };
        var close = new Button { Content = T("Common.Close"), MinWidth = 90, Margin = new Thickness(4, 0, 0, 0) };
        close.Click += (_, _) => dialog.DialogResult = false;
        actions.Children.Add(choose);
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    private void SelectBom(Bom selected)
    {
        _bomId = selected.Id;
        _bomVersion = selected.Version;
        _cmbBom.Text = selected.PickerLabel;
        _txtItem.Text = selected.MaterialCode;
        _txtItemName.Text = selected.MaterialName ?? string.Empty;
        OnBomSelectionChanged();
    }

    private void Renumber()
    {
        for (var i = 0; i < _items.Count; i++)
            _items[i].LineNo = i + 1;
    }

    private void ResetOrder()
    {
        _currentId = null;
        _status = MfgOrderStatuses.Draft;
        _bomId = null;
        _bomVersion = null;
        _txtDocNo.Text = "0";
        _dtDate.SelectedDate = DateTime.Today;
        _txtItem.Text = string.Empty;
        _txtItemName.Text = string.Empty;
        _txtQty.Text = "1";
        _txtUnit.Text = "PCS";
        _txtWhs.Text = string.Empty;
        _txtOverheads.Text = "0.00";
        _txtStdCost.Text = "0.00";
        _txtNotes.Text = string.Empty;
        _cmbBom.Text = string.Empty;
        _dtPlannedStart.SelectedDate = null;
        _dtPlannedEnd.SelectedDate = null;
        _txtSalesOrder.Text = string.Empty;
        _txtCostCenter.Text = string.Empty;
        _txtSourceWhs.Text = string.Empty;
        _txtWipWhs.Text = string.Empty;
        _txtTargetWhs.Text = string.Empty;
        _txtScrapWhs.Text = string.Empty;
        _inputDocumentKind = "MATERIAL_ISSUE";
        _outputDocumentKind = "MATERIAL_RECEIPT";
        _txtFinishQty.Text = "0";
        _items.Clear();
        _items.Add(new MfgItemVm { LineNo = 1, Unit = "KG", Qty = 1, UnitCost = 0 });
        _grid.IsReadOnly = false;
        RefreshStageButtons();
        CalculateCost();
    }

    /// <summary>Stage-button enablement follows the persisted status:
    /// Confirm from Draft; Issue/Finish while In-Process; Close once
    /// Completed; the legacy one-step Post only from Draft.</summary>
    private void RefreshStageButtons()
    {
        var editable = string.Equals(_status, MfgOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase);
        _btnSave.IsEnabled = editable;
        _btnPost.IsEnabled = editable && _currentId.HasValue && !_bomId.HasValue;
        _btnFetchBom.IsEnabled = editable;
        _btnConfirm.IsEnabled = editable && _currentId.HasValue;
        var inProcess = string.Equals(_status, MfgOrderStatuses.InProcess, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_status, MfgOrderStatuses.Confirmed, StringComparison.OrdinalIgnoreCase);
        _btnIssue.IsEnabled = inProcess;
        _btnFinish.IsEnabled = inProcess;
        _btnCloseOrder.IsEnabled = string.Equals(_status, MfgOrderStatuses.Completed, StringComparison.OrdinalIgnoreCase);
        _btnCancelOrder.IsEnabled = _currentId.HasValue
            && !string.Equals(_status, MfgOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_status, MfgOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase);
        _grid.IsReadOnly = !editable;
    }

    private static decimal ParseDecimal(TextBox box)
        => decimal.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ||
           decimal.TryParse(box.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out v) ? v : 0m;

    private void CalculateCost()
    {
        var overheads = ParseDecimal(_txtOverheads);
        var targetQty = ParseDecimal(_txtQty);
        if (targetQty <= 0) targetQty = 1;
        var rawTotal = _items.Where(i => i.LineKind is MfgLineKinds.Raw or MfgLineKinds.Cost).Sum(i => i.TotalCost);
        var grandTotal = rawTotal + overheads;
        var unitCost = grandTotal / targetQty;
        _lblTotCost.Text = $"{T("MfgOrder.TotalCost")}: {grandTotal:N2}";
        _lblTotCost.FontWeight = FontWeights.Bold;
        _lblUnitCost.Text = $"{T("MfgOrder.UnitCost")}: {unitCost:N2}";
        _lblStatus.Text = $"{T("Invoice.Status")}: {_status}";
    }

    private MfgOrder BuildOrder()
    {
        var order = new MfgOrder
        {
            Id = _currentId ?? Guid.NewGuid(),
            DocNo = int.TryParse(_txtDocNo.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var docNo) ? docNo : 0,
            DocDate = DateOnly.FromDateTime(_dtDate.SelectedDate ?? DateTime.Today),
            ItemCode = _txtItem.Text.Trim(),
            ItemName = string.IsNullOrWhiteSpace(_txtItemName.Text) ? null : _txtItemName.Text.Trim(),
            Qty = ParseDecimal(_txtQty),
            UnitCode = string.IsNullOrWhiteSpace(_txtUnit.Text) ? "PCS" : _txtUnit.Text.Trim(),
            WarehouseCode = string.IsNullOrWhiteSpace(_txtWhs.Text) ? null : _txtWhs.Text.Trim(),
            OverheadAmount = ParseDecimal(_txtOverheads),
            StandardUnitCost = ParseDecimal(_txtStdCost),
            Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim(),
            Status = _status,
            BomId = _bomId,
            BomVersion = _bomVersion,
            PlannedStartDate = _dtPlannedStart.SelectedDate is DateTime ps ? DateOnly.FromDateTime(ps) : null,
            PlannedEndDate = _dtPlannedEnd.SelectedDate is DateTime pe ? DateOnly.FromDateTime(pe) : null,
            SalesOrderNo = string.IsNullOrWhiteSpace(_txtSalesOrder.Text) ? null : _txtSalesOrder.Text.Trim(),
            CostCenter = string.IsNullOrWhiteSpace(_txtCostCenter.Text) ? null : _txtCostCenter.Text.Trim(),
            SourceWarehouseCode = string.IsNullOrWhiteSpace(_txtSourceWhs.Text) ? null : _txtSourceWhs.Text.Trim(),
            WipWarehouseCode = string.IsNullOrWhiteSpace(_txtWipWhs.Text) ? null : _txtWipWhs.Text.Trim(),
            TargetWarehouseCode = string.IsNullOrWhiteSpace(_txtTargetWhs.Text) ? null : _txtTargetWhs.Text.Trim(),
            ScrapWarehouseCode = string.IsNullOrWhiteSpace(_txtScrapWhs.Text) ? null : _txtScrapWhs.Text.Trim(),
            InputDocumentKind = _inputDocumentKind,
            OutputDocumentKind = _outputDocumentKind
        };
        foreach (var item in _items.Where(i => i.Qty > 0 || !string.IsNullOrWhiteSpace(i.ItemCode)))
            order.Lines.Add(new MfgOrderLine
            {
                OrderId = order.Id,
                LineKind = string.IsNullOrWhiteSpace(item.LineKind) ? MfgLineKinds.Raw : item.LineKind.Trim().ToUpperInvariant(),
                ItemCode = item.ItemCode.Trim(),
                ItemName = string.IsNullOrWhiteSpace(item.ItemName) ? null : item.ItemName.Trim(),
                Qty = item.Qty,
                UnitCode = string.IsNullOrWhiteSpace(item.Unit) ? null : item.Unit.Trim(),
                UnitCost = item.UnitCost,
                StandardUnitCost = item.StandardUnitCost,
                WarehouseCode = string.IsNullOrWhiteSpace(item.Warehouse) ? null : item.Warehouse.Trim(),
                DebitAccountCode = string.IsNullOrWhiteSpace(item.DebitAccount) ? null : item.DebitAccount.Trim(),
                CreditAccountCode = string.IsNullOrWhiteSpace(item.CreditAccount) ? null : item.CreditAccount.Trim(),
                CostCenter = string.IsNullOrWhiteSpace(item.CostCenter) ? null : item.CostCenter.Trim(),
                CostSharePct = item.CostSharePct
            });
        return order;
    }

    private async Task SaveOrderAsync()
    {
        if (!AppHost.Permissions.Has(_currentId.HasValue ? PermCat.Manufacturing.OrderUpdate : PermCat.Manufacturing.OrderCreate))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            if (string.IsNullOrWhiteSpace(CurrentCompanyCode))
                throw new InvalidOperationException(T("MfgOrders.NoCompany"));
            if (string.IsNullOrWhiteSpace(CurrentUserId))
                throw new InvalidOperationException(T("MfgOrders.NoUser"));
            var order = BuildOrder();
            await service.SaveDraftAsync(CurrentCompanyCode, order, CurrentUserId);
            _currentId = order.Id;
            _txtDocNo.Text = order.DocNo.ToString(CultureInfo.InvariantCulture);
            CalculateCost();
            MessageBox.Show(this, T("MfgOrder.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveDraft", ex);
        }
    }

    private async Task PostOrderAsync()
    {
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.ProductionPost))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            if (!_currentId.HasValue)
            {
                await SaveOrderAsync();
                if (!_currentId.HasValue) return;
            }
            if (!AskConfirm("MfgOrders.ConfirmPost")) return;
            await service.PostAsync(CurrentCompanyCode, _currentId.Value, CurrentUserId);
            _status = MfgOrderStatuses.Posted;
            RefreshStageButtons();
            CalculateCost();
            MessageBox.Show(this, T("MfgOrder.Posted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Post", ex);
        }
    }

    private async Task CancelOrderAsync()
    {
        if (!_currentId.HasValue) return;
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.OrderCancel))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!AskConfirm("MfgOrders.ConfirmCancel")) return;
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            await service.CancelAsync(CurrentCompanyCode, _currentId.Value, CurrentUserId);
            _status = MfgOrderStatuses.Cancelled;
            RefreshStageButtons();
            CalculateCost();
        }
        catch (Exception ex)
        {
            ReportFailure("Cancel", ex);
        }
    }

    // ------------------------------------------------------------------
    // BOM link + staged workflow (migration 058).
    // ------------------------------------------------------------------

    private void OnBomSelectionChanged()
    {
        var index = _cmbBom.SelectedIndex;
        if (index >= 0 && index < _bomOptions.Count)
        {
            _bomId = _bomOptions[index].Id;
            _bomVersion = _bomOptions[index].Version;
        }
    }

    private void SyncFinishedItemName()
    {
        var code = _txtItem.Text.Trim();
        var option = _materialOptions.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
        if (option is not null)
            _txtItemName.Text = option.Name ?? string.Empty;
    }

    /// <summary>Fetch/explode: replaces the Draft lines with the BOM's
    /// requirements for the order quantity — line costs come from the
    /// BOM's standard-cost snapshots (MFG-3: never a silent zero).</summary>
    private async Task FetchBomAsync()
    {
        try
        {
            if (AppHost.Boms is not { } boms)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            if (!_bomId.HasValue)
            {
                OnBomSelectionChanged();
                if (!_bomId.HasValue)
                    throw new InvalidOperationException(T("MfgOrder.SelectBom"));
            }
            var qty = ParseDecimal(_txtQty);
            if (qty <= 0)
                throw new InvalidOperationException(T("MfgOrder.QtyInvalid"));
            var explosion = await boms.ExplodeAsync(CurrentCompanyCode, _bomId.Value, qty);
            var bom = _bomOptions.FirstOrDefault(b => b.Id == _bomId.Value);
            _items.Clear();
            var lineNo = 0;
            foreach (var req in explosion.Requirements)
                _items.Add(new MfgItemVm
                {
                    LineNo = ++lineNo,
                    LineKind = MfgLineKinds.Raw,
                    ItemCode = req.MaterialCode,
                    ItemName = req.MaterialName ?? string.Empty,
                    Unit = req.UnitCode ?? string.Empty,
                    Qty = req.Qty,
                    UnitCost = req.StandardUnitCost,
                    StandardUnitCost = req.StandardUnitCost,
                    Warehouse = req.WarehouseCode ?? string.Empty
                });
            if (bom is not null)
            {
                _txtItem.Text = bom.MaterialCode;
                _txtItemName.Text = bom.MaterialName ?? string.Empty;
                _txtUnit.Text = bom.OutputUnit;
                _txtStdCost.Text = bom.StandardUnitCost.ToString("0.00", CultureInfo.InvariantCulture);
                _inputDocumentKind = bom.InputDocumentKind;
                _outputDocumentKind = bom.OutputDocumentKind;
                _txtSourceWhs.Text = bom.InputWarehouseCode ??
                    bom.Lines.Select(l => l.WarehouseCode).FirstOrDefault(w => !string.IsNullOrWhiteSpace(w)) ?? string.Empty;
                var mainOutput = bom.Outputs.FirstOrDefault(o => string.Equals(o.OutputKind, "MAIN", StringComparison.OrdinalIgnoreCase));
                _txtTargetWhs.Text = bom.OutputWarehouseCode ?? mainOutput?.WarehouseCode ?? string.Empty;
                var scale = qty / bom.OutputQty;
                foreach (var expense in bom.Expenses)
                    _items.Add(new MfgItemVm
                    {
                        LineNo = ++lineNo,
                        LineKind = MfgLineKinds.Cost,
                        ItemCode = expense.ExpenseCode,
                        ItemName = expense.Description ?? expense.ExpenseCode,
                        Unit = "AMOUNT",
                        Qty = 1m,
                        UnitCost = decimal.Round(expense.Amount * scale, 2, MidpointRounding.AwayFromZero),
                        DebitAccount = expense.DebitAccountCode ?? string.Empty,
                        CreditAccount = expense.CreditAccountCode ?? string.Empty,
                        CostCenter = expense.CostCenter ?? string.Empty
                    });
                foreach (var output in bom.Outputs.Where(o => !string.Equals(o.OutputKind, "MAIN", StringComparison.OrdinalIgnoreCase)))
                    _items.Add(new MfgItemVm
                    {
                        LineNo = ++lineNo,
                        LineKind = string.Equals(output.OutputKind, "SCRAP", StringComparison.OrdinalIgnoreCase) ? MfgLineKinds.Scrap : MfgLineKinds.ByProduct,
                        ItemCode = output.MaterialCode,
                        ItemName = output.MaterialName ?? string.Empty,
                        Unit = output.UnitCode ?? string.Empty,
                        Qty = decimal.Round(output.Qty * scale, 4, MidpointRounding.AwayFromZero),
                        Warehouse = output.WarehouseCode ?? string.Empty,
                        CostSharePct = output.CostSharePct
                    });
                _txtOverheads.Text = "0.00";
            }
            CalculateCost();
            MessageBox.Show(this, Tf("MfgOrder.BomFetched", explosion.Requirements.Count), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("FetchBom", ex);
        }
    }

    private async Task ConfirmOrderAsync()
    {
        if (!_currentId.HasValue) return;
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.OrderUpdate))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            await service.ConfirmAsync(CurrentCompanyCode, _currentId.Value, CurrentUserId);
            _status = MfgOrderStatuses.Confirmed;
            RefreshStageButtons();
            MessageBox.Show(this, T("MfgOrder.Confirmed"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Confirm", ex);
        }
    }

    private async Task IssueOrderAsync()
    {
        if (!_currentId.HasValue) return;
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.ProductionPost))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            var lines = _items
                .Where(i => i.Id != Guid.Empty && i.StageQty > 0 && string.Equals(i.LineKind, MfgLineKinds.Raw, StringComparison.OrdinalIgnoreCase))
                .Select(i => new MfgIssueLine(i.Id, i.StageQty))
                .ToList();
            if (lines.Count == 0)
                throw new InvalidOperationException(T("MfgOrder.NothingToIssue"));
            var result = await service.IssueAsync(CurrentCompanyCode, _currentId.Value, lines, CurrentUserId);
            _status = result.Status;
            RefreshStageButtons();
            LoadOrder(int.Parse(_txtDocNo.Text, CultureInfo.InvariantCulture));
            MessageBox.Show(this, Tf("MfgOrder.IssuedLine", result.Amount), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Issue", ex);
        }
    }

    private async Task FinishOrderAsync()
    {
        if (!_currentId.HasValue) return;
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.ProductionPost))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            var finishQty = ParseDecimal(_txtFinishQty);
            if (finishQty <= 0)
                throw new InvalidOperationException(T("MfgOrder.FinishQtyInvalid"));
            var receipts = _items
                .Where(i => i.Id != Guid.Empty && i.StageQty > 0
                    && (string.Equals(i.LineKind, MfgLineKinds.ByProduct, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(i.LineKind, MfgLineKinds.Scrap, StringComparison.OrdinalIgnoreCase)))
                .Select(i => new MfgReceiptLine(i.Id, i.StageQty))
                .ToList();
            var result = await service.FinishAsync(CurrentCompanyCode, _currentId.Value, finishQty, receipts, CurrentUserId);
            _status = result.Status;
            RefreshStageButtons();
            LoadOrder(int.Parse(_txtDocNo.Text, CultureInfo.InvariantCulture));
            MessageBox.Show(this, Tf("MfgOrder.FinishedLine", result.Amount), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Finish", ex);
        }
    }

    private async Task CloseOrderAsync()
    {
        if (!_currentId.HasValue) return;
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.ProductionPost))
        {
            MessageBox.Show(this, T("MfgOrders.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!AskConfirm("MfgOrder.ConfirmClose")) return;
        try
        {
            if (AppHost.Manufacturing is not { } service)
                throw new InvalidOperationException(T("MfgOrders.ServiceUnavailable"));
            var result = await service.CloseAsync(CurrentCompanyCode, _currentId.Value, CurrentUserId);
            _status = result.Status;
            RefreshStageButtons();
            MessageBox.Show(this, Tf("MfgOrder.ClosedLine", result.Amount), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("Close", ex);
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        const string errorCode = "APP-2010";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var context = $"Operation={operation}; Window={GetType().Name}; Company={CurrentCompanyCode}";
        AppHost.Logging?.LogError("WindowPersistence",
            $"[{definition.Code}] {definition.Name} | {context} | {exception.Message}", exception);
        MessageBox.Show(this, $"[{definition.Code}] {exception.Message}", Title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ------------------------------------------------------------------
    // Print (JrnWin A4 pattern).
    // ------------------------------------------------------------------
    private void PrintOrder()
    {
        var root = new Border { Background = Brushes.White, Padding = new Thickness(40), Width = 794, Height = 1123 };
        var stack = new StackPanel();
        root.Child = stack;
        stack.Children.Add(new TextBlock
        {
            Text = CurrentCompanyDisplay, FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79))
        });
        stack.Children.Add(new TextBlock
        {
            Text = T("MfgOrder.Title"), FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 10)
        });
        void Meta(string labelKey, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            row.Children.Add(new TextBlock { Text = T(labelKey) + ": ", FontWeight = FontWeights.SemiBold, FontSize = 12 });
            row.Children.Add(new TextBlock { Text = value, FontSize = 12 });
            stack.Children.Add(row);
        }
        Meta("MfgOrder.OrderNo", _txtDocNo.Text.Trim());
        Meta("MfgOrder.Date", (_dtDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Meta("MfgOrder.TargetMaterial", $"{_txtItem.Text.Trim()} — {_txtItemName.Text.Trim()} x {_txtQty.Text.Trim()} {_txtUnit.Text.Trim()}");
        Meta("Invoice.Warehouse", _txtWhs.Text.Trim());
        var table = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var width in new[] { 110, 200, 70, 90, 100, 110 })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        void Cell(int row, int col, string text, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 11, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(3, 2, 3, 2), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col); table.Children.Add(tb);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, T("Invoice.Col.Code"), true); Cell(0, 1, T("Invoice.Col.Description"), true);
        Cell(0, 2, T("MfgOrder.Unit"), true); Cell(0, 3, T("MfgOrder.Quantity"), true);
        Cell(0, 4, T("Invoice.Col.PurchasePrice"), true); Cell(0, 5, T("Invoice.Col.LineTotal"), true);
        var rowIndex = 1;
        foreach (var item in _items)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(rowIndex, 0, item.ItemCode); Cell(rowIndex, 1, item.ItemName); Cell(rowIndex, 2, item.Unit);
            Cell(rowIndex, 3, item.Qty.ToString("0.###", CultureInfo.InvariantCulture));
            Cell(rowIndex, 4, item.UnitCost.ToString("N2", CultureInfo.InvariantCulture));
            Cell(rowIndex, 5, item.TotalCost.ToString("N2", CultureInfo.InvariantCulture));
            rowIndex++;
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(rowIndex, 4, T("MfgOrder.TotalCost"), true);
        Cell(rowIndex, 5, (_items.Sum(i => i.TotalCost) + ParseDecimal(_txtOverheads)).ToString("N2", CultureInfo.InvariantCulture), true);
        stack.Children.Add(table);

        var paginator = new SinglePagePaginator(root);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, Title);
    }

    private sealed class SinglePagePaginator : DocumentPaginator
    {
        private readonly FrameworkElement _page;
        public SinglePagePaginator(FrameworkElement page) => _page = page;
        public override bool IsPageCountValid => true;
        public override int PageCount => 1;
        public override Size PageSize { get; set; } = new(794, 1123);
        public override IDocumentPaginatorSource Source => null!;
        public override DocumentPage GetPage(int pageNumber)
        {
            _page.Measure(PageSize);
            _page.Arrange(new Rect(PageSize));
            _page.UpdateLayout();
            return new DocumentPage(_page);
        }
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("MfgOrder.Title");
        if (_grid.Columns.Count >= 16)
        {
            _grid.Columns[0].Header = "#";
            _grid.Columns[1].Header = T("MfgOrder.Col.Kind");
            _grid.Columns[2].Header = T("Invoice.Col.Code");
            _grid.Columns[3].Header = T("Invoice.Col.Description");
            _grid.Columns[4].Header = T("MfgOrder.Unit");
            _grid.Columns[5].Header = T("MfgOrder.Quantity");
            _grid.Columns[6].Header = T("MfgOrder.Col.Consumed");
            _grid.Columns[7].Header = T("MfgOrder.Col.StageQty");
            _grid.Columns[8].Header = T("Invoice.Col.PurchasePrice");
            _grid.Columns[9].Header = T("MfgOrders.StandardUnitCost");
            _grid.Columns[10].Header = T("Invoice.Warehouse");
            _grid.Columns[11].Header = T("MfgOrder.Col.DebitAccount");
            _grid.Columns[12].Header = T("MfgOrder.Col.CreditAccount");
            _grid.Columns[13].Header = T("MfgOrder.Col.CostCenter");
            _grid.Columns[14].Header = T("MfgOrder.Col.CostShare");
            _grid.Columns[15].Header = T("Invoice.Col.LineTotal");
        }
        RefreshLocalizedContent(Content as DependencyObject);
        if (_grid.ItemsSource is ICollectionView view) view.Refresh();
        CalculateCost();
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(VisualTreeHelper.GetChild(parent, i));
    }


    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 8, 0);
        btn.MinWidth = VisuId.ButtonMinWidth;
        btn.Tag = key;
        btn.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private static string SectionLabel(string key) => T(key);

    private static DataGridTextColumn TextCol(string path, double width, bool readOnly = false)
        => new()
        {
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = width,
            IsReadOnly = readOnly
        };

    private sealed class MfgItemVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _itemCode = "", _itemName = "", _unit = "", _lineKind = MfgLineKinds.Raw, _warehouse = "";
        private string _debitAccount = "", _creditAccount = "", _costCenter = "";
        private decimal _qty = 1, _cost, _stdCost, _consumed, _produced, _stageQty, _sharePct;

        public Guid Id { get; set; }
        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string LineKind
        {
            get => _lineKind;
            set { _lineKind = value; OnChanged(); OnChanged(nameof(Section)); }
        }
        public string Section => _lineKind switch
        {
            MfgLineKinds.Raw => $"1 - {SectionLabel("Bom.Inputs")}",
            MfgLineKinds.Cost => $"2 - {SectionLabel("Bom.Expenses")}",
            _ => $"3 - {SectionLabel("Bom.Outputs")}"
        };
        public string ItemCode { get => _itemCode; set { _itemCode = value; OnChanged(); } }
        public string ItemName { get => _itemName; set { _itemName = value; OnChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnChanged(); } }
        public string Warehouse { get => _warehouse; set { _warehouse = value; OnChanged(); } }
        public string DebitAccount { get => _debitAccount; set { _debitAccount = value; OnChanged(); } }
        public string CreditAccount { get => _creditAccount; set { _creditAccount = value; OnChanged(); } }
        public string CostCenter { get => _costCenter; set { _costCenter = value; OnChanged(); } }

        public decimal Qty { get => _qty; set { _qty = value; OnChanged(nameof(QtyText)); OnChanged(nameof(TotalCostText)); } }
        public decimal UnitCost { get => _cost; set { _cost = value; OnChanged(nameof(CostText)); OnChanged(nameof(TotalCostText)); } }
        public decimal StandardUnitCost { get => _stdCost; set { _stdCost = value; OnChanged(nameof(StdCostText)); } }
        public decimal ConsumedQty { get => _consumed; set { _consumed = value; OnChanged(nameof(ConsumedText)); } }
        public decimal ProducedQty { get => _produced; set { _produced = value; OnChanged(nameof(ConsumedText)); } }
        public decimal StageQty { get => _stageQty; set { _stageQty = value; OnChanged(nameof(StageQtyText)); } }
        public decimal CostSharePct { get => _sharePct; set { _sharePct = value; OnChanged(nameof(ShareText)); } }

        public decimal TotalCost => _qty * _cost;
        public string TotalCostText => TotalCost.ToString("N2", CultureInfo.InvariantCulture);

        public string QtyText
        {
            get => _qty.ToString("0.###", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Qty = v; }
        }

        public string CostText
        {
            get => _cost.ToString("0.####", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) UnitCost = v; }
        }

        /// <summary>The BOUND standard-cost text (MFG-4 fix).</summary>
        public string StdCostText
        {
            get => _stdCost.ToString("0.####", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) StandardUnitCost = v; }
        }

        public string ConsumedText => string.Equals(_lineKind, MfgLineKinds.Raw, StringComparison.OrdinalIgnoreCase)
            ? _consumed.ToString("0.###", CultureInfo.InvariantCulture)
            : _produced.ToString("0.###", CultureInfo.InvariantCulture);

        public string StageQtyText
        {
            get => _stageQty.ToString("0.###", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) StageQty = v; }
        }

        public string ShareText
        {
            get => _sharePct.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) CostSharePct = v; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
