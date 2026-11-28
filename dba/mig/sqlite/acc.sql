-- @shoutech-migration 007_accounting_indexes.sql C179A89E33BBE17874E77037D4F1CF9AA033EDBC30660AE0D014892DBF947AFC C2EC08E7FF40B9EB5DB396DA140FCC19266AEF00CE316A5D823F8F6361DADB8A C2EC08E7FF40B9EB5DB396DA140FCC19266AEF00CE316A5D823F8F6361DADB8A C2EC08E7FF40B9EB5DB396DA140FCC19266AEF00CE316A5D823F8F6361DADB8A
CREATE INDEX IF NOT EXISTS ix_journal_entries_company_date_status
    ON journal_entries(company_id, entry_date, status, journal_entry_id);
CREATE INDEX IF NOT EXISTS ix_journal_entry_lines_entry_account
    ON journal_entry_lines(journal_entry_id, account_id, line_number);
CREATE INDEX IF NOT EXISTS ix_journal_audit_events_entry_time
    ON journal_audit_events(journal_entry_id, event_at, journal_audit_event_id);
-- @shoutech-migration 008_journal_reversals.sql 0C9DF0E0BEBD076CA97C9B5A3CB42C87CD1A2E9B44AF23B86619B0F407F75421 07C6954DD083D31C61B08B70AE085CBAC9180559095ED669CD45A3373F7CA6D2 07C6954DD083D31C61B08B70AE085CBAC9180559095ED669CD45A3373F7CA6D2 07C6954DD083D31C61B08B70AE085CBAC9180559095ED669CD45A3373F7CA6D2
ALTER TABLE journal_entries ADD COLUMN reversal_of_entry_id TEXT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_reversal_of
    ON journal_entries(reversal_of_entry_id)
    WHERE reversal_of_entry_id IS NOT NULL;
-- @shoutech-migration 009_accounting_periods_and_accounts.sql 992F6C0BB8BFD64F28001A38335825AD6E16D590D738EBDBE35810A23A0322E6 1B229DBA8D544C9536BECB4E390AC15D88DC39A880DD63C97B5A1E0ADFB1A375 1B229DBA8D544C9536BECB4E390AC15D88DC39A880DD63C97B5A1E0ADFB1A375 1B229DBA8D544C9536BECB4E390AC15D88DC39A880DD63C97B5A1E0ADFB1A375
-- Accounting controls: chart of accounts, fiscal years and periods.
CREATE TABLE IF NOT EXISTS chart_of_accounts (
    account_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    account_name TEXT NOT NULL,
    account_type TEXT NOT NULL CHECK (account_type IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE')),
    parent_account_id TEXT,
    is_postable INTEGER NOT NULL DEFAULT 1 CHECK (is_postable IN (0,1)),
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    is_deleted INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0,1)),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(company_id, account_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id),
    FOREIGN KEY(parent_account_id) REFERENCES chart_of_accounts(account_id)
);
CREATE INDEX IF NOT EXISTS ix_chart_of_accounts_parent ON chart_of_accounts(company_id, parent_account_id);
CREATE INDEX IF NOT EXISTS ix_chart_of_accounts_active ON chart_of_accounts(company_id, is_active, is_postable);

ALTER TABLE fiscal_years ADD COLUMN closed_by TEXT;
ALTER TABLE fiscal_years ADD COLUMN closed_at TEXT;

CREATE TABLE IF NOT EXISTS fiscal_periods (
    fiscal_period_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    fiscal_year_id INTEGER NOT NULL,
    period_number INTEGER NOT NULL CHECK (period_number BETWEEN 1 AND 13),
    period_name TEXT NOT NULL,
    start_date TEXT NOT NULL,
    end_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','CLOSED','LOCKED')),
    closed_by TEXT,
    closed_at TEXT,
    UNIQUE(company_id, fiscal_year_id, period_number),
    CHECK (start_date <= end_date),
    FOREIGN KEY(company_id) REFERENCES companies(company_id),
    FOREIGN KEY(fiscal_year_id) REFERENCES fiscal_years(fiscal_year_id)
);
CREATE INDEX IF NOT EXISTS ix_fiscal_periods_dates ON fiscal_periods(company_id, start_date, end_date, status);

ALTER TABLE journal_entries ADD COLUMN fiscal_period_id TEXT NULL;
CREATE INDEX IF NOT EXISTS ix_journal_entries_period_status ON journal_entries(company_id, fiscal_period_id, status);

-- Seed missing fiscal years for years already represented in journal history.
INSERT OR IGNORE INTO fiscal_years(company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed)
SELECT DISTINCT company_id, CAST(substr(entry_date,1,4) AS INTEGER), '', substr(entry_date,1,4)||'-01-01', substr(entry_date,1,4)||'-12-31', 0, 0
FROM journal_entries;

-- Seed monthly periods only where a fiscal year has no periods yet.
INSERT OR IGNORE INTO fiscal_periods(fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status)
SELECT lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-4'||substr(lower(hex(randomblob(2))),2)||'-a'||substr(lower(hex(randomblob(2))),2)||'-'||lower(hex(randomblob(6))),
       fy.company_id, fy.fiscal_year_id, m.month_no, printf('%04d-%02d', fy.fiscal_year, m.month_no),
       printf('%04d-%02d-01', fy.fiscal_year, m.month_no),
       date(printf('%04d-%02d-01', fy.fiscal_year, m.month_no), '+1 month', '-1 day'), 'OPEN'
FROM fiscal_years fy
JOIN (SELECT 1 month_no UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11 UNION ALL SELECT 12) m
WHERE NOT EXISTS (SELECT 1 FROM fiscal_periods fp WHERE fp.company_id=fy.company_id AND fp.fiscal_year_id=fy.fiscal_year_id);

-- Attach legacy journal rows to the matching generated period without deleting business data.
UPDATE journal_entries
SET fiscal_period_id = (
    SELECT fp.fiscal_period_id FROM fiscal_periods fp
    WHERE fp.company_id = journal_entries.company_id
      AND journal_entries.entry_date BETWEEN fp.start_date AND fp.end_date
    ORDER BY fp.start_date LIMIT 1
)
WHERE fiscal_period_id IS NULL;

CREATE TRIGGER IF NOT EXISTS trg_journal_post_requires_open_period
BEFORE UPDATE OF status ON journal_entries
WHEN NEW.status = 1
BEGIN
    SELECT CASE WHEN NEW.fiscal_period_id IS NULL
      THEN RAISE(ABORT, 'Journal entry must belong to a fiscal period before posting') END;
    SELECT CASE WHEN NOT EXISTS (
      SELECT 1 FROM fiscal_periods fp JOIN fiscal_years fy ON fy.fiscal_year_id=fp.fiscal_year_id
      WHERE fp.fiscal_period_id=NEW.fiscal_period_id AND fp.company_id=NEW.company_id
        AND NEW.entry_date BETWEEN fp.start_date AND fp.end_date AND fp.status='OPEN' AND fy.is_closed=0
    ) THEN RAISE(ABORT, 'Fiscal period is closed or does not match journal date') END;
    SELECT CASE WHEN EXISTS (
      SELECT 1 FROM journal_entry_lines l LEFT JOIN chart_of_accounts a ON a.account_id=l.account_id AND a.company_id=NEW.company_id
      WHERE l.journal_entry_id=NEW.journal_entry_id AND (a.account_id IS NULL OR a.is_active<>1 OR a.is_deleted<>0 OR a.is_postable<>1)
    ) THEN RAISE(ABORT, 'Journal contains an inactive, missing, or non-postable account') END;
