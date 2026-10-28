// ========================================================================
// FILE: Services/Inventory.cs
// PROJECT: SHOUTECH ERP - Inventory & Material master
// Merged: InventoryModels, IInvServ, InvServ, InvRepo, InventoryRepositoryFactory, MatRepo
// ========================================================================

#nullable enable

using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Common;
using ShouTech.App.Views;
using System.IO;

namespace ShouTech.App.Services
{

    // ========== InventoryModels.cs ==========
public sealed record InventoryItem(
    int Id,
    string Code,
    string Name,
    string? Name2,
    int CategoryId,
    string Unit,
    decimal Cost,
    decimal Price,
    bool IsActive);

    // ========== IInvServ.cs ==========
public interface IInvServ
    {
        // Items
        Task<Guid> CreateItemAsync(string code, string name, string? name2, Guid categoryId,
            string unit, decimal cost, decimal price, string userId, CancellationToken ct = default);

        Task UpdateItemAsync(Guid id, string name, string? name2, Guid categoryId,
            string unit, decimal cost, decimal price, string userId, CancellationToken ct = default);

        Task SoftDeleteItemAsync(Guid id, string userId, CancellationToken ct = default);

        Task<bool> ItemCodeExistsAsync(string code, Guid? excludeId = null, CancellationToken ct = default);

        // Stock
        Task<Guid> PostStockInAsync(Guid warehouseId, Guid itemId, decimal qty, decimal cost,
            string? refDoc, string userId, CancellationToken ct = default);

        Task<Guid> PostStockOutAsync(Guid warehouseId, Guid itemId, decimal qty,
            string? refDoc, string userId, CancellationToken ct = default);

        Task<decimal> GetAvailableQtyAsync(Guid warehouseId, Guid itemId, CancellationToken ct = default);
    }

    // ========== InvServ.cs ==========
/// <summary>
    /// Enterprise Inventory service.
    /// Protected by License (CanWrite + Module INV) and Permissions.
    /// </summary>
    public sealed class InvServ : IInvServ
    {
        private readonly ILicServ _lic;
        private readonly IPermServ _perm;
        // private readonly IInvRepo _repo; // to be wired with the data layer

        public InvServ(ILicServ lic, IPermServ perm)
        {
            _lic = lic ?? throw new ArgumentNullException(nameof(lic));
            _perm = perm ?? throw new ArgumentNullException(nameof(perm));
        }

        public async Task<Guid> CreateItemAsync(
            string code, string name, string? name2, Guid categoryId,
            string unit, decimal cost, decimal price, string userId,
            CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Inv, ct);

            if (!_perm.Has(PermKey.InvCreate))
                throw new UnauthorizedAccessException("You do not have permission to create items.");

            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("Item code is required.");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Item name is required.");
            if (cost < 0 || price < 0)
                throw new ArgumentException("Cost and Price cannot be negative.");

            // Operational code paths use IInvRepo (SqliteInvRepo/SqlInvRepo) directly.
            // This service is a legacy validation facade; it must not return a fake
            // identifier that makes an unexecuted operation look successful.
            throw new NotSupportedException(
                "InvServ is a validation-only legacy facade. " +
                "Use IInvRepo (SqliteInvRepo/SqlInvRepo) for persistence operations.");
        }

        public async Task UpdateItemAsync(
            Guid id, string name, string? name2, Guid categoryId,
            string unit, decimal cost, decimal price, string userId,
            CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Inv, ct);

            if (!_perm.Has(PermKey.InvCreate))
                throw new UnauthorizedAccessException("You do not have permission to update items.");

            if (id == Guid.Empty)
                throw new ArgumentException("Invalid item id.");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Item name is required.");
        }

        public async Task SoftDeleteItemAsync(Guid id, string userId, CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Inv, ct);

            if (!_perm.Has(PermKey.InvDelete))
                throw new UnauthorizedAccessException("You do not have permission to delete items.");

            if (id == Guid.Empty)
                throw new ArgumentException("Invalid item id.");
        }

        public async Task<bool> ItemCodeExistsAsync(string code, Guid? excludeId = null, CancellationToken ct = default)
        {
            await _lic.EnsureModuleAsync(ModKey.Inv, ct);

            // Must not return a fake false - it could allow duplicate item codes.
            // The real query is available via IInvRepo.ItemCodeExistsAsync.
            throw new NotSupportedException(
                "InvServ is a validation-only legacy facade. " +
                "Use IInvRepo.ItemCodeExistsAsync for the real query.");
        }

        public async Task<Guid> PostStockInAsync(
            Guid warehouseId, Guid itemId, decimal qty, decimal cost,
            string? refDoc, string userId, CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Inv, ct);

            if (!_perm.Has(PermKey.InvMove))
                throw new UnauthorizedAccessException("You do not have permission to post stock movements.");

            if (qty <= 0)
                throw new ArgumentException("Quantity must be greater than zero.");
            if (cost < 0)
                throw new ArgumentException("Cost cannot be negative.");

            throw new NotSupportedException(
                "InvServ is a validation-only legacy facade. " +
                "Use IInvRepo.PostStockInAsync for the real posting.");
        }

        public async Task<Guid> PostStockOutAsync(
            Guid warehouseId, Guid itemId, decimal qty,
            string? refDoc, string userId, CancellationToken ct = default)
        {
            await _lic.EnsureCanWriteAsync(ct);
            await _lic.EnsureModuleAsync(ModKey.Inv, ct);

            if (!_perm.Has(PermKey.InvMove))
                throw new UnauthorizedAccessException("You do not have permission to post stock movements.");

            if (qty <= 0)
                throw new ArgumentException("Quantity must be greater than zero.");

            var available = await GetAvailableQtyAsync(warehouseId, itemId, ct);
            if (available < qty)
                throw new InvalidOperationException("Insufficient stock quantity.");

            throw new NotSupportedException(
                "InvServ is a validation-only legacy facade. " +
                "Use IInvRepo.PostStockOutAsync for the real posting.");
        }

        public async Task<decimal> GetAvailableQtyAsync(Guid warehouseId, Guid itemId, CancellationToken ct = default)
        {
            await _lic.EnsureModuleAsync(ModKey.Inv, ct);

            // Returning silent 0 would block every stock out with "insufficient".
            // The real query is available via IInvRepo.
            throw new NotSupportedException(
                "InvServ is a validation-only legacy facade. " +
                "Use IInvRepo.GetAvailableQtyAsync for the real query.");
        }
    }

    // ========== InvRepo.cs ==========
public interface IInvRepo
{
    Task<IReadOnlyList<InventoryItem>> GetItemsAsync(string? search, Guid companyId, CancellationToken ct);
    Task<InventoryItem?> GetItemAsync(int id, Guid companyId, CancellationToken ct);
    Task<int> CreateItemAsync(string code, string name, string? name2, int categoryId,
        string unit, decimal cost, decimal price, Guid companyId, Guid userId, CancellationToken ct);
    Task UpdateItemAsync(int id, string name, string? name2, int categoryId, string unit,
        decimal cost, decimal price, Guid companyId, Guid userId, CancellationToken ct);
    Task SoftDeleteItemAsync(int id, Guid companyId, Guid userId, CancellationToken ct);
    Task<bool> ItemCodeExistsAsync(string code, int? excludeId, Guid companyId, CancellationToken ct);
    Task<long> PostStockInAsync(long warehouseId, int itemId, decimal qty, decimal cost,
        string? reference, Guid companyId, Guid userId, CancellationToken ct);
    Task<long> PostStockOutAsync(long warehouseId, int itemId, decimal qty,
        string? reference, Guid companyId, Guid userId, CancellationToken ct);
    Task<decimal> GetAvailableQtyAsync(long warehouseId, int itemId, Guid companyId, CancellationToken ct);
}

/// <summary>PostgreSQL implementation backed by the inventory tables.</summary>
public sealed class SqlInvRepo : IInvRepo
{
    private readonly string _connectionString;
    public SqlInvRepo(string connectionString)
        => _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A database connection string is required.", nameof(connectionString))
            : connectionString;

    private NpgsqlConnection OpenConnection() => new(_connectionString);

    public async Task<IReadOnlyList<InventoryItem>> GetItemsAsync(string? search, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            SELECT p.product_id Id, p.product_code Code, p.product_name_ar Name, p.product_name_en Name2,
                   p.category_id CategoryId, u.uom_code Unit, p.cost_price Cost, p.default_sale_price Price, p.is_active IsActive
            FROM products p INNER JOIN units_of_measure u ON u.uom_id=p.primary_uom_id
            WHERE p.company_id=@companyId AND p.is_deleted=false
              AND (@search IS NULL OR p.product_code ILIKE '%' || @search || '%' OR p.product_name_ar ILIKE '%' || @search || '%')
            ORDER BY p.product_code LIMIT 1000
            """;
        var rows = await db.QueryAsync<InventoryItem>(new CommandDefinition(sql, new { companyId, search = string.IsNullOrWhiteSpace(search) ? null : search.Trim() }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<InventoryItem?> GetItemAsync(int id, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            SELECT p.product_id Id, p.product_code Code, p.product_name_ar Name, p.product_name_en Name2,
                   p.category_id CategoryId, u.uom_code Unit, p.cost_price Cost, p.default_sale_price Price, p.is_active IsActive
            FROM products p INNER JOIN units_of_measure u ON u.uom_id=p.primary_uom_id
            WHERE p.product_id=@id AND p.company_id=@companyId AND p.is_deleted=false LIMIT 1
            """;
        return await db.QuerySingleOrDefaultAsync<InventoryItem>(new CommandDefinition(sql, new { id, companyId }, cancellationToken: ct));
    }

    public async Task<int> CreateItemAsync(string code, string name, string? name2, int categoryId,
        string unit, decimal cost, decimal price, Guid companyId, Guid userId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            INSERT INTO products
                (company_id, product_code, product_name_ar, product_name_en, category_id, primary_uom_id,
                 default_sale_price, default_purchase_price, cost_price, created_by)
            SELECT @companyId, @code, @name, @name2, @categoryId, u.uom_id,
                   @price, @cost, @cost, @userId
            FROM units_of_measure u
            WHERE u.company_id = @companyId AND u.uom_code = @unit AND u.is_deleted = false AND u.is_active = true
            RETURNING product_id;
            """;
        var id = await db.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new
        { companyId, code, name, name2, categoryId, unit, cost, price, userId }, cancellationToken: ct));
        return id ?? throw new InvalidOperationException("The specified unit was not found for the company.");
    }

    public async Task UpdateItemAsync(int id, string name, string? name2, int categoryId, string unit,
        decimal cost, decimal price, Guid companyId, Guid userId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            UPDATE products p SET product_name_ar=@name, product_name_en=@name2, category_id=@categoryId,
                primary_uom_id=u.uom_id, cost_price=@cost, default_sale_price=@price,
                default_purchase_price=@cost, updated_by=@userId, updated_at=now()
            FROM units_of_measure u
            WHERE p.product_id=@id AND p.company_id=@companyId AND p.is_deleted=false
              AND u.company_id=p.company_id AND u.uom_code=@unit AND u.is_deleted=false AND u.is_active=true;
            """;
        var affected = await db.ExecuteAsync(new CommandDefinition(sql,
            new { id, name, name2, categoryId, unit, cost, price, companyId, userId }, cancellationToken: ct));
        if (affected != 1) throw new KeyNotFoundException("Inventory item was not found.");
    }

    public async Task SoftDeleteItemAsync(int id, Guid companyId, Guid userId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            UPDATE products SET is_deleted=true, is_active=false, deleted_at=now(),
                deleted_by=@userId WHERE product_id=@id AND company_id=@companyId AND is_deleted=false;
            """;
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { id, companyId, userId }, cancellationToken: ct));
        if (affected != 1) throw new KeyNotFoundException("Inventory item was not found.");
    }

    public async Task<bool> ItemCodeExistsAsync(string code, int? excludeId, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM products WHERE company_id=@companyId AND product_code=@code
                    AND is_deleted=false AND (@excludeId IS NULL OR product_id<>@excludeId)
            ) THEN 1 ELSE 0 END)
            """;
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition(sql,
            new { code, excludeId, companyId }, cancellationToken: ct));
    }

    public Task<long> PostStockInAsync(long warehouseId, int itemId, decimal qty, decimal cost,
        string? reference, Guid companyId, Guid userId, CancellationToken ct)
        => ExecuteMovementAsync(true, warehouseId, itemId, qty, cost, reference, companyId, userId, ct);

    public Task<long> PostStockOutAsync(long warehouseId, int itemId, decimal qty,
        string? reference, Guid companyId, Guid userId, CancellationToken ct)
        => ExecuteMovementAsync(false, warehouseId, itemId, qty, null, reference, companyId, userId, ct);

    private async Task<long> ExecuteMovementAsync(bool increase, long warehouseId, int itemId, decimal qty,
        decimal? cost, string? reference, Guid companyId, Guid userId, CancellationToken ct)
    {
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "Quantity must be positive.");
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var delta = increase ? qty : -qty;
        var movementType = increase ? "PURCHASE" : "SALE";
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO stock_balances (company_id, warehouse_id, product_id, available_qty, last_cost, last_updated, is_deleted)
            VALUES (@companyId, @warehouseId, @itemId, @delta, @cost, now(), false)
            ON CONFLICT (company_id, warehouse_id, product_id) DO UPDATE SET available_qty=stock_balances.available_qty + EXCLUDED.available_qty, last_cost=COALESCE(EXCLUDED.last_cost, stock_balances.last_cost), last_updated=now()
            """, new { companyId, warehouseId, itemId, delta, cost }, tx, cancellationToken: ct));
        if (!increase && await db.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT available_qty FROM stock_balances WHERE company_id=@companyId AND warehouse_id=@warehouseId AND product_id=@itemId FOR UPDATE",
            new { companyId, warehouseId, itemId }, tx, cancellationToken: ct)) < 0)
            throw new InvalidOperationException("Insufficient available stock.");
        var id = await db.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO stock_movements
                (company_id, movement_type, warehouse_id_to, product_id, quantity, unit_cost,
                 reference_table, notes, created_by)
            VALUES (@companyId, @movementType, @warehouseId, @itemId, @qty, @cost,
                    'Inventory', @reference, @userId)
            RETURNING movement_id
            """, new { companyId, movementType, warehouseId, itemId, qty, cost, reference, userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<decimal> GetAvailableQtyAsync(long warehouseId, int itemId, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = "SELECT COALESCE(available_qty, 0) FROM stock_balances WHERE company_id=@companyId AND warehouse_id=@warehouseId AND product_id=@itemId AND is_deleted=false";
        return await db.ExecuteScalarAsync<decimal?>(new CommandDefinition(sql,
            new { companyId, warehouseId, itemId }, cancellationToken: ct)) ?? 0m;
    }
}

public sealed class SqliteInvRepo : IInvRepo
{
    private readonly string _connectionString;
    public SqliteInvRepo(string connectionString)
        => _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A database connection string is required.", nameof(connectionString))
            : connectionString;

    private SqliteConnection OpenConnection() => new(_connectionString);

    public async Task<IReadOnlyList<InventoryItem>> GetItemsAsync(string? search, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            SELECT p.product_id Id, p.product_code Code, p.product_name_ar Name, p.product_name_en Name2,
                   p.category_id CategoryId, u.uom_code Unit, p.cost_price Cost, p.default_sale_price Price, p.is_active IsActive
            FROM products p INNER JOIN units_of_measure u ON u.uom_id=p.primary_uom_id
            WHERE p.company_id=@companyId AND p.is_deleted=0
              AND (@search IS NULL OR p.product_code LIKE '%' || @search || '%' OR p.product_name_ar LIKE '%' || @search || '%')
            ORDER BY p.product_code LIMIT 1000
            """;
        var rows = await db.QueryAsync<InventoryItem>(new CommandDefinition(sql, new { companyId = companyId.ToString(), search = string.IsNullOrWhiteSpace(search) ? null : search.Trim() }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<InventoryItem?> GetItemAsync(int id, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            SELECT p.product_id Id, p.product_code Code, p.product_name_ar Name, p.product_name_en Name2,
                   p.category_id CategoryId, u.uom_code Unit, p.cost_price Cost, p.default_sale_price Price, p.is_active IsActive
            FROM products p INNER JOIN units_of_measure u ON u.uom_id=p.primary_uom_id
            WHERE p.product_id=@id AND p.company_id=@companyId AND p.is_deleted=0 LIMIT 1
            """;
        return await db.QuerySingleOrDefaultAsync<InventoryItem>(new CommandDefinition(sql, new { id, companyId = companyId.ToString() }, cancellationToken: ct));
    }

