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
