-- 095 - Supplier advances (PostgreSQL): a PAY- cash voucher may be
-- flagged as an advance against one purchase order; the advance posts
-- to the new Advances to Suppliers account (1215) instead of AP control
-- (2201), and PayRecWin allocation consumes it (Dr AP 2201 / Cr
-- Advances 1215) only against invoices of that same PO.
-- SQLite counterpart: sqlite/095_supplier_advances.sql.

-- Advance marking on the voucher's party link (migration 061 table).
ALTER TABLE voucher_party_links ADD COLUMN IF NOT EXISTS po_doc_id BIGINT;
ALTER TABLE voucher_party_links ADD COLUMN IF NOT EXISTS is_advance BOOLEAN NOT NULL DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS ix_voucher_party_links_po
    ON voucher_party_links(company_id, po_doc_id) WHERE is_advance;

-- Allocation provenance: source_kind 'PAYMENT' (historical default) or
-- 'ADVANCE' (consumption of a PO advance; advance_po_doc_id = the PO,
-- clearing_journal_id = the allocation transaction's Dr AP / Cr
-- Advances journal entry, reversed exactly on unallocation).
ALTER TABLE payment_allocations ADD COLUMN IF NOT EXISTS source_kind TEXT NOT NULL DEFAULT 'PAYMENT';
ALTER TABLE payment_allocations ADD COLUMN IF NOT EXISTS advance_po_doc_id BIGINT;
ALTER TABLE payment_allocations ADD COLUMN IF NOT EXISTS clearing_journal_id TEXT;

CREATE INDEX IF NOT EXISTS ix_payment_allocations_advance_po
    ON payment_allocations(company_id, advance_po_doc_id) WHERE source_kind = 'ADVANCE';

-- Advances to Suppliers account (1215) for every company carrying the
-- purchase group (account 402), under the same parent as Inventory
-- (1203) in the 12xx current-asset block. New companies receive it from
-- the COA templates (cod/app/src/Resources/COA/*.shc). Deterministic
-- account id follows the migration-059 md5 pattern.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT md5(c.company_id::text || ':095:' || '1215')::uuid,
       c.company_id,
       '1215',
       'Advances to Suppliers',
       'Advances to Suppliers',
       'ASSET',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '1203'),
       TRUE, TRUE, FALSE
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '402')
ON CONFLICT (company_id, account_code) DO NOTHING;
