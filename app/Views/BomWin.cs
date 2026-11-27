using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Application.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Bill of Materials master (migration 058): the reusable, versioned
/// recipe the ribbon's "Manufacturing templates" entry opens (sweep
/// MFG-6 — it previously opened a blank production order). List +
/// editor at the app standard: the header carries material (picker),
/// version, active/default flags and the output batch quantity + unit;
/// the lines carry component (picker-filled code), quantity in the
/// line's unit, scrap %, per-line warehouse and the standard cost
/// snapshot the repository takes from the valuation engine at save.
/// Explode expands the saved BOM multi-level (cycle-safe, MFG-1001)
/// into leaf requirements. Full persistence through BomService; every
/// failure is reported through AppHost.Errors with its catalog code.
/// </summary>
public sealed class BomWin : BaseWin
{
    protected override string? ScheduleTintName => "Fern";

    private readonly BomService? _service;
    private readonly List<Bom> _boms = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();
    private readonly List<(DataGridColumn Column, string Key)> _lineHeaders = new();
    private readonly List<(DataGridColumn Column, string Key)> _explosionHeaders = new();

    private readonly DataGrid _grid = new();
    private readonly DataGrid _linesGrid = new();
    private readonly DataGrid _explosionGrid = new();
    private readonly TextBlock _statusBar = new();
    private readonly ComboBox _material = new() { IsEditable = true };
    private readonly TextBox _materialName = new() { IsReadOnly = true };
    private readonly TextBox _version = new() { Text = "1" };
    private readonly TextBox _outputQty = new() { Text = "1" };
    private readonly ComboBox _outputUnit = new() { IsEditable = true };
    private readonly CheckBox _isActive = new() { IsChecked = true };
    private readonly CheckBox _isDefault = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _explodeQty = new() { Text = "1" };
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _addLineButton = new();
    private readonly Button _removeLineButton = new();
    private readonly Button _explodeButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private readonly ObservableCollection<BomLineVm> _lines = new();
    private Guid? _selectedId;
    private List<MfgMaterialOption> _materialOptions = new();

