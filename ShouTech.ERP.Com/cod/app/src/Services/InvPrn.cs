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
