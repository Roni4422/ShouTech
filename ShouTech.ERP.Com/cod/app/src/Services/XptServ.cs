using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ShouTech.App.Reporting;

namespace ShouTech.App.Services;

/// <summary>
/// Unified export service for every report window.
///
/// Channels:
///   - Excel  : professional styled XLSX via <see cref="XlsxWrt"/> (no dependencies).
///   - PDF    : page-perfect PDF via <see cref="RptRend"/> + <see cref="PdfWrt"/>.
///   - Data   : encrypted internal container (.shx) for branch-to-branch exchange,
///              openable only by another licensed ShouTech ERP installation.
///   - Print  : paginated through <see cref="RptPaginator"/>.
/// </summary>
public static class XptServ
{
    /// <summary>Everything needed to render/export a report page set.</summary>
    public sealed class ExportModel
    {
        public required string ReportKey { get; init; }
        public required string Title { get; init; }
        public required string CompanyName { get; init; }
        public required string GeneratedBy { get; init; }
        public required string FilterSummary { get; init; }
        public required IReadOnlyList<RptCol> Columns { get; init; }
        public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
        public IReadOnlyDictionary<string, object?>? Totals { get; init; }
        public required bool Rtl { get; init; }
        public required Func<string, string> Translate { get; init; }
        public string? DatasetIdentityHeader { get; init; }
    }

    // ------------------------------------------------------------------
    // Excel
    // ------------------------------------------------------------------

    public static bool ExportExcel(Window owner, ExportModel model)
    {
        var path = AskSavePath(owner, model, "xlsx", "Excel Files (*.xlsx)|*.xlsx");
        if (path == null)
            return false;

        try
        {
            var cols = model.Columns.Select(c => new XlsxWrt.Col
            {
                Header = model.Translate(c.HeaderTextKey),
                Width = Math.Max(10, c.Width / 7.0),
                Numeric = RptRend.IsNumeric(c.Type)
            }).ToList();

            var rows = model.Rows
                .Select(r => (IReadOnlyList<object?>)model.Columns
                    .Select(c => r.TryGetValue(c.Key, out var v) ? v : null)
                    .ToList())
                .ToList();

            if (model.Totals != null)
            {
                rows.Add(model.Columns
                    .Select(c => model.Totals.TryGetValue(c.Key, out var v) ? v : null)
                    .ToList());
            }

            XlsxWrt.Write(path, model.Title, cols, rows, model.Rtl, model.DatasetIdentityHeader);
            NotifySuccess(owner, path);
            return true;
        }
        catch (Exception ex)
        {
            NotifyFailure(owner, ex);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // PDF
    // ------------------------------------------------------------------

    public static bool ExportPdf(Window owner, ExportModel model)
    {
        var path = AskSavePath(owner, model, "pdf", "PDF Files (*.pdf)|*.pdf");
        if (path == null)
            return false;

        try
        {
            const int pdfColumnsPerPage = 8;
            var columnGroups = model.Columns.Chunk(pdfColumnsPerPage).ToArray();
            var pages = new List<byte[]>();
            for (var i = 0; i < columnGroups.Length; i++)
            {
                var pageModel = new ExportModel
                {
                    ReportKey = model.ReportKey,
                    Title = columnGroups.Length == 1
                        ? model.Title
                        : $"{model.Title} ({i + 1}/{columnGroups.Length})",
                    CompanyName = model.CompanyName,
                    GeneratedBy = model.GeneratedBy,
                    FilterSummary = model.FilterSummary,
                    Columns = columnGroups[i],
                    Rows = model.Rows,
                    Totals = model.Totals,
                    Rtl = model.Rtl,
                    Translate = model.Translate
                };
                pages.AddRange(RenderPages(pageModel));
            }
            PdfWrt.Write(path, pages, RptRend.PageSize.Width * 72.0 / 96.0, RptRend.PageSize.Height * 72.0 / 96.0);
            NotifySuccess(owner, path);
            return true;
        }
        catch (Exception ex)
        {
            NotifyFailure(owner, ex);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Encrypted branch data (.shx)
    // ------------------------------------------------------------------

    public static bool ExportEncrypted(Window owner, ExportModel model)
    {
        var path = AskSavePath(owner, model, "shx", "ShouTech Data (*.shx)|*.shx");
        if (path == null)
            return false;

        try
        {
            var payload = new
            {
                report = model.ReportKey,
                company = model.CompanyName,
                generatedBy = model.GeneratedBy,
                generatedAt = DateTime.UtcNow,
                filters = model.FilterSummary,
                columns = model.Columns.Select(c => new
                {
                    c.Key,
                    c.HeaderTextKey,
                    type = c.Type switch
                    {
                        RptColType.WholeNumber => "Integer",
                        RptColType.FractionalNumber => "Decimal",
                        _ => c.Type.ToString()
                    }
                }),
                rows = model.Rows
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(payload);
            XptCry.EncryptToFile(path, json, XptCry.KindGenericData);
            NotifySuccess(owner, path);
            return true;
        }
        catch (Exception ex)
        {
            NotifyFailure(owner, ex);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Print
    // ------------------------------------------------------------------

    public static void Print(Window owner, ExportModel model)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;

        var pageCount = PageCount(model);
        var paginator = new RptPaginator(pageCount, i =>
            RptRend.BuildPage(BuildPageModel(model, i, pageCount)));
        dialog.PrintDocument(paginator, $"{model.Title} - {model.CompanyName}");
    }

    // ------------------------------------------------------------------
    // Page rendering shared by PDF and print
    // ------------------------------------------------------------------

    private static int PageCount(ExportModel model)
        => Math.Max(1, (int)Math.Ceiling(model.Rows.Count / (double)RptRend.RowsPerPage));

    private static RptRend.PageModel BuildPageModel(ExportModel model, int pageIndex, int pageCount)
    {
        var rows = model.Rows
            .Skip(pageIndex * RptRend.RowsPerPage)
            .Take(RptRend.RowsPerPage)
            .ToList();

        return new RptRend.PageModel
        {
            CompanyName = model.CompanyName,
            ReportTitle = model.Title,
            FilterSummary = model.FilterSummary,
            GeneratedBy = model.GeneratedBy,
            GeneratedAt = DateTime.Now,
            Columns = model.Columns,
            Rows = rows,
            PageIndex = pageIndex,
            PageCount = pageCount,
            Rtl = model.Rtl,
            Translate = model.Translate,
            Totals = model.Totals
        };
    }

    private static List<byte[]> RenderPages(ExportModel model)
    {
        var count = PageCount(model);
        var pages = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
            pages.Add(RptRend.RenderJpeg(RptRend.BuildPage(BuildPageModel(model, i, count))));
        return pages;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string? AskSavePath(Window owner, ExportModel model, string ext, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"{Sanitize(model.Title)}_{DateTime.Now:yyyyMMdd_HHmm}.{ext}"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static void NotifySuccess(Window owner, string path)
    {
        var app = System.Windows.Application.Current as App;
        var loc = app?.Localization;
        var text = string.Format(CultureInfo.CurrentCulture,
            loc?.GetString("Export.Success") ?? "Exported: {0}", path);
        MessageBox.Show(owner, text, loc?.GetString("Common.Info") ?? string.Empty,
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void NotifyFailure(Window owner, Exception exception)
    {
        var app = System.Windows.Application.Current as App;
        var loc = app?.Localization;
        MessageBox.Show(owner,
            $"{loc?.GetString("Export.Failed") ?? "Export failed."}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
            loc?.GetString("Common.Error") ?? string.Empty,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
