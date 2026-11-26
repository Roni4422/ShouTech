-- ============================================================================
-- 100 - Sales returns completion (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/100_srt_returns_completion.sql —
-- see that file for the full design notes (return reasons master,
-- condition routing, inspection step, approval workflow, no-invoice
-- and delivery-sourced returns, SAL-7021 enforcement, and the chosen
-- damaged-goods GL treatment: no cost-side entry for Damaged lines, so
-- the original COGS stands as the write-off loss and Inventory GL ==
-- stock valuation). Dialect differences only: BOOLEAN flags,
-- TIMESTAMPTZ audit stamps, ADD COLUMN IF NOT EXISTS, and
-- ON CONFLICT seed upserts.
-- ============================================================================

-- ---- Return reasons master -------------------------------------------------
CREATE TABLE IF NOT EXISTS srt_return_reasons (
    reason_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL DEFAULT '',
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    name_fr TEXT NOT NULL DEFAULT '',
    name_tr TEXT NOT NULL DEFAULT '',
    default_condition TEXT NOT NULL DEFAULT 'Good'
        CHECK (default_condition IN ('Good','Quarantine','Damaged')),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_srt_return_reasons_company_code
    ON srt_return_reasons(company_id, code) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_srt_return_reasons_company
    ON srt_return_reasons(company_id, is_active) WHERE is_deleted = FALSE;

INSERT INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-defective', '', 'DEFECTIVE', 'Defective', 'معيب', 'Défectueux', 'Kusurlu', 'Quarantine')
ON CONFLICT (reason_id) DO NOTHING;
INSERT INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-wrongitem', '', 'WRONGITEM', 'Wrong Item', 'صنف خاطئ', 'Article incorrect', 'Yanlış Ürün', 'Good')
ON CONFLICT (reason_id) DO NOTHING;
INSERT INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-changedmind', '', 'CHANGEDMIND', 'Customer Changed Mind', 'غيّر العميل رأيه', 'Changement d''avis du client', 'Müşteri Fikrini Değiştirdi', 'Good')
ON CONFLICT (reason_id) DO NOTHING;
INSERT INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-damagedtransit', '', 'DAMAGEDTRANSIT', 'Damaged In Transit', 'تالف أثناء النقل', 'Endommagé pendant le transport', 'Nakliye Sırasında Hasarlı', 'Damaged')
ON CONFLICT (reason_id) DO NOTHING;
INSERT INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-expired', '', 'EXPIRED', 'Expired', 'منتهي الصلاحية', 'Expiré', 'Süresi Dolmuş', 'Quarantine')
ON CONFLICT (reason_id) DO NOTHING;
INSERT INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-other', '', 'OTHER', 'Other', 'أخرى', 'Autre', 'Diğer', 'Good')
ON CONFLICT (reason_id) DO NOTHING;

-- ---- Per-company return settings --------------------------------------------
CREATE TABLE IF NOT EXISTS srt_return_settings (
    company_id TEXT NOT NULL PRIMARY KEY,
    approval_required BOOLEAN NOT NULL DEFAULT FALSE,
    inspection_amount_threshold NUMERIC,
    updated_at TIMESTAMPTZ,
    updated_by TEXT
);

-- ---- SRT header workflow columns --------------------------------------------
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS return_reason_code TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS return_source_kind TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS no_invoice BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS inspection_status TEXT NOT NULL DEFAULT 'NotRequired';
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS inspected_by TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS inspected_at TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS approval_status TEXT NOT NULL DEFAULT 'NotRequired';
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS approved_by TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS approved_at TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS rejection_reason TEXT;

-- ---- SRT line routing columns -------------------------------------------------
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS return_condition TEXT NOT NULL DEFAULT 'Good';
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS return_reason_code TEXT;

-- ---- Permission: SALES.RETURN.APPROVE -----------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RETURN.APPROVE','Sales Return - Approve','SALES','RETURN','APPROVE','مرتجعات المبيعات - اعتماد','Sales Return - Approve','Approve or reject a sales return before posting, and authorize no-invoice (walk-in) returns (sales spec section 6, migration 100)',394,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.RETURN.APPROVE') ON CONFLICT DO NOTHING;
