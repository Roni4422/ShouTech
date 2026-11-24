using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ShouTech.App.Interaction;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Infrastructure;

namespace ShouTech.App.Views;

/// <summary>
/// Warranty window (sales Level 3, migration 105, workstream W10) —
/// one window for the whole after-sales warranty pillar:
///   * Warranties: the register of warranties (one per sold serial,
///     derived from the migration-054 serial records). Actions:
///     "Register warranties" scans a posted invoice / POS sale number
///     and registers every sold serial that lacks a warranty
///     (idempotent; posting itself is never hooked), manual
///     registration by serial lookup, and Void.
///   * Claims: claims filed against the selected warranty, with the
///     decision workflow. Filing against an expired / void warranty
///     is SAL-7026. An approved claim with action Return first creates
///     an RMA through SrtRepo (the returns machinery owns it) and the
///     claim links it.
/// Status shown is the EFFECTIVE status (Void terminal; otherwise the
/// stored expiry decides Active vs Expired). RBAC:
/// SALES.WARRANTY.VIEW gates the window, SALES.WARRANTY.MANAGE the
/// writes; the claim-created RMA additionally flows through
/// SrtRepo.CreateRma under the caller's return permissions.
/// </summary>
public sealed class WarrantyWin : BaseWin
{
    protected override string? ScheduleTintName => "Reed";

    private readonly WarrantyService? _service;
    private readonly List<WarrantyRow> _warranties = new();
    private readonly List<WarrantyClaimRecord> _claims = new();
    private readonly List<(TextBlock Label, string Key)> _labels = new();
    private readonly List<(DataGridColumn Column, string Key)> _headers = new();

    private readonly TabControl _tabs = new();
    private readonly TabItem _warrantiesTab = new();
    private readonly TabItem _claimsTab = new();
    private readonly TextBlock _statusBar = new();

    // Warranties tab
    private readonly DataGrid _warrantyGrid = new();
    private readonly TextBox _invoiceNo = new();
    private readonly TextBox _defaultMonths = new();
    private readonly TextBox _serialNo = new();
    private readonly TextBox _manualMonths = new();
    private readonly TextBox _terms = new();
    private readonly Button _registerFromInvoiceButton = new();
    private readonly Button _registerManualButton = new();
    private readonly Button _voidButton = new();
    private readonly Button _refreshWarrantiesButton = new();
    private WarrantyRecord? _selectedWarranty;

    // Claims tab
    private readonly DataGrid _claimGrid = new();
    private readonly TextBox _claimIssue = new();
    private readonly DatePicker _claimDate = new();
    private readonly ComboBox _claimAction = new();
    private readonly TextBox _claimNote = new();
    private readonly Button _fileClaimButton = new();
    private readonly Button _approveClaimButton = new();
    private readonly Button _rejectClaimButton = new();
    private readonly Button _refreshClaimsButton = new();

    private readonly Button _closeButton = new();

    /// <summary>Grid row: the warranty plus its effective status.</summary>
    public sealed record WarrantyRow(
        long Id, string SerialNo, string MaterialCode, string PartyName, string SourceDocNo,
        string SaleDate, int WarrantyMonths, string ExpiryDate, string Status);

