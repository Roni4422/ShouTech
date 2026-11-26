-- ============================================================================
-- 094 - Stock Delivered But Not Billed account (SQLite)
--
-- Sales cycle upgrade B4 (docs/SALES_CYCLE_UPGRADE.md, gap G12): under
-- perpetual inventory a posted delivery note now carries its own GL entry
--   Dr 1215 Stock Delivered But Not Billed / Cr 1203 Merchandise Inventory
-- and a delivery-linked sales invoice releases COGS against 1215
-- (Dr 401 / Cr 1215) instead of crediting inventory a second time. The
-- delivered-but-unbilled position is therefore visible on the balance
-- sheet between delivery and billing.
--
-- CODE CHOICE: the upgrade plan named this account 1204, but 1204 is
-- already "Prepaid Expenses" in every seeded COA template and
-- chart_of_accounts enforces UNIQUE(company_id, account_code) — reusing
-- 1204 would have posted delivery cost into Prepaid Expenses. 1215 is
-- the first free code in the inventory family (1210 Raw Materials,
-- 1211 WIP, 1212 Finished Goods, 1213 Spare Parts) and is used instead;
-- GlAccounts.StockDeliveredNotBilled carries the same code.
--
-- This migration adds 1215 to every EXISTING company that already has a
-- chart of accounts and lacks the code (the 058 manufacturing pattern:
-- templates carry the account for new companies, posting code resolves
-- it with TryResolveAccount and falls back to Inventory 1203 on charts
-- that have no postable 1215, so a chart this migration cannot reach
-- never breaks). The parent is the company's own group account '12'
-- (Current Assets) when present.
--
-- Idempotent: the checksum ledger skips a re-run; inside the file the
-- INSERT is guarded by NOT EXISTS on (company_id, '1215').
-- ============================================================================

INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '1215',
       'Stock Delivered But Not Billed',
       'بضاعة مسلّمة غير مفوترة',
       'ASSET',
       (SELECT g.account_id FROM chart_of_accounts g
         WHERE g.company_id = c.company_id AND g.account_code = '12'
         LIMIT 1),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts x WHERE x.company_id = c.company_id)
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts x
                  WHERE x.company_id = c.company_id AND x.account_code = '1215');