END;
CREATE TRIGGER IF NOT EXISTS trg_journal_post_insert_requires_open_period
BEFORE INSERT ON journal_entries
WHEN NEW.status = 1
BEGIN
    SELECT RAISE(ABORT, 'Journal entries must be inserted as drafts and posted after lines are added');
    SELECT CASE WHEN NEW.fiscal_period_id IS NULL
      THEN RAISE(ABORT, 'Journal entry must belong to a fiscal period before posting') END;
    SELECT CASE WHEN NOT EXISTS (
      SELECT 1 FROM fiscal_periods fp JOIN fiscal_years fy ON fy.fiscal_year_id=fp.fiscal_year_id
      WHERE fp.fiscal_period_id=NEW.fiscal_period_id AND fp.company_id=NEW.company_id
        AND NEW.entry_date BETWEEN fp.start_date AND fp.end_date AND fp.status='OPEN' AND fy.is_closed=0
    ) THEN RAISE(ABORT, 'Fiscal period is closed or does not match journal date') END;
    SELECT CASE WHEN EXISTS (
      SELECT 1 FROM journal_entry_lines l LEFT JOIN chart_of_accounts a ON a.account_id=l.account_id AND a.company_id=NEW.company_id
      WHERE l.journal_entry_id=NEW.journal_entry_id AND (a.account_id IS NULL OR a.is_active<>1 OR a.is_deleted<>0 OR a.is_postable<>1)
    ) THEN RAISE(ABORT, 'Journal contains an inactive, missing, or non-postable account') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_prevent_period_reopen
BEFORE UPDATE OF status ON fiscal_periods
WHEN (OLD.status='LOCKED' AND NEW.status<>'LOCKED') OR (OLD.status='CLOSED' AND NEW.status='OPEN')
BEGIN
    SELECT RAISE(ABORT, 'Closed or locked fiscal periods cannot be reopened through direct database updates');
END;
CREATE TRIGGER IF NOT EXISTS trg_prevent_year_reopen
BEFORE UPDATE OF is_closed ON fiscal_years
WHEN OLD.is_closed=1 AND NEW.is_closed=0
BEGIN
    SELECT RAISE(ABORT, 'Closed fiscal years cannot be reopened through direct database updates');
END;
-- @shoutech-migration 010_journal_header_metadata.sql 4B3FAA66B4C7F06B818997D1473E5AC9249B9F9986158F02311EB0C49A49B7D2 A99473F0CD9D493EF062E5CB19A1FA3BAE93FBB81F0F5925AACF4F69E709A03B A99473F0CD9D493EF062E5CB19A1FA3BAE93FBB81F0F5925AACF4F69E709A03B A99473F0CD9D493EF062E5CB19A1FA3BAE93FBB81F0F5925AACF4F69E709A03B
-- Persist the complete journal header entered by the journal window.
ALTER TABLE journal_entries ADD COLUMN reference_number TEXT NULL;
ALTER TABLE journal_entries ADD COLUMN currency_code TEXT NOT NULL DEFAULT 'SAR';
ALTER TABLE journal_entries ADD COLUMN exchange_rate NUMERIC NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
-- @shoutech-migration 011_journal_document_number.sql 9E86C739F1A52414AB448DC6BBED294BE89396BFEEDC821D00E7843229D48A68 BB7AD1CF1A57A2609DC99D4FD76B2C4A5CDA06179BE15D50A5CC99591C78B5E7 BB7AD1CF1A57A2609DC99D4FD76B2C4A5CDA06179BE15D50A5CC99591C78B5E7 BB7AD1CF1A57A2609DC99D4FD76B2C4A5CDA06179BE15D50A5CC99591C78B5E7
-- Preserve the visible journal document number separately from the external reference.
ALTER TABLE journal_entries ADD COLUMN document_number TEXT NULL;
-- @shoutech-migration 012_accounting_accounts.sql 047143C649FB68E1A4E3B5B0D24C7123E014E7643AEE694559CCBA6A0554A234 E301BDB3174730447E5D5CF2551A83447AC238EDE3C21DE8241C05A458C552AC E301BDB3174730447E5D5CF2551A83447AC238EDE3C21DE8241C05A458C552AC E301BDB3174730447E5D5CF2551A83447AC238EDE3C21DE8241C05A458C552AC
-- Chart of accounts schema is owned by DBA migrations.
        CREATE TABLE IF NOT EXISTS accounts (
            account_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            company_id TEXT NOT NULL DEFAULT '',
            account_code TEXT NOT NULL,
            parent_code TEXT,
            name_ar TEXT NOT NULL,
            name_en TEXT NOT NULL DEFAULT '',
            posting_mode TEXT NOT NULL DEFAULT 'قابل للترحيل',
            status TEXT NOT NULL DEFAULT 'فعال',
            risk TEXT NOT NULL DEFAULT 'منخفض',
            cost_center TEXT NOT NULL DEFAULT 'غير محدد',
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
            UNIQUE(company_id, account_code)
        );
        CREATE INDEX IF NOT EXISTS ix_accounts_parent ON accounts(company_id, parent_code, account_code);
        CREATE INDEX IF NOT EXISTS ix_accounts_code ON accounts(company_id, account_code);
-- @if-column-missing accounts.deleted_at
ALTER TABLE accounts ADD COLUMN deleted_at TEXT;
-- @if-column-missing accounts.deleted_by
ALTER TABLE accounts ADD COLUMN deleted_by TEXT;
-- @shoutech-migration 012_journal_header_fields.sql 5DC40CA13E5ED56DF5DCA56C0DFB663B08482AC43C9A51F67F415986DDFE0C53 9EE1E79595A613F213A16A76B975E9CCECA4B86E8A31C6C394C822DE1BC43682 9EE1E79595A613F213A16A76B975E9CCECA4B86E8A31C6C394C822DE1BC43682 9EE1E79595A613F213A16A76B975E9CCECA4B86E8A31C6C394C822DE1BC43682
-- Persist the journal header fields already accepted by the domain and UI.
-- Defaults preserve compatibility with existing journal entries.
-- @if-column-missing journal_entries.reference_number
ALTER TABLE journal_entries ADD COLUMN reference_number TEXT NULL;
-- @if-column-missing journal_entries.currency_code
ALTER TABLE journal_entries ADD COLUMN currency_code TEXT NOT NULL DEFAULT 'SAR' CHECK (length(trim(currency_code)) = 3);
-- @if-column-missing journal_entries.exchange_rate
ALTER TABLE journal_entries ADD COLUMN exchange_rate NUMERIC NOT NULL DEFAULT 1 CHECK (exchange_rate > 0);
-- @shoutech-migration 013_journal_document_number_unique.sql 78149CB9710C500E94B47F4F6BF5B63DB91B464AF19F9D2844D668260217B787 FB0E062A96107C975A4EE6F858DDDE850D99F2497909920A57FEB71CA6A9214B FB0E062A96107C975A4EE6F858DDDE850D99F2497909920A57FEB71CA6A9214B FB0E062A96107C975A4EE6F858DDDE850D99F2497909920A57FEB71CA6A9214B
-- Prevent duplicate visible journal numbers within a company while permitting legacy nulls.
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_company_document_number
    ON journal_entries(company_id, document_number)
    WHERE document_number IS NOT NULL AND length(trim(document_number)) > 0;
