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
