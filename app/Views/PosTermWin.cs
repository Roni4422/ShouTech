using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Views;

/// <summary>
/// POS terminal definition (POS family P1, owner workstream
/// 2026-11-24): the terminal-definition window — a named
/// point-of-sale station
/// bound to its warehouse, cash + card/bank GL accounts, drawer,
/// receipt printer, walk-in default customer, price level and
/// permitted users (migration 119 completed the field set).
/// Replaces the generic definition-editor route for
/// POS.DefinePOS with a dedicated, complete-pillars window:
/// every field persists to pos_terminals through PosTermRepo,
/// sessions resolve the terminal's warehouse from here
/// (PosRepository.GetTerminalWarehouseIdAsync), and the session
/// window enforces the permitted-users list when opening.
/// </summary>
public sealed class PosTermWin : BaseWin
{
    protected override string? ScheduleTintName => "Heather";

    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly DataGrid _grid = new();
    private readonly TextBox _code = new();
    private readonly TextBox _nameEn = new();
    private readonly TextBox _nameAr = new();
    private readonly ComboBox _warehouse = new() { IsEditable = true };
    private readonly ComboBox _cashAccount = new() { IsEditable = true };
    private readonly ComboBox _bankAccount = new() { IsEditable = true };
    private readonly TextBox _drawer = new();
    private readonly TextBox _printer = new();
    private readonly TextBox _customer = new();
    private readonly ComboBox _priceLevel = new() { IsEditable = true };
    private readonly TextBox _users = new();
    private readonly ComboBox _salesKind = new() { IsEditable = true };
    private readonly ComboBox _closingAccount = new() { IsEditable = true };
    private readonly TextBox _currency = new();
    private readonly ComboBox _custGroup = new() { IsEditable = true };
    private readonly ComboBox _saleDesign = new() { IsEditable = true };
    private readonly ComboBox _returnDesign = new() { IsEditable = true };
    private readonly ComboBox _giftDesign = new() { IsEditable = true };
    private readonly CheckBox _active = new() { IsChecked = true };
    private readonly TextBlock _statusBar = new();
    private readonly Button _newButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _deleteButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();

    private List<PosTermRepo.PosTermRec> _terminals = new();
    private PosTermRepo.PosTermRec? _current;

