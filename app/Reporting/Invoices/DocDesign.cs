// ========================================================================
// FILE: Reporting/Invoices/DocDesign.cs
// PROJECT: SHOUTECH ERP — Invoice Designer (services phase B3)
// Banded print-layout designs for the six printable document kinds,
// their JSON transfer shape (the .stid payload) and the doc_designs
// store (migration 116). Model choice (see SERVICES_GAP_INVENTORY):
// banded designer (FastReport/Stimulsoft style) over Phenix's
// fixed-form editor — one engine, per-kind designs.
// ========================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Reporting.Invoices;

/// <summary>The six document kinds that carry print designs.</summary>
public enum DocDesignKind
{
    SalesInvoice,
    PurchaseInvoice,
    SalesReturn,
    PurchaseReturn,
    StockEntry,
    StockExit
}

public static class DocDesignKinds
{
    public static string Key(DocDesignKind kind) => kind switch
    {
        DocDesignKind.SalesInvoice => "SALES_INVOICE",
        DocDesignKind.PurchaseInvoice => "PURCHASE_INVOICE",
        DocDesignKind.SalesReturn => "SALES_RETURN",
        DocDesignKind.PurchaseReturn => "PURCHASE_RETURN",
        DocDesignKind.StockEntry => "STOCK_ENTRY",
        DocDesignKind.StockExit => "STOCK_EXIT",
        _ => "SALES_INVOICE"
    };

    public static IReadOnlyList<DocDesignKind> All { get; } = Enum.GetValues<DocDesignKind>();

    /// <summary>Maps a runtime document kind to its design kind;
    /// null when the kind has no designed print path (POS receipts,
    /// transfers, counts and quotations keep their own layouts).</summary>
    public static DocDesignKind? ForDocument(InvDocKind kind) => kind switch
    {
        InvDocKind.SalesInvoice or InvDocKind.PosSale => DocDesignKind.SalesInvoice,
        InvDocKind.SalesReturn => DocDesignKind.SalesReturn,
        InvDocKind.PurchaseInvoice => DocDesignKind.PurchaseInvoice,
        InvDocKind.PurchaseReturn => DocDesignKind.PurchaseReturn,
        InvDocKind.StockIn => DocDesignKind.StockEntry,
        InvDocKind.StockOut => DocDesignKind.StockExit,
        _ => null
    };

    /// <summary>The InvDocKind a design kind prints (for previews).</summary>
    public static InvDocKind DocumentKind(DocDesignKind kind) => kind switch
    {
        DocDesignKind.SalesInvoice => InvDocKind.SalesInvoice,
        DocDesignKind.PurchaseInvoice => InvDocKind.PurchaseInvoice,
        DocDesignKind.SalesReturn => InvDocKind.SalesReturn,
        DocDesignKind.PurchaseReturn => InvDocKind.PurchaseReturn,
        DocDesignKind.StockEntry => InvDocKind.StockIn,
        DocDesignKind.StockExit => InvDocKind.StockOut,
        _ => InvDocKind.SalesInvoice
    };
}

/// <summary>One column of the lines band: an InvLine field, its
/// printed caption and its relative width weight.</summary>
public sealed class DocDesignColumn
{
    public string FieldKey { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public double Width { get; set; } = 1;
    public bool Visible { get; set; } = true;
}

/// <summary>
/// A banded invoice print design. Bands: company header, party,
/// document meta, lines, totals (+ tax), footer elements (amount in
/// words, notes, barcode, signatures) — each band toggled, the lines
/// band's columns chosen/reordered/resized. Serialized as JSON into
/// doc_designs.PayloadJson and into the encrypted .stid files.
/// </summary>
public sealed class DocDesign
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = string.Empty;