    public BomWin()
    {
        _service = AppHost.Boms;
        Width = 1080;
        Height = 640;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Manufacturing.BomView))
        {
            MessageBox.Show(this, T("Bom.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_addLineButton, "Invoice.AddLine");
        StyleButton(_removeLineButton, "Invoice.RemoveLine");
        StyleButton(_explodeButton, "Bom.Explode");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _addLineButton.Click += (_, _) => AddLine();
        _removeLineButton.Click += (_, _) => RemoveLine();
        _explodeButton.Click += async (_, _) => await ExplodeAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _addLineButton, _removeLineButton, _explodeButton, _refreshButton, _closeButton })
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

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 340 });

        ConfigureGrid(_grid, readOnly: true);
        _grid.Columns.Add(TextCol(_headers, "Bom.Col.Material", nameof(Bom.MaterialCode), 110));
        _grid.Columns.Add(TextCol(_headers, "Bom.Col.Version", nameof(Bom.Version), 60));
        _grid.Columns.Add(TextCol(_headers, "Bom.Col.OutputQty", nameof(Bom.OutputQty), 80));
        _grid.Columns.Add(TextCol(_headers, "Bom.Col.Active", nameof(Bom.IsActive), 60));
        _grid.Columns.Add(TextCol(_headers, "Bom.Col.Default", nameof(Bom.IsDefault), 60));
        _grid.Columns.Add(TextCol(_headers, "Bom.Col.StandardUnitCost", nameof(Bom.StandardUnitCost), 100));
        _grid.SelectionChanged += (_, _) => OnBomSelected();
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });

        var form = CreateFormGrid(2);
        AddFormField(_labels, form, 0, 0, "Bom.Col.Material", _material);
        AddFormField(_labels, form, 0, 1, "Invoice.Col.Description", _materialName);
        AddFormField(_labels, form, 1, 0, "Bom.Col.Version", _version);
        AddFormField(_labels, form, 1, 1, "Bom.Col.OutputQty", _outputQty);
        AddFormField(_labels, form, 2, 0, "MfgOrder.Unit", _outputUnit);
        AddFormField(_labels, form, 2, 1, "Vouchers.Memo", _notes);
        AddFormElement(form, 3, 0, _isActive);
        AddFormElement(form, 3, 1, _isDefault);
        _material.SelectionChanged += (_, _) => SyncMaterialName();
        _outputUnit.ItemsSource = new[] { "PCS", "KG", "G", "L", "M", "BOX" };
        _outputUnit.Text = "PCS";
        Grid.SetRow(form, 0);
        right.Children.Add(form);

        ConfigureGrid(_linesGrid, readOnly: false);
        _linesGrid.Columns.Add(TextCol(_lineHeaders, "Invoice.Col.Code", nameof(BomLineVm.ComponentCode), 110));
        _linesGrid.Columns.Add(TextCol(_lineHeaders, "Invoice.Col.Description", nameof(BomLineVm.ComponentName), 150));
        _linesGrid.Columns.Add(TextCol(_lineHeaders, "MfgOrder.Quantity", nameof(BomLineVm.QtyText), 80));
        _linesGrid.Columns.Add(TextCol(_lineHeaders, "MfgOrder.Unit", nameof(BomLineVm.Unit), 60));
        _linesGrid.Columns.Add(TextCol(_lineHeaders, "Bom.Col.ScrapPct", nameof(BomLineVm.ScrapText), 70));
        _linesGrid.Columns.Add(TextCol(_lineHeaders, "Invoice.Warehouse", nameof(BomLineVm.Warehouse), 90));
        _linesGrid.Columns.Add(TextCol(_lineHeaders, "Bom.Col.StandardUnitCost", nameof(BomLineVm.StandardCostText), 100, readOnly: true));
        _linesGrid.ItemsSource = _lines;
        Grid.SetRow(_linesGrid, 1);
        right.Children.Add(_linesGrid);

        var explosionPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var explodeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        explodeRow.Children.Add(new TextBlock { Text = T("Bom.ExplodeQty"), Tag = "Bom.ExplodeQty", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _explodeQty.Width = 90;
        explodeRow.Children.Add(_explodeQty);
        DockPanel.SetDock(explodeRow, Dock.Top);
        explosionPanel.Children.Add(explodeRow);
        ConfigureGrid(_explosionGrid, readOnly: true);
        _explosionGrid.Columns.Add(TextCol(_explosionHeaders, "Invoice.Col.Code", nameof(BomRequirement.MaterialCode), 110));
        _explosionGrid.Columns.Add(TextCol(_explosionHeaders, "MfgOrder.Quantity", nameof(BomRequirement.Qty), 90));
        _explosionGrid.Columns.Add(TextCol(_explosionHeaders, "Bom.Col.BaseQty", nameof(BomRequirement.BaseQty), 90));
        _explosionGrid.Columns.Add(TextCol(_explosionHeaders, "Bom.Col.StandardUnitCost", nameof(BomRequirement.StandardUnitCost), 100));
        _explosionGrid.Columns.Add(TextCol(_explosionHeaders, "Invoice.Col.LineTotal", nameof(BomRequirement.StandardAmount), 100));
        explosionPanel.Children.Add(_explosionGrid);
        Grid.SetRow(explosionPanel, 2);
        right.Children.Add(explosionPanel);

        Grid.SetColumn(right, 1);
        content.Children.Add(right);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Bom.Title");
        _isActive.Content = T("Bom.Col.Active");
        _isDefault.Content = T("Bom.Col.Default");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers.Concat(_lineHeaders).Concat(_explosionHeaders))
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _addLineButton, _removeLineButton, _explodeButton, _refreshButton, _closeButton })
            if (b.Tag is string key)
                b.Content = T(key);
        RefreshLocalizedContent(Content as DependencyObject);
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null)
            {
                _statusBar.Text = T("Bom.ServiceUnavailable");
                return;
            }
            _materialOptions = (await _service.GetMaterialOptionsAsync(CurrentCompanyCode)).ToList();
            _material.ItemsSource = _materialOptions.Select(m => m.Code).ToList();
            var rows = await _service.GetAllAsync(CurrentCompanyCode, includeInactive: true);
            _boms.Clear();
            _boms.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _boms;
            _statusBar.Text = Tf("Bom.StatusLine", _boms.Count);
        }
        catch (Exception ex)
        {
            ReportFailure("Refresh", ex);
        }
    }

    private void OnBomSelected()
    {
        if (_grid.SelectedItem is not Bom bom)
            return;
        _selectedId = bom.Id;
        _material.Text = bom.MaterialCode;
        _materialName.Text = bom.MaterialName ?? string.Empty;
        _version.Text = bom.Version.ToString(CultureInfo.InvariantCulture);
        _outputQty.Text = bom.OutputQty.ToString("0.###", CultureInfo.InvariantCulture);
        _outputUnit.Text = bom.OutputUnit;
        _isActive.IsChecked = bom.IsActive;
        _isDefault.IsChecked = bom.IsDefault;
        _notes.Text = bom.Notes ?? string.Empty;
        _lines.Clear();
        foreach (var line in bom.Lines)
            _lines.Add(BomLineVm.From(line));
        _explosionGrid.ItemsSource = null;
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _grid.SelectedItem = null;
        _material.Text = string.Empty;
        _materialName.Text = string.Empty;
        _version.Text = "1";
        _outputQty.Text = "1";
        _outputUnit.Text = "PCS";
        _isActive.IsChecked = true;
        _isDefault.IsChecked = false;
        _notes.Text = string.Empty;
        _lines.Clear();
        AddLine();
        _explosionGrid.ItemsSource = null;
    }

    private void SyncMaterialName()
    {
        var code = _material.Text.Trim();
        var option = _materialOptions.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
        if (option is not null)
            _materialName.Text = option.Name ?? string.Empty;
    }

    private void AddLine()
    {
        _lines.Add(new BomLineVm { Qty = 1, Unit = "KG" });
        Renumber();
    }

    private void RemoveLine()
    {
        if (_linesGrid.SelectedItem is BomLineVm selected && _lines.Count > 1)
        {
            _lines.Remove(selected);
            Renumber();
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < _lines.Count; i++)
            _lines[i].LineNo = i + 1;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Bom.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Manufacturing.BomManage))
                throw new InvalidOperationException(T("Bom.PermissionDenied"));
            _linesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            _linesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            SyncMaterialName();
            var existing = _selectedId is Guid id ? _boms.FirstOrDefault(b => b.Id == id) : null;
            var bom = existing ?? new Bom();
            bom.MaterialCode = _material.Text.Trim();
            bom.MaterialName = NullIfBlank(_materialName.Text);
            bom.Version = int.TryParse(_version.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 1;
            bom.OutputQty = ParseDecimal(_outputQty);
            bom.OutputUnit = string.IsNullOrWhiteSpace(_outputUnit.Text) ? "PCS" : _outputUnit.Text.Trim();
            bom.IsActive = _isActive.IsChecked == true;
            bom.IsDefault = _isDefault.IsChecked == true;
            bom.Notes = NullIfBlank(_notes.Text);
            bom.Lines.Clear();
            foreach (var vm in _lines.Where(l => !string.IsNullOrWhiteSpace(l.ComponentCode)))
            {
                bom.Lines.Add(new BomLine
                {
                    BomId = bom.Id,
                    ComponentCode = vm.ComponentCode.Trim(),
                    ComponentName = NullIfBlank(vm.ComponentName),
                    Qty = vm.Qty,
                    UnitCode = NullIfBlank(vm.Unit),
                    ScrapPct = vm.ScrapPct,
                    WarehouseCode = NullIfBlank(vm.Warehouse),
                    StandardUnitCost = vm.StandardUnitCost
                });
            }
            await _service.SaveAsync(CurrentCompanyCode, bom, CurrentUserId);
            _selectedId = bom.Id;
            _statusBar.Text = T("Bom.Saved");
            await RefreshAsync();
            _grid.SelectedItem = _boms.FirstOrDefault(b => b.Id == bom.Id);
        }
        catch (Exception ex)
        {
            ReportFailure("Save", ex);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Bom.SelectFirst"));
            if (!AppHost.Permissions.Has(PermCat.Manufacturing.BomManage))
                throw new InvalidOperationException(T("Bom.PermissionDenied"));
            if (!AskConfirm("Bom.ConfirmDelete")) return;
            await _service.DeleteAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReportFailure("Delete", ex);
        }
    }

    private async Task ExplodeAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Bom.SelectFirst"));
            var qty = ParseDecimal(_explodeQty);
            if (qty <= 0)
                throw new InvalidOperationException(T("Bom.ExplodeQtyInvalid"));
            var explosion = await _service.ExplodeAsync(CurrentCompanyCode, id, qty);
            _explosionGrid.ItemsSource = explosion.Requirements;
            _statusBar.Text = Tf("Bom.ExplosionLine", explosion.Requirements.Count, explosion.TotalStandardCost);
        }
        catch (Exception ex)
        {
            ReportFailure("Explode", ex);
        }
    }

    private void ReportFailure(string operation, Exception exception)
    {
        AppHost.Errors.Report(exception is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed,
            exception, $"BomWin.{operation}");
        MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static decimal ParseDecimal(TextBox box)
        => decimal.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ||
           decimal.TryParse(box.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out v) ? v : 0m;

    private static void ConfigureGrid(DataGrid grid, bool readOnly)
    {
        grid.AutoGenerateColumns = false;
        grid.CanUserAddRows = false;
        grid.CanUserDeleteRows = false;
        grid.IsReadOnly = readOnly;
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

    private static DataGridTextColumn TextCol(List<(DataGridColumn Column, string Key)> headers, string key, string path, double width, bool readOnly = false)
    {
        var column = new DataGridTextColumn
        {
            Binding = new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(width),
            IsReadOnly = readOnly
        };
        headers.Add((column, key));
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

    private sealed class BomLineVm : INotifyPropertyChanged
    {
        private int _lineNo;
        private string _componentCode = "", _componentName = "", _unit = "";
        private string _warehouse = "";
        private decimal _qty = 1, _scrap, _stdCost;

        public static BomLineVm From(BomLine line) => new()
        {
            LineNo = line.LineNo,
            ComponentCode = line.ComponentCode,
            ComponentName = line.ComponentName ?? string.Empty,
            Qty = line.Qty,
            Unit = line.UnitCode ?? string.Empty,
            ScrapPct = line.ScrapPct,
            Warehouse = line.WarehouseCode ?? string.Empty,
            StandardUnitCost = line.StandardUnitCost
        };

        public int LineNo { get => _lineNo; set { _lineNo = value; OnChanged(); } }
        public string ComponentCode { get => _componentCode; set { _componentCode = value; OnChanged(); } }
        public string ComponentName { get => _componentName; set { _componentName = value; OnChanged(); } }
        public string Unit { get => _unit; set { _unit = value; OnChanged(); } }
        public string Warehouse { get => _warehouse; set { _warehouse = value; OnChanged(); } }
        public decimal Qty { get => _qty; set { _qty = value; OnChanged(nameof(QtyText)); } }
        public decimal ScrapPct { get => _scrap; set { _scrap = value; OnChanged(nameof(ScrapText)); } }
        public decimal StandardUnitCost { get => _stdCost; set { _stdCost = value; OnChanged(nameof(StandardCostText)); } }

        public string QtyText
        {
            get => _qty.ToString("0.###", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) Qty = v; }
        }
        public string ScrapText
        {
            get => _scrap.ToString("0.##", CultureInfo.InvariantCulture);
            set { if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) ScrapPct = v; }
        }
        public string StandardCostText => _stdCost.ToString("0.####", CultureInfo.InvariantCulture);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
