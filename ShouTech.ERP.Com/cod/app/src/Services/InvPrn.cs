using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using ShouTech.App.Reporting;
using ShouTech.App.Reporting.Invoices;

namespace ShouTech.App.Services;

/// <summary>
/// Invoice print / PDF pipeline. Renders a professional bilingual-capable
/// invoice visual then reuses <see cref="RptRend"/> / <see cref="PdfWrt"/>.
/// </summary>
public static class InvPrn
{
    public static void Print(InvDoc doc, string companyName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var visual = BuildPage(doc, companyName);
        var paginator = new InvDocumentPaginator(visual);
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, doc.DocNo);
    }

    public static void ExportPdf(InvDoc doc, string companyName)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = $"{doc.DocNo}.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        var visual = BuildPage(doc, companyName);
        visual.Measure(new Size(794, 1123)); // A4 at 96 DPI
        visual.Arrange(new Rect(0, 0, 794, 1123));
        visual.UpdateLayout();
        var jpeg = RptRend.RenderJpeg(visual, 94);
        PdfWrt.Write(dlg.FileName, new[] { jpeg }, 595.27, 841.89);
    }

    private static Border BuildPage(InvDoc doc, string companyName)
    {
        var root = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(32),
            Width = 794,
            Height = 1123
        };
        var stack = new StackPanel();
        root.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = companyName,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79))
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{doc.KindCode}  {doc.DocNo}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = doc.DocDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 12)
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{doc.PartyCode}  {doc.PartyName}",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var grid = new Grid();
        for (var c = 0; c < 6; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string[] headers = { "#", "Code", "Description", "Qty", "Price", "Total" };
        for (var c = 0; c < headers.Length; c++)
        {
            var h = new TextBlock
            {
                Text = headers[c],
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79)),
                Foreground = Brushes.White
            };
            Grid.SetColumn(h, c);
            Grid.SetRow(h, 0);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(h);
        }

        var rowIndex = 1;
        foreach (var line in doc.Lines)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var vals = new[]
            {
                line.LineNo.ToString(CultureInfo.InvariantCulture),
                line.MaterialCode,
                line.Description,
                line.Qty.ToString("N3", CultureInfo.InvariantCulture),
                line.UnitPrice.ToString("N2", CultureInfo.InvariantCulture),
                line.LineTotal.ToString("N2", CultureInfo.InvariantCulture),
            };
            for (var c = 0; c < vals.Length; c++)
            {
                var cell = new TextBlock
                {
                    Text = vals[c],
                    Padding = new Thickness(4),
                    Background = rowIndex % 2 == 0
                        ? new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFB))
                        : Brushes.Transparent
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            rowIndex++;
        }
        stack.Children.Add(grid);

        stack.Children.Add(new TextBlock
        {
            Text = $"Subtotal: {doc.Subtotal:N2}    Tax: {doc.TaxAmount:N2}    Total: {doc.GrandTotal:N2} {doc.CurrencyCode}",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        });

        if (!string.IsNullOrWhiteSpace(doc.Notes))
        {
            stack.Children.Add(new TextBlock
            {
                Text = doc.Notes,
                FontSize = 11,
                Opacity = 0.75,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return root;
    }

    // ---------------------------------------------------------------
    // POS receipt (Build POS 066): the till's thermal-width receipt —
    // store, bill, cashier/session, lines, totals, every tender and
    // the change. Direction follows the active language (LangDirSv),
    // so an Arabic till prints a fully RTL receipt. Labels come from
    // the POS.* language keys, never literals.
    // ---------------------------------------------------------------

    public static void PrintPosReceipt(
        InvDoc doc, IReadOnlyList<ShouTech.Domain.Entities.PosTender> tenders,
        decimal change, string companyName, string? sessionNo, string? cashier)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(tenders);
        var visual = BuildReceiptPage(doc, tenders, change, companyName, sessionNo, cashier);
        var paginator = new InvDocumentPaginator(visual) { PageSize = new Size(302, 1123) };
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, doc.DocNo);
    }

    private static string L(string key, string fallback)
    {
        var value = AppHost.Localization.GetString("POS." + key);
        return string.IsNullOrWhiteSpace(value) || value == "POS." + key ? fallback : value;
    }

    private static Border BuildReceiptPage(
        InvDoc doc, IReadOnlyList<ShouTech.Domain.Entities.PosTender> tenders,
        decimal change, string companyName, string? sessionNo, string? cashier)
    {
        var root = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(14),
            Width = 302,
            FlowDirection = LangDirSv.GetFlowDirection(AppHost.Localization.GetCurrentLanguage())
        };
        var stack = new StackPanel();
        root.Child = stack;

        void Line(string text, double size = 11, bool bold = false, bool center = false)
            => stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 1, 0, 1)
            });
        void Rule() => stack.Children.Add(new Border { Height = 1, Background = Brushes.Gray, Margin = new Thickness(0, 5, 0, 5) });
        string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture) + " " + doc.CurrencyCode;

        Line(companyName, 15, bold: true, center: true);
        Line(doc.Kind == InvDocKind.SalesReturn ? L("ReceiptReturnTitle", "POS RETURN") : L("ReceiptTitle", "POS RECEIPT"), 11, bold: true, center: true);
        Rule();
        Line($"{L("ReceiptNo", "Receipt")}: {doc.DocNo}");
        Line($"{L("ReceiptDate", "Date")}: {doc.DocDate:yyyy-MM-dd} {DateTime.Now:HH:mm}");
        if (!string.IsNullOrWhiteSpace(sessionNo)) Line($"{L("SessionNo", "Session")}: {sessionNo}");
        if (!string.IsNullOrWhiteSpace(cashier)) Line($"{L("Cashier", "Cashier")}: {cashier}");
        if (!string.IsNullOrWhiteSpace(doc.PartyName)) Line($"{L("ReceiptCustomer", "Customer")}: {doc.PartyCode} {doc.PartyName}");
        Rule();
        foreach (var line in doc.Lines)
        {
            Line($"{line.Description}", 11, bold: true);
            var discount = line.DiscountPct > 0 ? $"  -{line.DiscountPct:N1}%" : string.Empty;
            Line($"{line.Qty:N3} {line.Unit} x {line.UnitPrice:N2}{discount}    {line.LineTotal:N2}");
        }
        Rule();
        Line($"{L("Subtotal", "Subtotal")}: {Money(doc.Subtotal)}");
        if (doc.LinesDiscountAmount > 0) Line($"{L("ReceiptDiscount", "Discount")}: -{Money(doc.LinesDiscountAmount)}");
        if (doc.LinesExtraAmount > 0) Line($"{L("ReceiptExtra", "Extra")}: {Money(doc.LinesExtraAmount)}");
        Line($"{L("Tax", "Tax")}: {Money(doc.TaxAmount)}");
        Line($"{L("ReceiptTotal", "TOTAL")}: {Money(doc.GrandTotal)}", 13, bold: true);
        Rule();
        foreach (var tender in tenders)
        {
            var label = tender.TenderType switch
            {
                ShouTech.Domain.Entities.PosTenderTypes.Card => L("Card", "Card"),
                ShouTech.Domain.Entities.PosTenderTypes.OnAccount => L("OnAccount", "On account"),
                _ => L("Cash", "Cash")
            };
            var amount = tender.Amount < 0 ? -tender.Amount : tender.Amount;
            Line($"{label}: {Money(amount)}");
            if (tender.TenderedAmount is decimal handed && tender.TenderType == ShouTech.Domain.Entities.PosTenderTypes.Cash)
                Line($"{L("ReceiptTendered", "Tendered")}: {Money(handed)}");
            if (!string.IsNullOrWhiteSpace(tender.CardRef))
                Line($"{L("ReceiptCardRef", "Card ref")}: {tender.CardRef}");
        }
        if (change > 0) Line($"{L("ReceiptChange", "Change")}: {Money(change)}", 12, bold: true);
        Rule();
        Line(L("ReceiptThanks", "Thank you"), 11, bold: true, center: true);
        return root;
    }

    private sealed class InvDocumentPaginator : DocumentPaginator
    {
        private readonly FrameworkElement _page;
        public InvDocumentPaginator(FrameworkElement page) => _page = page;
        public override bool IsPageCountValid => true;
        public override int PageCount => 1;
        public override Size PageSize { get; set; } = new(794, 1123);
        public override IDocumentPaginatorSource Source => null!;
        public override DocumentPage GetPage(int pageNumber)
        {
            _page.Measure(PageSize);
            _page.Arrange(new Rect(PageSize));
            _page.UpdateLayout();
            return new DocumentPage(_page);
        }
    }
}
