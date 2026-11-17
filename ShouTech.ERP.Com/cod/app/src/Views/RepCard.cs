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
    private readonly TextBox _txtCode = new() { Width = 110, Height = 26 };
    private readonly TextBox _txtNameEn = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtNameAr = new() { Width = 220, Height = 26 };
    private readonly TextBox _txtPhone = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtComm = new() { Width = 90, Height = 26, Text = "0" };
    private readonly TextBox _txtTarget = new() { Width = 120, Height = 26, Text = "0.00" };
    private readonly ComboBox _cbGroup = new() { Width = 200, Height = 26 };
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
        Width = 720;
        Height = 400;
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

        var formPanel = new StackPanel { Margin = new Thickness(4) };

        var r1 = new WrapPanel();
        r1.Children.Add(Labeled("SalesRepCard.Code", _txtCode));
        r1.Children.Add(Labeled("SalesRepCard.Name", _txtNameEn));
        r1.Children.Add(Labeled("SalesRepCard.Phone", _txtPhone));
        formPanel.Children.Add(r1);

        var r2 = new WrapPanel();
        r2.Children.Add(Labeled("MasterCards.NameAr", _txtNameAr));
        r2.Children.Add(Labeled("MasterCards.Group", _cbGroup));
        formPanel.Children.Add(r2);

        var r3 = new WrapPanel();
        r3.Children.Add(Labeled("SalesRepCard.Commission", _txtComm));
        r3.Children.Add(Labeled("SalesRepCard.Target", _txtTarget));
        var activePanel = new StackPanel { Margin = new Thickness(0, 20, 10, 6) };
        _chkActive.Content = T("MasterCards.Active");
        _chkActive.Tag = "MasterCards.Active";
        activePanel.Children.Add(_chkActive);
        r3.Children.Add(activePanel);
        formPanel.Children.Add(r3);

        root.Children.Add(formPanel);
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

    private static StackPanel Labeled(string key, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 4) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 2), Opacity = 0.85 });
        if (control is TextBox textBox)
            textBox.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
        if (control is ComboBox comboBox)
            comboBox.SetResourceReference(StyleProperty, "VisuComboBoxStyle");
        sp.Children.Add(control);
        return sp;
    }

    private static void StyleButton(Button btn, string key)
    {
        btn.Content = T(key);
        btn.Margin = new Thickness(0, 0, 5, 0);
        btn.Padding = new Thickness(9, 3, 9, 3);
        btn.MinWidth = 76;
        btn.Height = 28;
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
