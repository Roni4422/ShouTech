-- ============================================================================
-- 105 - Sales cycle Level 3 (SQLite): CRM campaigns, warranty, RMA
--
-- Owner sales spec Arabic appendix Level 3 (CRM / warranty / after-sales /
-- marketing), workstream W10:
--   1. CRM CAMPAIGNS: crm_campaigns master (name, kind, window, budget,
--      target notes, status) + a nullable campaign_id link on crm_leads /
--      crm_opportunities (migration 057). The legacy free-text `campaign`
--      columns stay untouched: campaign_id is the structured link the
--      outcome roll-up (leads / opportunities / won value per campaign)
--      aggregates on, the text column remains the display snapshot.
--   2. WARRANTY (prefix swa_ — sau_ is already the sales-audit prefix of
--      migration 098, so warranty takes its own): swa_warranties, one row
--      per SOLD SERIAL (stock_serials, migration 054), registered from a
--      posted invoice's serial allocations or manually by serial lookup.
--      Expiry is computed at registration (sale date + warranty months,
--      the material card's warranty_months from migration 013 being the
--      default term) and stored, so expiry checks never recompute.
--      swa_warranty_claims: claims against a warranty; filing against an
--      Expired / Void warranty is SAL-7026; an Approved claim with
--      action Return links the RMA it created (rma_id).
--   3. RMA REQUESTS (srt-adjacent, riding the W6 return machinery of
--      migration 100): srt_rma_requests with the requested lines stored
--      as ONE JSON snapshot (the sau_document_revisions precedent of
--      migration 098). Approval reuses SALES.RETURN.APPROVE (migration
--      100 — no duplicate permission). Conversion creates a DRAFT sales
--      return through InvRepo / SrtRepo and stamps linked_return_doc_id;
--      converting a non-Approved or already-converted RMA is SAL-7025.
--
-- Company keying is the company CODE (TEXT) — the party / stock world,
-- exactly like 054 / 057 / 100. party_id on swa_ / srt_ rows is the
-- parties.ledger_id integer identity documents carry.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, column adds guarded
-- by the migrator directive, permission seeds INSERT OR IGNORE +
-- UPDATE; the checksum ledger skips a re-run.
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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_campaigns_company
    ON crm_campaigns(company_id, status) WHERE is_deleted = 0;

-- @if-column-missing crm_leads.campaign_id
ALTER TABLE crm_leads ADD COLUMN campaign_id TEXT;
-- @if-column-missing crm_opportunities.campaign_id
ALTER TABLE crm_opportunities ADD COLUMN campaign_id TEXT;
CREATE INDEX IF NOT EXISTS ix_crm_leads_campaign
    ON crm_leads(campaign_id) WHERE campaign_id IS NOT NULL AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_campaign
    ON crm_opportunities(campaign_id) WHERE campaign_id IS NOT NULL AND is_deleted = 0;

