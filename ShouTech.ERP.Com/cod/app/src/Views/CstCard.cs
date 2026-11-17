using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Services;

namespace ShouTech.App.Views;

/// <summary>
/// Customer &amp; Supplier card. Fully persisted through
/// <see cref="CardStore"/> (migration 050: customers / suppliers tables):
/// every field on the three tabs round-trips to the database, the type
/// selector decides which table(s) the card lives in (Customer, Supplier
/// or Both), delete is a soft delete, and the metrics bar shows the real
/// party-ledger totals for the loaded card.
/// </summary>
public sealed class CstCard : BaseWin
{
    private readonly TextBox _txtCode = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtNameAr = new() { Width = 240, Height = 26 };
    private readonly TextBox _txtNameEn = new() { Width = 240, Height = 26 };
    private readonly ComboBox _cbType = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtGroup = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtTaxNo = new() { Width = 170, Height = 26 };
    private readonly TextBox _txtCrNo = new() { Width = 170, Height = 26 };

    private readonly TextBox _txtCreditLimit = new() { Width = 130, Height = 26, Text = "0.00" };
    private readonly TextBox _txtCreditDays = new() { Width = 100, Height = 26, Text = "30" };
    private readonly ComboBox _cbPriceTier = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtGlAcc = new() { Width = 160, Height = 26 };

    private readonly TextBox _txtPhone = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtMobile = new() { Width = 130, Height = 26 };
    private readonly TextBox _txtEmail = new() { Width = 240, Height = 26 };
    private readonly TextBox _txtAddress = new() { Width = 390, Height = 26 };

    private readonly TextBlock _lblCurBal = new();
    private readonly TextBlock _lblTotDr = new();
    private readonly TextBlock _lblTotCr = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnLoad = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnStmt = new();

    private long? _loadedId;
    private string _loadedKind = "C";

    // Stable storage codes for the price-tier combo (index-aligned with the
    // localized items added in ApplyLocalizedText).
    private static readonly string[] PriceTierCodes = { "Retail", "Wholesale", "Distributor" };

