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

namespace ShouTech.App.Views;

/// <summary>
/// Delivery desk (ribbon completion F1): one window, two tabs.
/// Requests tab: delivery_requests manager — new request (auto doc number),
/// assign driver, and the status flow Pending → Assigned → Delivered /
/// Cancelled enforced by the service/repository. Settlements tab: driver
/// cash closings — CloseDriverCash creates a Closed driver_settlements row
/// with net = collected - fees. Actions live in bottom command bars.
/// </summary>
public sealed class DeliveryDeskWin : BaseWin
{
    private readonly DefinitionService? _service;
    private readonly List<DeliveryRequest> _requests = new();
    private readonly List<DriverSettlement> _settlements = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly DataGrid _settlementGrid = new();
    private readonly TextBox _party = new();
    private readonly TextBox _address = new();
    private readonly TextBox _area = new();
    private readonly TextBox _driver = new();
    private readonly DatePicker _requestDate = new();
    private readonly TextBox _amount = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _settleDriver = new();
    private readonly DatePicker _settleDate = new();
    private readonly TextBox _collected = new();
    private readonly TextBox _fees = new();
    private readonly TextBlock _requestsHeader = new();
    private readonly TextBlock _settlementsHeader = new();
    private readonly TextBlock _statusBar = new();
    private readonly TabItem _requestsTab = new();
    private readonly TabItem _settlementsTab = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _assignButton = new();
    private readonly Button _deliverButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _closeCashButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private string? _selectedRequestId;

