-- Currency master data and company-scoped exchange-rate history.
-- Additive migration: existing currency codes in historical documents remain unchanged.
CREATE TABLE IF NOT EXISTS currencies (
    currency_code TEXT PRIMARY KEY COLLATE NOCASE,
    currency_name_en TEXT NOT NULL,
    currency_name_ar TEXT,
    symbol TEXT,
    fraction_name_en TEXT,
    fraction_name_ar TEXT,
    decimal_places INTEGER NOT NULL DEFAULT 2 CHECK (decimal_places BETWEEN 0 AND 6),
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT
);

CREATE TABLE IF NOT EXISTS currency_rates (
    rate_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL REFERENCES companies(company_id) ON DELETE RESTRICT,
    from_currency TEXT NOT NULL COLLATE NOCASE REFERENCES currencies(currency_code) ON UPDATE CASCADE ON DELETE RESTRICT,
    to_currency TEXT NOT NULL COLLATE NOCASE REFERENCES currencies(currency_code) ON UPDATE CASCADE ON DELETE RESTRICT,
    rate NUMERIC NOT NULL CHECK (rate > 0),
    buy_rate NUMERIC CHECK (buy_rate IS NULL OR buy_rate > 0),
    sell_rate NUMERIC CHECK (sell_rate IS NULL OR sell_rate > 0),
    min_rate NUMERIC CHECK (min_rate IS NULL OR min_rate > 0),
    max_rate NUMERIC CHECK (max_rate IS NULL OR max_rate > 0),
    effective_date TEXT NOT NULL,
    source TEXT,
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    CHECK (min_rate IS NULL OR max_rate IS NULL OR min_rate <= max_rate),
    CHECK (from_currency <> to_currency),
    UNIQUE (company_id, from_currency, to_currency, effective_date)
);

CREATE INDEX IF NOT EXISTS ix_currency_rates_company_date
    ON currency_rates(company_id, effective_date DESC);
CREATE INDEX IF NOT EXISTS ix_currency_rates_pair_date
    ON currency_rates(company_id, from_currency, to_currency, effective_date DESC);
