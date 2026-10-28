using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShouTech.App.Views;

/// <summary>
/// </summary>
public sealed class BnkCard : BaseWin
{

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


    private readonly TextBox _txtCode = new() { Width = 120, Height = 26 };
    private readonly TextBox _txtName = new() { Width = 230, Height = 26 };
    private readonly TextBox _txtBranch = new() { Width = 150, Height = 26 };
    private readonly TextBox _txtAccNo = new() { Width = 180, Height = 26 };
    private readonly TextBox _txtIban = new() { Width = 260, Height = 26 };
    private readonly TextBox _txtCur = new() { Width = 70, Height = 26, Text = "SAR" };
    private readonly TextBox _txtGlAcc = new() { Width = 140, Height = 26 };

    private readonly TextBlock _lblBal = new();

    private readonly Button _btnNew = new();
    private readonly Button _btnSave = new();
    private readonly Button _btnDelete = new();

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
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        StyleButton(_btnNew, "Common.New");
        StyleButton(_btnSave, "Common.Save");
        StyleButton(_btnDelete, "Common.Delete");

        _btnNew.Click += (_, _) => ResetCard();
        _btnSave.Click += (_, _) => ShowPersistenceUnavailable();
        _btnDelete.Click += (_, _) => ShowPersistenceUnavailable();

        foreach (var b in new[] { _btnNew, _btnSave, _btnDelete })
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

        metricsBorder.Child = _lblBal;
        _lblBal.FontWeight = FontWeights.Bold;

        DockPanel.SetDock(metricsBorder, Dock.Bottom);
        root.Children.Add(metricsBorder);

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
        _txtCode.Text = "BNK-01";
        _txtName.Text = string.Empty;
        _txtBranch.Text = string.Empty;
        _txtAccNo.Text = string.Empty;
        _txtIban.Text = "SA";
        _txtGlAcc.Text = "121201";
        _lblBal.Text = $"{T("BankCard.Balance")}: 0.00 SAR";
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
