-- ============================================================================
-- 030 - English-only seed cleanup (SQLite)
--
-- Applied migrations are immutable (SHA-256 checksums are enforced by the
-- migrator), so the Arabic seed values written by 004/012/028 are normalized
-- here instead of editing those files:
--   * permission_groups.name_ar and permissions.name_ar now carry the
--     canonical English text (the name_ar columns are kept, not dropped).
--   * permissions.description: the seeded Arabic generic text becomes
--     "Operational permission within the <Group> section".
--   * accounts: Arabic enum-like stored values (posting_mode, status, risk,
--     cost_center) become the canonical English tokens the application uses,
--     and the column defaults change to English via a table rebuild.
--   * Legacy Arabic stock movement type values become "Opening Stock" (the
--     export classification no longer matches Arabic text).
-- The legacy Arabic literals are written as Unicode code points (char() /
-- U&'...') so this file itself stays English-only. User-entered data (e.g.
-- accounts.name_ar values typed by users) is not touched.
-- ============================================================================

-- ---- Permission catalog -----------------------------------------------------
UPDATE permission_groups SET name_ar = name_en;

UPDATE permissions SET name_ar = name_en;

UPDATE permissions
SET description = 'Operational permission within the ' ||
    COALESCE((SELECT name_en FROM permission_groups
              WHERE permission_groups.group_code = permissions.group_code), 'System') ||
    ' section'
WHERE description LIKE char(1589,1604,1575,1581,1610,1577,32,1578,1588,1594,1610,1604,1610,1577,32,1590,1605,1606,32,1602,1587,1605,32) || '%';

UPDATE permissions
SET description = 'Manage user permissions'
WHERE permission_code = 'SYS.PERMISSIONS.MANAGE';

-- ---- Accounts: normalize stored enum values ---------------------------------
UPDATE accounts SET posting_mode = 'Postable'    WHERE posting_mode = char(1602,1575,1576,1604,32,1604,1604,1578,1585,1581,1610,1604);
UPDATE accounts SET posting_mode = 'Summary'     WHERE posting_mode = char(1578,1580,1605,1610,1593,1610);
UPDATE accounts SET status       = 'Active'      WHERE status = char(1601,1593,1575,1604);
UPDATE accounts SET status       = 'Disabled'    WHERE status = char(1605,1593,1591,1604);
UPDATE accounts SET risk         = 'Low'         WHERE risk = char(1605,1606,1582,1601,1590);
UPDATE accounts SET risk         = 'Medium'      WHERE risk = char(1605,1578,1608,1587,1591);
UPDATE accounts SET risk         = 'High'        WHERE risk = char(1593,1575,1604,1610);
UPDATE accounts SET cost_center  = 'Unspecified' WHERE cost_center = char(1594,1610,1585,32,1605,1581,1583,1583);
UPDATE accounts SET cost_center  = 'Required'    WHERE cost_center = char(1573,1604,1586,1575,1605,1610);
UPDATE accounts SET cost_center  = 'Optional'    WHERE cost_center = char(1575,1582,1578,1610,1575,1585,1610);

-- ---- Accounts: English column defaults (SQLite table rebuild) ---------------
CREATE TABLE accounts_new (
    account_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL DEFAULT '',
    account_code TEXT NOT NULL,
    parent_code TEXT,
    name_ar TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    posting_mode TEXT NOT NULL DEFAULT 'Postable',
    status TEXT NOT NULL DEFAULT 'Active',
    risk TEXT NOT NULL DEFAULT 'Low',
    cost_center TEXT NOT NULL DEFAULT 'Unspecified',
    allow_reconcile INTEGER NOT NULL DEFAULT 0,
    taxable INTEGER NOT NULL DEFAULT 0,
    foreign_currency INTEGER NOT NULL DEFAULT 0,
    cost_required INTEGER NOT NULL DEFAULT 0,
    budget INTEGER NOT NULL DEFAULT 0,
    is_default INTEGER NOT NULL DEFAULT 0,
    tax_rate NUMERIC NOT NULL DEFAULT 0,
    description TEXT NOT NULL DEFAULT '',
    balance NUMERIC NOT NULL DEFAULT 0,
    debit NUMERIC NOT NULL DEFAULT 0,
    credit NUMERIC NOT NULL DEFAULT 0,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT,
    deleted_at TEXT,
    deleted_by TEXT,
    UNIQUE(company_id, account_code)
);

INSERT INTO accounts_new (
    account_id, company_id, account_code, parent_code, name_ar, name_en,
    posting_mode, status, risk, cost_center, allow_reconcile, taxable,
    foreign_currency, cost_required, budget, is_default, tax_rate, description,
    balance, debit, credit, is_deleted, created_at, updated_at, deleted_at, deleted_by)
SELECT
    account_id, company_id, account_code, parent_code, name_ar, name_en,
    posting_mode, status, risk, cost_center, allow_reconcile, taxable,
    foreign_currency, cost_required, budget, is_default, tax_rate, description,
    balance, debit, credit, is_deleted, created_at, updated_at, deleted_at, deleted_by
FROM accounts;

DROP TABLE accounts;
ALTER TABLE accounts_new RENAME TO accounts;

CREATE INDEX IF NOT EXISTS ix_accounts_parent ON accounts(company_id, parent_code, account_code);
CREATE INDEX IF NOT EXISTS ix_accounts_code ON accounts(company_id, account_code);

-- ---- Legacy stock movement types ---------------------------------------------
UPDATE stock_movements SET movement_type = 'Opening Stock'
WHERE movement_type LIKE '%' || char(1575,1601,1578,1578,1575,1581) || '%' OR movement_type LIKE '%' || char(1571,1608,1604) || '%';

UPDATE material_stock_movements SET movement_type = 'Opening Stock'
WHERE movement_type LIKE '%' || char(1575,1601,1578,1578,1575,1581) || '%' OR movement_type LIKE '%' || char(1571,1608,1604) || '%';
