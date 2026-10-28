using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShouTech.App.Reporting;

/// <summary>
/// Renders report pages as WPF visuals and rasterizes them to JPEG.
/// Shared by the PDF export and by the print paginator so that the printed
/// page, the PDF page, and the on-screen report are pixel-consistent.
/// </summary>
public static class RptRend
{
    /// <summary>A4 at 96 DIP/inch.</summary>
    public static readonly Size PageSize = new(793.7, 1122.5);

    /// <summary>Render scale: 96 DIP -> 200 DPI raster.</summary>
    public const double RenderScale = 200.0 / 96.0;

    public sealed class PageModel
    {
        public required string CompanyName { get; init; }
        public required string ReportTitle { get; init; }
        public required string FilterSummary { get; init; }
        public required string GeneratedBy { get; init; }
        public required DateTime GeneratedAt { get; init; }
        public required IReadOnlyList<RptCol> Columns { get; init; }
        public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
        public required int PageIndex { get; init; }
        public required int PageCount { get; init; }
        public required bool Rtl { get; init; }
        public required Func<string, string> Translate { get; init; }
        public IReadOnlyDictionary<string, object?>? Totals { get; init; }
    }

    private const double Margin = 36;
    private const double RowHeight = 24;
    private const double HeaderHeight = 26;

    /// <summary>Rows that fit on one page below the report header.</summary>
    public static int RowsPerPage => 34;

