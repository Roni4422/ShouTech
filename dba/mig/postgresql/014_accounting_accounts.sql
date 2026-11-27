-- Chart of accounts schema is owned by DBA migrations.
        CREATE TABLE IF NOT EXISTS accounts (
            account_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL DEFAULT '',
            account_code TEXT NOT NULL,
            parent_code TEXT,
            name_ar TEXT NOT NULL,
            name_en TEXT NOT NULL DEFAULT '',
            posting_mode TEXT NOT NULL DEFAULT 'قابل للترحيل',
            status TEXT NOT NULL DEFAULT 'فعال',
            risk TEXT NOT NULL DEFAULT 'منخفض',
            cost_center TEXT NOT NULL DEFAULT 'غير محدد',
            allow_reconcile BOOLEAN NOT NULL DEFAULT FALSE,
            taxable BOOLEAN NOT NULL DEFAULT FALSE,
            foreign_currency BOOLEAN NOT NULL DEFAULT FALSE,
            cost_required BOOLEAN NOT NULL DEFAULT FALSE,
            budget BOOLEAN NOT NULL DEFAULT FALSE,
            is_default BOOLEAN NOT NULL DEFAULT FALSE,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            description TEXT NOT NULL DEFAULT '',
            balance NUMERIC NOT NULL DEFAULT 0,
            debit NUMERIC NOT NULL DEFAULT 0,
            credit NUMERIC NOT NULL DEFAULT 0,
            is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
            created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TIMESTAMPTZ,
            UNIQUE(company_id, account_code)
        );
        CREATE INDEX IF NOT EXISTS ix_accounts_parent ON accounts(company_id, parent_code, account_code);
        CREATE INDEX IF NOT EXISTS ix_accounts_code ON accounts(company_id, account_code);
ALTER TABLE accounts ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;
ALTER TABLE accounts ADD COLUMN IF NOT EXISTS deleted_by TEXT;
