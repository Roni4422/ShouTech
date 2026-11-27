-- ============================================================================
-- 059 - Chart-of-accounts unification (SQLite)
--
-- Fix Phase Step 2 (sweep defects J-1/C-1, C-2, C-3, R-1, R-2): the product
-- had TWO charts of accounts with no sync:
--   * accounts          (migration 012): INTEGER id, company_id = company
--     CODE, written by company creation / CoaWin / AccCard, with cached
--     balance/debit/credit columns nothing maintained.
--   * chart_of_accounts (migration 009): Guid id, company_id = company Guid,
--     read by JournalRepository, the posting triggers, ChequeRepository and
--     MasterCardRepository; journal_entry_lines.account_id points HERE.
--
-- chart_of_accounts is the canonical store from this migration on:
--   * posted history (journal_entry_lines) already references its Guids, so
--     unifying here rewrites NO journal history;
--   * it carries the disciplined schema (account_type, boolean flags,
--     foreign keys to companies and to its own parent);
--   * the UI-only attributes of accounts are carried over as extension
--     columns below, so no card field is lost.
--
-- Data mapping (explicit):
--   * company: accounts.company_id holds the company CODE; the canonical
--     company key is companies.company_id (Guid), resolved through the
--     companies row (company_code match, or an already-Guid company_id).
--     Accounts rows whose company cannot be resolved belong to no company
--     and are NOT migrated (the journal could never reference them).
--   * identity: rows already in chart_of_accounts KEEP their account_id, so
--     every existing journal_entry_lines reference keeps pointing at the
--     same account. Rows that exist only in accounts receive a fresh Guid.
--   * conflicts (same company + code in both stores): the chart row keeps
--     its identity and account_type; descriptive fields, flags, posting
--     mode and status are taken from accounts (the store the user actually
--     maintained through the UI); is_deleted is set if either store had it.
--   * account_type for accounts-only rows is derived from the code root,
--     matching the seed chart's numbering (1 Assets, 2 Liabilities,
--     3 Equity, 5 Revenue; 4 Purchases and 6 Expenses are EXPENSE-nature).
--   * parent: parent_account_id is resolved from accounts.parent_code
--     AFTER all rows exist, so insert order cannot break the link.
--   * the cached accounts.balance/debit/credit values are NOT carried over:
--     they were never maintained (C-2). Balances are computed from posted
--     journal_entry_lines from now on.
--
-- The legacy accounts table is dropped at the end: exactly one writer path
-- and one reader path remain. Fiscal periods are backfilled for fiscal
-- years that have none (009 only seeded years existing at ITS run time),
-- so a company created by the app can actually post a journal.
-- ============================================================================

-- ---- Canonical store: UI extension columns --------------------------------
-- @if-column-missing chart_of_accounts.name_ar
ALTER TABLE chart_of_accounts ADD COLUMN name_ar TEXT;
-- @if-column-missing chart_of_accounts.risk
ALTER TABLE chart_of_accounts ADD COLUMN risk TEXT NOT NULL DEFAULT 'Low';
-- @if-column-missing chart_of_accounts.cost_center_mode
ALTER TABLE chart_of_accounts ADD COLUMN cost_center_mode TEXT NOT NULL DEFAULT 'Unspecified';
-- @if-column-missing chart_of_accounts.allow_reconcile
ALTER TABLE chart_of_accounts ADD COLUMN allow_reconcile INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.taxable
ALTER TABLE chart_of_accounts ADD COLUMN taxable INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.foreign_currency
ALTER TABLE chart_of_accounts ADD COLUMN foreign_currency INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.cost_required
ALTER TABLE chart_of_accounts ADD COLUMN cost_required INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.budget
ALTER TABLE chart_of_accounts ADD COLUMN budget INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.is_default
ALTER TABLE chart_of_accounts ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.tax_rate
ALTER TABLE chart_of_accounts ADD COLUMN tax_rate NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.description
ALTER TABLE chart_of_accounts ADD COLUMN description TEXT NOT NULL DEFAULT '';
-- @if-column-missing chart_of_accounts.updated_at
ALTER TABLE chart_of_accounts ADD COLUMN updated_at TEXT;
-- @if-column-missing chart_of_accounts.deleted_at
ALTER TABLE chart_of_accounts ADD COLUMN deleted_at TEXT;
-- @if-column-missing chart_of_accounts.deleted_by
ALTER TABLE chart_of_accounts ADD COLUMN deleted_by TEXT;

-- Existing canonical rows: the single account_name is the best Arabic-name
-- source available until a user edits the card.
UPDATE chart_of_accounts SET name_ar = account_name WHERE name_ar IS NULL OR name_ar = '';

