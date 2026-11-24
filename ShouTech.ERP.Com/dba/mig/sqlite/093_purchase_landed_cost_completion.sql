-- 093 - Landed-cost completion (SQLite): per-charge payee + credit mode,
-- stored inventory/consumed split per allocation, multi-receipt vouchers,
-- and the dedicated Purchase Price Variance account (415).
-- PostgreSQL counterpart: postgresql/093_purchase_landed_cost_completion.sql.

-- L1/L2: a charge may be owed to a payee party other than the receipt
-- supplier (payee_party_id = parties.ledger_id; NULL = receipt supplier),
-- and may already be booked elsewhere (credit_mode = 'AlreadyBooked':
-- posting credits the charge's own expense account and writes no
-- party-ledger entry). Default 'Payable' preserves the pre-093 behaviour.
ALTER TABLE purchase_landed_cost_charges ADD COLUMN payee_party_id INTEGER;
ALTER TABLE purchase_landed_cost_charges ADD COLUMN credit_mode TEXT NOT NULL DEFAULT 'Payable'
    CHECK (credit_mode IN ('Payable','AlreadyBooked'));

-- L3: the inventory/consumed split computed at post time is stored on the
-- allocation itself; reversal vouchers copy it (negated) and posting
-- replays it exactly instead of recomputing from post-reversal stock.
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN inventory_amount NUMERIC;
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN consumed_amount NUMERIC;

-- L4: a voucher may draw lines from several receipts. The header keeps
-- receipt_doc_id as the primary (first) receipt for compatibility; this
-- link table carries every receipt the voucher draws from.
CREATE TABLE IF NOT EXISTS purchase_landed_cost_receipts (
    landed_cost_id TEXT NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    PRIMARY KEY (landed_cost_id, receipt_doc_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(receipt_doc_id) REFERENCES purchase_documents(doc_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_receipts_receipt
    ON purchase_landed_cost_receipts(receipt_doc_id, landed_cost_id);

-- Backfill the link table for vouchers created before this migration.
INSERT OR IGNORE INTO purchase_landed_cost_receipts (landed_cost_id, receipt_doc_id)
SELECT landed_cost_id, receipt_doc_id FROM purchase_landed_costs;

-- L5: dedicated Purchase Price Variance account (415), seeded into every
-- existing company that carries the purchase group (account 402), under
-- the same parent as 402. New companies receive it from the COA templates
-- (cod/app/src/Resources/COA/*.shc). Deterministic per-company account id
-- is not required on SQLite; the random GUID form matches migration 059.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '415',
       'Purchase Price Variance',
       'Purchase Price Variance',
       'EXPENSE',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '402'),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '402')
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts a
                  WHERE a.company_id = c.company_id AND a.account_code = '415');