    public bool ShowCompanyAddress { get; set; }
    public bool ShowPartyBlock { get; set; } = true;
    public bool ShowDocMeta { get; set; } = true;
    public bool ShowTaxBlock { get; set; } = true;
    public bool ShowAmountInWords { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowSignatures { get; set; }
    public bool ShowBarcode { get; set; }

    /// <summary>Lines-band header fill (CSS hex).</summary>
    public string HeaderBackColor { get; set; } = "#51708E";

    public List<DocDesignColumn> Columns { get; set; } = DefaultColumns();

    public static List<DocDesignColumn> DefaultColumns() => new()
    {
        new() { FieldKey = "LineNo", Caption = "#", Width = 0.5 },
        new() { FieldKey = "MaterialCode", Caption = "Code", Width = 1 },
        new() { FieldKey = "Description", Caption = "Description", Width = 2.5 },
        new() { FieldKey = "Qty", Caption = "Qty", Width = 0.8 },
        new() { FieldKey = "UnitPrice", Caption = "Price", Width = 1 },
        new() { FieldKey = "LineTotal", Caption = "Total", Width = 1.2 }
    };

    /// <summary>The built-in default: today's InvPrn layout as a
    /// design (same bands and columns; the header accent follows the
    /// app default slate instead of the legacy pinned blue).</summary>
    public static DocDesign BuiltInDefault() => new()
    {
        Name = "Built-in",
        ShowCompanyAddress = false,
        ShowPartyBlock = true,
        ShowDocMeta = false,
        ShowTaxBlock = true,
        ShowAmountInWords = false,
        ShowNotes = true,
        ShowSignatures = false,
        ShowBarcode = false,
        HeaderBackColor = "#51708E",
        Columns = DefaultColumns()
    };

    // ── Line field catalog ─────────────────────────────────────────
    public sealed record LineField(string Key, string DefaultCaption, bool IsNumeric);

    public static IReadOnlyList<LineField> LineFields { get; } = new[]
    {
        new LineField("LineNo", "#", true),
        new LineField("MaterialCode", "Code", false),
        new LineField("Description", "Description", false),
        new LineField("Unit", "Unit", false),
        new LineField("Qty", "Qty", true),
        new LineField("UnitPrice", "Price", true),
        new LineField("DiscountPct", "Disc %", true),
        new LineField("TaxRate", "Tax %", true),
        new LineField("LineSubtotal", "Subtotal", true),
        new LineField("LineTax", "Tax", true),
        new LineField("LineTotal", "Total", true),
        new LineField("WarehouseName", "Warehouse", false)
    };

    public static IReadOnlyList<string> LineFieldKeys { get; } =
        LineFields.Select(f => f.Key).ToList();

    public static bool IsKnownField(string fieldKey)
        => LineFields.Any(f => string.Equals(f.Key, fieldKey, StringComparison.OrdinalIgnoreCase));

    public static bool IsNumericField(string fieldKey)
        => LineFields.FirstOrDefault(f => string.Equals(f.Key, fieldKey, StringComparison.OrdinalIgnoreCase))?.IsNumeric ?? false;

    /// <summary>Formats one line value for a design column. Digit
    /// counts and display rules come from the program-wide Options
    /// settings (NumFmt.Current, OPTWIN_SECTIONS §3.2) — quantities
    /// use the quantity digits, prices/money the price digits,
    /// percents one digit; culture stays invariant (Western digits
    /// are mandatory program-wide).</summary>
    public static string FormatLineValue(InvLine line, string fieldKey)
    {
        var inv = CultureInfo.InvariantCulture;
        return fieldKey switch
        {
            "LineNo" => line.LineNo.ToString(inv),
            "MaterialCode" => line.MaterialCode,
            "Description" => line.Description,
            "Unit" => line.Unit,
            "Qty" => NumFmt.FormatQty(line.Qty),
            "UnitPrice" => NumFmt.FormatPrice(line.UnitPrice),
            "DiscountPct" => NumFmt.FormatPercent(line.DiscountPct),
            "TaxRate" => NumFmt.FormatPercent(line.TaxRate),
                "LineSubtotal" => NumFmt.FormatMoney(line.LineSubtotal),
            "LineTax" => NumFmt.FormatMoney(line.LineTax),
            "LineTotal" => NumFmt.FormatMoney(line.LineTotal),
            "WarehouseName" => line.WarehouseName,
            _ => string.Empty
        };
    }

    // ── JSON transfer (doc_designs payload + .stid files) ─────────
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a design payload; null when the payload is
    /// not a supported design (callers surface DSG-8103).</summary>
    public static DocDesign? FromJson(string payloadJson)
    {
        DocDesign? design;
        try
        {
            design = JsonSerializer.Deserialize<DocDesign>(payloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (design is null || design.Version != CurrentVersion || design.Columns.Count == 0)
            return null;
        if (design.Columns.Any(c => !IsKnownField(c.FieldKey)))
            return null;
        return design;
    }
}

/// <summary>One row of the doc_designs list (designer window).</summary>
public sealed record DocDesignListItem(int DesignID, string DesignName, bool IsDefault);

/// <summary>Persistence for doc_designs (migration 116), mirroring
/// ReportRepo's provider handling (SQLite + PostgreSQL).</summary>
public static class DocDesignRepo
{
    private static bool IsPg => DatabaseRuntime.IsPostgreSql();

    private static DbConnection Open()
    {
        var cs = DatabaseRuntime.GetConnectionString()
            ?? throw new InvalidOperationException("Database connection is not configured.");
        return IsPg
            ? new NpgsqlConnection(cs)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(cs));
    }

    private static string Now => IsPg ? "NOW()" : "CURRENT_TIMESTAMP";

    public static async Task<IReadOnlyList<DocDesignListItem>> ListAsync(
        Guid companyId, string kindKey, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        var rows = await db.QueryAsync<DocDesignListItem>(new CommandDefinition("""
            SELECT DesignID, DesignName, IsDefault
            FROM doc_designs
            WHERE IsDeleted = 0 AND CompanyID = @companyId AND DocKind = @kindKey
            ORDER BY IsDefault DESC, DesignName
            """, new { companyId = companyId.ToString("D"), kindKey }, cancellationToken: ct));
        return rows.ToList();
    }

    public static async Task<string?> LoadPayloadAsync(int designId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        return await db.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT PayloadJson FROM doc_designs
            WHERE IsDeleted = 0 AND DesignID = @designId
            """, new { designId }, cancellationToken: ct));
    }

    /// <summary>The company's default design for a document kind,
    /// or null (the built-in layout applies).</summary>
    public static async Task<DocDesign?> LoadDefaultAsync(
        Guid companyId, InvDocKind docKind, CancellationToken ct = default)
    {
        if (DocDesignKinds.ForDocument(docKind) is not { } designKind)
            return null;
        await using var db = Open();
        await db.OpenAsync(ct);
        var payload = await db.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT PayloadJson FROM doc_designs
            WHERE IsDeleted = 0 AND CompanyID = @companyId AND DocKind = @kindKey AND IsDefault = 1
            ORDER BY DesignID DESC
            """, new { companyId = companyId.ToString("D"), kindKey = DocDesignKinds.Key(designKind) },
            cancellationToken: ct));
        return payload is null ? null : DocDesign.FromJson(payload);
    }