-- ---- Merge: enrich canonical rows that also exist in accounts -------------
UPDATE chart_of_accounts
SET name_ar = (SELECT a.name_ar FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    account_name = COALESCE((SELECT NULLIF(a.name_en, '') FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id), account_name),
    is_postable = (SELECT CASE WHEN a.posting_mode = 'Summary' THEN 0 ELSE 1 END FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    is_active = (SELECT CASE WHEN a.status = 'Disabled' THEN 0 ELSE 1 END FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    is_deleted = CASE WHEN EXISTS (SELECT 1 FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code AND a.is_deleted = 1
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id) THEN 1 ELSE is_deleted END,
    risk = (SELECT a.risk FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    cost_center_mode = (SELECT a.cost_center FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    allow_reconcile = (SELECT a.allow_reconcile FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    taxable = (SELECT a.taxable FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    foreign_currency = (SELECT a.foreign_currency FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    cost_required = (SELECT a.cost_required FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    budget = (SELECT a.budget FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    is_default = (SELECT a.is_default FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    tax_rate = (SELECT a.tax_rate FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    description = (SELECT CASE WHEN COALESCE(a.description, '') <> '' THEN a.description ELSE chart_of_accounts.description END FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    updated_at = (SELECT a.updated_at FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    deleted_at = (SELECT a.deleted_at FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id),
    deleted_by = (SELECT a.deleted_by FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id)
WHERE EXISTS (SELECT 1 FROM accounts a
               WHERE a.account_code = chart_of_accounts.account_code
                 AND (SELECT c.company_id FROM companies c
                      WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                      LIMIT 1) = chart_of_accounts.company_id);

-- ---- Merge: insert accounts-only rows into the canonical store ------------
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted,
     risk, cost_center_mode, allow_reconcile, taxable, foreign_currency,
     cost_required, budget, is_default, tax_rate, description,
     created_at, updated_at, deleted_at, deleted_by)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       (SELECT c.company_id FROM companies c
        WHERE c.company_code = a.company_id OR c.company_id = a.company_id LIMIT 1),
       a.account_code,
       COALESCE(NULLIF(a.name_en, ''), a.name_ar),
       a.name_ar,
       CASE substr(a.account_code, 1, 1)
           WHEN '1' THEN 'ASSET'
           WHEN '2' THEN 'LIABILITY'
           WHEN '3' THEN 'EQUITY'
           WHEN '5' THEN 'REVENUE'
           ELSE 'EXPENSE'
       END,
       NULL,
       CASE WHEN a.posting_mode = 'Summary' THEN 0 ELSE 1 END,
       CASE WHEN a.status = 'Disabled' THEN 0 ELSE 1 END,
       a.is_deleted,
       a.risk, a.cost_center, a.allow_reconcile, a.taxable, a.foreign_currency,
       a.cost_required, a.budget, a.is_default, a.tax_rate, a.description,
       a.created_at, a.updated_at, a.deleted_at, a.deleted_by
FROM accounts a
WHERE (SELECT c.company_id FROM companies c
       WHERE c.company_code = a.company_id OR c.company_id = a.company_id LIMIT 1) IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts ch
                  WHERE ch.company_id = (SELECT c.company_id FROM companies c
                        WHERE c.company_code = a.company_id OR c.company_id = a.company_id LIMIT 1)
                    AND ch.account_code = a.account_code);

-- ---- Merge: resolve parents from accounts.parent_code ----------------------
UPDATE chart_of_accounts
SET parent_account_id = (
    SELECT p.account_id
    FROM accounts a
    JOIN chart_of_accounts p
      ON p.company_id = chart_of_accounts.company_id AND p.account_code = a.parent_code
    WHERE a.account_code = chart_of_accounts.account_code
      AND (SELECT c.company_id FROM companies c
           WHERE c.company_code = a.company_id OR c.company_id = a.company_id
           LIMIT 1) = chart_of_accounts.company_id
    LIMIT 1)
WHERE parent_account_id IS NULL
  AND EXISTS (SELECT 1 FROM accounts a
              WHERE a.account_code = chart_of_accounts.account_code
                AND a.parent_code IS NOT NULL AND a.parent_code <> ''
                AND (SELECT c.company_id FROM companies c
                     WHERE c.company_code = a.company_id OR c.company_id = a.company_id
                     LIMIT 1) = chart_of_accounts.company_id);

-- ---- Fiscal periods backfill (years with no periods cannot post) -----------
INSERT OR IGNORE INTO fiscal_periods
    (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       fy.company_id, fy.fiscal_year_id, m.n + 1,
       strftime('%Y-%m', date(fy.start_date, printf('+%d months', m.n))),
       date(fy.start_date, printf('+%d months', m.n)),
       MIN(date(fy.start_date, printf('+%d months', m.n), '+1 month', '-1 day'), fy.end_date),
       'OPEN'
FROM fiscal_years fy
JOIN (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3
      UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7
      UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11
      UNION ALL SELECT 12) m
WHERE fy.is_closed = 0
  AND date(fy.start_date, printf('+%d months', m.n)) <= fy.end_date
  AND NOT EXISTS (SELECT 1 FROM fiscal_periods fp
                  WHERE fp.company_id = fy.company_id AND fp.fiscal_year_id = fy.fiscal_year_id);

-- ---- One canonical store: the legacy table goes away -----------------------
DROP TABLE IF EXISTS accounts;
