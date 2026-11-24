using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Services;
using ShouTech.App.Views.Reports;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Customer/supplier card (ribbon chunk B): fully persisted to the parties
/// table through <see cref="ShouTech.Application.Services.MasterCardService"/>.
/// The parameterless constructor defaults to Customer (the existing RptNav
/// routing for both cards); <see cref="CstCard(bool)"/> or
/// <see cref="PartyKind"/> selects Supplier. Group lookup comes from
/// party_groups, the balance from party_ledger, and Statement opens the
/// account-statement report window.
/// </summary>
public sealed class CstCard : BaseWin
{
    protected override string? ScheduleTintName => "Rose";

    // Field sizes are NOT set here: height comes from the central styles
    // (ControlHeight 32) and width from the form-grid column the field sits
    // in (see BuildUi / docs/WINDOW_PROPORTIONS.md).
    private readonly TextBox _txtCode = new();
    private readonly TextBox _txtNameAr = new();
    private readonly TextBox _txtNameEn = new();
    private readonly ComboBox _cbType = new();
    private readonly ComboBox _cbGroup = new();
    private readonly TextBox _txtTaxNo = new();
    private readonly TextBox _txtCrNo = new();

    private readonly TextBox _txtCreditLimit = new() { Text = "0.00" };
    private readonly TextBox _txtCreditDays = new() { Text = "30" };
    private readonly ComboBox _cbPriceTier = new();
    private readonly TextBox _txtGlAcc = new();

    private readonly TextBox _txtPhone = new();
    private readonly TextBox _txtMobile = new();
    private readonly TextBox _txtEmail = new();
    private readonly TextBox _txtAddress = new();
    private readonly TextBox _txtNotes = new();
    private readonly CheckBox _chkActive = new() { VerticalAlignment = VerticalAlignment.Center, IsChecked = true };

    // W1 (migration 095): header additions + party structure tabs.
    private readonly TextBox _txtCurrency = new();
    private readonly ComboBox _cbRisk = new();
    private readonly CheckBox _chkBlacklisted = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _txtBlacklistReason = new();
    private readonly TextBlock _lblOpening = new();
    private string? _loadedBlacklistedAt;
    private string? _loadedBlacklistedBy;

    private readonly ObservableCollection<PartyContact> _contacts = new();
    private readonly ObservableCollection<PartyAddress> _addresses = new();
    private readonly ObservableCollection<PartyBranch> _branches = new();
    private readonly DataGrid _gridContacts = new();
    private readonly DataGrid _gridAddresses = new();
    private readonly DataGrid _gridBranches = new();
    private readonly Button _btnAddContact = new();
    private readonly Button _btnDelContact = new();
    private readonly Button _btnAddAddress = new();
    private readonly Button _btnDelAddress = new();
    private readonly Button _btnAddBranch = new();
    private readonly Button _btnDelBranch = new();
    private readonly Button _btnOpening = new();

    private readonly TextBlock _lblCurBal = new();
    private readonly TextBlock _lblTotDr = new();
    private readonly TextBlock _lblTotCr = new();

    private readonly Button _btnLoad = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnStmt = new();
    /// <summary>W23: party attachments on the generalized attachment store
    /// (migration 098, PARTY owner) — same window/service as documents.</summary>
    private readonly Button _btnAttachments = new();

    private readonly List<PartyGroup> _groups = new();
    private Guid? _loadedId;
    private string _partyKind = PartyKinds.Customer;
    private bool _applyingKind;

    /// <summary>Party kind of this card: "C" customer (default) or "S" supplier.</summary>
    public string PartyKind
    {
        get => _partyKind;
        set
        {
            _partyKind = value == PartyKinds.Supplier ? PartyKinds.Supplier : PartyKinds.Customer;
            SyncTypeCombo();
        }
    }

    public CstCard() : this(false)
    {
    }

