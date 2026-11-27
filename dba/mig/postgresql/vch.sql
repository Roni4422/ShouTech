-- @shoutech-migration 012_commercial_documents.sql DDB1BE57B700F6BA11EAF55734ACD645E0E0D02950772292F71E9F6C70450E84 DDB1BE57B700F6BA11EAF55734ACD645E0E0D02950772292F71E9F6C70450E84
-- Owns commercial document and reversal schema. Runtime services must not create tables.
        CREATE TABLE IF NOT EXISTS sales_documents (
            doc_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL,
            doc_kind TEXT NOT NULL DEFAULT 'SAL',
            doc_no TEXT NOT NULL,
            doc_date TEXT NOT NULL,
            party_id BIGINT,
            party_name TEXT,
            warehouse_id INT,
            currency_code TEXT NOT NULL DEFAULT 'SAR',
            exchange_rate NUMERIC NOT NULL DEFAULT 1,
            subtotal NUMERIC NOT NULL DEFAULT 0,
            discount_amount NUMERIC NOT NULL DEFAULT 0,
            tax_amount NUMERIC NOT NULL DEFAULT 0,
            grand_total NUMERIC NOT NULL DEFAULT 0,
            paid_amount NUMERIC NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'Draft',
            tax_code TEXT,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            notes TEXT,
            created_by TEXT,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            posted_by TEXT,
            posted_at TIMESTAMPTZ,
            is_deleted INT NOT NULL DEFAULT 0,
            UNIQUE (company_id, doc_kind, doc_no)
        );
        CREATE TABLE IF NOT EXISTS sales_document_lines (
            line_id BIGSERIAL PRIMARY KEY,
            doc_id BIGINT NOT NULL REFERENCES sales_documents(doc_id) ON DELETE CASCADE,
            line_no INT NOT NULL,
            material_id BIGINT,
            material_code TEXT,
            description TEXT NOT NULL,
            unit TEXT,
            qty NUMERIC NOT NULL DEFAULT 0,
            unit_price NUMERIC NOT NULL DEFAULT 0,
            discount_pct NUMERIC NOT NULL DEFAULT 0,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            line_subtotal NUMERIC NOT NULL DEFAULT 0,
            line_tax NUMERIC NOT NULL DEFAULT 0,
            line_total NUMERIC NOT NULL DEFAULT 0,
            warehouse_id INT,
            UNIQUE (doc_id, line_no)
        );
        CREATE TABLE IF NOT EXISTS purchase_documents (
            doc_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL,
            doc_kind TEXT NOT NULL DEFAULT 'PUR',
            doc_no TEXT NOT NULL,
            doc_date TEXT NOT NULL,
            party_id BIGINT,
            party_name TEXT,
            warehouse_id INT,
            currency_code TEXT NOT NULL DEFAULT 'SAR',
            exchange_rate NUMERIC NOT NULL DEFAULT 1,
            subtotal NUMERIC NOT NULL DEFAULT 0,
            discount_amount NUMERIC NOT NULL DEFAULT 0,
            tax_amount NUMERIC NOT NULL DEFAULT 0,
            grand_total NUMERIC NOT NULL DEFAULT 0,
            paid_amount NUMERIC NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'Draft',
            tax_code TEXT,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            notes TEXT,
            created_by TEXT,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            posted_by TEXT,
            posted_at TIMESTAMPTZ,
            is_deleted INT NOT NULL DEFAULT 0,
            UNIQUE (company_id, doc_kind, doc_no)
        );
        CREATE TABLE IF NOT EXISTS purchase_document_lines (
            line_id BIGSERIAL PRIMARY KEY,
            doc_id BIGINT NOT NULL REFERENCES purchase_documents(doc_id) ON DELETE CASCADE,
            line_no INT NOT NULL,
            material_id BIGINT,
            material_code TEXT,
            description TEXT NOT NULL,
            unit TEXT,
            qty NUMERIC NOT NULL DEFAULT 0,
            unit_price NUMERIC NOT NULL DEFAULT 0,
            discount_pct NUMERIC NOT NULL DEFAULT 0,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            line_subtotal NUMERIC NOT NULL DEFAULT 0,
            line_tax NUMERIC NOT NULL DEFAULT 0,
            line_total NUMERIC NOT NULL DEFAULT 0,
            warehouse_id INT,
            UNIQUE (doc_id, line_no)
        );
        CREATE TABLE IF NOT EXISTS party_ledger (
            ledger_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL,
            party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
            party_id BIGINT NOT NULL,
            doc_kind TEXT,
            doc_id BIGINT,
            doc_no TEXT,
            entry_date TEXT NOT NULL,
            debit NUMERIC NOT NULL DEFAULT 0,
            credit NUMERIC NOT NULL DEFAULT 0,
            currency_code TEXT NOT NULL DEFAULT 'SAR',
            note TEXT,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        CREATE INDEX IF NOT EXISTS ix_party_ledger_party
            ON party_ledger(company_id, party_kind, party_id);
        CREATE TABLE IF NOT EXISTS stock_movements (
            stock_movement_id BIGSERIAL PRIMARY KEY,
            company_id TEXT NOT NULL,
            warehouse_id INT,
            product_id BIGINT,
            movement_date TEXT NOT NULL,
            movement_type TEXT NOT NULL,
            document_no TEXT,
            qty_in NUMERIC NOT NULL DEFAULT 0,
            qty_out NUMERIC NOT NULL DEFAULT 0,
            unit_cost NUMERIC NOT NULL DEFAULT 0,
            note TEXT
        );
CREATE TABLE IF NOT EXISTS document_reversals (
    reversal_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    original_doc_kind TEXT NOT NULL,
    original_doc_id BIGINT NOT NULL,
    reversal_doc_kind TEXT NOT NULL,
    reversal_doc_id BIGINT,
    reason TEXT NOT NULL,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL,
    status TEXT NOT NULL,
    UNIQUE (company_id, original_doc_kind, original_doc_id)
);
-- @shoutech-migration 023_invoice_header_fields_postgresql.sql 3776208EBDABA935FB4EC883B5BE6D3C700F09BA81033612767FBB2484513253 3776208EBDABA935FB4EC883B5BE6D3C700F09BA81033612767FBB2484513253
-- Migration 023 (PostgreSQL): expand commercial invoice headers for Phenix-parity fields.

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS receipt_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS reference_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS branch_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS sales_rep_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS cost_center TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS party_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS goods_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS warehouse_name TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS party_code TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS receipt_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS reference_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS branch_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS sales_rep_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS cost_center TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS party_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS goods_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS warehouse_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS party_code TEXT;

ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS warehouse_name TEXT;
ALTER TABLE purchase_document_lines ADD COLUMN IF NOT EXISTS warehouse_name TEXT;

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS original_doc_id BIGINT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS original_doc_kind TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS original_doc_no TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS journal_doc_no TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS original_doc_id BIGINT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS original_doc_kind TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS original_doc_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS journal_doc_no TEXT;
-- @shoutech-migration 024_document_links.sql C90326DBC5D3DF2CDE7E6B1E7F704C4DB6FC6CB8F444D501E9A30F033455CA00 C90326DBC5D3DF2CDE7E6B1E7F704C4DB6FC6CB8F444D501E9A30F033455CA00
-- Owns cross-document chain links (Quotation -> Sales Order -> Delivery Note -> Invoice,
-- and RFQ -> Purchase Order -> Receipt -> Purchase Invoice).
-- A link records that the target document was created from the source document.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS document_links (
    link_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    company_id TEXT NOT NULL,
    source_doc_kind TEXT NOT NULL,
    source_doc_id BIGINT NOT NULL,
    target_doc_kind TEXT NOT NULL,
    target_doc_id BIGINT NOT NULL,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_document_links UNIQUE (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id)
);

CREATE INDEX IF NOT EXISTS ix_document_links_source
    ON document_links(company_id, source_doc_kind, source_doc_id);

CREATE INDEX IF NOT EXISTS ix_document_links_target
    ON document_links(company_id, target_doc_kind, target_doc_id);
-- @shoutech-migration 025_payment_allocations.sql 8925B5764FA5D7B9C154F7899367C34FAE951E2CED3BDFF9338E2BE85FF202D7 8925B5764FA5D7B9C154F7899367C34FAE951E2CED3BDFF9338E2BE85FF202D7
-- PostgreSQL counterpart of sqlite/025_payment_allocations.sql.
-- Owns payment-to-document allocations for the Payment Reconciliation window.

CREATE TABLE IF NOT EXISTS payment_allocations (
    allocation_id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
    party_id BIGINT NOT NULL,
    payment_ledger_id BIGINT NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES','PURCHASE')),
    doc_id BIGINT NOT NULL,
    doc_no TEXT,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    allocated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    note TEXT
);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_payment
    ON payment_allocations(company_id, payment_ledger_id);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_doc
    ON payment_allocations(company_id, doc_table, doc_id);
-- @shoutech-migration 032_document_order_fields.sql 65228B3A43DEC6927FF7DB2D6E74AF50C50282132BDC74C8B27F713D9B14C7E5 65228B3A43DEC6927FF7DB2D6E74AF50C50282132BDC74C8B27F713D9B14C7E5
-- Migration 032 (PostgreSQL): order/quotation scheduling fields for the
-- commercial document engine (ribbon completion, RS-5/RS-6).
-- delivery_date: promised delivery (sales) / required-by (purchase) date.
-- valid_till: quotation validity deadline (ERPNext Quotation pattern);
-- conversion of an expired quotation is blocked in the service layer.
-- Runtime services must not create tables.

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS delivery_date TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS valid_till TEXT;

ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS delivery_date TEXT;
ALTER TABLE purchase_documents ADD COLUMN IF NOT EXISTS valid_till TEXT;

CREATE INDEX IF NOT EXISTS ix_sales_documents_kind_status
    ON sales_documents (company_id, doc_kind, status, doc_date);

CREATE INDEX IF NOT EXISTS ix_purchase_documents_kind_status
    ON purchase_documents (company_id, doc_kind, status, doc_date);
-- @shoutech-migration 033_cheques.sql 0AD7654AEE8C28FB4B20EF4CEE3DF953E4BEDF82D2EB5F40F06FB20C15D6B849 0AD7654AEE8C28FB4B20EF4CEE3DF953E4BEDF82D2EB5F40F06FB20C15D6B849
-- PostgreSQL counterpart of sqlite/033_cheques.sql (Cheques portfolio, RS-4).
-- Money uses NUMERIC (never float) on PostgreSQL; ids are uuid per 026 style.

CREATE TABLE IF NOT EXISTS cheques (
    id uuid PRIMARY KEY,
    company_id TEXT NOT NULL,
    direction TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
    cheque_no TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    branch_name TEXT,
    drawer_name TEXT,
    party_account_code TEXT,
    party_name TEXT,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    exchange_rate NUMERIC NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
    issue_date TEXT,
    due_date TEXT,
    status TEXT NOT NULL DEFAULT 'InPortfolio'
        CHECK (status IN ('InPortfolio','Issued','Deposited','Cleared','Bounced','Endorsed','Cancelled')),
    portfolio_account_code TEXT,
    deposit_bank_account_code TEXT,
    deposit_date TEXT,
    clearance_date TEXT,
    endorsed_to_account_code TEXT,
    bounce_date TEXT,
    bounce_reason TEXT,
    fees_amount NUMERIC NOT NULL DEFAULT 0 CHECK (fees_amount >= 0),
    journal_entry_id TEXT,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_cheques_company_bank_drawer_no
    ON cheques(company_id, bank_name, drawer_name, cheque_no) WHERE is_deleted = FALSE;

CREATE INDEX IF NOT EXISTS ix_cheques_company_status_due
    ON cheques(company_id, status, due_date);
-- @shoutech-migration 035_bank_reconciliation.sql E8F2E8CB6194940CCB02CBF11F7E119275762FA06D0B8028EA7F8132C89BB12C E8F2E8CB6194940CCB02CBF11F7E119275762FA06D0B8028EA7F8132C89BB12C
-- Migration 035 (PostgreSQL): bank reconciliation marks (ribbon completion RS-9).
-- Counterpart of sqlite/035_bank_reconciliation.sql.
--
-- journal_line_key choice: journal_entry_lines HAS a stable surrogate key,
-- journal_entry_line_id (bigint identity on PostgreSQL, INTEGER PRIMARY KEY
-- AUTOINCREMENT on SQLite), unique per line. The mark stores
-- CAST(journal_entry_line_id AS TEXT) as journal_line_key -- no composite
-- fallback is needed.

CREATE TABLE IF NOT EXISTS bank_reconciliation_marks (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    journal_entry_id TEXT NOT NULL,
    journal_line_key TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    cleared_amount DOUBLE PRECISION NOT NULL DEFAULT 0,
    is_cleared INTEGER NOT NULL DEFAULT 1 CHECK (is_cleared IN (0, 1)),
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ,
    UNIQUE (company_id, journal_line_key)
);

CREATE INDEX IF NOT EXISTS ix_bank_rec_marks_account
    ON bank_reconciliation_marks(company_id, account_code, statement_date);
-- @shoutech-migration 063_cheques_bankrec.sql BC67B248E7956BCD6DE084B77A51D6B3E9B536B238EC9D061DA1FBC885ACCCD3 BC67B248E7956BCD6DE084B77A51D6B3E9B536B238EC9D061DA1FBC885ACCCD3
-- ============================================================================
-- 063 - Cheques per-cheque GL link + real bank-statement model (PostgreSQL)
-- PostgreSQL counterpart of sqlite/063_cheques_bankrec.sql (Fix Phase
-- Step 8, sweep cheques-bankrec.md). See the SQLite file for the full
-- design notes: cheques.batch_doc_no / cancel_reason, the
-- bank_statements + bank_statement_lines statement side that makes
-- BankRecWin's Difference a real quantity, marks linked to statement
-- lines, and the one-time copy of legacy bank_accounts rows into the
-- canonical `banks` store (BNKC-01).
-- Idempotent: ADD COLUMN IF NOT EXISTS, CREATE ... IF NOT EXISTS, and
-- the banks copy only inserts codes not already present.
-- ============================================================================

ALTER TABLE cheques ADD COLUMN IF NOT EXISTS batch_doc_no TEXT;
ALTER TABLE cheques ADD COLUMN IF NOT EXISTS cancel_reason TEXT;

CREATE TABLE IF NOT EXISTS bank_statements (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    period_from TEXT,
    period_to TEXT,
    opening_balance NUMERIC NOT NULL DEFAULT 0,
    closing_balance NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closed')),
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_bank_statements_company_account
    ON bank_statements(company_id, account_code, statement_date);

CREATE TABLE IF NOT EXISTS bank_statement_lines (
    id TEXT PRIMARY KEY,
    statement_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    line_date TEXT NOT NULL,
    reference TEXT,
    description TEXT,
    amount NUMERIC NOT NULL DEFAULT 0,
    matched_journal_line_key TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_statement
    ON bank_statement_lines(statement_id, line_date);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_matched
    ON bank_statement_lines(company_id, matched_journal_line_key);

ALTER TABLE bank_reconciliation_marks ADD COLUMN IF NOT EXISTS statement_id TEXT;
ALTER TABLE bank_reconciliation_marks ADD COLUMN IF NOT EXISTS statement_line_id TEXT;

-- Canonical banks: copy any legacy bank_accounts rows into banks.
-- banks.id is uuid on PostgreSQL: derive a stable uuid from the legacy
-- row identity (md5 hex with hyphens inserted).
INSERT INTO banks (id, company_id, code, name_en, branch_name, account_no, iban,
                   currency_code, gl_account_code, is_active)
SELECT (substr(m, 1, 8) || '-' || substr(m, 9, 4) || '-' || substr(m, 13, 4) || '-' ||
        substr(m, 17, 4) || '-' || substr(m, 21, 12))::uuid,
       ba.company_id, ba.bank_code, ba.bank_name,
       NULLIF(ba.branch, ''), NULLIF(ba.account_no, ''), NULLIF(ba.iban, ''),
       ba.currency_code, NULLIF(ba.gl_account_code, ''),
       (ba.is_active <> 0)
FROM bank_accounts ba,
     LATERAL (SELECT md5(ba.company_id || ':' || ba.bank_id::text || ':' || ba.bank_code) AS m) x
WHERE ba.is_deleted = 0
  AND NOT EXISTS (
      SELECT 1 FROM banks b
      WHERE b.company_id = ba.company_id AND b.code = ba.bank_code AND b.is_deleted = false);
-- @shoutech-migration 075_document_number_sequences.sql 8770855A95EA28B4B8A7629C1EBF30456A8EF14CBDB51836D1FA8019DCADAFA1 8770855A95EA28B4B8A7629C1EBF30456A8EF14CBDB51836D1FA8019DCADAFA1
-- Atomic, company-scoped document number allocation.
CREATE TABLE IF NOT EXISTS document_number_sequences (
    company_id TEXT NOT NULL,
    doc_kind TEXT NOT NULL,
    doc_year INTEGER NOT NULL,
    last_value BIGINT NOT NULL CHECK (last_value >= 0),
    PRIMARY KEY (company_id, doc_kind, doc_year)
);
-- @shoutech-migration 077_payment_allocation_company_scope.sql FF62126A67AB0A8E6480ABCC85680C99D98AD0BB59961F87A969AB55DE6A07ED FF62126A67AB0A8E6480ABCC85680C99D98AD0BB59961F87A969AB55DE6A07ED
-- PostgreSQL counterpart of sqlite/077_payment_allocation_company_scope.sql.
-- Preserve the company-code key for party_ledger and the document's own key.
ALTER TABLE payment_allocations
    ADD COLUMN IF NOT EXISTS document_company_id TEXT;

UPDATE payment_allocations
SET document_company_id = CASE
    WHEN doc_table = 'SALES' THEN
        (SELECT d.company_id::text FROM sales_documents d WHERE d.doc_id = payment_allocations.doc_id)
    WHEN doc_table = 'PURCHASE' THEN
        (SELECT d.company_id::text FROM purchase_documents d WHERE d.doc_id = payment_allocations.doc_id)
    ELSE NULL
END
WHERE document_company_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_payment_allocations_document_company
    ON payment_allocations(document_company_id, doc_table, doc_id);
-- @shoutech-migration 079_payment_allocation_reversal_audit.sql 28C2ACE0F9978216FF1B256D1AEB96D529728B3F090744BCF8219037630A953A 28C2ACE0F9978216FF1B256D1AEB96D529728B3F090744BCF8219037630A953A
-- PostgreSQL counterpart of sqlite/079_payment_allocation_reversal_audit.sql.
CREATE TABLE IF NOT EXISTS payment_allocation_reversal_audit (
    audit_id UUID PRIMARY KEY,
    company_id TEXT NOT NULL,
    allocation_id UUID NOT NULL,
    payment_ledger_id BIGINT NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES', 'PURCHASE')),
    doc_id BIGINT NOT NULL,
    document_company_id TEXT NOT NULL,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    note TEXT,
    released_by TEXT NOT NULL,
    reversal_journal_id UUID NOT NULL,
    released_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (company_id, allocation_id)
);

CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_payment
    ON payment_allocation_reversal_audit(company_id, payment_ledger_id);
CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_document
    ON payment_allocation_reversal_audit(company_id, doc_table, doc_id);
-- @shoutech-migration 103_sre_receipts_audit_recon.sql C5EE2EA283F4EF6FD27A19F4C67A4D6969F665A2C62B256AEA290D61ACA3423F C5EE2EA283F4EF6FD27A19F4C67A4D6969F665A2C62B256AEA290D61ACA3423F
-- ============================================================================
-- 103 - Receipts completion (PostgreSQL twin of
-- dba/mig/sqlite/103_sre_receipts_audit_recon.sql — see that file for the
-- full design notes: settlement discounts on payment_allocations gated by
-- SALES.SETTLEMENT.DISCOUNT, cross-currency allocation amounts doc_amount /
-- fx_amount with realized FX posted to the EXISTING template accounts 509
-- Foreign Exchange Losses (loss) and 306 Other Income (gain), settlement
-- discount to 305 Sales Discounts / 405 Purchase Discounts, the unified
-- sau_document_events log including AccessDenied rows, and the sensitive
-- permission seed at display_order 401). Dialect differences only:
-- ADD COLUMN IF NOT EXISTS, ON CONFLICT seed upserts, BOOLEAN flags.
-- ============================================================================

-- ---- Allocation: settlement discount + cross-currency amounts --------------
ALTER TABLE payment_allocations ADD COLUMN IF NOT EXISTS discount_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE payment_allocations ADD COLUMN IF NOT EXISTS doc_amount NUMERIC;
ALTER TABLE payment_allocations ADD COLUMN IF NOT EXISTS fx_amount NUMERIC NOT NULL DEFAULT 0;

-- ---- Unified document events (sau) ------------------------------------------
CREATE TABLE IF NOT EXISTS sau_document_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_side TEXT NOT NULL DEFAULT 'SALES' CHECK (doc_side IN ('SALES','PURCHASE')),
    doc_kind TEXT NOT NULL DEFAULT '',
    doc_id BIGINT NOT NULL DEFAULT 0,
    doc_no TEXT,
    event_type TEXT NOT NULL CHECK (event_type IN ('Created','Modified','Submitted','Approved','Rejected','Posted','Reversed','Cancelled','Allocated','AccessDenied')),
    actor TEXT NOT NULL DEFAULT '',
    details TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_sau_document_events_doc
    ON sau_document_events(company_id, doc_side, doc_kind, doc_id, created_at);
CREATE INDEX IF NOT EXISTS ix_sau_document_events_company_type
    ON sau_document_events(company_id, event_type, created_at);

-- ---- Permission: SALES.SETTLEMENT.DISCOUNT (sensitive) -----------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.SETTLEMENT.DISCOUNT','Sales Settlement - Discount','SALES','SETTLEMENT','DISCOUNT','تسوية المبيعات - خصم تسوية','Sales Settlement - Discount','Grant a settlement (early-payment) discount while allocating a receipt or payment (sales spec section 7, migration 103)',401,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.SETTLEMENT.DISCOUNT') ON CONFLICT DO NOTHING;
-- @shoutech-migration 110_supplier_advances.sql 5D189B0A9CB5DF3083FF98840AB7777035A8C0B8BE6FB8C94F4138C92E1F8804 5D189B0A9CB5DF3083FF98840AB7777035A8C0B8BE6FB8C94F4138C92E1F8804
-- 110 - Supplier advances (PostgreSQL): a PAY- cash voucher may be
-- flagged as an advance against one purchase order; the advance posts
-- to the new Advances to Suppliers account (1215) instead of AP control
-- (2201), and PayRecWin allocation consumes it (Dr AP 2201 / Cr
-- Advances 1215) only against invoices of that same PO.
-- SQLite counterpart: sqlite/110_supplier_advances.sql.

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
SELECT md5(c.company_id::text || ':110:' || '1215')::uuid,
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
-- @shoutech-migration 116_doc_designs.sql 3B774F13289825218223981140B35202D4984155D46AFC5AC5F3F22355E203BA 3B774F13289825218223981140B35202D4984155D46AFC5AC5F3F22355E203BA
-- 116 - Invoice Designer store (services phase B3).
-- See the SQLite companion for the design note. Flag columns are
-- INTEGER (0/1): the app filters IsDeleted = 0 / IsDefault = 1 on
-- both providers.

CREATE TABLE IF NOT EXISTS doc_designs (
    DesignID INTEGER GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    CompanyID TEXT NOT NULL,
    DocKind TEXT NOT NULL,
    DesignName TEXT NOT NULL,
    IsDefault INTEGER NOT NULL DEFAULT 0,
    PayloadJson TEXT NOT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    UpdatedBy TEXT,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    UpdatedAt TIMESTAMP,
    DeletedAt TIMESTAMP,
    DeletedBy TEXT
);

CREATE INDEX IF NOT EXISTS ix_doc_designs_company_kind
    ON doc_designs(CompanyID, DocKind, IsDeleted);
-- @shoutech-migration 120_invoice_line_notes.sql AA4DA638D908987C1171C114E436438617AA2157A98E44388E8261442CCFE134 AA4DA638D908987C1171C114E436438617AA2157A98E44388E8261442CCFE134
-- 120 - Per-line notes on commercial documents (invoice family
-- batch INV1, owner reference 2026-11-24 16:06: the Phenix invoice
-- grid carries a line-notes column). One design for all invoice
-- types: the notes ride on both line tables and surface in
-- InvoiceWin as an optional column (column chooser, per-user).

-- @if-column-missing sales_document_lines.notes
ALTER TABLE sales_document_lines ADD COLUMN notes TEXT;
-- @if-column-missing purchase_document_lines.notes
ALTER TABLE purchase_document_lines ADD COLUMN notes TEXT;
-- @shoutech-migration 123_number_series_config.sql F5A26D1D8A75E9408A308283A27E36C615F8DC7AF6D08BCDA1FC5CD8F566CFC5 F5A26D1D8A75E9408A308283A27E36C615F8DC7AF6D08BCDA1FC5CD8F566CFC5
-- 123: per-series numbering configuration (NUMBERING_STUDY §3.4).
-- One row per (company, series): printed prefix, digit padding, yearly
-- segmentation and the start floor. No row = built-in defaults. The
-- counters themselves stay in document_number_sequences (migration 075);
-- configuration never renumbers history and never moves a counter back.
CREATE TABLE IF NOT EXISTS number_series_config (
    company_id     TEXT    NOT NULL,
    series_key     TEXT    NOT NULL,
    prefix         TEXT    NULL,
    padding        INTEGER NULL,
    yearly_segment INTEGER NOT NULL DEFAULT 1,
    start_value    BIGINT  NOT NULL DEFAULT 1,
    updated_by     TEXT    NULL,
    updated_at     TEXT    NULL,
    PRIMARY KEY (company_id, series_key)
);
-- @shoutech-migration 125_print_settings_logo.sql 521F8945EEBC8FF36B9B0C273627CCA93F45C5E2B7EB2B5AD1941CC280010F54 521F8945EEBC8FF36B9B0C273627CCA93F45C5E2B7EB2B5AD1941CC280010F54
-- 125 - Print settings + company logo (docs/OPTWIN_SECTIONS.md §3.3,
-- Landing 2). One row per company: which company-profile fields the
-- print header block carries, the free-text print header, the
-- balance/user lines on printed invoices, and the default printer.
-- The company logo itself lives on the companies row (BYTEA) so it
-- travels with the company database on both providers.
CREATE TABLE IF NOT EXISTS print_settings (
    company_id            TEXT    NOT NULL PRIMARY KEY,
    header_name           INTEGER NOT NULL DEFAULT 1,
    header_address        INTEGER NOT NULL DEFAULT 1,
    header_phone          INTEGER NOT NULL DEFAULT 0,
    header_mobile         INTEGER NOT NULL DEFAULT 0,
    header_email          INTEGER NOT NULL DEFAULT 0,
    header_website        INTEGER NOT NULL DEFAULT 0,
    header_license        INTEGER NOT NULL DEFAULT 0,
    header_tax_number     INTEGER NOT NULL DEFAULT 0,
    header_text           TEXT    NULL,
    print_previous_balance INTEGER NOT NULL DEFAULT 0,
    print_current_balance  INTEGER NOT NULL DEFAULT 0,
    print_user_on_invoice  INTEGER NOT NULL DEFAULT 0,
    default_printer       TEXT    NULL,
    updated_by            TEXT    NULL,
    updated_at            TEXT    NULL
);

-- @if-column-missing companies.logo_data
ALTER TABLE companies ADD COLUMN logo_data BYTEA;
-- @if-column-missing companies.logo_media_type
ALTER TABLE companies ADD COLUMN logo_media_type VARCHAR(100);
