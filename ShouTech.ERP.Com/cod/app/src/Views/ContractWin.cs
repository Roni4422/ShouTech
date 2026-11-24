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
using ShouTech.Domain;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Sales contracts console (owner sales spec section 10, migration
/// 101, workstream W7c): the one window for the contracts pillar —
/// master list + header editor + lines grid (edited as
/// "material, qty, price, next date, interval days, scheduled qty"
/// lines, the CommWin tiers pattern), status actions
/// (Activate / Suspend / Renew), the scheduled-delivery generation
/// run, the expiry sweep, an expiring-soon view, and the selected
/// contract's fulfillment + profitability figures (contracted vs
/// delivered vs invoiced, remaining, revenue vs COGS margin).
/// Contract prices feed the pricing engine as resolution level (a);
/// orders linked to a contract are validated by the repositories
/// (SAL-7023 / SAL-7015). RBAC: SALES.CONTRACT.VIEW gates the window,
/// MANAGE gates every write.
/// </summary>
public sealed class ContractWin : BaseWin
{
    protected override string? ScheduleTintName => "Quince";

    private static readonly char[] NewLineSeparators = { '\r', '\n' };

    private readonly ContractService? _service;
    private readonly List<SalesContract> _contracts = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _contractGrid = new();
    private readonly DataGrid _lineGrid = new();
    private readonly TextBlock _statusBar = new();
    private readonly TextBlock _fulfillmentBar = new();

    private readonly TextBox _partyId = new();
    private readonly TextBox _partyCode = new();
    private readonly TextBox _partyName = new();
    private readonly TextBox _title = new();
    private readonly DatePicker _startDate = new();
    private readonly DatePicker _endDate = new();
    private readonly TextBox _currency = new();
    private readonly TextBox _paymentTerms = new();
    private readonly TextBox _guaranteeText = new();
    private readonly TextBox _guaranteeAmount = new();
    private readonly TextBox _notes = new();
    private readonly TextBox _lines = new();

    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _activateButton = new();
    private readonly Button _suspendButton = new();
    private readonly Button _renewButton = new();
    private readonly Button _generateButton = new();
    private readonly Button _expireButton = new();
    private readonly Button _expiringButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private Guid? _selectedId;
    private string _selectedStatus = ContractStatuses.Draft;
    private string _selectedNo = string.Empty;

    public ContractWin()
    {
        _service = AppHost.Contracts;
        Width = 1120;
        Height = 660;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.ContractView))
        {
            MessageBox.Show(this, T("Contract.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var bar = BottomBar(_newButton, _saveButton, _deleteButton, _activateButton, _suspendButton,
            _renewButton, _generateButton, _expireButton, _expiringButton, _refreshButton, _closeButton);
        StyleButton(_newButton, "Common.New");
        StyleButton(_saveButton, "Common.Save");
        StyleButton(_deleteButton, "Common.Delete");
        StyleButton(_activateButton, "Contract.Activate");
        StyleButton(_suspendButton, "Contract.Suspend");
        StyleButton(_renewButton, "Contract.Renew");
        StyleButton(_generateButton, "Contract.Generate");
        StyleButton(_expireButton, "Contract.ExpireSweep");
        StyleButton(_expiringButton, "Contract.Expiring");
        StyleButton(_refreshButton, "Common.Refresh");
        StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => ClearEditor();
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        _activateButton.Click += async (_, _) => await SetStatusAsync(ContractStatuses.Active);
        _suspendButton.Click += async (_, _) => await SetStatusAsync(ContractStatuses.Suspended);
        _renewButton.Click += async (_, _) => await RenewAsync();
        _generateButton.Click += async (_, _) => await GenerateAsync();
        _expireButton.Click += async (_, _) => await ExpireSweepAsync();
        _expiringButton.Click += async (_, _) => await ShowExpiringAsync();
        _refreshButton.Click += async (_, _) => await RefreshAsync();
        _closeButton.Click += (_, _) => Close();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });

        ConfigureGrid(_contractGrid);
        _contractGrid.Columns.Add(TextCol("Contract.Col.No", nameof(SalesContract.ContractNo), 110));
        _contractGrid.Columns.Add(TextCol("Contract.Col.Party", nameof(SalesContract.PartyName), 150));
        _contractGrid.Columns.Add(TextCol("Contract.Col.Title", nameof(SalesContract.Title), 180));
        _contractGrid.Columns.Add(TextCol("Contract.Col.Start", nameof(SalesContract.StartDate), 90));
        _contractGrid.Columns.Add(TextCol("Contract.Col.End", nameof(SalesContract.EndDate), 90));
        _contractGrid.Columns.Add(TextCol("Contract.Col.Status", nameof(SalesContract.Status), 90));
        _contractGrid.Columns.Add(TextCol("Contract.Col.Currency", nameof(SalesContract.CurrencyCode), 70));
        _contractGrid.SelectionChanged += async (_, _) => await OnSelectedAsync();
        Grid.SetRow(_contractGrid, 0);
        content.Children.Add(_contractGrid);

        var lower = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 320 });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var form = CreateFormGrid(1);
        AddFormField(_labels, form, 0, 0, "Contract.PartyId", _partyId);
        AddFormField(_labels, form, 1, 0, "Contract.PartyCode", _partyCode);
        AddFormField(_labels, form, 2, 0, "Contract.Col.Party", _partyName);
        AddFormField(_labels, form, 3, 0, "Contract.Col.Title", _title);
        AddFormField(_labels, form, 4, 0, "Contract.Col.Start", _startDate);
        AddFormField(_labels, form, 5, 0, "Contract.Col.End", _endDate);
        AddFormField(_labels, form, 6, 0, "Contract.Col.Currency", _currency);
        AddFormField(_labels, form, 7, 0, "Contract.PaymentTerms", _paymentTerms);
        AddFormField(_labels, form, 8, 0, "Contract.Guarantee", _guaranteeText);
        AddFormField(_labels, form, 9, 0, "Contract.GuaranteeAmount", _guaranteeAmount);
        AddFormField(_labels, form, 10, 0, "Contract.Notes", _notes);
        AddFormField(_labels, form, 11, 0, "Contract.Lines", _lines);
        _currency.Text = "SAR";
        _lines.TextWrapping = TextWrapping.Wrap;
        _lines.AcceptsReturn = true;
        _lines.MinHeight = 64;
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 0);
        lower.Children.Add(formScroll);

        var right = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        _fulfillmentBar.TextWrapping = TextWrapping.Wrap;
        _fulfillmentBar.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_fulfillmentBar, Dock.Top);
        right.Children.Add(_fulfillmentBar);
        ConfigureGrid(_lineGrid);
        _lineGrid.Columns.Add(TextCol("Contract.Col.Material", nameof(SalesContractLine.MaterialId), 80));
        _lineGrid.Columns.Add(TextCol("Contract.Col.Contracted", nameof(SalesContractLine.ContractedQty), 90));
        _lineGrid.Columns.Add(TextCol("Contract.Col.Price", nameof(SalesContractLine.UnitPrice), 80));
        _lineGrid.Columns.Add(TextCol("Contract.Col.Delivered", nameof(SalesContractLine.DeliveredQty), 90));
        _lineGrid.Columns.Add(TextCol("Contract.Col.Invoiced", nameof(SalesContractLine.InvoicedQty), 90));
        _lineGrid.Columns.Add(TextCol("Contract.Col.Remaining", nameof(SalesContractLine.RemainingQty), 90));
        _lineGrid.Columns.Add(TextCol("Contract.Col.NextDelivery", nameof(SalesContractLine.NextDeliveryDate), 100));
        right.Children.Add(_lineGrid);
        Grid.SetColumn(right, 1);
        lower.Children.Add(right);

        Grid.SetRow(lower, 1);
        content.Children.Add(lower);
        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Contract.Title");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[]
        {
            _newButton, _saveButton, _deleteButton, _activateButton, _suspendButton,
            _renewButton, _generateButton, _expireButton, _expiringButton, _refreshButton, _closeButton
        })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    // ------------------------------------------------------------------
    // Data + actions
    // ------------------------------------------------------------------
    private async Task RefreshAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Contract.ServiceUnavailable"); return; }
            var rows = await _service.GetContractsAsync(CurrentCompanyCode);
            _contracts.Clear();
            _contracts.AddRange(rows);
            _contractGrid.ItemsSource = null;
            _contractGrid.ItemsSource = _contracts;
            _statusBar.Text = Tf("Contract.StatusCount", _contracts.Count);
        }
        catch (Exception ex) { Report(ex, "ContractWin.RefreshAsync"); }
    }

    private async Task ShowExpiringAsync()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Contract.ServiceUnavailable"); return; }
            var rows = await _service.GetExpiringAsync(CurrentCompanyCode, 30);
            _contracts.Clear();
            _contracts.AddRange(rows);
            _contractGrid.ItemsSource = null;
            _contractGrid.ItemsSource = _contracts;
            _statusBar.Text = Tf("Contract.ExpiringStatus", _contracts.Count);
        }
        catch (Exception ex) { Report(ex, "ContractWin.ShowExpiringAsync"); }
    }

    private async Task OnSelectedAsync()
    {
        if (_contractGrid.SelectedItem is not SalesContract contract) return;
        _selectedId = contract.Id;
        _selectedStatus = contract.Status;
        _selectedNo = contract.ContractNo;
        _partyId.Text = contract.PartyId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _partyCode.Text = contract.PartyCode ?? string.Empty;
        _partyName.Text = contract.PartyName ?? string.Empty;
        _title.Text = contract.Title;
        _startDate.SelectedDate = ParseDate(contract.StartDate);
        _endDate.SelectedDate = ParseDate(contract.EndDate);
        _currency.Text = contract.CurrencyCode;
        _paymentTerms.Text = contract.PaymentTerms ?? string.Empty;
        _guaranteeText.Text = contract.GuaranteeText ?? string.Empty;
        _guaranteeAmount.Text = contract.GuaranteeAmount.ToString(CultureInfo.InvariantCulture);
        _notes.Text = contract.Notes ?? string.Empty;
        _lines.Text = string.Join(Environment.NewLine, contract.Lines.OrderBy(l => l.LineNo).Select(FormatLine));
        _lineGrid.ItemsSource = null;
        _lineGrid.ItemsSource = contract.Lines;
        try
        {
            if (_service is null) return;
            var f = await _service.GetFulfillmentAsync(CurrentCompanyCode, contract.Id);
            if (f is not null)
            {
                _fulfillmentBar.Text = Tf("Contract.Fulfillment",
                    f.ContractedQty, f.DeliveredQty, f.InvoicedQty, f.RemainingQty,
                    f.Revenue, f.Cogs, f.Margin, f.MarginPct);
                var refreshed = _contracts.FirstOrDefault(c => c.Id == contract.Id);
                if (refreshed is not null)
                {
                    var fresh = await _service.GetContractAsync(CurrentCompanyCode, contract.Id);
                    if (fresh is not null)
                    {
                        _lineGrid.ItemsSource = null;
                        _lineGrid.ItemsSource = fresh.Lines;
                    }
                }
            }
        }
        catch (Exception ex) { Report(ex, "ContractWin.OnSelectedAsync"); }
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _selectedStatus = ContractStatuses.Draft;
        _selectedNo = string.Empty;
        _contractGrid.SelectedItem = null;
        _partyId.Text = string.Empty;
        _partyCode.Text = string.Empty;
        _partyName.Text = string.Empty;
        _title.Text = string.Empty;
        _startDate.SelectedDate = null;
        _endDate.SelectedDate = null;
        _currency.Text = "SAR";
        _paymentTerms.Text = string.Empty;
        _guaranteeText.Text = string.Empty;
        _guaranteeAmount.Text = "0";
        _notes.Text = string.Empty;
        _lines.Text = string.Empty;
        _lineGrid.ItemsSource = null;
        _fulfillmentBar.Text = string.Empty;
    }

    private async Task SaveAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Contract.ServiceUnavailable"));
            RequireManage();
            var existing = _selectedId is Guid id ? _contracts.FirstOrDefault(c => c.Id == id) : null;
            var contract = existing ?? new SalesContract();
            contract.PartyId = ParseLongOrNull(_partyId.Text);
            contract.PartyCode = NullIfBlank(_partyCode.Text);
            contract.PartyName = NullIfBlank(_partyName.Text);
            contract.Title = _title.Text.Trim();
            contract.StartDate = DateText(_startDate) ?? string.Empty;
            contract.EndDate = DateText(_endDate) ?? string.Empty;
            contract.CurrencyCode = string.IsNullOrWhiteSpace(_currency.Text) ? "SAR" : _currency.Text.Trim();
            contract.PaymentTerms = NullIfBlank(_paymentTerms.Text);
            contract.GuaranteeText = NullIfBlank(_guaranteeText.Text);
            contract.GuaranteeAmount = ParseDecimal(_guaranteeAmount.Text);
            contract.Notes = NullIfBlank(_notes.Text);
            contract.Lines = ParseLines(contract.Id);
            await _service.SaveContractAsync(CurrentCompanyCode, contract, CurrentUserId);
            _selectedId = contract.Id;
            _statusBar.Text = Tf("Contract.Saved", contract.ContractNo);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "ContractWin.SaveAsync"); }
    }

    private async Task DeleteAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Contract.Select"));
            RequireManage();
            await _service.DeleteContractAsync(CurrentCompanyCode, id, CurrentUserId);
            ClearEditor();
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "ContractWin.DeleteAsync"); }
    }

    private async Task SetStatusAsync(string toStatus)
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Contract.Select"));
            RequireManage();
            await _service.SetStatusAsync(CurrentCompanyCode, id, toStatus, CurrentUserId);
            _statusBar.Text = Tf("Contract.StatusChanged", _selectedNo, toStatus);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "ContractWin.SetStatusAsync"); }
    }

    private async Task RenewAsync()
    {
        try
        {
            if (_service is null || _selectedId is not Guid id)
                throw new InvalidOperationException(T("Contract.Select"));
            RequireManage();
            var successor = await _service.RenewAsync(CurrentCompanyCode, id, CurrentUserId);
            _statusBar.Text = Tf("Contract.Renewed", _selectedNo, successor.ContractNo);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "ContractWin.RenewAsync"); }
    }

    private async Task GenerateAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Contract.ServiceUnavailable"));
            RequireManage();
            var created = await _service.GenerateDueDeliveriesAsync(CurrentCompanyCode, CurrentUserId);
            _statusBar.Text = Tf("Contract.Generated", created);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "ContractWin.GenerateAsync"); }
    }

    private async Task ExpireSweepAsync()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Contract.ServiceUnavailable"));
            RequireManage();
            var expired = await _service.ExpireOverdueAsync(CurrentCompanyCode, CurrentUserId);
            _statusBar.Text = Tf("Contract.Expired", expired);
            await RefreshAsync();
        }
        catch (Exception ex) { Report(ex, "ContractWin.ExpireSweepAsync"); }
    }

    private List<SalesContractLine> ParseLines(Guid contractId)
    {
        var lines = new List<SalesContractLine>();
        foreach (var raw in _lines.Text.Split(NewLineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var materialId))
                throw new InvalidOperationException(Tf("Contract.LineInvalid", raw));
            var line = new SalesContractLine
            {
                ContractId = contractId,
                MaterialId = materialId,
                ContractedQty = ParseDecimal(parts[1]),
                UnitPrice = ParseDecimal(parts[2]),
                NextDeliveryDate = parts.Length > 3 && parts[3] != "-" ? parts[3] : null,
                IntervalDays = parts.Length > 4 && parts[4] != "-" ? (int)ParseDecimal(parts[4]) : null,
                ScheduledQty = parts.Length > 5 ? ParseDecimal(parts[5]) : 0m
            };
            lines.Add(line);
        }
        return lines;
    }

    private static string FormatLine(SalesContractLine line)
        => string.Join(", ", new[]
        {
            line.MaterialId.ToString(CultureInfo.InvariantCulture),
            line.ContractedQty.ToString(CultureInfo.InvariantCulture),
            line.UnitPrice.ToString(CultureInfo.InvariantCulture),
            line.NextDeliveryDate ?? "-",
            line.IntervalDays?.ToString(CultureInfo.InvariantCulture) ?? "-",
            line.ScheduledQty.ToString(CultureInfo.InvariantCulture)
        });

    // ------------------------------------------------------------------
    // Helpers (CommWin pattern)
    // ------------------------------------------------------------------
    private static void RequireManage()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.ContractManage))
            throw new InvalidOperationException(T("Contract.PermissionDenied"));
    }

    private void Report(Exception ex, string context)
    {
        AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, context);
        MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static Border BottomBar(params Button[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        FlowAlign.End(bar);
        foreach (var button in buttons) bar.Children.Add(button);
        var border = new Border
        {
            Child = bar,
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
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

    private static string? DateText(DatePicker picker)
        => picker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static decimal ParseDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

    private static long? ParseLongOrNull(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
