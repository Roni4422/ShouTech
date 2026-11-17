using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Services;
using ShouTech.Domain.Entities;

namespace ShouTech.App.Views;

/// <summary>
/// Bank account card (ribbon chunk B): fully persisted through
/// <see cref="ShouTech.Application.Services.MasterCardService"/>. Load by code,
/// save (insert/update), delete, and a live balance computed from the linked
/// GL account's posted journal lines. Actions live in the bottom command bar.
/// </summary>
public sealed class BnkCard : BaseWin
{
    private readonly TextBox _txtCode = new();
    private readonly TextBox _txtNameEn = new();
    private readonly TextBox _txtNameAr = new();
    private readonly TextBox _txtBranch = new();
    private readonly TextBox _txtAccNo = new();
    private readonly TextBox _txtIban = new();
    private readonly TextBox _txtCur = new() { Text = UsrPref.Load().CurrencyCode };
    private readonly TextBox _txtGlAcc = new();
    private readonly TextBox _txtNotes = new();
    private readonly CheckBox _chkActive = new() { VerticalAlignment = VerticalAlignment.Center, IsChecked = true };

    private readonly TextBlock _lblBal = new();

    private readonly Button _btnLoad = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();

    private Guid? _loadedId;

    public BnkCard()
    {
        Width = 720;
        Height = 460;
        MinWidth = 620;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AppHost.Permissions.Has(PermCat.Banking.BankView))
        {
            MessageBox.Show(this, T("MasterCards.PermissionDenied"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    /// <summary>Loads the bank with the given code into the card.</summary>
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
            var bank = await service.GetBankByCodeAsync(CurrentCompanyCode, code);
            if (bank is null)
            {
                MessageBox.Show(this, T("MasterCards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ShowBank(bank);
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

        metricsBorder.Child = _lblBal;
        _lblBal.FontWeight = FontWeights.Bold;

        DockPanel.SetDock(metricsBorder, Dock.Bottom);
        root.Children.Add(metricsBorder);
        root.Children.Add(actionBar);

        // One shared column grid for the whole form (3 pairs): field edges
        // align across all rows; wider fields span pairs (Phenix rule).
        var form = CreateFormGrid(3);
        form.Margin = new Thickness(16);
        AddFormField(form, 0, 0, "BankCard.Code", _txtCode);
        AddFormField(form, 0, 1, "BankCard.Name", _txtNameEn);
        AddFormField(form, 0, 2, "BankCard.Branch", _txtBranch);
        AddFormField(form, 1, 0, "MasterCards.NameAr", _txtNameAr, spanPairs: 2);
        AddFormField(form, 1, 2, "BankCard.AccountNo", _txtAccNo);
        AddFormField(form, 2, 0, "BankCard.Iban", _txtIban, spanPairs: 2);
        AddFormField(form, 3, 0, "BankCard.Currency", _txtCur);
        AddFormField(form, 3, 1, "BankCard.GlAccount", _txtGlAcc);
        _chkActive.Content = T("MasterCards.Active");
        _chkActive.Tag = "MasterCards.Active";
        AddFormElement(form, 3, 2, _chkActive);
        AddFormField(form, 4, 0, "MasterCards.Notes", _txtNotes, spanPairs: 3);

        root.Children.Add(form);
        Content = root;
    }

    private void ShowBank(Bank bank)
    {
        _loadedId = bank.Id;
        _txtCode.Text = bank.Code;
        _txtNameEn.Text = bank.NameEn;
        _txtNameAr.Text = bank.NameAr ?? string.Empty;
        _txtBranch.Text = bank.BranchName ?? string.Empty;
        _txtAccNo.Text = bank.AccountNo ?? string.Empty;
        _txtIban.Text = bank.Iban ?? string.Empty;
        _txtCur.Text = bank.CurrencyCode;
        _txtGlAcc.Text = bank.GlAccountCode ?? string.Empty;
        _txtNotes.Text = bank.Notes ?? string.Empty;
        _chkActive.IsChecked = bank.IsActive;
    }

    private async Task RefreshBalanceAsync()
    {
        var service = AppHost.MasterCards;
        if (service is null || string.IsNullOrWhiteSpace(_txtGlAcc.Text))
        {
            _lblBal.Text = $"{T("BankCard.Balance")}: 0.00 {_txtCur.Text}";
            return;
        }
        var balance = await service.GetBankBalanceAsync(CurrentCompanyCode, _txtGlAcc.Text.Trim());
        _lblBal.Text = $"{T("BankCard.Balance")}: {balance.Balance.ToString("N2", CultureInfo.CurrentCulture)} {_txtCur.Text}";
    }

    private async Task SaveAsync()
    {
        try
        {
            var service = AppHost.MasterCards
                ?? throw new InvalidOperationException(T("MasterCards.ServiceUnavailable"));
            if (!AppHost.Permissions.Has(PermCat.Banking.BankManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            var bank = new Bank
            {
                Id = _loadedId ?? Guid.NewGuid(),
                Code = _txtCode.Text.Trim(),
                NameEn = _txtNameEn.Text.Trim(),
                NameAr = string.IsNullOrWhiteSpace(_txtNameAr.Text) ? null : _txtNameAr.Text.Trim(),
                BranchName = string.IsNullOrWhiteSpace(_txtBranch.Text) ? null : _txtBranch.Text.Trim(),
                AccountNo = string.IsNullOrWhiteSpace(_txtAccNo.Text) ? null : _txtAccNo.Text.Trim(),
                Iban = string.IsNullOrWhiteSpace(_txtIban.Text) ? null : _txtIban.Text.Trim(),
                CurrencyCode = string.IsNullOrWhiteSpace(_txtCur.Text) ? UsrPref.Load().CurrencyCode : _txtCur.Text.Trim(),
                GlAccountCode = string.IsNullOrWhiteSpace(_txtGlAcc.Text) ? null : _txtGlAcc.Text.Trim(),
                IsActive = _chkActive.IsChecked == true,
                Notes = string.IsNullOrWhiteSpace(_txtNotes.Text) ? null : _txtNotes.Text.Trim()
            };
            await service.SaveBankAsync(CurrentCompanyCode, bank, CurrentUserId);
            _loadedId = bank.Id;
            MessageBox.Show(this, T("BankCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (!AppHost.Permissions.Has(PermCat.Banking.BankManage))
                throw new InvalidOperationException(T("MasterCards.PermissionDenied"));
            if (MessageBox.Show(this, T("Common.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await service.DeleteBankAsync(CurrentCompanyCode, _loadedId.Value, CurrentUserId);
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
        _txtBranch.Text = string.Empty;
        _txtAccNo.Text = string.Empty;
        _txtIban.Text = string.Empty;
        _txtCur.Text = UsrPref.Load().CurrencyCode;
        _txtGlAcc.Text = string.Empty;
        _txtNotes.Text = string.Empty;
        _chkActive.IsChecked = true;
        _lblBal.Text = $"{T("BankCard.Balance")}: 0.00 {UsrPref.Load().CurrencyCode}";
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("BankCard.Title");
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
