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
using ShouTech.Domain.Entities;

using ShouTech.Domain;
namespace ShouTech.App.Views;

/// <summary>
/// POS checkout (Build POS 066): split-tender settlement of one bill.
/// Cash (applied + handed-over, change computed), card, and on-account
/// tenders combine freely; the applied amounts must equal the bill
/// total exactly (PosTenderMath, POS-1004) and on-account requires the
/// named customer PosWin resolved (its credit guard runs inside
/// InvRepo.Post). Posting goes through PosPosting.CompleteSale — the
/// verified InvRepo path with the session context — and a posted sale
/// prints its receipt through InvPrn.PrintPosReceipt.
/// </summary>
public sealed class PosCheckoutWin : BaseWin
{
    private readonly InvDoc _doc;
    private readonly Guid? _sessionId;
    private readonly string? _sessionNo;
    private readonly PosDiscountApproval? _discountApproval;
    private readonly TextBlock _docNo = new();
    private readonly TextBlock _date = new();
    private readonly TextBlock _customer = new();
    private readonly TextBlock _session = new();
    private readonly TextBlock _total = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(20), FontWeight = FontWeights.Bold };
    private readonly TextBox _cashApplied = new() { Text = "0" };
    private readonly TextBox _cashTendered = new() { Text = "0" };
    private readonly TextBox _cardApplied = new() { Text = "0" };
    private readonly TextBox _cardRef = new();
    private readonly TextBox _onAccount = new() { Text = "0" };
    private readonly TextBlock _change = new() { FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(16), FontWeight = FontWeights.Bold };
    private readonly TextBlock _remaining = new();
    private readonly Button _complete = new();
    private readonly Button _cancel = new();

    /// <summary>The posted document and its change, for the caller.</summary>
    public InvDoc? PostedDoc { get; private set; }
    public decimal ChangeDue { get; private set; }

    public PosCheckoutWin(InvDoc doc, Guid? sessionId, string? sessionNo, string defaultTender,
        PosDiscountApproval? discountApproval = null)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _sessionId = sessionId;
        _sessionNo = sessionNo;
        _discountApproval = discountApproval;
        Width = 620;
        Height = 560;
        MinWidth = 540;
        // Honest content height after the 2026-11-20 compaction below is
        // ~374 DIP (see FIX_POSCHECKOUT_MINIMUM.md); 372 leaves the middle
        // band a few DIP of scroll at the minimum instead of clipping.
        // Must stay <= 684x384 (verify_screenfit.py cap).
        MinHeight = 372;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // The cashier's pre-selected tender pre-fills its box.
        var total = doc.GrandTotal;
        switch (defaultTender)
        {
            case "Card": case "K-Net": case "Visa": _cardApplied.Text = total.ToString("N2", CultureInfo.InvariantCulture); break;
            case "Credit": _onAccount.Text = total.ToString("N2", CultureInfo.InvariantCulture); break;
            default:
                _cashApplied.Text = total.ToString("N2", CultureInfo.InvariantCulture);
                _cashTendered.Text = total.ToString("N2", CultureInfo.InvariantCulture);
                break;
        }
        BuildUi();
        UpdateTenderMath();
    }

    private void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var title = new TextBlock { Text = T("POS.CheckoutTitle"), FontSize = ShouTech.App.Services.VisuId.ResolveFontSize(16), FontWeight = FontWeights.SemiBold };
        title.Tag = "POS.CheckoutTitle";
        header.Children.Add(title);
        var document = CreateFormGrid(2);
        AddFormField(document, 0, 0, "Invoice.DocNo", _docNo);
        AddFormField(document, 0, 1, "Invoice.Date", _date);
        AddFormField(document, 1, 0, "Invoice.CustomerName", _customer);
        AddFormField(document, 1, 1, "POS.SessionNo", _session);
        header.Children.Add(document);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var content = new StackPanel();
        var totalPanel = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 2, 0, 6) };
        totalPanel.SetResourceReference(Border.BackgroundProperty, "SurfaceSecondaryBrush");
        _total.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        totalPanel.Child = _total;
        content.Children.Add(totalPanel);

        var tenders = CreateFormGrid(2);
        AddTenderField(tenders, 0, 0, "POS.TenderCash", _cashApplied);
        AddTenderField(tenders, 0, 1, "POS.TenderCashGiven", _cashTendered);
        AddTenderField(tenders, 1, 0, "POS.TenderCard", _cardApplied);
        AddTenderField(tenders, 1, 1, "POS.CardRef", _cardRef);
        AddTenderField(tenders, 2, 0, "POS.TenderOnAccount", _onAccount);
        content.Children.Add(tenders);

        _change.Margin = new Thickness(0, 4, 0, 0);
        content.Children.Add(_change);
        content.Children.Add(_remaining);
        // The middle band scrolls vertically: at the 372 minimum the
        // compacted content is ~374 DIP, so a few DIP of font-metric
        // variance degrades to a tiny scroll instead of clipping, and
        // the Complete/Cancel buttons (row 2) stay visible regardless.
        var contentScroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(contentScroll, 1);
        root.Children.Add(contentScroll);

        var actions = new StackPanel { Orientation = Orientation.Horizontal,  Margin = new Thickness(0, 6, 0, 0) };
        FlowAlign.End(actions);
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
        _customer.Text = string.IsNullOrWhiteSpace(_doc.PartyName) ? T("POS.WalkIn") : $"{_doc.PartyCode} {_doc.PartyName}".Trim();
        _session.Text = _sessionNo ?? T("POS.Sessionless");
        _total.Text = T("Invoice.Total") + ": " + _doc.GrandTotal.ToString("N2", CultureInfo.InvariantCulture) + " " + _doc.CurrencyCode;
        foreach (var box in new[] { _cashApplied, _cashTendered, _cardApplied, _onAccount })
            box.TextChanged += (_, _) => UpdateTenderMath();
    }

    private static void AddTenderField(Grid grid, int row, int pair, string labelKey, Control control)
    {
        AddFormField(grid, row, pair, labelKey, control);
    }

    private static void ConfigureButton(Button button, string key)
    {
        button.Content = T(key);
        button.Tag = key;
        button.MinWidth = ShouTech.App.Services.VisuId.ButtonMinWidth;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.SetResourceReference(StyleProperty, "VisuButtonStyle");
    }

    private static decimal Parse(TextBox box)
        => decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? value : 0m;

    private List<PosTender> BuildTenders()
    {
        var tenders = new List<PosTender>();
        var cash = Parse(_cashApplied);
        var card = Parse(_cardApplied);
        var account = Parse(_onAccount);
        if (cash > 0 || Parse(_cashTendered) > 0)
            tenders.Add(new PosTender
            {
                TenderType = PosTenderTypes.Cash, Amount = cash,
                TenderedAmount = Parse(_cashTendered) > 0 ? Parse(_cashTendered) : cash
            });
        if (card > 0)
            tenders.Add(new PosTender { TenderType = PosTenderTypes.Card, Amount = card, CardRef = _cardRef.Text.Trim() });
        if (account > 0)
            tenders.Add(new PosTender { TenderType = PosTenderTypes.OnAccount, Amount = account });
        return tenders;
    }

    private void UpdateTenderMath()
    {
        var applied = Parse(_cashApplied) + Parse(_cardApplied) + Parse(_onAccount);
        var change = Math.Max(0m, Parse(_cashTendered) - Parse(_cashApplied));
        ChangeDue = change;
        _change.Text = $"{T("POS.Change")}: {change:N2} {_doc.CurrencyCode}";
        var remaining = _doc.GrandTotal - applied;
        _remaining.Text = remaining == 0m
            ? T("POS.TendersBalanced")
            : $"{T("POS.TendersRemaining")}: {remaining:N2} {_doc.CurrencyCode}";
    }

    private void CompleteSale()
    {
        if (_doc.Lines.Count == 0) { ShowWarn("Invoice.NeedLines"); return; }
        var tenders = BuildTenders();
        try
        {
            PosTenderMath.ValidateSaleTenders(_doc.GrandTotal, tenders, _doc.PartyId is not null);
        }
        catch (AppError ex)
        {
            AppHost.Errors.Report(ex.Code, ex, "PosCheckoutWin.CompleteSale");
            MessageBox.Show(this, ex.Message, T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!AskConfirm("POS.ConfirmSale")) return;

        _complete.IsEnabled = false;
        try
        {
            var posted = PosPosting.CompleteSale(_doc, _sessionId, tenders, CurrentUserId,
                doc => BatchSerialCaptureSvc.CaptureForPosting(doc, this));
            if (posted.Status != InvDocStatus.Posted)
            {
                // Batch/serial capture was cancelled; the draft stands.
                _complete.IsEnabled = true;
                return;
            }
            PostedDoc = posted;
            // W9: a supervisor-approved over-limit discount is
            // audited against the POSTED document (migration 104,
            // pos_discount_overrides). A failed audit write is
            // reported centrally; the sale itself already posted.
            if (_discountApproval is not null)
            {
                try
                {
                    PosDiscountGuard.RecordOverride("POS", posted.DocNo, posted.DocId, _discountApproval);
                }
                catch (Exception auditEx)
                {
                    AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, auditEx, "PosCheckoutWin.RecordDiscountOverride");
                }
            }
            ShowInfo("POS.SalePosted");
            try
            {
                InvPrn.PrintPosReceipt(posted, tenders, ChangeDue, CurrentCompanyDisplay, _sessionNo, CurrentUserDisplay);
            }
            catch (Exception printEx)
            {
                AppHost.Errors.Report(ErrorCodes.WindowOperationFailed, printEx, "PosCheckoutWin.PrintReceipt");
            }
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
        _total.Text = T("Invoice.Total") + ": " + _doc.GrandTotal.ToString("N2", CultureInfo.InvariantCulture) + " " + _doc.CurrencyCode;
        _customer.Text = string.IsNullOrWhiteSpace(_doc.PartyName) ? T("POS.WalkIn") : $"{_doc.PartyCode} {_doc.PartyName}".Trim();
        _session.Text = _sessionNo ?? T("POS.Sessionless");
        UpdateTenderMath();
    }

    private static void RefreshLocalizedText(DependencyObject? parent)
    {
        if (parent is Button button && button.Tag is string buttonKey)
            button.Content = T(buttonKey);
        if (parent is TextBlock textBlock && textBlock.Tag is string textKey)
            textBlock.Text = T(textKey);
        if (parent == null) return;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshLocalizedText(VisualTreeHelper.GetChild(parent, i));
    }
}
