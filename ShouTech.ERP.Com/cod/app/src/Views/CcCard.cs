using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Cost center card (ribbon chunk B): fully persisted through
/// <see cref="ShouTech.Application.Services.MasterCardService"/>. Load by code,
/// save (insert/update), guarded delete, parent lookup from the company's cost
/// centers, and live debit/credit/balance from posted journal lines. Actions
/// live in the Explorer-style bottom command bar.
/// </summary>
public sealed class CcCard : BaseWin
{
    private readonly TextBox _txtCode = new();
    private readonly ComboBox _cbParent = new();
    private readonly TextBox _txtNameAr = new();
    private readonly TextBox _txtNameEn = new();
    private readonly TextBox _txtLevel = new() { Text = "1", IsReadOnly = true };
    private readonly TextBox _txtNotes = new();
    private readonly CheckBox _chkGroup = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _chkActive = new() { VerticalAlignment = VerticalAlignment.Center, IsChecked = true };

    private readonly TextBlock _lblDr = new();
    private readonly TextBlock _lblCr = new();
    private readonly TextBlock _lblBal = new();

    private readonly Button _btnLoad = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();

    private readonly List<CostCenter> _parents = new();
    private Guid? _loadedId;
    private Guid? _presetParentId;

    /// <summary>When set before the window loads, the new card starts under this parent cost center.</summary>
    public Guid? InitialParentId
    {
        get => _presetParentId;
        set => _presetParentId = value;
    }

