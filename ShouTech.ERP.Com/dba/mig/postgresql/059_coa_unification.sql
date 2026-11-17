-- ============================================================================
-- 059 - Chart-of-accounts unification (PostgreSQL)
--
-- PostgreSQL counterpart of sqlite/059_coa_unification.sql (Fix Phase
-- Step 2; sweep defects J-1/C-1, C-2, C-3, R-1, R-2). chart_of_accounts is
-- the canonical store: journal_entry_lines.account_id already references
-- its uuids, so no posted history is rewritten. The UI attributes of the
-- legacy accounts table are carried over as extension columns; accounts
-- rows are merged in (company CODE resolved to the company uuid through
-- companies), accounts-only rows receive deterministic uuids derived from
-- (company, code) so a retried migration can never duplicate them, and the
-- legacy table is dropped. Cached accounts.balance/debit/credit values are
-- deliberately NOT carried over (never maintained, defect C-2); balances
-- are computed from posted journal_entry_lines from now on. Fiscal periods
-- are backfilled for fiscal years that have none.
-- ============================================================================

-- ---- Canonical store: UI extension columns --------------------------------
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS name_ar TEXT;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS risk TEXT NOT NULL DEFAULT 'Low';
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS cost_center_mode TEXT NOT NULL DEFAULT 'Unspecified';
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS allow_reconcile BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS taxable BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS foreign_currency BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS cost_required BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS budget BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS is_default BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS tax_rate NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS description TEXT NOT NULL DEFAULT '';
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE chart_of_accounts ADD COLUMN IF NOT EXISTS deleted_by TEXT;

UPDATE chart_of_accounts SET name_ar = account_name WHERE name_ar IS NULL OR name_ar = '';

-- ---- Merge: enrich canonical rows that also exist in accounts -------------
UPDATE chart_of_accounts ch
SET name_ar = a.name_ar,
    account_name = COALESCE(NULLIF(a.name_en, ''), ch.account_name),
    is_postable = (a.posting_mode <> 'Summary'),
    is_active = (a.status <> 'Disabled'),
    is_deleted = (ch.is_deleted OR a.is_deleted),
    risk = a.risk,
    cost_center_mode = a.cost_center,
    allow_reconcile = a.allow_reconcile,
    taxable = a.taxable,
    foreign_currency = a.foreign_currency,
    cost_required = a.cost_required,
    budget = a.budget,
    is_default = a.is_default,
    tax_rate = a.tax_rate,
    description = CASE WHEN COALESCE(a.description, '') <> '' THEN a.description ELSE ch.description END,
    updated_at = a.updated_at,
    deleted_at = a.deleted_at,
    deleted_by = a.deleted_by
FROM accounts a
CROSS JOIN LATERAL (
    SELECT c.company_id FROM companies c
    WHERE c.company_code = a.company_id OR c.company_id::text = a.company_id
    LIMIT 1
) rc
WHERE ch.company_id = rc.company_id AND ch.account_code = a.account_code;

-- ---- Merge: insert accounts-only rows into the canonical store ------------
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted,
     risk, cost_center_mode, allow_reconcile, taxable, foreign_currency,
     cost_required, budget, is_default, tax_rate, description,
     created_at, updated_at, deleted_at, deleted_by)
SELECT md5(rc.company_id::text || ':059:' || a.account_code)::uuid,
       rc.company_id,
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
       (a.posting_mode <> 'Summary'),
       (a.status <> 'Disabled'),
       a.is_deleted,
       a.risk, a.cost_center, a.allow_reconcile, a.taxable, a.foreign_currency,
       a.cost_required, a.budget, a.is_default, a.tax_rate, a.description,
       a.created_at, a.updated_at, a.deleted_at, a.deleted_by
FROM accounts a
CROSS JOIN LATERAL (
    SELECT c.company_id FROM companies c
    WHERE c.company_code = a.company_id OR c.company_id::text = a.company_id
    LIMIT 1
) rc
ON CONFLICT (company_id, account_code) DO NOTHING;

-- ---- Merge: resolve parents from accounts.parent_code ----------------------
UPDATE chart_of_accounts ch
SET parent_account_id = p.account_id
FROM accounts a
CROSS JOIN LATERAL (
    SELECT c.company_id FROM companies c
    WHERE c.company_code = a.company_id OR c.company_id::text = a.company_id
    LIMIT 1
) rc
JOIN chart_of_accounts p
  ON p.company_id = rc.company_id AND p.account_code = a.parent_code
WHERE ch.company_id = rc.company_id
  AND ch.account_code = a.account_code
  AND ch.parent_account_id IS NULL
  AND a.parent_code IS NOT NULL AND a.parent_code <> '';

-- ---- Fiscal periods backfill (years with no periods cannot post) -----------
INSERT INTO fiscal_periods
    (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status)
SELECT md5(fy.company_id::text || ':059:' || fy.fiscal_year_id::text || ':' || g.n::text)::uuid,
       fy.company_id, fy.fiscal_year_id, g.n + 1,
       to_char((fy.start_date + (g.n || ' months')::interval)::date, 'YYYY-MM'),
       (fy.start_date + (g.n || ' months')::interval)::date,
       LEAST((fy.start_date + ((g.n + 1) || ' months')::interval - interval '1 day')::date, fy.end_date),
       'OPEN'
FROM fiscal_years fy
CROSS JOIN generate_series(0, 12) AS g(n)
WHERE NOT fy.is_closed
  AND (fy.start_date + (g.n || ' months')::interval)::date <= fy.end_date
  AND NOT EXISTS (SELECT 1 FROM fiscal_periods fp
                  WHERE fp.company_id = fy.company_id AND fp.fiscal_year_id = fy.fiscal_year_id)
ON CONFLICT DO NOTHING;

-- ---- One canonical store: the legacy table goes away -----------------------
DROP TABLE IF EXISTS accounts;