    public async Task<int> CreateItemAsync(string code, string name, string? name2, int categoryId,
        string unit, decimal cost, decimal price, Guid companyId, Guid userId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var uomId = await db.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT uom_id FROM units_of_measure
            WHERE company_id=@companyId AND uom_code=@unit AND is_deleted=0 AND is_active=1
            LIMIT 1
            """, new { companyId = companyId.ToString(), unit }, tx, cancellationToken: ct));
        if (!uomId.HasValue)
            throw new InvalidOperationException("The specified unit was not found for the company.");

        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO products
                (company_id, product_code, product_name_ar, product_name_en, category_id, primary_uom_id,
                 default_sale_price, default_purchase_price, cost_price, created_by)
            VALUES (@companyId, @code, @name, @name2, @categoryId, @primaryUomId,
                    @price, @cost, @cost, @userId)
            """, new
        {
            companyId = companyId.ToString(),
            code,
            name,
            name2,
            categoryId,
            primaryUomId = uomId.Value,
            price,
            cost,
            userId = userId.ToString()
        }, tx, cancellationToken: ct));
        var id = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return checked((int)id);
    }

    public async Task UpdateItemAsync(int id, string name, string? name2, int categoryId, string unit,
        decimal cost, decimal price, Guid companyId, Guid userId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var uomId = await db.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT uom_id FROM units_of_measure
            WHERE company_id=@companyId AND uom_code=@unit AND is_deleted=0 AND is_active=1
            LIMIT 1
            """, new { companyId = companyId.ToString(), unit }, tx, cancellationToken: ct));
        if (!uomId.HasValue)
            throw new InvalidOperationException("The specified unit was not found for the company.");

        var affected = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE products
            SET product_name_ar=@name,
                product_name_en=@name2,
                category_id=@categoryId,
                primary_uom_id=@primaryUomId,
                cost_price=@cost,
                default_sale_price=@price,
                default_purchase_price=@cost,
                updated_by=@userId,
                updated_at=CURRENT_TIMESTAMP
            WHERE product_id=@id AND company_id=@companyId AND is_deleted=0
            """, new
        {
            id,
            name,
            name2,
            categoryId,
            primaryUomId = uomId.Value,
            cost,
            price,
            companyId = companyId.ToString(),
            userId = userId.ToString()
        }, tx, cancellationToken: ct));
        if (affected != 1) throw new KeyNotFoundException("Inventory item was not found.");
        await tx.CommitAsync(ct);
    }

    public async Task SoftDeleteItemAsync(int id, Guid companyId, Guid userId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            UPDATE products SET is_deleted=1, is_active=0, deleted_at=CURRENT_TIMESTAMP,
                deleted_by=@userId WHERE product_id=@id AND company_id=@companyId AND is_deleted=0;
            """;
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { id, companyId = companyId.ToString(), userId = userId.ToString() }, cancellationToken: ct));
        if (affected != 1) throw new KeyNotFoundException("Inventory item was not found.");
    }

    public async Task<bool> ItemCodeExistsAsync(string code, int? excludeId, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM products WHERE company_id=@companyId AND product_code=@code
                    AND is_deleted=0 AND (@excludeId IS NULL OR product_id<>@excludeId)
            ) THEN 1 ELSE 0 END
            """;
        return await db.ExecuteScalarAsync<bool>(new CommandDefinition(sql,
            new { code, excludeId, companyId = companyId.ToString() }, cancellationToken: ct));
    }

    public Task<long> PostStockInAsync(long warehouseId, int itemId, decimal qty, decimal cost,
        string? reference, Guid companyId, Guid userId, CancellationToken ct)
        => ExecuteMovementAsync(true, warehouseId, itemId, qty, cost, reference, companyId, userId, ct);

    public Task<long> PostStockOutAsync(long warehouseId, int itemId, decimal qty,
        string? reference, Guid companyId, Guid userId, CancellationToken ct)
        => ExecuteMovementAsync(false, warehouseId, itemId, qty, null, reference, companyId, userId, ct);

    private async Task<long> ExecuteMovementAsync(bool increase, long warehouseId, int itemId, decimal qty,
        decimal? cost, string? reference, Guid companyId, Guid userId, CancellationToken ct)
    {
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(qty), "Quantity must be positive.");
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var delta = increase ? qty : -qty;
        var movementType = increase ? "PURCHASE" : "SALE";
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO stock_balances (company_id, warehouse_id, product_id, available_qty, last_cost, last_updated, is_deleted)
            VALUES (@companyId, @warehouseId, @itemId, @delta, @cost, CURRENT_TIMESTAMP, 0)
            ON CONFLICT (company_id, warehouse_id, product_id) DO UPDATE SET
                available_qty = stock_balances.available_qty + excluded.available_qty,
                last_cost = COALESCE(excluded.last_cost, stock_balances.last_cost),
                last_updated = CURRENT_TIMESTAMP
            """, new { companyId = companyId.ToString(), warehouseId, itemId, delta, cost }, tx, cancellationToken: ct));
        if (!increase && await db.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT available_qty FROM stock_balances WHERE company_id=@companyId AND warehouse_id=@warehouseId AND product_id=@itemId",
            new { companyId = companyId.ToString(), warehouseId, itemId }, tx, cancellationToken: ct)) < 0)
            throw new InvalidOperationException("Insufficient available stock.");
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO stock_movements
                (company_id, movement_type, warehouse_id_to, product_id, quantity, unit_cost,
                 reference_table, notes, created_by)
            VALUES (@companyId, @movementType, @warehouseId, @itemId, @qty, @cost,
                    'Inventory', @reference, @userId)
            """, new { companyId = companyId.ToString(), movementType, warehouseId, itemId, qty, cost, reference, userId = userId.ToString() }, tx, cancellationToken: ct));
        var id = await db.ExecuteScalarAsync<long>(new CommandDefinition("SELECT last_insert_rowid()", transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<decimal> GetAvailableQtyAsync(long warehouseId, int itemId, Guid companyId, CancellationToken ct)
    {
        await using var db = OpenConnection();
        await db.OpenAsync(ct);
        const string sql = "SELECT COALESCE(available_qty, 0) FROM stock_balances WHERE company_id=@companyId AND warehouse_id=@warehouseId AND product_id=@itemId AND is_deleted=0";
        return await db.ExecuteScalarAsync<decimal?>(new CommandDefinition(sql,
            new { companyId = companyId.ToString(), warehouseId, itemId }, cancellationToken: ct)) ?? 0m;
    }
}

    // ========== InventoryRepositoryFactory.cs ==========
public static class InventoryRepositoryFactory
{
    public static IInvRepo Create(string provider, string connectionString)
    {
        var normalized = ShouTech.App.Common.DatabaseProviderNames.Normalize(provider);
        ShouTech.App.Common.DatabaseProviderNames.EnsureCoreImplementation(normalized);
        return normalized switch
        {
            ShouTech.App.Common.DatabaseProviderNames.Sqlite => new SqliteInvRepo(connectionString),
            ShouTech.App.Common.DatabaseProviderNames.PostgreSql => new SqlInvRepo(connectionString),
            _ => throw new NotSupportedException($"Unsupported database provider '{provider}'.")
        };
    }
}

    // ========== MatRepo.cs ==========
/// <summary>Attribute row for material master (must be public for WPF DataGrid CanUserAddRows).</summary>
public sealed class MatAttrRow
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Group { get; set; } = "";
    public int SortOrder { get; set; }
}

internal static class MatRepo
{
	public sealed class ProductCategoryRec
	{
		public string Id { get; set; } = "";
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
	}

    public sealed class MatGroupRec
	{
		public long Id { get; set; }
		public long CardNo { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? ParentCode { get; set; }
		public string? Warehouse { get; set; }
		public string Notes { get; set; } = "";
		public bool IsSalesClass { get; set; }
		public bool IsDeleted { get; set; }
		public string Status { get; set; } = "Active";
		public long DisplayOrder { get; set; }
		public string Description { get; set; } = "";
		public long LinkedCount { get; set; }
		public string? CreatedBy { get; set; }
		public string? CreatedAt { get; set; }
		public string? UpdatedBy { get; set; }
		public string? UpdatedAt { get; set; }
		public string? GeneralSpecs { get; set; }
		public string? ImagePath { get; set; }
	}

    internal sealed class MatRec
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameEn { get; set; } = "";
        public string NamePrintAr { get; set; } = "";
        public string NamePrintEn { get; set; } = "";
        public string ShortName { get; set; } = "";
        public string SearchName { get; set; } = "";
        public string? AltCode { get; set; }
        public string? OldCode { get; set; }
        public string? SupplierCode { get; set; }
        public string? CustomerCode { get; set; }
        public string? ManufacturerCode { get; set; }
        public string? HsCode { get; set; }
        public string? Gtin { get; set; }
        public string? SkuExternal { get; set; }
        public string PosName { get; set; } = "";
        public string? GroupCode { get; set; }
        public string? Category { get; set; }
        public string? SubCategory { get; set; }
        public string? Brand { get; set; }
        public string? Model { get; set; }
        public string? Color { get; set; }
        public string? SizeSpec { get; set; }
        public string? Grade { get; set; }
        public string? Class1 { get; set; }
        public string? Class2 { get; set; }
        public string? Class3 { get; set; }
        public string? Class4 { get; set; }
        public string? Class5 { get; set; }
        public string? ProductLine { get; set; }
        public string? AbcClass { get; set; }
        public string? Season { get; set; }
        public string ItemType { get; set; } = "";
        public string Status { get; set; } = "";
        public string LifecycleStatus { get; set; } = "";
        public string MakeOrBuy { get; set; } = "";
        public string StockStage { get; set; } = "";
        public bool IsStocked { get; set; }
        public bool IsPurchased { get; set; }
        public bool IsSold { get; set; }
        public bool IsProduced { get; set; }
        public bool IsService { get; set; }
        public decimal WeightNet { get; set; }
        public decimal WeightGross { get; set; }
        public string? WeightUnit { get; set; }
        public decimal Volume { get; set; }
        public string? VolumeUnit { get; set; }
        public decimal Length { get; set; }
        public decimal Width { get; set; }
        public decimal Height { get; set; }
        public string? DimUnit { get; set; }
        public decimal Area { get; set; }
        public string? AreaUnit { get; set; }
        public decimal PackQty { get; set; }
        public decimal UnitsPerParcel { get; set; }
        public string? OriginCountry { get; set; }
        public string? Manufacturer { get; set; }
        public string? BrandOwner { get; set; }
        public string? Source { get; set; }
        public int WarrantyMonths { get; set; }
        public string? CatalogueRef { get; set; }
        public string? WebUrl { get; set; }
        public string Tags { get; set; } = "";
        public string? Unit1 { get; set; }
        public decimal Unit1Factor { get; set; }
        public string? Unit1Barcode { get; set; }
        public string? Unit2 { get; set; }
        public decimal Unit2Factor { get; set; }
        public string? Unit2Barcode { get; set; }
        public string? Unit3 { get; set; }
        public decimal Unit3Factor { get; set; }
        public string? Unit3Barcode { get; set; }
        public string? Unit4 { get; set; }
        public decimal Unit4Factor { get; set; }
        public string? Unit4Barcode { get; set; }
        public string? Unit5 { get; set; }
        public decimal Unit5Factor { get; set; }
        public string? Unit5Barcode { get; set; }
        public string? DefaultUnit { get; set; }
        public string? PurchaseUnit { get; set; }
        public string? SalesUnit { get; set; }
        public string CostMethod { get; set; } = "";
        public decimal Cost { get; set; }
        public decimal StandardCost { get; set; }
        public decimal LastPurchaseCost { get; set; }
        public decimal AvgCost { get; set; }
        public string CostCurrency { get; set; } = "";
        public decimal OverheadPct { get; set; }
        public decimal LandedCost { get; set; }
        public string? LastPurchaseDate { get; set; }
        public decimal PriceRetail { get; set; }
        public decimal PriceWholesale { get; set; }
        public decimal PriceSpecial { get; set; }
        public decimal PriceMin { get; set; }
        public decimal PriceList { get; set; }
        public decimal PriceExport { get; set; }
        public decimal PriceDistributor { get; set; }
        public decimal PriceOnline { get; set; }
        public string PriceCurrency { get; set; } = "";
        public decimal MarginTargetPct { get; set; }
        public decimal DiscountMaxPct { get; set; }
        public bool AllowInvoiceDisc { get; set; }
        public bool PriceIncludesTax { get; set; }
        public decimal QtyOnHand { get; set; }
        public decimal MinQty { get; set; }
        public decimal MaxQty { get; set; }
        public decimal ReorderQty { get; set; }
        public decimal SafetyStock { get; set; }
        public string? DefaultWarehouse { get; set; }
        public string? ShelfBin { get; set; }
        public int LeadTimeDays { get; set; }
        public decimal LotSize { get; set; }
        public decimal OrderMultiple { get; set; }
        public decimal EconomicOrderQty { get; set; }
        public string ReplenishmentMethod { get; set; } = "";
        public bool HasExpiry { get; set; }
        public int ShelfLifeDays { get; set; }
        public int AlertBeforeExpiryDays { get; set; }
        public bool HasSerial { get; set; }
        public bool HasBatch { get; set; }
        public bool HasLot { get; set; }
        public bool TrackLocation { get; set; }
        public bool QualityCheckRequired { get; set; }
        public bool IsTaxable { get; set; }
        public decimal TaxRate { get; set; }
        public string? TaxCode { get; set; }
        public string? TaxAccount { get; set; }
        public string? TaxType { get; set; }
        public bool IsZeroRated { get; set; }
        public string? AccInventory { get; set; }
        public string? AccSales { get; set; }
        public string? AccSalesReturn { get; set; }
        public string? AccCogs { get; set; }
        public string? AccPurchase { get; set; }
        public string? AccPurchaseReturn { get; set; }
        public string? AccExpense { get; set; }
        public string? AccWip { get; set; }
        public bool AllowSale { get; set; }
        public bool AllowPurchase { get; set; }
        public bool AllowReturn { get; set; }
        public bool AllowNegative { get; set; }
        public decimal CommissionPct { get; set; }
        public decimal BonusQty { get; set; }
        public string? BonusItemCode { get; set; }
        public decimal MinSaleQty { get; set; }
        public decimal MaxSaleQty { get; set; }
        public bool SalesBlocked { get; set; }
        public string? ImagePath { get; set; }
        public string? ImagePath2 { get; set; }
        public string? AttachmentPath { get; set; }
        public string Description { get; set; } = "";
        public string Notes { get; set; } = "";
        public string InternalNotes { get; set; } = "";
        public string SpecSheet { get; set; } = "";
        public string LabelText { get; set; } = "";
        public string UnitsJson { get; set; } = "";
        public string PricesJson { get; set; } = "";
        public string AttributesJson { get; set; } = "";
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? UpdatedBy { get; set; }
        public List<MatAttrRow> Attributes { get; set; } = new();
        public List<MatBarcode> ExtraBarcodes { get; set; } = new();
        public List<MatPriceLine> PriceLines { get; set; } = new();
        public List<MatSupplier> Suppliers { get; set; } = new();
    }

