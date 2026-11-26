-- ============================================================================
-- 062 - Base-currency amounts on every money line (PostgreSQL)
--
-- Mirror of the SQLite migration: journal_entry_lines and party_ledger
-- gain base_debit / base_credit + the effective exchange_rate applied;
-- journal_entries gains base_currency_code (companies.currency_code).
-- Backfill converts existing lines at their header / document rate
-- (1.0 for base-currency rows) so historical reports stay consistent.
-- Rate precedence + the ACC-5015 missing-rate failure live in the
-- posting code (FxRates). Idempotent: ADD COLUMN IF NOT EXISTS and
-- backfills that only touch rows whose base amounts are still NULL.
-- ============================================================================

ALTER TABLE journal_entry_lines ADD COLUMN IF NOT EXISTS base_debit NUMERIC;
ALTER TABLE journal_entry_lines ADD COLUMN IF NOT EXISTS base_credit NUMERIC;
ALTER TABLE journal_entry_lines ADD COLUMN IF NOT EXISTS exchange_rate NUMERIC;

ALTER TABLE journal_entries ADD COLUMN IF NOT EXISTS base_currency_code TEXT;

ALTER TABLE party_ledger ADD COLUMN IF NOT EXISTS base_debit NUMERIC;
ALTER TABLE party_ledger ADD COLUMN IF NOT EXISTS base_credit NUMERIC;
ALTER TABLE party_ledger ADD COLUMN IF NOT EXISTS exchange_rate NUMERIC;

-- ---- Backfill: journal header base currency ---------------------------------
UPDATE journal_entries j
SET base_currency_code = co.currency_code
FROM companies co
WHERE CAST(co.company_id AS TEXT) = CAST(j.company_id AS TEXT)
  AND j.base_currency_code IS NULL
  AND co.currency_code IS NOT NULL AND co.currency_code <> '';

-- ---- Backfill: journal lines at the header rate ------------------------------
UPDATE journal_entry_lines l
SET exchange_rate = eff.rate,
    base_debit = ROUND((l.debit * eff.rate)::numeric, 4),
    base_credit = ROUND((l.credit * eff.rate)::numeric, 4)
FROM journal_entries j,
     LATERAL (SELECT CASE
                       WHEN j.currency_code IS NOT NULL
                            AND j.base_currency_code IS NOT NULL
                            AND UPPER(j.currency_code) = UPPER(j.base_currency_code)
                         THEN 1::numeric
                       WHEN COALESCE(j.exchange_rate, 0) > 0 THEN j.exchange_rate
                       ELSE 1::numeric
                     END AS rate) eff
WHERE j.journal_entry_id = l.journal_entry_id
  AND l.base_debit IS NULL;

-- ---- Backfill: party_ledger ---------------------------------------------------
-- Pass 1: journal-linked settlement rows take the journal entry's rate.
UPDATE party_ledger p
SET exchange_rate = CASE
                      WHEN UPPER(COALESCE(j.currency_code, '')) =
                           UPPER(COALESCE((SELECT co.currency_code FROM companies co
                                           WHERE co.company_code = p.company_id), ''))
                        THEN 1
                      WHEN COALESCE(j.exchange_rate, 0) > 0 THEN j.exchange_rate
                      ELSE 1
                    END
FROM journal_entries j
WHERE CAST(j.journal_entry_id AS TEXT) = p.journal_entry_id
  AND p.base_debit IS NULL AND p.journal_entry_id IS NOT NULL;

-- Pass 2: sales-document rows take the document's rate.
UPDATE party_ledger p
SET exchange_rate = CASE WHEN COALESCE(d.exchange_rate, 0) > 0 THEN d.exchange_rate ELSE 1 END
FROM sales_documents d
WHERE d.doc_id = p.doc_id AND d.company_id = p.company_id
  AND p.base_debit IS NULL AND p.exchange_rate IS NULL AND p.doc_id IS NOT NULL;

-- Pass 3: purchase-document rows take the document's rate.
UPDATE party_ledger p
SET exchange_rate = CASE WHEN COALESCE(d.exchange_rate, 0) > 0 THEN d.exchange_rate ELSE 1 END
FROM purchase_documents d
WHERE d.doc_id = p.doc_id AND d.company_id = p.company_id
  AND p.base_debit IS NULL AND p.exchange_rate IS NULL AND p.doc_id IS NOT NULL;

-- Pass 4: master rate on or before the entry date, else 1.0.
UPDATE party_ledger p
SET exchange_rate = COALESCE((
        SELECT r.rate FROM currency_rates r
        WHERE r.company_id = p.company_id
          AND UPPER(r.currency_code) = UPPER(p.currency_code)
          AND r.rate_date <= p.entry_date
          AND COALESCE(r.rate, 0) > 0
        ORDER BY r.rate_date DESC LIMIT 1), 1)
WHERE p.base_debit IS NULL AND p.exchange_rate IS NULL;

UPDATE party_ledger
SET base_debit = ROUND((debit * exchange_rate)::numeric, 4),
    base_credit = ROUND((credit * exchange_rate)::numeric, 4)
WHERE base_debit IS NULL;
