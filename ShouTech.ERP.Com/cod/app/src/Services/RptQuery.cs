using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;

namespace ShouTech.App.Services;

/// <summary>
/// Shared, company-scoped query surface for standard report windows.
/// Failures never crash the UI: empty result sets are returned when schema
/// or connection is unavailable so reports remain usable during setup.
/// </summary>
public static class RptQuery
{
    private static string Company => AppState.CurrentCompanyCode ?? string.Empty;
    private static object DateStart(DateTime value)
        => DatabaseRuntime.IsPostgreSql()
            ? DateTime.SpecifyKind(value.Date, DateTimeKind.Utc)
            : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object DateEndExclusive(DateTime value)
        => DateStart(value.Date.AddDays(1));

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> TrialBalanceAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT a.account_code AS Code,
                   a.name_ar AS Name,
                   a.name_en AS NameEn,
                   COALESCE(a.debit, 0) AS Debit,
                   COALESCE(a.credit, 0) AS Credit,
                   COALESCE(a.balance, 0) AS Balance
            FROM accounts a
            WHERE a.company_id = $company AND a.is_deleted = 0
            ORDER BY a.account_code;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BalanceSheetAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = """
            SELECT a.account_code AS Code,
                   a.name_ar AS Name,
                   a.name_en AS NameEn,
                   CASE WHEN a.account_code LIKE '2%' OR a.account_code LIKE '3%'
                        THEN SUM(COALESCE(l.credit, 0) - COALESCE(l.debit, 0))
                        ELSE SUM(COALESCE(l.debit, 0) - COALESCE(l.credit, 0))
                   END AS Amount
            FROM accounts a
            INNER JOIN journal_entry_lines l
              ON l.account_id = CAST(a.account_id AS TEXT)
            INNER JOIN journal_entries j
              ON j.journal_entry_id = l.journal_entry_id
             AND j.company_id = a.company_id
            WHERE a.company_id = $company
              AND a.is_deleted = 0
              AND j.status = 1
              AND j.entry_date < $to
              AND (a.account_code LIKE '1%' OR a.account_code LIKE '2%' OR a.account_code LIKE '3%')
              AND NOT EXISTS (
                  SELECT 1
                  FROM accounts child
                  WHERE child.company_id = a.company_id
                    AND child.parent_code = a.account_code
                    AND child.is_deleted = 0
              )
            GROUP BY a.account_id, a.account_code, a.name_ar, a.name_en
            HAVING ABS(SUM(COALESCE(l.debit, 0) - COALESCE(l.credit, 0))) > 0.0001
            ORDER BY a.account_code;
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace("a.is_deleted = 0", "a.is_deleted = FALSE", StringComparison.Ordinal)
                .Replace("child.is_deleted = 0", "child.is_deleted = FALSE", StringComparison.Ordinal);
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$to", DateEndExclusive(asOf)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> IncomeStatementAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = """
            SELECT a.account_code AS Code,
                   a.name_ar AS Name,
                   a.name_en AS NameEn,
                   CASE WHEN a.account_code LIKE '5%'
                        THEN SUM(COALESCE(l.credit, 0) - COALESCE(l.debit, 0))
                        ELSE SUM(COALESCE(l.debit, 0) - COALESCE(l.credit, 0))
                   END AS Amount
            FROM accounts a
            INNER JOIN journal_entry_lines l
              ON l.account_id = CAST(a.account_id AS TEXT)
            INNER JOIN journal_entries j
              ON j.journal_entry_id = l.journal_entry_id
             AND j.company_id = a.company_id
            WHERE a.company_id = $company
              AND a.is_deleted = 0
              AND j.status = 1
              AND j.entry_date >= $from
              AND j.entry_date < $to
              AND (a.account_code LIKE '4%' OR a.account_code LIKE '5%' OR a.account_code LIKE '6%')
              AND NOT EXISTS (
                  SELECT 1
                  FROM accounts child
                  WHERE child.company_id = a.company_id
                    AND child.parent_code = a.account_code
                    AND child.is_deleted = 0
              )
            GROUP BY a.account_id, a.account_code, a.name_ar, a.name_en
            HAVING ABS(SUM(COALESCE(l.debit, 0) - COALESCE(l.credit, 0))) > 0.0001
            ORDER BY a.account_code;
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace("a.is_deleted = 0", "a.is_deleted = FALSE", StringComparison.Ordinal)
                .Replace("child.is_deleted = 0", "child.is_deleted = FALSE", StringComparison.Ordinal);
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", DateStart(from)),
            ("$to", DateEndExclusive(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AccountStatementAsync(
        string accountCode, DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT j.entry_date AS EntryDate,
                   j.journal_entry_id AS DocNo,
                   COALESCE(j.description, '') AS Description,
                   COALESCE(l.debit, 0) AS Debit,
                   COALESCE(l.credit, 0) AS Credit,
                   COALESCE(l.note, '') AS Note
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            WHERE j.company_id = $company
              AND l.account_id = $account
              AND j.entry_date >= $from
              AND j.entry_date <= $to
              AND j.status = 1
            ORDER BY j.entry_date, j.journal_entry_id, l.line_number;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$account", accountCode ?? string.Empty),
            ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> JournalListAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT j.entry_date AS EntryDate,
                   j.journal_entry_id AS DocNo,
                   COALESCE(j.description, '') AS Description,
                   CASE j.status WHEN 0 THEN 'Draft' WHEN 1 THEN 'Posted' ELSE 'Reversed' END AS Status,
                   COALESCE((SELECT SUM(debit) FROM journal_entry_lines x WHERE x.journal_entry_id = j.journal_entry_id), 0) AS Debit,
                   COALESCE((SELECT SUM(credit) FROM journal_entry_lines x WHERE x.journal_entry_id = j.journal_entry_id), 0) AS Credit,
                   COALESCE(j.created_by, '') AS CreatedBy
            FROM journal_entries j
            WHERE j.company_id = $company
              AND j.entry_date >= $from
              AND j.entry_date <= $to
            ORDER BY j.entry_date DESC, j.journal_entry_id DESC;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StockBalancesAsync(
        string? warehouseId,
        string? materialSearch,
        string? groupCode,
        string? stockStatus,
        CancellationToken ct)
    {
        DocSchema.Ensure();
        MatRepo.EnsureSchema();
        var sql = """
            SELECT COALESCE(w.warehouse_code, CAST(s.warehouse_id AS TEXT)) AS Warehouse,
                   COALESCE(m.code, CAST(s.material_id AS TEXT)) AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(m.name_en, '') AS MaterialNameEn,
                   COALESCE(m.default_unit, '') AS Unit,
                   COALESCE(s.available_qty, 0) AS Qty,
                   COALESCE(s.reserved_qty, 0) AS Reserved,
                   COALESCE(s.available_qty, 0) + COALESCE(s.reserved_qty, 0) AS OnHand,
                   COALESCE(m.min_qty, 0) AS MinQty,
                   COALESCE(s.average_cost, 0) AS AvgCost,
                   COALESCE(s.available_qty, 0) * COALESCE(s.average_cost, 0) AS Value
            FROM material_stock_balances s
            LEFT JOIN warehouses w
              ON w.company_id = s.company_id AND w.warehouse_id = s.warehouse_id
            LEFT JOIN materials m
              ON m.company_id = s.company_id AND m.material_id = s.material_id
            WHERE s.company_id = $company AND s.is_deleted = 0
              AND ($mat = '' OR LOWER(COALESCE(m.code, '')) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(COALESCE(m.name_ar, '')) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(COALESCE(m.name_en, '')) LIKE '%' || LOWER($mat) || '%')
              AND ($group = '' OR COALESCE(m.group_code, '') = $group)
              AND (
                    $state = ''
                    OR ($state = 'IN_STOCK' AND COALESCE(s.available_qty, 0) > 0)
                    OR ($state = 'OUT_OF_STOCK' AND COALESCE(s.available_qty, 0) = 0)
                    OR ($state = 'NEGATIVE' AND COALESCE(s.available_qty, 0) < 0)
                    OR ($state = 'REORDER' AND COALESCE(m.min_qty, 0) > 0
                        AND COALESCE(s.available_qty, 0) <= COALESCE(m.min_qty, 0))
                  )
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace("s.is_deleted = 0", "s.is_deleted = FALSE", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(warehouseId))
            sql += " AND CAST(s.warehouse_id AS TEXT) = $wh";
        sql += " ORDER BY Warehouse, MaterialCode;";

        var args = new List<(string Name, object? Value)>
        {
            ("$company", Company),
            ("$mat", materialSearch ?? string.Empty),
            ("$group", groupCode ?? string.Empty),
            ("$state", stockStatus ?? string.Empty)
        };
        if (!string.IsNullOrWhiteSpace(warehouseId))
            args.Add(("$wh", warehouseId));
        return await QueryAsync(sql, ct, args.ToArray());
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MaterialMovesAsync(
        string? materialSearch,
        string? warehouseId,
        string? movementType,
        string? documentNo,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        DocSchema.Ensure();
        MatRepo.EnsureSchema();
        var sql = """
            WITH ledger AS (
                SELECT sm.stock_movement_id AS MovementId,
                       sm.movement_date AS MoveDate,
                       sm.movement_type AS MoveType,
                       COALESCE(sm.document_no, '') AS DocNo,
                       sm.warehouse_id AS WarehouseId,
                       COALESCE(w.warehouse_code, CAST(sm.warehouse_id AS TEXT)) AS Warehouse,
                       COALESCE(w.warehouse_name_ar, '') AS WarehouseName,
                       COALESCE(w.warehouse_name_en, '') AS WarehouseNameEn,
                       sm.material_id AS MaterialId,
                       COALESCE(m.code, '') AS MaterialCode,
                       COALESCE(m.name_ar, '') AS MaterialName,
                       COALESCE(m.name_en, '') AS MaterialNameEn,
                       COALESCE(sm.qty_in, 0) AS QtyIn,
                       COALESCE(sm.qty_out, 0) AS QtyOut,
                       COALESCE(sm.unit_cost, 0) AS UnitCost,
                       COALESCE(sm.note, '') AS Note,
                       SUM(COALESCE(sm.qty_in, 0) - COALESCE(sm.qty_out, 0))
                           OVER (
                               PARTITION BY sm.warehouse_id, sm.material_id
                               ORDER BY sm.movement_date, sm.stock_movement_id
                               ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                           ) AS Balance
                FROM material_stock_movements sm
                LEFT JOIN warehouses w
                  ON w.company_id = sm.company_id AND w.warehouse_id = sm.warehouse_id
                LEFT JOIN materials m
                  ON m.company_id = sm.company_id AND m.material_id = sm.material_id
                WHERE sm.company_id = $company AND sm.is_deleted = 0
            )
            SELECT MovementId AS "MovementId",
                   MoveDate AS "MoveDate",
                   MoveType AS "MoveType",
                   DocNo AS "DocNo",
                   Warehouse AS "Warehouse",
                   WarehouseName AS "WarehouseName",
                   WarehouseNameEn AS "WarehouseNameEn",
                   MaterialCode AS "MaterialCode",
                   MaterialName AS "MaterialName",
                   MaterialNameEn AS "MaterialNameEn",
                   QtyIn AS "QtyIn",
                   QtyOut AS "QtyOut",
                   UnitCost AS "UnitCost",
                   (QtyIn - QtyOut) * UnitCost AS "MoveValue",
                   Balance AS "Balance",
                   Note AS "Note"
            FROM ledger
            WHERE MoveDate >= $from AND MoveDate < $to
              AND ($mat = '' OR LOWER(MaterialCode) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(MaterialName) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(MaterialNameEn) LIKE '%' || LOWER($mat) || '%')
              AND ($wh = '' OR CAST(WarehouseId AS TEXT) = $wh)
              AND ($type = '' OR MoveType = $type)
              AND ($doc = '' OR DocNo LIKE '%' || $doc || '%')
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace("sm.is_deleted = 0", "sm.is_deleted = FALSE", StringComparison.Ordinal);
        sql += " ORDER BY MoveDate, MovementId;";
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", DateStart(from)),
            ("$to", DateEndExclusive(to)),
            ("$mat", materialSearch ?? string.Empty),
            ("$wh", warehouseId ?? string.Empty),
            ("$type", movementType ?? string.Empty),
            ("$doc", documentNo ?? string.Empty));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CustomerBalancesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT c.customer_code AS Code,
                   c.customer_name_ar AS Name,
                   COALESCE(c.customer_name_en, '') AS NameEn,
                   COALESCE(c.phone, '') AS Phone,
                   COALESCE(bal.balance, 0) AS Balance
            FROM customers c
            LEFT JOIN (
                SELECT party_id, SUM(debit - credit) AS balance
                FROM party_ledger
                WHERE company_id = $company AND party_kind = 'C'
                GROUP BY party_id
            ) bal ON bal.party_id = c.customer_id
            WHERE c.company_id = $company AND c.is_deleted = 0 AND c.is_active = 1
            ORDER BY c.customer_code;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SupplierBalancesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT s.supplier_code AS Code,
                   s.supplier_name_ar AS Name,
                   COALESCE(s.supplier_name_en, '') AS NameEn,
                   COALESCE(s.phone, '') AS Phone,
                   COALESCE(bal.balance, 0) AS Balance
            FROM suppliers s
            LEFT JOIN (
                SELECT party_id, SUM(debit - credit) AS balance
                FROM party_ledger
                WHERE company_id = $company AND party_kind = 'S'
                GROUP BY party_id
            ) bal ON bal.party_id = s.supplier_id
            WHERE s.company_id = $company AND s.is_deleted = 0 AND s.is_active = 1
            ORDER BY s.supplier_code;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SalesInvoiceListAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_date AS DocDate,
                   d.doc_no AS DocNo,
                   d.doc_kind AS Kind,
                   COALESCE(c.customer_code, '') AS PartyCode,
                   COALESCE(c.customer_name_ar, d.party_name) AS PartyName,
                   COALESCE(d.subtotal, 0) AS Subtotal,
                   COALESCE(d.tax_amount, 0) AS Tax,
                   COALESCE(d.grand_total, 0) AS Total,
                   COALESCE(d.status, 'Draft') AS Status,
                   COALESCE(d.currency_code, 'SAR') AS Currency
            FROM sales_documents d
            LEFT JOIN customers c ON c.customer_id = d.party_id
            WHERE d.company_id = $company
              AND d.doc_date >= $from
              AND d.doc_date <= $to
              AND d.is_deleted = 0
            ORDER BY d.doc_date DESC, d.doc_no DESC;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PurchaseInvoiceListAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_date AS DocDate,
                   d.doc_no AS DocNo,
                   d.doc_kind AS Kind,
                   COALESCE(s.supplier_code, '') AS PartyCode,
                   COALESCE(s.supplier_name_ar, d.party_name) AS PartyName,
                   COALESCE(d.subtotal, 0) AS Subtotal,
                   COALESCE(d.tax_amount, 0) AS Tax,
                   COALESCE(d.grand_total, 0) AS Total,
                   COALESCE(d.status, 'Draft') AS Status,
                   COALESCE(d.currency_code, 'SAR') AS Currency
            FROM purchase_documents d
            LEFT JOIN suppliers s ON s.supplier_id = d.party_id
            WHERE d.company_id = $company
              AND d.doc_date >= $from
              AND d.doc_date <= $to
              AND d.is_deleted = 0
            ORDER BY d.doc_date DESC, d.doc_no DESC;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }


    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PartyAgingAsync(
        bool customers, CancellationToken ct)
    {
        DocSchema.Ensure();
        var partyKind = customers ? "C" : "S";
        var table = customers ? "customers" : "suppliers";
        var idCol = customers ? "customer_id" : "supplier_id";
        var codeCol = customers ? "customer_code" : "supplier_code";
        var nameCol = customers ? "customer_name_ar" : "supplier_name_ar";
        var sql = $"""
            SELECT p.{codeCol} AS Code,
                   p.{nameCol} AS Name,
                   COALESCE(SUM(CASE WHEN julianday('now') - julianday(l.entry_date) <= 30 THEN l.debit - l.credit ELSE 0 END), 0) AS Bucket0_30,
                   COALESCE(SUM(CASE WHEN julianday('now') - julianday(l.entry_date) > 30 AND julianday('now') - julianday(l.entry_date) <= 60 THEN l.debit - l.credit ELSE 0 END), 0) AS Bucket31_60,
                   COALESCE(SUM(CASE WHEN julianday('now') - julianday(l.entry_date) > 60 AND julianday('now') - julianday(l.entry_date) <= 90 THEN l.debit - l.credit ELSE 0 END), 0) AS Bucket61_90,
                   COALESCE(SUM(CASE WHEN julianday('now') - julianday(l.entry_date) > 90 THEN l.debit - l.credit ELSE 0 END), 0) AS Bucket90Plus,
                   COALESCE(SUM(l.debit - l.credit), 0) AS Balance
            FROM {table} p
            LEFT JOIN party_ledger l
              ON l.party_id = p.{idCol} AND l.company_id = p.company_id AND l.party_kind = $pk
            WHERE p.company_id = $company AND p.is_deleted = 0 AND p.is_active = 1
            GROUP BY p.{codeCol}, p.{nameCol}
            HAVING ABS(COALESCE(SUM(l.debit - l.credit), 0)) > 0.0001
            ORDER BY p.{codeCol};
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$pk", partyKind));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ItemProfitabilityAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(l.material_code, '') AS MaterialCode,
                   COALESCE(l.description, '') AS MaterialName,
                   COALESCE(SUM(l.qty), 0) AS QtySold,
                   COALESCE(SUM(l.line_subtotal), 0) AS Revenue,
                   COALESCE(SUM(l.line_tax), 0) AS Tax,
                   COALESCE(SUM(l.line_total), 0) AS Total
            FROM sales_document_lines l
            INNER JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = $company
              AND d.is_deleted = 0
              AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL','POS')
              AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY l.material_code, l.description
            ORDER BY Revenue DESC;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StockAdjustmentsAsync(
        string? materialSearch,
        string? warehouseId,
        string? documentNo,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        DocSchema.Ensure();
        MatRepo.EnsureSchema();
        var sql = """
            SELECT sm.movement_date AS MoveDate,
                   sm.movement_type AS MoveType,
                   COALESCE(sm.document_no, '') AS DocNo,
                   COALESCE(w.warehouse_code, CAST(sm.warehouse_id AS TEXT)) AS Warehouse,
                   COALESCE(w.warehouse_name_ar, '') AS WarehouseName,
                   COALESCE(w.warehouse_name_en, '') AS WarehouseNameEn,
                   COALESCE(m.code, CAST(sm.material_id AS TEXT)) AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(sm.qty_in, 0) AS QtyIn,
                   COALESCE(sm.qty_out, 0) AS QtyOut,
                   COALESCE(sm.unit_cost, 0) AS UnitCost,
                   COALESCE(sm.note, '') AS Note
            FROM material_stock_movements sm
            LEFT JOIN warehouses w
              ON w.company_id = sm.company_id AND w.warehouse_id = sm.warehouse_id
            LEFT JOIN materials m
              ON m.company_id = sm.company_id AND m.material_id = sm.material_id
            WHERE sm.company_id = $company
              AND sm.is_deleted = 0
              AND sm.movement_type IN ('ADJ', 'COUNT_CLOSE')
              AND sm.movement_date >= $from AND sm.movement_date < $to
              AND ($mat = '' OR LOWER(COALESCE(m.code, '')) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(COALESCE(m.name_ar, '')) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(COALESCE(m.name_en, '')) LIKE '%' || LOWER($mat) || '%')
              AND ($doc = '' OR COALESCE(sm.document_no, '') LIKE '%' || $doc || '%')
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace("sm.is_deleted = 0", "sm.is_deleted = FALSE", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(warehouseId))
            sql += " AND CAST(sm.warehouse_id AS TEXT) = $wh";
        sql += " ORDER BY sm.movement_date DESC, sm.stock_movement_id DESC;";
        var args = new List<(string Name, object? Value)>
        {
            ("$company", Company),
            ("$from", DateStart(from)),
            ("$to", DateEndExclusive(to)),
            ("$mat", materialSearch ?? string.Empty),
            ("$doc", documentNo ?? string.Empty)
        };
        if (!string.IsNullOrWhiteSpace(warehouseId))
            args.Add(("$wh", warehouseId));
        return await QueryAsync(sql, ct, args.ToArray());
    }

    public static IReadOnlyList<KeyValuePair<string, string>> WarehouseChoices()
    {
        DocSchema.Ensure();
        var sql = """
            SELECT warehouse_id,
                   CAST(COALESCE(warehouse_code, 0) AS TEXT),
                   COALESCE(warehouse_name_ar, ''),
                   COALESCE(warehouse_name_en, '')
            FROM warehouses
            WHERE company_id = $company AND is_deleted = 0 AND is_active = 1
            ORDER BY warehouse_code;
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace(
                "is_deleted = 0 AND is_active = 1",
                "is_deleted = FALSE AND is_active = TRUE",
                StringComparison.Ordinal);

        var rows = QueryAsync(sql, CancellationToken.None, ("$company", Company))
            .GetAwaiter().GetResult();
        var list = new List<KeyValuePair<string, string>> { new("", "—") };
        foreach (var row in rows)
        {
            var values = row.Values.ToArray();
            if (values.Length >= 4)
            {
                var code = Convert.ToString(values[1], CultureInfo.InvariantCulture) ?? "";
                var nameAr = Convert.ToString(values[2], CultureInfo.InvariantCulture) ?? "";
                var nameEn = Convert.ToString(values[3], CultureInfo.InvariantCulture) ?? "";
                var name = string.Equals(App.CurrentLanguageCode, "ar", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(nameEn)
                    ? nameAr
                    : nameEn;
                list.Add(new(
                    Convert.ToString(values[0], CultureInfo.InvariantCulture) ?? "",
                    $"{code} — {name}"));
            }
        }
        return list;
    }

    // ==================================================================
    // Chunk E (ribbon completion RS-11): report query methods.
    // Every method is a real, company-scoped, parameterized query over
    // existing tables. Tables owned by parallel chunks (cheques 033,
    // mfg_orders 034, targets/banks 031/036) are queried with the agreed
    // column contract; QueryAsync returns an empty set while those
    // migrations are absent, and the windows fall back where specified.
    // ==================================================================

    private static string FmtDate(DateTime v) => v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string PgBoolFix(string sql)
        => DatabaseRuntime.IsPostgreSql()
            ? sql.Replace("is_deleted = 0", "is_deleted = FALSE", StringComparison.Ordinal)
                 .Replace("is_service = 1", "is_service = TRUE", StringComparison.Ordinal)
                 .Replace("has_expiry = 1", "has_expiry = TRUE", StringComparison.Ordinal)
                 .Replace("is_active = 1", "is_active = TRUE", StringComparison.Ordinal)
            : sql;

    // Cash/bank GL accounts carry no flag column in the schema; the rest of
    // the app identifies them by account name. Same heuristic everywhere in
    // Chunk E: English account name contains cash / bank / box.
    private const string CashAccountSql =
        "(LOWER(COALESCE(a.name_en, '')) LIKE '%cash%' OR LOWER(COALESCE(a.name_en, '')) LIKE '%bank%' OR LOWER(COALESCE(a.name_en, '')) LIKE '%box%')";
    private const string BankAccountSql =
        "(LOWER(COALESCE(a.name_en, '')) LIKE '%bank%')";

    // ---------------- Inventory ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> EndOfPeriodAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT COALESCE(w.warehouse_code, CAST(sm.warehouse_id AS TEXT)) AS Warehouse,
                   COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(m.default_unit, '') AS Unit,
                   COALESCE(SUM(sm.qty_in), 0) AS QtyIn,
                   COALESCE(SUM(sm.qty_out), 0) AS QtyOut,
                   COALESCE(SUM(sm.qty_in - sm.qty_out), 0) AS Balance,
                   CASE WHEN SUM(sm.qty_in - sm.qty_out) = 0 THEN 0
                        ELSE SUM((sm.qty_in - sm.qty_out) * sm.unit_cost) / SUM(sm.qty_in - sm.qty_out) END AS AvgCost,
                   COALESCE(SUM((sm.qty_in - sm.qty_out) * sm.unit_cost), 0) AS Value
            FROM material_stock_movements sm
            LEFT JOIN materials m ON m.company_id = sm.company_id AND m.material_id = sm.material_id
            LEFT JOIN warehouses w ON w.company_id = sm.company_id AND w.warehouse_id = sm.warehouse_id
            WHERE sm.company_id = $company AND sm.is_deleted = 0 AND sm.movement_date <= $asof
            GROUP BY sm.warehouse_id, w.warehouse_code, m.code, m.name_ar, m.default_unit
            ORDER BY Warehouse, MaterialCode;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$asof", FmtDate(asOf)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MaterialMovementChartAsync(
        int year, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   substr(sm.movement_date, 1, 7) AS Month,
                   COALESCE(SUM(sm.qty_in), 0) AS QtyIn,
                   COALESCE(SUM(sm.qty_out), 0) AS QtyOut,
                   COALESCE(SUM(sm.qty_in - sm.qty_out), 0) AS NetQty,
                   COUNT(*) AS MoveCount
            FROM material_stock_movements sm
            LEFT JOIN materials m ON m.company_id = sm.company_id AND m.material_id = sm.material_id
            WHERE sm.company_id = $company AND sm.is_deleted = 0
              AND sm.movement_date >= $from AND sm.movement_date <= $to
            GROUP BY m.code, m.name_ar, substr(sm.movement_date, 1, 7)
            ORDER BY MaterialCode, Month;
            """);
        return await QueryAsync(sql, ct, ("$company", Company),
            ("$from", $"{year:D4}-01-01"), ("$to", $"{year:D4}-12-31"));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExpiredMaterialsAsync(
        CancellationToken ct)
    {
        // Schema fact: there is NO batch/lot expiry-date table. Materials only
        // carry has_expiry / shelf_life_days / alert_before_expiry_days, so the
        // report lists flagged materials with their last movement date and the
        // window derives an honest note from shelf life + last movement.
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT m.code AS MaterialCode,
                   m.name_ar AS MaterialName,
                   COALESCE(m.default_unit, '') AS Unit,
                   COALESCE(m.shelf_life_days, 0) AS ShelfLifeDays,
                   COALESCE(m.alert_before_expiry_days, 0) AS AlertDays,
                   (SELECT MAX(x.movement_date) FROM material_stock_movements x
                     WHERE x.company_id = m.company_id AND x.material_id = m.material_id AND x.is_deleted = 0) AS LastMoveDate,
                   COALESCE((SELECT SUM(b.available_qty) FROM material_stock_balances b
                     WHERE b.company_id = m.company_id AND b.material_id = m.material_id AND b.is_deleted = 0), 0) AS OnHand
            FROM materials m
            WHERE m.company_id = $company AND m.is_deleted = 0 AND m.has_expiry = 1
            ORDER BY m.code;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvoiceAgeInvoicesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(d.party_code, '') AS PartyCode,
                   COALESCE(d.party_name, '') AS PartyName,
                   d.doc_no AS DocNo,
                   d.doc_date AS DocDate,
                   COALESCE(d.grand_total, 0) - COALESCE(d.paid_amount, 0) AS OpenAmount
            FROM sales_documents d
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL', 'POS')
              AND COALESCE(d.grand_total, 0) - COALESCE(d.paid_amount, 0) > 0.0001
            ORDER BY d.doc_date;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PriceHistoryAsync(
        string? materialSearch, DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_date AS DocDate, d.doc_no AS DocNo, d.doc_kind AS Kind,
                   COALESCE(d.party_name, '') AS PartyName,
                   COALESCE(l.material_code, '') AS MaterialCode,
                   COALESCE(l.description, '') AS MaterialName,
                   COALESCE(l.qty, 0) AS Qty,
                   COALESCE(l.unit_price, 0) AS UnitPrice,
                   COALESCE(l.line_total, 0) AS Total
            FROM sales_document_lines l
            INNER JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_date >= $from AND d.doc_date <= $to
              AND ($mat = '' OR LOWER(COALESCE(l.material_code, '')) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(COALESCE(l.description, '')) LIKE '%' || LOWER($mat) || '%')
            UNION ALL
            SELECT d.doc_date, d.doc_no, d.doc_kind,
                   COALESCE(d.party_name, ''),
                   COALESCE(l.material_code, ''),
                   COALESCE(l.description, ''),
                   COALESCE(l.qty, 0), COALESCE(l.unit_price, 0), COALESCE(l.line_total, 0)
            FROM purchase_document_lines l
            INNER JOIN purchase_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_date >= $from AND d.doc_date <= $to
              AND ($mat = '' OR LOWER(COALESCE(l.material_code, '')) LIKE '%' || LOWER($mat) || '%'
                   OR LOWER(COALESCE(l.description, '')) LIKE '%' || LOWER($mat) || '%')
            ORDER BY DocDate DESC, DocNo DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company),
            ("$from", FmtDate(from)), ("$to", FmtDate(to)), ("$mat", materialSearch ?? string.Empty));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ServiceMaterialsAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT m.code AS MaterialCode,
                   m.name_ar AS MaterialName,
                   COALESCE(m.default_unit, '') AS Unit,
                   COALESCE((SELECT COUNT(*) FROM material_stock_movements x
                     WHERE x.company_id = m.company_id AND x.material_id = m.material_id AND x.is_deleted = 0), 0) AS MoveCount,
                   COALESCE((SELECT SUM(x.qty_in) FROM material_stock_movements x
                     WHERE x.company_id = m.company_id AND x.material_id = m.material_id AND x.is_deleted = 0), 0) AS QtyIn,
                   COALESCE((SELECT SUM(x.qty_out) FROM material_stock_movements x
                     WHERE x.company_id = m.company_id AND x.material_id = m.material_id AND x.is_deleted = 0), 0) AS QtyOut,
                   COALESCE((SELECT SUM(l.line_total) FROM sales_document_lines l
                     INNER JOIN sales_documents d ON d.doc_id = l.doc_id
                     WHERE d.company_id = m.company_id AND d.is_deleted = 0 AND d.status = 'Posted'
                       AND l.material_code = m.code), 0) AS Revenue
            FROM materials m
            WHERE m.company_id = $company AND m.is_deleted = 0 AND m.is_service = 1
            ORDER BY m.code;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MaterialStatisticsAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT m.code AS MaterialCode,
                   m.name_ar AS MaterialName,
                   COALESCE(s.QtySold, 0) AS QtySold,
                   COALESCE(s.Revenue, 0) AS Revenue,
                   COALESCE(p.QtyPurchased, 0) AS QtyPurchased,
                   COALESCE(p.PurchaseAmount, 0) AS PurchaseAmount,
                   COALESCE(s.QtySold, 0) * COALESCE(NULLIF(m.avg_cost, 0), m.cost, 0) AS Cost,
                   COALESCE(s.Revenue, 0) - COALESCE(s.QtySold, 0) * COALESCE(NULLIF(m.avg_cost, 0), m.cost, 0) AS Margin,
                   CASE WHEN COALESCE(s.Revenue, 0) = 0 THEN 0
                        ELSE (COALESCE(s.Revenue, 0) - COALESCE(s.QtySold, 0) * COALESCE(NULLIF(m.avg_cost, 0), m.cost, 0)) / s.Revenue END AS MarginPct
            FROM materials m
            LEFT JOIN (
                SELECT l.material_code AS code, SUM(l.qty) AS QtySold, SUM(l.line_total) AS Revenue
                FROM sales_document_lines l INNER JOIN sales_documents d ON d.doc_id = l.doc_id
                WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
                  AND d.doc_kind IN ('SAL', 'POS') AND d.doc_date >= $from AND d.doc_date <= $to
                GROUP BY l.material_code
            ) s ON s.code = m.code
            LEFT JOIN (
                SELECT l.material_code AS code, SUM(l.qty) AS QtyPurchased, SUM(l.line_total) AS PurchaseAmount
                FROM purchase_document_lines l INNER JOIN purchase_documents d ON d.doc_id = l.doc_id
                WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
                  AND d.doc_kind IN ('PUR') AND d.doc_date >= $from AND d.doc_date <= $to
                GROUP BY l.material_code
            ) p ON p.code = m.code
            WHERE m.company_id = $company AND m.is_deleted = 0
              AND (COALESCE(s.QtySold, 0) <> 0 OR COALESCE(p.QtyPurchased, 0) <> 0)
            ORDER BY Revenue DESC;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StagnantMaterialsAsync(
        int days, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var cutoff = FmtDate(DateTime.Today.AddDays(-Math.Max(0, days)));
        var sql = PgBoolFix("""
            SELECT COALESCE(w.warehouse_code, CAST(b.warehouse_id AS TEXT)) AS Warehouse,
                   COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(b.available_qty, 0) AS OnHand,
                   COALESCE(b.average_cost, 0) AS AvgCost,
                   COALESCE(b.available_qty, 0) * COALESCE(b.average_cost, 0) AS Value,
                   (SELECT MAX(x.movement_date) FROM material_stock_movements x
                     WHERE x.company_id = b.company_id AND x.warehouse_id = b.warehouse_id
                       AND x.material_id = b.material_id AND x.is_deleted = 0 AND x.qty_out > 0) AS LastOutDate
            FROM material_stock_balances b
            LEFT JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
            LEFT JOIN warehouses w ON w.company_id = b.company_id AND w.warehouse_id = b.warehouse_id
            WHERE b.company_id = $company AND b.is_deleted = 0 AND COALESCE(b.available_qty, 0) > 0
              AND NOT EXISTS (
                  SELECT 1 FROM material_stock_movements x
                  WHERE x.company_id = b.company_id AND x.warehouse_id = b.warehouse_id
                    AND x.material_id = b.material_id AND x.is_deleted = 0
                    AND x.qty_out > 0 AND x.movement_date >= $cutoff)
            ORDER BY Value DESC;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$cutoff", cutoff));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MostActiveMaterialsAsync(
        int limit, DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COUNT(*) AS MoveCount,
                   COALESCE(SUM(sm.qty_in), 0) AS QtyIn,
                   COALESCE(SUM(sm.qty_out), 0) AS QtyOut,
                   COALESCE(SUM(sm.qty_in + sm.qty_out), 0) AS TotalQty,
                   COALESCE(SUM((sm.qty_in + sm.qty_out) * sm.unit_cost), 0) AS Value
            FROM material_stock_movements sm
            LEFT JOIN materials m ON m.company_id = sm.company_id AND m.material_id = sm.material_id
            WHERE sm.company_id = $company AND sm.is_deleted = 0
              AND sm.movement_date >= $from AND sm.movement_date <= $to
            GROUP BY m.code, m.name_ar
            ORDER BY TotalQty DESC
            LIMIT $limit;
            """);
        return await QueryAsync(sql, ct, ("$company", Company),
            ("$from", FmtDate(from)), ("$to", FmtDate(to)), ("$limit", Math.Max(1, limit)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MaterialComparisonAsync(
        DateTime f1, DateTime t1, DateTime f2, DateTime t2, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f1 AND sm.movement_date <= $t1 THEN sm.qty_in - sm.qty_out ELSE 0 END), 0) AS Qty1,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f1 AND sm.movement_date <= $t1 THEN (sm.qty_in - sm.qty_out) * sm.unit_cost ELSE 0 END), 0) AS Value1,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN sm.qty_in - sm.qty_out ELSE 0 END), 0) AS Qty2,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN (sm.qty_in - sm.qty_out) * sm.unit_cost ELSE 0 END), 0) AS Value2,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN sm.qty_in - sm.qty_out ELSE 0 END), 0)
                     - COALESCE(SUM(CASE WHEN sm.movement_date >= $f1 AND sm.movement_date <= $t1 THEN sm.qty_in - sm.qty_out ELSE 0 END), 0) AS QtyDiff,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN (sm.qty_in - sm.qty_out) * sm.unit_cost ELSE 0 END), 0)
                     - COALESCE(SUM(CASE WHEN sm.movement_date >= $f1 AND sm.movement_date <= $t1 THEN (sm.qty_in - sm.qty_out) * sm.unit_cost ELSE 0 END), 0) AS ValueDiff
            FROM material_stock_movements sm
            LEFT JOIN materials m ON m.company_id = sm.company_id AND m.material_id = sm.material_id
            WHERE sm.company_id = $company AND sm.is_deleted = 0
              AND ((sm.movement_date >= $f1 AND sm.movement_date <= $t1) OR (sm.movement_date >= $f2 AND sm.movement_date <= $t2))
            GROUP BY m.code, m.name_ar
            ORDER BY MaterialCode;
            """);
        return await QueryAsync(sql, ct, ("$company", Company),
            ("$f1", FmtDate(f1)), ("$t1", FmtDate(t1)), ("$f2", FmtDate(f2)), ("$t2", FmtDate(t2)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> NegativeStockAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT COALESCE(w.warehouse_code, CAST(sm.warehouse_id AS TEXT)) AS Warehouse,
                   COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(SUM(sm.qty_in - sm.qty_out), 0) AS Balance,
                   CASE WHEN SUM(sm.qty_in - sm.qty_out) = 0 THEN 0
                        ELSE SUM((sm.qty_in - sm.qty_out) * sm.unit_cost) / SUM(sm.qty_in - sm.qty_out) END AS AvgCost,
                   COALESCE(SUM((sm.qty_in - sm.qty_out) * sm.unit_cost), 0) AS Value
            FROM material_stock_movements sm
            LEFT JOIN materials m ON m.company_id = sm.company_id AND m.material_id = sm.material_id
            LEFT JOIN warehouses w ON w.company_id = sm.company_id AND w.warehouse_id = sm.warehouse_id
            WHERE sm.company_id = $company AND sm.is_deleted = 0
            GROUP BY sm.warehouse_id, w.warehouse_code, m.code, m.name_ar
            HAVING SUM(sm.qty_in - sm.qty_out) < -0.0001
            ORDER BY Balance;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InventoryCustomersAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(d.party_code, '') AS PartyCode,
                   COALESCE(d.party_name, '') AS PartyName,
                   COALESCE(l.material_code, '') AS MaterialCode,
                   COALESCE(l.description, '') AS MaterialName,
                   COALESCE(SUM(l.qty), 0) AS Qty,
                   COALESCE(SUM(l.line_total), 0) AS Value,
                   COUNT(DISTINCT d.doc_id) AS InvoiceCount
            FROM sales_document_lines l
            INNER JOIN sales_documents d ON d.doc_id = l.doc_id
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL', 'POS') AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY d.party_code, d.party_name, l.material_code, l.description
            ORDER BY PartyName, Value DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BranchMaterialsAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT CAST(COALESCE(w.warehouse_code, b.warehouse_id) AS TEXT) || ' - ' || COALESCE(w.warehouse_name_en, w.warehouse_name_ar, '') AS Branch,
                   COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(b.available_qty, 0) AS Qty,
                   COALESCE(b.average_cost, 0) AS AvgCost,
                   COALESCE(b.available_qty, 0) * COALESCE(b.average_cost, 0) AS Value
            FROM material_stock_balances b
            LEFT JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
            LEFT JOIN warehouses w ON w.company_id = b.company_id AND w.warehouse_id = b.warehouse_id
            WHERE b.company_id = $company AND b.is_deleted = 0
            ORDER BY Branch, MaterialCode;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    // ---------------- Accounts ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CostCenterReportAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(NULLIF(l.cost_center, ''), '-') AS CostCenter,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   COALESCE(SUM(l.debit - l.credit), 0) AS Balance,
                   COUNT(*) AS LineCount
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            WHERE j.company_id = $company AND j.status = 1
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY COALESCE(NULLIF(l.cost_center, ''), '-')
            ORDER BY CostCenter;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CostCenterTrialAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT a.account_code AS AccountCode,
                   a.name_ar AS AccountName,
                   COALESCE(NULLIF(l.cost_center, ''), '-') AS CostCenter,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   COALESCE(SUM(l.debit - l.credit), 0) AS Balance
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN accounts a ON CAST(a.account_id AS TEXT) = l.account_id AND a.company_id = j.company_id
            WHERE j.company_id = $company AND j.status = 1 AND a.is_deleted = 0
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY a.account_code, a.name_ar, COALESCE(NULLIF(l.cost_center, ''), '-')
            ORDER BY CostCenter, AccountCode;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AverageCurrencyRateAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(j.currency_code, 'SAR') AS Currency,
                   substr(j.entry_date, 1, 7) AS Month,
                   COUNT(*) AS Entries,
                   COALESCE(MIN(j.exchange_rate), 0) AS MinRate,
                   COALESCE(MAX(j.exchange_rate), 0) AS MaxRate,
                   COALESCE(AVG(j.exchange_rate), 0) AS AvgRate,
                   CASE WHEN SUM(t.TotalDebit) = 0 THEN COALESCE(AVG(j.exchange_rate), 0)
                        ELSE SUM(j.exchange_rate * t.TotalDebit) / SUM(t.TotalDebit) END AS WeightedRate
            FROM journal_entries j
            LEFT JOIN (
                SELECT journal_entry_id, SUM(debit) AS TotalDebit
                FROM journal_entry_lines GROUP BY journal_entry_id
            ) t ON t.journal_entry_id = j.journal_entry_id
            WHERE j.company_id = $company AND j.status = 1
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY COALESCE(j.currency_code, 'SAR'), substr(j.entry_date, 1, 7)
            ORDER BY Currency, Month;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExchangeRateDifferencesAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // Entry amounts are recorded in the entry currency; BaseDebit restates
        // them at the entry rate and Difference is the effect vs. a rate of 1.
        DocSchema.Ensure();
        const string sql = """
            SELECT j.entry_date AS DocDate,
                   COALESCE(j.document_number, j.journal_entry_id) AS DocNo,
                   COALESCE(j.currency_code, 'SAR') AS Currency,
                   COALESCE(j.exchange_rate, 1) AS Rate,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   COALESCE(SUM(l.debit), 0) * COALESCE(j.exchange_rate, 1) AS BaseDebit,
                   COALESCE(SUM(l.debit), 0) * (COALESCE(j.exchange_rate, 1) - 1) AS Difference
            FROM journal_entries j
            INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
            WHERE j.company_id = $company AND j.status = 1
              AND COALESCE(j.exchange_rate, 1) <> 1
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY j.journal_entry_id, j.entry_date, j.document_number, j.currency_code, j.exchange_rate
            ORDER BY j.entry_date;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AccountMovementChartAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT a.account_code AS AccountCode,
                   a.name_ar AS AccountName,
                   substr(j.entry_date, 1, 7) AS Month,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   COALESCE(SUM(l.debit - l.credit), 0) AS Balance
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN accounts a ON CAST(a.account_id AS TEXT) = l.account_id AND a.company_id = j.company_id
            WHERE j.company_id = $company AND j.status = 1 AND a.is_deleted = 0
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY a.account_code, a.name_ar, substr(j.entry_date, 1, 7)
            ORDER BY AccountCode, Month;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BranchAccountMovementAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // Journal entries carry no branch column; branch movement is therefore
        // the posted document totals grouped by the branch_name stored on the
        // document headers (migration 023).
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(NULLIF(d.branch_name, ''), '-') AS Branch, d.doc_kind AS Kind,
                   COUNT(*) AS DocCount, COALESCE(SUM(d.grand_total), 0) AS Total
            FROM sales_documents d
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY COALESCE(NULLIF(d.branch_name, ''), '-'), d.doc_kind
            UNION ALL
            SELECT COALESCE(NULLIF(d.branch_name, ''), '-'), d.doc_kind,
                   COUNT(*), COALESCE(SUM(d.grand_total), 0)
            FROM purchase_documents d
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY COALESCE(NULLIF(d.branch_name, ''), '-'), d.doc_kind
            ORDER BY Branch, Kind;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CashFlowSectionsAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // Indirect approximation (documented method): every posted journal
        // line that touches a cash/bank account is classified by the account
        // type of the entry's first non-cash counterpart line — revenue /
        // expense / receivable / payable / inventory counterparts are
        // Operating, fixed-asset & investment counterparts (codes 15x/16x)
        // are Investing, equity & long-term loan counterparts (3xx / 23x)
        // are Financing, everything else Operating.
        DocSchema.Ensure();
        var sql = PgBoolFix($"""
            SELECT CASE
                     WHEN cp.Code LIKE '15%' OR cp.Code LIKE '16%' THEN 'Investing'
                     WHEN cp.Code LIKE '3%' OR cp.Code LIKE '23%' THEN 'Financing'
                     ELSE 'Operating' END AS Section,
                   COALESCE(SUM(l.debit - l.credit), 0) AS Amount
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN accounts a ON CAST(a.account_id AS TEXT) = l.account_id AND a.company_id = j.company_id
            LEFT JOIN (
                SELECT l2.journal_entry_id AS EntryId,
                       (SELECT a2.account_code FROM journal_entry_lines lx
                        INNER JOIN accounts a2 ON CAST(a2.account_id AS TEXT) = lx.account_id AND a2.company_id = $company
                        WHERE lx.journal_entry_id = l2.journal_entry_id
                          AND NOT (LOWER(COALESCE(a2.name_en, '')) LIKE '%cash%' OR LOWER(COALESCE(a2.name_en, '')) LIKE '%bank%' OR LOWER(COALESCE(a2.name_en, '')) LIKE '%box%')
                        ORDER BY lx.line_number LIMIT 1) AS Code
                FROM journal_entry_lines l2 GROUP BY l2.journal_entry_id
            ) cp ON cp.EntryId = j.journal_entry_id
            WHERE j.company_id = $company AND j.status = 1 AND a.is_deleted = 0
              AND {CashAccountSql}
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY Section
            ORDER BY Section;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CashPositionAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix($"""
            SELECT COALESCE(SUM(l.debit - l.credit), 0) AS Balance
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN accounts a ON CAST(a.account_id AS TEXT) = l.account_id AND a.company_id = j.company_id
            WHERE j.company_id = $company AND j.status = 1 AND a.is_deleted = 0
              AND {CashAccountSql}
              AND j.entry_date <= $asof;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$asof", FmtDate(asOf)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> DormantAccountsAsync(
        int days, CancellationToken ct)
    {
        DocSchema.Ensure();
        var cutoff = FmtDate(DateTime.Today.AddDays(-Math.Max(0, days)));
        var sql = PgBoolFix("""
            SELECT a.account_code AS AccountCode,
                   a.name_ar AS AccountName,
                   COALESCE(a.debit, 0) AS Debit,
                   COALESCE(a.credit, 0) AS Credit,
                   COALESCE(a.balance, 0) AS Balance,
                   (SELECT MAX(j.entry_date) FROM journal_entry_lines l
                    INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                    WHERE l.account_id = CAST(a.account_id AS TEXT) AND j.company_id = a.company_id AND j.status = 1) AS LastMoveDate
            FROM accounts a
            WHERE a.company_id = $company AND a.is_deleted = 0 AND ABS(COALESCE(a.balance, 0)) > 0.0001
              AND COALESCE((SELECT MAX(j.entry_date) FROM journal_entry_lines l
                    INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                    WHERE l.account_id = CAST(a.account_id AS TEXT) AND j.company_id = a.company_id AND j.status = 1), '0001-01-01') < $cutoff
            ORDER BY a.account_code;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$cutoff", cutoff));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CollectionRatesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(c.customer_code, CAST(pl.party_id AS TEXT)) AS PartyCode,
                   COALESCE(c.customer_name_ar, '') AS PartyName,
                   COALESCE(SUM(pl.debit), 0) AS Invoiced,
                   COALESCE(SUM(pl.credit), 0) AS Collected,
                   COALESCE(SUM(pl.debit - pl.credit), 0) AS Balance,
                   CASE WHEN SUM(pl.debit) = 0 THEN 0 ELSE SUM(pl.credit) / SUM(pl.debit) END AS CollectionPct
            FROM party_ledger pl
            LEFT JOIN customers c ON c.customer_id = pl.party_id AND c.company_id = pl.company_id
            WHERE pl.company_id = $company AND pl.party_kind = 'C'
            GROUP BY pl.party_id, c.customer_code, c.customer_name_ar
            HAVING ABS(COALESCE(SUM(pl.debit), 0)) > 0.0001
            ORDER BY PartyCode;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AccountComparisonAsync(
        DateTime f1, DateTime t1, DateTime f2, DateTime t2, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT a.account_code AS AccountCode,
                   a.name_ar AS AccountName,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN l.debit ELSE 0 END), 0) AS Debit1,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN l.credit ELSE 0 END), 0) AS Credit1,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN l.debit - l.credit ELSE 0 END), 0) AS Balance1,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN l.debit ELSE 0 END), 0) AS Debit2,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN l.credit ELSE 0 END), 0) AS Credit2,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN l.debit - l.credit ELSE 0 END), 0) AS Balance2,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN l.debit - l.credit ELSE 0 END), 0)
                     - COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN l.debit - l.credit ELSE 0 END), 0) AS Diff
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN accounts a ON CAST(a.account_id AS TEXT) = l.account_id AND a.company_id = j.company_id
            WHERE j.company_id = $company AND j.status = 1 AND a.is_deleted = 0
              AND ((j.entry_date >= $f1 AND j.entry_date <= $t1) OR (j.entry_date >= $f2 AND j.entry_date <= $t2))
            GROUP BY a.account_code, a.name_ar
            ORDER BY AccountCode;
            """);
        return await QueryAsync(sql, ct, ("$company", Company),
            ("$f1", FmtDate(f1)), ("$t1", FmtDate(t1)), ("$f2", FmtDate(f2)), ("$t2", FmtDate(t2)));
    }

    // ---------------- Bonds ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CashJournalAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix($"""
            SELECT j.entry_date AS EntryDate,
                   COALESCE(j.document_number, j.journal_entry_id) AS DocNo,
                   COALESCE(j.description, '') AS Description,
                   COALESCE(j.reference_number, '') AS Reference,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   CASE j.status WHEN 0 THEN 'Draft' WHEN 1 THEN 'Posted' ELSE 'Reversed' END AS Status
            FROM journal_entries j
            INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
            INNER JOIN accounts a ON CAST(a.account_id AS TEXT) = l.account_id AND a.company_id = j.company_id
            WHERE j.company_id = $company AND a.is_deleted = 0 AND {CashAccountSql}
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY j.journal_entry_id, j.entry_date, j.document_number, j.description, j.reference_number, j.status
            ORDER BY j.entry_date DESC, j.journal_entry_id DESC;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    // ---------------- Invoices ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvoiceProfitsAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT d.doc_date AS DocDate,
                   d.doc_no AS DocNo,
                   COALESCE(d.party_name, '') AS PartyName,
                   COALESCE(SUM(l.line_total), 0) AS Revenue,
                   COALESCE(SUM(l.qty * COALESCE(NULLIF(m.avg_cost, 0), m.cost, 0)), 0) AS Cost,
                   COALESCE(SUM(l.line_total), 0) - COALESCE(SUM(l.qty * COALESCE(NULLIF(m.avg_cost, 0), m.cost, 0)), 0) AS Profit,
                   CASE WHEN SUM(l.line_total) = 0 THEN 0
                        ELSE (SUM(l.line_total) - SUM(l.qty * COALESCE(NULLIF(m.avg_cost, 0), m.cost, 0))) / SUM(l.line_total) END AS MarginPct
            FROM sales_documents d
            INNER JOIN sales_document_lines l ON l.doc_id = d.doc_id
            LEFT JOIN materials m ON m.company_id = d.company_id AND m.code = l.material_code
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL', 'POS') AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY d.doc_id, d.doc_date, d.doc_no, d.party_name
            ORDER BY d.doc_date DESC, d.doc_no DESC;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PackingListAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT d.doc_date AS DocDate,
                   d.doc_no AS DocNo,
                   COALESCE(d.party_name, '') AS PartyName,
                   COALESCE(l.material_code, '') AS MaterialCode,
                   COALESCE(l.description, '') AS MaterialName,
                   COALESCE(l.qty, 0) AS Qty,
                   COALESCE(l.unit, '') AS Unit,
                   COALESCE(l.qty, 0) * COALESCE(m.weight_net, 0) AS WeightNet,
                   COALESCE(l.qty, 0) * COALESCE(m.weight_gross, 0) AS WeightGross
            FROM sales_documents d
            INNER JOIN sales_document_lines l ON l.doc_id = d.doc_id
            LEFT JOIN materials m ON m.company_id = d.company_id AND m.code = l.material_code
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL', 'DLV', 'POS') AND d.doc_date >= $from AND d.doc_date <= $to
            ORDER BY d.doc_date DESC, d.doc_no DESC, l.line_no;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    // ---------------- POS ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PosCashReportAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // sales_documents has no terminal/session column; POS documents are
        // doc_kind='POS' and the schedule groups them by date + warehouse.
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_date AS DocDate,
                   COALESCE(NULLIF(d.warehouse_name, ''), CAST(d.warehouse_id AS TEXT)) AS Warehouse,
                   COUNT(*) AS DocCount,
                   COALESCE(SUM(d.subtotal), 0) AS Subtotal,
                   COALESCE(SUM(d.discount_amount), 0) AS Discount,
                   COALESCE(SUM(d.tax_amount), 0) AS Tax,
                   COALESCE(SUM(d.grand_total), 0) AS Total,
                   CASE WHEN COUNT(*) = 0 THEN 0 ELSE SUM(d.grand_total) / COUNT(*) END AS AvgTicket
            FROM sales_documents d
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind = 'POS' AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY d.doc_date, COALESCE(NULLIF(d.warehouse_name, ''), CAST(d.warehouse_id AS TEXT))
            ORDER BY d.doc_date DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    // ---------------- Customers ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CustomerDueAsync(
        CancellationToken ct)
    {
        // Written against party_ledger + customers only, so it keeps working
        // whether or not the parallel parties table (031) exists.
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(c.customer_code, CAST(pl.party_id AS TEXT)) AS PartyCode,
                   COALESCE(c.customer_name_ar, '') AS PartyName,
                   COALESCE(c.phone, '') AS Phone,
                   COALESCE(SUM(pl.debit), 0) AS Invoiced,
                   COALESCE(SUM(pl.credit), 0) AS Collected,
                   COALESCE(SUM(pl.debit - pl.credit), 0) AS Balance,
                   MIN(pl.entry_date) AS OldestDate
            FROM party_ledger pl
            LEFT JOIN customers c ON c.customer_id = pl.party_id AND c.company_id = pl.company_id
            WHERE pl.company_id = $company AND pl.party_kind = 'C'
            GROUP BY pl.party_id, c.customer_code, c.customer_name_ar, c.phone
            HAVING COALESCE(SUM(pl.debit - pl.credit), 0) > 0.0001
            ORDER BY OldestDate, Balance DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> TargetsAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // targets (migration 036, parallel chunk): owner_kind PARTY/REP,
        // owner_code, period_start, target_amount. Actuals are posted sales
        // totals in the same range; parties with sales but no target row are
        // unioned in with a zero target.
        DocSchema.Ensure();
        const string sql = """
            SELECT t.owner_code AS PartyCode,
                   COALESCE(c.customer_name_ar, t.owner_code) AS PartyName,
                   COALESCE(SUM(t.target_amount), 0) AS Target,
                   COALESCE((SELECT SUM(d.grand_total) FROM sales_documents d
                     WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
                       AND d.doc_kind IN ('SAL', 'POS') AND d.party_code = t.owner_code
                       AND d.doc_date >= $from AND d.doc_date <= $to), 0) AS Actual
            FROM targets t
            LEFT JOIN customers c ON c.company_id = t.company_id AND c.customer_code = t.owner_code
            WHERE t.company_id = $company AND t.owner_kind = 'PARTY'
              AND t.period_start >= $from AND t.period_start <= $to
            GROUP BY t.owner_code, c.customer_name_ar
            UNION ALL
            SELECT d.party_code, COALESCE(MAX(d.party_name), d.party_code), 0,
                   COALESCE(SUM(d.grand_total), 0)
            FROM sales_documents d
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL', 'POS') AND COALESCE(d.party_code, '') <> ''
              AND d.doc_date >= $from AND d.doc_date <= $to
              AND NOT EXISTS (SELECT 1 FROM targets t2
                  WHERE t2.company_id = $company AND t2.owner_kind = 'PARTY' AND t2.owner_code = d.party_code
                    AND t2.period_start >= $from AND t2.period_start <= $to)
            GROUP BY d.party_code
            ORDER BY PartyCode;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SalesByPartyAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(d.party_code, '') AS PartyCode,
                   COALESCE(d.party_name, '') AS PartyName,
                   COALESCE(SUM(d.grand_total), 0) AS Actual
            FROM sales_documents d
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL', 'POS') AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY d.party_code, d.party_name
            ORDER BY Actual DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    // ---------------- Sales reps ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SalesRepMovementAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(NULLIF(d.sales_rep_name, ''), '-') AS Rep,
                   COUNT(*) AS DocCount,
                   COALESCE(SUM(d.subtotal), 0) AS Subtotal,
                   COALESCE(SUM(d.discount_amount), 0) AS Discount,
                   COALESCE(SUM(d.tax_amount), 0) AS Tax,
                   COALESCE(SUM(d.grand_total), 0) AS Total
            FROM sales_documents d
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY COALESCE(NULLIF(d.sales_rep_name, ''), '-')
            ORDER BY Total DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SalesRepCollectionsAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // party_ledger carries no rep column; collections per rep are the
        // payment_allocations (025) settled against that rep's invoices.
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(NULLIF(d.sales_rep_name, ''), '-') AS Rep,
                   COUNT(*) AS AllocationCount,
                   COALESCE(SUM(pa.amount), 0) AS Collected
            FROM payment_allocations pa
            INNER JOIN sales_documents d ON d.doc_id = pa.doc_id AND d.company_id = pa.company_id
            WHERE pa.company_id = $company AND pa.doc_table = 'SALES'
              AND pa.allocated_at >= $from AND pa.allocated_at <= $to
            GROUP BY COALESCE(NULLIF(d.sales_rep_name, ''), '-')
            ORDER BY Collected DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company),
            ("$from", FmtDate(from)), ("$to", FmtDate(to) + " 23:59:59"));
    }

    // ---------------- Manufacturing (migration 034, parallel chunk) ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MfgOrderCostsAsync(
        CancellationToken ct)
    {
        const string sql = """
            SELECT o.doc_no AS DocNo,
                   o.doc_date AS DocDate,
                   o.item_code AS ItemCode,
                   COALESCE(o.qty, 0) AS Qty,
                   COALESCE(o.unit_cost, 0) AS UnitCost,
                   COALESCE(o.standard_unit_cost, 0) AS StandardUnitCost,
                   COALESCE(o.total_cost, 0) AS TotalCost,
                   COALESCE(o.qty, 0) * COALESCE(o.standard_unit_cost, 0) AS StandardTotal,
                   COALESCE(o.total_cost, 0) - COALESCE(o.qty, 0) * COALESCE(o.standard_unit_cost, 0) AS Variance,
                   COALESCE(o.status, '') AS Status
            FROM mfg_orders o
            WHERE o.company_id = $company
            ORDER BY o.doc_date DESC, o.doc_no DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MfgVarianceAsync(
        string mode, CancellationToken ct)
    {
        // mode: Total (all lines) / Raw / Overhead / Finished — filtered by
        // line_kind families; variance = actual amount − standard amount.
        var kindFilter = mode switch
        {
            "Raw" => "AND l.line_kind IN ('RAW', 'MATERIAL', 'RAW_MATERIAL')",
            "Overhead" => "AND l.line_kind IN ('OVERHEAD', 'ADDITIONAL', 'LABOR', 'OTHER')",
            "Finished" => "AND l.line_kind IN ('FINISHED', 'OUTPUT', 'PRODUCT')",
            _ => ""
        };
        var sql = $"""
            SELECT o.doc_no AS DocNo,
                   l.item_code AS ItemCode,
                   l.line_kind AS LineKind,
                   COALESCE(SUM(l.qty), 0) AS Qty,
                   COALESCE(SUM(l.amount), 0) AS Amount,
                   COALESCE(SUM(l.qty * l.standard_unit_cost), 0) AS StandardAmount,
                   COALESCE(SUM(l.amount - l.qty * l.standard_unit_cost), 0) AS Variance
            FROM mfg_order_lines l
            INNER JOIN mfg_orders o ON o.order_id = l.order_id AND o.company_id = l.company_id
            WHERE l.company_id = $company {kindFilter}
            GROUP BY o.doc_no, l.item_code, l.line_kind
            ORDER BY o.doc_no, l.line_kind, l.item_code;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MfgFeasibilityAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT o.doc_no AS DocNo,
                   o.item_code AS ItemCode,
                   l.item_code AS LineItemCode,
                   COALESCE(l.qty, 0) AS Required,
                   COALESCE((SELECT SUM(b.available_qty) FROM material_stock_balances b
                     INNER JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
                     WHERE b.company_id = l.company_id AND m.code = l.item_code AND b.is_deleted = 0), 0) AS OnHand
            FROM mfg_orders o
            INNER JOIN mfg_order_lines l ON l.order_id = o.order_id AND l.company_id = o.company_id
            WHERE o.company_id = $company AND o.status = 'Draft'
              AND l.line_kind NOT IN ('FINISHED', 'OUTPUT', 'PRODUCT')
            ORDER BY o.doc_no, l.item_code;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    // ---------------- Banks ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BankBalancesFromBanksAsync(
        CancellationToken ct)
    {
        // banks (migration 031, parallel chunk) carries gl_account_code; the
        // balance is the posted GL balance of that account.
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT a.account_code AS AccountCode,
                   a.name_ar AS AccountName,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   COALESCE(SUM(l.debit - l.credit), 0) AS Balance
            FROM banks b
            INNER JOIN accounts a ON a.company_id = b.company_id AND a.account_code = b.gl_account_code
            LEFT JOIN journal_entry_lines l ON l.account_id = CAST(a.account_id AS TEXT)
            LEFT JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                AND j.company_id = a.company_id AND j.status = 1
            WHERE b.company_id = $company AND a.is_deleted = 0
            GROUP BY a.account_code, a.name_ar
            ORDER BY a.account_code;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BankAccountsGlAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix($"""
            SELECT a.account_code AS AccountCode,
                   a.name_ar AS AccountName,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   COALESCE(SUM(l.debit - l.credit), 0) AS Balance
            FROM accounts a
            LEFT JOIN journal_entry_lines l ON l.account_id = CAST(a.account_id AS TEXT)
            LEFT JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                AND j.company_id = a.company_id AND j.status = 1
            WHERE a.company_id = $company AND a.is_deleted = 0 AND {BankAccountSql}
            GROUP BY a.account_code, a.name_ar
            ORDER BY a.account_code;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ChequesDueSummaryAsync(
        int days, CancellationToken ct)
    {
        // cheques (migration 033, parallel chunk): direction IN/OUT, amount,
        // due_date, status. Cancelled/void cheques are excluded.
        var from = FmtDate(DateTime.Today);
        var to = FmtDate(DateTime.Today.AddDays(Math.Max(0, days)));
        const string sql = """
            SELECT c.direction AS Direction,
                   COUNT(*) AS ChequeCount,
                   COALESCE(SUM(c.amount), 0) AS Amount
            FROM cheques c
            WHERE c.company_id = $company
              AND c.due_date >= $from AND c.due_date <= $to
              AND COALESCE(c.status, '') NOT IN ('Cancelled', 'Void', 'CANCELLED', 'VOID')
            GROUP BY c.direction
            ORDER BY c.direction;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", from), ("$to", to));
    }

    // ---------------- Orders ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> OrderPaymentsAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // Paid per order = payment_allocations settled against the order when
        // allocations exist, otherwise the document's own paid_amount.
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_date AS DocDate, d.doc_no AS DocNo, d.doc_kind AS Kind,
                   COALESCE(d.party_name, '') AS PartyName,
                   COALESCE(d.grand_total, 0) AS Total,
                   COALESCE(NULLIF(alloc.Paid, 0), d.paid_amount, 0) AS Paid,
                   COALESCE(d.grand_total, 0) - COALESCE(NULLIF(alloc.Paid, 0), d.paid_amount, 0) AS Remaining,
                   COALESCE(d.status, '') AS Status
            FROM sales_documents d
            LEFT JOIN (
                SELECT doc_id, SUM(amount) AS Paid FROM payment_allocations
                WHERE company_id = $company AND doc_table = 'SALES' GROUP BY doc_id
            ) alloc ON alloc.doc_id = d.doc_id
            WHERE d.company_id = $company AND d.is_deleted = 0
              AND d.doc_kind IN ('SO', 'QUO') AND d.doc_date >= $from AND d.doc_date <= $to
            UNION ALL
            SELECT d.doc_date, d.doc_no, d.doc_kind,
                   COALESCE(d.party_name, ''),
                   COALESCE(d.grand_total, 0),
                   COALESCE(NULLIF(alloc.Paid, 0), d.paid_amount, 0),
                   COALESCE(d.grand_total, 0) - COALESCE(NULLIF(alloc.Paid, 0), d.paid_amount, 0),
                   COALESCE(d.status, '')
            FROM purchase_documents d
            LEFT JOIN (
                SELECT doc_id, SUM(amount) AS Paid FROM payment_allocations
                WHERE company_id = $company AND doc_table = 'PURCHASE' GROUP BY doc_id
            ) alloc ON alloc.doc_id = d.doc_id
            WHERE d.company_id = $company AND d.is_deleted = 0
              AND d.doc_kind IN ('PO', 'RFQ') AND d.doc_date >= $from AND d.doc_date <= $to
            ORDER BY DocDate DESC, DocNo DESC;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }


    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            return Array.Empty<IReadOnlyDictionary<string, object?>>();

        try
        {
            await using var connection = Open(cs);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in args)
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                command.Parameters.Add(p);
            }

            var result = new List<IReadOnlyDictionary<string, object?>>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                result.Add(row);
            }
            return result;
        }
        catch
        {
            // Reports must never throw into the UI for missing optional tables.
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
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
}
