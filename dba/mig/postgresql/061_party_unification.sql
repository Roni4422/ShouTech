-- ============================================================================
-- 061 - Party master unification + settlement link (PostgreSQL)
--
-- Mirror of the SQLite migration: parties (031) becomes the ONE canonical
-- party master; its stable integer identity is parties.ledger_id (legacy
-- customers/suppliers ids are kept on match, legacy-only rows are inserted
-- with their legacy id, card-only parties receive fresh ids above every
-- existing identity incl. party_ledger.party_id). party_ledger.company_id
-- rows written under the company GUID are normalized to the company CODE.
-- party_ledger gains journal_entry_id (cash-voucher settlement link) and
-- voucher_party_links stores a voucher's resolved party at draft-save time.
-- The legacy customers/suppliers tables are dropped at the end.
-- Idempotent: IF NOT EXISTS / column guards / self-guarding data statements.
-- ============================================================================

ALTER TABLE parties ADD COLUMN IF NOT EXISTS ledger_id BIGINT;
ALTER TABLE party_ledger ADD COLUMN IF NOT EXISTS journal_entry_id TEXT;

CREATE TABLE IF NOT EXISTS voucher_party_links (
    journal_entry_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
    party_id BIGINT NOT NULL,
    party_code TEXT NOT NULL,
    doc_kind TEXT NOT NULL CHECK (doc_kind IN ('RCV','PAY')),
    doc_no TEXT NOT NULL,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_voucher_party_links_company
    ON voucher_party_links(company_id, party_kind, party_id);

-- 1) Matched parties keep the legacy integer identity.
UPDATE parties p
SET ledger_id = c.customer_id
FROM customers c
WHERE p.kind = 'C' AND p.ledger_id IS NULL
  AND c.company_id = p.company_id AND c.customer_code = p.code AND c.is_deleted = 0;

UPDATE parties p
SET ledger_id = s.supplier_id
FROM suppliers s
WHERE p.kind = 'S' AND p.ledger_id IS NULL
  AND s.company_id = p.company_id AND s.supplier_code = p.code AND s.is_deleted = 0;

-- 2) Legacy-only parties are created in the canonical store.
INSERT INTO parties
    (id, company_id, kind, code, name_en, name_ar, tax_no, commercial_reg_no,
     credit_limit, credit_days, price_tier, gl_account_code, phone, mobile,
     email, address, is_active, notes, is_deleted, created_by, created_at,
     updated_by, updated_at, ledger_id)
SELECT
    md5(c.company_id || ':061:C:' || c.customer_code)::uuid,
    c.company_id, 'C', c.customer_code,
    CASE WHEN c.customer_name_en <> '' THEN c.customer_name_en ELSE c.customer_name_ar END,
    c.customer_name_ar, c.tax_number, c.commercial_reg,
    c.credit_limit, c.credit_days, NULLIF(c.price_tier, ''), NULLIF(c.gl_account_code, ''),
    NULLIF(c.phone, ''), NULLIF(c.mobile, ''), NULLIF(c.email, ''), NULLIF(c.address, ''),
    (c.is_active <> 0), NULL, (c.is_deleted <> 0), c.created_by, c.created_at,
    c.updated_by, c.updated_at,
    c.customer_id
FROM customers c
WHERE NOT EXISTS (
    SELECT 1 FROM parties p
    WHERE p.company_id = c.company_id AND p.kind = 'C' AND p.code = c.customer_code AND p.is_deleted = FALSE)
ON CONFLICT DO NOTHING;

INSERT INTO parties
    (id, company_id, kind, code, name_en, name_ar, tax_no, commercial_reg_no,
     credit_limit, credit_days, price_tier, gl_account_code, phone, mobile,
     email, address, is_active, notes, is_deleted, created_by, created_at,
     updated_by, updated_at, ledger_id)
SELECT
    md5(s.company_id || ':061:S:' || s.supplier_code)::uuid,
    s.company_id, 'S', s.supplier_code,
    CASE WHEN s.supplier_name_en <> '' THEN s.supplier_name_en ELSE s.supplier_name_ar END,
    s.supplier_name_ar, s.tax_number, s.commercial_reg,
    s.credit_limit, s.credit_days, NULLIF(s.price_tier, ''), NULLIF(s.gl_account_code, ''),
    NULLIF(s.phone, ''), NULLIF(s.mobile, ''), NULLIF(s.email, ''), NULLIF(s.address, ''),
    (s.is_active <> 0), NULL, (s.is_deleted <> 0), s.created_by, s.created_at,
    s.updated_by, s.updated_at,
    s.supplier_id
FROM suppliers s
WHERE NOT EXISTS (
    SELECT 1 FROM parties p
    WHERE p.company_id = s.company_id AND p.kind = 'S' AND p.code = s.supplier_code AND p.is_deleted = FALSE)
ON CONFLICT DO NOTHING;

-- 3) Fresh ledger ids for card-only parties, above every existing identity.
WITH base AS (
    SELECT GREATEST(
        COALESCE((SELECT MAX(ledger_id) FROM parties), 0),
        COALESCE((SELECT MAX(party_id) FROM party_ledger), 0)) AS v
),
ranked AS (
    SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS rn
    FROM parties WHERE ledger_id IS NULL
)
UPDATE parties p
SET ledger_id = (SELECT v FROM base) + ranked.rn
FROM ranked
WHERE p.id = ranked.id;

CREATE UNIQUE INDEX IF NOT EXISTS ux_parties_ledger_id
    ON parties(ledger_id) WHERE ledger_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_party_ledger_journal
    ON party_ledger(journal_entry_id) WHERE journal_entry_id IS NOT NULL;

-- 4) party_ledger company namespace: GUID -> company CODE (B-06).
UPDATE party_ledger pl
SET company_id = co.company_code
FROM companies co
WHERE CAST(co.company_id AS TEXT) = pl.company_id
  AND co.company_code <> pl.company_id;

-- 5) One master: the legacy stores are gone.
DROP TABLE IF EXISTS customers;
DROP TABLE IF EXISTS suppliers;
