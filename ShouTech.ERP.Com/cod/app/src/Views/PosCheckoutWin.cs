using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting.Invoices;
using ShouTech.App.Services;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>POS-specific tender review and document posting.</summary>
public sealed class PosCheckoutWin : BaseWin
{
    private readonly InvDoc _doc;
    private readonly string _paymentMethod;
    private readonly TextBox _customerCode = new();
    private readonly TextBox _customerName = new() { IsReadOnly = true };
    private readonly TextBlock _docNo = new();
    private readonly TextBlock _date = new();
    private readonly TextBlock _total = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(26), FontWeight = FontWeights.Bold };
    private readonly TextBlock _payment = new();
    private readonly Button _complete = new();
    private readonly Button _cancel = new();

    public PosCheckoutWin(IEnumerable<InvLine> lines, string paymentMethod, string? notes)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrWhiteSpace(paymentMethod))
            throw new ArgumentException("A payment method is required.", nameof(paymentMethod));

        _paymentMethod = paymentMethod.Trim();
        _doc = new InvDoc
        {
            Kind = InvDocKind.PosSale,
            DocNo = InvRepo.NextDocNo(InvDocKind.PosSale),
            DocDate = DateTime.Today,
            CurrencyCode = UsrPref.Load().CurrencyCode,
            PaymentMethod = MapPaymentMethod(paymentMethod),
            DefaultTaxRate = TaxSvc.GetDefaultRate(),
            Notes = notes,
            Lines = lines.Select(line => new InvLine
            {
                LineNo = line.LineNo,
                MaterialId = line.MaterialId,
                MaterialCode = line.MaterialCode,
                Description = line.Description,
                Unit = line.Unit,
                Qty = line.Qty,
                UnitPrice = line.UnitPrice,
                DiscountPct = line.DiscountPct,
                TaxRate = line.TaxRate,
                WarehouseId = line.WarehouseId
            }).ToList()
        };

        Width = 560;
        Height = 440;
        MinWidth = 500;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        BuildUi();
    }

    private void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var title = new TextBlock
        {
            Text = T("POS.CheckoutTitle"),
            FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(19),
            FontWeight = FontWeights.SemiBold
        };
        title.Tag = "POS.CheckoutTitle";
        header.Children.Add(title);
        var document = CreateFormGrid(2);
        AddFormField(document, 0, 0, "Invoice.DocNo", _docNo);
        AddFormField(document, 0, 1, "Invoice.Date", _date);
        header.Children.Add(document);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var content = new StackPanel();
        var customer = CreateFormGrid(2);
        customer.Margin = new Thickness(0, 0, 0, 8);
        AddFormField(customer, 0, 0, "Invoice.CustomerCode", _customerCode);
        AddFormField(customer, 0, 1, "Invoice.CustomerName", _customerName);
        content.Children.Add(customer);

        var payment = CreateFormGrid(1);
        payment.Margin = new Thickness(0, 0, 0, 8);
        _payment.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _payment.FontWeight = FontWeights.SemiBold;
        AddFormField(payment, 0, 0, "POS.PaymentMethod", _payment);
        content.Children.Add(payment);

        var totalPanel = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 2, 0, 8)
        };
        totalPanel.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        _total.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        totalPanel.Child = _total;
        content.Children.Add(totalPanel);

        var paymentNotice = new TextBlock
        {
            Text = T("POS.PaymentPostingNotice"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        };
        paymentNotice.Tag = "POS.PaymentPostingNotice";
        content.Children.Add(paymentNotice);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        ConfigureButton(_cancel, "Common.Cancel");
        ConfigureButton(_complete, "POS.CompleteSale");
        _complete.MinWidth = ShouTech.App.Services.VisuId.PrimaryButtonMinWidth;
        _complete.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        _cancel.Click += (_, _) => Close();
        _complete.Click += (_, _) => CompleteSale();
        actions.Children.Add(_cancel);
        actions.Children.Add(_complete);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        Content = root;
        _docNo.Text = _doc.DocNo;
        _date.Text = _doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _payment.Text = T("POS.PaymentMethod") + ": " + T(PaymentMethodKey());
        _total.Text = T("Invoice.Total") + ": " + _doc.GrandTotal.ToString("N2", CultureInfo.InvariantCulture) + " " + _doc.CurrencyCode;
        _customerCode.LostFocus += (_, _) => ResolveCustomer();
    }

    private static void ConfigureButton(Button button, string key)
    {
        button.Content = T(key);
        button.Tag = key;
        button.MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private void ResolveCustomer()
    {
        var found = TaxSvc.ResolveParty(_customerCode.Text, customer: true);
        if (found is null)
        {
            _doc.PartyId = null;
            _doc.PartyCode = _customerCode.Text.Trim();
            _doc.PartyName = string.Empty;
            _customerName.Clear();
            return;
        }

        _doc.PartyId = found.Value.Id;
        _doc.PartyCode = _customerCode.Text.Trim();
        _doc.PartyName = found.Value.Name;
        _customerName.Text = found.Value.Name;
    }

    private string PaymentMethodKey()
        => "POS." + (_paymentMethod.Equals("K-Net", StringComparison.OrdinalIgnoreCase) ? "KNet" : _paymentMethod);

    /// <summary>Maps the POS tender label onto the persisted payment-method enum.
    /// Card networks (K-Net, Visa) post as Card, never silently as Cash.</summary>
    private static InvPaymentMethod MapPaymentMethod(string method)
    {
        if (method.Equals("Credit", StringComparison.OrdinalIgnoreCase))
            return InvPaymentMethod.Credit;
        if (method.Equals("K-Net", StringComparison.OrdinalIgnoreCase)
            || method.Equals("Visa", StringComparison.OrdinalIgnoreCase)
            || method.Equals("Card", StringComparison.OrdinalIgnoreCase))
            return InvPaymentMethod.Card;
        return InvPaymentMethod.Cash;
    }

    private void CompleteSale()
    {
        if (_doc.Lines.Count == 0)
        {
            ShowWarn("Invoice.NeedLines");
            return;
        }

        ResolveCustomer();
        if (_customerCode.Text.Length > 0 && _doc.PartyId is null)
        {
            ShowWarn("POS.CustomerNotFound");
            return;
        }
        if (_paymentMethod.Equals("Credit", StringComparison.OrdinalIgnoreCase) && _doc.PartyId is null)
        {
            ShowWarn("POS.CreditNeedsCustomer");
            return;
        }
        if (!AskConfirm("POS.ConfirmSale"))
            return;

        _complete.IsEnabled = false;
        try
        {
            // A completed POS sale is settled in full unless it is on credit.
            _doc.PaidAmount = _doc.PaymentMethod == InvPaymentMethod.Credit ? 0m : _doc.GrandTotal;
            InvRepo.SaveDraft(_doc);
            InvRepo.Post(_doc.DocId, _doc.Kind, AppState.CurrentUser?.Username ?? Environment.UserName);
            ShowInfo("POS.SalePosted");
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, ex, $"PosCheckoutWin.CompleteSale | DocNo={_doc.DocNo}");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            _complete.IsEnabled = true;
        }
    }

    protected override void ApplyLocalizedText()
    {
        Title = T("POS.CheckoutTitle");
        RefreshLocalizedText(Content as DependencyObject);
        _payment.Text = T("POS.PaymentMethod") + ": " + T(PaymentMethodKey());
        _total.Text = T("Invoice.Total") + ": " + _doc.GrandTotal.ToString("N2", CultureInfo.InvariantCulture) + " " + _doc.CurrencyCode;
    }

    private static void RefreshLocalizedText(DependencyObject? parent)
    {
        if (parent is Button button && button.Tag is string buttonKey)
            button.Content = T(buttonKey);
        if (parent is TextBlock textBlock && textBlock.Tag is string textKey)
            textBlock.Text = T(textKey);
        if (parent == null)
            return;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedText(VisualTreeHelper.GetChild(parent, i));
    }
}