    public static async Task<int> SaveAsync(
        Guid companyId, string kindKey, DocDesign design, bool makeDefault,
        int? existingId, int userId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var user = userId.ToString(CultureInfo.InvariantCulture);
        var company = companyId.ToString("D");
        var payload = design.ToJson();
        int id;

        if (existingId is int updateId)
        {
            var updated = await db.ExecuteAsync(new CommandDefinition($"""
                UPDATE doc_designs
                SET DesignName = @name, PayloadJson = @payload,
                    UpdatedBy = @user, UpdatedAt = {Now}
                WHERE DesignID = @updateId AND CompanyID = @company
                  AND DocKind = @kindKey AND IsDeleted = 0
                """, new { name = design.Name, payload, user, updateId, company, kindKey }, tx, cancellationToken: ct));
            if (updated == 0)
                throw new InvalidOperationException("The design no longer exists.");
            id = updateId;
        }
        else
        {
            id = await db.ExecuteScalarAsync<int>(new CommandDefinition($"""
                INSERT INTO doc_designs (CompanyID, DocKind, DesignName, IsDefault, PayloadJson, CreatedBy)
                VALUES (@company, @kindKey, @name, 0, @payload, @user)
                {(IsPg ? "RETURNING DesignID" : "; SELECT last_insert_rowid()")}
                """, new { company, kindKey, name = design.Name, payload, user }, tx, cancellationToken: ct));
        }

        if (makeDefault)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE doc_designs SET IsDefault = 0
                WHERE CompanyID = @company AND DocKind = @kindKey AND IsDeleted = 0
                """, new { company, kindKey }, tx, cancellationToken: ct));
            await db.ExecuteAsync(new CommandDefinition($"""
                UPDATE doc_designs SET IsDefault = 1, UpdatedBy = @user, UpdatedAt = {Now}
                WHERE DesignID = @id
                """, new { user, id }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public static async Task SetDefaultAsync(
        int designId, Guid companyId, string kindKey, int userId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var company = companyId.ToString("D");
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE doc_designs SET IsDefault = 0
            WHERE CompanyID = @company AND DocKind = @kindKey AND IsDeleted = 0
            """, new { company, kindKey }, tx, cancellationToken: ct));
        var updated = await db.ExecuteAsync(new CommandDefinition($"""
            UPDATE doc_designs SET IsDefault = 1, UpdatedBy = @user, UpdatedAt = {Now}
            WHERE DesignID = @designId AND CompanyID = @company AND DocKind = @kindKey AND IsDeleted = 0
            """, new { user = userId.ToString(CultureInfo.InvariantCulture), designId, company, kindKey },
            tx, cancellationToken: ct));
        if (updated == 0)
            throw new InvalidOperationException("The design no longer exists.");
        await tx.CommitAsync(ct);
    }

    public static async Task DeleteAsync(int designId, int userId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition($"""
            UPDATE doc_designs
            SET IsDeleted = 1, IsDefault = 0, DeletedAt = {Now},
                DeletedBy = @user, UpdatedBy = @user, UpdatedAt = {Now}
            WHERE DesignID = @designId AND IsDeleted = 0
            """, new { user = userId.ToString(CultureInfo.InvariantCulture), designId },
            cancellationToken: ct));
    }

    /// <summary>
    /// Print-path resolver: the current company's default design for
    /// the document's kind plus its address block, for InvPrn's
    /// callers (InvoiceWin). Any failure is reported centrally and
    /// degrades to the built-in layout — printing never breaks
    /// because a design could not be loaded.
    /// </summary>
    public static async Task<(DocDesign? Design, string? CompanyAddress)> ResolveForPrintAsync(
        InvDocKind docKind, string companyCode)
    {
        try
        {
            var companies = await CoLocalStore.LoadAsync();
            var rec = companies.FirstOrDefault(c =>
                string.Equals(c.Code, companyCode, StringComparison.OrdinalIgnoreCase));
            if (rec is null)
                return (null, null);

            var cityCountry = string.Join(", ",
                new[] { rec.City, rec.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var parts = new[] { rec.AddressLine1, rec.AddressLine2, cityCountry }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var address = string.Join("\n", parts);
            var design = await LoadDefaultAsync(rec.CompanyId, docKind);
            return (design, address.Length == 0 ? null : address);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "DocDesignRepo.ResolveForPrintAsync");
            return (null, null);
        }
    }
}
