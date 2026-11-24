-- ============================================================================
-- 099 - Sales pricing & promotions engine (PostgreSQL) — sales cycle W7a, spr
-- Mirror of the SQLite migration: materials.bonus_buy_qty (the BUY side
-- of the dormant BXGY pair), spr_price_overrides (audited SAL-7018/7019/
-- 7020 overrides) and spr_price_history (master price-change audit).
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, ADD COLUMN IF NOT
-- EXISTS; the checksum ledger skips a re-run.
-- ============================================================================

ALTER TABLE materials
    ADD COLUMN IF NOT EXISTS bonus_buy_qty NUMERIC NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS spr_price_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    doc_id BIGINT,
    line_no INTEGER NOT NULL DEFAULT 0,
    material_id BIGINT,
    material_code TEXT,
    rule_code TEXT NOT NULL,
    limit_value NUMERIC NOT NULL DEFAULT 0,
    actual_value NUMERIC NOT NULL DEFAULT 0,
    unit_price NUMERIC NOT NULL DEFAULT 0,
    discount_pct NUMERIC NOT NULL DEFAULT 0,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_spr_price_overrides_doc
    ON spr_price_overrides(company_id, doc_kind, doc_no);

CREATE TABLE IF NOT EXISTS spr_price_history (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('Material','PriceList','CustomerPrice')),
    source_key TEXT NOT NULL,
    field_name TEXT NOT NULL,
    old_value NUMERIC,
    new_value NUMERIC,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_spr_price_history_source
    ON spr_price_history(company_id, source_kind, source_key);
