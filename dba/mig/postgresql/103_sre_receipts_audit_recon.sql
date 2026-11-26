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