    public CstCard(bool isSupplier)
    {
        _partyKind = isSupplier ? PartyKinds.Supplier : PartyKinds.Customer;
        Width = 740;
        Height = 505;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Customers.View))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshGroupsAsync();
    }

    /// <summary>Loads a party by kind + code into the card (used by PartyManagerWin).</summary>
    public async Task LoadParty(string kind, string code)
    {
        try
        {
            var service = AppHost.MasterCards;
            if (service is null)
            {
                MessageBox.Show(this, T("MasterCards.ServiceUnavailable"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            PartyKind = kind;
            var party = await service.GetPartyByCodeAsync(CurrentCompanyCode, PartyKind, code);
            if (party is null)
            {
                MessageBox.Show(this, T("MasterCards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await RefreshGroupsAsync();
            ShowParty(party);
            await RefreshBalanceAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var toolbar = new WrapPanel();
        FlowAlign.End(toolbar);
        StyleButton(_btnLoad, "MasterCards.Load");
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");
        StyleButton(_btnStmt, "CustCard.Statement");
        StyleButton(_btnOpening, "CustCard.OpeningBalance");
        StyleButton(_btnAttachments, "PurchaseAttachments.Open");

        _btnLoad.Click += async (_, _) => await LoadParty(PartyKind, _txtCode.Text.Trim());
        _btnNew.Click += (_, _) => ResetCard();
        _btnSave.Click += async (_, _) => await SaveAsync();
        _btnDelete.Click += async (_, _) => await DeleteAsync();
        _btnStmt.Click += (_, _) => OpenStatement();
        _btnOpening.Click += async (_, _) => await PostOpeningBalanceAsync();
        _btnAttachments.Click += async (_, _) => await OpenAttachmentsAsync();

        foreach (var b in new[] { _btnLoad, _btnNew, _btnSave, _btnDelete, _btnStmt, _btnOpening, _btnAttachments })
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

        var metricsBorder = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        metricsBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        metricsBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        _lblTotDr.Margin = new Thickness(24, 0, 0, 0);
        _lblTotCr.Margin = new Thickness(24, 0, 0, 0);
        var metricsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        metricsPanel.Children.Add(_lblCurBal);
        metricsPanel.Children.Add(_lblTotDr);
        metricsPanel.Children.Add(_lblTotCr);
        metricsBorder.Child = metricsPanel;

        DockPanel.SetDock(metricsBorder, Dock.Bottom);
        root.Children.Add(metricsBorder);
        root.Children.Add(actionBar);

        var tabs = new TabControl();
        tabs.SetResourceReference(StyleProperty, "VisuTabControlStyle");

        // All three tabs share ONE span scheme (3 label+field pairs), so field
        // edges align across every group of the window (Phenix rule).
        var tabGen = new TabItem { Header = new TextBlock { Text = T("Ser.Tab.General"), Tag = "Ser.Tab.General" } };
        var formGen = CreateFormGrid(3);
        formGen.Margin = new Thickness(16);
        AddFormField(formGen, 0, 0, "CustCard.Code", _txtCode);
        AddFormField(formGen, 0, 1, "CustCard.Type", _cbType);
        AddFormField(formGen, 0, 2, "CustCard.Group", _cbGroup);
        AddFormField(formGen, 1, 0, "CustCard.NameAr", _txtNameAr);
        AddFormField(formGen, 1, 1, "CustCard.NameEn", _txtNameEn, spanPairs: 2);
        AddFormField(formGen, 2, 0, "CustCard.TaxNumber", _txtTaxNo);
        AddFormField(formGen, 2, 1, "CustCard.CommercialReg", _txtCrNo);
        _chkActive.Content = T("MasterCards.Active");
        _chkActive.Tag = "MasterCards.Active";
        AddFormElement(formGen, 2, 2, _chkActive);
        tabGen.Content = formGen;
        tabs.Items.Add(tabGen);

        var tabFin = new TabItem { Header = new TextBlock { Text = T("Whs.TabAccounts"), Tag = "Whs.TabAccounts" } };
        var formFin = CreateFormGrid(3);
        formFin.Margin = new Thickness(16);
        AddFormField(formFin, 0, 0, "CustCard.CreditLimit", _txtCreditLimit);
        AddFormField(formFin, 0, 1, "CustCard.CreditDays", _txtCreditDays);
        AddFormField(formFin, 0, 2, "CustCard.PriceTier", _cbPriceTier);
        AddFormField(formFin, 1, 0, "CustCard.AccountLink", _txtGlAcc);
        AddFormField(formFin, 1, 1, "CustCard.DefaultCurrency", _txtCurrency);
        AddFormField(formFin, 1, 2, "CustCard.RiskClass", _cbRisk);
        _chkBlacklisted.Content = T("CustCard.Blacklisted");
        _chkBlacklisted.Tag = "CustCard.Blacklisted";
        AddFormElement(formFin, 2, 0, _chkBlacklisted);
        AddFormField(formFin, 2, 1, "CustCard.BlacklistReason", _txtBlacklistReason, spanPairs: 2);
        AddFormElement(formFin, 3, 0, _lblOpening);
        tabFin.Content = formFin;
        tabs.Items.Add(tabFin);

        var tabCont = new TabItem { Header = new TextBlock { Text = T("CustCard.Address"), Tag = "CustCard.Address" } };
        var formCont = CreateFormGrid(3);
        formCont.Margin = new Thickness(16);
        AddFormField(formCont, 0, 0, "CustCard.Phone", _txtPhone);
        AddFormField(formCont, 0, 1, "CustCard.Mobile", _txtMobile);
        AddFormField(formCont, 1, 0, "CustCard.Email", _txtEmail, spanPairs: 2);
        AddFormField(formCont, 2, 0, "CustCard.Address", _txtAddress, spanPairs: 3);
        AddFormField(formCont, 3, 0, "MasterCards.Notes", _txtNotes, spanPairs: 3);
        tabCont.Content = formCont;
        tabs.Items.Add(tabCont);

        // W1: party structure tabs (migration 095). Rows edit in the
        // grid and persist with the card's Save (bottom action bar);
        // each tab carries its own Add/Delete buttons at its bottom.
        tabs.Items.Add(BuildStructureTab("CustCard.TabContacts", _gridContacts, _contacts, _btnAddContact, _btnDelContact, grid =>
        {
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.ContactName"), Binding = new System.Windows.Data.Binding(nameof(PartyContact.Name)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 150 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.ContactRole"), Binding = new System.Windows.Data.Binding(nameof(PartyContact.Role)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.Phone"), Binding = new System.Windows.Data.Binding(nameof(PartyContact.Phone)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.Mobile"), Binding = new System.Windows.Data.Binding(nameof(PartyContact.Mobile)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.Email"), Binding = new System.Windows.Data.Binding(nameof(PartyContact.Email)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 170 });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = T("CustCard.Primary"), Binding = new System.Windows.Data.Binding(nameof(PartyContact.IsPrimary)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 70 });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = T("MasterCards.Active"), Binding = new System.Windows.Data.Binding(nameof(PartyContact.IsActive)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 70 });
        }));
        _btnAddContact.Click += (_, _) => _contacts.Add(new PartyContact { PartyId = _loadedId ?? Guid.Empty, IsPrimary = _contacts.Count == 0 });
        _btnDelContact.Click += (_, _) => { if (_gridContacts.SelectedItem is PartyContact c) _contacts.Remove(c); };

        tabs.Items.Add(BuildStructureTab("CustCard.TabAddresses", _gridAddresses, _addresses, _btnAddAddress, _btnDelAddress, grid =>
        {
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.AddressKind"), Binding = new System.Windows.Data.Binding(nameof(PartyAddress.Kind)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 90 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.City"), Binding = new System.Windows.Data.Binding(nameof(PartyAddress.City)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.Country"), Binding = new System.Windows.Data.Binding(nameof(PartyAddress.Country)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.PostalCode"), Binding = new System.Windows.Data.Binding(nameof(PartyAddress.PostalCode)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 90 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.Address"), Binding = new System.Windows.Data.Binding(nameof(PartyAddress.Address)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 240 });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = T("CustCard.Default"), Binding = new System.Windows.Data.Binding(nameof(PartyAddress.IsDefault)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 70 });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = T("MasterCards.Active"), Binding = new System.Windows.Data.Binding(nameof(PartyAddress.IsActive)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 70 });
        }));
        _btnAddAddress.Click += (_, _) => _addresses.Add(new PartyAddress { PartyId = _loadedId ?? Guid.Empty, IsDefault = _addresses.Count == 0 });
        _btnDelAddress.Click += (_, _) => { if (_gridAddresses.SelectedItem is PartyAddress a) _addresses.Remove(a); };

        tabs.Items.Add(BuildStructureTab("CustCard.TabBranches", _gridBranches, _branches, _btnAddBranch, _btnDelBranch, grid =>
        {
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.BranchName"), Binding = new System.Windows.Data.Binding(nameof(PartyBranch.Name)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 170 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.Address"), Binding = new System.Windows.Data.Binding(nameof(PartyBranch.Address)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 260 });
            grid.Columns.Add(new DataGridTextColumn { Header = T("CustCard.Phone"), Binding = new System.Windows.Data.Binding(nameof(PartyBranch.Phone)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 120 });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = T("MasterCards.Active"), Binding = new System.Windows.Data.Binding(nameof(PartyBranch.IsActive)) { Mode = System.Windows.Data.BindingMode.TwoWay }, Width = 70 });
        }));
        _btnAddBranch.Click += (_, _) => _branches.Add(new PartyBranch { PartyId = _loadedId ?? Guid.Empty });
        _btnDelBranch.Click += (_, _) => { if (_gridBranches.SelectedItem is PartyBranch b) _branches.Remove(b); };

        root.Children.Add(tabs);
        Content = root;

        _cbType.SelectionChanged += async (_, _) =>
        {
            if (_applyingKind)
                return;
            _partyKind = _cbType.SelectedIndex == 1 ? PartyKinds.Supplier : PartyKinds.Customer;
            _loadedId = null;
            await RefreshGroupsAsync();
        };
        _cbPriceTier.ItemsSource = new[] { "Retail", "Wholesale", "Distributor" };
        _cbPriceTier.SelectedIndex = 0;
        _cbRisk.ItemsSource = new[] { string.Empty, PartyRiskClasses.Low, PartyRiskClasses.Medium, PartyRiskClasses.High };
        _cbRisk.SelectedIndex = 0;
    }

    private static TabItem BuildStructureTab(string headerKey, DataGrid grid, System.Collections.IEnumerable items, Button addButton, Button deleteButton, Action<DataGrid> buildColumns)
    {
        var tab = new TabItem { Header = new TextBlock { Text = T(headerKey), Tag = headerKey } };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        FlowAlign.End(bar);
        StyleButton(addButton, "Common.New");
        StyleButton(deleteButton, "Common.Delete");
        bar.Children.Add(addButton);
        bar.Children.Add(deleteButton);
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);
        grid.AutoGenerateColumns = false;
        grid.CanUserAddRows = false;
        grid.ItemsSource = items;
        grid.SetResourceReference(StyleProperty, "VisuDataGridStyle");
        buildColumns(grid);
        panel.Children.Add(grid);
        tab.Content = panel;
        return tab;
    }

    private void SyncTypeCombo()
    {
        _applyingKind = true;
        try { _cbType.SelectedIndex = _partyKind == PartyKinds.Supplier ? 1 : 0; }
        finally { _applyingKind = false; }
    }

    private async Task RefreshGroupsAsync()
    {
        var service = AppHost.MasterCards;
        if (service is null)
            return;
        var groups = await service.GetPartyGroupsAsync(CurrentCompanyCode, PartyKind);
        _groups.Clear();
        _groups.AddRange(groups);
        var selected = (_cbGroup.SelectedItem as PartyGroup)?.Id;
        _cbGroup.ItemsSource = null;
        _cbGroup.ItemsSource = _groups;
        _cbGroup.DisplayMemberPath = nameof(PartyGroup.Code);
        if (selected.HasValue)
        {
            foreach (var group in _groups)
                if (group.Id == selected.Value)
                    _cbGroup.SelectedItem = group;
        }
    }

    private void ShowParty(Party party)
    {
        _loadedId = party.Id;
        _partyKind = party.Kind;
        SyncTypeCombo();
        _txtCode.Text = party.Code;
        _txtNameEn.Text = party.NameEn;
        _txtNameAr.Text = party.NameAr ?? string.Empty;
        _txtTaxNo.Text = party.TaxNo ?? string.Empty;
        _txtCrNo.Text = party.CommercialRegNo ?? string.Empty;
        _txtCreditLimit.Text = party.CreditLimit.ToString("0.##", CultureInfo.InvariantCulture);
        _txtCreditDays.Text = party.CreditDays.ToString(CultureInfo.InvariantCulture);
        _cbPriceTier.SelectedItem = string.IsNullOrWhiteSpace(party.PriceTier) ? "Retail" : party.PriceTier;
        _txtGlAcc.Text = party.GlAccountCode ?? string.Empty;
        _txtPhone.Text = party.Phone ?? string.Empty;
        _txtMobile.Text = party.Mobile ?? string.Empty;
        _txtEmail.Text = party.Email ?? string.Empty;
        _txtAddress.Text = party.Address ?? string.Empty;
        _txtNotes.Text = party.Notes ?? string.Empty;
        _chkActive.IsChecked = party.IsActive;
        _txtCurrency.Text = party.DefaultCurrency ?? string.Empty;
        _cbRisk.SelectedItem = party.RiskClass ?? string.Empty;
        _chkBlacklisted.IsChecked = party.IsBlacklisted;
        _txtBlacklistReason.Text = party.BlacklistReason ?? string.Empty;
        _loadedBlacklistedAt = party.BlacklistedAt;
        _loadedBlacklistedBy = party.BlacklistedBy;
        _lblOpening.Text = party.OpeningBalanceAmount.HasValue
            ? $"{T("CustCard.OpeningBalance")}: {party.OpeningBalanceAmount.Value.ToString("N2", CultureInfo.CurrentCulture)} ({party.OpeningBalanceDate})"
            : string.Empty;
        _ = LoadChildrenAsync(party.Id);
        _cbGroup.SelectedItem = null;
        if (party.PartyGroupId.HasValue)
        {
            foreach (var group in _groups)
                if (group.Id == party.PartyGroupId.Value)
                    _cbGroup.SelectedItem = group;
        }
    }

    private async Task LoadChildrenAsync(Guid partyId)
    {
        var service = AppHost.MasterCards;
        if (service is null)
            return;
        try
        {
            _contacts.Clear();
            foreach (var c in await service.GetPartyContactsAsync(CurrentCompanyCode, partyId))
                _contacts.Add(c);
            _addresses.Clear();
            foreach (var a in await service.GetPartyAddressesAsync(CurrentCompanyCode, partyId))
                _addresses.Add(a);
            _branches.Clear();
            foreach (var b in await service.GetPartyBranchesAsync(CurrentCompanyCode, partyId))
                _branches.Add(b);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ShouTech.Domain.ErrorCodes.NonFatalInternalFailure, ex, "CstCard.LoadChildren");
        }
    }

    private async Task SaveChildrenAsync(ShouTech.Application.Services.MasterCardService service, Guid partyId)
    {
        // Persist grid rows; rows removed from a grid are soft-deleted by
        // diffing against the stored set (full persistence, no orphans).
        var storedContacts = await service.GetPartyContactsAsync(CurrentCompanyCode, partyId);
        foreach (var stored in storedContacts)
            if (_contacts.All(c => c.Id != stored.Id))
                await service.DeletePartyContactAsync(CurrentCompanyCode, stored.Id, CurrentUserId);
        foreach (var contact in _contacts)
        {
            contact.PartyId = partyId;
            if (string.IsNullOrWhiteSpace(contact.Name))
                continue;
            await service.SavePartyContactAsync(CurrentCompanyCode, contact, CurrentUserId);
        }

        var storedAddresses = await service.GetPartyAddressesAsync(CurrentCompanyCode, partyId);
        foreach (var stored in storedAddresses)
            if (_addresses.All(a => a.Id != stored.Id))
                await service.DeletePartyAddressAsync(CurrentCompanyCode, stored.Id, CurrentUserId);
        foreach (var address in _addresses)
        {
            address.PartyId = partyId;
            if (!PartyAddressKinds.IsValid(address.Kind))
                address.Kind = PartyAddressKinds.Delivery;
            await service.SavePartyAddressAsync(CurrentCompanyCode, address, CurrentUserId);
        }

        var storedBranches = await service.GetPartyBranchesAsync(CurrentCompanyCode, partyId);
        foreach (var stored in storedBranches)
            if (_branches.All(b => b.Id != stored.Id))
                await service.DeletePartyBranchAsync(CurrentCompanyCode, stored.Id, CurrentUserId);
        foreach (var branch in _branches)
        {
            branch.PartyId = partyId;
            if (string.IsNullOrWhiteSpace(branch.Name))
                continue;
            await service.SavePartyBranchAsync(CurrentCompanyCode, branch, CurrentUserId);
        }

        await LoadChildrenAsync(partyId);
        // Primary contact / default address mirror onto the card summary.
        var refreshed = await service.GetPartyByCodeAsync(CurrentCompanyCode, PartyKind, _txtCode.Text.Trim());
        if (refreshed is not null)
        {
            _txtPhone.Text = refreshed.Phone ?? string.Empty;
            _txtMobile.Text = refreshed.Mobile ?? string.Empty;
            _txtEmail.Text = refreshed.Email ?? string.Empty;
            _txtAddress.Text = refreshed.Address ?? string.Empty;
        }
    }

    private async Task RefreshBalanceAsync()
    {
        var service = AppHost.MasterCards;
        if (service is null || string.IsNullOrWhiteSpace(_txtCode.Text))
        {
            UpdateBalances(0, 0);
            return;
        }
        var balance = await service.GetPartyBalanceAsync(CurrentCompanyCode, PartyKind, _txtCode.Text.Trim());
        UpdateBalances(balance.Debit, balance.Credit);
    }

    private async Task SaveAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            var permission = _loadedId.HasValue ? PermCat.Customers.Update : PermCat.Customers.Create;
            if (!AppHost.Permissions.Has(permission))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            var party = new Party
            {
                Id = _loadedId ?? Guid.NewGuid(),
                Kind = PartyKind,
                Code = _txtCode.Text.Trim(),
                NameEn = _txtNameEn.Text.Trim(),
                NameAr = NullIfBlank(_txtNameAr.Text),
                PartyGroupId = (_cbGroup.SelectedItem as PartyGroup)?.Id,
                TaxNo = NullIfBlank(_txtTaxNo.Text),
                CommercialRegNo = NullIfBlank(_txtCrNo.Text),
                CreditLimit = ParseDecimal(_txtCreditLimit.Text),
                CreditDays = ParseInt(_txtCreditDays.Text),
                PriceTier = _cbPriceTier.SelectedItem as string ?? "Retail",
                GlAccountCode = NullIfBlank(_txtGlAcc.Text),
                Phone = NullIfBlank(_txtPhone.Text),
                Mobile = NullIfBlank(_txtMobile.Text),
                Email = NullIfBlank(_txtEmail.Text),
                Address = NullIfBlank(_txtAddress.Text),
                IsActive = _chkActive.IsChecked == true,
                Notes = NullIfBlank(_txtNotes.Text),
                DefaultCurrency = NullIfBlank(_txtCurrency.Text)?.ToUpperInvariant(),
                RiskClass = NullIfBlank(_cbRisk.SelectedItem as string),
                IsBlacklisted = _chkBlacklisted.IsChecked == true,
                BlacklistReason = NullIfBlank(_txtBlacklistReason.Text),
                BlacklistedAt = _chkBlacklisted.IsChecked == true
                    ? _loadedBlacklistedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : null,
                BlacklistedBy = _chkBlacklisted.IsChecked == true
                    ? _loadedBlacklistedBy ?? CurrentUserId
                    : null
            };
            await service.SavePartyAsync(CurrentCompanyCode, party, CurrentUserId);
            _loadedId = party.Id;
            if (_chkBlacklisted.IsChecked == true)
            {
                _loadedBlacklistedAt = party.BlacklistedAt;
                _loadedBlacklistedBy = party.BlacklistedBy;
            }
            else
            {
                _loadedBlacklistedAt = null;
                _loadedBlacklistedBy = null;
            }
            await SaveChildrenAsync(service, party.Id);
            MessageBox.Show(this, T("CustCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshBalanceAsync();
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
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (!_loadedId.HasValue)
                return;
            if (!AppHost.Permissions.Has(PermCat.Customers.Delete))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await service.DeletePartyAsync(CurrentCompanyCode, _loadedId.Value, PartyKind, _txtCode.Text.Trim(), CurrentUserId);
            MessageBox.Show(this, T("CustCard.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            ResetCard();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>W23: opens this party's attachments (PARTY owner) in the
    /// shared attachment window; the party must be saved/loaded first so
    /// the owner reference is a real party id.</summary>
    private async Task OpenAttachmentsAsync()
    {
        if (_loadedId is not Guid partyId)
        {
            MessageBox.Show(this, T("OrderDoc.SaveFirst"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (AppHost.PurchaseAttachments is null || AppHost.JournalStore is null)
        {
            MessageBox.Show(this, T("PurchaseAttachments.MissingService"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var companyId = await AppHost.JournalStore.ResolveCompanyIdAsync(CurrentCompanyCode);
            new PurchaseAttachmentsWin(companyId, AttachmentOwnerKind.Party,
                partyId.ToString("D"), CurrentUserId) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ShouTech.Domain.ErrorCodes.WindowOperationFailed, ex, "CstCard.OpenAttachments");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenStatement()
    {
        // W1: customers open the party-ledger customer statement
        // (CustStmtWin) pre-filtered to this party; suppliers keep the
        // supplier statement. (Previously this opened the GL statement.)
        if (PartyKind == PartyKinds.Supplier)
        {
            var supplierWindow = new SupStatementWin { Owner = this, InitialSearch = NullIfBlank(_txtCode.Text) };
            supplierWindow.Show();
            return;
        }
        var window = new CustStmtWin { Owner = this, InitialSearch = NullIfBlank(_txtCode.Text) };
        window.Show();
    }

    private async Task PostOpeningBalanceAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (!_loadedId.HasValue)
                throw new InvalidOperationException(T("CustCard.SaveFirst"));
            var party = await service.GetPartyByCodeAsync(CurrentCompanyCode, PartyKind, _txtCode.Text.Trim());
            if (party is null)
                throw new InvalidOperationException(T("MasterCards.NotFound"));
            if (party.OpeningBalanceAmount.HasValue)
                throw new InvalidOperationException(T("CustCard.OpeningAlreadyPosted"));

            var amountBox = new TextBox { Text = "0.00", Margin = new Thickness(0, 4, 0, 8) };
            var datePicker = new DatePicker { SelectedDate = DateTime.Today, Margin = new Thickness(0, 4, 0, 8) };
            var ok = new Button { Content = T("Common.Save"), MinWidth = VisuId.ButtonMinWidth, IsDefault = true };
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = T("CustCard.OpeningAmount") });
            panel.Children.Add(amountBox);
            panel.Children.Add(new TextBlock { Text = T("CustCard.OpeningDate") });
            panel.Children.Add(datePicker);
            panel.Children.Add(ok);
            var dialog = new Window
            {
                Title = T("CustCard.OpeningBalance"),
                Content = panel,
                Width = 320,
                Height = 230,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };
            ok.Click += (_, _) => dialog.DialogResult = true;
            if (dialog.ShowDialog() != true)
                return;
            var amount = ParseDecimal(amountBox.Text);
            var date = DateOnly.FromDateTime(datePicker.SelectedDate ?? DateTime.Today);
            await service.PostPartyOpeningBalanceAsync(CurrentCompanyCode, PartyKind, party.Code, amount, date, party.DefaultCurrency, CurrentUserId);
            MessageBox.Show(this, T("CustCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            ShowParty(party);
            var refreshed = await service.GetPartyByCodeAsync(CurrentCompanyCode, PartyKind, party.Code);
            if (refreshed is not null)
                ShowParty(refreshed);
            await RefreshBalanceAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetCard()
    {
        _loadedId = null;
        _txtCode.Text = string.Empty;
        _txtNameAr.Text = string.Empty;
        _txtNameEn.Text = string.Empty;
        _txtTaxNo.Text = string.Empty;
        _txtCrNo.Text = string.Empty;
        _txtCreditLimit.Text = "0.00";
        _txtCreditDays.Text = "30";
        _cbPriceTier.SelectedIndex = 0;
        _txtGlAcc.Text = string.Empty;
        _txtPhone.Text = string.Empty;
        _txtMobile.Text = string.Empty;
        _txtEmail.Text = string.Empty;
        _txtAddress.Text = string.Empty;
        _txtNotes.Text = string.Empty;
        _chkActive.IsChecked = true;
        _cbGroup.SelectedItem = null;
        _txtCurrency.Text = string.Empty;
        _cbRisk.SelectedIndex = 0;
        _chkBlacklisted.IsChecked = false;
        _txtBlacklistReason.Text = string.Empty;
        _loadedBlacklistedAt = null;
        _loadedBlacklistedBy = null;
        _lblOpening.Text = string.Empty;
        _contacts.Clear();
        _addresses.Clear();
        _branches.Clear();
        UpdateBalances(0, 0);
    }

    private void UpdateBalances(decimal debit, decimal credit)
    {
        // Step 4 (sweep C-3): a supplier's balance is what the company
        // OWES (credit - debit); customers show debit - credit. The raw
        // totals below stay as recorded in the ledger.
        var balance = PartyKind == PartyKinds.Supplier ? credit - debit : debit - credit;
        _lblCurBal.Text = $"{T("CustCard.Balance")}: {balance.ToString("N2", CultureInfo.CurrentCulture)}";
        _lblCurBal.FontWeight = FontWeights.Bold;
        _lblTotDr.Text = $"{T("CustCard.TotalDebit")}: {debit.ToString("N2", CultureInfo.CurrentCulture)}";
        _lblTotCr.Text = $"{T("CustCard.TotalCredit")}: {credit.ToString("N2", CultureInfo.CurrentCulture)}";
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CustCard.Title");

        _applyingKind = true;
        try
        {
            _cbType.Items.Clear();
            _cbType.Items.Add(T("CustCard.Customer"));
            _cbType.Items.Add(T("CustCard.Supplier"));
            _cbType.SelectedIndex = _partyKind == PartyKinds.Supplier ? 1 : 0;
        }
        finally { _applyingKind = false; }

        _chkActive.Content = T("MasterCards.Active");
        RefreshLocalizedContent(Content as DependencyObject);
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        if (parent is CheckBox check && check.Tag is string cKey)
            check.Content = T(cKey);
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

    private static int ParseInt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ||
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            return v;
        throw new InvalidOperationException("Invalid number format.");
    }
}
