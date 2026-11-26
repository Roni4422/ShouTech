using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ShouTech.App.Services;

/// <summary>
/// Minimal PDF 1.4 writer that embeds rendered pages as JPEG images.
///
/// Rationale: pages are rendered by WPF visuals first, which guarantees perfect
/// complex-script shaping (Arabic) and exact theme fidelity without shipping a
/// font-embedding/shaping engine. Output is print-grade (rendered at high DPI).
/// </summary>
public static class PdfWrt
{
    /// <summary>
    /// Writes a PDF document from pre-rendered JPEG pages.
    /// Page size is expressed in points (1/72 inch).
    /// </summary>
    public static void Write(string path, IReadOnlyList<byte[]> jpegPages, double pageWidthPt, double pageHeightPt)
    {
        if (jpegPages.Count == 0)
            throw new ArgumentException("At least one page is required.", nameof(jpegPages));

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            WriteDocument(fs, jpegPages, pageWidthPt, pageHeightPt);
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteDocument(Stream output, IReadOnlyList<byte[]> pages, double w, double h)
    {
        var offsets = new List<long>();
        var objects = new List<Action<Stream>>();

        // Object numbering: 1=Catalog, 2=Pages, then per page: content stream + image xobject + page.
        var pageCount = pages.Count;
        var catalogId = 1;
        var pagesId = 2;
        var nextId = 3;
        var pageObjIds = new List<(int pageId, int contentId, int imageId)>(pageCount);
        for (var i = 0; i < pageCount; i++)
        {
            // Serialization order per page: content stream, image xobject, page object.
            pageObjIds.Add((nextId + 2, nextId, nextId + 1));
            nextId += 3;
        }

        objects.Add(wr => WriteAscii(wr, "<< /Type /Catalog /Pages 2 0 R >>"));

        var kids = new StringBuilder();
        foreach (var p in pageObjIds)
            kids.Append(CultureInfo.InvariantCulture, $"{p.pageId} 0 R ");
        objects.Add(wr => WriteAscii(wr,
            $"<< /Type /Pages /Kids [ {kids}] /Count {pageCount} >>"));

        for (var i = 0; i < pageCount; i++)
        {
            // Copy the loop variable: the lambdas below execute after the loop,
            // and a for-loop variable is shared across iterations.
            var idx = i;
            var (pageId, contentId, imageId) = pageObjIds[i];
            var jpeg = pages[i];
            var jpegDims = JpegDimensions(jpeg, out var imgW, out var imgH);

            // Content stream: draw the image scaled to the full page.
            var content = $"q\n{F(w)} 0 0 {F(h)} 0 0 cm\n/Im{idx} Do\nQ\n";
            objects.Add(wr =>
            {
                WriteAscii(wr, $"<< /Length {content.Length} >>\nstream\n{content}endstream");
            });

            objects.Add(wr =>
            {
                WriteAscii(wr,
                    $"<< /Type /XObject /Subtype /Image /Width {imgW} /Height {imgH} " +
                    "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode " +
                    $"/Length {jpeg.Length} >>\nstream\n");
                wr.Write(jpeg, 0, jpeg.Length);
                WriteAscii(wr, "\nendstream");
            });

            objects.Add(wr => WriteAscii(wr,
                $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {F(w)} {F(h)}] " +
                $"/Resources << /XObject << /Im{idx} {imageId} 0 R >> >> " +
                $"/Contents {contentId} 0 R >>"));

            _ = jpegDims;
        }

        // Serialize with a cross-reference table.
        WriteAscii(output, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            WriteAscii(output, $"{i + 1} 0 obj\n");
            objects[i](output);
            WriteAscii(output, "\nendobj\n");
        }

        var xrefPos = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            WriteAscii(output, $"{off:0000000000} 00000 n \n");
        WriteAscii(output,
            $"trailer\n<< /Size {objects.Count + 1} /Root {catalogId} 0 R >>\n" +
            $"startxref\n{xrefPos}\n%%EOF");
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void WriteAscii(Stream s, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Parses the JPEG SOF marker to read pixel dimensions.</summary>
    private static bool JpegDimensions(byte[] jpeg, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return false;

        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            // SOF0..SOF15 except DHT/DAC/RST carry dimensions.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return true;
            }
            var segLen = (jpeg[i + 2] << 8) | jpeg[i + 3];
            i += 2 + segLen;
        }
        return false;
    }
}
