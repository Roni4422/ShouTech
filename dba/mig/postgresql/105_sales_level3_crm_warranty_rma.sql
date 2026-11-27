-- ============================================================================
-- 105 - Sales cycle Level 3 (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/105_sales_level3_crm_warranty_rma.sql —
-- see that file for the full design notes (CRM campaigns + campaign_id
-- linkage, swa_ warranties and claims deriving from sold serials of
-- migration 054, srt_rma_requests riding the W6 return machinery with
-- SAL-7025 / SAL-7026, SALES.WARRANTY.* + CRM.CAMPAIGN.* permissions).
-- Dialect differences only: BOOLEAN flags, TIMESTAMPTZ audit stamps,
-- BIGSERIAL surrogate keys, ADD COLUMN IF NOT EXISTS, and ON CONFLICT
-- seed upserts.
-- ============================================================================

-- ---- CRM campaigns -----------------------------------------------------------
CREATE TABLE IF NOT EXISTS crm_campaigns (
    campaign_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    name TEXT NOT NULL DEFAULT '',
    kind TEXT NOT NULL DEFAULT 'Marketing'
        CHECK (kind IN ('Marketing','Exhibition','Web','Seasonal','Referral','Other')),
    start_date TEXT,
    end_date TEXT,
    budget NUMERIC NOT NULL DEFAULT 0,
    target_notes TEXT,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Active','Completed','Cancelled')),
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_campaigns_company
    ON crm_campaigns(company_id, status) WHERE is_deleted = FALSE;

ALTER TABLE crm_leads ADD COLUMN IF NOT EXISTS campaign_id TEXT;
ALTER TABLE crm_opportunities ADD COLUMN IF NOT EXISTS campaign_id TEXT;
CREATE INDEX IF NOT EXISTS ix_crm_leads_campaign
    ON crm_leads(campaign_id) WHERE campaign_id IS NOT NULL AND is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_campaign
    ON crm_opportunities(campaign_id) WHERE campaign_id IS NOT NULL AND is_deleted = FALSE;

-- ---- Warranties (one row per sold serial) -------------------------------------
CREATE TABLE IF NOT EXISTS swa_warranties (
    warranty_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    serial_id BIGINT NOT NULL,
    serial_no TEXT NOT NULL,
    material_id INTEGER NOT NULL,
    material_code TEXT NOT NULL DEFAULT '',
    party_id INTEGER,
    party_name TEXT NOT NULL DEFAULT '',
    source_doc_kind TEXT NOT NULL DEFAULT '',
    source_doc_id BIGINT,
    source_doc_no TEXT NOT NULL DEFAULT '',
    sale_date TEXT NOT NULL,
    warranty_months INTEGER NOT NULL DEFAULT 0,
    expiry_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Expired','Void')),
    terms TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    UNIQUE(company_id, serial_id)
);
CREATE INDEX IF NOT EXISTS ix_swa_warranties_company_status
    ON swa_warranties(company_id, status, expiry_date) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_swa_warranties_serial_no
    ON swa_warranties(company_id, serial_no) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_swa_warranties_party
    ON swa_warranties(company_id, party_id) WHERE is_deleted = FALSE;

-- ---- Warranty claims ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS swa_warranty_claims (
    claim_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    warranty_id BIGINT NOT NULL,
    claim_date TEXT NOT NULL,
    issue TEXT NOT NULL DEFAULT '',
    decision TEXT NOT NULL DEFAULT 'Pending'
        CHECK (decision IN ('Pending','Approved','Rejected')),
    action TEXT
        CHECK (action IS NULL OR action IN ('Repair','Replace','Return')),
    resolution_note TEXT,
    decided_by TEXT,
    decided_at TEXT,
    rma_id BIGINT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_swa_claims_company_warranty
    ON swa_warranty_claims(company_id, warranty_id, claim_date) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_swa_claims_company_decision
    ON swa_warranty_claims(company_id, decision) WHERE is_deleted = FALSE;

-- ---- RMA requests (srt-adjacent; lines as one JSON snapshot, 098 precedent) -----
CREATE TABLE IF NOT EXISTS srt_rma_requests (
    rma_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    rma_no TEXT NOT NULL DEFAULT '',
    party_id INTEGER,
    party_name TEXT NOT NULL DEFAULT '',
    source_kind TEXT NOT NULL DEFAULT 'NONE'
        CHECK (source_kind IN ('INV','DLV','NONE')),
    source_doc_id BIGINT,
    source_doc_no TEXT NOT NULL DEFAULT '',
    reason_code TEXT NOT NULL DEFAULT '',
    notes TEXT,
    lines_json TEXT NOT NULL DEFAULT '[]',
    status TEXT NOT NULL DEFAULT 'Requested'
        CHECK (status IN ('Requested','Approved','Rejected','Converted')),
    requested_by TEXT,
    decided_by TEXT,
    decided_at TEXT,
    decision_note TEXT,
    linked_return_doc_id BIGINT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_srt_rma_company_no
    ON srt_rma_requests(company_id, rma_no) WHERE is_deleted = FALSE AND rma_no <> '';
CREATE INDEX IF NOT EXISTS ix_srt_rma_company_status
    ON srt_rma_requests(company_id, status, created_at) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_srt_rma_company_party
    ON srt_rma_requests(company_id, party_id) WHERE is_deleted = FALSE;

-- ---- Permissions (057/100 pattern; display_order continues at 420) --------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CAMPAIGN.VIEW','CRM Campaign - View','CRM','CAMPAIGN','VIEW','CRM Campaign - View','CRM Campaign - View','View CRM campaigns and their outcome roll-up (sales Level 3, migration 105)',420,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CAMPAIGN.EDIT','CRM Campaign - Edit','CRM','CAMPAIGN','EDIT','CRM Campaign - Edit','CRM Campaign - Edit','Create and edit CRM campaigns (sales Level 3, migration 105)',421,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.WARRANTY.VIEW','Sales Warranty - View','SALES','WARRANTY','VIEW','Sales Warranty - View','Sales Warranty - View','View warranty registrations and claims (sales Level 3, migration 105)',422,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.WARRANTY.MANAGE','Sales Warranty - Manage','SALES','WARRANTY','MANAGE','Sales Warranty - Manage','Sales Warranty - Manage','Register warranties, void them, file and decide warranty claims (sales Level 3, migration 105)',423,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('CRM.CAMPAIGN.VIEW','CRM.CAMPAIGN.EDIT','SALES.WARRANTY.VIEW','SALES.WARRANTY.MANAGE') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('CRM.CAMPAIGN.VIEW','CRM.CAMPAIGN.EDIT','SALES.WARRANTY.VIEW','SALES.WARRANTY.MANAGE') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('CRM.CAMPAIGN.VIEW','SALES.WARRANTY.VIEW') ON CONFLICT DO NOTHING;
