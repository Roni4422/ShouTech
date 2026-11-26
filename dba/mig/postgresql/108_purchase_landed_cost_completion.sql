-- 108 - Landed-cost completion (PostgreSQL): per-charge payee + credit
-- mode, stored inventory/consumed split per allocation, multi-receipt
-- vouchers, and the dedicated Purchase Price Variance account (415).
-- SQLite counterpart: sqlite/108_purchase_landed_cost_completion.sql.

-- L1/L2: per-charge payee (parties.ledger_id; NULL = receipt supplier)
-- and credit mode: 'Payable' (default; voucher creates the liability) or
-- 'Linked' (liability already exists on linked_document_id, a posted
-- supplier document; posting reclasses out of linked_account_code, the
-- account that document originally debited, and creates NO new
-- liability). 'AlreadyBooked' remains in the CHECK only so drafts
-- written by the interim 108 build stay postable.
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS payee_party_id BIGINT;
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS credit_mode TEXT NOT NULL DEFAULT 'Payable'
    CHECK (credit_mode IN ('Payable','AlreadyBooked','Linked'));
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS linked_document_id BIGINT
    REFERENCES purchase_documents(doc_id);
ALTER TABLE purchase_landed_cost_charges ADD COLUMN IF NOT EXISTS linked_account_code TEXT;
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_charges_linked
    ON purchase_landed_cost_charges(linked_document_id);

-- L3: inventory/consumed split stored at post time, replayed by reversal.
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN IF NOT EXISTS inventory_amount NUMERIC;
ALTER TABLE purchase_landed_cost_allocations ADD COLUMN IF NOT EXISTS consumed_amount NUMERIC;

-- L4: every receipt a voucher draws from (header receipt_doc_id remains
-- the primary/first receipt).
CREATE TABLE IF NOT EXISTS purchase_landed_cost_receipts (
    landed_cost_id UUID NOT NULL,
    receipt_doc_id BIGINT NOT NULL,
    PRIMARY KEY (landed_cost_id, receipt_doc_id),
    FOREIGN KEY(landed_cost_id) REFERENCES purchase_landed_costs(landed_cost_id),
    FOREIGN KEY(receipt_doc_id) REFERENCES purchase_documents(doc_id)
);
CREATE INDEX IF NOT EXISTS ix_purchase_landed_cost_receipts_receipt
    ON purchase_landed_cost_receipts(receipt_doc_id, landed_cost_id);

INSERT INTO purchase_landed_cost_receipts (landed_cost_id, receipt_doc_id)
SELECT landed_cost_id, receipt_doc_id FROM purchase_landed_costs
ON CONFLICT DO NOTHING;

-- L5: Purchase Price Variance account (415) for every company carrying
-- the purchase group (account 402), under the same parent as 402.
-- Deterministic account id follows the migration-059 md5 pattern.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT md5(c.company_id::text || ':108:' || '415')::uuid,
       c.company_id,
       '415',
       'Purchase Price Variance',
       'Purchase Price Variance',
       'EXPENSE',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '402'),
       TRUE, TRUE, FALSE
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '402')
ON CONFLICT (company_id, account_code) DO NOTHING;