    internal sealed class MatBarcode { public long Id { get; set; } public string Barcode { get; set; } = ""; public string BarcodeType { get; set; } = "EAN"; public string? UnitName { get; set; } }
    internal sealed class MatPriceLine { public long Id { get; set; } public string PriceLevel { get; set; } = ""; public decimal Price { get; set; } public string Currency { get; set; } = ""; public string? UnitName { get; set; } public string? ValidFrom { get; set; } public string? ValidTo { get; set; } }
    internal sealed class MatSupplier { public long Id { get; set; } public string SupplierCode { get; set; } = ""; public string? SupplierItemCode { get; set; } public int LeadTimeDays { get; set; } public decimal MinOrderQty { get; set; } public bool IsPreferred { get; set; } }


    public static void EnsureSchema()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("No connection to the current company database.");
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = DatabaseRuntime.IsPostgreSql()
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'materials';"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'materials';";
        if (Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("materials schema is missing. Apply the provider migrations under dba/mig.");
    }

    public sealed class StockRec
    {
        public long MaterialId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string NameEn { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal Quantity { get; init; }
        public decimal AverageCost { get; init; }
    }

    public static List<StockRec> LoadStockRows(long warehouseId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warehouseId);

        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("No connection to the current company database.");

        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.material_id, m.code, m.name_ar, COALESCE(m.name_en, ''),
                   COALESCE(m.default_unit, ''),
                   COALESCE(s.available_qty, 0),
                   COALESCE(s.average_cost, m.avg_cost, m.cost, 0)
            FROM materials m
            LEFT JOIN material_stock_balances s
              ON s.company_id = m.company_id
             AND s.material_id = m.material_id
             AND s.warehouse_id = $warehouse
             AND s.is_deleted = 0
            WHERE m.company_id = $company
              AND m.is_deleted = 0
              AND m.is_stocked = 1
              AND m.is_service = 0
            ORDER BY m.code;
            """;
        if (DatabaseRuntime.IsPostgreSql())
        {
            command.CommandText = command.CommandText
                .Replace("s.is_deleted = 0", "s.is_deleted = FALSE", StringComparison.Ordinal)
                .Replace("m.is_deleted = 0", "m.is_deleted = FALSE", StringComparison.Ordinal)
                .Replace("m.is_stocked = 1", "m.is_stocked = TRUE", StringComparison.Ordinal)
                .Replace("m.is_service = 0", "m.is_service = FALSE", StringComparison.Ordinal);
        }
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$warehouse", warehouseId);

        using var reader = command.ExecuteReader();
        var rows = new List<StockRec>();
        while (reader.Read())
        {
            rows.Add(new StockRec
            {
                MaterialId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Code = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                Name = Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "",
                NameEn = Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "",
                Unit = Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? "",
                Quantity = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                AverageCost = Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture)
            });
        }
        return rows;
    }

    public static List<MatRec> LoadAll()
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return new();
        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT material_id, code, name_ar, name_en, name_print_ar, name_print_en, short_name, search_name, alt_code, old_code, supplier_code, customer_code, manufacturer_code, hs_code, gtin, sku_external, pos_name, group_code, category, sub_category, brand, model, color, size_spec, grade, class1, class2, class3, class4, class5, product_line, abc_class, season, item_type, status, lifecycle_status, make_or_buy, stock_stage, is_stocked, is_purchased, is_sold, is_produced, is_service, weight_net, weight_gross, weight_unit, volume, volume_unit, length, width, height, dim_unit, area, area_unit, pack_qty, units_per_parcel, origin_country, manufacturer, brand_owner, source, warranty_months, catalogue_ref, web_url, tags, unit1, unit1_factor, unit1_barcode, unit2, unit2_factor, unit2_barcode, unit3, unit3_factor, unit3_barcode, unit4, unit4_factor, unit4_barcode, unit5, unit5_factor, unit5_barcode, default_unit, purchase_unit, sales_unit, cost_method, cost, standard_cost, last_purchase_cost, avg_cost, cost_currency, overhead_pct, landed_cost, last_purchase_date, price_retail, price_wholesale, price_special, price_min, price_list, price_export, price_distributor, price_online, price_currency, margin_target_pct, discount_max_pct, allow_invoice_disc, price_includes_tax, qty_on_hand, min_qty, max_qty, reorder_qty, safety_stock, default_warehouse, shelf_bin, lead_time_days, lot_size, order_multiple, economic_order_qty, replenishment_method, has_expiry, shelf_life_days, alert_before_expiry_days, has_serial, has_batch, has_lot, track_location, quality_check_required, is_taxable, tax_rate, tax_code, tax_account, tax_type, is_zero_rated, acc_inventory, acc_sales, acc_sales_return, acc_cogs, acc_purchase, acc_purchase_return, acc_expense, acc_wip, allow_sale, allow_purchase, allow_return, allow_negative, commission_pct, bonus_qty, bonus_item_code, min_sale_qty, max_sale_qty, sales_blocked, image_path, image_path2, attachment_path, description, notes, internal_notes, spec_sheet, label_text, units_json, prices_json, attributes_json, is_deleted, created_at, updated_at, created_by, updated_by FROM materials WHERE company_id=$company AND is_deleted=0 ORDER BY code";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        using var reader = command.ExecuteReader();
        var result = new List<MatRec>();
        while (reader.Read()) result.Add(Map(reader));
        return result;
    }

    public static MatRec? LoadById(long id, bool includeChildren = true)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return null;
        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT material_id, code, name_ar, name_en, name_print_ar, name_print_en, short_name, search_name, alt_code, old_code, supplier_code, customer_code, manufacturer_code, hs_code, gtin, sku_external, pos_name, group_code, category, sub_category, brand, model, color, size_spec, grade, class1, class2, class3, class4, class5, product_line, abc_class, season, item_type, status, lifecycle_status, make_or_buy, stock_stage, is_stocked, is_purchased, is_sold, is_produced, is_service, weight_net, weight_gross, weight_unit, volume, volume_unit, length, width, height, dim_unit, area, area_unit, pack_qty, units_per_parcel, origin_country, manufacturer, brand_owner, source, warranty_months, catalogue_ref, web_url, tags, unit1, unit1_factor, unit1_barcode, unit2, unit2_factor, unit2_barcode, unit3, unit3_factor, unit3_barcode, unit4, unit4_factor, unit4_barcode, unit5, unit5_factor, unit5_barcode, default_unit, purchase_unit, sales_unit, cost_method, cost, standard_cost, last_purchase_cost, avg_cost, cost_currency, overhead_pct, landed_cost, last_purchase_date, price_retail, price_wholesale, price_special, price_min, price_list, price_export, price_distributor, price_online, price_currency, margin_target_pct, discount_max_pct, allow_invoice_disc, price_includes_tax, qty_on_hand, min_qty, max_qty, reorder_qty, safety_stock, default_warehouse, shelf_bin, lead_time_days, lot_size, order_multiple, economic_order_qty, replenishment_method, has_expiry, shelf_life_days, alert_before_expiry_days, has_serial, has_batch, has_lot, track_location, quality_check_required, is_taxable, tax_rate, tax_code, tax_account, tax_type, is_zero_rated, acc_inventory, acc_sales, acc_sales_return, acc_cogs, acc_purchase, acc_purchase_return, acc_expense, acc_wip, allow_sale, allow_purchase, allow_return, allow_negative, commission_pct, bonus_qty, bonus_item_code, min_sale_qty, max_sale_qty, sales_blocked, image_path, image_path2, attachment_path, description, notes, internal_notes, spec_sheet, label_text, units_json, prices_json, attributes_json, is_deleted, created_at, updated_at, created_by, updated_by FROM materials WHERE company_id=$company AND material_id=$id AND is_deleted=0";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var rec = Map(reader);
        reader.Close();
        if (includeChildren) LoadChildren(connection, rec);
        return rec;
    }

    public static bool CodeExists(string code, long? excludeId = null)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return false;
        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        command.CommandText = excludeId is null
            ? "SELECT COUNT(1) FROM materials WHERE company_id=$company AND code=$code AND is_deleted=0"
            : "SELECT COUNT(1) FROM materials WHERE company_id=$company AND code=$code AND material_id<>$id AND is_deleted=0";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$code", code.Trim());
        if (excludeId is not null) Add(command, "$id", excludeId.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static string NextCode(string? groupPrefix = null)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) return string.IsNullOrWhiteSpace(groupPrefix) ? "0001" : groupPrefix + "0001";
        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(groupPrefix))
        { command.CommandText = "SELECT code FROM materials WHERE company_id=$company AND is_deleted=0 ORDER BY code DESC LIMIT 1"; Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty); }
        else
        { command.CommandText = "SELECT code FROM materials WHERE company_id=$company AND code LIKE $p AND is_deleted=0 ORDER BY code DESC LIMIT 1"; Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(command, "$p", groupPrefix + "%"); }
        var last = command.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(last)) return string.IsNullOrWhiteSpace(groupPrefix) ? "0001" : groupPrefix + "0001";
        int i = last.Length - 1; while (i >= 0 && char.IsDigit(last[i])) i--;
        var prefix = last[..(i + 1)]; var numPart = last[(i + 1)..];
        if (!long.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) n = 0;
        n++; var width = Math.Max(numPart.Length, 4);
        return prefix + n.ToString("D" + width, CultureInfo.InvariantCulture);
    }

    public static MatRec Save(MatRec value, bool isNew)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("No connection to the current company database.");
        EnsureSchema();
        using var connection = Open(cs);
        using var transaction = connection.BeginTransaction();
        if (isNew)
        {
            if (CodeExistsOn(connection, transaction, value.Code, null)) throw new InvalidOperationException("Material code already exists: " + value.Code);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO materials (company_id, code, name_ar, name_en, name_print_ar, name_print_en, short_name, search_name, alt_code, old_code, supplier_code, customer_code, manufacturer_code, hs_code, gtin, sku_external, pos_name, group_code, category, sub_category, brand, model, color, size_spec, grade, class1, class2, class3, class4, class5, product_line, abc_class, season, item_type, status, lifecycle_status, make_or_buy, stock_stage, is_stocked, is_purchased, is_sold, is_produced, is_service, weight_net, weight_gross, weight_unit, volume, volume_unit, length, width, height, dim_unit, area, area_unit, pack_qty, units_per_parcel, origin_country, manufacturer, brand_owner, source, warranty_months, catalogue_ref, web_url, tags, unit1, unit1_factor, unit1_barcode, unit2, unit2_factor, unit2_barcode, unit3, unit3_factor, unit3_barcode, unit4, unit4_factor, unit4_barcode, unit5, unit5_factor, unit5_barcode, default_unit, purchase_unit, sales_unit, cost_method, cost, standard_cost, last_purchase_cost, avg_cost, cost_currency, overhead_pct, landed_cost, last_purchase_date, price_retail, price_wholesale, price_special, price_min, price_list, price_export, price_distributor, price_online, price_currency, margin_target_pct, discount_max_pct, allow_invoice_disc, price_includes_tax, qty_on_hand, min_qty, max_qty, reorder_qty, safety_stock, default_warehouse, shelf_bin, lead_time_days, lot_size, order_multiple, economic_order_qty, replenishment_method, has_expiry, shelf_life_days, alert_before_expiry_days, has_serial, has_batch, has_lot, track_location, quality_check_required, is_taxable, tax_rate, tax_code, tax_account, tax_type, is_zero_rated, acc_inventory, acc_sales, acc_sales_return, acc_cogs, acc_purchase, acc_purchase_return, acc_expense, acc_wip, allow_sale, allow_purchase, allow_return, allow_negative, commission_pct, bonus_qty, bonus_item_code, min_sale_qty, max_sale_qty, sales_blocked, image_path, image_path2, attachment_path, description, notes, internal_notes, spec_sheet, label_text, units_json, prices_json, attributes_json, is_deleted, created_by, updated_by) VALUES ($company, $code, $name_ar, $name_en, $name_print_ar, $name_print_en, $short_name, $search_name, $alt_code, $old_code, $supplier_code, $customer_code, $manufacturer_code, $hs_code, $gtin, $sku_external, $pos_name, $group_code, $category, $sub_category, $brand, $model, $color, $size_spec, $grade, $class1, $class2, $class3, $class4, $class5, $product_line, $abc_class, $season, $item_type, $status, $lifecycle_status, $make_or_buy, $stock_stage, $is_stocked, $is_purchased, $is_sold, $is_produced, $is_service, $weight_net, $weight_gross, $weight_unit, $volume, $volume_unit, $length, $width, $height, $dim_unit, $area, $area_unit, $pack_qty, $units_per_parcel, $origin_country, $manufacturer, $brand_owner, $source, $warranty_months, $catalogue_ref, $web_url, $tags, $unit1, $unit1_factor, $unit1_barcode, $unit2, $unit2_factor, $unit2_barcode, $unit3, $unit3_factor, $unit3_barcode, $unit4, $unit4_factor, $unit4_barcode, $unit5, $unit5_factor, $unit5_barcode, $default_unit, $purchase_unit, $sales_unit, $cost_method, $cost, $standard_cost, $last_purchase_cost, $avg_cost, $cost_currency, $overhead_pct, $landed_cost, $last_purchase_date, $price_retail, $price_wholesale, $price_special, $price_min, $price_list, $price_export, $price_distributor, $price_online, $price_currency, $margin_target_pct, $discount_max_pct, $allow_invoice_disc, $price_includes_tax, $qty_on_hand, $min_qty, $max_qty, $reorder_qty, $safety_stock, $default_warehouse, $shelf_bin, $lead_time_days, $lot_size, $order_multiple, $economic_order_qty, $replenishment_method, $has_expiry, $shelf_life_days, $alert_before_expiry_days, $has_serial, $has_batch, $has_lot, $track_location, $quality_check_required, $is_taxable, $tax_rate, $tax_code, $tax_account, $tax_type, $is_zero_rated, $acc_inventory, $acc_sales, $acc_sales_return, $acc_cogs, $acc_purchase, $acc_purchase_return, $acc_expense, $acc_wip, $allow_sale, $allow_purchase, $allow_return, $allow_negative, $commission_pct, $bonus_qty, $bonus_item_code, $min_sale_qty, $max_sale_qty, $sales_blocked, $image_path, $image_path2, $attachment_path, $description, $notes, $internal_notes, $spec_sheet, $label_text, $units_json, $prices_json, $attributes_json, $is_deleted, $created_by, $updated_by)";
            Bind(command, value);
            command.ExecuteNonQuery();
            using var idCmd = connection.CreateCommand();
            idCmd.Transaction = transaction;
            idCmd.CommandText = DatabaseRuntime.IsPostgreSql() ? "SELECT currval(pg_get_serial_sequence('materials','material_id'))" : "SELECT last_insert_rowid()";
            value.Id = Convert.ToInt64(idCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            SaveChildren(connection, transaction, value);
            transaction.Commit();
            return value;
        }
        else
        {
            if (CodeExistsOn(connection, transaction, value.Code, value.Id)) throw new InvalidOperationException("Material code already exists: " + value.Code);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE materials SET name_ar=$name_ar, name_en=$name_en, name_print_ar=$name_print_ar, name_print_en=$name_print_en, short_name=$short_name, search_name=$search_name, alt_code=$alt_code, old_code=$old_code, supplier_code=$supplier_code, customer_code=$customer_code, manufacturer_code=$manufacturer_code, hs_code=$hs_code, gtin=$gtin, sku_external=$sku_external, pos_name=$pos_name, group_code=$group_code, category=$category, sub_category=$sub_category, brand=$brand, model=$model, color=$color, size_spec=$size_spec, grade=$grade, class1=$class1, class2=$class2, class3=$class3, class4=$class4, class5=$class5, product_line=$product_line, abc_class=$abc_class, season=$season, item_type=$item_type, status=$status, lifecycle_status=$lifecycle_status, make_or_buy=$make_or_buy, stock_stage=$stock_stage, is_stocked=$is_stocked, is_purchased=$is_purchased, is_sold=$is_sold, is_produced=$is_produced, is_service=$is_service, weight_net=$weight_net, weight_gross=$weight_gross, weight_unit=$weight_unit, volume=$volume, volume_unit=$volume_unit, length=$length, width=$width, height=$height, dim_unit=$dim_unit, area=$area, area_unit=$area_unit, pack_qty=$pack_qty, units_per_parcel=$units_per_parcel, origin_country=$origin_country, manufacturer=$manufacturer, brand_owner=$brand_owner, source=$source, warranty_months=$warranty_months, catalogue_ref=$catalogue_ref, web_url=$web_url, tags=$tags, unit1=$unit1, unit1_factor=$unit1_factor, unit1_barcode=$unit1_barcode, unit2=$unit2, unit2_factor=$unit2_factor, unit2_barcode=$unit2_barcode, unit3=$unit3, unit3_factor=$unit3_factor, unit3_barcode=$unit3_barcode, unit4=$unit4, unit4_factor=$unit4_factor, unit4_barcode=$unit4_barcode, unit5=$unit5, unit5_factor=$unit5_factor, unit5_barcode=$unit5_barcode, default_unit=$default_unit, purchase_unit=$purchase_unit, sales_unit=$sales_unit, cost_method=$cost_method, cost=$cost, standard_cost=$standard_cost, last_purchase_cost=$last_purchase_cost, avg_cost=$avg_cost, cost_currency=$cost_currency, overhead_pct=$overhead_pct, landed_cost=$landed_cost, last_purchase_date=$last_purchase_date, price_retail=$price_retail, price_wholesale=$price_wholesale, price_special=$price_special, price_min=$price_min, price_list=$price_list, price_export=$price_export, price_distributor=$price_distributor, price_online=$price_online, price_currency=$price_currency, margin_target_pct=$margin_target_pct, discount_max_pct=$discount_max_pct, allow_invoice_disc=$allow_invoice_disc, price_includes_tax=$price_includes_tax, qty_on_hand=$qty_on_hand, min_qty=$min_qty, max_qty=$max_qty, reorder_qty=$reorder_qty, safety_stock=$safety_stock, default_warehouse=$default_warehouse, shelf_bin=$shelf_bin, lead_time_days=$lead_time_days, lot_size=$lot_size, order_multiple=$order_multiple, economic_order_qty=$economic_order_qty, replenishment_method=$replenishment_method, has_expiry=$has_expiry, shelf_life_days=$shelf_life_days, alert_before_expiry_days=$alert_before_expiry_days, has_serial=$has_serial, has_batch=$has_batch, has_lot=$has_lot, track_location=$track_location, quality_check_required=$quality_check_required, is_taxable=$is_taxable, tax_rate=$tax_rate, tax_code=$tax_code, tax_account=$tax_account, tax_type=$tax_type, is_zero_rated=$is_zero_rated, acc_inventory=$acc_inventory, acc_sales=$acc_sales, acc_sales_return=$acc_sales_return, acc_cogs=$acc_cogs, acc_purchase=$acc_purchase, acc_purchase_return=$acc_purchase_return, acc_expense=$acc_expense, acc_wip=$acc_wip, allow_sale=$allow_sale, allow_purchase=$allow_purchase, allow_return=$allow_return, allow_negative=$allow_negative, commission_pct=$commission_pct, bonus_qty=$bonus_qty, bonus_item_code=$bonus_item_code, min_sale_qty=$min_sale_qty, max_sale_qty=$max_sale_qty, sales_blocked=$sales_blocked, image_path=$image_path, image_path2=$image_path2, attachment_path=$attachment_path, description=$description, notes=$notes, internal_notes=$internal_notes, spec_sheet=$spec_sheet, label_text=$label_text, units_json=$units_json, prices_json=$prices_json, attributes_json=$attributes_json, is_deleted=$is_deleted, updated_by=$updated_by, updated_at=CURRENT_TIMESTAMP WHERE company_id=$company AND material_id=$id AND is_deleted=0";
            Bind(command, value);
            Add(command, "$id", value.Id);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Material was not saved because it no longer exists or is no longer editable.");
            using (var del = connection.CreateCommand()) { del.Transaction = transaction; del.CommandText = "DELETE FROM material_attributes WHERE company_id=$company AND material_id=$id"; Add(del, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(del, "$id", value.Id); del.ExecuteNonQuery(); }
            using (var del = connection.CreateCommand()) { del.Transaction = transaction; del.CommandText = "DELETE FROM material_extra_barcodes WHERE company_id=$company AND material_id=$id"; Add(del, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(del, "$id", value.Id); del.ExecuteNonQuery(); }
            using (var del = connection.CreateCommand()) { del.Transaction = transaction; del.CommandText = "DELETE FROM material_price_list WHERE company_id=$company AND material_id=$id"; Add(del, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(del, "$id", value.Id); del.ExecuteNonQuery(); }
            using (var del = connection.CreateCommand()) { del.Transaction = transaction; del.CommandText = "DELETE FROM material_suppliers WHERE company_id=$company AND material_id=$id"; Add(del, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(del, "$id", value.Id); del.ExecuteNonQuery(); }
            SaveChildren(connection, transaction, value);
            transaction.Commit();
            return value;
        }
    }

    public static void SoftDelete(long id)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Material id must be positive.");

        var companyCode = AppState.CurrentCompanyCode;
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new InvalidOperationException("A company must be selected before deleting a material.");

        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("The company database connection is not configured.");

        EnsureSchema();
        using var connection = Open(cs);
        using var command = connection.CreateCommand();
        var deletedValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
        var activeValue = DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0";
        command.CommandText = $"UPDATE materials SET is_deleted={deletedValue}, deleted_at=$deletedAt, deleted_by=$deletedBy, updated_at=CURRENT_TIMESTAMP WHERE company_id=$company AND material_id=$id AND COALESCE(is_deleted,{activeValue})={activeValue}";
        Add(command, "$company", companyCode);
        Add(command, "$id", id);
        Add(command, "$deletedAt", DateTimeOffset.UtcNow);
        Add(command, "$deletedBy", AppState.CurrentUser?.Username);
        var affected = command.ExecuteNonQuery();
        if (affected == 0)
            throw new InvalidOperationException("The material was not found in the selected company or has already been deleted.");
    }

    private static bool CodeExistsOn(DbConnection connection, DbTransaction tx, string code, long? excludeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = excludeId is null
            ? "SELECT COUNT(1) FROM materials WHERE company_id=$company AND code=$code AND is_deleted=0"
            : "SELECT COUNT(1) FROM materials WHERE company_id=$company AND code=$code AND material_id<>$id AND is_deleted=0";
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$code", code.Trim());
        if (excludeId is not null) Add(command, "$id", excludeId.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static MatRec Map(DbDataReader r)
    {
        var m = new MatRec();
        m.Id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
        m.Code = r.IsDBNull(1) ? "" : (r.GetValue(1)?.ToString() ?? "");
        m.Name = r.IsDBNull(2) ? "" : (r.GetValue(2)?.ToString() ?? "");
        m.NameEn = r.IsDBNull(3) ? "" : (r.GetValue(3)?.ToString() ?? "");
        m.NamePrintAr = r.IsDBNull(4) ? "" : (r.GetValue(4)?.ToString() ?? "");
        m.NamePrintEn = r.IsDBNull(5) ? "" : (r.GetValue(5)?.ToString() ?? "");
        m.ShortName = r.IsDBNull(6) ? "" : (r.GetValue(6)?.ToString() ?? "");
        m.SearchName = r.IsDBNull(7) ? "" : (r.GetValue(7)?.ToString() ?? "");
        m.AltCode = r.IsDBNull(8) ? null : r.GetValue(8)?.ToString();
        m.OldCode = r.IsDBNull(9) ? null : r.GetValue(9)?.ToString();
        m.SupplierCode = r.IsDBNull(10) ? null : r.GetValue(10)?.ToString();
        m.CustomerCode = r.IsDBNull(11) ? null : r.GetValue(11)?.ToString();
        m.ManufacturerCode = r.IsDBNull(12) ? null : r.GetValue(12)?.ToString();
        m.HsCode = r.IsDBNull(13) ? null : r.GetValue(13)?.ToString();
        m.Gtin = r.IsDBNull(14) ? null : r.GetValue(14)?.ToString();
        m.SkuExternal = r.IsDBNull(15) ? null : r.GetValue(15)?.ToString();
        m.PosName = r.IsDBNull(16) ? "" : (r.GetValue(16)?.ToString() ?? "");
        m.GroupCode = r.IsDBNull(17) ? null : r.GetValue(17)?.ToString();
        m.Category = r.IsDBNull(18) ? null : r.GetValue(18)?.ToString();
        m.SubCategory = r.IsDBNull(19) ? null : r.GetValue(19)?.ToString();
        m.Brand = r.IsDBNull(20) ? null : r.GetValue(20)?.ToString();
        m.Model = r.IsDBNull(21) ? null : r.GetValue(21)?.ToString();
        m.Color = r.IsDBNull(22) ? null : r.GetValue(22)?.ToString();
        m.SizeSpec = r.IsDBNull(23) ? null : r.GetValue(23)?.ToString();
        m.Grade = r.IsDBNull(24) ? null : r.GetValue(24)?.ToString();
        m.Class1 = r.IsDBNull(25) ? null : r.GetValue(25)?.ToString();
        m.Class2 = r.IsDBNull(26) ? null : r.GetValue(26)?.ToString();
        m.Class3 = r.IsDBNull(27) ? null : r.GetValue(27)?.ToString();
        m.Class4 = r.IsDBNull(28) ? null : r.GetValue(28)?.ToString();
        m.Class5 = r.IsDBNull(29) ? null : r.GetValue(29)?.ToString();
        m.ProductLine = r.IsDBNull(30) ? null : r.GetValue(30)?.ToString();
        m.AbcClass = r.IsDBNull(31) ? null : r.GetValue(31)?.ToString();
        m.Season = r.IsDBNull(32) ? null : r.GetValue(32)?.ToString();
        m.ItemType = r.IsDBNull(33) ? "" : (r.GetValue(33)?.ToString() ?? "");
        m.Status = r.IsDBNull(34) ? "" : (r.GetValue(34)?.ToString() ?? "");
        m.LifecycleStatus = r.IsDBNull(35) ? "" : (r.GetValue(35)?.ToString() ?? "");
        m.MakeOrBuy = r.IsDBNull(36) ? "" : (r.GetValue(36)?.ToString() ?? "");
        m.StockStage = r.IsDBNull(37) ? "" : (r.GetValue(37)?.ToString() ?? "");
        m.IsStocked = ReadBool(r, 38);
        m.IsPurchased = ReadBool(r, 39);
        m.IsSold = ReadBool(r, 40);
        m.IsProduced = ReadBool(r, 41);
        m.IsService = ReadBool(r, 42);
        m.WeightNet = r.IsDBNull(43) ? 0m : Convert.ToDecimal(r.GetValue(43), CultureInfo.InvariantCulture);
        m.WeightGross = r.IsDBNull(44) ? 0m : Convert.ToDecimal(r.GetValue(44), CultureInfo.InvariantCulture);
        m.WeightUnit = r.IsDBNull(45) ? null : r.GetValue(45)?.ToString();
        m.Volume = r.IsDBNull(46) ? 0m : Convert.ToDecimal(r.GetValue(46), CultureInfo.InvariantCulture);
        m.VolumeUnit = r.IsDBNull(47) ? null : r.GetValue(47)?.ToString();
        m.Length = r.IsDBNull(48) ? 0m : Convert.ToDecimal(r.GetValue(48), CultureInfo.InvariantCulture);
        m.Width = r.IsDBNull(49) ? 0m : Convert.ToDecimal(r.GetValue(49), CultureInfo.InvariantCulture);
        m.Height = r.IsDBNull(50) ? 0m : Convert.ToDecimal(r.GetValue(50), CultureInfo.InvariantCulture);
        m.DimUnit = r.IsDBNull(51) ? null : r.GetValue(51)?.ToString();
        m.Area = r.IsDBNull(52) ? 0m : Convert.ToDecimal(r.GetValue(52), CultureInfo.InvariantCulture);
        m.AreaUnit = r.IsDBNull(53) ? null : r.GetValue(53)?.ToString();
        m.PackQty = r.IsDBNull(54) ? 0m : Convert.ToDecimal(r.GetValue(54), CultureInfo.InvariantCulture);
        m.UnitsPerParcel = r.IsDBNull(55) ? 0m : Convert.ToDecimal(r.GetValue(55), CultureInfo.InvariantCulture);
        m.OriginCountry = r.IsDBNull(56) ? null : r.GetValue(56)?.ToString();
        m.Manufacturer = r.IsDBNull(57) ? null : r.GetValue(57)?.ToString();
        m.BrandOwner = r.IsDBNull(58) ? null : r.GetValue(58)?.ToString();
        m.Source = r.IsDBNull(59) ? null : r.GetValue(59)?.ToString();
        m.WarrantyMonths = r.IsDBNull(60) ? 0 : Convert.ToInt32(r.GetValue(60), CultureInfo.InvariantCulture);
        m.CatalogueRef = r.IsDBNull(61) ? null : r.GetValue(61)?.ToString();
        m.WebUrl = r.IsDBNull(62) ? null : r.GetValue(62)?.ToString();
        m.Tags = r.IsDBNull(63) ? "" : (r.GetValue(63)?.ToString() ?? "");
        m.Unit1 = r.IsDBNull(64) ? null : r.GetValue(64)?.ToString();
        m.Unit1Factor = r.IsDBNull(65) ? 0m : Convert.ToDecimal(r.GetValue(65), CultureInfo.InvariantCulture);
        m.Unit1Barcode = r.IsDBNull(66) ? null : r.GetValue(66)?.ToString();
        m.Unit2 = r.IsDBNull(67) ? null : r.GetValue(67)?.ToString();
        m.Unit2Factor = r.IsDBNull(68) ? 0m : Convert.ToDecimal(r.GetValue(68), CultureInfo.InvariantCulture);
        m.Unit2Barcode = r.IsDBNull(69) ? null : r.GetValue(69)?.ToString();
        m.Unit3 = r.IsDBNull(70) ? null : r.GetValue(70)?.ToString();
        m.Unit3Factor = r.IsDBNull(71) ? 0m : Convert.ToDecimal(r.GetValue(71), CultureInfo.InvariantCulture);
        m.Unit3Barcode = r.IsDBNull(72) ? null : r.GetValue(72)?.ToString();
        m.Unit4 = r.IsDBNull(73) ? null : r.GetValue(73)?.ToString();
        m.Unit4Factor = r.IsDBNull(74) ? 0m : Convert.ToDecimal(r.GetValue(74), CultureInfo.InvariantCulture);
        m.Unit4Barcode = r.IsDBNull(75) ? null : r.GetValue(75)?.ToString();
        m.Unit5 = r.IsDBNull(76) ? null : r.GetValue(76)?.ToString();
        m.Unit5Factor = r.IsDBNull(77) ? 0m : Convert.ToDecimal(r.GetValue(77), CultureInfo.InvariantCulture);
        m.Unit5Barcode = r.IsDBNull(78) ? null : r.GetValue(78)?.ToString();
        m.DefaultUnit = r.IsDBNull(79) ? null : r.GetValue(79)?.ToString();
        m.PurchaseUnit = r.IsDBNull(80) ? null : r.GetValue(80)?.ToString();
        m.SalesUnit = r.IsDBNull(81) ? null : r.GetValue(81)?.ToString();
        m.CostMethod = r.IsDBNull(82) ? "" : (r.GetValue(82)?.ToString() ?? "");
        m.Cost = r.IsDBNull(83) ? 0m : Convert.ToDecimal(r.GetValue(83), CultureInfo.InvariantCulture);
        m.StandardCost = r.IsDBNull(84) ? 0m : Convert.ToDecimal(r.GetValue(84), CultureInfo.InvariantCulture);
        m.LastPurchaseCost = r.IsDBNull(85) ? 0m : Convert.ToDecimal(r.GetValue(85), CultureInfo.InvariantCulture);
        m.AvgCost = r.IsDBNull(86) ? 0m : Convert.ToDecimal(r.GetValue(86), CultureInfo.InvariantCulture);
        m.CostCurrency = r.IsDBNull(87) ? "" : (r.GetValue(87)?.ToString() ?? "");
        m.OverheadPct = r.IsDBNull(88) ? 0m : Convert.ToDecimal(r.GetValue(88), CultureInfo.InvariantCulture);
        m.LandedCost = r.IsDBNull(89) ? 0m : Convert.ToDecimal(r.GetValue(89), CultureInfo.InvariantCulture);
        m.LastPurchaseDate = r.IsDBNull(90) ? null : r.GetValue(90)?.ToString();
        m.PriceRetail = r.IsDBNull(91) ? 0m : Convert.ToDecimal(r.GetValue(91), CultureInfo.InvariantCulture);
        m.PriceWholesale = r.IsDBNull(92) ? 0m : Convert.ToDecimal(r.GetValue(92), CultureInfo.InvariantCulture);
        m.PriceSpecial = r.IsDBNull(93) ? 0m : Convert.ToDecimal(r.GetValue(93), CultureInfo.InvariantCulture);
        m.PriceMin = r.IsDBNull(94) ? 0m : Convert.ToDecimal(r.GetValue(94), CultureInfo.InvariantCulture);
        m.PriceList = r.IsDBNull(95) ? 0m : Convert.ToDecimal(r.GetValue(95), CultureInfo.InvariantCulture);
        m.PriceExport = r.IsDBNull(96) ? 0m : Convert.ToDecimal(r.GetValue(96), CultureInfo.InvariantCulture);
        m.PriceDistributor = r.IsDBNull(97) ? 0m : Convert.ToDecimal(r.GetValue(97), CultureInfo.InvariantCulture);
        m.PriceOnline = r.IsDBNull(98) ? 0m : Convert.ToDecimal(r.GetValue(98), CultureInfo.InvariantCulture);
        m.PriceCurrency = r.IsDBNull(99) ? "" : (r.GetValue(99)?.ToString() ?? "");
        m.MarginTargetPct = r.IsDBNull(100) ? 0m : Convert.ToDecimal(r.GetValue(100), CultureInfo.InvariantCulture);
        m.DiscountMaxPct = r.IsDBNull(101) ? 0m : Convert.ToDecimal(r.GetValue(101), CultureInfo.InvariantCulture);
        m.AllowInvoiceDisc = ReadBool(r, 102);
        m.PriceIncludesTax = ReadBool(r, 103);
        m.QtyOnHand = r.IsDBNull(104) ? 0m : Convert.ToDecimal(r.GetValue(104), CultureInfo.InvariantCulture);
        m.MinQty = r.IsDBNull(105) ? 0m : Convert.ToDecimal(r.GetValue(105), CultureInfo.InvariantCulture);
        m.MaxQty = r.IsDBNull(106) ? 0m : Convert.ToDecimal(r.GetValue(106), CultureInfo.InvariantCulture);
        m.ReorderQty = r.IsDBNull(107) ? 0m : Convert.ToDecimal(r.GetValue(107), CultureInfo.InvariantCulture);
        m.SafetyStock = r.IsDBNull(108) ? 0m : Convert.ToDecimal(r.GetValue(108), CultureInfo.InvariantCulture);
        m.DefaultWarehouse = r.IsDBNull(109) ? null : r.GetValue(109)?.ToString();
        m.ShelfBin = r.IsDBNull(110) ? null : r.GetValue(110)?.ToString();
        m.LeadTimeDays = r.IsDBNull(111) ? 0 : Convert.ToInt32(r.GetValue(111), CultureInfo.InvariantCulture);
        m.LotSize = r.IsDBNull(112) ? 0m : Convert.ToDecimal(r.GetValue(112), CultureInfo.InvariantCulture);
        m.OrderMultiple = r.IsDBNull(113) ? 0m : Convert.ToDecimal(r.GetValue(113), CultureInfo.InvariantCulture);
        m.EconomicOrderQty = r.IsDBNull(114) ? 0m : Convert.ToDecimal(r.GetValue(114), CultureInfo.InvariantCulture);
        m.ReplenishmentMethod = r.IsDBNull(115) ? "" : (r.GetValue(115)?.ToString() ?? "");
        m.HasExpiry = ReadBool(r, 116);
        m.ShelfLifeDays = r.IsDBNull(117) ? 0 : Convert.ToInt32(r.GetValue(117), CultureInfo.InvariantCulture);
        m.AlertBeforeExpiryDays = r.IsDBNull(118) ? 0 : Convert.ToInt32(r.GetValue(118), CultureInfo.InvariantCulture);
        m.HasSerial = ReadBool(r, 119);
        m.HasBatch = ReadBool(r, 120);
        m.HasLot = ReadBool(r, 121);
        m.TrackLocation = ReadBool(r, 122);
        m.QualityCheckRequired = ReadBool(r, 123);
        m.IsTaxable = ReadBool(r, 124);
        m.TaxRate = r.IsDBNull(125) ? 0m : Convert.ToDecimal(r.GetValue(125), CultureInfo.InvariantCulture);
        m.TaxCode = r.IsDBNull(126) ? null : r.GetValue(126)?.ToString();
        m.TaxAccount = r.IsDBNull(127) ? null : r.GetValue(127)?.ToString();
        m.TaxType = r.IsDBNull(128) ? null : r.GetValue(128)?.ToString();
        m.IsZeroRated = ReadBool(r, 129);
        m.AccInventory = r.IsDBNull(130) ? null : r.GetValue(130)?.ToString();
        m.AccSales = r.IsDBNull(131) ? null : r.GetValue(131)?.ToString();
        m.AccSalesReturn = r.IsDBNull(132) ? null : r.GetValue(132)?.ToString();
        m.AccCogs = r.IsDBNull(133) ? null : r.GetValue(133)?.ToString();
        m.AccPurchase = r.IsDBNull(134) ? null : r.GetValue(134)?.ToString();
        m.AccPurchaseReturn = r.IsDBNull(135) ? null : r.GetValue(135)?.ToString();
        m.AccExpense = r.IsDBNull(136) ? null : r.GetValue(136)?.ToString();
        m.AccWip = r.IsDBNull(137) ? null : r.GetValue(137)?.ToString();
        m.AllowSale = ReadBool(r, 138);
        m.AllowPurchase = ReadBool(r, 139);
        m.AllowReturn = ReadBool(r, 140);
        m.AllowNegative = ReadBool(r, 141);
        m.CommissionPct = r.IsDBNull(142) ? 0m : Convert.ToDecimal(r.GetValue(142), CultureInfo.InvariantCulture);
        m.BonusQty = r.IsDBNull(143) ? 0m : Convert.ToDecimal(r.GetValue(143), CultureInfo.InvariantCulture);
        m.BonusItemCode = r.IsDBNull(144) ? null : r.GetValue(144)?.ToString();
        m.MinSaleQty = r.IsDBNull(145) ? 0m : Convert.ToDecimal(r.GetValue(145), CultureInfo.InvariantCulture);
        m.MaxSaleQty = r.IsDBNull(146) ? 0m : Convert.ToDecimal(r.GetValue(146), CultureInfo.InvariantCulture);
        m.SalesBlocked = ReadBool(r, 147);
        m.ImagePath = r.IsDBNull(148) ? null : r.GetValue(148)?.ToString();
        m.ImagePath2 = r.IsDBNull(149) ? null : r.GetValue(149)?.ToString();
        m.AttachmentPath = r.IsDBNull(150) ? null : r.GetValue(150)?.ToString();
        m.Description = r.IsDBNull(151) ? "" : (r.GetValue(151)?.ToString() ?? "");
        m.Notes = r.IsDBNull(152) ? "" : (r.GetValue(152)?.ToString() ?? "");
        m.InternalNotes = r.IsDBNull(153) ? "" : (r.GetValue(153)?.ToString() ?? "");
        m.SpecSheet = r.IsDBNull(154) ? "" : (r.GetValue(154)?.ToString() ?? "");
        m.LabelText = r.IsDBNull(155) ? "" : (r.GetValue(155)?.ToString() ?? "");
        m.UnitsJson = r.IsDBNull(156) ? "" : (r.GetValue(156)?.ToString() ?? "");
        m.PricesJson = r.IsDBNull(157) ? "" : (r.GetValue(157)?.ToString() ?? "");
        m.AttributesJson = r.IsDBNull(158) ? "" : (r.GetValue(158)?.ToString() ?? "");
        m.CreatedAt = r.IsDBNull(160) ? null : r.GetValue(160)?.ToString();
        m.UpdatedAt = r.IsDBNull(161) ? null : r.GetValue(161)?.ToString();
        m.CreatedBy = r.IsDBNull(162) ? null : r.GetValue(162)?.ToString();
        m.UpdatedBy = r.IsDBNull(163) ? null : r.GetValue(163)?.ToString();
        return m;
    }

    private static bool ReadBool(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return false;
        var v = r.GetValue(i);
        if (v is bool b) return b;
        if (v is long l) return l != 0;
        if (v is int n) return n != 0;
        return Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0;
    }

    private static void Bind(DbCommand command, MatRec v)
    {
        Add(command, "$company", AppState.CurrentCompanyCode ?? string.Empty);
        Add(command, "$code", v.Code);
        Add(command, "$name_ar", v.Name);
        Add(command, "$name_en", v.NameEn);
        Add(command, "$name_print_ar", v.NamePrintAr);
        Add(command, "$name_print_en", v.NamePrintEn);
        Add(command, "$short_name", v.ShortName);
        Add(command, "$search_name", v.SearchName);
        Add(command, "$alt_code", (object?)v.AltCode ?? DBNull.Value);
        Add(command, "$old_code", (object?)v.OldCode ?? DBNull.Value);
        Add(command, "$supplier_code", (object?)v.SupplierCode ?? DBNull.Value);
        Add(command, "$customer_code", (object?)v.CustomerCode ?? DBNull.Value);
        Add(command, "$manufacturer_code", (object?)v.ManufacturerCode ?? DBNull.Value);
        Add(command, "$hs_code", (object?)v.HsCode ?? DBNull.Value);
        Add(command, "$gtin", (object?)v.Gtin ?? DBNull.Value);
        Add(command, "$sku_external", (object?)v.SkuExternal ?? DBNull.Value);
        Add(command, "$pos_name", v.PosName);
        Add(command, "$group_code", (object?)v.GroupCode ?? DBNull.Value);
        Add(command, "$category", (object?)v.Category ?? DBNull.Value);
        Add(command, "$sub_category", (object?)v.SubCategory ?? DBNull.Value);
        Add(command, "$brand", (object?)v.Brand ?? DBNull.Value);
        Add(command, "$model", (object?)v.Model ?? DBNull.Value);
        Add(command, "$color", (object?)v.Color ?? DBNull.Value);
        Add(command, "$size_spec", (object?)v.SizeSpec ?? DBNull.Value);
        Add(command, "$grade", (object?)v.Grade ?? DBNull.Value);
        Add(command, "$class1", (object?)v.Class1 ?? DBNull.Value);
        Add(command, "$class2", (object?)v.Class2 ?? DBNull.Value);
        Add(command, "$class3", (object?)v.Class3 ?? DBNull.Value);
        Add(command, "$class4", (object?)v.Class4 ?? DBNull.Value);
        Add(command, "$class5", (object?)v.Class5 ?? DBNull.Value);
        Add(command, "$product_line", (object?)v.ProductLine ?? DBNull.Value);
        Add(command, "$abc_class", (object?)v.AbcClass ?? DBNull.Value);
        Add(command, "$season", (object?)v.Season ?? DBNull.Value);
        Add(command, "$item_type", v.ItemType);
        Add(command, "$status", v.Status);
        Add(command, "$lifecycle_status", v.LifecycleStatus);
        Add(command, "$make_or_buy", v.MakeOrBuy);
        Add(command, "$stock_stage", v.StockStage);
        Add(command, "$is_stocked", v.IsStocked ? 1 : 0);
        Add(command, "$is_purchased", v.IsPurchased ? 1 : 0);
        Add(command, "$is_sold", v.IsSold ? 1 : 0);
        Add(command, "$is_produced", v.IsProduced ? 1 : 0);
        Add(command, "$is_service", v.IsService ? 1 : 0);
        Add(command, "$weight_net", v.WeightNet);
        Add(command, "$weight_gross", v.WeightGross);
        Add(command, "$weight_unit", (object?)v.WeightUnit ?? DBNull.Value);
        Add(command, "$volume", v.Volume);
        Add(command, "$volume_unit", (object?)v.VolumeUnit ?? DBNull.Value);
        Add(command, "$length", v.Length);
        Add(command, "$width", v.Width);
        Add(command, "$height", v.Height);
        Add(command, "$dim_unit", (object?)v.DimUnit ?? DBNull.Value);
        Add(command, "$area", v.Area);
        Add(command, "$area_unit", (object?)v.AreaUnit ?? DBNull.Value);
        Add(command, "$pack_qty", v.PackQty);
        Add(command, "$units_per_parcel", v.UnitsPerParcel);
        Add(command, "$origin_country", (object?)v.OriginCountry ?? DBNull.Value);
        Add(command, "$manufacturer", (object?)v.Manufacturer ?? DBNull.Value);
        Add(command, "$brand_owner", (object?)v.BrandOwner ?? DBNull.Value);
        Add(command, "$source", (object?)v.Source ?? DBNull.Value);
        Add(command, "$warranty_months", v.WarrantyMonths);
        Add(command, "$catalogue_ref", (object?)v.CatalogueRef ?? DBNull.Value);
        Add(command, "$web_url", (object?)v.WebUrl ?? DBNull.Value);
        Add(command, "$tags", v.Tags);
        Add(command, "$unit1", (object?)v.Unit1 ?? DBNull.Value);
        Add(command, "$unit1_factor", v.Unit1Factor);
        Add(command, "$unit1_barcode", (object?)v.Unit1Barcode ?? DBNull.Value);
        Add(command, "$unit2", (object?)v.Unit2 ?? DBNull.Value);
        Add(command, "$unit2_factor", v.Unit2Factor);
        Add(command, "$unit2_barcode", (object?)v.Unit2Barcode ?? DBNull.Value);
        Add(command, "$unit3", (object?)v.Unit3 ?? DBNull.Value);
        Add(command, "$unit3_factor", v.Unit3Factor);
        Add(command, "$unit3_barcode", (object?)v.Unit3Barcode ?? DBNull.Value);
        Add(command, "$unit4", (object?)v.Unit4 ?? DBNull.Value);
        Add(command, "$unit4_factor", v.Unit4Factor);
        Add(command, "$unit4_barcode", (object?)v.Unit4Barcode ?? DBNull.Value);
        Add(command, "$unit5", (object?)v.Unit5 ?? DBNull.Value);
        Add(command, "$unit5_factor", v.Unit5Factor);
        Add(command, "$unit5_barcode", (object?)v.Unit5Barcode ?? DBNull.Value);
        Add(command, "$default_unit", (object?)v.DefaultUnit ?? DBNull.Value);
        Add(command, "$purchase_unit", (object?)v.PurchaseUnit ?? DBNull.Value);
        Add(command, "$sales_unit", (object?)v.SalesUnit ?? DBNull.Value);
        Add(command, "$cost_method", v.CostMethod);
        Add(command, "$cost", v.Cost);
        Add(command, "$standard_cost", v.StandardCost);
        Add(command, "$last_purchase_cost", v.LastPurchaseCost);
        Add(command, "$avg_cost", v.AvgCost);
        Add(command, "$cost_currency", v.CostCurrency);
        Add(command, "$overhead_pct", v.OverheadPct);
        Add(command, "$landed_cost", v.LandedCost);
        Add(command, "$last_purchase_date", (object?)v.LastPurchaseDate ?? DBNull.Value);
        Add(command, "$price_retail", v.PriceRetail);
        Add(command, "$price_wholesale", v.PriceWholesale);
        Add(command, "$price_special", v.PriceSpecial);
        Add(command, "$price_min", v.PriceMin);
        Add(command, "$price_list", v.PriceList);
        Add(command, "$price_export", v.PriceExport);
        Add(command, "$price_distributor", v.PriceDistributor);
        Add(command, "$price_online", v.PriceOnline);
        Add(command, "$price_currency", v.PriceCurrency);
        Add(command, "$margin_target_pct", v.MarginTargetPct);
        Add(command, "$discount_max_pct", v.DiscountMaxPct);
        Add(command, "$allow_invoice_disc", v.AllowInvoiceDisc ? 1 : 0);
        Add(command, "$price_includes_tax", v.PriceIncludesTax ? 1 : 0);
        Add(command, "$qty_on_hand", v.QtyOnHand);
        Add(command, "$min_qty", v.MinQty);
        Add(command, "$max_qty", v.MaxQty);
        Add(command, "$reorder_qty", v.ReorderQty);
        Add(command, "$safety_stock", v.SafetyStock);
        Add(command, "$default_warehouse", (object?)v.DefaultWarehouse ?? DBNull.Value);
        Add(command, "$shelf_bin", (object?)v.ShelfBin ?? DBNull.Value);
        Add(command, "$lead_time_days", v.LeadTimeDays);
        Add(command, "$lot_size", v.LotSize);
        Add(command, "$order_multiple", v.OrderMultiple);
        Add(command, "$economic_order_qty", v.EconomicOrderQty);
        Add(command, "$replenishment_method", v.ReplenishmentMethod);
        Add(command, "$has_expiry", v.HasExpiry ? 1 : 0);
        Add(command, "$shelf_life_days", v.ShelfLifeDays);
        Add(command, "$alert_before_expiry_days", v.AlertBeforeExpiryDays);
        Add(command, "$has_serial", v.HasSerial ? 1 : 0);
        Add(command, "$has_batch", v.HasBatch ? 1 : 0);
        Add(command, "$has_lot", v.HasLot ? 1 : 0);
        Add(command, "$track_location", v.TrackLocation ? 1 : 0);
        Add(command, "$quality_check_required", v.QualityCheckRequired ? 1 : 0);
        Add(command, "$is_taxable", v.IsTaxable ? 1 : 0);
        Add(command, "$tax_rate", v.TaxRate);
        Add(command, "$tax_code", (object?)v.TaxCode ?? DBNull.Value);
        Add(command, "$tax_account", (object?)v.TaxAccount ?? DBNull.Value);
        Add(command, "$tax_type", (object?)v.TaxType ?? DBNull.Value);
        Add(command, "$is_zero_rated", v.IsZeroRated ? 1 : 0);
        Add(command, "$acc_inventory", (object?)v.AccInventory ?? DBNull.Value);
        Add(command, "$acc_sales", (object?)v.AccSales ?? DBNull.Value);
        Add(command, "$acc_sales_return", (object?)v.AccSalesReturn ?? DBNull.Value);
        Add(command, "$acc_cogs", (object?)v.AccCogs ?? DBNull.Value);
        Add(command, "$acc_purchase", (object?)v.AccPurchase ?? DBNull.Value);
        Add(command, "$acc_purchase_return", (object?)v.AccPurchaseReturn ?? DBNull.Value);
        Add(command, "$acc_expense", (object?)v.AccExpense ?? DBNull.Value);
        Add(command, "$acc_wip", (object?)v.AccWip ?? DBNull.Value);
        Add(command, "$allow_sale", v.AllowSale ? 1 : 0);
        Add(command, "$allow_purchase", v.AllowPurchase ? 1 : 0);
        Add(command, "$allow_return", v.AllowReturn ? 1 : 0);
        Add(command, "$allow_negative", v.AllowNegative ? 1 : 0);
        Add(command, "$commission_pct", v.CommissionPct);
        Add(command, "$bonus_qty", v.BonusQty);
        Add(command, "$bonus_item_code", (object?)v.BonusItemCode ?? DBNull.Value);
        Add(command, "$min_sale_qty", v.MinSaleQty);
        Add(command, "$max_sale_qty", v.MaxSaleQty);
        Add(command, "$sales_blocked", v.SalesBlocked ? 1 : 0);
        Add(command, "$image_path", (object?)v.ImagePath ?? DBNull.Value);
        Add(command, "$image_path2", (object?)v.ImagePath2 ?? DBNull.Value);
        Add(command, "$attachment_path", (object?)v.AttachmentPath ?? DBNull.Value);
        Add(command, "$description", v.Description);
        Add(command, "$notes", v.Notes);
        Add(command, "$internal_notes", v.InternalNotes);
        Add(command, "$spec_sheet", v.SpecSheet);
        Add(command, "$label_text", v.LabelText);
        Add(command, "$units_json", v.UnitsJson);
        Add(command, "$prices_json", v.PricesJson);
        Add(command, "$attributes_json", v.AttributesJson);
        Add(command, "$is_deleted", 0);
        Add(command, "$created_by", (object?)v.CreatedBy ?? DBNull.Value);
        Add(command, "$updated_by", (object?)v.UpdatedBy ?? DBNull.Value);
    }

    private static void LoadChildren(DbConnection connection, MatRec rec)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT attr_id, attr_key, attr_value, attr_group, sort_order FROM material_attributes WHERE company_id=$company AND material_id=$id ORDER BY sort_order, attr_key";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rec.Attributes.Add(new MatAttrRow { Id = Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture), Key = rd.GetValue(1)?.ToString() ?? "", Value = rd.GetValue(2)?.ToString() ?? "", Group = rd.GetValue(3)?.ToString() ?? "", SortOrder = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4), CultureInfo.InvariantCulture) });
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT barcode_id, barcode, barcode_type, unit_name FROM material_extra_barcodes WHERE company_id=$company AND material_id=$id";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rec.ExtraBarcodes.Add(new MatBarcode { Id = Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture), Barcode = rd.GetValue(1)?.ToString() ?? "", BarcodeType = rd.GetValue(2)?.ToString() ?? "EAN", UnitName = rd.IsDBNull(3) ? null : rd.GetValue(3)?.ToString() });
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT price_id, price_level, price, currency, unit_name, valid_from, valid_to FROM material_price_list WHERE company_id=$company AND material_id=$id";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rec.PriceLines.Add(new MatPriceLine { Id = Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture), PriceLevel = rd.GetValue(1)?.ToString() ?? "", Price = rd.IsDBNull(2) ? 0m : Convert.ToDecimal(rd.GetValue(2), CultureInfo.InvariantCulture), Currency = rd.GetValue(3)?.ToString() ?? "", UnitName = rd.IsDBNull(4) ? null : rd.GetValue(4)?.ToString(), ValidFrom = rd.IsDBNull(5) ? null : rd.GetValue(5)?.ToString(), ValidTo = rd.IsDBNull(6) ? null : rd.GetValue(6)?.ToString() });
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, supplier_code, supplier_item_code, lead_time_days, min_order_qty, is_preferred FROM material_suppliers WHERE company_id=$company AND material_id=$id";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rec.Suppliers.Add(new MatSupplier { Id = Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture), SupplierCode = rd.GetValue(1)?.ToString() ?? "", SupplierItemCode = rd.IsDBNull(2) ? null : rd.GetValue(2)?.ToString(), LeadTimeDays = rd.IsDBNull(3) ? 0 : Convert.ToInt32(rd.GetValue(3), CultureInfo.InvariantCulture), MinOrderQty = rd.IsDBNull(4) ? 0m : Convert.ToDecimal(rd.GetValue(4), CultureInfo.InvariantCulture), IsPreferred = ReadBool(rd, 5) });
        }
    }

    private static void SaveChildren(DbConnection connection, DbTransaction tx, MatRec rec)
    {
        foreach (var a in rec.Attributes)
        {
            using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO material_attributes (company_id, material_id, attr_key, attr_value, attr_group, sort_order) VALUES ($company,$id,$k,$v,$g,$s)";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id); Add(cmd, "$k", a.Key); Add(cmd, "$v", a.Value); Add(cmd, "$g", a.Group); Add(cmd, "$s", a.SortOrder);
            cmd.ExecuteNonQuery();
        }
        foreach (var b in rec.ExtraBarcodes)
        {
            using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO material_extra_barcodes (company_id, material_id, barcode, barcode_type, unit_name) VALUES ($company,$id,$b,$t,$u)";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id); Add(cmd, "$b", b.Barcode); Add(cmd, "$t", b.BarcodeType); Add(cmd, "$u", (object?)b.UnitName ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        foreach (var p in rec.PriceLines)
        {
            using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO material_price_list (company_id, material_id, price_level, price, currency, unit_name, valid_from, valid_to) VALUES ($company,$id,$l,$p,$c,$u,$f,$t)";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id); Add(cmd, "$l", p.PriceLevel); Add(cmd, "$p", p.Price); Add(cmd, "$c", p.Currency); Add(cmd, "$u", (object?)p.UnitName ?? DBNull.Value); Add(cmd, "$f", (object?)p.ValidFrom ?? DBNull.Value); Add(cmd, "$t", (object?)p.ValidTo ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        foreach (var s in rec.Suppliers)
        {
            using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO material_suppliers (company_id, material_id, supplier_code, supplier_item_code, lead_time_days, min_order_qty, is_preferred) VALUES ($company,$id,$sc,$si,$lt,$mq,$pr)";
            Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty); Add(cmd, "$id", rec.Id); Add(cmd, "$sc", s.SupplierCode); Add(cmd, "$si", (object?)s.SupplierItemCode ?? DBNull.Value); Add(cmd, "$lt", s.LeadTimeDays); Add(cmd, "$mq", s.MinOrderQty); Add(cmd, "$pr", s.IsPreferred ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    private static DbConnection Open(string connectionString)
    {
        DbConnection connection = DatabaseRuntime.IsPostgreSql()
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(DatabaseRuntime.NormalizeSqliteConnectionString(connectionString));
        connection.Open();
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

	private static void EnsureGroupCardSchema(DbConnection conn)
	{
		using var command = conn.CreateCommand();
		command.CommandText = DatabaseRuntime.IsPostgreSql()
			? """
			  SELECT COUNT(*) FROM information_schema.tables
			  WHERE table_schema = current_schema()
			    AND table_name IN ('material_groups', 'material_group_sequences')
			  """
			: """
			  SELECT COUNT(*) FROM sqlite_master
			  WHERE type = 'table'
			    AND name IN ('material_groups', 'material_group_sequences')
			  """;
		if (Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) != 2)
			throw new InvalidOperationException("Material-group schema is missing. Apply the provider migrations under dba/mig.");
	}

	// ====================================================================
	// Warehouse master
	// ====================================================================

	/// <summary>
	/// Warehouse master row for WhsWin.
	///
	/// Data contract:
	///   Id         - internal primary key (warehouse_id), never shown to the user
	///   Code       - system-generated numeric warehouse code (unique per company)
	///   CardNo     - user-entered numeric warehouse number (unique per company when &gt; 0)
	///   ParentCode - Code of the parent warehouse, or null for a root warehouse
	/// </summary>
	public sealed class WarehouseRec
	{
		public long Id { get; set; }

		/// <summary>System-generated numeric warehouse code (read-only in UI).</summary>
		public long Code { get; set; }

		/// <summary>User-entered numeric warehouse number. 0 means not assigned.</summary>
		public long CardNo { get; set; }

		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";

		/// <summary>Code of the parent warehouse, or null for a root warehouse.</summary>
		public long? ParentCode { get; set; }

		public bool IsDefault { get; set; }
		public bool IsActive { get; set; } = true;
		public bool IsDeleted { get; set; }

		public string? Address { get; set; }
		public string? City { get; set; }
		public string? Region { get; set; }
		public string? Country { get; set; }
		public string? Phone { get; set; }
		public string? Aisle { get; set; }
		public string? Rack { get; set; }
		public string? Shelf { get; set; }
		public string? Notes { get; set; }

		/// <summary>UI label used by ComboBox and TreeView.</summary>
		public string Display
		{
			get
			{
				// Sentinel entry used by the parent ComboBox ("no parent").
				if (Id == 0)
					return "(No Parent)";

				var codeText = Code.ToString(CultureInfo.InvariantCulture);

				if (!string.IsNullOrWhiteSpace(Name))
					return $"{codeText} - {Name.Trim()}";

				if (!string.IsNullOrWhiteSpace(NameEn))
					return $"{codeText} - {NameEn.Trim()}";

				return codeText;
			}
		}
	}

	internal static void EnsureWarehouseSchema(DbConnection conn)
	{
		using var command = conn.CreateCommand();
		command.CommandText = DatabaseRuntime.IsPostgreSql()
			? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'warehouses';"
			: "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'warehouses';";
		if (Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) != 1)
			throw new InvalidOperationException("Warehouse schema is missing. Apply the provider migrations under dba/mig.");
	}

	private static string RequireCurrentCompanyCode()
	{
		var company = AppState.CurrentCompanyCode?.Trim();

		if (string.IsNullOrWhiteSpace(company))
			throw new InvalidOperationException(
				"WHS-005: No current company is selected.");

		return company;
	}

	private static void LogWarehouseError(string operation, Exception ex)
	{
		try
		{
			var logDir = Path.Combine(AppContext.BaseDirectory, "log");
			Directory.CreateDirectory(logDir);

			var path = Path.Combine(logDir, "log.log");

			var text =
				$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " +
				$"[Warehouse] [{operation}] " +
				$"{ex}\r\n";

			File.AppendAllText(path, text);
		}
		catch
		{
			// Logging must never break the application.
		}
	}

	/// <summary>
	/// Loads active warehouses for the current company. If the company has no
	/// active warehouse, a default "Main" warehouse is created with warehouse_code = 1.
	/// </summary>
	public static List<WarehouseRec> LoadWarehouses()
	{
		try
		{
			var cs = DatabaseRuntime.GetConnectionString();

			if (string.IsNullOrWhiteSpace(cs))
			{
				throw new InvalidOperationException(
					"WHS-004: Current company database connection is unavailable.");
			}

			var company = RequireCurrentCompanyCode();

			using var conn = Open(cs);
			EnsureWarehouseSchema(conn);

			var result = ReadActiveWarehouses(conn, company);

			if (result.Count == 0)
				result = InsertMainWarehouse(conn, company);

			return result;
		}
		catch (Exception ex)
		{
			LogWarehouseError("LoadWarehouses", ex);

			throw new InvalidOperationException(
				$"WHS-003: Failed to load warehouses. {ex.Message}",
				ex);
		}
	}

	private static List<WarehouseRec> ReadActiveWarehouses(
		DbConnection conn,
		string company)
	{
		var result = new List<WarehouseRec>();

		using var cmd = conn.CreateCommand();

		cmd.CommandText = @"
			SELECT
				warehouse_id,
				card_no,
				warehouse_code,
				warehouse_name_ar,
				warehouse_name_en,
				parent_code,
				is_default,
				is_active,
				address_line1,
				city,
				region,
				country,
				phone,
				aisle,
				rack,
				shelf,
				notes
			FROM warehouses
			WHERE company_id = $co
			  AND is_deleted = 0
			  AND is_active  = 1
			ORDER BY warehouse_id;";

		Add(cmd, "$co", company);

		using var rd = cmd.ExecuteReader();

		while (rd.Read())
			result.Add(MapWarehouse(rd));

		return result;
	}

	private static WarehouseRec MapWarehouse(DbDataReader rd)
	{
		var id = rd.IsDBNull(0)
			? 0L
			: Convert.ToInt64(rd.GetValue(0), CultureInfo.InvariantCulture);

		return new WarehouseRec
		{
			Id = id,

			CardNo = rd.IsDBNull(1)
				? 0L
				: Convert.ToInt64(rd.GetValue(1), CultureInfo.InvariantCulture),

			Code = rd.IsDBNull(2)
				? 0L
				: Convert.ToInt64(rd.GetValue(2), CultureInfo.InvariantCulture),

			Name = rd.IsDBNull(3)
				? ""
				: rd.GetValue(3)?.ToString() ?? "",

			NameEn = rd.IsDBNull(4)
				? ""
				: rd.GetValue(4)?.ToString() ?? "",

			ParentCode = rd.IsDBNull(5)
				? (long?)null
				: Convert.ToInt64(rd.GetValue(5), CultureInfo.InvariantCulture),

			IsDefault =
				!rd.IsDBNull(6)
				&& Convert.ToInt32(rd.GetValue(6), CultureInfo.InvariantCulture) != 0,

			IsActive =
				rd.IsDBNull(7)
				|| Convert.ToInt32(rd.GetValue(7), CultureInfo.InvariantCulture) != 0,

			IsDeleted = false,

			Address = rd.IsDBNull(8)  ? null : rd.GetValue(8)?.ToString(),
			City    = rd.IsDBNull(9)  ? null : rd.GetValue(9)?.ToString(),
			Region  = rd.IsDBNull(10) ? null : rd.GetValue(10)?.ToString(),
			Country = rd.IsDBNull(11) ? null : rd.GetValue(11)?.ToString(),
			Phone   = rd.IsDBNull(12) ? null : rd.GetValue(12)?.ToString(),
			Aisle   = rd.IsDBNull(13) ? null : rd.GetValue(13)?.ToString(),
			Rack    = rd.IsDBNull(14) ? null : rd.GetValue(14)?.ToString(),
			Shelf   = rd.IsDBNull(15) ? null : rd.GetValue(15)?.ToString(),
			Notes   = rd.IsDBNull(16) ? null : rd.GetValue(16)?.ToString()
		};
	}

	private static List<WarehouseRec> InsertMainWarehouse(
		DbConnection conn,
		string company)
	{
		// Never pass warehouse_id explicitly: on PostgreSQL it would desynchronize
		// the BIGSERIAL sequence and break every subsequent insert.
		using (var tx = conn.BeginTransaction())
		{
			try
			{
				using (var ins = conn.CreateCommand())
				{
					ins.Transaction = tx;

					ins.CommandText = DatabaseRuntime.IsPostgreSql()
						? @"
							INSERT INTO warehouses
							  (company_id, card_no, warehouse_code,
							   warehouse_name_ar, warehouse_name_en,
							   is_default, is_active, is_deleted, created_at)
							VALUES
							  ($co, 0, 1, 'Main', 'Main', 1, 1, 0, NOW());"
						: @"
							INSERT INTO warehouses
							  (company_id, card_no, warehouse_code,
							   warehouse_name_ar, warehouse_name_en,
							   is_default, is_active, is_deleted, created_at)
							VALUES
							  ($co, 0, 1, 'Main', 'Main', 1, 1, 0, CURRENT_TIMESTAMP);";

					Add(ins, "$co", company);

					var affected = ins.ExecuteNonQuery();

					if (affected != 1)
					{
						throw new InvalidOperationException(
							"WHS-008: Main warehouse insert affected zero rows.");
					}
				}

				tx.Commit();
			}
			catch
			{
				try { tx.Rollback(); } catch { /* ignored */ }
				throw;
			}
		}

		return ReadActiveWarehouses(conn, company);
	}

	/// <summary>
	/// Returns the next system-generated numeric warehouse code for the current
	/// company. Numbers are contiguous and start at 1 (no zero-padding).
	/// </summary>
	public static long NextWarehouseCode()
	{
		var cs = DatabaseRuntime.GetConnectionString();

		if (string.IsNullOrWhiteSpace(cs))
		{
			throw new InvalidOperationException(
				"WHS-004: Current company database connection is unavailable.");
		}

		var company = RequireCurrentCompanyCode();

		using var conn = Open(cs);
		EnsureWarehouseSchema(conn);

		using var cmd = conn.CreateCommand();

		cmd.CommandText = @"
			SELECT COALESCE(MAX(warehouse_code), 0)
			FROM warehouses
			WHERE company_id = $co
			  AND is_deleted = 0;";

		Add(cmd, "$co", company);

		var max = Convert.ToInt64(
			cmd.ExecuteScalar() ?? 0L,
			CultureInfo.InvariantCulture);

		return checked(max + 1);
	}

	/// <summary>
	/// Insert or update a warehouse for the current company.
	/// Both operations are transactional and verify the affected row.
	/// </summary>
	public static WarehouseRec SaveWarehouse(
		WarehouseRec rec,
		bool isNew)
	{
		ArgumentNullException.ThrowIfNull(rec);

		if (rec.CardNo <= 0)
		{
			throw new ArgumentException(
				"WHS-006: Warehouse number is required and must be positive.",
				nameof(rec));
		}

		if (string.IsNullOrWhiteSpace(rec.Name)
			&& string.IsNullOrWhiteSpace(rec.NameEn))
		{
			throw new ArgumentException(
				"WHS-007: Warehouse name is required.",
				nameof(rec));
		}

		var cs = DatabaseRuntime.GetConnectionString();

		if (string.IsNullOrWhiteSpace(cs))
		{
			throw new InvalidOperationException(
				"WHS-004: Current company database connection is unavailable.");
		}

		try
		{
			var company = RequireCurrentCompanyCode();

			using var conn = Open(cs);
			EnsureWarehouseSchema(conn);

			using var tx = conn.BeginTransaction();

			try
			{
				EnsureCardNoIsUnique(conn, tx, company, rec, isNew);

				if (rec.IsDefault)
					ClearDefaultFlag(conn, tx, company);

				if (isNew)
					InsertWarehouse(conn, tx, company, rec);
				else
					UpdateWarehouse(conn, tx, company, rec);

				tx.Commit();
				return rec;
			}
			catch
			{
				try { tx.Rollback(); } catch { /* ignored */ }
				throw;
			}
		}
		catch (Exception ex)
		{
			LogWarehouseError(
				isNew ? "SaveWarehouse.Insert" : "SaveWarehouse.Update",
				ex);

			throw;
		}
	}

	private static void EnsureCardNoIsUnique(
		DbConnection conn,
		DbTransaction tx,
		string company,
		WarehouseRec rec,
		bool isNew)
	{
		if (rec.CardNo <= 0)
			return;

		using var chk = conn.CreateCommand();
		chk.Transaction = tx;

		chk.CommandText = isNew
			? @"SELECT COUNT(1) FROM warehouses
				WHERE company_id = $co AND card_no = $card AND is_deleted = 0;"
			: @"SELECT COUNT(1) FROM warehouses
				WHERE company_id = $co AND card_no = $card
				  AND is_deleted = 0 AND warehouse_id <> $id;";

		Add(chk, "$co",   company);
		Add(chk, "$card", rec.CardNo);

		if (!isNew)
			Add(chk, "$id", rec.Id);

		var count = Convert.ToInt64(
			chk.ExecuteScalar() ?? 0L,
			CultureInfo.InvariantCulture);

		if (count > 0)
		{
			throw new InvalidOperationException(
				"WHS-006: Warehouse number already exists for this company.");
		}
	}

	private static void ClearDefaultFlag(
		DbConnection conn,
		DbTransaction tx,
		string company)
	{
		using var clr = conn.CreateCommand();
		clr.Transaction = tx;

		clr.CommandText = @"
			UPDATE warehouses
			SET is_default = 0
			WHERE company_id = $co
			  AND is_deleted = 0;";

		Add(clr, "$co", company);
		clr.ExecuteNonQuery();
	}

	private static void InsertWarehouse(
		DbConnection conn,
		DbTransaction tx,
		string company,
		WarehouseRec rec)
	{
		// Assign the next system code inside the same transaction.
		long nextCode;

		using (var seq = conn.CreateCommand())
		{
			seq.Transaction = tx;

			seq.CommandText = @"
				SELECT COALESCE(MAX(warehouse_code), 0) + 1
				FROM warehouses
				WHERE company_id = $co;";

			Add(seq, "$co", company);

			nextCode = Convert.ToInt64(
				seq.ExecuteScalar() ?? 1L,
				CultureInfo.InvariantCulture);
		}

		if (nextCode <= 0)
			nextCode = 1;

		rec.Code = nextCode;

		using var ins = conn.CreateCommand();
		ins.Transaction = tx;

		var insertSql = @"
			INSERT INTO warehouses
			(
				company_id, card_no, warehouse_code,
				warehouse_name_ar, warehouse_name_en, parent_code,
				address_line1, city, region, country, phone,
				aisle, rack, shelf, notes,
				is_default, is_active, is_deleted, created_at
			)
			VALUES
			(
				$co, $card, $code,
				$na, $ne, $pc,
				$ad, $ci, $re, $cy, $ph,
				$ai, $rk, $sh, $no,
				$def, 1, 0, {0}
			)";

		ins.CommandText = string.Format(
			CultureInfo.InvariantCulture,
			insertSql,
			DatabaseRuntime.IsPostgreSql() ? "NOW()" : "CURRENT_TIMESTAMP");

		if (DatabaseRuntime.IsPostgreSql())
			ins.CommandText += " RETURNING warehouse_id;";

		Add(ins, "$co",   company);
		Add(ins, "$card", rec.CardNo);
		Add(ins, "$code", rec.Code);
		Add(ins, "$na",   rec.Name ?? "");
		Add(ins, "$ne",   rec.NameEn ?? "");
		Add(ins, "$pc",   rec.ParentCode.HasValue
							? (object)rec.ParentCode.Value
							: DBNull.Value);
		Add(ins, "$ad",   (object?)rec.Address ?? DBNull.Value);
		Add(ins, "$ci",   (object?)rec.City    ?? DBNull.Value);
		Add(ins, "$re",   (object?)rec.Region  ?? DBNull.Value);
		Add(ins, "$cy",   (object?)rec.Country ?? DBNull.Value);
		Add(ins, "$ph",   (object?)rec.Phone   ?? DBNull.Value);
		Add(ins, "$ai",   (object?)rec.Aisle   ?? DBNull.Value);
		Add(ins, "$rk",   (object?)rec.Rack    ?? DBNull.Value);
		Add(ins, "$sh",   (object?)rec.Shelf   ?? DBNull.Value);
		Add(ins, "$no",   (object?)rec.Notes   ?? DBNull.Value);
		Add(ins, "$def",  rec.IsDefault ? 1 : 0);

		if (DatabaseRuntime.IsPostgreSql())
		{
			var idObj = ins.ExecuteScalar();

			if (idObj == null || idObj is DBNull)
			{
				throw new InvalidOperationException(
					"WHS-008: Warehouse ID was not returned by the database.");
			}

			rec.Id = Convert.ToInt64(idObj, CultureInfo.InvariantCulture);
		}
		else
		{
			var affected = ins.ExecuteNonQuery();

			if (affected != 1)
			{
				throw new InvalidOperationException(
					"WHS-008: Warehouse insert affected zero rows.");
			}

			using var idCmd = conn.CreateCommand();
			idCmd.Transaction = tx;
			idCmd.CommandText = "SELECT last_insert_rowid();";

			rec.Id = Convert.ToInt64(
				idCmd.ExecuteScalar(),
				CultureInfo.InvariantCulture);
		}

		if (rec.Id <= 0)
		{
			throw new InvalidOperationException(
				"WHS-008: Invalid warehouse ID returned.");
		}
	}

	private static void UpdateWarehouse(
		DbConnection conn,
		DbTransaction tx,
		string company,
		WarehouseRec rec)
	{
		if (rec.Id <= 0)
		{
			throw new InvalidOperationException(
				"WHS-009: Invalid warehouse ID.");
		}

		using var upd = conn.CreateCommand();
		upd.Transaction = tx;

		var updateSql = @"
			UPDATE warehouses SET
				card_no           = $card,
				warehouse_name_ar = $na,
				warehouse_name_en = $ne,
				parent_code       = $pc,
				address_line1     = $ad,
				city              = $ci,
				region            = $re,
				country           = $cy,
				phone             = $ph,
				aisle             = $ai,
				rack              = $rk,
				shelf             = $sh,
				notes             = $no,
				is_default        = $def,
				is_active         = $act,
				updated_at        = {0}
			WHERE warehouse_id = $id
			  AND company_id   = $co
			  AND is_deleted   = 0;";

		upd.CommandText = string.Format(
			CultureInfo.InvariantCulture,
			updateSql,
			DatabaseRuntime.IsPostgreSql() ? "NOW()" : "CURRENT_TIMESTAMP");

		Add(upd, "$card", rec.CardNo);
		Add(upd, "$na",   rec.Name ?? "");
		Add(upd, "$ne",   rec.NameEn ?? "");
		Add(upd, "$pc",   rec.ParentCode.HasValue
							? (object)rec.ParentCode.Value
							: DBNull.Value);
		Add(upd, "$ad",   (object?)rec.Address ?? DBNull.Value);
		Add(upd, "$ci",   (object?)rec.City    ?? DBNull.Value);
		Add(upd, "$re",   (object?)rec.Region  ?? DBNull.Value);
		Add(upd, "$cy",   (object?)rec.Country ?? DBNull.Value);
		Add(upd, "$ph",   (object?)rec.Phone   ?? DBNull.Value);
		Add(upd, "$ai",   (object?)rec.Aisle   ?? DBNull.Value);
		Add(upd, "$rk",   (object?)rec.Rack    ?? DBNull.Value);
		Add(upd, "$sh",   (object?)rec.Shelf   ?? DBNull.Value);
		Add(upd, "$no",   (object?)rec.Notes   ?? DBNull.Value);
		Add(upd, "$def",  rec.IsDefault ? 1 : 0);
		Add(upd, "$act",  rec.IsActive ? 1 : 0);
		Add(upd, "$id",   rec.Id);
		Add(upd, "$co",   company);

		var affected = upd.ExecuteNonQuery();

		if (affected != 1)
		{
			throw new KeyNotFoundException(
				$"WHS-010: Warehouse {rec.Id} was not found in the current company database.");
		}
	}

	/// <summary>Soft-delete warehouse by id for the current company.</summary>
	public static void SoftDeleteWarehouse(long id)
	{
		if (id <= 0)
			return;

		var cs = DatabaseRuntime.GetConnectionString();
		if (string.IsNullOrWhiteSpace(cs)) return;

		using var conn = Open(cs);
		EnsureWarehouseSchema(conn);

		var company = RequireCurrentCompanyCode();

		using var cmd = conn.CreateCommand();

		cmd.CommandText = @"
			UPDATE warehouses
			SET is_deleted = 1,
				is_active  = 0,
				is_default = 0
			WHERE warehouse_id = $id
			  AND company_id   = $co
			  AND is_deleted   = 0;";

		Add(cmd, "$id", id);
		Add(cmd, "$co", company);

		var affected = cmd.ExecuteNonQuery();

		if (affected != 1)
		{
			throw new KeyNotFoundException(
				$"WHS-002: Warehouse {id} was not found or was already deleted.");
		}
	}

	// ====================================================================
	// Material Groups (CatWin)
	// ====================================================================

	public static List<ProductCategoryRec> LoadProductCategories()
	{
		var result = new List<ProductCategoryRec>();
		var cs = DatabaseRuntime.GetConnectionString();
		if (string.IsNullOrWhiteSpace(cs)) return result;

		using var conn = Open(cs);
		using var cmd = conn.CreateCommand();
		var activeValue = DatabaseRuntime.IsPostgreSql() ? "TRUE" : "1";
		var notDeletedValue = DatabaseRuntime.IsPostgreSql() ? "FALSE" : "0";
		cmd.CommandText = $@"
			SELECT category_id, category_code, category_name_ar, category_name_en
			FROM product_categories
			WHERE company_id = $company
			  AND is_active = {activeValue}
			  AND is_deleted = {notDeletedValue}
			ORDER BY category_code;";
		Add(cmd, "$company", AppState.CurrentCompanyCode ?? string.Empty);
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			result.Add(new ProductCategoryRec
			{
				Id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "",
				Code = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "",
				Name = reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? "",
				NameEn = reader.IsDBNull(3) ? "" : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? ""
			});
		}
		return result;
	}

	public static List<MatGroupRec> LoadGroups()
	{
		var result = new List<MatGroupRec>();

		try
		{
			var cs = DatabaseRuntime.GetConnectionString();

			if (string.IsNullOrWhiteSpace(cs))
				return result;

			using var conn = Open(cs);

			EnsureGroupCardSchema(conn);

			using var cmd = conn.CreateCommand();

			cmd.CommandText = @"
				SELECT
					group_id,
					company_id,
					card_no,
					group_code,
					group_name,
					group_name_en,
					parent_code,
					warehouse,
					notes,
					is_sales_class,
					is_deleted,
					status,
					general_specs,
					image_path,
					created_by,
					created_at,
					updated_by,
					updated_at
				FROM material_groups
				WHERE company_id = $co
				  AND is_deleted = 0
				ORDER BY card_no;";

			Add(
				cmd,
				"$co",
				AppState.CurrentCompanyCode ?? string.Empty);

			using var reader = cmd.ExecuteReader();

			while (reader.Read())
			{
				result.Add(new MatGroupRec
				{
					Id = reader.IsDBNull(0)
						? 0
						: Convert.ToInt64(
							reader.GetValue(0),
							CultureInfo.InvariantCulture),

					CardNo = reader.IsDBNull(2)
						? 0
						: Convert.ToInt64(
							reader.GetValue(2),
							CultureInfo.InvariantCulture),

					Code = reader.IsDBNull(3)
						? ""
						: reader.GetValue(3)?.ToString() ?? "",

					Name = reader.IsDBNull(4)
						? ""
						: reader.GetValue(4)?.ToString() ?? "",

					NameEn = reader.IsDBNull(5)
						? ""
						: reader.GetValue(5)?.ToString() ?? "",

					ParentCode = reader.IsDBNull(6)
						? null
						: reader.GetValue(6)?.ToString(),

					Warehouse = reader.IsDBNull(7)
						? null
						: reader.GetValue(7)?.ToString(),

					Notes = reader.IsDBNull(8)
						? ""
						: reader.GetValue(8)?.ToString() ?? "",

					IsSalesClass =
						!reader.IsDBNull(9) &&
						Convert.ToInt32(
							reader.GetValue(9),
							CultureInfo.InvariantCulture) != 0,

					IsDeleted =
						!reader.IsDBNull(10) &&
						Convert.ToInt32(
							reader.GetValue(10),
							CultureInfo.InvariantCulture) != 0,

					Status = reader.IsDBNull(11)
						? "Active"
						: reader.GetValue(11)?.ToString() ?? "Active",

					GeneralSpecs = reader.FieldCount > 12 && !reader.IsDBNull(12)
						? reader.GetValue(12)?.ToString() ?? ""
						: "",

					ImagePath = reader.FieldCount > 13 && !reader.IsDBNull(13)
						? reader.GetValue(13)?.ToString()
						: null,

					CreatedBy = reader.FieldCount > 14 && !reader.IsDBNull(14)
						? reader.GetValue(14)?.ToString()
						: null,

					CreatedAt = reader.FieldCount > 15 && !reader.IsDBNull(15)
						? reader.GetValue(15)?.ToString()
						: null,

					UpdatedBy = reader.FieldCount > 16 && !reader.IsDBNull(16)
						? reader.GetValue(16)?.ToString()
						: null,

					UpdatedAt = reader.FieldCount > 17 && !reader.IsDBNull(17)
						? reader.GetValue(17)?.ToString()
						: null
				});
			}
		}
		catch
		{
			// Existing project behavior.
		}

		return result;
	}

	/// <summary>
	/// Returns a display preview of the next permanent category-card number.
	/// The actual number is assigned again transactionally when the record is saved.
	/// </summary>
	public static long GetNextGroupCardNoPreview()
	{
		var company = AppState.CurrentCompanyCode ?? string.Empty;
		if (string.IsNullOrWhiteSpace(company))
			throw new InvalidOperationException("Current company is not defined.");

		var cs = DatabaseRuntime.GetConnectionString();
		if (string.IsNullOrWhiteSpace(cs))
			throw new InvalidOperationException("Database connection is not configured.");

		using var conn = Open(cs);
		EnsureGroupCardSchema(conn);
		using var cmd = conn.CreateCommand();
		cmd.CommandText = @"
			SELECT MAX(
				COALESCE((SELECT last_card_no FROM material_group_sequences WHERE company_id = $co), 0),
				COALESCE((SELECT MAX(card_no) FROM material_groups WHERE company_id = $co), 0)
			) + 1;";
		Add(cmd, "$co", company);
		return Convert.ToInt64(cmd.ExecuteScalar() ?? 1L, CultureInfo.InvariantCulture);
	}

	// Gets the next permanent card number for the current company.
	// This number is never reused after deletion.
	private static long NextGroupCardNo(
		DbConnection conn,
		DbTransaction tx)
	{
		var company =
			AppState.CurrentCompanyCode ?? string.Empty;

		if (string.IsNullOrWhiteSpace(company))
			throw new InvalidOperationException(
				"Current company is not defined.");

		// Reconcile the sequence with all existing records (including soft-deleted
		// ones) before incrementing. This safely upgrades older databases where the
		// sequence table is missing a company row or is behind the stored card numbers.
		if (DatabaseRuntime.IsPostgreSql())
		{
			using var sync = conn.CreateCommand();
			sync.Transaction = tx;
			sync.CommandText = @"
				INSERT INTO material_group_sequences (company_id, last_card_no)
				SELECT $co, COALESCE(MAX(card_no), 0)
				FROM material_groups
				WHERE company_id = $co
				ON CONFLICT (company_id) DO UPDATE SET
					last_card_no = GREATEST(
						material_group_sequences.last_card_no,
						EXCLUDED.last_card_no);";
			Add(sync, "$co", company);
			sync.ExecuteNonQuery();
		}
		else
		{
			using var sync = conn.CreateCommand();
			sync.Transaction = tx;
			sync.CommandText = @"
				INSERT OR IGNORE INTO material_group_sequences (company_id, last_card_no)
				SELECT $co, COALESCE(MAX(card_no), 0)
				FROM material_groups
				WHERE company_id = $co;";
			Add(sync, "$co", company);
			sync.ExecuteNonQuery();

			using var reconcile = conn.CreateCommand();
			reconcile.Transaction = tx;
			reconcile.CommandText = @"
				UPDATE material_group_sequences
				SET last_card_no = MAX(
					last_card_no,
					(SELECT COALESCE(MAX(card_no), 0)
					 FROM material_groups WHERE company_id = $co))
				WHERE company_id = $co;";
			Add(reconcile, "$co", company);
			reconcile.ExecuteNonQuery();
		}

		if (DatabaseRuntime.IsPostgreSql())
		{
			using var cmd = conn.CreateCommand();
			cmd.Transaction = tx;

			cmd.CommandText = @"
				INSERT INTO material_group_sequences
					(company_id, last_card_no)
				VALUES
					($co, 1)
				ON CONFLICT (company_id)
				DO UPDATE SET
					last_card_no =
						material_group_sequences.last_card_no + 1
				RETURNING last_card_no;";

			Add(cmd, "$co", company);

			return Convert.ToInt64(
				cmd.ExecuteScalar(),
				CultureInfo.InvariantCulture);
		}

		// SQLite
		using (var update = conn.CreateCommand())
		{
			update.Transaction = tx;

			update.CommandText = @"
				UPDATE material_group_sequences
				SET last_card_no = last_card_no + 1
				WHERE company_id = $co;";

			Add(update, "$co", company);

			var affected = update.ExecuteNonQuery();

			if (affected == 0)
			{
				using var insert = conn.CreateCommand();
				insert.Transaction = tx;

				insert.CommandText = @"
					INSERT INTO material_group_sequences
						(company_id, last_card_no)
					VALUES
						($co, 1);";

				Add(insert, "$co", company);
				insert.ExecuteNonQuery();

				return 1;
			}
		}

		using (var select = conn.CreateCommand())
		{
			select.Transaction = tx;

			select.CommandText = @"
				SELECT last_card_no
				FROM material_group_sequences
				WHERE company_id = $co;";

			Add(select, "$co", company);

			return Convert.ToInt64(
				select.ExecuteScalar(),
				CultureInfo.InvariantCulture);
		}
	}

	public static MatGroupRec SaveGroup(
		MatGroupRec rec,
		bool isNew)
	{
		var cs = DatabaseRuntime.GetConnectionString();

		if (string.IsNullOrWhiteSpace(cs))
			return rec;

		using var conn = Open(cs);

		EnsureGroupCardSchema(conn);

		using var tx = conn.BeginTransaction();

		try
		{
			var company =
				AppState.CurrentCompanyCode ?? string.Empty;

			if (isNew)
			{
				// Generate permanent automatic card number.
				rec.CardNo = NextGroupCardNo(conn, tx);

				using var cmd = conn.CreateCommand();
				cmd.Transaction = tx;

				cmd.CommandText = @"
					INSERT INTO material_groups
					(
						company_id,
						card_no,
						group_code,
						group_name,
						group_name_en,
						parent_code,
						warehouse,
						notes,
						general_specs,
						image_path,
						is_sales_class,
						is_deleted,
						status,
						created_by,
						created_at
					)
					VALUES
					(
						$co,
						$card,
						$c,
						$n,
						$ne,
						$p,
						$w,
						$no,
						$gs,
						$img,
						$sc,
						0,
						$st,
						$uby,
						$uat
					);";

				var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
				var user = AppState.CurrentUser?.Username ?? "";

				Add(cmd, "$co", company);
				Add(cmd, "$card", rec.CardNo);
				Add(cmd, "$c", rec.Code);
				Add(cmd, "$n", rec.Name ?? "");
				Add(cmd, "$ne", rec.NameEn ?? "");
				Add(cmd, "$p", (object?)rec.ParentCode ?? DBNull.Value);
				Add(cmd, "$w", (object?)rec.Warehouse ?? DBNull.Value);
				Add(cmd, "$no", rec.Notes ?? "");
				Add(cmd, "$gs", rec.GeneralSpecs ?? "");
				Add(cmd, "$img", (object?)rec.ImagePath ?? DBNull.Value);
				Add(cmd, "$sc", rec.IsSalesClass ? 1 : 0);
				Add(cmd, "$st", rec.Status ?? "Active");
				Add(cmd, "$uby", user);
				Add(cmd, "$uat", now);

				cmd.ExecuteNonQuery();

				// Obtain internal database identity.
				using var idCmd = conn.CreateCommand();
				idCmd.Transaction = tx;

				if (DatabaseRuntime.IsPostgreSql())
				{
					idCmd.CommandText = @"
						SELECT group_id
						FROM material_groups
						WHERE company_id = $co
						  AND card_no = $card;";

					Add(idCmd, "$co", company);
					Add(idCmd, "$card", rec.CardNo);

					rec.Id = Convert.ToInt64(
						idCmd.ExecuteScalar(),
						CultureInfo.InvariantCulture);
				}
				else
				{
					idCmd.CommandText =
						"SELECT last_insert_rowid();";

					rec.Id = Convert.ToInt64(
						idCmd.ExecuteScalar(),
						CultureInfo.InvariantCulture);
				}
			}
			else
			{
				using var cmd = conn.CreateCommand();
				cmd.Transaction = tx;

				cmd.CommandText = @"
					UPDATE material_groups
					SET
						group_code = $c,
						group_name = $n,
						group_name_en = $ne,
						parent_code = $p,
						warehouse = $w,
						notes = $no,
						general_specs = $gs,
						image_path = $img,
						is_sales_class = $sc,
						status = $st,
						updated_by = $uby,
						updated_at = $uat
					WHERE company_id = $co
					  AND group_id = $id;";

				var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
				var user = AppState.CurrentUser?.Username ?? "";

				Add(cmd, "$co", company);
				Add(cmd, "$id", rec.Id);
				Add(cmd, "$c", rec.Code ?? "");
				Add(cmd, "$n", rec.Name ?? "");
				Add(cmd, "$ne", rec.NameEn ?? "");
				Add(cmd, "$p", (object?)rec.ParentCode ?? DBNull.Value);
				Add(cmd, "$w", (object?)rec.Warehouse ?? DBNull.Value);
				Add(cmd, "$no", rec.Notes ?? "");
				Add(cmd, "$gs", rec.GeneralSpecs ?? "");
				Add(cmd, "$img", (object?)rec.ImagePath ?? DBNull.Value);
				Add(cmd, "$sc", rec.IsSalesClass ? 1 : 0);
				Add(cmd, "$st", rec.Status ?? "Active");
				Add(cmd, "$uby", user);
				Add(cmd, "$uat", now);

				cmd.ExecuteNonQuery();
			}

			tx.Commit();
		}
		catch
		{
			try
			{
				tx.Rollback();
			}
			catch
			{
			}

			throw;
		}

		return rec;
	}

	public static string NextGroupCode()
	{
		try
		{
			var cs = DatabaseRuntime.GetConnectionString();

			if (string.IsNullOrWhiteSpace(cs))
				return "0001";

			using var conn = Open(cs);
			using var cmd = conn.CreateCommand();

			cmd.CommandText = @"
				SELECT group_code
				FROM material_groups
				WHERE company_id = $co
				  AND is_deleted = 0
				ORDER BY group_code DESC
				LIMIT 1;";

			Add(
				cmd,
				"$co",
				AppState.CurrentCompanyCode ?? "");

			var last = cmd.ExecuteScalar()?.ToString();

			if (string.IsNullOrWhiteSpace(last))
				return "0001";

			if (int.TryParse(last, out var n))
			{
				return (n + 1).ToString(
					"D4",
					CultureInfo.InvariantCulture);
			}

			return "0001";
		}
		catch
		{
			return "0001";
		}
	}

	public static void SoftDeleteGroup(long id)
	{
		if (id <= 0)
			throw new ArgumentOutOfRangeException(nameof(id), "Material group id must be positive.");

		var companyCode = AppState.CurrentCompanyCode;
		if (string.IsNullOrWhiteSpace(companyCode))
			throw new InvalidOperationException("A company must be selected before deleting a material group.");

		var cs = DatabaseRuntime.GetConnectionString();
		if (string.IsNullOrWhiteSpace(cs))
			throw new InvalidOperationException("The company database connection is not configured.");

		EnsureSchema();
		using var conn = Open(cs);
		EnsureGroupCardSchema(conn);
		using var cmd = conn.CreateCommand();

		cmd.CommandText = @"
			UPDATE material_groups
			SET is_deleted = 1,
			    deleted_at = $deletedAt,
			    deleted_by = $deletedBy
			WHERE company_id = $co
			  AND group_id = $id
			  AND COALESCE(is_deleted, 0) = 0;";

		Add(cmd, "$co", companyCode);
		Add(cmd, "$id", id);
		Add(cmd, "$deletedAt", DateTimeOffset.UtcNow);
		Add(cmd, "$deletedBy", AppState.CurrentUser?.Username);

		var affected = cmd.ExecuteNonQuery();
		if (affected == 0)
			throw new InvalidOperationException("The material group was not found in the selected company or has already been deleted.");
	}
	}
}