    public PosTermWin()
    {
        Width = 980;
        Height = 620;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += (_, _) => RefreshList();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        root.Children.Add(F2Ui.StatusBar(_statusBar));

        F2Ui.StyleButton(_newButton, "Common.New");
        F2Ui.StyleButton(_saveButton, "Common.Save");
        F2Ui.StyleButton(_deleteButton, "Common.Delete");
        F2Ui.StyleButton(_refreshButton, "Common.Refresh");
        F2Ui.StyleButton(_closeButton, "Common.Close");
        _newButton.Click += (_, _) => NewRecord();
        _saveButton.Click += (_, _) => SaveRecord();
        _deleteButton.Click += (_, _) => DeleteRecord();
        _refreshButton.Click += (_, _) => RefreshList();
        _closeButton.Click += (_, _) => Close();
        root.Children.Add(F2Ui.BottomBar(_newButton, _saveButton, _deleteButton, _refreshButton, _closeButton));

        var content = new DockPanel();

        F2Ui.ConfigureGrid(_grid);
        DataGridTableBehavior.SetShowRowNumbers(_grid, true);
        _grid.IsReadOnly = true;
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosTerm.Code", nameof(PosTermRepo.PosTermRec.Code), 90));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosTerm.Name", nameof(PosTermRepo.PosTermRec.NameEn), 170));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosTerm.Warehouse", nameof(PosTermRepo.PosTermRec.WarehouseCode), 100));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosTerm.CashAccount", nameof(PosTermRepo.PosTermRec.CashAccountCode), 120));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosTerm.BankAccount", nameof(PosTermRepo.PosTermRec.BankAccountCode), 120));
        _grid.Columns.Add(F2Ui.TextCol(_headers, "PosTerm.PriceLevel", nameof(PosTermRepo.PosTermRec.PriceLevel), 100));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is PosTermRepo.PosTermRec rec)
                LoadRecord(rec);
        };
        DockPanel.SetDock(_grid, Dock.Top);
        _grid.Height = 190;
        content.Children.Add(_grid);

        var form = F2Ui.CreateFormGrid(3);
        form.Margin = new Thickness(0, 8, 0, 4);
        F2Ui.AddField(_labels, form, 0, 0, "PosTerm.Code", _code);
        F2Ui.AddField(_labels, form, 0, 1, "PosTerm.Name", _nameEn);
        F2Ui.AddField(_labels, form, 0, 2, "PosTerm.NameAr", _nameAr);
        F2Ui.AddField(_labels, form, 1, 0, "PosTerm.Warehouse", _warehouse);
        F2Ui.AddField(_labels, form, 1, 1, "PosTerm.CashAccount", _cashAccount);
        F2Ui.AddField(_labels, form, 1, 2, "PosTerm.BankAccount", _bankAccount);
        F2Ui.AddField(_labels, form, 2, 0, "PosTerm.Drawer", _drawer);
        F2Ui.AddField(_labels, form, 2, 1, "PosTerm.Printer", _printer);
        F2Ui.AddField(_labels, form, 2, 2, "PosTerm.PriceLevel", _priceLevel);
        // Double-click opens the party search (customers are
        // parties under the canonical model) and fills the code.
        _customer.MouseDoubleClick += (_, _) =>
        {
            var picker = new PartySearchWin { Owner = this };
            if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedPartyCode))
                _customer.Text = picker.SelectedPartyCode;
        };
        F2Ui.AddField(_labels, form, 3, 0, "PosTerm.DefaultCustomer", _customer);
        F2Ui.AddField(_labels, form, 3, 1, "PosTerm.PermittedUsers", _users);
        F2Ui.AddField(_labels, form, 3, 2, "PosTerm.Active", _active);
        // P4 (Phenix-verified bindings): document kinds the point
        // issues, the separate closing account, per-point currency,
        // customer-group scoping, per-point print designs.
        _salesKind.ItemsSource = new[] { "POS", "SAL" };
        F2Ui.AddField(_labels, form, 4, 0, "PosTerm.SalesDocKind", _salesKind);
        F2Ui.AddField(_labels, form, 4, 1, "PosTerm.ClosingAccount", _closingAccount);
        F2Ui.AddField(_labels, form, 4, 2, "PosTerm.Currency", _currency);
        F2Ui.AddField(_labels, form, 5, 0, "PosTerm.CustomerGroup", _custGroup);
        F2Ui.AddField(_labels, form, 5, 1, "PosTerm.SaleDesign", _saleDesign);
        F2Ui.AddField(_labels, form, 5, 2, "PosTerm.ReturnDesign", _returnDesign);
        F2Ui.AddField(_labels, form, 6, 0, "PosTerm.GiftDesign", _giftDesign);
        content.Children.Add(form);

        root.Children.Add(content);
        Content = root;
        ApplyLocalizedText();
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("PosTerm.Title");
        F2Ui.ApplyTexts(_labels, _headers, new[] { _newButton, _saveButton, _deleteButton, _refreshButton, _closeButton }, T);
    }

    private void RefreshList()
    {
        try
        {
            _terminals = PosTermRepo.LoadAll();
            _grid.ItemsSource = null;
            _grid.ItemsSource = _terminals;
            _statusBar.Text = _terminals.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

            _warehouse.ItemsSource = MatRepo.LoadAllWarehouses()
                .Select(w => w.Code.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
            var accounts = AccRepo.LoadAll()
                .OrderBy(a => a.Code, StringComparer.Ordinal)
                .Select(a => a.Code)
                .ToList();
            _cashAccount.ItemsSource = accounts;
            _bankAccount.ItemsSource = accounts;
            _closingAccount.ItemsSource = accounts;
            _priceLevel.ItemsSource = PosTermRepo.LoadPriceLevels();
            var designs = PosTermRepo.LoadDesignNames();
            _saleDesign.ItemsSource = designs;
            _returnDesign.ItemsSource = designs;
            _giftDesign.ItemsSource = designs;
            _custGroup.ItemsSource = PosTermRepo.LoadCustomerGroups();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosTermWin.RefreshList");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewRecord()
    {
        _current = null;
        _grid.SelectedItem = null;
        _code.Clear();
        _nameEn.Clear();
        _nameAr.Clear();
        _warehouse.Text = string.Empty;
        _cashAccount.Text = string.Empty;
        _bankAccount.Text = string.Empty;
        _drawer.Clear();
        _printer.Clear();
        _customer.Clear();
        _priceLevel.Text = string.Empty;
        _users.Clear();
        _salesKind.Text = string.Empty;
        _closingAccount.Text = string.Empty;
        _currency.Clear();
        _custGroup.Text = string.Empty;
        _saleDesign.Text = string.Empty;
        _returnDesign.Text = string.Empty;
        _giftDesign.Text = string.Empty;
        _active.IsChecked = true;
        _code.Focus();
    }

    private void LoadRecord(PosTermRepo.PosTermRec rec)
    {
        _current = rec;
        _code.Text = rec.Code;
        _nameEn.Text = rec.NameEn;
        _nameAr.Text = rec.NameAr;
        _warehouse.Text = rec.WarehouseCode ?? string.Empty;
        _cashAccount.Text = rec.CashAccountCode ?? string.Empty;
        _bankAccount.Text = rec.BankAccountCode ?? string.Empty;
        _drawer.Text = rec.DrawerName ?? string.Empty;
        _printer.Text = rec.PrinterName ?? string.Empty;
        _customer.Text = rec.DefaultCustomerCode ?? string.Empty;
        _priceLevel.Text = rec.PriceLevel ?? string.Empty;
        _users.Text = rec.PermittedUsers ?? string.Empty;
        _salesKind.Text = rec.SalesDocKind ?? string.Empty;
        _closingAccount.Text = rec.ClosingAccountCode ?? string.Empty;
        _currency.Text = rec.CurrencyCode ?? string.Empty;
        _custGroup.Text = rec.CustomerGroupCode ?? string.Empty;
        _saleDesign.Text = rec.SaleDesignName ?? string.Empty;
        _returnDesign.Text = rec.ReturnDesignName ?? string.Empty;
        _giftDesign.Text = rec.GiftDesignName ?? string.Empty;
        _active.IsChecked = rec.IsActive;
    }

    private void SaveRecord()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_code.Text) || string.IsNullOrWhiteSpace(_nameEn.Text))
            {
                MessageBox.Show(this, T("PosTerm.CodeRequired"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var value = new PosTermRepo.PosTermRec(
                _current?.Id ?? string.Empty,
                _code.Text.Trim(),
                _nameEn.Text.Trim(),
                _nameAr.Text.Trim(),
                Blank(_warehouse.Text), Blank(_cashAccount.Text), Blank(_bankAccount.Text),
                Blank(_drawer.Text), Blank(_printer.Text), Blank(_customer.Text),
                Blank(_priceLevel.Text), Blank(_users.Text),
                _active.IsChecked == true,
                Blank(_salesKind.Text), Blank(_closingAccount.Text),
                Blank(_currency.Text), Blank(_custGroup.Text),
                Blank(_saleDesign.Text), Blank(_returnDesign.Text), Blank(_giftDesign.Text));
            var id = PosTermRepo.Save(value, CurrentUserDisplay);
            _statusBar.Text = T("PosTerm.Saved");
            RefreshList();
            _grid.SelectedItem = _terminals.FirstOrDefault(t => t.Id == id);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosTermWin.SaveRecord");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteRecord()
    {
        try
        {
            if (_current is null)
                return;
            if (MessageBox.Show(this, T("PosTerm.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            PosTermRepo.SoftDelete(_current.Id, CurrentUserDisplay);
            NewRecord();
            RefreshList();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, "PosTermWin.DeleteRecord");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? Blank(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
