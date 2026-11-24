-- ============================================================================
-- 100 - Sales returns completion (SQLite)
--
-- Owner sales spec section 6 (Sales_Cycle.txt), workstream W6: the SRT
-- document gains the pieces Part I did not build —
--   1. a RETURN REASONS master (srt_return_reasons) with a default
--      condition routing per reason; every SRT carries a header reason
--      code (sales_documents.return_reason_code) and an optional
--      per-line override (sales_document_lines.return_reason_code).
--   2. CONDITION ROUTING per return line
--      (sales_document_lines.return_condition: Good / Quarantine /
--      Damaged). Good re-enters available stock (existing behaviour);
--      Quarantine lands in the company's quarantine warehouse
--      (migration 056 mechanism, QualityGate.EnsureQuarantineWarehouse);
--      Damaged NEVER re-enters stock.
--   3. an INSPECTION step: sales_documents.inspection_status
--      (NotRequired / PendingInspection / Inspected) + inspector stamp.
--      A return whose lines include a Quarantine or Damaged line — or
--      whose total reaches the company threshold below — cannot post
--      while PendingInspection (SAL-7022).
--   4. an APPROVAL workflow: sales_documents.approval_status
--      (NotRequired / PendingApproval / Approved / Rejected) +
--      approver stamp + rejection_reason, gated by the per-company
--      flag in srt_return_settings (default OFF — existing flows are
--      unaffected) and the SALES.RETURN.APPROVE permission.
--   5. NO-INVOICE returns: sales_documents.no_invoice = 1 flags a
--      walk-in return with no source document (permitted only with
--      SALES.RETURN.APPROVE); return_source_kind records the source
--      shape of every new SRT: 'INV' (invoice / POS sale), 'DLV'
--      (uninvoiced delivery), 'NONE' (no-invoice). Legacy rows keep
--      NULL and are treated as 'INV' when they carry an original, else
--      'NONE'-free legacy behaviour (no new gates apply to them beyond
--      the quantity rule, which already existed).
--   7. REMAINING-QUANTITY enforcement is completed in code: the
--      cumulative check that already ran for invoice sources now also
--      runs for delivery sources and raises the cataloged SAL-7021
--      (ReturnQuantityExceeded) instead of a plain message.
--
-- DAMAGED-GOODS GL TREATMENT (chosen, W6): a Damaged line posts NO
-- cost-side entry at all — no stock movement, no Dr Inventory, and NO
-- credit back to COGS. The original sale's COGS expense therefore
-- STANDS as the write-off loss of the worthless goods. This keeps the
-- journal balanced (the revenue side — Dr Sales Returns / Cr
-- Receivable — is unaffected) and keeps Inventory GL == stock
-- valuation trivially, because neither side moves. The alternative
-- (Dr a damaged/write-off expense / Cr COGS) would reclassify the same
-- loss out of COGS and flatter the original sale's gross margin; the
-- loss belongs to the sale that shipped the goods. A company that
-- wants the reclassification can still book it as a manual journal.
--
-- Conventions mirror 056/097: company keying is the company CODE
-- (TEXT) — the stock / party world. Reason seeds are GLOBAL rows
-- (company_id = '') shared by every company, exactly like the seeded
-- chart templates; a company may add its own rows under its code,
-- which shadow a global row with the same code. Names follow the
-- master-data pattern (name_en is the English source; ar/fr/tr
-- carried alongside, quality_parameters pattern extended to 4 langs).
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, column adds guarded
-- by the migrator directive, permission seeds INSERT OR IGNORE +
-- UPDATE, reason seeds INSERT OR IGNORE; the checksum ledger skips a
-- re-run.
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
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_srt_return_reasons_company_code
    ON srt_return_reasons(company_id, code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_srt_return_reasons_company
    ON srt_return_reasons(company_id, is_active) WHERE is_deleted = 0;

-- Global default reasons (English source). Defective / Expired route
-- to Quarantine by default (they need a disposition decision);
-- DamagedInTransit is Damaged (never stock); the rest are Good.
INSERT OR IGNORE INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-defective', '', 'DEFECTIVE', 'Defective', 'معيب', 'Défectueux', 'Kusurlu', 'Quarantine');
INSERT OR IGNORE INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-wrongitem', '', 'WRONGITEM', 'Wrong Item', 'صنف خاطئ', 'Article incorrect', 'Yanlış Ürün', 'Good');
INSERT OR IGNORE INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-changedmind', '', 'CHANGEDMIND', 'Customer Changed Mind', 'غيّر العميل رأيه', 'Changement d''avis du client', 'Müşteri Fikrini Değiştirdi', 'Good');
INSERT OR IGNORE INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-damagedtransit', '', 'DAMAGEDTRANSIT', 'Damaged In Transit', 'تالف أثناء النقل', 'Endommagé pendant le transport', 'Nakliye Sırasında Hasarlı', 'Damaged');
INSERT OR IGNORE INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-expired', '', 'EXPIRED', 'Expired', 'منتهي الصلاحية', 'Expiré', 'Süresi Dolmuş', 'Quarantine');
INSERT OR IGNORE INTO srt_return_reasons(reason_id, company_id, code, name_en, name_ar, name_fr, name_tr, default_condition)
VALUES ('srt-reason-other', '', 'OTHER', 'Other', 'أخرى', 'Autre', 'Diğer', 'Good');

-- ---- Per-company return settings (quality_settings pattern, 056) -----------
CREATE TABLE IF NOT EXISTS srt_return_settings (
    company_id TEXT NOT NULL PRIMARY KEY,
    approval_required INTEGER NOT NULL DEFAULT 0,
    inspection_amount_threshold NUMERIC,
    updated_at TEXT,
    updated_by TEXT
);

-- ---- SRT header workflow columns --------------------------------------------
-- @if-column-missing sales_documents.return_reason_code
ALTER TABLE sales_documents ADD COLUMN return_reason_code TEXT;
-- @if-column-missing sales_documents.return_source_kind
ALTER TABLE sales_documents ADD COLUMN return_source_kind TEXT;
-- @if-column-missing sales_documents.no_invoice
ALTER TABLE sales_documents ADD COLUMN no_invoice INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing sales_documents.inspection_status
ALTER TABLE sales_documents ADD COLUMN inspection_status TEXT NOT NULL DEFAULT 'NotRequired';
-- @if-column-missing sales_documents.inspected_by
ALTER TABLE sales_documents ADD COLUMN inspected_by TEXT;
-- @if-column-missing sales_documents.inspected_at
ALTER TABLE sales_documents ADD COLUMN inspected_at TEXT;
-- @if-column-missing sales_documents.approval_status
ALTER TABLE sales_documents ADD COLUMN approval_status TEXT NOT NULL DEFAULT 'NotRequired';
-- @if-column-missing sales_documents.approved_by
ALTER TABLE sales_documents ADD COLUMN approved_by TEXT;
-- @if-column-missing sales_documents.approved_at
ALTER TABLE sales_documents ADD COLUMN approved_at TEXT;
-- @if-column-missing sales_documents.rejection_reason
ALTER TABLE sales_documents ADD COLUMN rejection_reason TEXT;

-- ---- SRT line routing columns -------------------------------------------------
-- @if-column-missing sales_document_lines.return_condition
ALTER TABLE sales_document_lines ADD COLUMN return_condition TEXT NOT NULL DEFAULT 'Good';
-- @if-column-missing sales_document_lines.return_reason_code
ALTER TABLE sales_document_lines ADD COLUMN return_reason_code TEXT;

-- ---- Permission: SALES.RETURN.APPROVE (097 pattern; display_order after 393) --
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RETURN.APPROVE','Sales Return - Approve','SALES','RETURN','APPROVE','مرتجعات المبيعات - اعتماد','Sales Return - Approve','Approve or reject a sales return before posting, and authorize no-invoice (walk-in) returns (sales spec section 6, migration 100)',394,1);
UPDATE permissions SET group_code='SALES',feature_code='RETURN',operation_code='APPROVE',name_ar='مرتجعات المبيعات - اعتماد',name_en='Sales Return - Approve',display_order=394,is_sensitive=1 WHERE permission_code='SALES.RETURN.APPROVE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.RETURN.APPROVE');
