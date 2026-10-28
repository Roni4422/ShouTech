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