    public DeliveryDeskWin()
    {
        _service = AppHost.Definitions;
        Width = 1180;
        Height = 740;
        MinWidth = 980;
        MinHeight = 600;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Delivery.OrderView) && !AppHost.Permissions.Has(PermCat.Delivery.DriverManage))
        {
            MessageBox.Show(this, T("Delivery.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var tabs = new TabControl();

        // ------------------------------------------------------- Requests tab
        var requestsRoot = new DockPanel { Margin = new Thickness(8) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_assignButton, "Delivery.Assign");
        StyleButton(_deliverButton, "Delivery.Deliver");
        StyleButton(_cancelButton, "Delivery.CancelRequest");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _assignButton.Click += async (_, _) => await TransitionAsync(DeliveryStatuses.Assigned);
        _deliverButton.Click += async (_, _) => await TransitionAsync(DeliveryStatuses.Delivered);
        _cancelButton.Click += async (_, _) => await TransitionAsync(DeliveryStatuses.Cancelled);
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _assignButton, _deliverButton, _cancelButton, _refreshButton, _closeButton })
            bar.Children.Add(b);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(barBorder, Dock.Bottom);
        requestsRoot.Children.Add(barBorder);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        var listPanel = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _requestsHeader.FontWeight = FontWeights.SemiBold;
        _requestsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_requestsHeader, Dock.Top);
        listPanel.Children.Add(_requestsHeader);
        ConfigureGrid(_grid);
        _grid.Columns.Add(TextCol("Delivery.DocNo", nameof(DeliveryRequest.DocNo), 70, center: true));
        _grid.Columns.Add(TextCol("Delivery.RequestDate", nameof(DeliveryRequest.RequestDate), 100, center: true));
        _grid.Columns.Add(TextCol("Delivery.PartyCode", nameof(DeliveryRequest.PartyCode), 110));
        _grid.Columns.Add(TextCol("Delivery.AreaCode", nameof(DeliveryRequest.AreaCode), 90));
        _grid.Columns.Add(TextCol("Delivery.DriverCode", nameof(DeliveryRequest.DriverCode), 100));
        _grid.Columns.Add(TextCol("Delivery.Status", nameof(DeliveryRequest.Status), 90));
        _grid.Columns.Add(TextCol("Delivery.Amount", nameof(DeliveryRequest.Amount), 100, numeric: true));
        _grid.SelectionChanged += (_, _) => OnRequestSelected();
        listPanel.Children.Add(_grid);
        Grid.SetColumn(listPanel, 0);
        content.Children.Add(listPanel);

        // Editor = a one-pair form grid: side labels, aligned edges
        // down the whole editor (Phenix rule).
        var editor = CreateFormGrid(1);
        AddFormField(_labels, editor, 0, 0, "Delivery.PartyCode", _party);
        AddFormField(_labels, editor, 1, 0, "Delivery.Address", _address);
        AddFormField(_labels, editor, 2, 0, "Delivery.AreaCode", _area);
        AddFormField(_labels, editor, 3, 0, "Delivery.DriverCode", _driver);
        AddFormField(_labels, editor, 4, 0, "Delivery.RequestDate", _requestDate);
        AddFormField(_labels, editor, 5, 0, "Delivery.Amount", _amount);
        AddFormField(_labels, editor, 6, 0, "Delivery.Notes", _notes);
        _requestDate.SelectedDate = DateTime.Today;
        Grid.SetColumn(editor, 1);
        content.Children.Add(editor);
        requestsRoot.Children.Add(content);
        _requestsTab.Content = requestsRoot;
        tabs.Items.Add(_requestsTab);

        // ---------------------------------------------------- Settlements tab
        var settleRoot = new DockPanel { Margin = new Thickness(8) };
        var settleBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_closeCashButton, "Delivery.CloseCash");
        _closeCashButton.Click += async (_, _) => await CloseCashAsync();
        settleBar.Children.Add(_closeCashButton);
        var settleBarBorder = new Border
        {
            Child = settleBar,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        settleBarBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(settleBarBorder, Dock.Bottom);
        settleRoot.Children.Add(settleBarBorder);

        var settleContent = new Grid();
        settleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        settleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        var settleList = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        _settlementsHeader.FontWeight = FontWeights.SemiBold;
        _settlementsHeader.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_settlementsHeader, Dock.Top);
        settleList.Children.Add(_settlementsHeader);
        ConfigureGrid(_settlementGrid);
        _settlementGrid.Columns.Add(TextCol("Delivery.DriverCode", nameof(DriverSettlement.DriverCode), 110));
        _settlementGrid.Columns.Add(TextCol("Delivery.SettleDate", nameof(DriverSettlement.SettleDate), 100, center: true));
        _settlementGrid.Columns.Add(TextCol("Delivery.Collected", nameof(DriverSettlement.CollectedAmount), 110, numeric: true));
        _settlementGrid.Columns.Add(TextCol("Delivery.Fees", nameof(DriverSettlement.FeesAmount), 100, numeric: true));
        _settlementGrid.Columns.Add(TextCol("Delivery.Net", nameof(DriverSettlement.NetAmount), 110, numeric: true));
        _settlementGrid.Columns.Add(TextCol("Delivery.Status", nameof(DriverSettlement.Status), 90));
        settleList.Children.Add(_settlementGrid);
        Grid.SetColumn(settleList, 0);
        settleContent.Children.Add(settleList);

        var settleEditor = CreateFormGrid(1);
        AddFormField(_labels, settleEditor, 0, 0, "Delivery.DriverCode", _settleDriver);
        AddFormField(_labels, settleEditor, 1, 0, "Delivery.SettleDate", _settleDate);
        AddFormField(_labels, settleEditor, 2, 0, "Delivery.Collected", _collected);
        AddFormField(_labels, settleEditor, 3, 0, "Delivery.Fees", _fees);
        _settleDate.SelectedDate = DateTime.Today;
        Grid.SetColumn(settleEditor, 1);
        settleContent.Children.Add(settleEditor);
        settleRoot.Children.Add(settleContent);
        _settlementsTab.Content = settleRoot;
        tabs.Items.Add(_settlementsTab);

        root.Children.Add(tabs);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Delivery.Title");
        _requestsTab.Header = T("Delivery.Requests");
        _settlementsTab.Header = T("Delivery.Settlements");
        _requestsHeader.Text = T("Delivery.Requests");
        _settlementsHeader.Text = T("Delivery.Settlements");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[] { _newButton, _saveButton, _deleteButton, _assignButton, _deliverButton, _cancelButton, _closeCashButton, _refreshButton, _closeButton })
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
                _statusBar.Text = T("Delivery.ServiceUnavailable");
                return;
            }
            var rows = await _service.ListDeliveryRequestsAsync(CurrentCompanyCode);
            _requests.Clear();
            _requests.AddRange(rows);
            _grid.ItemsSource = null;
            _grid.ItemsSource = _requests;
            var settlements = await _service.ListDriverSettlementsAsync(CurrentCompanyCode);
            _settlements.Clear();
            _settlements.AddRange(settlements);
            _settlementGrid.ItemsSource = null;
            _settlementGrid.ItemsSource = _settlements;
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRequestSelected()
    {
        if (_grid.SelectedItem is not DeliveryRequest request)
            return;
        _selectedRequestId = request.Id;
        _party.Text = request.PartyCode ?? string.Empty;
        _address.Text = request.Address ?? string.Empty;
        _area.Text = request.AreaCode ?? string.Empty;
        _driver.Text = request.DriverCode ?? string.Empty;
        _requestDate.SelectedDate = request.RequestDate.ToDateTime(TimeOnly.MinValue);
        _amount.Text = request.Amount.ToString("0.##", CultureInfo.CurrentCulture);
        _notes.Text = request.Notes ?? string.Empty;
        _settleDriver.Text = request.DriverCode ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Delivery.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Delivery.OrderCreate) && !AppHost.Permissions.Has(PermCat.Delivery.OrderUpdate))
                throw new InvalidOperationException(T("Delivery.PermissionDenied"));
            var existing = _requests.FirstOrDefault(r => r.Id == _selectedRequestId);
            var request = new DeliveryRequest
            {
                Id = _selectedRequestId ?? string.Empty,
                DocNo = existing?.DocNo ?? 0,
                RequestDate = _requestDate.SelectedDate.HasValue
                    ? DateOnly.FromDateTime(_requestDate.SelectedDate.Value)
                    : DateOnly.FromDateTime(DateTime.Today),
                PartyCode = NullIfBlank(_party.Text),
                Address = NullIfBlank(_address.Text),
                AreaCode = NullIfBlank(_area.Text),
                DriverCode = NullIfBlank(_driver.Text),
                Status = existing?.Status ?? DeliveryStatuses.Pending,
                Amount = ParseDecimal(_amount.Text),
                Notes = NullIfBlank(_notes.Text)
            };
            var id = await _service.SaveDeliveryRequestAsync(CurrentCompanyCode, request, CurrentUserId);
            _selectedRequestId = id;
            MessageBox.Show(this, T("Delivery.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedRequestId is null)
                return;
            if (!AppHost.Permissions.Has(PermCat.Delivery.OrderCancel))
                throw new InvalidOperationException(T("Delivery.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteDeliveryRequestAsync(CurrentCompanyCode, _selectedRequestId, CurrentUserId);
            _selectedRequestId = null;
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task TransitionAsync(string newStatus)
    {
        try
        {
            if (_service is null || _selectedRequestId is null)
                return;
            if (!AppHost.Permissions.Has(PermCat.Delivery.OrderUpdate))
                throw new InvalidOperationException(T("Delivery.PermissionDenied"));
            var driver = newStatus == DeliveryStatuses.Assigned ? NullIfBlank(_driver.Text) : null;
            await _service.SetDeliveryStatusAsync(CurrentCompanyCode, _selectedRequestId, newStatus, driver, CurrentUserId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CloseCashAsync()
    {
        try
        {
            if (_service is null)
                throw new InvalidOperationException(T("Delivery.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Delivery.CashboxClose))
                throw new InvalidOperationException(T("Delivery.PermissionDenied"));
            var driver = _settleDriver.Text.Trim();
            if (driver.Length == 0)
                throw new InvalidOperationException(T("Delivery.DriverRequired"));
            var settleDate = _settleDate.SelectedDate.HasValue
                ? DateOnly.FromDateTime(_settleDate.SelectedDate.Value)
                : DateOnly.FromDateTime(DateTime.Today);
            await _service.CloseDriverCashAsync(
                CurrentCompanyCode, driver, settleDate,
                ParseDecimal(_collected.Text), ParseDecimal(_fees.Text), CurrentUserId);
            MessageBox.Show(this, T("Delivery.SettlementClosed"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            _collected.Clear();
            _fees.Clear();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearEditor()
    {
        _selectedRequestId = null;
        _grid.SelectedItem = null;
        _party.Clear();
        _address.Clear();
        _area.Clear();
        _driver.Clear();
        _requestDate.SelectedDate = DateTime.Today;
        _amount.Clear();
        _notes.Clear();
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        var open = _requests.Count(r => r.Status is DeliveryStatuses.Pending or DeliveryStatuses.Assigned);
        var net = _settlements.Sum(s => s.NetAmount);
        _statusBar.Text = $"{_requests.Count} · {T("Delivery.OpenRequests")}: {open} · {T("Delivery.Net")}: {net:N2}";
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

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal ParseDecimal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;
        if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ||
            decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }
}