    /// <summary>Builds the visual tree for a single page.</summary>
    public static FrameworkElement BuildPage(PageModel model)
    {
        var bg = Brush("BackgroundBrush", Brushes.White);
        var fg = Brush("TextBrush", Brushes.Black);
        var accent = Brush("AccentBrush", new SolidColorBrush(Color.FromRgb(0x1F, 0x4E, 0x79)));
        var zebra = ZebraBrush();

        var root = new Grid { Width = PageSize.Width, Height = PageSize.Height, Background = bg };
        if (model.Rtl)
            root.FlowDirection = FlowDirection.RightToLeft;

        var stack = new StackPanel { Margin = new Thickness(Margin) };
        root.Children.Add(stack);

        // ---- Report header ------------------------------------------------
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = model.CompanyName,
            FontSize = 13,
            Foreground = fg
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = model.ReportTitle,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            Margin = new Thickness(0, 2, 0, 2)
        });
        if (!string.IsNullOrWhiteSpace(model.FilterSummary))
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = model.FilterSummary,
                FontSize = 10,
                Foreground = fg,
                Opacity = 0.75
            });
        }
        headerGrid.Children.Add(titleStack);

        var meta = new TextBlock
        {
            FontSize = 9,
            Foreground = fg,
            Opacity = 0.75,
            TextAlignment = TextAlignment.Right,
            Text = $"{model.GeneratedAt:yyyy-MM-dd HH:mm}\n{model.GeneratedBy}"
        };
        Grid.SetColumn(meta, 1);
        headerGrid.Children.Add(meta);
        stack.Children.Add(headerGrid);

        stack.Children.Add(new Border { Height = 2, Background = accent, Margin = new Thickness(0, 0, 0, 8) });

        // ---- Table ---------------------------------------------------------
        var widths = ColumnWidths(model.Columns);
        stack.Children.Add(BuildRow(model.Columns, widths, null, accent, Brushes.White, bold: true, model));

        for (var i = 0; i < model.Rows.Count; i++)
        {
            var fill = (i % 2) == 1 ? zebra : Brushes.Transparent;
            stack.Children.Add(BuildRow(model.Columns, widths, model.Rows[i], fill, fg, bold: false, model));
        }

        // ---- Totals ---------------------------------------------------------
        if (model.Totals != null && model.PageIndex == model.PageCount - 1)
        {
            stack.Children.Add(BuildRow(model.Columns, widths, model.Totals,
                new SolidColorBrush(Color.FromArgb(0x22, 0x1F, 0x4E, 0x79)), fg, bold: true, model));
        }

        // ---- Footer ---------------------------------------------------------
        var footer = new TextBlock
        {
            FontSize = 9,
            Foreground = fg,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
            Text = $"{model.Translate("Report.Page")} {model.PageIndex + 1} / {model.PageCount}"
        };
        var footerHost = new Grid { Width = PageSize.Width, Height = PageSize.Height };
        footerHost.Children.Add(root);
        footerHost.Children.Add(footer);
        return footerHost;
    }

    /// <summary>Rasterizes a page visual to a JPEG byte array.</summary>
    public static byte[] RenderJpeg(FrameworkElement page, int quality = 92)
    {
        page.Measure(new Size(PageSize.Width, PageSize.Height));
        page.Arrange(new Rect(0, 0, PageSize.Width, PageSize.Height));
        page.UpdateLayout();

        var bmp = new RenderTargetBitmap(
            (int)(PageSize.Width * RenderScale),
            (int)(PageSize.Height * RenderScale),
            96 * RenderScale, 96 * RenderScale,
            PixelFormats.Pbgra32);
        bmp.Render(page);

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private static Grid BuildRow(
        IReadOnlyList<RptCol> columns,
        IReadOnlyList<GridLength> widths,
        IReadOnlyDictionary<string, object?>? values,
        Brush fill,
        Brush foreground,
        bool bold,
        PageModel model)
    {
        var grid = new Grid { Background = fill, Height = values == null ? HeaderHeight : RowHeight };
        for (var i = 0; i < columns.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = widths[i] });

        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            var text = values == null
                ? model.Translate(col.HeaderTextKey)
                : FormatCell(values.TryGetValue(col.Key, out var v) ? v : null, col);

            var block = new TextBlock
            {
                Text = text,
                FontSize = values == null ? 10.5 : 10,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = IsNumeric(col.Type) ? TextAlignment.Right : TextAlignment.Left
            };
            Grid.SetColumn(block, i);
            grid.Children.Add(block);
        }
        return grid;
    }

    /// <summary>Formats a cell value according to the column type.</summary>
    public static string FormatCell(object? value, RptCol col)
    {
        if (value == null)
            return string.Empty;

        var culture = CultureInfo.CurrentCulture;
        return col.Type switch
        {
            RptColType.Money or RptColType.FractionalNumber
                => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(col.Format ?? "N2", culture),
            RptColType.WholeNumber
                => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(col.Format ?? "N0", culture),
            RptColType.Percent
                => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(col.Format ?? "P1", culture),
            RptColType.Date
                => value is DateTime d ? d.ToString("yyyy-MM-dd", culture) : value.ToString() ?? string.Empty,
            RptColType.DateTime
                => value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm", culture) : value.ToString() ?? string.Empty,
            RptColType.Boolean
                => value is bool b && b ? "✓" : string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    public static bool IsNumeric(RptColType type)
        => type is RptColType.WholeNumber or RptColType.FractionalNumber or RptColType.Money or RptColType.Percent;

    private static List<GridLength> ColumnWidths(IReadOnlyList<RptCol> columns)
    {
        var total = columns.Sum(c => Math.Max(c.Width, 40));
        var available = PageSize.Width - Margin * 2;
        var result = new List<GridLength>(columns.Count);
        foreach (var c in columns)
        {
            var share = Math.Max(c.Width, 40) / total;
            result.Add(new GridLength(available * share, GridUnitType.Pixel));
        }
        return result;
    }

    private static SolidColorBrush Brush(string key, SolidColorBrush fallback)
	{
		var res = System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush;
		return res ?? fallback;
	}

    /// <summary>Alternating-row brush derived from the active theme surface color.</summary>
    public static SolidColorBrush ZebraBrush()
    {
        var surface = Brush("SurfaceBrush", new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC)));
        var c = surface.Color;
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        var factor = luminance > 0.5 ? 0.94 : 1.10;
        return new SolidColorBrush(Color.FromArgb(0xFF,
            Clamp(c.R * factor),
            Clamp(c.G * factor),
            Clamp(c.B * factor)));
    }

    private static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, (int)v));
}

/// <summary>
/// DocumentPaginator that feeds <see cref="RptRend"/> pages to the print pipeline.
/// </summary>
public sealed class RptPaginator : DocumentPaginator
{
    private readonly Func<int, FrameworkElement> _pageFactory;
    private readonly int _pageCount;

    public RptPaginator(int pageCount, Func<int, FrameworkElement> pageFactory)
    {
        _pageCount = Math.Max(1, pageCount);
        _pageFactory = pageFactory;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pageCount;
    public override Size PageSize { get; set; } = RptRend.PageSize;
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        var visual = _pageFactory(pageNumber);
        visual.Measure(PageSize);
        visual.Arrange(new Rect(PageSize));
        return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
    }
}
