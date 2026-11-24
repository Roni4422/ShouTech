// ========================================================================
// FILE: Reporting/LabelDesign.cs
// PROJECT: SHOUTECH ERP — Barcode Designer store (services phase B4)
// Named label designs per company (migration 117, label_designs):
// label size, symbology (Code 128 / Code 39), module/bar sizes,
// fonts, colors, visible elements. The JSON payload is also the
// encrypted .stld file content. The designer window's local file
// (barcode-design.cfg) remains the working fallback.
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
using ShouTech.App.Common;
using ShouTech.App.Services;
using ShouTech.Domain;

namespace ShouTech.App.Reporting;

/// <summary>One barcode-label design (the BcdDesWin design surface,
/// persisted). Color fields carry the designer's palette names.</summary>
public sealed class LabelDesign
{
    public const int CurrentVersion = 1;

    public const string SymbologyCode128 = "CODE128";
    public const string SymbologyCode39 = "CODE39";

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = string.Empty;

    /// <summary>CODE128 or CODE39.</summary>
    public string Symbology { get; set; } = SymbologyCode128;

    public double Width { get; set; } = 240;
    public double Height { get; set; } = 150;
    public double ModuleWidth { get; set; } = 2;
    public double BarHeight { get; set; } = 44;
    public string Font { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 13;
    public bool Bold { get; set; } = true;
    public string TextColor { get; set; } = "Black";
    public string BarColor { get; set; } = "Black";
    public string Background { get; set; } = "White";
    public bool ShowName { get; set; } = true;
    public bool ShowPrice { get; set; } = true;
    public bool ShowCode { get; set; } = true;

    /// <summary>Projects the design onto the sheet printer's style.</summary>
    public LabelStyle ToStyle() => new()
    {
        Symbology = Symbology,
        ModuleWidth = ModuleWidth,
        BarHeight = BarHeight,
        BarColorName = BarColor,
        BackgroundName = Background,
        TextColorName = TextColor,
        FontSize = FontSize,
        Bold = Bold,
        ShowName = ShowName,
        ShowPrice = ShowPrice,
        ShowCode = ShowCode
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses a design payload; null when unsupported
    /// (callers surface DSG-8103).</summary>
    public static LabelDesign? FromJson(string payloadJson)
    {
        LabelDesign? design;
        try
        {
            design = JsonSerializer.Deserialize<LabelDesign>(payloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (design is null || design.Version != CurrentVersion)
            return null;
        design.Symbology = string.Equals(design.Symbology, SymbologyCode39, StringComparison.OrdinalIgnoreCase)
            ? SymbologyCode39
            : SymbologyCode128;
        return design;
    }
}

/// <summary>One row of the label_designs list (designer window).</summary>
public sealed record LabelDesignListItem(int DesignID, string DesignName, bool IsDefault);

/// <summary>Persistence for label_designs (migration 117),
/// mirroring DocDesignRepo's provider handling.</summary>
public static class LabelDesignRepo
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

    public static async Task<IReadOnlyList<LabelDesignListItem>> ListAsync(
        Guid companyId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        var rows = await db.QueryAsync<LabelDesignListItem>(new CommandDefinition("""
            SELECT DesignID, DesignName, IsDefault
            FROM label_designs
            WHERE IsDeleted = 0 AND CompanyID = @companyId
            ORDER BY IsDefault DESC, DesignName
            """, new { companyId = companyId.ToString("D") }, cancellationToken: ct));
        return rows.ToList();
    }

    public static async Task<string?> LoadPayloadAsync(int designId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        return await db.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT PayloadJson FROM label_designs
            WHERE IsDeleted = 0 AND DesignID = @designId
            """, new { designId }, cancellationToken: ct));
    }

    /// <summary>The company's default label design, or null (the
    /// local working design / historic layout applies).</summary>
    public static async Task<LabelDesign?> LoadDefaultAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        var payload = await db.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT PayloadJson FROM label_designs
            WHERE IsDeleted = 0 AND CompanyID = @companyId AND IsDefault = 1
            ORDER BY DesignID DESC
            """, new { companyId = companyId.ToString("D") }, cancellationToken: ct));
        return payload is null ? null : LabelDesign.FromJson(payload);
    }

    public static async Task<int> SaveAsync(
        Guid companyId, LabelDesign design, bool makeDefault,
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
                UPDATE label_designs
                SET DesignName = @name, PayloadJson = @payload,
                    UpdatedBy = @user, UpdatedAt = {Now}
                WHERE DesignID = @updateId AND CompanyID = @company AND IsDeleted = 0
                """, new { name = design.Name, payload, user, updateId, company }, tx, cancellationToken: ct));
            if (updated == 0)
                throw new InvalidOperationException("The design no longer exists.");
            id = updateId;
        }
        else
        {
            id = await db.ExecuteScalarAsync<int>(new CommandDefinition($"""
                INSERT INTO label_designs (CompanyID, DesignName, IsDefault, PayloadJson, CreatedBy)
                VALUES (@company, @name, 0, @payload, @user)
                {(IsPg ? "RETURNING DesignID" : "; SELECT last_insert_rowid()")}
                """, new { company, name = design.Name, payload, user }, tx, cancellationToken: ct));
        }

        if (makeDefault)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                UPDATE label_designs SET IsDefault = 0
                WHERE CompanyID = @company AND IsDeleted = 0
                """, new { company }, tx, cancellationToken: ct));
            await db.ExecuteAsync(new CommandDefinition($"""
                UPDATE label_designs SET IsDefault = 1, UpdatedBy = @user, UpdatedAt = {Now}
                WHERE DesignID = @id
                """, new { user, id }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public static async Task SetDefaultAsync(
        int designId, Guid companyId, int userId, CancellationToken ct = default)
    {
        await using var db = Open();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var company = companyId.ToString("D");
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE label_designs SET IsDefault = 0
            WHERE CompanyID = @company AND IsDeleted = 0
            """, new { company }, tx, cancellationToken: ct));
        var updated = await db.ExecuteAsync(new CommandDefinition($"""
            UPDATE label_designs SET IsDefault = 1, UpdatedBy = @user, UpdatedAt = {Now}
            WHERE DesignID = @designId AND CompanyID = @company AND IsDeleted = 0
            """, new { user = userId.ToString(CultureInfo.InvariantCulture), designId, company },
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
            UPDATE label_designs
            SET IsDeleted = 1, IsDefault = 0, DeletedAt = {Now},
                DeletedBy = @user, UpdatedBy = @user, UpdatedAt = {Now}
            WHERE DesignID = @designId AND IsDeleted = 0
            """, new { user = userId.ToString(CultureInfo.InvariantCulture), designId },
            cancellationToken: ct));
    }

    /// <summary>
    /// Print-path resolver for the label sheets (BcdGenWin): the
    /// current company's default label design. Failures are reported
    /// centrally and degrade to null (historic layout) — printing
    /// never breaks because a design could not be loaded.
    /// </summary>
    public static async Task<LabelDesign?> ResolveDefaultAsync(string companyCode)
    {
        try
        {
            var companies = await CoLocalStore.LoadAsync();
            var rec = companies.FirstOrDefault(c =>
                string.Equals(c.Code, companyCode, StringComparison.OrdinalIgnoreCase));
            return rec is null ? null : await LoadDefaultAsync(rec.CompanyId);
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "LabelDesignRepo.ResolveDefaultAsync");
            return null;
        }
    }
}