-- ---- Warranties (one row per sold serial) -------------------------------------
CREATE TABLE IF NOT EXISTS swa_warranties (
    warranty_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    serial_id INTEGER NOT NULL,
    serial_no TEXT NOT NULL,
    material_id INTEGER NOT NULL,
    material_code TEXT NOT NULL DEFAULT '',
    party_id INTEGER,
    party_name TEXT NOT NULL DEFAULT '',
    source_doc_kind TEXT NOT NULL DEFAULT '',
    source_doc_id INTEGER,
    source_doc_no TEXT NOT NULL DEFAULT '',
    sale_date TEXT NOT NULL,
    warranty_months INTEGER NOT NULL DEFAULT 0,
    expiry_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Expired','Void')),
    terms TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT,
    UNIQUE(company_id, serial_id)
);
CREATE INDEX IF NOT EXISTS ix_swa_warranties_company_status
    ON swa_warranties(company_id, status, expiry_date) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_swa_warranties_serial_no
    ON swa_warranties(company_id, serial_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_swa_warranties_party
    ON swa_warranties(company_id, party_id) WHERE is_deleted = 0;

-- ---- Warranty claims ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS swa_warranty_claims (
    claim_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    warranty_id INTEGER NOT NULL,
    claim_date TEXT NOT NULL,
    issue TEXT NOT NULL DEFAULT '',
    decision TEXT NOT NULL DEFAULT 'Pending'
        CHECK (decision IN ('Pending','Approved','Rejected')),
    action TEXT
        CHECK (action IS NULL OR action IN ('Repair','Replace','Return')),
    resolution_note TEXT,
    decided_by TEXT,
    decided_at TEXT,
    rma_id INTEGER,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_swa_claims_company_warranty
    ON swa_warranty_claims(company_id, warranty_id, claim_date) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_swa_claims_company_decision
    ON swa_warranty_claims(company_id, decision) WHERE is_deleted = 0;

-- ---- RMA requests (srt-adjacent; lines as one JSON snapshot, 098 precedent) -----
CREATE TABLE IF NOT EXISTS srt_rma_requests (
    rma_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    rma_no TEXT NOT NULL DEFAULT '',
    party_id INTEGER,
    party_name TEXT NOT NULL DEFAULT '',
    source_kind TEXT NOT NULL DEFAULT 'NONE'
        CHECK (source_kind IN ('INV','DLV','NONE')),
    source_doc_id INTEGER,
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
    linked_return_doc_id INTEGER,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_srt_rma_company_no
    ON srt_rma_requests(company_id, rma_no) WHERE is_deleted = 0 AND rma_no <> '';
CREATE INDEX IF NOT EXISTS ix_srt_rma_company_status
    ON srt_rma_requests(company_id, status, created_at) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_srt_rma_company_party
    ON srt_rma_requests(company_id, party_id) WHERE is_deleted = 0;

-- ---- Permissions (057/100 pattern; display_order continues at 420) --------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CAMPAIGN.VIEW','CRM Campaign - View','CRM','CAMPAIGN','VIEW','CRM Campaign - View','CRM Campaign - View','View CRM campaigns and their outcome roll-up (sales Level 3, migration 105)',420,0);
UPDATE permissions SET group_code='CRM',feature_code='CAMPAIGN',operation_code='VIEW',name_ar='CRM Campaign - View',name_en='CRM Campaign - View',display_order=420,is_sensitive=0 WHERE permission_code='CRM.CAMPAIGN.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.CAMPAIGN.EDIT','CRM Campaign - Edit','CRM','CAMPAIGN','EDIT','CRM Campaign - Edit','CRM Campaign - Edit','Create and edit CRM campaigns (sales Level 3, migration 105)',421,0);
UPDATE permissions SET group_code='CRM',feature_code='CAMPAIGN',operation_code='EDIT',name_ar='CRM Campaign - Edit',name_en='CRM Campaign - Edit',display_order=421,is_sensitive=0 WHERE permission_code='CRM.CAMPAIGN.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.WARRANTY.VIEW','Sales Warranty - View','SALES','WARRANTY','VIEW','Sales Warranty - View','Sales Warranty - View','View warranty registrations and claims (sales Level 3, migration 105)',422,0);
UPDATE permissions SET group_code='SALES',feature_code='WARRANTY',operation_code='VIEW',name_ar='Sales Warranty - View',name_en='Sales Warranty - View',display_order=422,is_sensitive=0 WHERE permission_code='SALES.WARRANTY.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.WARRANTY.MANAGE','Sales Warranty - Manage','SALES','WARRANTY','MANAGE','Sales Warranty - Manage','Sales Warranty - Manage','Register warranties, void them, file and decide warranty claims (sales Level 3, migration 105)',423,1);
UPDATE permissions SET group_code='SALES',feature_code='WARRANTY',operation_code='MANAGE',name_ar='Sales Warranty - Manage',name_en='Sales Warranty - Manage',display_order=423,is_sensitive=1 WHERE permission_code='SALES.WARRANTY.MANAGE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('CRM.CAMPAIGN.VIEW','CRM.CAMPAIGN.EDIT','SALES.WARRANTY.VIEW','SALES.WARRANTY.MANAGE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('CRM.CAMPAIGN.VIEW','CRM.CAMPAIGN.EDIT','SALES.WARRANTY.VIEW','SALES.WARRANTY.MANAGE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('CRM.CAMPAIGN.VIEW','SALES.WARRANTY.VIEW');
