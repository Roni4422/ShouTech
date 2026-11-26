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
