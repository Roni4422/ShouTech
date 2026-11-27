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
