using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using ShouTech.App.Common;
using ShouTech.App.Configuration;
using ShouTech.App.Reporting;
using ShouTech.App.Reporting.Invoices;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>
/// Invoice print / PDF pipeline. Renders a professional bilingual-capable
/// invoice visual then reuses <see cref="RptRend"/> / <see cref="PdfWrt"/>.
/// </summary>
public static class InvPrn
{
    public static void Print(InvDoc doc, string companyName, DocDesign? design = null, string? companyAddress = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var visual = BuildPage(doc, companyName, design, companyAddress,
            PrintSetSvc.ResolveHeader(), ResolveExtras(doc));
        var paginator = new InvDocumentPaginator(visual);
        var dlg = new PrintDialog();
        int copies = 1;
        try { copies = UsrPref.Load().PrintInvoiceCopies; }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "InvPrn.Print.Copies");
        }
        PrintSetSvc.ApplyDialogSettings(dlg, copies);
        if (dlg.ShowDialog() == true)
            dlg.PrintDocument(paginator, doc.DocNo);
    }

    /// <summary>Resolves the Options-governed extras for one
    /// document (print settings §3.3): previous/current party
    /// balance from party_ledger (excluding this document's own
    /// rows for "previous") and the posting-user line. Any failure
    /// degrades to no extras — printing never breaks over them.</summary>
    private static PrintSetSvc.PrintExtras? ResolveExtras(InvDoc doc)
    {
        try
        {
            var settings = PrintSetSvc.Load();
            decimal? previous = null, current = null;
            var partyKind = doc.Kind switch
            {
                InvDocKind.SalesInvoice or InvDocKind.SalesReturn or InvDocKind.PosSale => "C",
                InvDocKind.PurchaseInvoice or InvDocKind.PurchaseReturn => "S",
                _ => null
            };
            if ((settings.PrintPreviousBalance || settings.PrintCurrentBalance) &&
                partyKind is not null && doc.PartyId is long partyId)
            {
                var (curr, prev) = PrintSetSvc.PartyBalances(
                    partyKind, partyId, doc.KindCode, doc.DocId);
                if (settings.PrintPreviousBalance) previous = prev;
                if (settings.PrintCurrentBalance) current = curr;
            }
            var postedBy = settings.PrintUserOnInvoice
                ? (!string.IsNullOrWhiteSpace(doc.PostedBy)
                    ? doc.PostedBy
                    : AppState.CurrentUser?.Username)
                : null;
            if (previous is null && current is null && string.IsNullOrWhiteSpace(postedBy))
                return null;
            return new PrintSetSvc.PrintExtras(previous, current, postedBy);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "InvPrn.ResolveExtras");
            return null;
        }
    }

    public static void ExportPdf(InvDoc doc, string companyName, DocDesign? design = null, string? companyAddress = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = $"{doc.DocNo}.pdf"
        };
        if (dlg.ShowDialog() != true) return;

        var visual = BuildPage(doc, companyName, design, companyAddress,
            PrintSetSvc.ResolveHeader(), ResolveExtras(doc));
        visual.Measure(new Size(794, 1123)); // A4 at 96 DPI
        visual.Arrange(new Rect(0, 0, 794, 1123));
        visual.UpdateLayout();
        var jpeg = RptRend.RenderJpeg(visual, 94);
        PdfWrt.Write(dlg.FileName, new[] { jpeg }, 595.27, 841.89);
    }

    /// <summary>Designer preview entry point (InvDesWin): renders a
    /// document through the exact production page builder.</summary>
    public static FrameworkElement BuildPreviewPage(
        InvDoc doc, string companyName, DocDesign design, string? companyAddress = null)
        => BuildPage(doc, companyName, design, companyAddress);

    private static SolidColorBrush ParseBrush(string? hex, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex.Trim()));
            }
            catch (FormatException)
            {
                // fall through to the fallback color
            }
        }
        return new SolidColorBrush(fallback);
    }

    /// <summary>
    /// Banded page builder (B3): header band (company + optional
    /// address), title/meta, party band, lines band (the design's
    /// columns), totals band (+ tax), then the footer elements the
    /// design enables (amount in words, notes, barcode, signatures).
    /// A null design uses DocDesign.BuiltInDefault — the historic
    /// layout as a design.
    /// </summary>
    private static Border BuildPage(InvDoc doc, string companyName, DocDesign? design, string? companyAddress,
        PrintSetSvc.PrintHeader? header = null, PrintSetSvc.PrintExtras? extras = null)
    {
        design ??= DocDesign.BuiltInDefault();
        var accent = ParseBrush(design.HeaderBackColor, Color.FromRgb(0x51, 0x70, 0x8E));
        var zebra = new SolidColorBrush(Color.FromArgb(0x16, accent.Color.R, accent.Color.G, accent.Color.B));

        var root = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(32),
            Width = 794,
            Height = 1123
        };
        var stack = new StackPanel();
        root.Child = stack;

        // ── Header band ──
        // When the Options-governed header resolves (print settings,
        // §3.3), it replaces the historic name+address pair: optional
        // logo, then the composed lines (first line = company name).
        // A null header keeps the historic layout byte-for-byte.
        if (header is not null)
        {
            if (header.LogoData is { Length: > 0 } logoBytes)
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    using (var stream = new System.IO.MemoryStream(logoBytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                    }
                    bitmap.Freeze();
                    stack.Children.Add(new System.Windows.Controls.Image
                    {
                        Source = bitmap,
                        Height = 56,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 0, 0, 6)
                    });
                }
                catch (Exception ex)
                {
                    AppHost.Errors.Report(ErrorCodes.NonFatalInternalFailure, ex, "InvPrn.BuildPage.Logo");
                }
            }
            for (var i = 0; i < header.Lines.Count; i++)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = header.Lines[i],
                    FontSize = i == 0 ? 18 : 10.5,
                    FontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = i == 0 ? accent : Brushes.Black,
                    Opacity = i == 0 ? 1 : 0.8,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = companyName,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = accent
            });
            if (design.ShowCompanyAddress && !string.IsNullOrWhiteSpace(companyAddress))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = companyAddress,
                    FontSize = 10.5,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        stack.Children.Add(new TextBlock
        {
            Text = $"{doc.KindCode}  {doc.DocNo}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = NumFmt.FormatDate(doc.DocDate),
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // ── Party band ──
        if (design.ShowPartyBlock && !string.IsNullOrWhiteSpace(doc.PartyName + doc.PartyCode))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{doc.PartyCode}  {doc.PartyName}",
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            });
            // Options-governed balance lines (§3.3): previous =
            // party_ledger balance before this document, current =
            // including it; resolved by ResolveExtras, formatted by
            // the program-wide NumFmt.
            if (extras?.PreviousBalance is { } previousBalance)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"Previous balance: {NumFmt.FormatMoney(previousBalance)} {doc.CurrencyCode}",
                    FontSize = 11,
                    Opacity = 0.85,
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }
            if (extras?.CurrentBalance is { } currentBalance)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"Current balance: {NumFmt.FormatMoney(currentBalance)} {doc.CurrencyCode}",
                    FontSize = 11,
                    Opacity = 0.85,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
        }

        // ── Document meta band ──
        if (design.ShowDocMeta)
        {
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(doc.WarehouseName)) meta.Add($"Warehouse: {doc.WarehouseName}");
            if (!string.IsNullOrWhiteSpace(doc.TargetWarehouseName)) meta.Add($"To: {doc.TargetWarehouseName}");
            if (!string.IsNullOrWhiteSpace(doc.Reference)) meta.Add($"Ref: {doc.Reference}");
            if (!string.IsNullOrWhiteSpace(doc.BranchName)) meta.Add($"Branch: {doc.BranchName}");
            if (!string.IsNullOrWhiteSpace(doc.SalesRepName)) meta.Add($"Sales rep: {doc.SalesRepName}");
            meta.Add($"Currency: {doc.CurrencyCode}");
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("    ", meta),
                FontSize = 11,
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        // ── Lines band ──
        var columns = design.Columns.Where(c => c.Visible).ToList();
        if (columns.Count == 0)
            columns = DocDesign.DefaultColumns();
        var grid = new Grid();
        foreach (var col in columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.1, col.Width), GridUnitType.Star)
            });

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < columns.Count; c++)
        {
            var h = new TextBlock
            {
                Text = columns[c].Caption,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(4),
                Background = accent,
                Foreground = Brushes.White,
                TextAlignment = DocDesign.IsNumericField(columns[c].FieldKey)
                    ? TextAlignment.Right : TextAlignment.Left
            };
            Grid.SetColumn(h, c);
            Grid.SetRow(h, 0);
            grid.Children.Add(h);
        }

        var rowIndex = 1;
        foreach (var line in doc.Lines)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = new TextBlock
                {
                    Text = DocDesign.FormatLineValue(line, columns[c].FieldKey),
                    Padding = new Thickness(4),
                    Background = rowIndex % 2 == 0 ? zebra : Brushes.Transparent,
                    TextAlignment = DocDesign.IsNumericField(columns[c].FieldKey)
                        ? TextAlignment.Right : TextAlignment.Left
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            rowIndex++;
        }
        stack.Children.Add(grid);

        // ── Totals band ──
        var totalsText = design.ShowTaxBlock
            ? $"Subtotal: {NumFmt.FormatMoney(doc.Subtotal)}    Tax: {NumFmt.FormatMoney(doc.TaxAmount)}    Total: {NumFmt.FormatMoney(doc.GrandTotal)} {doc.CurrencyCode}"
            : $"Total: {NumFmt.FormatMoney(doc.GrandTotal)} {doc.CurrencyCode}";
        stack.Children.Add(new TextBlock
        {
            Text = totalsText,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        });

        if (design.ShowAmountInWords)
        {
            stack.Children.Add(new TextBlock
            {
                Text = NumWords.ToWords(doc.GrandTotal, doc.CurrencyCode),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (design.ShowNotes && !string.IsNullOrWhiteSpace(doc.Notes))
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

        // ── Barcode element: the document number as Code-128 (the
        // repo's encoder; QR has no encoder in the repo and is not
        // faked — see SERVICES_GAP_INVENTORY). ──
        if (design.ShowBarcode && !string.IsNullOrWhiteSpace(doc.DocNo))
        {
            var barcodeStack = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            barcodeStack.Children.Add(Code128.BuildVisual(doc.DocNo, moduleWidth: 1.6, barHeight: 40));
            barcodeStack.Children.Add(new TextBlock
            {
                Text = doc.DocNo,
                FontSize = 10,
                Margin = new Thickness(12, 2, 0, 0)
            });
            stack.Children.Add(barcodeStack);
        }

        // ── Signature blocks ──
        if (design.ShowSignatures)
        {
            var sigGrid = new Grid { Margin = new Thickness(0, 36, 0, 0) };
            for (var c = 0; c < 3; c++)
                sigGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string[] labels = { "Prepared by", "Approved by", "Received by" };
            for (var c = 0; c < labels.Length; c++)
            {
                var cell = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
                cell.Children.Add(new Border { Height = 1, Background = Brushes.Gray });
                cell.Children.Add(new TextBlock
                {
                    Text = labels[c],
                    FontSize = 10.5,
                    Opacity = 0.8,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                Grid.SetColumn(cell, c);
                sigGrid.Children.Add(cell);
            }
            stack.Children.Add(sigGrid);
        }

        // ── Posting-user line (Options-governed, §3.3) ──
        if (!string.IsNullOrWhiteSpace(extras?.PostedBy))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"Posted by: {extras.PostedBy}",
                FontSize = 10,
                Opacity = 0.7,
                Margin = new Thickness(0, 18, 0, 0)
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
        string Money(decimal value) => NumFmt.FormatMoney(value) + " " + doc.CurrencyCode;

        Line(companyName, 15, bold: true, center: true);
        Line(doc.Kind == InvDocKind.SalesReturn ? L("ReceiptReturnTitle", "POS RETURN") : L("ReceiptTitle", "POS RECEIPT"), 11, bold: true, center: true);
        Rule();
        Line($"{L("ReceiptNo", "Receipt")}: {doc.DocNo}");
        Line($"{L("ReceiptDate", "Date")}: {NumFmt.FormatDate(doc.DocDate)} {NumFmt.FormatTimeOfDay(DateTime.Now)}");
        if (!string.IsNullOrWhiteSpace(sessionNo)) Line($"{L("SessionNo", "Session")}: {sessionNo}");
        if (!string.IsNullOrWhiteSpace(cashier)) Line($"{L("Cashier", "Cashier")}: {cashier}");
        if (!string.IsNullOrWhiteSpace(doc.PartyName)) Line($"{L("ReceiptCustomer", "Customer")}: {doc.PartyCode} {doc.PartyName}");
        Rule();
        foreach (var line in doc.Lines)
        {
            Line($"{line.Description}", 11, bold: true);
            var discount = line.DiscountPct > 0 ? $"  -{NumFmt.FormatPercent(line.DiscountPct)}%" : string.Empty;
            Line($"{NumFmt.FormatQty(line.Qty)} {line.Unit} x {NumFmt.FormatPrice(line.UnitPrice)}{discount}    {NumFmt.FormatMoney(line.LineTotal)}");
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
