using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShouTech.App.Views;

/// <summary>
/// </summary>
public sealed class CstCard : BaseWin
{
    /// <summary>Logs unavailable operations instead of reporting a false success.</summary>
    private void ShowOperationUnavailable(string operation)
    {
        const string errorCode = "APP-2011";
        var context = $"Operation={operation} is not implemented in window {GetType().Name}.";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var exception = new NotSupportedException(context);

        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowOperation",
            $"[{definition.Code}] {definition.Name} | Window={GetType().Name} | {context}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowOperation",
            "OperationUnavailable",
            $"Code={definition.Code} | Window={GetType().Name} | {context}");

        MessageBox.Show(
            this,
            $"[{definition.Code}] {definition.Message}\n{context}",
            "العملية غير متاحة",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }


    /// <summary>
    /// Prevents a false success message when this screen has no database persistence wired.
    /// This intentionally does not claim to save any values.
    /// </summary>
    private void ShowPersistenceUnavailable()
    {
        const string errorCode = "APP-2010";
        const string context = "This window has no database persistence implementation.";
        var definition = ShouTech.App.Localization.ErrorCatalog.Resolve(errorCode);
        var exception = new NotSupportedException(context);

        // Errors are routed to errors.log; the operational trace goes to log.log.
        ShouTech.App.Services.AppHost.Logging?.LogError(
            "WindowPersistence",
            $"[{definition.Code}] {definition.Name} | Window={GetType().Name} | {context}",
            exception);
        ShouTech.App.Services.AppHost.Logging?.LogOperation(
            "WindowPersistence",
            "PersistenceUnavailable",
            $"Code={definition.Code} | Window={GetType().Name}");

        MessageBox.Show(
            this,
            $"[{definition.Code}] {definition.Message}\n{context}\nلم يتم حفظ أي بيانات.",
            "العملية غير متاحة",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }


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
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();
    private readonly Button _btnStmt = new();

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
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");
        StyleButton(_btnStmt, "CustCard.Statement");

        _btnNew.Click += (_, _) => ResetCard();
        _btnSave.Click += (_, _) => ShowPersistenceUnavailable();
        _btnDelete.Click += (_, _) => ShowPersistenceUnavailable();
        _btnStmt.Click += (_, _) => ShowOperationUnavailable("Customer statement");

        foreach (var b in new[] { _btnNew, _btnSave, _btnDelete, _btnStmt })
            toolbar.Children.Add(b);

        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

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
        _txtCode.Text = "CUS-" + DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        _txtNameAr.Text = string.Empty;
        _txtNameEn.Text = string.Empty;
        _txtGroup.Text = string.Empty;
        _txtTaxNo.Text = string.Empty;
        _txtCrNo.Text = string.Empty;
        _txtCreditLimit.Text = "50000.00";
        _txtCreditDays.Text = "30";
        _txtGlAcc.Text = "121101";
        _txtPhone.Text = string.Empty;
        _txtMobile.Text = string.Empty;
        _txtEmail.Text = string.Empty;
        _txtAddress.Text = string.Empty;
        UpdateBalances(0, 0);
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
