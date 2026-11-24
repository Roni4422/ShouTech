-- ============================================================================
-- 099 - Sales pricing & promotions engine (SQLite) — sales cycle W7a, spr
--
--   * materials.bonus_buy_qty: the BUY side of the dormant BXGY pair.
--     bonus_qty (013) alone could not express "buy X get Y free" — it
--     records only the free quantity. bonus_buy_qty is the paid quantity
--     that earns bonus_qty free units of bonus_item_code (or of the
--     material itself when bonus_item_code is empty).
--   * spr_price_overrides: audit row SalesPriceGuard writes when a
--     SALES.PRICE.OVERRIDE / SALES.DISCOUNT.OVERRIDE holder authorizes a
--     line past price_min (SAL-7018), margin_target_pct (SAL-7019) or
--     discount_max_pct (SAL-7020) — the PartyCreditGuard/credit_overrides
--     pattern (095), per line and per rule.
--   * spr_price_history: master price-change audit (old -> new, who,
--     when) for material card prices, material_price_list prices and
--     customer_prices, written by SalesPriceHistory at the save seams.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, column add guarded by
-- the migrator directive; the checksum ledger skips a re-run.
-- ============================================================================

-- @if-column-missing materials.bonus_buy_qty
ALTER TABLE materials ADD COLUMN bonus_buy_qty NUMERIC NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS spr_price_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    doc_id INTEGER,
    line_no INTEGER NOT NULL DEFAULT 0,
    material_id INTEGER,
    material_code TEXT,
    rule_code TEXT NOT NULL,
    limit_value REAL NOT NULL DEFAULT 0,
    actual_value REAL NOT NULL DEFAULT 0,
    unit_price REAL NOT NULL DEFAULT 0,
    discount_pct REAL NOT NULL DEFAULT 0,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_spr_price_overrides_doc
    ON spr_price_overrides(company_id, doc_kind, doc_no);

CREATE TABLE IF NOT EXISTS spr_price_history (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('Material','PriceList','CustomerPrice')),
    source_key TEXT NOT NULL,
    field_name TEXT NOT NULL,
    old_value REAL,
    new_value REAL,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_spr_price_history_source
    ON spr_price_history(company_id, source_kind, source_key);
