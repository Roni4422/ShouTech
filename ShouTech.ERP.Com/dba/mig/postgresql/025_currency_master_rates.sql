-- Currency master data and company-scoped exchange-rate history.
-- Additive migration: existing currency codes in historical documents remain unchanged.
CREATE TABLE IF NOT EXISTS currencies (
    currency_code VARCHAR(3) PRIMARY KEY,
    currency_name_en VARCHAR(100) NOT NULL,
    currency_name_ar VARCHAR(100),
    symbol VARCHAR(16),
    fraction_name_en VARCHAR(100),
    fraction_name_ar VARCHAR(100),
    decimal_places SMALLINT NOT NULL DEFAULT 2 CHECK (decimal_places BETWEEN 0 AND 6),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS currency_rates (
    rate_id BIGSERIAL PRIMARY KEY,
    company_id UUID NOT NULL REFERENCES companies(company_id) ON DELETE RESTRICT,
    from_currency VARCHAR(3) NOT NULL REFERENCES currencies(currency_code) ON UPDATE CASCADE ON DELETE RESTRICT,
    to_currency VARCHAR(3) NOT NULL REFERENCES currencies(currency_code) ON UPDATE CASCADE ON DELETE RESTRICT,
    rate NUMERIC(20, 10) NOT NULL CHECK (rate > 0),
    buy_rate NUMERIC(20, 10) CHECK (buy_rate IS NULL OR buy_rate > 0),
    sell_rate NUMERIC(20, 10) CHECK (sell_rate IS NULL OR sell_rate > 0),
    min_rate NUMERIC(20, 10) CHECK (min_rate IS NULL OR min_rate > 0),
    max_rate NUMERIC(20, 10) CHECK (max_rate IS NULL OR max_rate > 0),
    effective_date DATE NOT NULL,
    source VARCHAR(120),
    notes TEXT,
    created_by VARCHAR(120),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK (min_rate IS NULL OR max_rate IS NULL OR min_rate <= max_rate),
    CHECK (from_currency <> to_currency),
    UNIQUE (company_id, from_currency, to_currency, effective_date)
);

CREATE INDEX IF NOT EXISTS ix_currency_rates_company_date
    ON currency_rates(company_id, effective_date DESC);
CREATE INDEX IF NOT EXISTS ix_currency_rates_pair_date
    ON currency_rates(company_id, from_currency, to_currency, effective_date DESC);
