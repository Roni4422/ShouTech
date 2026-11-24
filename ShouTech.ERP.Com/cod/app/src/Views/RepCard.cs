using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Sales representative card (ribbon chunk B): fully persisted through
/// <see cref="ShouTech.Application.Services.MasterCardService"/> — code, names,
/// phone, commission %, target amount and group (from sales_rep_groups).
/// Load by code, save (insert/update), delete. Bottom command bar.
/// </summary>
public sealed class RepCard : BaseWin
{
    private readonly TextBox _txtCode = new();
    private readonly TextBox _txtNameEn = new();
    private readonly TextBox _txtNameAr = new();
    private readonly TextBox _txtPhone = new();
    private readonly TextBox _txtComm = new() { Text = "0" };
    private readonly TextBox _txtTarget = new() { Text = "0.00" };
    private readonly ComboBox _cbGroup = new();
    private readonly CheckBox _chkActive = new() { VerticalAlignment = VerticalAlignment.Center, IsChecked = true };

    private readonly Button _btnLoad = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();

    private readonly TextBlock _lblSummary = new();

    private readonly List<SalesRepGroup> _groups = new();
    private Guid? _loadedId;

    public RepCard()
    {
        Width = 650;
        Height = 360;
        MinWidth = 640;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Representatives.View))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        await RefreshGroupsAsync();
    }

    /// <summary>Loads the sales rep with the given code into the card.</summary>
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
            var rep = await service.GetSalesRepByCodeAsync(CurrentCompanyCode, code);
            if (rep is null)
            {
                MessageBox.Show(this, T("MasterCards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await RefreshGroupsAsync();
            ShowRep(rep);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

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
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        metricsBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        metricsBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        metricsBorder.Child = _lblSummary;
        _lblSummary.FontWeight = FontWeights.Bold;

        DockPanel.SetDock(metricsBorder, Dock.Bottom);
        root.Children.Add(metricsBorder);
        root.Children.Add(actionBar);

        // One shared column grid for the whole form (3 pairs), side labels,
        // field edges aligned across rows (Phenix rule).
        var form = CreateFormGrid(3);
        form.Margin = new Thickness(16);
        AddFormField(form, 0, 0, "SalesRepCard.Code", _txtCode);
        AddFormField(form, 0, 1, "SalesRepCard.Name", _txtNameEn);
        AddFormField(form, 0, 2, "SalesRepCard.Phone", _txtPhone);
        AddFormField(form, 1, 0, "MasterCards.NameAr", _txtNameAr, spanPairs: 2);
        AddFormField(form, 1, 2, "MasterCards.Group", _cbGroup);
        AddFormField(form, 2, 0, "SalesRepCard.Commission", _txtComm);
        AddFormField(form, 2, 1, "SalesRepCard.Target", _txtTarget);
        _chkActive.Content = T("MasterCards.Active");
        _chkActive.Tag = "MasterCards.Active";
        AddFormElement(form, 2, 2, _chkActive);

        root.Children.Add(form);
        Content = root;
    }

    private async Task RefreshGroupsAsync()
    {
        var service = AppHost.MasterCards;
        if (service is null)
            return;
        var groups = await service.GetSalesRepGroupsAsync(CurrentCompanyCode);
        _groups.Clear();
        _groups.AddRange(groups);
        var selected = (_cbGroup.SelectedItem as SalesRepGroup)?.Id;
        _cbGroup.ItemsSource = null;
        _cbGroup.ItemsSource = _groups;
        _cbGroup.DisplayMemberPath = nameof(SalesRepGroup.Code);
        if (selected.HasValue)
        {
            foreach (var group in _groups)
                if (group.Id == selected.Value)
                    _cbGroup.SelectedItem = group;
        }
    }

    private void ShowRep(SalesRep rep)
    {
        _loadedId = rep.Id;
        _txtCode.Text = rep.Code;
        _txtNameEn.Text = rep.NameEn;
        _txtNameAr.Text = rep.NameAr ?? string.Empty;
        _txtPhone.Text = rep.Phone ?? string.Empty;
        _txtComm.Text = rep.CommissionPct.ToString("0.##", CultureInfo.InvariantCulture);
        _txtTarget.Text = rep.TargetAmount.ToString("0.##", CultureInfo.InvariantCulture);
        _chkActive.IsChecked = rep.IsActive;
        _cbGroup.SelectedItem = null;
        if (rep.GroupId.HasValue)
        {
            foreach (var group in _groups)
                if (group.Id == rep.GroupId.Value)
                    _cbGroup.SelectedItem = group;
        }
        UpdateSummary(rep.TargetAmount, rep.CommissionPct);
    }

    private async Task SaveAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            var permission = _loadedId.HasValue ? PermCat.Representatives.Update : PermCat.Representatives.Create;
            if (!AppHost.Permissions.Has(permission))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            var rep = new SalesRep
            {
                Id = _loadedId ?? Guid.NewGuid(),
                Code = _txtCode.Text.Trim(),
                NameEn = _txtNameEn.Text.Trim(),
                NameAr = string.IsNullOrWhiteSpace(_txtNameAr.Text) ? null : _txtNameAr.Text.Trim(),
                Phone = string.IsNullOrWhiteSpace(_txtPhone.Text) ? null : _txtPhone.Text.Trim(),
                CommissionPct = ParseDecimal(_txtComm.Text),
                TargetAmount = ParseDecimal(_txtTarget.Text),
                GroupId = (_cbGroup.SelectedItem as SalesRepGroup)?.Id,
                IsActive = _chkActive.IsChecked == true
            };
            await service.SaveSalesRepAsync(CurrentCompanyCode, rep, CurrentUserId);
            _loadedId = rep.Id;
            UpdateSummary(rep.TargetAmount, rep.CommissionPct);
            MessageBox.Show(this, T("SalesRepCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (!AppHost.Permissions.Has(PermCat.Representatives.Delete))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await service.DeleteSalesRepAsync(CurrentCompanyCode, _loadedId.Value, CurrentUserId);
            MessageBox.Show(this, T("MasterCards.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            ResetCard();
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
        _txtNameEn.Text = string.Empty;
        _txtNameAr.Text = string.Empty;
        _txtPhone.Text = string.Empty;
        _txtComm.Text = "0";
        _txtTarget.Text = "0.00";
        _cbGroup.SelectedItem = null;
        _chkActive.IsChecked = true;
        UpdateSummary(0, 0);
    }

    private void UpdateSummary(decimal target, decimal commissionPct)
    {
        _lblSummary.Text = $"{T("SalesRepCard.Target")}: {target.ToString("N2", CultureInfo.CurrentCulture)} · {T("SalesRepCard.Commission")}: {commissionPct.ToString("0.##", CultureInfo.CurrentCulture)}%";
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("SalesRepCard.Title");
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
