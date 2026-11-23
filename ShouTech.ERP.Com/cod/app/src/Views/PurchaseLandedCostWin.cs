using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.App.Views;

/// <summary>Creates, posts and reverses supplier landed-cost vouchers.</summary>
public sealed class PurchaseLandedCostWin : BaseWin
{
    private Guid _companyId;
    private readonly TextBox _number = new(), _receipt = new(), _currency = new() { Text = "SAR" },
        _rate = new() { Text = "1" }, _voucherId = new(), _reversalNumber = new(), _reason = new();
    private readonly DatePicker _date = new() { SelectedDate = DateTime.Today };
    private readonly DatePicker _reversalDate = new() { SelectedDate = DateTime.Today };
    private readonly DataGrid _grid = new();
    private readonly ObservableCollection<ChargeRow> _charges = new();
    private readonly TextBlock _status = new();
    private readonly Button _add = new(), _remove = new(), _create = new(), _post = new(), _reverse = new(), _close = new();

    public PurchaseLandedCostWin()
    {
        Title = T("PurchaseLandedCost.Title");
        Width = 1120; Height = 720; MinWidth = 850; MinHeight = 560;
        _grid.ItemsSource = _charges;
        BuildUi();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (AppHost.PurchaseLandedCosts is null || AppHost.Orders is null || AppHost.JournalStore is null)
        {
            MessageBox.Show(this, T("PurchaseLandedCost.ServiceUnavailable"), Title);
            Close();
            return;
        }
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.VIEW")
            && !AppHost.Permissions.Has("PUR.LANDEDCOST.EDIT")
            && !AppHost.Permissions.Has("PUR.LANDEDCOST.POST")
            && !AppHost.Permissions.Has("PUR.LANDEDCOST.REVERSE"))
        {
            MessageBox.Show(this, T("PurchaseLandedCost.PermissionDenied"), Title);
            Close();
            return;
        }
        try
        {
            _companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
            if (_companyId == Guid.Empty) throw new InvalidOperationException("Company context is unavailable.");
            _charges.Add(new ChargeRow { Code = "FREIGHT", Description = "Freight", Amount = "0", Basis = "Value" });
            ApplyPermissions();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 8) };
        StyleButton(_add, "PurchaseLandedCost.AddCharge");
        StyleButton(_remove, "PurchaseLandedCost.RemoveCharge");
        StyleButton(_create, "PurchaseLandedCost.CreateDraft");
        StyleButton(_post, "PurchaseLandedCost.Post");
        StyleButton(_reverse, "PurchaseLandedCost.Reverse");
        StyleButton(_close, "PurchaseLandedCost.Close");
        _add.Click += (_, _) => _charges.Add(new ChargeRow { Code = "OTHER", Description = "", Amount = "0", Basis = "Value" });
        _remove.Click += (_, _) => { if (_grid.SelectedItem is ChargeRow row) _charges.Remove(row); };
        _create.Click += async (_, _) => await CreateDraftAsync();
        _post.Click += async (_, _) => await PostAsync();
        _reverse.Click += async (_, _) => await ReverseAsync();
        _close.Click += (_, _) => Close();
        foreach (var button in new[] { _add, _remove, _create, _post, _reverse, _close }) toolbar.Children.Add(button);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        var status = new Border { Child = _status, Padding = new Thickness(6), BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (var i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 2; i++) header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddField(header, 0, 0, "PurchaseLandedCost.Number", _number);
        AddField(header, 1, 0, "PurchaseLandedCost.Date", _date);
        AddField(header, 2, 0, "PurchaseLandedCost.ReceiptNo", _receipt);
        AddField(header, 3, 0, "PurchaseLandedCost.Currency", _currency);
        AddField(header, 0, 1, "PurchaseLandedCost.ExchangeRate", _rate);
        AddField(header, 1, 1, "PurchaseLandedCost.VoucherId", _voucherId);
        AddField(header, 2, 1, "PurchaseLandedCost.ReversalNo", _reversalNumber);
        AddField(header, 3, 1, "PurchaseLandedCost.ReversalDate", _reversalDate);
        Grid.SetRow(header, 0);
        body.Children.Add(header);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.IsReadOnly = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.ChargeCode"), Binding = new Binding(nameof(ChargeRow.Code)), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.Description"), Binding = new Binding(nameof(ChargeRow.Description)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = T("PurchaseLandedCost.Amount"), Binding = new Binding(nameof(ChargeRow.Amount)), Width = 120 });
        var basisColumn = new DataGridComboBoxColumn
        {
            Header = T("PurchaseLandedCost.Basis"),
            ItemsSource = new[] { "Value", "Quantity", "Weight", "Volume" },
            SelectedItemBinding = new Binding(nameof(ChargeRow.Basis)),
            Width = 130
        };
        _grid.Columns.Add(basisColumn);
        Grid.SetRow(_grid, 1);
        body.Children.Add(_grid);

        var reversal = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        reversal.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        reversal.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        AddField(reversal, 0, 0, "PurchaseLandedCost.Reason", _reason);
        Grid.SetRow(reversal, 2);
        body.Children.Add(reversal);

        root.Children.Add(body);
        Content = root;
        ApplyPermissions();
    }

    private void AddField(Grid grid, int column, int row, string key, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(5) };
        panel.Children.Add(new TextBlock { Text = T(key), Margin = new Thickness(0, 0, 0, 3) });
        control.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(control);
        Grid.SetColumn(panel, column);
        Grid.SetRow(panel, row);
        grid.Children.Add(panel);
    }

    private void ApplyPermissions()
    {
        _create.IsEnabled = AppHost.Permissions.Has("PUR.LANDEDCOST.EDIT");
        _post.IsEnabled = AppHost.Permissions.Has("PUR.LANDEDCOST.POST");
        _reverse.IsEnabled = AppHost.Permissions.Has("PUR.LANDEDCOST.REVERSE");
        _add.IsEnabled = _remove.IsEnabled = _create.IsEnabled;
    }

    private async Task CreateDraftAsync()
    {
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.EDIT")) return;
        try
        {
            if (!decimal.TryParse(_rate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate <= 0m)
                throw new InvalidOperationException(T("PurchaseLandedCost.InvalidInput"));
            var receipt = await AppHost.Orders!.FindByNumberAsync(
                CommercialDocSide.Purchase, _companyId, CommercialDocKinds.PurchaseReceipt, _receipt.Text.Trim());
            if (receipt is null) throw new InvalidOperationException(T("PurchaseLandedCost.NoReceipt"));
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var charges = _charges.Select(row =>
            {
                if (!decimal.TryParse(row.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                    throw new FormatException(T("PurchaseLandedCost.InvalidInput"));
                return new PurchaseLandedCostChargeDraft(row.Code, row.Description, amount, row.Basis);
            }).ToArray();
            var id = await AppHost.PurchaseLandedCosts!.CreateDraftAsync(_companyId, receipt.DocId,
                _number.Text.Trim(), DateOnly.FromDateTime(_date.SelectedDate ?? DateTime.Today),
                string.IsNullOrWhiteSpace(_currency.Text) ? receipt.CurrencyCode : _currency.Text.Trim(),
                rate, charges, CurrentUserId);
            _voucherId.Text = id.ToString("D");
            _status.Text = T("PurchaseLandedCost.DraftCreated") + " " + _number.Text.Trim();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseLandedCostWin.CreateDraftAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PostAsync()
    {
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.POST")) return;
        try
        {
            if (!Guid.TryParse(_voucherId.Text, out var id)) throw new InvalidOperationException(T("PurchaseLandedCost.NoVoucher"));
            var journal = await AppHost.PurchaseLandedCosts!.PostAsync(_companyId, id, CurrentUserId);
            _status.Text = T("PurchaseLandedCost.Posted") + " " + journal.ToString("D");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseLandedCostWin.PostAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ReverseAsync()
    {
        if (!AppHost.Permissions.Has("PUR.LANDEDCOST.REVERSE")) return;
        try
        {
            if (!Guid.TryParse(_voucherId.Text, out var id)) throw new InvalidOperationException(T("PurchaseLandedCost.NoVoucher"));
            var journal = await AppHost.PurchaseLandedCosts!.ReverseAsync(_companyId, id, _reversalNumber.Text.Trim(),
                DateOnly.FromDateTime(_reversalDate.SelectedDate ?? DateTime.Today), _reason.Text, CurrentUserId);
            _status.Text = T("PurchaseLandedCost.Reversed") + " " + journal.ToString("D");
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PurchaseLandedCostWin.ReverseAsync");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed class ChargeRow
    {
        public string Code { get; set; } = "";
        public string Description { get; set; } = "";
        public string Amount { get; set; } = "0";
        public string Basis { get; set; } = "Value";
    }
}
