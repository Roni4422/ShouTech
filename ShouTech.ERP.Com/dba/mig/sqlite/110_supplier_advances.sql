-- 110 - Supplier advances (SQLite): a PAY- cash voucher may be flagged as
-- an advance against one purchase order; the advance posts to the new
-- Advances to Suppliers account (1215) instead of AP control (2201), and
-- PayRecWin allocation consumes it (Dr AP 2201 / Cr Advances 1215) only
-- against invoices of that same PO.
-- PostgreSQL counterpart: postgresql/110_supplier_advances.sql.

-- Advance marking on the voucher's party link (migration 061 table):
-- po_doc_id = purchase_documents.doc_id of the PO the advance belongs to;
-- is_advance = 1 only for PAY links flagged as advances at draft save.
-- @if-column-missing voucher_party_links.po_doc_id
ALTER TABLE voucher_party_links ADD COLUMN po_doc_id INTEGER;
-- @if-column-missing voucher_party_links.is_advance
ALTER TABLE voucher_party_links ADD COLUMN is_advance INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_voucher_party_links_po
    ON voucher_party_links(company_id, po_doc_id) WHERE is_advance = 1;

-- Allocation provenance: an allocation row now names its source.
-- 'PAYMENT' = a normal party-ledger payment (the historical default);
-- 'ADVANCE' = consumption of a PO advance: advance_po_doc_id carries the
-- PO the advance belongs to and clearing_journal_id the allocation
-- transaction's Dr AP / Cr Advances journal entry, so the allocation can
-- be reversed exactly (advance restored, clearing entry mirrored).
-- @if-column-missing payment_allocations.source_kind
ALTER TABLE payment_allocations ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'PAYMENT';
-- @if-column-missing payment_allocations.advance_po_doc_id
ALTER TABLE payment_allocations ADD COLUMN advance_po_doc_id INTEGER;
-- @if-column-missing payment_allocations.clearing_journal_id
ALTER TABLE payment_allocations ADD COLUMN clearing_journal_id TEXT;

CREATE INDEX IF NOT EXISTS ix_payment_allocations_advance_po
    ON payment_allocations(company_id, advance_po_doc_id) WHERE source_kind = 'ADVANCE';

-- Advances to Suppliers account (1215), seeded into every existing
-- company that carries the purchase group (account 402), under the same
-- parent as Inventory (1203) in the 12xx current-asset block (1201 AR,
-- 1203 Inventory, 1205 Input VAT; 1210-1214 are manufacturing inventory
-- accounts in the industrial templates, so 1215 is the first code free
-- in every template). New companies receive it from the COA templates
-- (cod/app/src/Resources/COA/*.shc). Deterministic per-company account
-- id is not required on SQLite; the random GUID form matches
-- migration 059.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '1215',
       'Advances to Suppliers',
       'Advances to Suppliers',
       'ASSET',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '1203'),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '402')
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts a
                  WHERE a.company_id = c.company_id AND a.account_code = '1215');
