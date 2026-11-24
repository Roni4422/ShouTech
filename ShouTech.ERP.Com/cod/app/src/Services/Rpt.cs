using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.App.Views;
using ShouTech.App.Views.Reports;
using ShouTech.Domain;

namespace ShouTech.App.Services;

/// <summary>
/// Shared, company-scoped query surface for standard report windows.
/// Failures SURFACE (Step 6, sweep RC-5): a failed report query is
/// reported through the central error mechanism (DB-1005) and thrown
/// as a cataloged AppError — never silently swallowed into an empty
/// result set that looks like "no data". Report windows handle the
/// error through RptBase, which reports it and shows the load-failed
/// status; a genuinely empty result still renders as 0 rows.
///
/// GL BASIS (Fix Phase Step 2, migration 059): every general-ledger report
/// below computes from POSTED journal_entry_lines (journal_entries.status
/// = 1) joined to the ONE canonical chart, chart_of_accounts, on the Guid
/// account_id the lines actually carry. Company scoping for the journal
/// family goes through the companies row (the shell carries the company
/// CODE; journal tables carry the company Guid). No GL report reads stored
/// account balances — the legacy accounts.balance/debit/credit columns
/// were never maintained (sweep C-2) and their table no longer exists.
///
/// BASE-CURRENCY BASIS (Fix Phase Step 7, migration 062, sweep J-5):
/// every GL total below sums the lines' BASE amounts
/// (COALESCE(base_debit, debit) / COALESCE(base_credit, credit) — the
/// fallback keeps rows written before the 062 backfill honest), and
/// every party-balance total sums party_ledger's base amounts, so a
/// foreign-currency entry reports at its converted value, never at
/// face value. Document-list reports (invoice lists, price history,
/// targets) still show DOCUMENT amounts in the document's own
/// currency — they are transaction-currency views by design.
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
        // Computed from POSTED journal lines in [from, to) — the pre-059
        // version selected the never-maintained cached accounts columns
        // and ignored its own date parameters entirely (sweep R-1).
        DocSchema.Ensure();
        var sql = """
            SELECT a.account_code AS Code,
                   COALESCE(a.name_ar, a.account_name) AS Name,
                   a.account_name AS NameEn,
                   COALESCE(SUM(m.debit), 0) AS Debit,
                   COALESCE(SUM(m.credit), 0) AS Credit,
                   COALESCE(SUM(m.debit), 0) - COALESCE(SUM(m.credit), 0) AS Balance
            FROM chart_of_accounts a
            INNER JOIN companies co ON co.company_id = a.company_id
            LEFT JOIN (
                SELECT l.account_id AS aid, COALESCE(l.base_debit, l.debit) AS debit, COALESCE(l.base_credit, l.credit) AS credit
                FROM journal_entry_lines l
                INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                WHERE j.status = 1 AND j.entry_date >= $from AND j.entry_date < $to
            ) m ON m.aid = a.account_id
            WHERE co.company_code = $company AND a.is_deleted = 0
            GROUP BY a.account_id, a.account_code, a.name_ar, a.account_name
            ORDER BY a.account_code;
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace("a.is_deleted = 0", "a.is_deleted = FALSE", StringComparison.Ordinal);
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", DateStart(from)),
            ("$to", DateEndExclusive(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BalanceSheetAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = """
            SELECT a.account_code AS Code,
                   COALESCE(a.name_ar, a.account_name) AS Name,
                   a.account_name AS NameEn,
                   CASE WHEN a.account_type IN ('LIABILITY', 'EQUITY')
                        THEN SUM(COALESCE(COALESCE(l.base_credit, l.credit), 0) - COALESCE(COALESCE(l.base_debit, l.debit), 0))
                        ELSE SUM(COALESCE(COALESCE(l.base_debit, l.debit), 0) - COALESCE(COALESCE(l.base_credit, l.credit), 0))
                   END AS Amount
            FROM chart_of_accounts a
            INNER JOIN companies co ON co.company_id = a.company_id
            INNER JOIN journal_entry_lines l
              ON l.account_id = a.account_id
            INNER JOIN journal_entries j
              ON j.journal_entry_id = l.journal_entry_id
             AND j.company_id = a.company_id
            WHERE co.company_code = $company
              AND a.is_deleted = 0
              AND j.status = 1
              AND j.entry_date < $to
              AND a.account_type IN ('ASSET', 'LIABILITY', 'EQUITY')
              AND NOT EXISTS (
                  SELECT 1
                  FROM chart_of_accounts child
                  WHERE child.company_id = a.company_id
                    AND child.parent_account_id = a.account_id
                    AND child.is_deleted = 0
              )
            GROUP BY a.account_id, a.account_code, a.name_ar, a.account_name, a.account_type
            HAVING ABS(SUM(COALESCE(COALESCE(l.base_debit, l.debit), 0) - COALESCE(COALESCE(l.base_credit, l.credit), 0))) > 0.0001
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
                   COALESCE(a.name_ar, a.account_name) AS Name,
                   a.account_name AS NameEn,
                   CASE WHEN a.account_type = 'REVENUE'
                        THEN SUM(COALESCE(COALESCE(l.base_credit, l.credit), 0) - COALESCE(COALESCE(l.base_debit, l.debit), 0))
                        ELSE SUM(COALESCE(COALESCE(l.base_debit, l.debit), 0) - COALESCE(COALESCE(l.base_credit, l.credit), 0))
                   END AS Amount
            FROM chart_of_accounts a
            INNER JOIN companies co ON co.company_id = a.company_id
            INNER JOIN journal_entry_lines l
              ON l.account_id = a.account_id
            INNER JOIN journal_entries j
              ON j.journal_entry_id = l.journal_entry_id
             AND j.company_id = a.company_id
            WHERE co.company_code = $company
              AND a.is_deleted = 0
              AND j.status = 1
              AND j.entry_date >= $from
              AND j.entry_date < $to
              AND a.account_type IN ('REVENUE', 'EXPENSE')
              AND NOT EXISTS (
                  SELECT 1
                  FROM chart_of_accounts child
                  WHERE child.company_id = a.company_id
                    AND child.parent_account_id = a.account_id
                    AND child.is_deleted = 0
              )
            GROUP BY a.account_id, a.account_code, a.name_ar, a.account_name, a.account_type
            HAVING ABS(SUM(COALESCE(COALESCE(l.base_debit, l.debit), 0) - COALESCE(COALESCE(l.base_credit, l.credit), 0))) > 0.0001
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
                   COALESCE(j.document_number, CAST(j.journal_entry_id AS TEXT)) AS DocNo,
                   COALESCE(j.description, '') AS Description,
                   COALESCE(COALESCE(l.base_debit, l.debit), 0) AS Debit,
                   COALESCE(COALESCE(l.base_credit, l.credit), 0) AS Credit,
                   COALESCE(l.note, '') AS Note
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = j.company_id
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company
              AND a.account_code = $account
              AND j.entry_date >= $from
              AND j.entry_date < $to
              AND j.status = 1
            ORDER BY j.entry_date, j.journal_entry_id, l.line_number;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$account", accountCode ?? string.Empty),
            ("$from", DateStart(from)),
            ("$to", DateEndExclusive(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> JournalListAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT j.entry_date AS EntryDate,
                   COALESCE(j.document_number, CAST(j.journal_entry_id AS TEXT)) AS DocNo,
                   COALESCE(j.description, '') AS Description,
                   CASE j.status WHEN 0 THEN 'Draft' WHEN 1 THEN 'Posted' ELSE 'Reversed' END AS Status,
                   COALESCE((SELECT SUM(COALESCE(base_debit, debit)) FROM journal_entry_lines x WHERE x.journal_entry_id = j.journal_entry_id), 0) AS Debit,
                   COALESCE((SELECT SUM(COALESCE(base_credit, credit)) FROM journal_entry_lines x WHERE x.journal_entry_id = j.journal_entry_id), 0) AS Credit,
                   COALESCE(j.created_by, '') AS CreatedBy
            FROM journal_entries j
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company
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
                   CASE WHEN COALESCE(s.stock_value, 0) <> 0 THEN s.stock_value
                        ELSE COALESCE(s.available_qty, 0) * COALESCE(s.average_cost, 0) END AS Value
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
        // Step 6: a STOCK report excludes service and non-stocked
        // materials (sweep finding — they inflated the stock value).
        sql += DatabaseRuntime.IsPostgreSql()
            ? " AND (m.is_service IS NULL OR m.is_service = FALSE) AND (m.is_stocked IS NULL OR m.is_stocked = TRUE)"
            : " AND COALESCE(m.is_service, 0) = 0 AND COALESCE(m.is_stocked, 1) = 1";
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
                       COALESCE(sm.cost_amount, (COALESCE(sm.qty_in, 0) - COALESCE(sm.qty_out, 0)) * COALESCE(sm.unit_cost, 0)) AS CostAmount,
                       COALESCE(sm.valuation_rate, sm.unit_cost, 0) AS ValuationRate,
                       COALESCE(sm.stock_value_after, 0) AS StockValueAfter,
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
                   CostAmount AS "MoveValue",
                   ValuationRate AS "ValuationRate",
                   StockValueAfter AS "BalanceValue",
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
        // Step 4: parties is the ONE master (migration 061); the ledger
        // joins on the party's ledger_id in this company + kind. Name is
        // the English name with the Arabic name as fallback (RP-5).
        const string sql = """
            SELECT c.code AS Code,
                   COALESCE(NULLIF(c.name_en, ''), c.name_ar, '') AS Name,
                   COALESCE(c.name_en, '') AS NameEn,
                   COALESCE(c.phone, '') AS Phone,
                   COALESCE(bal.balance, 0) AS Balance,
                   c.is_active AS IsActive,
                   COALESCE(c.is_blacklisted, 0) AS IsBlacklisted
            FROM parties c
            LEFT JOIN (
                SELECT party_id, SUM(COALESCE(base_debit, debit) - COALESCE(base_credit, credit)) AS balance
                FROM party_ledger
                WHERE company_id = $company AND party_kind = 'C'
                GROUP BY party_id
            ) bal ON bal.party_id = c.ledger_id
            WHERE c.company_id = $company AND c.kind = 'C' AND c.is_deleted = 0
            ORDER BY c.code;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SupplierBalancesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        // Supplier balances are amounts OWED: credit - debit (RP-2 — the
        // pre-unification report printed payables negative).
        const string sql = """
            SELECT s.code AS Code,
                   COALESCE(NULLIF(s.name_en, ''), s.name_ar, '') AS Name,
                   COALESCE(s.name_en, '') AS NameEn,
                   COALESCE(s.phone, '') AS Phone,
                   COALESCE(bal.balance, 0) AS Balance
            FROM parties s
            LEFT JOIN (
                SELECT party_id, SUM(COALESCE(base_credit, credit) - COALESCE(base_debit, debit)) AS balance
                FROM party_ledger
                WHERE company_id = $company AND party_kind = 'S'
                GROUP BY party_id
            ) bal ON bal.party_id = s.ledger_id
            WHERE s.company_id = $company AND s.kind = 'S' AND s.is_deleted = 0 AND s.is_active = 1
            ORDER BY s.code;
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
                   COALESCE(c.code, '') AS PartyCode,
                   COALESCE(NULLIF(c.name_en, ''), c.name_ar, d.party_name) AS PartyName,
                   COALESCE(d.subtotal, 0) AS Subtotal,
                   COALESCE(d.tax_amount, 0) AS Tax,
                   COALESCE(d.grand_total, 0) AS Total,
                   COALESCE(d.status, 'Draft') AS Status,
                   COALESCE(d.currency_code, 'SAR') AS Currency
            FROM sales_documents d
            LEFT JOIN parties c ON c.ledger_id = d.party_id AND c.company_id = d.company_id AND c.kind = 'C'
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
                   COALESCE(s.code, '') AS PartyCode,
                   COALESCE(NULLIF(s.name_en, ''), s.name_ar, d.party_name) AS PartyName,
                   COALESCE(d.subtotal, 0) AS Subtotal,
                   COALESCE(d.tax_amount, 0) AS Tax,
                   COALESCE(d.grand_total, 0) AS Total,
                   COALESCE(d.status, 'Draft') AS Status,
                   COALESCE(d.currency_code, 'SAR') AS Currency
            FROM purchase_documents d
            INNER JOIN companies co
              ON co.company_code = $company
             AND (CAST(co.company_id AS TEXT) = CAST(d.company_id AS TEXT)
                  OR d.company_id = co.company_code)
            LEFT JOIN parties s
              ON s.ledger_id = d.party_id AND s.company_id = $company AND s.kind = 'S'
            WHERE d.doc_kind = 'PUR'
              AND d.doc_date >= $from
              AND d.doc_date <= $to
              AND CAST(d.is_deleted AS TEXT) IN ('0', 'false')
            ORDER BY d.doc_date DESC, d.doc_no DESC;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }


    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SupplierStatementAsync(
        DateTime from, DateTime to, string? search, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            WITH supplier_ledger AS (
                SELECT p.code AS SupplierCode,
                       COALESCE(NULLIF(p.name_en, ''), p.name_ar, '') AS SupplierName,
                       l.ledger_id AS LedgerId,
                       l.entry_date AS EntryDate,
                       COALESCE(l.doc_no, '') AS DocumentNo,
                       COALESCE(l.doc_kind, '') AS DocumentKind,
                       COALESCE(l.note, '') AS Note,
                       COALESCE(l.currency_code, '') AS CurrencyCode,
                       COALESCE(l.base_debit, l.debit) AS DebitBase,
                       COALESCE(l.base_credit, l.credit) AS CreditBase,
                       SUM(COALESCE(l.base_credit, l.credit) - COALESCE(l.base_debit, l.debit))
                           OVER (PARTITION BY l.party_id ORDER BY l.entry_date, l.ledger_id
                                 ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningBalanceBase,
                       SUM(CASE WHEN l.entry_date < $from
                                THEN COALESCE(l.base_credit, l.credit) - COALESCE(l.base_debit, l.debit)
                                ELSE 0 END)
                           OVER (PARTITION BY l.party_id) AS OpeningBalanceBase
                FROM party_ledger l
                INNER JOIN parties p
                  ON p.ledger_id = l.party_id AND p.company_id = l.company_id AND p.kind = 'S'
                WHERE l.company_id = $company AND l.party_kind = 'S'
            )
            SELECT SupplierCode, SupplierName, EntryDate, DocumentNo, DocumentKind,
                   Note, CurrencyCode, DebitBase, CreditBase,
                   OpeningBalanceBase, RunningBalanceBase
            FROM supplier_ledger
            WHERE EntryDate >= $from AND EntryDate < $toEx
              AND ($search = '' OR LOWER(SupplierCode) LIKE LOWER($search)
                   OR LOWER(SupplierName) LIKE LOWER($search)
                   OR LOWER(DocumentNo) LIKE LOWER($search))
            ORDER BY SupplierCode, EntryDate, LedgerId;
            """;
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? string.Empty : "%" + search.Trim() + "%";
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$toEx", to.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$search", normalizedSearch));
    }

    /// <summary>
    /// Customer ledger statement (W1): the mirror of
    /// SupplierStatementAsync with customer orientation — the running
    /// balance is debit − credit (what the customer owes), computed over
    /// ALL of the party's ledger rows so the opening balance (entries
    /// before the range) and the running balance stay exact.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CustomerStatementAsync(
        DateTime from, DateTime to, string? search, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            WITH customer_ledger AS (
                SELECT p.code AS CustomerCode,
                       COALESCE(NULLIF(p.name_en, ''), p.name_ar, '') AS CustomerName,
                       l.ledger_id AS LedgerId,
                       l.entry_date AS EntryDate,
                       COALESCE(l.doc_no, '') AS DocumentNo,
                       COALESCE(l.doc_kind, '') AS DocumentKind,
                       COALESCE(l.note, '') AS Note,
                       COALESCE(l.currency_code, '') AS CurrencyCode,
                       COALESCE(l.base_debit, l.debit) AS DebitBase,
                       COALESCE(l.base_credit, l.credit) AS CreditBase,
                       SUM(COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit))
                           OVER (PARTITION BY l.party_id ORDER BY l.entry_date, l.ledger_id
                                 ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningBalanceBase,
                       SUM(CASE WHEN l.entry_date < $from
                                THEN COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit)
                                ELSE 0 END)
                           OVER (PARTITION BY l.party_id) AS OpeningBalanceBase
                FROM party_ledger l
                INNER JOIN parties p
                  ON p.ledger_id = l.party_id AND p.company_id = l.company_id AND p.kind = 'C'
                WHERE l.company_id = $company AND l.party_kind = 'C'
            )
            SELECT CustomerCode, CustomerName, EntryDate, DocumentNo, DocumentKind,
                   Note, CurrencyCode, DebitBase, CreditBase,
                   OpeningBalanceBase, RunningBalanceBase
            FROM customer_ledger
            WHERE EntryDate >= $from AND EntryDate < $toEx
              AND ($search = '' OR LOWER(CustomerCode) LIKE LOWER($search)
                   OR LOWER(CustomerName) LIKE LOWER($search)
                   OR LOWER(DocumentNo) LIKE LOWER($search))
            ORDER BY CustomerCode, EntryDate, LedgerId;
            """;
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? string.Empty : "%" + search.Trim() + "%";
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$toEx", to.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$search", normalizedSearch));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GoodsReceivedNotInvoicedAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT r.doc_date AS ReceiptDate,
                   r.doc_no AS ReceiptNo,
                   po.doc_no AS PurchaseOrderNo,
                   COALESCE(NULLIF(s.name_en, ''), s.name_ar, r.party_name, '') AS SupplierName,
                   COALESCE(l.material_code, '') AS MaterialCode,
                   l.description AS Description,
                   l.qty AS ReceivedQty,
                   COALESCE(m.MatchedQty, 0) AS InvoicedQty,
                   COALESCE(rt.ReturnedQty, 0) AS ReturnedQty,
                   CASE WHEN l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0) > 0
                        THEN l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0)
                        ELSE 0 END AS RemainingQty,
                   CASE WHEN l.qty > 0 AND
                       l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0) > 0
                       THEN l.line_subtotal * r.exchange_rate *
                           (l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0)) * 1.0 / l.qty
                       ELSE 0 END AS RemainingBaseAmount
            FROM purchase_documents r
            INNER JOIN companies co ON CAST(co.company_id AS TEXT) = r.company_id
            INNER JOIN document_links dl
              ON dl.company_id = r.company_id
             AND dl.source_doc_kind = 'PO'
             AND dl.target_doc_kind = 'RCV'
             AND dl.target_doc_id = r.doc_id
            INNER JOIN purchase_documents po
              ON po.company_id = r.company_id
             AND po.doc_id = dl.source_doc_id
             AND po.doc_kind = 'PO'
            INNER JOIN purchase_document_lines l ON l.doc_id = r.doc_id
            LEFT JOIN parties s
              ON s.ledger_id = r.party_id AND s.company_id = r.company_id AND s.kind = 'S'
            LEFT JOIN (
                SELECT m.company_id, m.receipt_doc_id, m.receipt_line_id,
                       SUM(m.matched_qty) AS MatchedQty
                FROM purchase_receipt_invoice_matches m
                INNER JOIN purchase_documents inv
                  ON inv.company_id = m.company_id
                 AND inv.doc_id = m.invoice_doc_id
                 AND inv.doc_kind = 'PUR'
                 AND inv.status = 'Posted'
                 AND CAST(inv.is_deleted AS TEXT) IN ('0', 'false')
                 AND inv.doc_date < $toEx
                GROUP BY m.company_id, m.receipt_doc_id, m.receipt_line_id
            ) m ON m.company_id = r.company_id
               AND m.receipt_doc_id = r.doc_id
               AND m.receipt_line_id = l.line_id
            LEFT JOIN (
                SELECT a.company_id, a.original_doc_id, a.original_line_id,
                       SUM(a.returned_qty) AS ReturnedQty
                FROM purchase_receipt_return_line_allocations a
                INNER JOIN purchase_documents rd
                  ON rd.company_id = a.company_id
                 AND rd.doc_id = a.return_doc_id
                 AND rd.doc_kind = 'RCR'
                 AND rd.status = 'Posted'
                 AND CAST(rd.is_deleted AS TEXT) IN ('0', 'false')
                 AND rd.doc_date < $toEx
                GROUP BY a.company_id, a.original_doc_id, a.original_line_id
            ) rt ON rt.company_id = r.company_id
                AND rt.original_doc_id = r.doc_id
                AND rt.original_line_id = l.line_id
            WHERE co.company_code = $company
              AND r.doc_kind = 'RCV'
              AND r.status = 'Posted'
              AND CAST(r.is_deleted AS TEXT) IN ('0', 'false')
              AND r.doc_date >= $from
              AND r.doc_date < $toEx
            ORDER BY r.doc_date, r.doc_no, l.line_no;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$toEx", to.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GoodsReceivedNotInvoicedReconciliationAsync(
        DateTime asOf, CancellationToken ct)
        => await GoodsReceivedNotInvoicedReconciliationAsync(null, asOf, ct);

    /// <summary>
    /// GRNI tie-out whose receipt scope ALIGNS with the detail query (Y2
    /// defect fix): when a from-date is supplied it bounds BOTH the open
    /// receipt lines and the GL 2203 balance, so the difference the report
    /// shows can always be explained by the rows above it. A null from-date
    /// is unbounded, matching the detail query's new default.
    /// The remaining-qty pro-rata factor carries an explicit * 1.0: in
    /// SQLite, quantities stored as integers would otherwise make
    /// (remaining / qty) an INTEGER division and zero out every
    /// partially-matched line (PostgreSQL NUMERIC division is exact and
    /// unaffected). The same factor guards the detail query and the
    /// by-period tie-out, so all three always agree.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GoodsReceivedNotInvoicedReconciliationAsync(
        DateTime? from, DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        var receiptFrom = from.HasValue ? "AND r.doc_date >= $from" : string.Empty;
        var glFrom = from.HasValue ? "AND j.entry_date >= $from" : string.Empty;
        var sql = $"""
            WITH open_receipt_lines AS (
                SELECT l.line_subtotal * r.exchange_rate *
                    CASE WHEN l.qty > 0 AND l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0) > 0
                         THEN (l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0)) * 1.0 / l.qty
                         ELSE 0 END AS OpenBaseAmount
                FROM purchase_documents r
                INNER JOIN companies co ON CAST(co.company_id AS TEXT) = r.company_id
                INNER JOIN purchase_document_lines l ON l.doc_id = r.doc_id
                INNER JOIN document_links dl
                  ON dl.company_id = r.company_id
                 AND dl.source_doc_kind = 'PO'
                 AND dl.target_doc_kind = 'RCV'
                 AND dl.target_doc_id = r.doc_id
                LEFT JOIN (
                    SELECT m.company_id, m.receipt_doc_id, m.receipt_line_id,
                           SUM(m.matched_qty) AS MatchedQty
                    FROM purchase_receipt_invoice_matches m
                    INNER JOIN purchase_documents inv
                      ON inv.company_id = m.company_id
                     AND inv.doc_id = m.invoice_doc_id
                     AND inv.doc_kind = 'PUR'
                     AND inv.status = 'Posted'
                     AND CAST(inv.is_deleted AS TEXT) IN ('0', 'false')
                     AND inv.doc_date <= $asOf
                    GROUP BY m.company_id, m.receipt_doc_id, m.receipt_line_id
                ) m ON m.company_id = r.company_id
                   AND m.receipt_doc_id = r.doc_id
                   AND m.receipt_line_id = l.line_id
                LEFT JOIN (
                    SELECT a.company_id, a.original_doc_id, a.original_line_id,
                           SUM(a.returned_qty) AS ReturnedQty
                    FROM purchase_receipt_return_line_allocations a
                    INNER JOIN purchase_documents rd
                      ON rd.company_id = a.company_id
                     AND rd.doc_id = a.return_doc_id
                     AND rd.doc_kind = 'RCR'
                     AND rd.status = 'Posted'
                     AND CAST(rd.is_deleted AS TEXT) IN ('0', 'false')
                     AND rd.doc_date <= $asOf
                    GROUP BY a.company_id, a.original_doc_id, a.original_line_id
                ) rt ON rt.company_id = r.company_id
                    AND rt.original_doc_id = r.doc_id
                    AND rt.original_line_id = l.line_id
                WHERE co.company_code = $company
                  AND r.doc_kind = 'RCV'
                  AND r.status = 'Posted'
                  AND CAST(r.is_deleted AS TEXT) IN ('0', 'false')
                  {receiptFrom}
                  AND r.doc_date <= $asOf
            ),
            receipt_control AS (
                SELECT COALESCE(SUM(OpenBaseAmount), 0) AS OpenReceiptBase
                FROM open_receipt_lines
            ),
            gl_control AS (
                SELECT COALESCE(SUM(COALESCE(l.base_credit, l.credit) -
                                    COALESCE(l.base_debit, l.debit)), 0) AS GlBalanceBase
                FROM journal_entries j
                INNER JOIN companies co ON co.company_id = j.company_id
                INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
                INNER JOIN chart_of_accounts a
                  ON a.account_id = l.account_id AND a.company_id = j.company_id
                WHERE co.company_code = $company
                  AND a.account_code = '2203'
                  AND j.status IN (1, 2)
                  {glFrom}
                  AND j.entry_date <= $asOf
            )
            SELECT receipt_control.OpenReceiptBase,
                   gl_control.GlBalanceBase,
                   receipt_control.OpenReceiptBase - gl_control.GlBalanceBase AS DifferenceBase
            FROM receipt_control CROSS JOIN gl_control;
            """;
        var args = new List<(string, object?)>
        {
            ("$company", Company),
            ("$asOf", DateStart(asOf))
        };
        if (from.HasValue)
            args.Add(("$from", DateStart(from.Value)));
        return await QueryAsync(sql, ct, args.ToArray());
    }

    /// <summary>
    /// Y2 exception (c): the GL 2203 tie-out broken down per period
    /// (calendar month). ReceiptBase is the still-open receipt value of
    /// receipts dated in the period (valued as of the as-of date, exactly
    /// like the reconciliation CTE); GlMovementBase is the net posted
    /// movement of account 2203 in the period. The window accumulates both
    /// sides and flags every period whose cumulative difference is not
    /// zero. Company-scoped, parameterized, provider-neutral (period is a
    /// plain yyyy-MM prefix of the ISO date text on both providers).
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GrniTieOutByPeriodAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            WITH open_receipt_lines AS (
                SELECT substr(CAST(r.doc_date AS TEXT), 1, 7) AS Period,
                       l.line_subtotal * r.exchange_rate *
                       CASE WHEN l.qty > 0 AND l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0) > 0
                            THEN (l.qty - COALESCE(m.MatchedQty, 0) - COALESCE(rt.ReturnedQty, 0)) * 1.0 / l.qty
                            ELSE 0 END AS OpenBaseAmount
                FROM purchase_documents r
                INNER JOIN companies co ON CAST(co.company_id AS TEXT) = r.company_id
                INNER JOIN purchase_document_lines l ON l.doc_id = r.doc_id
                INNER JOIN document_links dl
                  ON dl.company_id = r.company_id
                 AND dl.source_doc_kind = 'PO'
                 AND dl.target_doc_kind = 'RCV'
                 AND dl.target_doc_id = r.doc_id
                LEFT JOIN (
                    SELECT m.company_id, m.receipt_doc_id, m.receipt_line_id,
                           SUM(m.matched_qty) AS MatchedQty
                    FROM purchase_receipt_invoice_matches m
                    INNER JOIN purchase_documents inv
                      ON inv.company_id = m.company_id
                     AND inv.doc_id = m.invoice_doc_id
                     AND inv.doc_kind = 'PUR'
                     AND inv.status = 'Posted'
                     AND CAST(inv.is_deleted AS TEXT) IN ('0', 'false')
                     AND inv.doc_date <= $asOf
                    GROUP BY m.company_id, m.receipt_doc_id, m.receipt_line_id
                ) m ON m.company_id = r.company_id
                   AND m.receipt_doc_id = r.doc_id
                   AND m.receipt_line_id = l.line_id
                LEFT JOIN (
                    SELECT a.company_id, a.original_doc_id, a.original_line_id,
                           SUM(a.returned_qty) AS ReturnedQty
                    FROM purchase_receipt_return_line_allocations a
                    INNER JOIN purchase_documents rd
                      ON rd.company_id = a.company_id
                     AND rd.doc_id = a.return_doc_id
                     AND rd.doc_kind = 'RCR'
                     AND rd.status = 'Posted'
                     AND CAST(rd.is_deleted AS TEXT) IN ('0', 'false')
                     AND rd.doc_date <= $asOf
                    GROUP BY a.company_id, a.original_doc_id, a.original_line_id
                ) rt ON rt.company_id = r.company_id
                    AND rt.original_doc_id = r.doc_id
                    AND rt.original_line_id = l.line_id
                WHERE co.company_code = $company
                  AND r.doc_kind = 'RCV'
                  AND r.status = 'Posted'
                  AND CAST(r.is_deleted AS TEXT) IN ('0', 'false')
                  AND r.doc_date <= $asOf
            ),
            receipt_by_period AS (
                SELECT Period, SUM(OpenBaseAmount) AS ReceiptBase
                FROM open_receipt_lines
                GROUP BY Period
            ),
            gl_by_period AS (
                SELECT substr(CAST(j.entry_date AS TEXT), 1, 7) AS Period,
                       SUM(COALESCE(l.base_credit, l.credit) -
                           COALESCE(l.base_debit, l.debit)) AS GlMovementBase
                FROM journal_entries j
                INNER JOIN companies co ON co.company_id = j.company_id
                INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
                INNER JOIN chart_of_accounts a
                  ON a.account_id = l.account_id AND a.company_id = j.company_id
                WHERE co.company_code = $company
                  AND a.account_code = '2203'
                  AND j.status IN (1, 2)
                  AND j.entry_date <= $asOf
                GROUP BY substr(CAST(j.entry_date AS TEXT), 1, 7)
            ),
            periods AS (
                SELECT Period FROM receipt_by_period
                UNION
                SELECT Period FROM gl_by_period
            )
            SELECT p.Period AS Period,
                   COALESCE(r.ReceiptBase, 0) AS ReceiptBase,
                   COALESCE(g.GlMovementBase, 0) AS GlMovementBase
            FROM periods p
            LEFT JOIN receipt_by_period r ON r.Period = p.Period
            LEFT JOIN gl_by_period g ON g.Period = p.Period
            ORDER BY p.Period;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$asOf", DateStart(asOf)));
    }

    /// <summary>
    /// W8 reconciliation (a), mirroring the GRNI control pattern above:
    /// the customer subledger (party_ledger, kind C, BASE amounts —
    /// migration 062 basis) against the AR control account 1201 in the
    /// GL (posted + reversed originals, status 1/2, so a reversed pair
    /// nets to zero exactly as in the GRNI query). One row:
    /// SubledgerBase / GlBalanceBase / DifferenceBase, all as of the
    /// given date. A non-zero difference is the exception the report
    /// exists to flag.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AccountsReceivableReconciliationAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            WITH subledger AS (
                SELECT COALESCE(SUM(COALESCE(l.base_debit, l.debit) -
                                    COALESCE(l.base_credit, l.credit)), 0) AS SubledgerBase
                FROM party_ledger l
                WHERE l.company_id = $company AND l.party_kind = 'C'
                  AND l.entry_date <= $asOf
            ),
            gl_control AS (
                SELECT COALESCE(SUM(COALESCE(l.base_debit, l.debit) -
                                    COALESCE(l.base_credit, l.credit)), 0) AS GlBalanceBase
                FROM journal_entries j
                INNER JOIN companies co ON co.company_id = j.company_id
                INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
                INNER JOIN chart_of_accounts a
                  ON a.account_id = l.account_id AND a.company_id = j.company_id
                WHERE co.company_code = $company
                  AND a.account_code = '1201'
                  AND j.status IN (1, 2)
                  AND j.entry_date <= $asOf
            )
            SELECT subledger.SubledgerBase,
                   gl_control.GlBalanceBase,
                   subledger.SubledgerBase - gl_control.GlBalanceBase AS DifferenceBase
            FROM subledger CROSS JOIN gl_control;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$asOf", DateStart(asOf)));
    }

    /// <summary>
    /// W8 reconciliation (b): the StockValuationEngine's valuation total
    /// against the inventory control accounts in the GL. The valuation
    /// side is the engine-maintained material_stock_balances snapshot
    /// (stock_value, falling back to qty x average_cost exactly as the
    /// stock reports compute Value) — a CURRENT snapshot, so the AsOf
    /// filter applies to the GL side and the report is exact when run
    /// as of today. The GL account set is the 1203 family the valuation
    /// code posts to: 1203 Inventory, 1211 WIP, 1212 Finished Goods and
    /// 1215 Stock Delivered But Not Billed (migration 094: a posted
    /// delivery debits 1215 while the goods still sit in the balances
    /// until invoiced, so 1215 belongs to the control total).
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InventoryReconciliationAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = """
            WITH valuation AS (
                SELECT COALESCE(SUM(CASE WHEN COALESCE(s.stock_value, 0) <> 0 THEN s.stock_value
                                         ELSE COALESCE(s.available_qty, 0) * COALESCE(s.average_cost, 0) END), 0) AS ValuationBase
                FROM material_stock_balances s
                WHERE s.company_id = $company AND s.is_deleted = 0
            ),
            gl_control AS (
                SELECT COALESCE(SUM(COALESCE(l.base_debit, l.debit) -
                                    COALESCE(l.base_credit, l.credit)), 0) AS GlBalanceBase
                FROM journal_entries j
                INNER JOIN companies co ON co.company_id = j.company_id
                INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
                INNER JOIN chart_of_accounts a
                  ON a.account_id = l.account_id AND a.company_id = j.company_id
                WHERE co.company_code = $company
                  AND a.account_code IN ('1203', '1211', '1212', '1215')
                  AND j.status IN (1, 2)
                  AND j.entry_date <= $asOf
            )
            SELECT valuation.ValuationBase AS SubledgerBase,
                   gl_control.GlBalanceBase,
                   valuation.ValuationBase - gl_control.GlBalanceBase AS DifferenceBase
            FROM valuation CROSS JOIN gl_control;
            """;
        if (DatabaseRuntime.IsPostgreSql())
            sql = sql.Replace("s.is_deleted = 0", "s.is_deleted = FALSE", StringComparison.Ordinal);
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$asOf", DateStart(asOf)));
    }

    /// <summary>
    /// Y2 exception (b): posted purchase invoices that have NO rows in
    /// purchase_receipt_invoice_matches (migration 078) — the manual
    /// InvoiceWin/InvRepo path creates such invoices whenever the
    /// three-way-match policy is off, and nothing flagged them before.
    /// The match check keys on the invoice doc_id alone: doc_id is the
    /// table's primary key, unique across both company-key spellings
    /// (code for the manual path, Guid for the chain path). Company scope
    /// uses the dual-key companies join, as in PurchaseInvoiceListAsync.
    /// Amounts are document currency plus the base-currency restatement
    /// (grand_total × exchange_rate), the report family's base basis.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> InvoicedWithoutReceiptAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_no AS InvoiceNo,
                   d.doc_date AS InvoiceDate,
                   COALESCE(s.code, '') AS SupplierCode,
                   COALESCE(NULLIF(s.name_en, ''), s.name_ar, d.party_name, '') AS SupplierName,
                   COALESCE(d.currency_code, '') AS CurrencyCode,
                   COALESCE(d.grand_total, 0) AS InvoiceAmount,
                   COALESCE(d.grand_total, 0) * COALESCE(NULLIF(d.exchange_rate, 0), 1) AS InvoiceBaseAmount,
                   COALESCE(d.paid_amount, 0) AS PaidAmount
            FROM purchase_documents d
            INNER JOIN companies co
              ON co.company_code = $company
             AND (CAST(co.company_id AS TEXT) = CAST(d.company_id AS TEXT)
                  OR d.company_id = co.company_code)
            LEFT JOIN parties s
              ON s.ledger_id = d.party_id AND s.company_id = $company AND s.kind = 'S'
            WHERE d.doc_kind = 'PUR'
              AND d.status = 'Posted'
              AND CAST(d.is_deleted AS TEXT) IN ('0', 'false')
              AND d.doc_date <= $asOf
              AND NOT EXISTS (
                    SELECT 1 FROM purchase_receipt_invoice_matches m
                    WHERE m.invoice_doc_id = d.doc_id)
            ORDER BY d.doc_date, d.doc_no;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$asOf", FmtDate(asOf)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PartyAgingAsync(
        bool customers, CancellationToken ct)
        => await PartyAgingAsync(customers, DateTime.Today, ct);

    /// <summary>
    /// Y3: the entry-date aging gains an as-of date — entries after the
    /// as-of date are excluded and ages are measured against it, so a past
    /// period-end aging can be reproduced exactly. The no-as-of overload
    /// above keeps the historical "age against today" behaviour for the
    /// customer side.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PartyAgingAsync(
        bool customers, DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        var partyKind = customers ? "C" : "S";
        // Step 4: canonical parties store, joined on ledger_id; the aged
        // amount is oriented per kind (suppliers age their payables,
        // credit - debit — RP-2). Day difference is provider-aware
        // (julianday is SQLite-only — report-family R-5).
        // Step 7: aging buckets are BASE amounts — a foreign invoice
        // ages at its converted value, not its face value.
        var amount = customers ? "COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit)" : "COALESCE(l.base_credit, l.credit) - COALESCE(l.base_debit, l.debit)";
        // W1 (migration 095): aging is DUE-DATE based when the ledger
        // row's source document carries a due_date (doc_date +
        // credit_days on SAL/PUR invoices); legacy rows without one fall
        // back to the ledger entry date. The document join is on doc_id
        // alone — doc_id is a table-wide identity, unique across
        // companies — because documents key the company by Guid while
        // party_ledger keys it by code.
        var docTable = customers ? "sales_documents" : "purchase_documents";
        // due_date exists only on sales_documents (migration 095);
        // supplier aging keeps the entry-date basis.
        var ageBasis = customers ? "COALESCE(d.due_date, l.entry_date)" : "l.entry_date";
        var ageDays = DatabaseRuntime.IsPostgreSql()
            ? $"(CAST($asOf AS DATE) - CAST({ageBasis} AS DATE))"
            : $"(julianday($asOf) - julianday({ageBasis}))";
        var sql = $"""
            SELECT p.code AS Code,
                   COALESCE(NULLIF(p.name_en, ''), p.name_ar, '') AS Name,
                   COALESCE(SUM(CASE WHEN {ageDays} <= 30 THEN {amount} ELSE 0 END), 0) AS Bucket0_30,
                   COALESCE(SUM(CASE WHEN {ageDays} > 30 AND {ageDays} <= 60 THEN {amount} ELSE 0 END), 0) AS Bucket31_60,
                   COALESCE(SUM(CASE WHEN {ageDays} > 60 AND {ageDays} <= 90 THEN {amount} ELSE 0 END), 0) AS Bucket61_90,
                   COALESCE(SUM(CASE WHEN {ageDays} > 90 THEN {amount} ELSE 0 END), 0) AS Bucket90Plus,
                   COALESCE(SUM({amount}), 0) AS Balance
            FROM parties p
            LEFT JOIN party_ledger l
              ON l.party_id = p.ledger_id AND l.company_id = p.company_id AND l.party_kind = $pk
             AND l.entry_date <= $asOf
            LEFT JOIN {docTable} d
              ON d.doc_id = l.doc_id
            WHERE p.company_id = $company AND p.kind = $pk AND p.is_deleted = 0 AND p.is_active = 1
            GROUP BY p.code, p.name_en, p.name_ar
            HAVING ABS(COALESCE(SUM({amount}), 0)) > 0.0001
            ORDER BY p.code;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$pk", partyKind), ("$asOf", FmtDate(asOf)));
    }

    /// <summary>
    /// Y3: open purchase invoices aged by DUE DATE as of a given date —
    /// one row per invoice (ERPNext Accounts Payable shape), not a
    /// supplier-level ledger aggregate. The due date is
    /// purchase_documents.due_date (migration 094), falling back to the
    /// document date for invoices that predate the column. Open amount is
    /// the stored grand_total − paid_amount; buckets and DaysOverdue are
    /// computed in SQL from the as-of date with the provider-aware day
    /// difference, on the BASE amount (open × exchange_rate), matching
    /// the entry-date mode's base basis. An invoice not yet due has
    /// negative DaysOverdue and lands in the 0-30 bucket.
    /// </summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PayableDueDateAgingAsync(
        DateTime asOf, CancellationToken ct)
    {
        DocSchema.Ensure();
        var daysOverdue = DatabaseRuntime.IsPostgreSql()
            ? "(CAST($asOf AS DATE) - CAST(COALESCE(d.due_date, d.doc_date) AS DATE))"
            : "CAST(julianday($asOf) - julianday(COALESCE(d.due_date, d.doc_date)) AS INTEGER)";
        var sql = $"""
            SELECT d.doc_no AS InvoiceNo,
                   d.doc_date AS InvoiceDate,
                   COALESCE(d.due_date, d.doc_date) AS DueDate,
                   COALESCE(s.code, '') AS SupplierCode,
                   COALESCE(NULLIF(s.name_en, ''), s.name_ar, d.party_name, '') AS SupplierName,
                   COALESCE(d.currency_code, '') AS CurrencyCode,
                   COALESCE(d.grand_total, 0) - COALESCE(d.paid_amount, 0) AS OpenAmount,
                   (COALESCE(d.grand_total, 0) - COALESCE(d.paid_amount, 0))
                       * COALESCE(NULLIF(d.exchange_rate, 0), 1) AS OpenBaseAmount,
                   {daysOverdue} AS DaysOverdue,
                   CASE WHEN {daysOverdue} <= 30 THEN 'Bucket0_30'
                        WHEN {daysOverdue} <= 60 THEN 'Bucket31_60'
                        WHEN {daysOverdue} <= 90 THEN 'Bucket61_90'
                        ELSE 'Bucket90Plus' END AS Bucket
            FROM purchase_documents d
            INNER JOIN companies co
              ON co.company_code = $company
             AND (CAST(co.company_id AS TEXT) = CAST(d.company_id AS TEXT)
                  OR d.company_id = co.company_code)
            LEFT JOIN parties s
              ON s.ledger_id = d.party_id AND s.company_id = $company AND s.kind = 'S'
            WHERE d.doc_kind = 'PUR'
              AND d.status = 'Posted'
              AND CAST(d.is_deleted AS TEXT) IN ('0', 'false')
              AND d.doc_date <= $asOf
              AND COALESCE(d.grand_total, 0) - COALESCE(d.paid_amount, 0) > 0.0001
            ORDER BY COALESCE(d.due_date, d.doc_date), d.doc_no;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$asOf", FmtDate(asOf)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ItemProfitabilityAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        // Step 6: Cost/Profit/MarginPct added — cost is the engine's
        // movement cost_amount per material (net of returns), so this
        // report finally shows profitability instead of revenue only.
        // Sales returns net against qty/revenue/cost.
        var sql = PgBoolFix("""
            SELECT agg.MaterialCode AS MaterialCode,
                   agg.MaterialName AS MaterialName,
                   agg.QtySold AS QtySold,
                   agg.Revenue AS Revenue,
                   agg.Tax AS Tax,
                   agg.Total AS Total,
                   COALESCE(c.Cost, 0) AS Cost,
                   agg.Revenue - COALESCE(c.Cost, 0) AS Profit,
                   CASE WHEN agg.Revenue = 0 THEN 0
                        ELSE (agg.Revenue - COALESCE(c.Cost, 0)) / agg.Revenue END AS MarginPct
            FROM (
                SELECT COALESCE(l.material_code, '') AS MaterialCode,
                       COALESCE(l.description, '') AS MaterialName,
                       COALESCE(SUM(CASE WHEN d.doc_kind = 'SRT' THEN -l.qty ELSE l.qty END), 0) AS QtySold,
                       COALESCE(SUM(CASE WHEN d.doc_kind = 'SRT' THEN -l.line_subtotal ELSE l.line_subtotal END), 0) AS Revenue,
                       COALESCE(SUM(CASE WHEN d.doc_kind = 'SRT' THEN -l.line_tax ELSE l.line_tax END), 0) AS Tax,
                       COALESCE(SUM(CASE WHEN d.doc_kind = 'SRT' THEN -l.line_total ELSE l.line_total END), 0) AS Total
                FROM sales_document_lines l
                INNER JOIN sales_documents d ON d.doc_id = l.doc_id
                WHERE d.company_id = $company
                  AND d.is_deleted = 0
                  AND d.status = 'Posted'
                  AND d.doc_kind IN ('SAL','POS','SRT')
                  AND d.doc_date >= $from AND d.doc_date <= $to
                GROUP BY l.material_code, l.description
            ) agg
            LEFT JOIN (
                SELECT mm.code AS code, -SUM(mv.cost_amount) AS Cost
                FROM material_stock_movements mv
                INNER JOIN materials mm ON mm.company_id = mv.company_id AND mm.material_id = mv.material_id
                WHERE mv.company_id = $company AND mv.is_deleted = 0
                  AND mv.movement_type IN ('SAL', 'POS', 'SRT')
                  AND mv.movement_date >= $from AND mv.movement_date < $toEx
                GROUP BY mm.code
            ) c ON c.code = agg.MaterialCode
            ORDER BY Revenue DESC;
            """);
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$toEx", DateEndExclusive(to)));
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
        "(LOWER(COALESCE(a.account_name, '')) LIKE '%cash%' OR LOWER(COALESCE(a.account_name, '')) LIKE '%bank%' OR LOWER(COALESCE(a.account_name, '')) LIKE '%box%')";
    private const string BankAccountSql =
        "(LOWER(COALESCE(a.account_name, '')) LIKE '%bank%')";

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
                        ELSE SUM(COALESCE(sm.cost_amount, (sm.qty_in - sm.qty_out) * sm.unit_cost)) / SUM(sm.qty_in - sm.qty_out) END AS AvgCost,
                   COALESCE(SUM(COALESCE(sm.cost_amount, (sm.qty_in - sm.qty_out) * sm.unit_cost)), 0) AS Value
            FROM material_stock_movements sm
            LEFT JOIN materials m ON m.company_id = sm.company_id AND m.material_id = sm.material_id
            LEFT JOIN warehouses w ON w.company_id = sm.company_id AND w.warehouse_id = sm.warehouse_id
            WHERE sm.company_id = $company AND sm.is_deleted = 0 AND sm.movement_date < $asof
            GROUP BY sm.warehouse_id, w.warehouse_code, m.code, m.name_ar, m.default_unit
            ORDER BY Warehouse, MaterialCode;
            """);
        // Step 6: Value is the sum of the engine's signed cost_amount
        // (legacy rows fall back to qty x recorded unit_cost), and the
        // as-of boundary is end-exclusive like every other report here —
        // the old '<= date' form dropped same-day movements whose
        // timestamps carried a time component on PostgreSQL, which is
        // how this report disagreed with Stock Balances (70 vs 40).
        return await QueryAsync(sql, ct, ("$company", Company), ("$asof", DateEndExclusive(asOf)));
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
        // Phase B 054: REAL batch expiry. Rows are batches with stock on
        // hand whose stock_batches.expiry_date is past, or within the
        // material's alert_before_expiry_days of today — the same rule
        // BatchSerialService.GetExpiredBatches implements for the
        // headless proof. The pre-054 inference (last movement + shelf
        // life) is gone: no date is ever fabricated here.
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT m.code AS MaterialCode,
                   m.name_ar AS MaterialName,
                   COALESCE(m.default_unit, '') AS Unit,
                   b.batch_no AS BatchNo,
                   COALESCE(w.warehouse_code, CAST(bb.warehouse_id AS TEXT)) AS Warehouse,
                   b.expiry_date AS ExpiryDate,
                   COALESCE(m.alert_before_expiry_days, 0) AS AlertDays,
                   bb.qty_on_hand AS OnHand
            FROM stock_batch_balances bb
            INNER JOIN stock_batches b ON b.batch_id = bb.batch_id
            INNER JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
            LEFT JOIN warehouses w ON w.company_id = bb.company_id AND w.warehouse_id = bb.warehouse_id
            WHERE bb.company_id = $company AND bb.qty_on_hand > 0
              AND b.is_deleted = 0 AND m.is_deleted = 0
              AND b.expiry_date IS NOT NULL AND b.expiry_date <> ''
            ORDER BY b.expiry_date, m.code, b.batch_no;
            """);
        var rows = await QueryAsync(sql, ct, ("$company", Company));
        var today = DateTime.Today;
        var kept = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in rows)
        {
            var expiryText = Convert.ToString(row.GetValueOrDefault("ExpiryDate"), CultureInfo.InvariantCulture);
            if (!DateTime.TryParseExact(expiryText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var expiry))
                continue;
            var alertDays = (int)RptWinUtil.Dec(row.GetValueOrDefault("AlertDays"));
            var daysRemaining = (expiry.Date - today).Days;
            if (daysRemaining >= 0 && daysRemaining > alertDays)
                continue;
            var copy = new Dictionary<string, object?>(row)
            {
                ["DaysRemaining"] = daysRemaining,
                ["IsExpired"] = daysRemaining < 0 ? 1 : 0
            };
            kept.Add(copy);
        }
        return kept;
    }

    /// <summary>Stock by batch x warehouse (Phase B 054): every batch
    /// balance row with its batch master data — the BatchBalWin source.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BatchBalancesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        var sql = PgBoolFix("""
            SELECT m.code AS MaterialCode,
                   m.name_ar AS MaterialName,
                   COALESCE(m.default_unit, '') AS Unit,
                   b.batch_no AS BatchNo,
                   COALESCE(b.supplier_batch_no, '') AS SupplierBatchNo,
                   b.mfg_date AS MfgDate,
                   b.expiry_date AS ExpiryDate,
                   b.status AS BatchStatus,
                   COALESCE(w.warehouse_code, CAST(bb.warehouse_id AS TEXT)) AS Warehouse,
                   bb.qty_on_hand AS OnHand
            FROM stock_batch_balances bb
            INNER JOIN stock_batches b ON b.batch_id = bb.batch_id
            INNER JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
            LEFT JOIN warehouses w ON w.company_id = bb.company_id AND w.warehouse_id = bb.warehouse_id
            WHERE bb.company_id = $company AND b.is_deleted = 0 AND m.is_deleted = 0
            ORDER BY m.code, b.batch_no, Warehouse;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    /// <summary>Budget vs actual (Phase B 055): one row per budget line
    /// with the annual and accumulated-monthly budget/actual pairs. The
    /// computation is delegated to BudgetRepository, whose actuals are
    /// the BudgetGuard's own — the report can never disagree with the
    /// posting guard. Budgets live in the journal world, so the shell's
    /// company CODE is first resolved to the companies Guid.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> BudgetVarianceAsync(
        CancellationToken ct)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new AppError(ErrorCodes.DatabaseNotInitialized,
                "No database connection is configured for reports.");
        var companyRows = await QueryAsync(
            "SELECT company_id AS CompanyId FROM companies WHERE company_code = $company", ct,
            ("$company", Company));
        if (companyRows.Count == 0 ||
            !Guid.TryParse(Convert.ToString(companyRows[0]["CompanyId"], CultureInfo.InvariantCulture), out var companyId))
            return Array.Empty<IReadOnlyDictionary<string, object?>>();

        var provider = DatabaseRuntime.IsPostgreSql() ? "PostgreSQL" : "SQLite";
        var repository = new ShouTech.Infrastructure.BudgetRepository(provider, cs);
        var variance = await repository.GetVarianceAsync(companyId, ct);
        var result = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in variance)
        {
            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Budget"] = row.BudgetName,
                ["FiscalYear"] = row.FiscalYearLabel,
                ["Account"] = string.IsNullOrWhiteSpace(row.AccountName)
                    ? row.AccountCode
                    : $"{row.AccountCode} — {row.AccountName}",
                ["Dimension"] = row.DimensionCode ?? string.Empty,
                ["AnnualBudget"] = row.AnnualBudget,
                ["AnnualActual"] = row.AnnualActual,
                ["Remaining"] = row.AnnualRemaining,
                ["Percent"] = row.PercentConsumed,
                ["MonthlyBudget"] = row.MonthlyBudgetToDate,
                ["MonthlyActual"] = row.MonthlyActualToDate
            });
        }
        return result;
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
        // Step 6: Cost comes from the engine's movement cost_amount
        // (per material, net of sales returns), and sales/purchase
        // aggregates net their returns (SRT/PRT) instead of ignoring
        // them. The old Cost multiplied raw line qty by the card's
        // avg_cost — a figure no posting path maintained.
        var sql = PgBoolFix("""
            SELECT m.code AS MaterialCode,
                   m.name_ar AS MaterialName,
                   COALESCE(s.QtySold, 0) AS QtySold,
                   COALESCE(s.Revenue, 0) AS Revenue,
                   COALESCE(p.QtyPurchased, 0) AS QtyPurchased,
                   COALESCE(p.PurchaseAmount, 0) AS PurchaseAmount,
                   COALESCE(c.Cost, 0) AS Cost,
                   COALESCE(s.Revenue, 0) - COALESCE(c.Cost, 0) AS Margin,
                   CASE WHEN COALESCE(s.Revenue, 0) = 0 THEN 0
                        ELSE (COALESCE(s.Revenue, 0) - COALESCE(c.Cost, 0)) / s.Revenue END AS MarginPct
            FROM materials m
            LEFT JOIN (
                SELECT l.material_code AS code,
                       SUM(CASE WHEN d.doc_kind = 'SRT' THEN -l.qty ELSE l.qty END) AS QtySold,
                       SUM(CASE WHEN d.doc_kind = 'SRT' THEN -l.line_total ELSE l.line_total END) AS Revenue
                FROM sales_document_lines l INNER JOIN sales_documents d ON d.doc_id = l.doc_id
                WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
                  AND d.doc_kind IN ('SAL', 'POS', 'SRT') AND d.doc_date >= $from AND d.doc_date <= $to
                GROUP BY l.material_code
            ) s ON s.code = m.code
            LEFT JOIN (
                SELECT l.material_code AS code,
                       SUM(CASE WHEN d.doc_kind = 'PRT' THEN -l.qty ELSE l.qty END) AS QtyPurchased,
                       SUM(CASE WHEN d.doc_kind = 'PRT' THEN -l.line_total ELSE l.line_total END) AS PurchaseAmount
                FROM purchase_document_lines l INNER JOIN purchase_documents d ON d.doc_id = l.doc_id
                WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
                  AND d.doc_kind IN ('PUR', 'PRT') AND d.doc_date >= $from AND d.doc_date <= $to
                GROUP BY l.material_code
            ) p ON p.code = m.code
            LEFT JOIN (
                SELECT mv.material_id AS mid, -SUM(mv.cost_amount) AS Cost
                FROM material_stock_movements mv
                WHERE mv.company_id = $company AND mv.is_deleted = 0
                  AND mv.movement_type IN ('SAL', 'POS', 'SRT')
                  AND mv.movement_date >= $from AND mv.movement_date < $toEx
                GROUP BY mv.material_id
            ) c ON c.mid = m.material_id
            WHERE m.company_id = $company AND m.is_deleted = 0
              AND (COALESCE(s.QtySold, 0) <> 0 OR COALESCE(p.QtyPurchased, 0) <> 0)
            ORDER BY Revenue DESC;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)),
            ("$toEx", DateEndExclusive(to)));
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
                   CASE WHEN COALESCE(b.stock_value, 0) <> 0 THEN b.stock_value
                        ELSE COALESCE(b.available_qty, 0) * COALESCE(b.average_cost, 0) END AS Value,
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
                   COALESCE(SUM(ABS(COALESCE(sm.cost_amount, (sm.qty_in - sm.qty_out) * sm.unit_cost))), 0) AS Value
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
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f1 AND sm.movement_date <= $t1 THEN COALESCE(sm.cost_amount, (sm.qty_in - sm.qty_out) * sm.unit_cost) ELSE 0 END), 0) AS Value1,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN sm.qty_in - sm.qty_out ELSE 0 END), 0) AS Qty2,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN COALESCE(sm.cost_amount, (sm.qty_in - sm.qty_out) * sm.unit_cost) ELSE 0 END), 0) AS Value2,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN sm.qty_in - sm.qty_out ELSE 0 END), 0)
                     - COALESCE(SUM(CASE WHEN sm.movement_date >= $f1 AND sm.movement_date <= $t1 THEN sm.qty_in - sm.qty_out ELSE 0 END), 0) AS QtyDiff,
                   COALESCE(SUM(CASE WHEN sm.movement_date >= $f2 AND sm.movement_date <= $t2 THEN COALESCE(sm.cost_amount, (sm.qty_in - sm.qty_out) * sm.unit_cost) ELSE 0 END), 0)
                     - COALESCE(SUM(CASE WHEN sm.movement_date >= $f1 AND sm.movement_date <= $t1 THEN COALESCE(sm.cost_amount, (sm.qty_in - sm.qty_out) * sm.unit_cost) ELSE 0 END), 0) AS ValueDiff
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
        // Step 6: read from the balances (the valuation of record), so
        // this report agrees with Stock Balances' NEGATIVE filter by
        // construction — the old movement-sum version could disagree
        // with it whenever the two derivations diverged.
        var sql = PgBoolFix("""
            SELECT COALESCE(w.warehouse_code, CAST(b.warehouse_id AS TEXT)) AS Warehouse,
                   COALESCE(m.code, '') AS MaterialCode,
                   COALESCE(m.name_ar, '') AS MaterialName,
                   COALESCE(b.available_qty, 0) AS Balance,
                   COALESCE(b.average_cost, 0) AS AvgCost,
                   CASE WHEN COALESCE(b.stock_value, 0) <> 0 THEN b.stock_value
                        ELSE COALESCE(b.available_qty, 0) * COALESCE(b.average_cost, 0) END AS Value
            FROM material_stock_balances b
            LEFT JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
            LEFT JOIN warehouses w ON w.company_id = b.company_id AND w.warehouse_id = b.warehouse_id
            WHERE b.company_id = $company AND b.is_deleted = 0 AND COALESCE(b.available_qty, 0) < -0.0001
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
                   CASE WHEN COALESCE(b.stock_value, 0) <> 0 THEN b.stock_value
                        ELSE COALESCE(b.available_qty, 0) * COALESCE(b.average_cost, 0) END AS Value
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
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit)), 0) AS Debit,
                   COALESCE(SUM(COALESCE(l.base_credit, l.credit)), 0) AS Credit,
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit)), 0) AS Balance,
                   COUNT(*) AS LineCount
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company AND j.status = 1
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
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit)), 0) AS Debit,
                   COALESCE(SUM(COALESCE(l.base_credit, l.credit)), 0) AS Credit,
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit)), 0) AS Balance
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = j.company_id
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company AND j.status = 1 AND a.is_deleted = 0
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
            INNER JOIN companies co ON co.company_id = j.company_id
            LEFT JOIN (
                SELECT journal_entry_id, SUM(COALESCE(base_debit, debit)) AS TotalDebit
                FROM journal_entry_lines GROUP BY journal_entry_id
            ) t ON t.journal_entry_id = j.journal_entry_id
            WHERE co.company_code = $company AND j.status = 1
              AND j.entry_date >= $from AND j.entry_date <= $to
            GROUP BY COALESCE(j.currency_code, 'SAR'), substr(j.entry_date, 1, 7)
            ORDER BY Currency, Month;
            """;
        return await QueryAsync(sql, ct, ("$company", Company), ("$from", FmtDate(from)), ("$to", FmtDate(to)));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExchangeRateDifferencesAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        // Step 7: BaseDebit is the lines' STORED base amount (migration
        // 062 — converted at posting at the effective rate), and
        // Difference is base minus transaction. Before, both were
        // derived by multiplying the unconverted sums by the header
        // rate at report time, which disagreed with the posted lines
        // whenever a line's effective rate came from the master table.
        DocSchema.Ensure();
        const string sql = """
            SELECT j.entry_date AS DocDate,
                   COALESCE(j.document_number, j.journal_entry_id) AS DocNo,
                   COALESCE(j.currency_code, 'SAR') AS Currency,
                   COALESCE(j.exchange_rate, 1) AS Rate,
                   COALESCE(SUM(l.debit), 0) AS Debit,
                   COALESCE(SUM(l.credit), 0) AS Credit,
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit)), 0) AS BaseDebit,
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit)), 0) - COALESCE(SUM(l.debit), 0) AS Difference
            FROM journal_entries j
            INNER JOIN companies co ON co.company_id = j.company_id
            INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
            WHERE co.company_code = $company AND j.status = 1
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
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit)), 0) AS Debit,
                   COALESCE(SUM(COALESCE(l.base_credit, l.credit)), 0) AS Credit,
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit)), 0) AS Balance
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = j.company_id
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company AND j.status = 1 AND a.is_deleted = 0
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
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit)), 0) AS Amount
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = j.company_id
            INNER JOIN companies co ON co.company_id = j.company_id
            LEFT JOIN (
                SELECT l2.journal_entry_id AS EntryId,
                       (SELECT a2.account_code FROM journal_entry_lines lx
                        INNER JOIN journal_entries jx ON jx.journal_entry_id = lx.journal_entry_id
                        INNER JOIN chart_of_accounts a2 ON a2.account_id = lx.account_id AND a2.company_id = jx.company_id
                        WHERE lx.journal_entry_id = l2.journal_entry_id
                          AND NOT (LOWER(COALESCE(a2.account_name, '')) LIKE '%cash%' OR LOWER(COALESCE(a2.account_name, '')) LIKE '%bank%' OR LOWER(COALESCE(a2.account_name, '')) LIKE '%box%')
                        ORDER BY lx.line_number LIMIT 1) AS Code
                FROM journal_entry_lines l2 GROUP BY l2.journal_entry_id
            ) cp ON cp.EntryId = j.journal_entry_id
            WHERE co.company_code = $company AND j.status = 1 AND a.is_deleted = 0
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
            SELECT COALESCE(SUM(COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit)), 0) AS Balance
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = j.company_id
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company AND j.status = 1 AND a.is_deleted = 0
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
                   COALESCE(a.name_ar, a.account_name) AS AccountName,
                   COALESCE(SUM(m.debit), 0) AS Debit,
                   COALESCE(SUM(m.credit), 0) AS Credit,
                   COALESCE(SUM(m.debit - m.credit), 0) AS Balance,
                   MAX(m.entry_date) AS LastMoveDate
            FROM chart_of_accounts a
            INNER JOIN companies co ON co.company_id = a.company_id
            LEFT JOIN (
                SELECT l.account_id AS aid, COALESCE(l.base_debit, l.debit) AS debit, COALESCE(l.base_credit, l.credit) AS credit, j.entry_date AS entry_date
                FROM journal_entry_lines l
                INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                WHERE j.status = 1
            ) m ON m.aid = a.account_id
            WHERE co.company_code = $company AND a.is_deleted = 0
            GROUP BY a.account_id, a.account_code, a.name_ar, a.account_name
            HAVING ABS(COALESCE(SUM(m.debit - m.credit), 0)) > 0.0001
              AND COALESCE(MAX(m.entry_date), '0001-01-01') < $cutoff
            ORDER BY a.account_code;
            """);
        return await QueryAsync(sql, ct, ("$company", Company), ("$cutoff", cutoff));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CollectionRatesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(c.code, CAST(pl.party_id AS TEXT)) AS PartyCode,
                   COALESCE(NULLIF(c.name_en, ''), c.name_ar, '') AS PartyName,
                   COALESCE(SUM(COALESCE(pl.base_debit, pl.debit)), 0) AS Invoiced,
                   COALESCE(SUM(COALESCE(pl.base_credit, pl.credit)), 0) AS Collected,
                   COALESCE(SUM(COALESCE(pl.base_debit, pl.debit) - COALESCE(pl.base_credit, pl.credit)), 0) AS Balance,
                   CASE WHEN SUM(COALESCE(pl.base_debit, pl.debit)) = 0 THEN 0 ELSE SUM(COALESCE(pl.base_credit, pl.credit)) / SUM(COALESCE(pl.base_debit, pl.debit)) END AS CollectionPct
            FROM party_ledger pl
            LEFT JOIN parties c ON c.ledger_id = pl.party_id AND c.company_id = pl.company_id AND c.kind = 'C'
            WHERE pl.company_id = $company AND pl.party_kind = 'C'
            GROUP BY pl.party_id, c.code, c.name_en, c.name_ar
            HAVING ABS(COALESCE(SUM(COALESCE(pl.base_debit, pl.debit)), 0)) > 0.0001
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
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN COALESCE(l.base_debit, l.debit) ELSE 0 END), 0) AS Debit1,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN COALESCE(l.base_credit, l.credit) ELSE 0 END), 0) AS Credit1,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit) ELSE 0 END), 0) AS Balance1,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN COALESCE(l.base_debit, l.debit) ELSE 0 END), 0) AS Debit2,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN COALESCE(l.base_credit, l.credit) ELSE 0 END), 0) AS Credit2,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit) ELSE 0 END), 0) AS Balance2,
                   COALESCE(SUM(CASE WHEN j.entry_date >= $f2 AND j.entry_date <= $t2 THEN COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit) ELSE 0 END), 0)
                     - COALESCE(SUM(CASE WHEN j.entry_date >= $f1 AND j.entry_date <= $t1 THEN COALESCE(l.base_debit, l.debit) - COALESCE(l.base_credit, l.credit) ELSE 0 END), 0) AS Diff
            FROM journal_entry_lines l
            INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
            INNER JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = j.company_id
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company AND j.status = 1 AND a.is_deleted = 0
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
                   COALESCE(SUM(COALESCE(l.base_debit, l.debit)), 0) AS Debit,
                   COALESCE(SUM(COALESCE(l.base_credit, l.credit)), 0) AS Credit,
                   CASE j.status WHEN 0 THEN 'Draft' WHEN 1 THEN 'Posted' ELSE 'Reversed' END AS Status
            FROM journal_entries j
            INNER JOIN journal_entry_lines l ON l.journal_entry_id = j.journal_entry_id
            INNER JOIN chart_of_accounts a ON a.account_id = l.account_id AND a.company_id = j.company_id
            INNER JOIN companies co ON co.company_id = j.company_id
            WHERE co.company_code = $company AND a.is_deleted = 0 AND {CashAccountSql}
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
        // Step 6: Cost is the valuation engine's signed cost_amount for
        // the document's own movements — valued per cost_method, in
        // base units, at posting time. The old formula (line qty in the
        // CHOSEN unit x the card's avg_cost, which no posting path ever
        // updated) reported a margin of 235 on a true profit of 100 in
        // the sweep case. Sales returns are included, netted: their
        // revenue and their re-entry cost both reverse.
        var sql = PgBoolFix("""
            SELECT d.doc_date AS DocDate,
                   d.doc_no AS DocNo,
                   COALESCE(d.party_name, '') AS PartyName,
                   CASE WHEN d.doc_kind = 'SRT' THEN -COALESCE(SUM(l.line_total), 0)
                        ELSE COALESCE(SUM(l.line_total), 0) END AS Revenue,
                   -COALESCE((
                       SELECT SUM(mv.cost_amount) FROM material_stock_movements mv
                       WHERE mv.company_id = d.company_id AND mv.document_no = d.doc_no
                         AND mv.movement_type IN ('SAL', 'POS', 'SRT') AND mv.is_deleted = 0
                   ), 0) AS Cost,
                   (CASE WHEN d.doc_kind = 'SRT' THEN -COALESCE(SUM(l.line_total), 0)
                         ELSE COALESCE(SUM(l.line_total), 0) END)
                   + COALESCE((
                       SELECT SUM(mv.cost_amount) FROM material_stock_movements mv
                       WHERE mv.company_id = d.company_id AND mv.document_no = d.doc_no
                         AND mv.movement_type IN ('SAL', 'POS', 'SRT') AND mv.is_deleted = 0
                   ), 0) AS Profit,
                   CASE WHEN SUM(l.line_total) = 0 THEN 0
                        ELSE ((CASE WHEN d.doc_kind = 'SRT' THEN -COALESCE(SUM(l.line_total), 0)
                                    ELSE COALESCE(SUM(l.line_total), 0) END)
                              + COALESCE((
                                  SELECT SUM(mv.cost_amount) FROM material_stock_movements mv
                                  WHERE mv.company_id = d.company_id AND mv.document_no = d.doc_no
                                    AND mv.movement_type IN ('SAL', 'POS', 'SRT') AND mv.is_deleted = 0
                              ), 0))
                             / (CASE WHEN d.doc_kind = 'SRT' THEN -SUM(l.line_total) ELSE SUM(l.line_total) END) END AS MarginPct
            FROM sales_documents d
            INNER JOIN sales_document_lines l ON l.doc_id = d.doc_id
            WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
              AND d.doc_kind IN ('SAL', 'POS', 'SRT') AND d.doc_date >= $from AND d.doc_date <= $to
            GROUP BY d.doc_id, d.doc_date, d.doc_no, d.party_name, d.doc_kind
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

    /// <summary>Per-terminal / per-cashier POS performance (sales
    /// cycle W9, migration 104): sales count and gross, returns,
    /// discounts given, paid-outs and average basket for the sessions
    /// in the period. The aggregation itself lives in PosRepository
    /// (the session world is keyed by session id, not by document
    /// columns); this is its RptQuery-shaped projection.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> PosPerformanceAsync(
        DateTime from, DateTime to, string? terminalCode, string? cashier, CancellationToken ct)
    {
        DocSchema.Ensure();
        var connection = DatabaseRuntime.GetConnectionString()
            ?? throw new AppError(ErrorCodes.DatabaseNotInitialized, "No database connection is configured for the POS performance report.");
        var repo = new ShouTech.Infrastructure.PosRepository(DatabaseRuntime.GetProvider(), connection);
        var rows = await repo.GetPerformanceAsync(Company, from, to, terminalCode, cashier, ct);
        return rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Terminal"] = r.TerminalCode,
            ["Cashier"] = r.Cashier,
            ["SalesCount"] = r.SalesCount,
            ["GrossSales"] = r.GrossSales,
            ["ReturnsCount"] = r.ReturnsCount,
            ["ReturnsTotal"] = r.ReturnsTotal,
            ["Discounts"] = r.DiscountsGiven,
            ["PaidOuts"] = r.PaidOuts,
            ["NetSales"] = r.NetSales,
            ["AvgBasket"] = r.AverageBasket
        }).ToList();
    }

    // ---------------- Customers ----------------

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CustomerDueAsync(
        CancellationToken ct)
    {
        // Written against party_ledger + the canonical parties master
        // (migration 061) — the only party store that exists now.
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(c.code, CAST(pl.party_id AS TEXT)) AS PartyCode,
                   COALESCE(NULLIF(c.name_en, ''), c.name_ar, '') AS PartyName,
                   COALESCE(c.phone, '') AS Phone,
                   COALESCE(SUM(COALESCE(pl.base_debit, pl.debit)), 0) AS Invoiced,
                   COALESCE(SUM(COALESCE(pl.base_credit, pl.credit)), 0) AS Collected,
                   COALESCE(SUM(COALESCE(pl.base_debit, pl.debit) - COALESCE(pl.base_credit, pl.credit)), 0) AS Balance,
                   MIN(pl.entry_date) AS OldestDate
            FROM party_ledger pl
            LEFT JOIN parties c ON c.ledger_id = pl.party_id AND c.company_id = pl.company_id AND c.kind = 'C'
            WHERE pl.company_id = $company AND pl.party_kind = 'C'
            GROUP BY pl.party_id, c.code, c.name_en, c.name_ar, c.phone
            HAVING COALESCE(SUM(COALESCE(pl.base_debit, pl.debit) - COALESCE(pl.base_credit, pl.credit)), 0) > 0.0001
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
                   COALESCE(NULLIF(c.name_en, ''), c.name_ar, t.owner_code) AS PartyName,
                   COALESCE(SUM(t.target_amount), 0) AS Target,
                   COALESCE((SELECT SUM(d.grand_total) FROM sales_documents d
                     WHERE d.company_id = $company AND d.is_deleted = 0 AND d.status = 'Posted'
                       AND d.doc_kind IN ('SAL', 'POS') AND d.party_code = t.owner_code
                       AND d.doc_date >= $from AND d.doc_date <= $to), 0) AS Actual
            FROM targets t
            LEFT JOIN parties c ON c.company_id = t.company_id AND c.kind = 'C' AND c.code = t.owner_code
            WHERE t.company_id = $company AND t.owner_kind = 'PARTY'
              AND t.period_start >= $from AND t.period_start <= $to
            GROUP BY t.owner_code, c.name_en, c.name_ar
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
        // Step 11 (sweep MFG-1 / MFG-4): re-sourced to the REAL migration
        // 034 schema. mfg_orders is keyed by `id` (there is no order_id
        // column) and mfg_order_lines carries NO company_id — company
        // scoping flows through the parent order. The pre-Step-11 SQL
        // joined o.order_id = l.order_id AND o.company_id = l.company_id
        // and filtered l.company_id, so every variance mode crashed on
        // open ("no such column").
        //
        // Mode semantics, against the kinds the domain actually allows
        // (MfgLineKinds: RAW and COST only — the old filters hunted
        // OVERHEAD/LABOR/FINISHED line kinds that can never exist):
        //   Total    — every order line (RAW + COST).
        //   Raw      — RAW lines only (materials consumed).
        //   Overhead — COST lines only (the additional-cost lines; the
        //              order's lump overhead_amount is a header figure
        //              with no standard, so it has no line variance).
        //   Finished — header-level per order: actual total_cost vs
        //              qty × standard_unit_cost (the MfgCosts figure),
        //              because finished goods are not order lines.
        // Variance = actual amount − qty × standard unit cost throughout.
        if (string.Equals(mode, "Finished", StringComparison.Ordinal))
        {
            const string finishedSql = """
                SELECT o.doc_no AS DocNo,
                       o.item_code AS ItemCode,
                       'FINISHED' AS LineKind,
                       COALESCE(o.qty, 0) AS Qty,
                       COALESCE(o.total_cost, 0) AS Amount,
                       COALESCE(o.qty, 0) * COALESCE(o.standard_unit_cost, 0) AS StandardAmount,
                       COALESCE(o.total_cost, 0) - COALESCE(o.qty, 0) * COALESCE(o.standard_unit_cost, 0) AS Variance
                FROM mfg_orders o
                WHERE o.company_id = $company
                ORDER BY o.doc_no;
                """;
            return await QueryAsync(finishedSql, ct, ("$company", Company));
        }

        var kindFilter = mode switch
        {
            "Raw" => "AND l.line_kind = 'RAW'",
            "Overhead" => "AND l.line_kind = 'COST'",
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
            INNER JOIN mfg_orders o ON o.id = l.order_id
            WHERE o.company_id = $company {kindFilter}
            GROUP BY o.doc_no, l.item_code, l.line_kind
            ORDER BY o.doc_no, l.line_kind, l.item_code;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> MfgFeasibilityAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure(); MatRepo.EnsureSchema();
        // Step 11 (sweep MFG-1): same 034 re-sourcing as MfgVarianceAsync
        // (join on o.id, company through the order, RAW lines only — the
        // only lines that consume stock). Required is restated in BASE
        // units below (line qty × unit equivalent, the StkPost rule),
        // because OnHand (material_stock_balances) is a base-unit figure;
        // comparing a display-unit requirement against it understated or
        // overstated shortages for every non-base-unit line.
        var sql = PgBoolFix("""
            SELECT o.doc_no AS DocNo,
                   o.item_code AS ItemCode,
                   l.item_code AS LineItemCode,
                   COALESCE(l.qty, 0) AS RequiredQty,
                   COALESCE(l.unit_code, '') AS Unit,
                   m2.material_id AS LineMaterialId,
                   COALESCE((SELECT SUM(b.available_qty) FROM material_stock_balances b
                     INNER JOIN materials m ON m.company_id = b.company_id AND m.material_id = b.material_id
                     WHERE b.company_id = o.company_id AND m.code = l.item_code AND b.is_deleted = 0), 0) AS OnHand
            FROM mfg_orders o
            INNER JOIN mfg_order_lines l ON l.order_id = o.id
            LEFT JOIN materials m2 ON m2.company_id = o.company_id AND m2.code = l.item_code AND m2.is_deleted = 0
            WHERE o.company_id = $company AND o.status = 'Draft'
              AND l.line_kind = 'RAW'
            ORDER BY o.doc_no, l.item_code;
            """);
        var raw = await QueryAsync(sql, ct, ("$company", Company));
        if (raw.Count == 0)
            return raw;

        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new AppError(ErrorCodes.DatabaseNotInitialized,
                "No database connection is configured for reports.");
        try
        {
            await using var connection = Open(cs);
            await connection.OpenAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var rows = new List<IReadOnlyDictionary<string, object?>>(raw.Count);
            foreach (var r in raw)
            {
                var requiredQty = RptWinUtil.Dec(r.GetValueOrDefault("RequiredQty"));
                var unit = Convert.ToString(r.GetValueOrDefault("Unit"), CultureInfo.InvariantCulture);
                var materialIdValue = r.GetValueOrDefault("LineMaterialId");
                var required = requiredQty;
                if (materialIdValue is not null && !string.IsNullOrWhiteSpace(unit))
                {
                    var materialId = Convert.ToInt64(materialIdValue, CultureInfo.InvariantCulture);
                    required = StkPost.ToBaseUnits(connection, tx, materialId, unit, requiredQty, 0m).BaseQty;
                }
                rows.Add(new Dictionary<string, object?>
                {
                    ["DocNo"] = r.GetValueOrDefault("DocNo"),
                    ["ItemCode"] = r.GetValueOrDefault("ItemCode"),
                    ["LineItemCode"] = r.GetValueOrDefault("LineItemCode"),
                    ["Required"] = required,
                    ["OnHand"] = r.GetValueOrDefault("OnHand")
                });
            }
            await tx.RollbackAsync(CancellationToken.None);
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppError)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "RptQuery.MfgFeasibilityAsync");
            throw new AppError(ErrorCodes.DatabaseQueryFailed,
                "A database operation failed.", ex);
        }
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
            INNER JOIN companies co ON co.company_code = b.company_id
            INNER JOIN chart_of_accounts a ON a.company_id = co.company_id AND a.account_code = b.gl_account_code
            LEFT JOIN (
                SELECT l.account_id AS aid, COALESCE(l.base_debit, l.debit) AS debit, COALESCE(l.base_credit, l.credit) AS credit
                FROM journal_entry_lines l
                INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                WHERE j.status = 1
            ) l ON l.aid = a.account_id
            WHERE co.company_code = $company AND a.is_deleted = 0
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
            FROM chart_of_accounts a
            INNER JOIN companies co ON co.company_id = a.company_id
            LEFT JOIN (
                SELECT l.account_id AS aid, COALESCE(l.base_debit, l.debit) AS debit, COALESCE(l.base_credit, l.credit) AS credit
                FROM journal_entry_lines l
                INNER JOIN journal_entries j ON j.journal_entry_id = l.journal_entry_id
                WHERE j.status = 1
            ) l ON l.aid = a.account_id
            WHERE co.company_code = $company AND a.is_deleted = 0 AND {BankAccountSql}
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


    // ==================================================================
    // Sales cycle upgrade B5 (G22): CRM funnel + sales-chain exception
    // reports. All read-only, company-scoped and parameterized.
    // CRM tables key company_id by company CODE (migration 057 note);
    // sales_documents rows may carry EITHER the code or the company Guid
    // (the chain repositories require the Guid key — Fix Phase Step 9),
    // so document queries resolve the company through the companies
    // registry accepting both keys, exactly like PurchaseInvoiceList.
    // ==================================================================

    /// <summary>Open pipeline per stage: opportunity count, raw expected
    /// total and probability-weighted total (expected_amount x the
    /// opportunity's own probability %, the CrmOpportunity.WeightedValue
    /// rule). Only Open stages: Won/Lost stages are outcomes, not
    /// pipeline. Stages with no opportunities still appear (LEFT JOIN).</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CrmPipelineByStageAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT COALESCE(NULLIF(s.name_en, ''), s.name_ar, '') AS Stage,
                   COALESCE(s.name_ar, '') AS StageAr,
                   COUNT(o.opp_id) AS OppCount,
                   COALESCE(SUM(COALESCE(o.expected_amount, 0)), 0) AS RawTotal,
                   COALESCE(SUM(COALESCE(o.expected_amount, 0) * COALESCE(o.probability, 0) / 100.0), 0) AS WeightedTotal
            FROM crm_stages s
            LEFT JOIN crm_opportunities o
              ON o.company_id = s.company_id AND o.stage_id = s.stage_id AND o.is_deleted = 0
            WHERE s.company_id = $company AND s.is_deleted = 0 AND s.is_active = 1 AND s.stage_type = 'Open'
            GROUP BY s.stage_id, s.name_en, s.name_ar, s.sequence
            ORDER BY s.sequence;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    /// <summary>Pipeline forecast (W10, sales Level 3): OPEN
    /// opportunities grouped by expected close month and owner rep,
    /// weighted by the STAGE probability (crm_stages.probability,
    /// migration 057 — the stage's own forecast weight, distinct from
    /// the per-opportunity override the funnel's Pipeline section
    /// uses). Opportunities without an expected close date cannot be
    /// placed in a month and are excluded rather than guessed.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CrmForecastByMonthAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT substr(o.expected_close_date, 1, 7) AS CloseMonth,
                   COALESCE(NULLIF(o.assigned_rep_code, ''), '') AS Rep,
                   COUNT(o.opp_id) AS OppCount,
                   COALESCE(SUM(COALESCE(o.expected_amount, 0)), 0) AS RawTotal,
                   COALESCE(SUM(COALESCE(o.expected_amount, 0) * COALESCE(s.probability, 0) / 100.0), 0) AS WeightedTotal
            FROM crm_opportunities o
            INNER JOIN crm_stages s
              ON s.company_id = o.company_id AND s.stage_id = o.stage_id AND s.is_deleted = 0 AND s.stage_type = 'Open'
            WHERE o.company_id = $company AND o.is_deleted = 0
              AND COALESCE(o.expected_close_date, '') <> ''
            GROUP BY substr(o.expected_close_date, 1, 7), o.assigned_rep_code
            ORDER BY CloseMonth, Rep;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    /// <summary>Closed outcomes per sales rep over [from, to]: opportunities
    /// whose CURRENT stage is Won/Lost and whose last update (stage moves
    /// stamp updated_at in CrmRepository.MoveStageAsync) falls in the
    /// period. The window derives the win rate won / (won + lost).</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CrmWinRateByRepAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT COALESCE(NULLIF(o.assigned_rep_code, ''), '') AS Rep,
                   SUM(CASE WHEN s.stage_type = 'Won' THEN 1 ELSE 0 END) AS Won,
                   SUM(CASE WHEN s.stage_type = 'Lost' THEN 1 ELSE 0 END) AS Lost,
                   COALESCE(SUM(COALESCE(o.expected_amount, 0)), 0) AS ClosedAmount
            FROM crm_opportunities o
            INNER JOIN crm_stages s
              ON s.company_id = o.company_id AND s.stage_id = o.stage_id AND s.is_deleted = 0
            WHERE o.company_id = $company AND o.is_deleted = 0
              AND s.stage_type IN ('Won', 'Lost')
              AND COALESCE(o.updated_at, o.created_at) >= $from
              AND COALESCE(o.updated_at, o.created_at) < $toEx
            GROUP BY o.assigned_rep_code
            ORDER BY Rep;
            """);
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", DateStart(from)),
            ("$toEx", DateEndExclusive(to)));
    }

    /// <summary>Every OPEN opportunity with its last touch: the latest
    /// activity timestamp recorded against it (crm_activities, any
    /// status — a scheduled or completed follow-up both count as
    /// activity), falling back to the opportunity's own updated_at /
    /// created_at when it has no activities. The window applies the
    /// stale-days threshold so the day math stays provider-neutral.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CrmOpenOpportunitiesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        var sql = PgBoolFix("""
            SELECT o.title AS Title,
                   COALESCE(NULLIF(p.name_en, ''), p.name_ar, '') AS PartyName,
                   COALESCE(p.name_ar, '') AS PartyNameAr,
                   COALESCE(o.expected_amount, 0) AS Amount,
                   o.expected_close_date AS ExpectedClose,
                   act.LastActivity AS LastActivity,
                   COALESCE(o.updated_at, o.created_at) AS UpdatedAt
            FROM crm_opportunities o
            INNER JOIN crm_stages s
              ON s.company_id = o.company_id AND s.stage_id = o.stage_id AND s.stage_type = 'Open'
            LEFT JOIN parties p
              ON p.id = o.party_id AND p.company_id = o.company_id AND p.is_deleted = 0
            LEFT JOIN (
                SELECT opp_id, MAX(COALESCE(completed_at, created_at)) AS LastActivity
                FROM crm_activities
                WHERE company_id = $company AND is_deleted = 0 AND opp_id IS NOT NULL
                GROUP BY opp_id
            ) act ON act.opp_id = o.opp_id
            WHERE o.company_id = $company AND o.is_deleted = 0
            ORDER BY o.expected_close_date, o.title;
            """);
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    /// <summary>Lost and expired quotations in [from, to]: QUO documents
    /// marked Lost (DocSearchWin SetLost) or Expired, plus Posted
    /// quotations whose valid_till is already past (they read as expired
    /// even before any status sweep flips them).</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> CrmLostExpiredQuotationsAsync(
        DateTime from, DateTime to, DateTime today, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_no AS DocNo,
                   d.doc_date AS DocDate,
                   COALESCE(NULLIF(d.party_name, ''), '') AS PartyName,
                   COALESCE(d.grand_total, 0) AS Total,
                   COALESCE(d.status, '') AS Status,
                   d.valid_till AS ValidTill
            FROM sales_documents d
            INNER JOIN companies co
              ON co.company_code = $company
             AND (CAST(co.company_id AS TEXT) = CAST(d.company_id AS TEXT)
                  OR d.company_id = co.company_code)
            WHERE d.doc_kind = 'QUO'
              AND CAST(d.is_deleted AS TEXT) IN ('0', 'false')
              AND d.doc_date >= $from AND d.doc_date <= $to
              AND (d.status IN ('Lost', 'Expired')
                   OR (d.status = 'Posted'
                       AND COALESCE(d.valid_till, '') <> ''
                       AND d.valid_till < $today))
            ORDER BY d.doc_date DESC, d.doc_no DESC;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", FmtDate(from)),
            ("$to", FmtDate(to)),
            ("$today", FmtDate(today)));
    }

    /// <summary>Lines of every Posted sales order of the current company.
    /// The exceptions window allocates the linked delivered quantities
    /// against these lines with the same material-key, line-order
    /// consumption OrderDocumentRepository.GetProgressAsync uses.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SalesOrderLinesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_id AS DocId,
                   d.doc_no AS DocNo,
                   d.doc_date AS DocDate,
                   COALESCE(NULLIF(d.party_name, ''), '') AS PartyName,
                   d.delivery_date AS DeliveryDate,
                   l.line_no AS LineNo,
                   l.material_id AS MaterialId,
                   COALESCE(l.qty, 0) AS Qty
            FROM sales_documents d
            INNER JOIN companies co
              ON co.company_code = $company
             AND (CAST(co.company_id AS TEXT) = CAST(d.company_id AS TEXT)
                  OR d.company_id = co.company_code)
            INNER JOIN sales_document_lines l ON l.doc_id = d.doc_id
            WHERE d.doc_kind = 'SO' AND d.status = 'Posted'
              AND CAST(d.is_deleted AS TEXT) IN ('0', 'false')
            ORDER BY d.doc_date, d.doc_no, l.line_no;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    /// <summary>Lines of every Posted delivery note of the current
    /// company, with line totals so the window can value the uninvoiced
    /// remainder at the delivery lines' own prices.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SalesDeliveryLinesAsync(
        CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT d.doc_id AS DocId,
                   d.doc_no AS DocNo,
                   d.doc_date AS DocDate,
                   COALESCE(NULLIF(d.party_name, ''), '') AS PartyName,
                   l.line_no AS LineNo,
                   l.material_id AS MaterialId,
                   COALESCE(l.qty, 0) AS Qty,
                   COALESCE(l.line_total, 0) AS LineTotal,
                   COALESCE(d.delivery_cost, 0) AS DeliveryCost
            FROM sales_documents d
            INNER JOIN companies co
              ON co.company_code = $company
             AND (CAST(co.company_id AS TEXT) = CAST(d.company_id AS TEXT)
                  OR d.company_id = co.company_code)
            INNER JOIN sales_document_lines l ON l.doc_id = d.doc_id
            WHERE d.doc_kind = 'DLV' AND d.status = 'Posted'
              AND CAST(d.is_deleted AS TEXT) IN ('0', 'false')
            ORDER BY d.doc_date, d.doc_no, l.line_no;
            """;
        return await QueryAsync(sql, ct, ("$company", Company));
    }

    /// <summary>Quantities carried from Posted chain documents into a
    /// target kind, aggregated per source document + target line
    /// (material_id, line_no) — the raw shape of
    /// OrderDocumentRepository.LinkedQuantitiesAsync, set-based over
    /// the whole company instead of per document. Used with
    /// (SO -&gt; DLV) for delivered quantities and (DLV -&gt; SAL) for
    /// invoiced quantities.</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SalesLinkedQuantitiesAsync(
        string sourceKind, string targetKind, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT dl.source_doc_id AS SourceId,
                   l.material_id AS MaterialId,
                   l.line_no AS LineNo,
                   COALESCE(SUM(l.qty), 0) AS Qty
            FROM document_links dl
            INNER JOIN companies co
              ON co.company_code = $company
             AND (CAST(co.company_id AS TEXT) = CAST(dl.company_id AS TEXT)
                  OR dl.company_id = co.company_code)
            INNER JOIN sales_documents td
              ON td.company_id = dl.company_id
             AND td.doc_id = dl.target_doc_id
             AND td.doc_kind = dl.target_doc_kind
             AND td.status = 'Posted'
             AND CAST(td.is_deleted AS TEXT) IN ('0', 'false')
            INNER JOIN sales_document_lines l ON l.doc_id = td.doc_id
            WHERE dl.source_doc_kind = $sk AND dl.target_doc_kind = $tk
            GROUP BY dl.source_doc_id, l.material_id, l.line_no;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$sk", sourceKind),
            ("$tk", targetKind));
    }

    /// <summary>Delivery-desk requests of the current company in the
    /// date range (sales cycle W4): one row per request with its driver,
    /// status, request date, proof-of-delivery timestamp, and the linked
    /// delivery note's recorded delivery cost. The exceptions window
    /// aggregates these per driver into the Delivery Performance
    /// section. delivery_requests.company_id carries the company CODE
    /// (the definitions family's convention).</summary>
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> DeliveryPerformanceAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        DocSchema.Ensure();
        const string sql = """
            SELECT COALESCE(NULLIF(r.driver_code, ''), '') AS DriverCode,
                   COALESCE(r.status, '') AS Status,
                   r.request_date AS RequestDate,
                   r.delivered_at AS DeliveredAt,
                   COALESCE(d.delivery_cost, 0) AS DeliveryCost
            FROM delivery_requests r
            LEFT JOIN companies co
              ON co.company_code = $company
            LEFT JOIN sales_documents d
              ON d.doc_id = r.sales_doc_id
             AND (CAST(co.company_id AS TEXT) = CAST(d.company_id AS TEXT)
                  OR d.company_id = co.company_code)
            WHERE r.company_id = $company
              AND CAST(r.is_deleted AS TEXT) IN ('0', 'false')
              AND r.request_date >= $from AND r.request_date <= $to
            ORDER BY r.driver_code, r.request_date;
            """;
        return await QueryAsync(sql, ct,
            ("$company", Company),
            ("$from", FmtDate(from)),
            ("$to", FmtDate(to)));
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        var cs = DatabaseRuntime.GetConnectionString();
        if (string.IsNullOrWhiteSpace(cs))
            throw new AppError(ErrorCodes.DatabaseNotInitialized,
                "No database connection is configured for reports.");

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
                // PostgreSQL folds unquoted SQL aliases to lowercase; report column keys use PascalCase.
                // A case-insensitive row map keeps the same query contract on SQLite and PostgreSQL.
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                result.Add(row);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sweep RC-5: this catch used to return an empty result set
            // for EVERY failure, so any SQL/schema/provider error
            // rendered as "0 rows" with a normal status line. Now the
            // failure is reported centrally (error catalog + LogServ via
            // AppHost.Errors) and thrown as a cataloged AppError; the
            // report window surfaces it instead of faking emptiness.
            AppHost.Errors.Report(ErrorCodes.DatabaseQueryFailed, ex, "RptQuery.QueryAsync");
            throw new AppError(ErrorCodes.DatabaseQueryFailed,
                "A database operation failed.", ex);
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