-- @shoutech-migration 026_projects.sql 33D69C5750BDB260B3457063DA44B4A36046CF2D8281C056C3836EB9199CF4E6 B93EC17BC1348893234860789451DA5934B34060825A6682524D0C91E3419FA9 B93EC17BC1348893234860789451DA5934B34060825A6682524D0C91E3419FA9 B93EC17BC1348893234860789451DA5934B34060825A6682524D0C91E3419FA9
-- Owns the Projects module (projects + project tasks). Dates are stored as
-- ISO text (yyyy-MM-dd) on both providers, matching the commercial-document
-- convention used by the provider-neutral repositories.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS projects (
    project_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    project_code TEXT NOT NULL,
    project_name TEXT NOT NULL,
    customer_id INTEGER,
    customer_name TEXT,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Active','OnHold','Completed','Cancelled')),
    start_date TEXT,
    end_date TEXT,
    budget NUMERIC NOT NULL DEFAULT 0 CHECK (budget >= 0),
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_projects_company_code
    ON projects(company_id, project_code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS project_tasks (
    task_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id),
    task_name TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'ToDo'
        CHECK (status IN ('ToDo','InProgress','Done')),
    due_date TEXT,
    assigned_to TEXT,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_project_tasks_project
    ON project_tasks(company_id, project_id);
-- @shoutech-migration 027_fixed_assets.sql B9084FAE0682E500498A59573393B87E42E2B3C77683074B65019CADC5026DE0 366DDBBC74E09BD1A08EACB3C1609A99F9523EA5495AC9415495D2557182D2D0 366DDBBC74E09BD1A08EACB3C1609A99F9523EA5495AC9415495D2557182D2D0 366DDBBC74E09BD1A08EACB3C1609A99F9523EA5495AC9415495D2557182D2D0
-- Owns the Fixed Assets register. Depreciation schedules are computed by the
-- application service from cost / salvage / useful life; only the register
-- itself is stored. Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS fixed_assets (
    asset_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    asset_code TEXT NOT NULL,
    asset_name TEXT NOT NULL,
    category TEXT,
    purchase_date TEXT NOT NULL,
    cost NUMERIC NOT NULL CHECK (cost >= 0),
    salvage_value NUMERIC NOT NULL DEFAULT 0 CHECK (salvage_value >= 0),
    useful_life_years INTEGER NOT NULL CHECK (useful_life_years > 0),
    depreciation_method TEXT NOT NULL DEFAULT 'StraightLine'
        CHECK (depreciation_method IN ('StraightLine','DecliningBalance')),
    location TEXT,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Disposed','FullyDepreciated')),
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_fixed_assets_company_code
    ON fixed_assets(company_id, asset_code) WHERE is_deleted = 0;
-- @shoutech-migration 030_english_only_seed_cleanup.sql 64940E5C9C0B28BC53EA496346E4FAA9C4D30C1A54040DF35D8EB58C6452A9B4 7FA8906F1C1BFBC73178B28363A627A0A96A127B97EB967F24B89891D81DA6BF 7FA8906F1C1BFBC73178B28363A627A0A96A127B97EB967F24B89891D81DA6BF 7FA8906F1C1BFBC73178B28363A627A0A96A127B97EB967F24B89891D81DA6BF
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
-- @shoutech-migration 052_posting_completeness.sql 7BC27FF068FB62EA20F0B31126B94B36F45EA467649F02A884512EA173CB2E02 A92462145DE93868D11BFE45A6053201E2AB92F3657A6D60575EE9BC5FB19001 A92462145DE93868D11BFE45A6053201E2AB92F3657A6D60575EE9BC5FB19001 A92462145DE93868D11BFE45A6053201E2AB92F3657A6D60575EE9BC5FB19001
-- Posting completeness (Phase A2):
-- 1) journal_line_cost_splits: the applied cost-center distribution of every
--    posted journal line, written by JournalRepository inside the posting
--    transaction. One row per (line, target cost center): when a line's cost
--    center has an active allocation (migration 031), the line amount is
--    split across the allocation targets by percentage; otherwise the line
--    maps 100% to its own leaf cost center. The SQLite table declares no
--    FKs (the party_ledger convention on this provider); the PostgreSQL
--    counterpart declares uuid FKs. The integrity of the rows themselves
--    is enforced by the posting transaction that writes them.
-- 2) journal_audit_events: allow event_type 4 (draft updated) alongside
--    1 (created), 2 (posted), 3 (reversed). SQLite cannot alter a CHECK
--    constraint in place, so the table is rebuilt preserving every row.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS journal_line_cost_splits (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    journal_entry_id TEXT NOT NULL,
    line_number INTEGER NOT NULL,
    cost_center_id TEXT NOT NULL,
    cost_center_code TEXT NOT NULL,
    percentage REAL NOT NULL,
    debit_amount NUMERIC NOT NULL DEFAULT 0,
    credit_amount NUMERIC NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_journal_line_cost_splits_entry
    ON journal_line_cost_splits(journal_entry_id, line_number);

CREATE INDEX IF NOT EXISTS ix_journal_line_cost_splits_center
    ON journal_line_cost_splits(company_id, cost_center_id);

CREATE TABLE journal_audit_events_new (
    journal_audit_event_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    journal_entry_id TEXT NOT NULL,
    event_type INTEGER NOT NULL CHECK (event_type IN (1, 2, 3, 4)),
    actor TEXT NOT NULL,
    event_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    details TEXT,
    FOREIGN KEY(journal_entry_id) REFERENCES journal_entries(journal_entry_id)
);

INSERT INTO journal_audit_events_new
    (journal_audit_event_id, journal_entry_id, event_type, actor, event_at, details)
SELECT journal_audit_event_id, journal_entry_id, event_type, actor, event_at, details
FROM journal_audit_events;

DROP TABLE journal_audit_events;

ALTER TABLE journal_audit_events_new RENAME TO journal_audit_events;

CREATE INDEX IF NOT EXISTS ix_journal_audit_events_entry_time
    ON journal_audit_events(journal_entry_id, event_at, journal_audit_event_id);
-- @shoutech-migration 055_budgets.sql AB56937BECAD32F458E3477D77CEFD3869148AA5D6FFAB6E6B6B28E4161F0069 6D074CB14620989F6F0DF345BFE845E6C04A458D2B85931C82900A30EC12C74F 6D074CB14620989F6F0DF345BFE845E6C04A458D2B85931C82900A30EC12C74F 6D074CB14620989F6F0DF345BFE845E6C04A458D2B85931C82900A30EC12C74F
-- ============================================================================
-- 055 - Budgets (SQLite)
--
-- Phase B design section 3 (migration 055): budgets live in the JOURNAL
-- world (Guid company id, journal_entry_lines, journal_line_cost_splits),
-- because that is where actuals and the posting guard belong.
--
--   * budgets: one budget per (company, fiscal year): what it is against
--     (CostCenter / Account / Project, owner decision BG3 includes
--     Project), its lifecycle status (Draft / Active / Closed - only
--     Active budgets are enforced, the ERPNext rule), and the control
--     action per horizon (action_annual / action_monthly, each
--     Stop / Warn / Ignore - the ERPNext control matrix). These
--     per-budget actions ARE the enforcement mode; the design specifies
--     no separate company-level mode switch.
--   * budget_lines: one row per (account x cost center x project) with
--     the annual amount. account_code is a snapshot for reporting.
--     cost_center_id references cost_centers.id (031); project_id
--     references projects.project_id (026); account_id references the
--     canonical chart_of_accounts.account_id (009/059).
--   * budget_period_amounts: the monthly distribution of a line
--     (period_no 1-12 within the budget's fiscal year). NO rows for a
--     line means the annual amount is split equally across the year;
--     when rows exist they are authoritative (the service layer keeps
--     their sum equal to the line's annual amount).
--   * budget_control_events: every Warn and Block the BudgetGuard
--     produces (design: every warn, block and override is auditable;
--     owner decision BG1 = no override in v1, so no Overridden rows
--     exist yet - the action_taken vocabulary reserves the value).
--
-- GROUND-TRUTH ADAPTATIONS vs the design text (documented in
-- docs/BUILD_PHASEB_055.md):
--   * fiscal_year_id is INTEGER here (BIGINT on PostgreSQL): that is
--     the real primary-key type of fiscal_years on both providers
--     (001/002), not TEXT as the design sketch wrote.
--   * The accounts.budget opt-in flag lives on the CANONICAL chart
--     (chart_of_accounts.budget, migration 059) since step 2 dropped
--     the legacy accounts table. BudgetGuard enforces only lines whose
--     account carries the flag; BudgetService sets the flag
--     transactionally when an account gains a budget line, so the flag
--     is the designed opt-in marker and never a silent trap.
--
-- NUMBERING NOTE: like 053/054, this file sorts before the
-- already-shipped 059-064 files; the migrator applies pending
-- migrations in filename order and skips by checksum. 055 depends on
-- 001/009 (fiscal years, chart), 026 (projects), 031 (cost centers),
-- 052 (cost splits) and 002-004 (roles/permissions) objects, never on
-- 059-064-only objects (chart_of_accounts.budget arrives with 059; the
-- guard reads it defensively so a pre-059 database simply enforces
-- nothing until unified).
--
-- Idempotent: table/index creation is IF NOT EXISTS, permission seeds
-- are INSERT OR IGNORE + UPDATE, and the checksum ledger skips a
-- re-run.
-- ============================================================================

-- ---- Budget headers ---------------------------------------------------------
CREATE TABLE IF NOT EXISTS budgets (
    budget_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    fiscal_year_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    budget_against TEXT NOT NULL DEFAULT 'CostCenter'
        CHECK (budget_against IN ('CostCenter','Account','Project')),
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Active','Closed')),
    action_annual TEXT NOT NULL DEFAULT 'Stop'
        CHECK (action_annual IN ('Stop','Warn','Ignore')),
    action_monthly TEXT NOT NULL DEFAULT 'Warn'
        CHECK (action_monthly IN ('Stop','Warn','Ignore')),
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_budgets_company_year_status
    ON budgets(company_id, fiscal_year_id, status)
    WHERE is_deleted = 0;

-- ---- Budget lines -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_lines (
    budget_line_id TEXT NOT NULL PRIMARY KEY,
    budget_id TEXT NOT NULL,
    account_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    cost_center_id TEXT,
    project_id TEXT,
    annual_amount NUMERIC NOT NULL DEFAULT 0 CHECK (annual_amount >= 0),
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_budget_lines_budget
    ON budget_lines(budget_id);
CREATE INDEX IF NOT EXISTS ix_budget_lines_account
    ON budget_lines(account_id);
-- One line per (budget, account, dimension): NULL dimensions are folded
-- to '' so the unique index actually binds (plain UNIQUE treats NULLs
-- as distinct on both providers).
CREATE UNIQUE INDEX IF NOT EXISTS ux_budget_lines_dimension
    ON budget_lines(budget_id, account_id,
                    COALESCE(cost_center_id, ''), COALESCE(project_id, ''));

-- ---- Monthly distribution -------------------------------------------------------
CREATE TABLE IF NOT EXISTS budget_period_amounts (
    budget_line_id TEXT NOT NULL,
    period_no INTEGER NOT NULL CHECK (period_no BETWEEN 1 AND 12),
    amount NUMERIC NOT NULL DEFAULT 0 CHECK (amount >= 0),
    PRIMARY KEY (budget_line_id, period_no)
);

-- ---- Control events (audit of every Warn / Block) --------------------------------
CREATE TABLE IF NOT EXISTS budget_control_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    budget_id TEXT NOT NULL,
    budget_line_id TEXT NOT NULL,
    journal_entry_id TEXT,
    action_taken TEXT NOT NULL CHECK (action_taken IN ('Warned','Blocked','Overridden')),
    horizon TEXT NOT NULL CHECK (horizon IN ('Annual','Monthly')),
    budget_amount NUMERIC NOT NULL DEFAULT 0,
    actual_amount NUMERIC NOT NULL DEFAULT 0,
    overage_amount NUMERIC NOT NULL DEFAULT 0,
    actor TEXT,
    reason TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_budget_control_events_budget
    ON budget_control_events(company_id, budget_id, created_at);
CREATE INDEX IF NOT EXISTS ix_budget_control_events_entry
    ON budget_control_events(journal_entry_id);

-- ---- Permissions (028/038/053/054 pattern) ----------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.VIEW','Accounting Budget - View','ACCOUNTING','BUDGET','VIEW','Accounting Budget - View','Accounting Budget - View','View budgets and budget-vs-actual variance (Phase B 055)',346,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='BUDGET',operation_code='VIEW',name_ar='Accounting Budget - View',name_en='Accounting Budget - View',display_order=346,is_sensitive=0 WHERE permission_code='ACC.BUDGET.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.EDIT','Accounting Budget - Edit','ACCOUNTING','BUDGET','EDIT','Accounting Budget - Edit','Accounting Budget - Edit','Create and edit budget definitions, lines and monthly distributions (Phase B 055)',347,0);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='BUDGET',operation_code='EDIT',name_ar='Accounting Budget - Edit',name_en='Accounting Budget - Edit',display_order=347,is_sensitive=0 WHERE permission_code='ACC.BUDGET.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('ACC.BUDGET.ACTIVATE','Accounting Budget - Activate','ACCOUNTING','BUDGET','ACTIVATE','Accounting Budget - Activate','Accounting Budget - Activate','Activate or close a budget so the posting guard enforces it (Phase B 055)',348,1);
UPDATE permissions SET group_code='ACCOUNTING',feature_code='BUDGET',operation_code='ACTIVATE',name_ar='Accounting Budget - Activate',name_en='Accounting Budget - Activate',display_order=348,is_sensitive=1 WHERE permission_code='ACC.BUDGET.ACTIVATE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.BUDGET.VIEW','ACC.BUDGET.EDIT','ACC.BUDGET.ACTIVATE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.BUDGET.VIEW','ACC.BUDGET.EDIT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('ACC.BUDGET.VIEW');
-- @shoutech-migration 059_coa_unification.sql 4223A80DE4AE14315AE91591451BCC8156F2DB4AD08EB7A444373F515D303E32 BFACDA4FABFDE227A112A7D4E22EC149FB5E04EE03AA5BE2A64418132D09C03D BFACDA4FABFDE227A112A7D4E22EC149FB5E04EE03AA5BE2A64418132D09C03D BFACDA4FABFDE227A112A7D4E22EC149FB5E04EE03AA5BE2A64418132D09C03D
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
-- @shoutech-migration 060_document_gl_posting.sql A10F434B93226730F1C99CADAEA6009B63367F0C690F9C79E34F1BFBDBA4104F 49E91BC0465E25C84C5ADD9A48FD3CC210BF987FFB8D63AFC29B225EDFE112FE 49E91BC0465E25C84C5ADD9A48FD3CC210BF987FFB8D63AFC29B225EDFE112FE 49E91BC0465E25C84C5ADD9A48FD3CC210BF987FFB8D63AFC29B225EDFE112FE
-- ============================================================================
-- 060 - Document GL posting link (SQLite)
--
-- Fix Phase Step 3 (sweep defect J-2): operational documents (invoices,
-- POS sales, order conversions, manufacturing completions, asset
-- depreciation) now create REAL journal entries. The entry carries its
-- source document on the header so reports, reversal and audit can trace
-- GL -> document and document -> GL without parsing document_number:
--   source_doc_type  e.g. SAL / PUR / SRT / PRT / POS / ORD-INV / MFG / AST-DEP
--   source_doc_id    document id in its own store (text: the stores mix
--                    integer doc_id and Guid ids)
--   source_doc_no    human document number at posting time
-- document_number on the entry remains the journal's own number
-- (JV-<source doc no> for document postings); journal_doc_no on the
-- document header is stamped with that same real number.
-- Idempotent: column adds are guarded by the migrator directives and the
-- index is IF NOT EXISTS; the checksum ledger also skips a re-run.
-- ============================================================================

-- @if-column-missing journal_entries.source_doc_type
ALTER TABLE journal_entries ADD COLUMN source_doc_type TEXT NULL;
-- @if-column-missing journal_entries.source_doc_id
ALTER TABLE journal_entries ADD COLUMN source_doc_id TEXT NULL;
-- @if-column-missing journal_entries.source_doc_no
ALTER TABLE journal_entries ADD COLUMN source_doc_no TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_journal_entries_source_doc
    ON journal_entries(company_id, source_doc_type, source_doc_id);
-- @shoutech-migration 061_party_unification.sql AF9B8BD8A86A67D9352B487317234FD03A56799479E1679C46106C755C9A8D47 6058E79089B5D0E49CB3C108770F78E0F3B61179D9061399B2F36CC7F517B90C 6058E79089B5D0E49CB3C108770F78E0F3B61179D9061399B2F36CC7F517B90C 6058E79089B5D0E49CB3C108770F78E0F3B61179D9061399B2F36CC7F517B90C
-- ============================================================================
-- 061 - Party master unification + settlement link (SQLite)
--
-- Fix Phase Step 4 (sweep defects C-1, C-2, B-03, B-06, G-1, RP-1, L-10):
-- the product had TWO party masters with no sync:
--   * parties            (migration 031): Guid id, kind C/S, company_id =
--     company CODE, written by CstCard / PartyManagerWin / PartySearchWin.
--   * customers/suppliers (migration 050): INTEGER id, company_id = company
--     CODE, read by invoicing (TaxSvc.ResolveParty), POS, the credit guard's
--     legacy fallback, and every party report. Nothing ever wrote them from
--     the cards, so a card-created party could not be invoiced, and the card
--     balance panel resolved the ledger through the party CODE parsed as a
--     number — code "1" inherited an unrelated party's balance (C-2).
--
-- DECISION: parties (031) is the ONE canonical party master.
--   * UI gravity: every party card/directory/picker already edits it, with
--     the richer field set (groups, tax no, commercial reg, credit terms).
--   * Operational gravity is preserved WITHOUT a second store: every
--     operational reference (party_ledger.party_id, sales/purchase
--     documents.party_id, payment_allocations.party_id) is an INTEGER, so
--     the canonical row carries a stable integer identity, ledger_id,
--     assigned by this migration (legacy ids are KEPT where a legacy row
--     matches, so no ledger/document history is re-pointed at all) and by
--     MasterCardRepository on every later insert. party_ledger.party_id
--     from this migration on means parties.ledger_id, always resolved
--     through (company, kind, code) — never by parsing the code.
--   * The legacy customers/suppliers tables are DROPPED at the end: one
--     writer path, one reader path, no dual-write, no compat view that
--     could silently diverge again.
--
-- Data mapping (explicit):
--   * match: legacy row joins parties on (company_id, kind, code) among
--     live rows; the parties row keeps its Guid and gains ledger_id =
--     the legacy integer id.
--   * legacy-only rows are INSERTED into parties with a fresh Guid and
--     ledger_id = their legacy id; fields map 1:1 (customer_name_en ->
--     name_en, customer_name_ar -> name_ar, tax_number -> tax_no,
--     commercial_reg -> commercial_reg_no, ...). The legacy free-text
--     party_group has no Guid target in party_groups and is not carried
--     (it was a label, never a foreign key).
--   * parties rows with no legacy match receive a fresh ledger_id ABOVE
--     every existing identity (max of assigned ledger_ids, legacy ids and
--     party_ledger.party_id values), so a fresh id can never inherit an
--     orphan ledger balance.
--   * party_ledger.company_id namespace (B-06): rows written under the
--     company GUID (order-conversion path) are normalized to the company
--     CODE every other writer/reader uses, via the companies registry.
--   * party_ledger gains journal_entry_id: the cash-voucher settlement
--     link. JournalRepository writes it when a posted voucher carries a
--     voucher_party_links row, and the shared reversal core mirrors those
--     rows when the voucher's journal entry is reversed.
--   * voucher_party_links (new): the party a cash voucher settles, stored
--     at draft-save time (resolved ledger id snapshot + amount), consumed
--     inside the journal posting transaction.
--
-- Idempotent: column adds are guarded by the migrator directives, every
-- data statement is self-guarding (ledger_id IS NULL / NOT EXISTS), the
-- drops are IF EXISTS, and the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Canonical identity + settlement link columns -------------------------
-- @if-column-missing parties.ledger_id
ALTER TABLE parties ADD COLUMN ledger_id INTEGER;
-- @if-column-missing party_ledger.journal_entry_id
ALTER TABLE party_ledger ADD COLUMN journal_entry_id TEXT;

CREATE TABLE IF NOT EXISTS voucher_party_links (
    journal_entry_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
    party_id INTEGER NOT NULL,
    party_code TEXT NOT NULL,
    doc_kind TEXT NOT NULL CHECK (doc_kind IN ('RCV','PAY')),
    doc_no TEXT NOT NULL,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_voucher_party_links_company
    ON voucher_party_links(company_id, party_kind, party_id);

-- ---- 1) Matched parties keep the legacy integer identity ------------------
UPDATE parties
SET ledger_id = (
        SELECT c.customer_id FROM customers c
        WHERE c.company_id = parties.company_id
          AND c.customer_code = parties.code
          AND c.is_deleted = 0)
WHERE parties.kind = 'C' AND parties.ledger_id IS NULL
  AND EXISTS (
        SELECT 1 FROM customers c
        WHERE c.company_id = parties.company_id
          AND c.customer_code = parties.code
          AND c.is_deleted = 0);

UPDATE parties
SET ledger_id = (
        SELECT s.supplier_id FROM suppliers s
        WHERE s.company_id = parties.company_id
          AND s.supplier_code = parties.code
          AND s.is_deleted = 0)
WHERE parties.kind = 'S' AND parties.ledger_id IS NULL
  AND EXISTS (
        SELECT 1 FROM suppliers s
        WHERE s.company_id = parties.company_id
          AND s.supplier_code = parties.code
          AND s.is_deleted = 0);

-- ---- 2) Legacy-only parties are created in the canonical store ------------
INSERT INTO parties
    (id, company_id, kind, code, name_en, name_ar, tax_no, commercial_reg_no,
     credit_limit, credit_days, price_tier, gl_account_code, phone, mobile,
     email, address, is_active, notes, is_deleted, created_by, created_at,
     updated_by, updated_at, ledger_id)
SELECT
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
        substr(lower(hex(randomblob(2))), 2) || '-a' ||
        substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
    c.company_id, 'C', c.customer_code,
    CASE WHEN c.customer_name_en <> '' THEN c.customer_name_en ELSE c.customer_name_ar END,
    c.customer_name_ar, c.tax_number, c.commercial_reg,
    c.credit_limit, c.credit_days, NULLIF(c.price_tier, ''), NULLIF(c.gl_account_code, ''),
    NULLIF(c.phone, ''), NULLIF(c.mobile, ''), NULLIF(c.email, ''), NULLIF(c.address, ''),
    c.is_active, NULL, c.is_deleted, c.created_by, c.created_at, c.updated_by, c.updated_at,
    c.customer_id
FROM customers c
WHERE NOT EXISTS (
    SELECT 1 FROM parties p
    WHERE p.company_id = c.company_id AND p.kind = 'C' AND p.code = c.customer_code AND p.is_deleted = 0);

INSERT INTO parties
    (id, company_id, kind, code, name_en, name_ar, tax_no, commercial_reg_no,
     credit_limit, credit_days, price_tier, gl_account_code, phone, mobile,
     email, address, is_active, notes, is_deleted, created_by, created_at,
     updated_by, updated_at, ledger_id)
SELECT
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
        substr(lower(hex(randomblob(2))), 2) || '-a' ||
        substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
    s.company_id, 'S', s.supplier_code,
    CASE WHEN s.supplier_name_en <> '' THEN s.supplier_name_en ELSE s.supplier_name_ar END,
    s.supplier_name_ar, s.tax_number, s.commercial_reg,
    s.credit_limit, s.credit_days, NULLIF(s.price_tier, ''), NULLIF(s.gl_account_code, ''),
    NULLIF(s.phone, ''), NULLIF(s.mobile, ''), NULLIF(s.email, ''), NULLIF(s.address, ''),
    s.is_active, NULL, s.is_deleted, s.created_by, s.created_at, s.updated_by, s.updated_at,
    s.supplier_id
FROM suppliers s
WHERE NOT EXISTS (
    SELECT 1 FROM parties p
    WHERE p.company_id = s.company_id AND p.kind = 'S' AND p.code = s.supplier_code AND p.is_deleted = 0);

-- ---- 3) Fresh ledger ids for card-only parties ----------------------------
-- Temporary negative markers keep the assignment unique while the final
-- values are computed above every pre-existing identity space.
UPDATE parties SET ledger_id = -rowid WHERE ledger_id IS NULL;

-- Materialize the ranks first: a correlated COUNT inside the assigning
-- UPDATE re-evaluates as earlier rows flip positive and hands two
-- parties the same fresh id.
DROP TABLE IF EXISTS tmp_party_fresh;
CREATE TEMP TABLE tmp_party_fresh AS
SELECT rowid AS prow,
       ROW_NUMBER() OVER (ORDER BY rowid) AS rn
FROM parties
WHERE ledger_id < 0;

UPDATE parties
SET ledger_id = (
        SELECT MAX(
                   (SELECT COALESCE(MAX(ledger_id), 0) FROM parties),
                   (SELECT COALESCE(MAX(customer_id), 0) FROM customers),
                   (SELECT COALESCE(MAX(supplier_id), 0) FROM suppliers),
                   (SELECT COALESCE(MAX(party_id), 0) FROM party_ledger))
               + f.rn
        FROM tmp_party_fresh f
        WHERE f.prow = parties.rowid)
WHERE rowid IN (SELECT prow FROM tmp_party_fresh);

DROP TABLE tmp_party_fresh;

CREATE UNIQUE INDEX IF NOT EXISTS ux_parties_ledger_id
    ON parties(ledger_id) WHERE ledger_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_party_ledger_journal
    ON party_ledger(journal_entry_id) WHERE journal_entry_id IS NOT NULL;

-- ---- 4) party_ledger company namespace: GUID -> company CODE (B-06) -------
UPDATE party_ledger
SET company_id = (
        SELECT co.company_code FROM companies co
        WHERE CAST(co.company_id AS TEXT) = party_ledger.company_id)
WHERE EXISTS (
        SELECT 1 FROM companies co
        WHERE CAST(co.company_id AS TEXT) = party_ledger.company_id
          AND co.company_code <> party_ledger.company_id);

-- ---- 5) One master: the legacy stores are gone ----------------------------
DROP TABLE IF EXISTS customers;
DROP TABLE IF EXISTS suppliers;
-- @shoutech-migration 062_base_currency_amounts.sql 7265EA75E4709D81C44BE91145D50831B487D6C63560015EE8EBCB712753CCEB 3C11F8D897F224433EEE16C4371A0998317C3239BBD34238E65FD9BD338C6922 3C11F8D897F224433EEE16C4371A0998317C3239BBD34238E65FD9BD338C6922 3C11F8D897F224433EEE16C4371A0998317C3239BBD34238E65FD9BD338C6922
-- ============================================================================
-- 062 - Base-currency amounts on every money line (SQLite)
--
-- Fix Phase Step 7 (sweep J-5 + invoicing/cash currency defects):
-- multi-currency was decorative. Journal headers stored currency_code +
-- exchange_rate, but journal_entry_lines carried no base-currency
-- amounts and no report converted by rate, so a USD entry at 3.75
-- reported as if 1 unit = 1 base unit. party_ledger had the same
-- disease: foreign-currency documents fed credit decisions at face
-- value.
--
-- This migration gives every money line that feeds the GL or a party
-- balance BOTH amounts:
--   * journal_entry_lines: base_debit / base_credit (transaction amount
--     x the effective exchange rate, rounded to 4dp) + exchange_rate
--     (the effective rate actually applied to the line, so the rate
--     source is auditable per line).
--   * journal_entries: base_currency_code (the company's base currency
--     the base amounts are expressed in; from companies.currency_code).
--   * party_ledger: base_debit / base_credit + exchange_rate, same
--     semantics, so a party's base exposure is never the raw foreign sum.
--
-- Rate-source precedence (enforced by the posting code, FxRates):
--   1. the document/entry rate stored on the header (explicit rate);
--   2. currency_rates master (latest rate on or before the entry date)
--      where the document carries no rate (stored rate <= 0);
--   3. base-currency rows are always rate 1.
-- Posting a foreign-currency document/entry with NO rate anywhere
-- fails with ACC-5015 instead of silently posting at 1.0.
--
-- Backfill: existing journal lines convert at their header rate (1.0
-- when the header currency IS the company base currency), so historical
-- reports stay consistent with the entries as posted. party_ledger
-- rows convert at (in order) the rate of the journal entry they settle
-- (journal_entry_id, migration 061), the rate of their source sales /
-- purchase document, the currency_rates master rate on or before the
-- entry date, else 1.0.
--
-- Idempotent: column adds are guarded by the migrator directives and
-- every backfill only touches rows whose base amounts are still NULL;
-- the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Journal lines: dual amounts -------------------------------------------
-- @if-column-missing journal_entry_lines.base_debit
ALTER TABLE journal_entry_lines ADD COLUMN base_debit NUMERIC;
-- @if-column-missing journal_entry_lines.base_credit
ALTER TABLE journal_entry_lines ADD COLUMN base_credit NUMERIC;
-- @if-column-missing journal_entry_lines.exchange_rate
ALTER TABLE journal_entry_lines ADD COLUMN exchange_rate NUMERIC;

-- ---- Journal header: the base currency the line amounts use ----------------
-- @if-column-missing journal_entries.base_currency_code
ALTER TABLE journal_entries ADD COLUMN base_currency_code TEXT;

-- ---- Party ledger: dual amounts ---------------------------------------------
-- @if-column-missing party_ledger.base_debit
ALTER TABLE party_ledger ADD COLUMN base_debit NUMERIC;
-- @if-column-missing party_ledger.base_credit
ALTER TABLE party_ledger ADD COLUMN base_credit NUMERIC;
-- @if-column-missing party_ledger.exchange_rate
ALTER TABLE party_ledger ADD COLUMN exchange_rate NUMERIC;

-- ---- Backfill: journal header base currency ---------------------------------
UPDATE journal_entries
SET base_currency_code = (
        SELECT co.currency_code FROM companies co
        WHERE CAST(co.company_id AS TEXT) = CAST(journal_entries.company_id AS TEXT))
WHERE base_currency_code IS NULL
  AND EXISTS (
        SELECT 1 FROM companies co
        WHERE CAST(co.company_id AS TEXT) = CAST(journal_entries.company_id AS TEXT)
          AND co.currency_code IS NOT NULL AND co.currency_code <> '');

-- ---- Backfill: journal lines at the header rate ------------------------------
-- Effective rate: 1 when the entry currency is the company's base
-- currency, otherwise the stored header exchange_rate (falling back to
-- 1 when a legacy header carries no usable rate).
UPDATE journal_entry_lines
SET exchange_rate = (
        SELECT CASE
                 WHEN j.currency_code IS NOT NULL
                      AND j.base_currency_code IS NOT NULL
                      AND UPPER(j.currency_code) = UPPER(j.base_currency_code)
                   THEN 1
                 WHEN COALESCE(j.exchange_rate, 0) > 0 THEN j.exchange_rate
                 ELSE 1
               END
        FROM journal_entries j
        WHERE j.journal_entry_id = journal_entry_lines.journal_entry_id),
    base_debit = ROUND(debit * (
        SELECT CASE
                 WHEN j.currency_code IS NOT NULL
                      AND j.base_currency_code IS NOT NULL
                      AND UPPER(j.currency_code) = UPPER(j.base_currency_code)
                   THEN 1
                 WHEN COALESCE(j.exchange_rate, 0) > 0 THEN j.exchange_rate
                 ELSE 1
               END
        FROM journal_entries j
        WHERE j.journal_entry_id = journal_entry_lines.journal_entry_id), 4),
    base_credit = ROUND(credit * (
        SELECT CASE
                 WHEN j.currency_code IS NOT NULL
                      AND j.base_currency_code IS NOT NULL
                      AND UPPER(j.currency_code) = UPPER(j.base_currency_code)
                   THEN 1
                 WHEN COALESCE(j.exchange_rate, 0) > 0 THEN j.exchange_rate
                 ELSE 1
               END
        FROM journal_entries j
        WHERE j.journal_entry_id = journal_entry_lines.journal_entry_id), 4)
WHERE base_debit IS NULL;

-- ---- Backfill: party_ledger at the journal / document / master rate ---------
-- Pass 1: rows that settle a journal entry take that entry's rate.
UPDATE party_ledger
SET exchange_rate = (
        SELECT CASE
                 WHEN UPPER(COALESCE(j.currency_code, '')) =
                      UPPER(COALESCE((SELECT co.currency_code FROM companies co
                                      WHERE co.company_code = party_ledger.company_id), ''))
                   THEN 1
                 WHEN COALESCE(j.exchange_rate, 0) > 0 THEN j.exchange_rate
                 ELSE 1
               END
        FROM journal_entries j
        WHERE CAST(j.journal_entry_id AS TEXT) = party_ledger.journal_entry_id)
WHERE base_debit IS NULL AND journal_entry_id IS NOT NULL
  AND EXISTS (SELECT 1 FROM journal_entries j
              WHERE CAST(j.journal_entry_id AS TEXT) = party_ledger.journal_entry_id);

-- Pass 2: rows written by a sales document take the document's rate.
UPDATE party_ledger
SET exchange_rate = (
        SELECT CASE WHEN COALESCE(d.exchange_rate, 0) > 0 THEN d.exchange_rate ELSE 1 END
        FROM sales_documents d
        WHERE d.doc_id = party_ledger.doc_id AND d.company_id = party_ledger.company_id)
WHERE base_debit IS NULL AND exchange_rate IS NULL AND doc_id IS NOT NULL
  AND EXISTS (SELECT 1 FROM sales_documents d
              WHERE d.doc_id = party_ledger.doc_id AND d.company_id = party_ledger.company_id);

-- Pass 3: rows written by a purchase document take the document's rate.
UPDATE party_ledger
SET exchange_rate = (
        SELECT CASE WHEN COALESCE(d.exchange_rate, 0) > 0 THEN d.exchange_rate ELSE 1 END
        FROM purchase_documents d
        WHERE d.doc_id = party_ledger.doc_id AND d.company_id = party_ledger.company_id)
WHERE base_debit IS NULL AND exchange_rate IS NULL AND doc_id IS NOT NULL
  AND EXISTS (SELECT 1 FROM purchase_documents d
              WHERE d.doc_id = party_ledger.doc_id AND d.company_id = party_ledger.company_id);

-- Pass 4: the rest take the currency_rates master rate on or before
-- the entry date for their currency (company keyed by code, the
-- namespace currency_rates is maintained in), else 1.0.
UPDATE party_ledger
SET exchange_rate = COALESCE((
        SELECT r.rate FROM currency_rates r
        WHERE r.company_id = party_ledger.company_id
          AND UPPER(r.currency_code) = UPPER(party_ledger.currency_code)
          AND r.rate_date <= party_ledger.entry_date
          AND COALESCE(r.rate, 0) > 0
        ORDER BY r.rate_date DESC LIMIT 1), 1)
WHERE base_debit IS NULL AND exchange_rate IS NULL;

UPDATE party_ledger
SET base_debit = ROUND(debit * exchange_rate, 4),
    base_credit = ROUND(credit * exchange_rate, 4)
WHERE base_debit IS NULL;
-- @shoutech-migration 118_account_card_fields.sql C3C84C1E584CC9B384FA72CC379FD5F956B71D3747F3D6C2F00457E85FADEC29 5E59076475989AA6D443AA5C872D4AE17F5896F09DEE09943F4CF0FA5F16AA9E 5E59076475989AA6D443AA5C872D4AE17F5896F09DEE09943F4CF0FA5F16AA9E 5E59076475989AA6D443AA5C872D4AE17F5896F09DEE09943F4CF0FA5F16AA9E
-- 118 - Account card completion (grid program R2e, owner spec
-- 2026-11-24 15:07/15:10). The account card is modelled on the
-- Al-Ameen 2004 chart/card reference (structure/content only;
-- visuals stay on the modern central system). chart_of_accounts
-- already carried code, names (Arabic + Latin), type, parent,
-- risk/confidentiality level, budget-control flag, reconcile
-- flag, tax rate, notes and computed balances. The fields the
-- reference card shows that the schema lacked are added here:
-- the closing account (posted to at year-end closing), the
-- balance warning side, the account's nature/side, the estimated
-- budget amount, the account currency + exchange parity, and
-- the last reconciliation date.

-- @if-column-missing chart_of_accounts.closing_account_id
ALTER TABLE chart_of_accounts ADD COLUMN closing_account_id TEXT;
-- @if-column-missing chart_of_accounts.warning_side
ALTER TABLE chart_of_accounts ADD COLUMN warning_side TEXT NOT NULL DEFAULT 'NONE';
-- @if-column-missing chart_of_accounts.account_side
ALTER TABLE chart_of_accounts ADD COLUMN account_side TEXT NOT NULL DEFAULT 'BOTH';
-- @if-column-missing chart_of_accounts.estimated_budget
ALTER TABLE chart_of_accounts ADD COLUMN estimated_budget NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing chart_of_accounts.currency_code
ALTER TABLE chart_of_accounts ADD COLUMN currency_code TEXT;
-- @if-column-missing chart_of_accounts.exchange_parity
ALTER TABLE chart_of_accounts ADD COLUMN exchange_parity NUMERIC NOT NULL DEFAULT 1;
-- @if-column-missing chart_of_accounts.reconciliation_date
ALTER TABLE chart_of_accounts ADD COLUMN reconciliation_date TEXT;
-- @shoutech-migration 128_fiscal_period_jrn_sequences.sql 57DFE54F25B41BA23313E7798853BF87CA32D47E3741E2F9ACC08F78FB22AC9A 2A13732FA59119BEB0E7D0BD14647836C4FCED4D390C3612E3853CE98107C00E 2A13732FA59119BEB0E7D0BD14647836C4FCED4D390C3612E3853CE98107C00E 2A13732FA59119BEB0E7D0BD14647836C4FCED4D390C3612E3853CE98107C00E
-- Central journal numbers restart at 1 for each actual fiscal period.
-- Keep the legacy document_number_sequences table for non-JRN families.
CREATE TABLE IF NOT EXISTS fiscal_period_number_sequences (
    company_id TEXT NOT NULL,
    series_key TEXT NOT NULL,
    fiscal_period_id TEXT NOT NULL,
    last_value INTEGER NOT NULL CHECK (last_value >= 0),
    PRIMARY KEY (company_id, series_key, fiscal_period_id)
);

-- Allow the same immutable JRN number in different fiscal periods.
DROP INDEX IF EXISTS ux_journal_entries_company_document_number;
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_company_period_document_number
    ON journal_entries(company_id, fiscal_period_id, document_number)
    WHERE document_number IS NOT NULL AND length(trim(document_number)) > 0;
-- @shoutech-migration 129_cash_book_accounts.sql CA6B8F88143B22674C7E1F5F4A38E363868F77427F41BE3FC2D7FA95BB3AF2AF E5FEDE6B296C2B32108EC0F6DEB63E7FD79B74954294775DB41DDF9F6DC334BC E5FEDE6B296C2B32108EC0F6DEB63E7FD79B74954294775DB41DDF9F6DC334BC E5FEDE6B296C2B32108EC0F6DEB63E7FD79B74954294775DB41DDF9F6DC334BC
-- 129 - Explicit cash-book classification for chart accounts.
-- Cash-book membership is configured on each posting account; account names
-- are never used to infer whether a movement belongs in the cash journal.
-- @if-column-missing chart_of_accounts.cash_book
ALTER TABLE chart_of_accounts ADD COLUMN cash_book INTEGER NOT NULL DEFAULT 0;
