-- ============================================================================
-- 104 - POS remainder (SQLite) — sales cycle W9, owner spec §6 remainder
--
--   * pos_session_expenses: cash paid OUT of the drawer during an open
--     session (amount, reason, optional reference), recorded by a
--     POS.EXPENSE.MANAGE holder. Sessions post NO cash journal at
--     close today (066: CloseSessionAsync computes expected cash from
--     tenders and mirrors the legacy pos_cash_closings register), so a
--     paid-out follows that same reconciliation model — it is a
--     session record that SUBTRACTS from the expected cash in the Z
--     report and at close; no parallel GL posting is invented.
--   * pos_discount_limits: the NUMERIC cashier discount ceiling —
--     max discount % per USER (subject_key = username) or per ROLE
--     (subject_key = role_code). Resolution at the till: the USER row
--     wins, else the highest ROLE row among the cashier's roles, else
--     no numeric cap (the permission gate alone applies, as before).
--     Neither UsrPref (a per-machine UI preference file a cashier can
--     edit) nor the permissions catalog (boolean grants, no numeric
--     metadata) can own an enforced security limit, so the limits live
--     in this POS-owned table next to pos_settings (066).
--   * pos_discount_overrides: audit of a supervisor override — the
--     cashier, the approving supervisor (a POS.DISCOUNT.OVERRIDE
--     holder who authenticated at the till), the limit, the effective
--     and max-line discount %, and the discount amount, per document.
--     spr_price_overrides (099) does NOT fit: it is a per-line /
--     per-rule pricing-guard audit (SAL-7018/7019/7020) whose actor is
--     the poster and which has no approver distinct from the actor;
--     a POS discount override is a document-level, two-person event.
--   * sales_documents.exchange_ref: a POS exchange posts TWO existing
--     documents (an SRT return + a POS sale, each through the verified
--     InvRepo path in its own transaction); both carry the same
--     exchange_ref so the pair is one auditable flow.
--   * Permission POS.EXPENSE.MANAGE (sensitive; administrators by
--     default, grantable per user like the other POS rights).
--
-- Company is keyed by CODE (TEXT) — the party/POS world, like 066.
-- Idempotent: the checksum ledger skips a re-run; inside the file
-- every CREATE is IF NOT EXISTS, the ALTER is guarded by the migrator
-- directive, and permission seeds are INSERT OR IGNORE + UPDATE.
-- ============================================================================

-- ---- Session paid-outs -------------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_session_expenses (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES pos_sessions(id),
    expense_no TEXT NOT NULL,
    amount REAL NOT NULL DEFAULT 0,
    reason TEXT NOT NULL DEFAULT '',
    reference_no TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_session_expenses_company_no
    ON pos_session_expenses(company_id, expense_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_pos_session_expenses_session
    ON pos_session_expenses(company_id, session_id) WHERE is_deleted = 0;

-- ---- Numeric cashier discount limits ------------------------------------------
CREATE TABLE IF NOT EXISTS pos_discount_limits (
    company_id TEXT NOT NULL,
    subject_kind TEXT NOT NULL CHECK (subject_kind IN ('USER','ROLE')),
    subject_key TEXT NOT NULL,
    max_discount_pct REAL NOT NULL DEFAULT 0,
    updated_by TEXT,
    updated_at TEXT,
    PRIMARY KEY (company_id, subject_kind, subject_key)
);

-- ---- Discount override audit ----------------------------------------------------
CREATE TABLE IF NOT EXISTS pos_discount_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    doc_id INTEGER,
    cashier TEXT NOT NULL,
    approved_by TEXT NOT NULL,
    limit_pct REAL NOT NULL DEFAULT 0,
    effective_pct REAL NOT NULL DEFAULT 0,
    max_line_pct REAL NOT NULL DEFAULT 0,
    discount_amount REAL NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_pos_discount_overrides_doc
    ON pos_discount_overrides(company_id, doc_kind, doc_no);

-- ---- Exchange link ----------------------------------------------------------------
-- @if-column-missing sales_documents.exchange_ref
ALTER TABLE sales_documents ADD COLUMN exchange_ref TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_exchange_ref
    ON sales_documents(company_id, exchange_ref) WHERE is_deleted = 0;

-- ---- Permission: POS.EXPENSE.MANAGE -------------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('POS.EXPENSE.MANAGE','POS Expense - Manage','POS','EXPENSE','MANAGE','مصروفات نقاط البيع - إدارة','POS Expense - Manage','Record cash paid out of the drawer during an open POS session (sales cycle W9, migration 104)',401,1);
UPDATE permissions SET group_code='POS',feature_code='EXPENSE',operation_code='MANAGE',name_ar='مصروفات نقاط البيع - إدارة',name_en='POS Expense - Manage',display_order=401,is_sensitive=1 WHERE permission_code='POS.EXPENSE.MANAGE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('POS.EXPENSE.MANAGE');