    public CcCard()
    {
        Width = 630;
        Height = 390;
        MinWidth = 620;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Accounting.CostCenterView))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshParentsAsync();
        if (_presetParentId.HasValue)
            SelectParent(_presetParentId.Value);
    }

    /// <summary>Loads the cost center with the given code into the card (used by CcListWin).</summary>
    public async Task LoadByCodeAsync(string code)
    {
        try
        {
            var service = AppHost.MasterCards;
            if (service is null)
            {
                MessageBox.Show(this, T("MasterCards.ServiceUnavailable"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var center = await service.GetCostCenterByCodeAsync(CurrentCompanyCode, code);
            if (center is null)
            {
                MessageBox.Show(this, T("MasterCards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await RefreshParentsAsync();
            ShowCenter(center);
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

        var toolbar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnLoad, "MasterCards.Load");
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");

        _btnLoad.Click += async (_, _) => await LoadByCodeAsync(_txtCode.Text.Trim());
        _btnNew.Click += (_, _) => ResetCard();
        _btnSave.Click += async (_, _) => await SaveAsync();
        _btnDelete.Click += async (_, _) => await DeleteAsync();

        foreach (var b in new[] { _btnLoad, _btnNew, _btnSave, _btnDelete })
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
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        metricsBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        metricsBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        _lblCr.Margin = new Thickness(24, 0, 0, 0);
        _lblBal.Margin = new Thickness(24, 0, 0, 0);
        var metricsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        metricsPanel.Children.Add(_lblDr);
        metricsPanel.Children.Add(_lblCr);
        metricsPanel.Children.Add(_lblBal);
        metricsBorder.Child = metricsPanel;

        DockPanel.SetDock(metricsBorder, Dock.Bottom);
        root.Children.Add(metricsBorder);
        root.Children.Add(actionBar);

        // One shared column grid for the whole form (3 pairs), side labels,
        // field edges aligned across rows (Phenix rule).
        var form = CreateFormGrid(3);
        form.Margin = new Thickness(16);
        AddFormField(form, 0, 0, "CostCenter.Code", _txtCode);
        AddFormField(form, 0, 1, "CostCenter.ParentCode", _cbParent);
        AddFormField(form, 0, 2, "CostCenter.Level", _txtLevel);
        AddFormField(form, 1, 0, "CostCenter.NameAr", _txtNameAr);
        AddFormField(form, 1, 1, "CostCenter.NameEn", _txtNameEn, spanPairs: 2);
        _chkGroup.Content = T("MasterCards.IsGroup");
        _chkGroup.Tag = "MasterCards.IsGroup";
        _chkActive.Content = T("MasterCards.Active");
        _chkActive.Tag = "MasterCards.Active";
        AddFormElement(form, 2, 0, _chkGroup);
        AddFormElement(form, 2, 1, _chkActive);
        AddFormField(form, 3, 0, "CostCenter.Notes", _txtNotes, spanPairs: 3);

        root.Children.Add(form);
        Content = root;
    }

    private async Task RefreshParentsAsync()
    {
        var service = AppHost.MasterCards;
        if (service is null)
            return;
        var all = await service.GetCostCentersAsync(CurrentCompanyCode);
        _parents.Clear();
        _parents.AddRange(all.Where(c => c.IsGroup));
        var selected = (_cbParent.SelectedItem as CostCenter)?.Id;
        _cbParent.ItemsSource = null;
        _cbParent.ItemsSource = _parents;
        _cbParent.DisplayMemberPath = nameof(CostCenter.Code);
        if (selected.HasValue)
            SelectParent(selected.Value);
    }

    private void SelectParent(Guid parentId)
    {
        foreach (var item in _parents)
        {
            if (item.Id == parentId)
            {
                _cbParent.SelectedItem = item;
                return;
            }
        }
    }

    private void ShowCenter(CostCenter center)
    {
        _loadedId = center.Id;
        _txtCode.Text = center.Code;
        _txtNameEn.Text = center.NameEn;
        _txtNameAr.Text = center.NameAr ?? string.Empty;
        _txtLevel.Text = center.Level.ToString(CultureInfo.InvariantCulture);
        _txtNotes.Text = center.Notes ?? string.Empty;
        _chkGroup.IsChecked = center.IsGroup;
        _chkActive.IsChecked = center.IsActive;
        _cbParent.SelectedItem = null;
        if (center.ParentId.HasValue)
            SelectParent(center.ParentId.Value);
    }

    private async Task RefreshBalanceAsync()
    {
        var service = AppHost.MasterCards;
        if (service is null || string.IsNullOrWhiteSpace(_txtCode.Text))
        {
            UpdateBalances(0, 0);
            return;
        }
        var balance = await service.GetCostCenterBalanceAsync(CurrentCompanyCode, _txtCode.Text.Trim());
        UpdateBalances(balance.Debit, balance.Credit);
    }

    private async Task SaveAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Accounting.CostCenterManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            var center = new CostCenter
            {
                Id = _loadedId ?? Guid.NewGuid(),
                Code = _txtCode.Text.Trim(),
                NameEn = _txtNameEn.Text.Trim(),
                NameAr = string.IsNullOrWhiteSpace(_txtNameAr.Text) ? null : _txtNameAr.Text.Trim(),
                ParentId = (_cbParent.SelectedItem as CostCenter)?.Id,
                IsGroup = _chkGroup.IsChecked == true,
                IsActive = _chkActive.IsChecked == true,
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim()
            };
            await service.SaveCostCenterAsync(CurrentCompanyCode, center, CurrentUserId);
            _loadedId = center.Id;
            _txtLevel.Text = center.Level.ToString(CultureInfo.InvariantCulture);
            MessageBox.Show(this, T("CostCenter.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshParentsAsync();
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
            if (!AppHost.Permissions.Has(PermCat.Accounting.CostCenterManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await service.DeleteCostCenterAsync(CurrentCompanyCode, _loadedId.Value, CurrentUserId);
            MessageBox.Show(this, T("MasterCards.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            ResetCard();
            await RefreshParentsAsync();
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
        _txtLevel.Text = "1";
        _txtNotes.Text = string.Empty;
        _chkGroup.IsChecked = false;
        _chkActive.IsChecked = true;
        _cbParent.SelectedItem = null;
        UpdateBalances(0, 0);
    }

    private void UpdateBalances(decimal debit, decimal credit)
    {
        _lblDr.Text = $"{T("CostCenter.Debit")}: {debit:N2}";
        _lblCr.Text = $"{T("CostCenter.Credit")}: {credit:N2}";
        var bal = debit - credit;
        _lblBal.Text = $"{T("CostCenter.Balance")}: {bal:N2}";
        _lblBal.FontWeight = FontWeights.Bold;
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CostCenter.Title");
        _chkGroup.Content = T("MasterCards.IsGroup");
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
}
