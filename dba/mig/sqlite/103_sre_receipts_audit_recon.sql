-- ============================================================================
-- 103 - Receipts completion: settlement discounts, cross-currency allocation
-- with realized FX, unified document events + denied-access logging (SQLite).
-- Sales cycle W8 (Sales_Cycle.txt sections 7, 14, 15; sre = Sales Receipts,
-- sau = Sales Audit prefixes, spec section 18).
--
--   1. SETTLEMENT DISCOUNTS at allocation: payment_allocations gains
--      discount_amount (DOCUMENT currency). Allocating a receipt may settle
--      an invoice by payment + discount; the discount posts Dr 305 Sales
--      Discounts / Cr 1201 Receivable (suppliers: Dr 2201 / Cr 405 Purchase
--      Discounts) in the allocation journal. Gated by the new sensitive
--      permission SALES.SETTLEMENT.DISCOUNT (this migration).
--   2. CROSS-CURRENCY ALLOCATION: payment_allocations gains doc_amount
--      (the amount settled on the document, in DOCUMENT currency; NULL on
--      legacy rows where it equals amount) and fx_amount (the realized FX
--      difference in BASE currency, signed: positive = loss debited to 509,
--      negative = gain credited to 306 Other Income). The hard currency
--      block in ReconciliationRepository is removed in code.
--      COA DECISION (W8, verified against every seeded template in
--      cod/app/src/Resources/COA/*.shc): all templates already carry
--        305 Sales Discounts (postable, debit-balance contra revenue),
--        405 Purchase Discounts (postable, credit-balance),
--        509 Foreign Exchange Losses (postable expense),
--        306 Other Income (postable revenue).
--      NO template carries a dedicated FX-gain account, so realized gains
--      credit 306 Other Income and losses debit 509 — both EXISTING
--      accounts; nothing is seeded into the chart.
--   3. DOCUMENT EVENTS (sau_document_events): the unified event log for
--      sales/purchase documents — Created/Modified/Submitted/Approved/
--      Rejected/Posted/Reversed/Cancelled/Allocated plus AccessDenied
--      (denied-access logging rides this table: one audit surface, the
--      event_type discriminates; a separate denials table would split the
--      per-document history the DocSearchWin viewer reads). doc_side
--      discriminates SALES vs PURCHASE because doc_id is only unique
--      within its own document table. company_id carries the DOCUMENT's
--      company key (Guid text for chain/native documents, matching
--      sau_approval_history); AccessDenied rows written before a document
--      context exists carry the company CODE.
--   4. Permission SALES.SETTLEMENT.DISCOUNT (sensitive; display_order 401
--      continues after migration 100's 394 / 098's 394-400 block — the
--      highest seeded order in the chain is 400).
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, column adds guarded by
-- the migrator directive, permission seeds INSERT OR IGNORE + UPDATE;
-- the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Allocation: settlement discount + cross-currency amounts --------------
-- @if-column-missing payment_allocations.discount_amount
ALTER TABLE payment_allocations ADD COLUMN discount_amount NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing payment_allocations.doc_amount
ALTER TABLE payment_allocations ADD COLUMN doc_amount NUMERIC;
-- @if-column-missing payment_allocations.fx_amount
ALTER TABLE payment_allocations ADD COLUMN fx_amount NUMERIC NOT NULL DEFAULT 0;

-- ---- Unified document events (sau) ------------------------------------------
CREATE TABLE IF NOT EXISTS sau_document_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_side TEXT NOT NULL DEFAULT 'SALES' CHECK (doc_side IN ('SALES','PURCHASE')),
    doc_kind TEXT NOT NULL DEFAULT '',
    doc_id INTEGER NOT NULL DEFAULT 0,
    doc_no TEXT,
    event_type TEXT NOT NULL CHECK (event_type IN ('Created','Modified','Submitted','Approved','Rejected','Posted','Reversed','Cancelled','Allocated','AccessDenied')),
    actor TEXT NOT NULL DEFAULT '',
    details TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_sau_document_events_doc
    ON sau_document_events(company_id, doc_side, doc_kind, doc_id, created_at);
CREATE INDEX IF NOT EXISTS ix_sau_document_events_company_type
    ON sau_document_events(company_id, event_type, created_at);

-- ---- Permission: SALES.SETTLEMENT.DISCOUNT (sensitive) -----------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.SETTLEMENT.DISCOUNT','Sales Settlement - Discount','SALES','SETTLEMENT','DISCOUNT','تسوية المبيعات - خصم تسوية','Sales Settlement - Discount','Grant a settlement (early-payment) discount while allocating a receipt or payment (sales spec section 7, migration 103)',401,1);
UPDATE permissions SET group_code='SALES',feature_code='SETTLEMENT',operation_code='DISCOUNT',name_ar='تسوية المبيعات - خصم تسوية',name_en='Sales Settlement - Discount',display_order=401,is_sensitive=1 WHERE permission_code='SALES.SETTLEMENT.DISCOUNT';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.SETTLEMENT.DISCOUNT');