    public WarrantyWin()
    {
        _service = AppHost.Warranties;
        Width = 1120;
        Height = 640;
        MinWidth = 660;
        MinHeight = 360;
        BuildUi();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.WarrantyView))
        {
            MessageBox.Show(this, T("Warranty.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        RefreshWarranties();
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
        StyleButton(_closeButton, "Common.Close");
        _closeButton.Click += (_, _) => Close();
        bar.Children.Add(_closeButton);
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

        _warrantiesTab.Content = BuildWarrantiesTab();
        _claimsTab.Content = BuildClaimsTab();
        _tabs.Items.Add(_warrantiesTab);
        _tabs.Items.Add(_claimsTab);
        root.Children.Add(_tabs);

        Content = root;
        ApplyLocalizedText();
    }

    private DockPanel BuildWarrantiesTab()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        AddActionField(actions, "Warranty.InvoiceNo", _invoiceNo, 130);
        AddActionField(actions, "Warranty.DefaultMonths", _defaultMonths, 60);
        AddActionField(actions, "Warranty.SerialNo", _serialNo, 150);
        AddActionField(actions, "Warranty.ManualMonths", _manualMonths, 60);
        AddActionField(actions, "Warranty.Terms", _terms, 220);
        DockPanel.SetDock(actions, Dock.Top);
        panel.Children.Add(actions);

        var bar = BottomBar(_registerFromInvoiceButton, _registerManualButton, _voidButton, _refreshWarrantiesButton);
        StyleButton(_registerFromInvoiceButton, "Warranty.RegisterFromInvoice");
        StyleButton(_registerManualButton, "Warranty.RegisterManual");
        StyleButton(_voidButton, "Warranty.Void");
        StyleButton(_refreshWarrantiesButton, "Common.Refresh");
        _registerFromInvoiceButton.Click += (_, _) => RegisterFromInvoice();
        _registerManualButton.Click += (_, _) => RegisterManual();
        _voidButton.Click += (_, _) => VoidSelected();
        _refreshWarrantiesButton.Click += (_, _) => RefreshWarranties();
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);

        ConfigureGrid(_warrantyGrid);
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.Serial", nameof(WarrantyRow.SerialNo), 140));
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.Material", nameof(WarrantyRow.MaterialCode), 110));
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.Party", nameof(WarrantyRow.PartyName), 170));
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.SourceDoc", nameof(WarrantyRow.SourceDocNo), 110));
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.SaleDate", nameof(WarrantyRow.SaleDate), 95));
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.Months", nameof(WarrantyRow.WarrantyMonths), 70));
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.Expiry", nameof(WarrantyRow.ExpiryDate), 95));
        _warrantyGrid.Columns.Add(TextCol("Warranty.Col.Status", nameof(WarrantyRow.Status), 85));
        _warrantyGrid.SelectionChanged += (_, _) => OnWarrantySelected();
        panel.Children.Add(_warrantyGrid);
        return panel;
    }

    private DockPanel BuildClaimsTab()
    {
        var panel = new DockPanel { Margin = new Thickness(8) };

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        AddActionField(actions, "Warranty.ClaimIssue", _claimIssue, 260);
        AddActionField(actions, "Warranty.ClaimDate", _claimDate, 130);
        AddActionField(actions, "Warranty.ClaimAction", _claimAction, 110);
        AddActionField(actions, "Warranty.ResolutionNote", _claimNote, 260);
        _claimAction.ItemsSource = WarrantyClaimActions.All;
        _claimAction.SelectedItem = WarrantyClaimActions.Repair;
        _claimDate.SelectedDate = DateTime.Today;
        DockPanel.SetDock(actions, Dock.Top);
        panel.Children.Add(actions);

        var bar = BottomBar(_fileClaimButton, _approveClaimButton, _rejectClaimButton, _refreshClaimsButton);
        StyleButton(_fileClaimButton, "Warranty.FileClaim");
        StyleButton(_approveClaimButton, "Warranty.ApproveClaim");
        StyleButton(_rejectClaimButton, "Warranty.RejectClaim");
        StyleButton(_refreshClaimsButton, "Common.Refresh");
        _fileClaimButton.Click += (_, _) => FileClaim();
        _approveClaimButton.Click += (_, _) => DecideClaim(approve: true);
        _rejectClaimButton.Click += (_, _) => DecideClaim(approve: false);
        _refreshClaimsButton.Click += (_, _) => RefreshClaims();
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);

        ConfigureGrid(_claimGrid);
        _claimGrid.Columns.Add(TextCol("Warranty.Col.ClaimDate", nameof(WarrantyClaimRecord.ClaimDate), 95));
        _claimGrid.Columns.Add(TextCol("Warranty.Col.Issue", nameof(WarrantyClaimRecord.Issue), 260));
        _claimGrid.Columns.Add(TextCol("Warranty.Col.Decision", nameof(WarrantyClaimRecord.Decision), 90));
        _claimGrid.Columns.Add(TextCol("Warranty.Col.Action", nameof(WarrantyClaimRecord.Action), 85));
        _claimGrid.Columns.Add(TextCol("Warranty.Col.Resolution", nameof(WarrantyClaimRecord.ResolutionNote), 220));
        _claimGrid.Columns.Add(TextCol("Warranty.Col.Rma", nameof(WarrantyClaimRecord.RmaId), 70));
        panel.Children.Add(_claimGrid);
        return panel;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("Warranty.Title");
        _warrantiesTab.Header = T("Warranty.Warranties");
        _claimsTab.Header = T("Warranty.Claims");
        foreach (var (label, key) in _labels)
            label.Text = T(key);
        foreach (var (column, key) in _headers)
            column.Header = T(key);
        foreach (var b in new[]
        {
            _closeButton, _registerFromInvoiceButton, _registerManualButton, _voidButton, _refreshWarrantiesButton,
            _fileClaimButton, _approveClaimButton, _rejectClaimButton, _refreshClaimsButton
        })
            if (b.Tag is string key)
                b.Content = T(key);
    }

    // ------------------------------------------------------------------
    // Warranties
    // ------------------------------------------------------------------
    private void RefreshWarranties()
    {
        try
        {
            if (_service is null) { _statusBar.Text = T("Warranty.ServiceUnavailable"); return; }
            var today = DateOnly.FromDateTime(DateTime.Today);
            var rows = _service.ListWarranties(CurrentCompanyCode);
            _warranties.Clear();
            _warranties.AddRange(rows.Select(w => new WarrantyRow(
                w.Id, w.SerialNo, w.MaterialCode, w.PartyName, w.SourceDocNo,
                w.SaleDate, w.WarrantyMonths, w.ExpiryDate, w.EffectiveStatus(today))));
            _warrantyGrid.ItemsSource = null;
            _warrantyGrid.ItemsSource = _warranties;
            _statusBar.Text = Tf("Warranty.StatusLine", _warranties.Count);
        }
        catch (Exception ex) { Report(ex, "WarrantyWin.RefreshWarranties"); }
    }

    private void OnWarrantySelected()
    {
        _selectedWarranty = null;
        if (_warrantyGrid.SelectedItem is WarrantyRow row && _service is not null)
            _selectedWarranty = _service.GetWarranty(CurrentCompanyCode, row.Id);
        RefreshClaims();
    }

    private void RegisterFromInvoice()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Warranty.ServiceUnavailable"));
            EnsureManage();
            var docNo = NullIfBlank(_invoiceNo.Text) ?? throw new InvalidOperationException(T("Warranty.InvoiceRequired"));
            var months = ParseInt(_defaultMonths.Text);
            var result = _service.RegisterFromDocument(CurrentCompanyCode, null, docNo, months,
                NullIfBlank(_terms.Text), CurrentUserId);
            _statusBar.Text = Tf("Warranty.RegisteredStatus", result.Registered, result.Skipped);
            RefreshWarranties();
        }
        catch (Exception ex) { Report(ex, "WarrantyWin.RegisterFromInvoice"); }
    }

    private void RegisterManual()
    {
        try
        {
            if (_service is null) throw new InvalidOperationException(T("Warranty.ServiceUnavailable"));
            EnsureManage();
            var serialNo = NullIfBlank(_serialNo.Text) ?? throw new InvalidOperationException(T("Warranty.SerialRequired"));
            var warranty = _service.RegisterManual(CurrentCompanyCode, serialNo, ParseInt(_manualMonths.Text),
                NullIfBlank(_terms.Text), CurrentUserId);
            _statusBar.Text = Tf("Warranty.ManualRegisteredStatus", warranty.SerialNo, warranty.ExpiryDate);
            RefreshWarranties();
        }
        catch (Exception ex) { Report(ex, "WarrantyWin.RegisterManual"); }
    }

    private void VoidSelected()
    {
        try
        {
            if (_service is null || _selectedWarranty is null)
                throw new InvalidOperationException(T("Warranty.SelectWarranty"));
            EnsureManage();
            _service.VoidWarranty(CurrentCompanyCode, _selectedWarranty.Id, CurrentUserId);
            RefreshWarranties();
        }
        catch (Exception ex) { Report(ex, "WarrantyWin.VoidSelected"); }
    }

    // ------------------------------------------------------------------
    // Claims
    // ------------------------------------------------------------------
    private void RefreshClaims()
    {
        try
        {
            if (_service is null || _selectedWarranty is null)
            {
                _claims.Clear();
                _claimGrid.ItemsSource = null;
                _claimGrid.ItemsSource = _claims;
                return;
            }
            var rows = _service.ListClaims(CurrentCompanyCode, _selectedWarranty.Id);
            _claims.Clear();
            _claims.AddRange(rows);
            _claimGrid.ItemsSource = null;
            _claimGrid.ItemsSource = _claims;
        }
        catch (Exception ex) { Report(ex, "WarrantyWin.RefreshClaims"); }
    }

    private void FileClaim()
    {
        try
        {
            if (_service is null || _selectedWarranty is null)
                throw new InvalidOperationException(T("Warranty.SelectWarranty"));
            EnsureManage();
            var issue = NullIfBlank(_claimIssue.Text) ?? throw new InvalidOperationException(T("Warranty.IssueRequired"));
            var date = _claimDate.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException(T("Warranty.ClaimDateRequired"));
            _service.FileClaim(CurrentCompanyCode, _selectedWarranty.Id, issue, date, CurrentUserId);
            _statusBar.Text = T("Warranty.ClaimFiled");
            RefreshClaims();
        }
        catch (Exception ex) { Report(ex, "WarrantyWin.FileClaim"); }
    }

    private void DecideClaim(bool approve)
    {
        try
        {
            if (_service is null || _selectedWarranty is null || _claimGrid.SelectedItem is not WarrantyClaimRecord claim)
                throw new InvalidOperationException(T("Warranty.SelectClaim"));
            EnsureManage();
            var action = approve ? _claimAction.SelectedItem as string ?? WarrantyClaimActions.Repair : null;
            long? rmaId = null;
            if (approve && action == WarrantyClaimActions.Return)
            {
                // The RMA is created FIRST, through the returns
                // machinery, so the claim can link a real request; the
                // claim decision then records the link atomically.
                rmaId = CreateClaimRma(claim);
            }
            _service.DecideClaim(CurrentCompanyCode, claim.Id, approve, action,
                NullIfBlank(_claimNote.Text), CurrentUserId, rmaId);
            _statusBar.Text = T("Warranty.ClaimDecided");
            RefreshClaims();
            RefreshWarranties();
        }
        catch (Exception ex) { Report(ex, "WarrantyWin.DecideClaim"); }
    }

    /// <summary>Builds the RMA an approved Return claim creates: one
    /// line for the claimed serial's material (qty 1), sourced from the
    /// warranty's source invoice when it has one, reason DEFECTIVE
    /// (the W6 master code for a unit coming back faulty).</summary>
    private long CreateClaimRma(WarrantyClaimRecord claim)
    {
        var warranty = _selectedWarranty
            ?? throw new InvalidOperationException(T("Warranty.SelectWarranty"));
        var hasSource = warranty.SourceDocId is > 0 && !string.IsNullOrWhiteSpace(warranty.SourceDocNo);
        var lines = new List<SrtRepo.RmaLine>
        {
            new(0, warranty.MaterialId, warranty.MaterialCode,
                $"Warranty claim {claim.Id} — serial {warranty.SerialNo}", string.Empty, 1m, 0m)
        };
        return SrtRepo.CreateRma(
            warranty.PartyId, warranty.PartyName,
            hasSource ? "INV" : "NONE",
            hasSource ? warranty.SourceDocId : null,
            hasSource ? warranty.SourceDocNo : string.Empty,
            "DEFECTIVE",
            $"Warranty claim {claim.Id} on serial {warranty.SerialNo}: {claim.Issue}",
            lines, CurrentUserId);
    }

    // ------------------------------------------------------------------
    // Helpers (CommWin pattern)
    // ------------------------------------------------------------------
    private static void EnsureManage()
    {
        if (!AppHost.Permissions.Has(PermCat.Sales.WarrantyManage))
            throw new InvalidOperationException(T("Warranty.PermissionDenied"));
    }

    private void Report(Exception ex, string context)
    {
        AppHost.Errors.Report(ex is AppError appError ? appError.Code : ErrorCodes.WindowOperationFailed, ex, context);
        MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void AddActionField(Panel panel, string key, Control control, double width)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _labels.Add((label, key));
        panel.Children.Add(label);
        control.Width = width;
        control.Margin = new Thickness(0, 0, 14, 6);
        control.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(control);
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

    private static int ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