    public CstCard()
    {
        Width = 780;
        Height = 520;
        MinWidth = 700;
        MinHeight = 440;
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
        StyleButton(_btnStmt, "CustCard.Statement");

        _btnNew.Click += (_, _) => ResetCard();
        _btnLoad.Click += (_, _) => LoadCard();
        _btnSave.Click += (_, _) => SaveCard();
        _btnDelete.Click += (_, _) => DeleteCard();
        _btnStmt.Click += (_, _) => RptNav.OpenCustomerBalances(this);

        foreach (var b in new[] { _btnNew, _btnLoad, _btnSave, _btnDelete, _btnStmt })
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

        var metricsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        metricsPanel.Children.Add(_lblCurBal);
        metricsPanel.Children.Add(new TextBlock { Width = 30 });
        metricsPanel.Children.Add(_lblTotDr);
        metricsPanel.Children.Add(new TextBlock { Width = 30 });
        metricsPanel.Children.Add(_lblTotCr);
        metricsBorder.Child = metricsPanel;

        DockPanel.SetDock(metricsBorder, Dock.Bottom);
        root.Children.Add(metricsBorder);
        root.Children.Add(actionBar);

        // 3. Tabbed Content
        var tabs = new TabControl();
        tabs.SetResourceReference(StyleProperty, "VisuTabControlStyle");

        var tabGen = new TabItem { Header = new TextBlock { Text = T("Ser.Tab.General"), Tag = "Ser.Tab.General" } };
        var panelGen = new StackPanel { Margin = new Thickness(8) };
        var row1 = new WrapPanel();
        row1.Children.Add(Labeled("CustCard.Code", _txtCode));
        row1.Children.Add(Labeled("CustCard.Type", _cbType));
        row1.Children.Add(Labeled("CustCard.Group", _txtGroup));
        panelGen.Children.Add(row1);

        var row2 = new WrapPanel();
        row2.Children.Add(Labeled("CustCard.Name", _txtNameAr));
        row2.Children.Add(Labeled("CustCard.NameEn", _txtNameEn));
        panelGen.Children.Add(row2);

        var row3 = new WrapPanel();
        row3.Children.Add(Labeled("CustCard.TaxNumber", _txtTaxNo));
        row3.Children.Add(Labeled("CustCard.CommercialReg", _txtCrNo));
        panelGen.Children.Add(row3);
        tabGen.Content = panelGen;
        tabs.Items.Add(tabGen);

        var tabFin = new TabItem { Header = new TextBlock { Text = T("Whs.TabAccounts"), Tag = "Whs.TabAccounts" } };
        var panelFin = new StackPanel { Margin = new Thickness(8) };
        var finRow1 = new WrapPanel();
        finRow1.Children.Add(Labeled("CustCard.CreditLimit", _txtCreditLimit));
        finRow1.Children.Add(Labeled("CustCard.CreditDays", _txtCreditDays));
        finRow1.Children.Add(Labeled("CustCard.PriceTier", _cbPriceTier));
        panelFin.Children.Add(finRow1);

        var finRow2 = new WrapPanel();
        finRow2.Children.Add(Labeled("CustCard.AccountLink", _txtGlAcc));
        panelFin.Children.Add(finRow2);
        tabFin.Content = panelFin;
        tabs.Items.Add(tabFin);

        var tabCont = new TabItem { Header = new TextBlock { Text = T("CustCard.Address"), Tag = "CustCard.Address" } };
        var panelCont = new StackPanel { Margin = new Thickness(8) };
        var contRow1 = new WrapPanel();
        contRow1.Children.Add(Labeled("CustCard.Phone", _txtPhone));
        contRow1.Children.Add(Labeled("CustCard.Mobile", _txtMobile));
        panelCont.Children.Add(contRow1);

        var contRow2 = new WrapPanel();
        contRow2.Children.Add(Labeled("CustCard.Email", _txtEmail));
        panelCont.Children.Add(contRow2);

        var contRow3 = new WrapPanel();
        contRow3.Children.Add(Labeled("CustCard.Address", _txtAddress));
        panelCont.Children.Add(contRow3);
        tabCont.Content = panelCont;
        tabs.Items.Add(tabCont);

        root.Children.Add(tabs);
        Content = root;
    }

    private void ResetCard()
    {
        _loadedId = null;
        _loadedKind = "C";
        _txtCode.Text = "CUS-" + DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        _txtNameAr.Text = string.Empty;
        _txtNameEn.Text = string.Empty;
        _cbType.SelectedIndex = 0;
        _txtGroup.Text = string.Empty;
        _txtTaxNo.Text = string.Empty;
        _txtCrNo.Text = string.Empty;
        _txtCreditLimit.Text = "50000.00";
        _txtCreditDays.Text = "30";
        _cbPriceTier.SelectedIndex = 0;
        _txtGlAcc.Text = "121101";
        _txtPhone.Text = string.Empty;
        _txtMobile.Text = string.Empty;
        _txtEmail.Text = string.Empty;
        _txtAddress.Text = string.Empty;
        UpdateBalances(0, 0);
    }

    private string SelectedKind => _cbType.SelectedIndex switch { 1 => "S", 2 => "B", _ => "C" };

