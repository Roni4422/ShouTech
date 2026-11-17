using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Bank card. Fully persisted through <see cref="CardStore"/> (migration
/// 050: bank_accounts table): every field round-trips to the database,
/// delete is a soft delete, and the metrics bar shows the real book
/// balance of the linked GL account.
/// </summary>
public sealed class BnkCard : BaseWin
{
    private readonly TextBox _txtCode = new() { Width = 120, Height = 26 };
    private readonly TextBox _txtName = new() { Width = 230, Height = 26 };
    private readonly TextBox _txtBranch = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtAccNo = new() { Width = 180, Height = 26 };
    private readonly TextBox _txtIban = new() { Width = 260, Height = 26 };
    private readonly TextBox _txtCur = new() { Width = 70, Height = 26, Text = UsrPref.Load().CurrencyCode };
    private readonly TextBox _txtGlAcc = new() { Width = 140, Height = 26 };

    private readonly TextBlock _lblBal = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();

    private long? _loadedId;

    public BnkCard()
    {
        Width = 680;
        Height = 400;
        MinWidth = 600;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        BuildUi();
        ApplyLocalizedText();
        ResetCard();
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // 1. Toolbar
        var toolbar = new WrapPanel { Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnLoad, "Vouchers.Load");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");

        _btnNew.Click += (_, _) => ResetCard();
        _btnLoad.Click += (_, _) => LoadCard();
        _btnSave.Click += (_, _) => SaveCard();
        _btnDelete.Click += (_, _) => DeleteCard();

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnDelete })
            toolbar.Children.Add(b);

        // Explorer-style bottom command bar: flat, transparent, subtle 1px top divider.
        var actionBar = new Border
        {
            Child = toolbar,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 8, 0, 0)
        };
        actionBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        DockPanel.SetDock(actionBar, Dock.Bottom);

        // 2. Metrics Bar
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

        // 3. Form Content
        var formPanel = new StackPanel { Margin = new Thickness(4) };

        var r1 = new WrapPanel();
        r1.Children.Add(Labeled("BankCard.Code", _txtCode));
        r1.Children.Add(Labeled("BankCard.Name", _txtName));
        r1.Children.Add(Labeled("BankCard.Branch", _txtBranch));
        formPanel.Children.Add(r1);

        var r2 = new WrapPanel();
        r2.Children.Add(Labeled("BankCard.AccountNo", _txtAccNo));
        r2.Children.Add(Labeled("BankCard.Iban", _txtIban));
        formPanel.Children.Add(r2);

        var r3 = new WrapPanel();
        r3.Children.Add(Labeled("BankCard.Currency", _txtCur));
        r3.Children.Add(Labeled("BankCard.GlAccount", _txtGlAcc));
        formPanel.Children.Add(r3);

        root.Children.Add(formPanel);
        Content = root;
    }

    private void ResetCard()
    {
        _loadedId = null;
        _txtCode.Text = "BNK-01";
        _txtName.Text = string.Empty;
        _txtBranch.Text = string.Empty;
        _txtAccNo.Text = string.Empty;
        _txtIban.Text = "SA";
        _txtCur.Text = UsrPref.Load().CurrencyCode;
        _txtGlAcc.Text = "121201";
        UpdateBalance(0);
    }

    private void LoadCard()
    {
        try
        {
            var rec = CardStore.LoadBank(_txtCode.Text);
            if (rec is null)
            {
                MessageBox.Show(this, T("Cards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _loadedId = rec.Id;
            _txtCode.Text = rec.Code;
            _txtName.Text = rec.Name;
            _txtBranch.Text = rec.Branch;
            _txtAccNo.Text = rec.AccountNo;
            _txtIban.Text = rec.Iban;
            _txtCur.Text = rec.CurrencyCode;
            _txtGlAcc.Text = rec.GlAccountCode;
            UpdateBalance(CardStore.BankBookBalance(rec.GlAccountCode));
        }
        catch (Exception ex)
        {
            ReportFailure("LoadBank", ex);
        }
    }

    private void SaveCard()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Banking.BankManage))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));
            var code = _txtCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException(T("Cards.CodeRequired"));
            if (string.IsNullOrWhiteSpace(_txtName.Text))
                throw new InvalidOperationException(T("Cards.NameRequired"));

            var rec = new CardStore.BankCardRec(
                _loadedId ?? 0, code, _txtName.Text.Trim(), _txtBranch.Text.Trim(),
                _txtAccNo.Text.Trim(), _txtIban.Text.Trim(),
                string.IsNullOrWhiteSpace(_txtCur.Text) ? UsrPref.Load().CurrencyCode : _txtCur.Text.Trim().ToUpperInvariant(),
                _txtGlAcc.Text.Trim());
            _loadedId = CardStore.SaveBank(rec, Actor());
            UpdateBalance(CardStore.BankBookBalance(rec.GlAccountCode));
            MessageBox.Show(this, T("BankCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveBank", ex);
        }
    }

    private void DeleteCard()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Banking.BankManage))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));
            if (_loadedId is null)
            {
                MessageBox.Show(this, T("Cards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(this, T("Cards.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (CardStore.SoftDeleteBank(_txtCode.Text, Actor()))
            {
                MessageBox.Show(this, T("BankCard.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                ResetCard();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("DeleteBank", ex);
        }
    }

    private void UpdateBalance(decimal balance)
        => _lblBal.Text = $"{T("BankCard.Balance")}: {balance:N2} {_txtCur.Text}";

    private static string Actor()
        => !string.IsNullOrWhiteSpace(UsrSession.Current?.Id)
            ? UsrSession.Current!.Id
            : AppHost.Session.CurrentUser ?? string.Empty;

    private void ReportFailure(string operation, Exception exception)
    {
        AppHost.Logging?.LogError("WindowPersistence",
            $"Window={GetType().Name} | Operation={operation} | {exception.Message}", exception);
        MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("BankCard.Title");
        RefreshLocalizedContent(Content as DependencyObject);
        _lblBal.Text = $"{T("BankCard.Balance")}: 0.00 {_txtCur.Text}";
    }

    private static void RefreshLocalizedContent(DependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is Button button && button.Tag is string bKey)
            button.Content = T(bKey);
        if (parent is TextBlock tb && tb.Tag is string tKey)
            tb.Text = T(tKey);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedContent(VisualTreeHelper.GetChild(parent, i));
    }

    private static StackPanel Labeled(string key, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 6) };
        sp.Children.Add(new TextBlock { Text = T(key), Tag = key, Margin = new Thickness(0, 0, 0, 2), Opacity = 0.85 });
        if (control is TextBox textBox)
            textBox.SetResourceReference(StyleProperty, "VisuTextBoxStyle");
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
}
