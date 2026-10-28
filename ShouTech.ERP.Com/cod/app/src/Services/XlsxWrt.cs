using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ShouTech.App.Services;

/// <summary>
/// Dependency-free XLSX writer (Open Packaging Conventions, inline strings).
/// Produces a professional worksheet: frozen styled header row, zebra striping,
/// column widths, auto filter, RTL sheet view when requested.
/// No third-party packages required; safe for offline enterprise deployments.
/// </summary>
public static class XlsxWrt
{
    public sealed class Col
    {
        public required string Header { get; init; }
        public double Width { get; init; } = 18;
        public bool Numeric { get; init; }
    }

    /// <summary>
    /// Writes a worksheet. Rows must align with <paramref name="cols"/> order.
    /// Values supported: string, numeric primitives, DateTime, bool, null.
    /// </summary>
    public static void Write(
        string path,
        string sheetName,
        IReadOnlyList<Col> cols,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        bool rtl,
        string? hiddenHeader = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddEntry(zip, "[Content_Types].xml", ContentTypes());
            AddEntry(zip, "_rels/.rels", RootRels());
            AddEntry(zip, "xl/workbook.xml", Workbook(sheetName, rtl));
            AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
            AddEntry(zip, "xl/styles.xml", Styles());
            AddEntry(zip, "docProps/core.xml", CoreProps());
            AddEntry(zip, "xl/worksheets/sheet1.xml", Sheet(cols, rows, rtl, hiddenHeader));
        }
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------------
    // Parts
    // ------------------------------------------------------------------

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ContentTypes() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
          <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
        </Types>
        """;

    private static string RootRels() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
        </Relationships>
        """;

    private static string Workbook(string sheetName, bool rtl) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                  xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>
            <sheet name="{Xml(sheetName)}" sheetId="1" r:id="rId1"/>
          </sheets>
        </workbook>
        """;

    private static string WorkbookRels() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private static string CoreProps() =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                           xmlns:dc="http://purl.org/dc/elements/1.1/"
                           xmlns:dcterms="http://purl.org/dc/terms/"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <dc:creator>ShouTech ERP</dc:creator>
          <cp:lastModifiedBy>ShouTech ERP</cp:lastModifiedBy>
          <dcterms:created xsi:type="dcterms:W3CDTF">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created>
        </cp:coreProperties>
        """;

    // Style indexes used by the sheet:
    //   0 default | 1 header | 2 zebra | 3 money | 4 money+zebra
    private static string Styles() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.00"/></numFmts>
          <fonts count="2">
            <font><sz val="11"/><name val="Segoe UI"/></font>
            <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Segoe UI"/></font>
          </fonts>
          <fills count="4">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E79"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFF2F6FB"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border>
              <left style="thin"><color rgb="FFB9C6D4"/></left>
              <right style="thin"><color rgb="FFB9C6D4"/></right>
              <top style="thin"><color rgb="FFB9C6D4"/></top>
              <bottom style="thin"><color rgb="FFB9C6D4"/></bottom>
              <diagonal/>
            </border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1"/>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1">
              <alignment horizontal="center" vertical="center" wrapText="1"/>
            </xf>
            <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyFill="1" applyBorder="1"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="164" fontId="0" fillId="3" borderId="1" xfId="0" applyNumberFormat="1" applyFill="1" applyBorder="1"/>
          </cellXfs>
        </styleSheet>
        """;

    private static string Sheet(IReadOnlyList<Col> cols, IReadOnlyList<IReadOnlyList<object?>> rows, bool rtl, string? hiddenHeader)
    {
        var hasHiddenHeader = !string.IsNullOrEmpty(hiddenHeader);
        var columnCount = cols.Count + (hasHiddenHeader ? 1 : 0);
        var sb = new StringBuilder(64_000);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append(rtl ? "<sheetViews><sheetView rightToLeft=\"1\" workbookViewId=\"0\">" : "<sheetViews><sheetView workbookViewId=\"0\">");
        sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");

        sb.Append("<cols>");
        for (var i = 0; i < cols.Count; i++)
        {
            var width = Math.Max(8, Math.Min(cols[i].Width, 60));
            sb.Append(CultureInfo.InvariantCulture,
                $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{width:0.#}\" customWidth=\"1\"/>");
        }
        if (hasHiddenHeader)
            sb.Append(CultureInfo.InvariantCulture,
                $"<col min=\"{columnCount}\" max=\"{columnCount}\" width=\"0\" hidden=\"1\" customWidth=\"1\"/>");
        sb.Append("</cols>");

        sb.Append("<sheetData>");

        // Header row
        sb.Append("<row r=\"1\" ht=\"24\" customHeight=\"1\">");
        for (var c = 0; c < cols.Count; c++)
            AppendCell(sb, 1, c, cols[c].Header, style: 1);
        if (hasHiddenHeader)
            AppendCell(sb, 1, cols.Count, hiddenHeader, style: 1);
        sb.Append("</row>");

        // Data rows with zebra striping
        for (var r = 0; r < rows.Count; r++)
        {
            var zebra = (r % 2) == 1;
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 2}\">");
            var row = rows[r];
            for (var c = 0; c < cols.Count; c++)
            {
                var value = c < row.Count ? row[c] : null;
                var numeric = cols[c].Numeric && IsNumeric(value);
                var style = numeric ? (zebra ? 4 : 3) : (zebra ? 2 : 0);
                AppendCell(sb, r + 2, c, value, style);
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        if (columnCount > 0 && rows.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<autoFilter ref=\"A1:{ColName(columnCount - 1)}{rows.Count + 1}\"/>");
        }

        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static void AppendCell(StringBuilder sb, int row, int col, object? value, int style)
    {
        var reference = $"{ColName(col)}{row}";
        switch (value)
        {
            case null:
                sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" s=\"{style}\"/>");
                break;
            case string s:
                sb.Append(CultureInfo.InvariantCulture,
                    $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(s)}</t></is></c>");
                break;
            case bool b:
                sb.Append(CultureInfo.InvariantCulture,
                    $"<c r=\"{reference}\" s=\"{style}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                break;
            case DateTime dt:
                sb.Append(CultureInfo.InvariantCulture,
                    $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t>{Xml(dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}</t></is></c>");
                break;
            case IFormattable f when IsNumeric(value):
                sb.Append(CultureInfo.InvariantCulture,
                    $"<c r=\"{reference}\" s=\"{style}\"><v>{f.ToString(null, CultureInfo.InvariantCulture)}</v></c>");
                break;
            case IFormattable f:
                sb.Append(CultureInfo.InvariantCulture,
                    $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(f.ToString(null, CultureInfo.InvariantCulture))}</t></is></c>");
                break;
            default:
                sb.Append(CultureInfo.InvariantCulture,
                    $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t>{Xml(value.ToString() ?? string.Empty)}</t></is></c>");
                break;
        }
    }

    private static bool IsNumeric(object? value)
        => value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal => true,
            float number => float.IsFinite(number),
            double number => double.IsFinite(number),
            _ => false
        };

    private static string ColName(int index)
    {
        // 0 -> A, 25 -> Z, 26 -> AA ...
        var name = string.Empty;
        var i = index;
        do
        {
            name = (char)('A' + (i % 26)) + name;
            i = i / 26 - 1;
        } while (i >= 0);
        return name;
    }

    private static string Xml(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
               .Replace("\"", "&quot;").Replace("'", "&apos;");
}