    private void LoadCard()
    {
        try
        {
            var card = CardStore.LoadParty(_txtCode.Text);
            if (card is null)
            {
                MessageBox.Show(this, T("Cards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _loadedId = card.Id;
            _loadedKind = card.Kind;
            _txtCode.Text = card.Code;
            _txtNameAr.Text = card.NameAr;
            _txtNameEn.Text = card.NameEn;
            _cbType.SelectedIndex = card.Kind switch { "S" => 1, "B" => 2, _ => 0 };
            _txtGroup.Text = card.Group;
            _txtTaxNo.Text = card.TaxNumber;
            _txtCrNo.Text = card.CommercialReg;
            _txtCreditLimit.Text = card.CreditLimit.ToString("0.00", CultureInfo.InvariantCulture);
            _txtCreditDays.Text = card.CreditDays.ToString(CultureInfo.InvariantCulture);
            var tier = Array.IndexOf(PriceTierCodes, card.PriceTier);
            _cbPriceTier.SelectedIndex = tier >= 0 ? tier : 0;
            _txtGlAcc.Text = card.GlAccountCode;
            _txtPhone.Text = card.Phone;
            _txtMobile.Text = card.Mobile;
            _txtEmail.Text = card.Email;
            _txtAddress.Text = card.Address;
            var (debit, credit) = CardStore.PartyTotals(card.Id, card.Kind);
            UpdateBalances(debit, credit);
        }
        catch (Exception ex)
        {
            ReportFailure("LoadParty", ex);
        }
    }

    private void SaveCard()
    {
        try
        {
            var isNew = _loadedId is null;
            var permission = isNew ? PermCat.Customers.Create : PermCat.Customers.Update;
            if (!AppHost.Permissions.Has(permission))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));

            var code = _txtCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException(T("Cards.CodeRequired"));
            if (string.IsNullOrWhiteSpace(_txtNameAr.Text))
                throw new InvalidOperationException(T("Cards.NameRequired"));
            if (!decimal.TryParse(_txtCreditLimit.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var creditLimit) || creditLimit < 0)
                throw new InvalidOperationException(T("Cards.InvalidAmount"));
            if (!int.TryParse(_txtCreditDays.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var creditDays) || creditDays < 0)
                throw new InvalidOperationException(T("Cards.InvalidAmount"));

            var tierIndex = _cbPriceTier.SelectedIndex >= 0 ? _cbPriceTier.SelectedIndex : 0;
            var card = new CardStore.PartyCard(
                _loadedId ?? 0, SelectedKind, code, _txtNameAr.Text.Trim(), _txtNameEn.Text.Trim(),
                _txtGroup.Text.Trim(), _txtTaxNo.Text.Trim(), _txtCrNo.Text.Trim(),
                creditLimit, creditDays, PriceTierCodes[tierIndex], _txtGlAcc.Text.Trim(),
                _txtPhone.Text.Trim(), _txtMobile.Text.Trim(), _txtEmail.Text.Trim(), _txtAddress.Text.Trim());

            _loadedId = CardStore.SaveParty(card, Actor());
            _loadedKind = card.Kind;
            var (debit, credit) = CardStore.PartyTotals(_loadedId.Value, _loadedKind);
            UpdateBalances(debit, credit);
            MessageBox.Show(this, T("CustCard.Saved"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportFailure("SaveParty", ex);
        }
    }

    private void DeleteCard()
    {
        try
        {
            if (!AppHost.Permissions.Has(PermCat.Customers.Delete))
                throw new InvalidOperationException(T("Cards.PermissionDenied"));
            if (_loadedId is null)
            {
                MessageBox.Show(this, T("Cards.NotFound"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show(this, T("Cards.ConfirmDelete"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            if (CardStore.SoftDeleteParty(_txtCode.Text, _loadedKind, Actor()))
            {
                MessageBox.Show(this, T("CustCard.Deleted"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                ResetCard();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("DeleteParty", ex);
        }
    }

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

    private void UpdateBalances(decimal debit, decimal credit)
    {
        var balance = debit - credit;
        _lblCurBal.Text = $"{T("CustCard.Balance")}: {balance:N2} SAR";
        _lblCurBal.FontWeight = FontWeights.Bold;
        _lblTotDr.Text = $"{T("CustCard.TotalDebit")}: {debit:N2} SAR";
        _lblTotCr.Text = $"{T("CustCard.TotalCredit")}: {credit:N2} SAR";
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("CustCard.Title");

        _cbType.Items.Clear();
        _cbType.Items.Add(T("CustCard.Customer"));
        _cbType.Items.Add(T("CustCard.Supplier"));
        _cbType.Items.Add(T("CustCard.Both"));
        _cbType.SelectedIndex = 0;

        _cbPriceTier.Items.Clear();
        _cbPriceTier.Items.Add(T("CustCard.Retail"));
        _cbPriceTier.Items.Add(T("CustCard.Wholesale"));
        _cbPriceTier.Items.Add(T("CustCard.Distributor"));
        _cbPriceTier.SelectedIndex = 0;

        RefreshLocalizedContent(Content as DependencyObject);
        UpdateBalances(0, 0);
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
