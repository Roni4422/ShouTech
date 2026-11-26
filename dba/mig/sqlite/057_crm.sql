-- ============================================================================
-- 057 - CRM pipeline (SQLite)
--
-- Phase B design section 4 (migration 057): leads, opportunities on a
-- configurable stage pipeline, follow-up activities, lost reasons, the
-- opportunity <-> document link table, and the stage-change history the
-- task mandate requires so pipeline movement is auditable.
--
-- OWNER DECISIONS (design section 4.3, recommendations followed):
--   * C1 - Lead is its OWN entity (crm_leads), not a prospect flag on
--     parties. Unqualified data never pollutes the unified party master;
--     conversion is the one moment a lead becomes a real party.
--   * C2 - Pipeline v1 is a grouped-list board with move buttons
--     (CrmPipelineWin), consistent with the app's grid language - no
--     drag-and-drop kanban in v1.
--   * C3 - The canonical CRM quotation is the EXISTING InvoiceWin
--     Quotation kind (InvDocKind.Quotation, sales_documents doc_kind
--     'QUO'). No second quote module is built. Ground truth from the
--     code: InvRepo.Load accepts BOTH company keys since Fix Phase
--     Step 9, while the step-9 chain (OrderService.
--     ConvertQuotationToOrderAsync -> CommercialDocumentRepository)
--     requires the Guid company key - so CrmRepository writes CRM
--     quotations with the company's Guid key (resolved from the
--     companies registry by code), which keeps the SAME row openable
--     in InvoiceWin AND convertible through the step-9 chain
--     (quotation -> order -> invoice). Quotations stay effect-free
--     until converted (Step 5 posting policy: no GL, no party ledger,
--     no stock).
--
-- OWNER OVERRIDE - ENTITLEMENT GATING POSTPONED: the design gated CRM
-- behind the dongle licensing module (ModKey.CRM + an EnsureModule
-- gate in the service). Licensing/entitlement work is postponed by the
-- owner, so NO gate is built here and no fake gate stands in its
-- place: CRM is fully functional under normal RBAC (CRM.* permissions
-- below, step-1 pattern). ModKey.Crm is registered so the vocabulary
-- is ready; entitlement gating arrives with the postponed licensing
-- work. Sales reps stay CORE (sales_reps, migration 031) - CRM only
-- REFERENCES a rep by code, exactly like documents do.
--
-- GROUND-TRUTH ADAPTATIONS vs the design sketch (documented in
-- docs/BUILD_PHASEB_057.md):
--   * CRM lives in the party world, so company_id is the company CODE
--     (TEXT), exactly like parties / sales_reps (031) - not the journal
--     world's Guid keying that budgets (055) use. The ONE exception is
--     the quotation document row itself (see C3 above).
--   * crm_opportunity_stage_history is ADDED (the sketch has no history
--     table; the mandate requires every stage change recorded).
--   * crm_opportunity_documents gains a surrogate link_id PK and
--     company_id (the sketch keys it only by opp_id); the documents tab
--     and the proof queries address rows by company + opportunity.
--   * crm_stages / crm_lost_reasons defaults are seeded PER COMPANY ON
--     FIRST USE by CrmRepository (a migration cannot know future
--     companies); the seeded pipeline is the design's: New (10) ->
--     Qualified (25) -> Proposition (50) -> Negotiation (75) ->
--     Won (100) / Lost (0), and the design's six lost reasons.
--   * crm_leads gains no second customer store: converted_party_id is
--     the ONLY party link and points at parties.id (migration 031/061
--     unified master); conversion inserts through the same parties
--     columns + ledger_id assignment MasterCardRepository performs.
--
-- NUMBERING NOTE: like 053-056, this file sorts before the already-
-- shipped 059-064 files; the migrator applies pending migrations in
-- filename order and skips by checksum. 057 depends on parties (031),
-- roles/permissions (002-004) and sales_documents (010), never on
-- 059-064 objects (061's parties.ledger_id is read defensively: the
-- conversion path assigns it exactly like MasterCardRepository, and
-- 061 has always run before any company database is used).
--
-- Idempotent: table/index creation is IF NOT EXISTS, permission seeds
-- are INSERT OR IGNORE + UPDATE, and the checksum ledger skips a
-- re-run.
-- ============================================================================

-- ---- Pipeline stages (per-company configuration, Odoo pattern) ---------------
CREATE TABLE IF NOT EXISTS crm_stages (
    stage_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    sequence INTEGER NOT NULL DEFAULT 0,
    probability NUMERIC NOT NULL DEFAULT 0,
    stage_type TEXT NOT NULL DEFAULT 'Open'
        CHECK (stage_type IN ('Open','Won','Lost')),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_stages_company
    ON crm_stages(company_id, sequence) WHERE is_deleted = 0;

-- ---- Lost reasons -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS crm_lost_reasons (
    reason_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_lost_reasons_company_name
    ON crm_lost_reasons(company_id, name_en) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_lost_reasons_company
    ON crm_lost_reasons(company_id, is_active) WHERE is_deleted = 0;

-- ---- Leads (own entity, decision C1) -------------------------------------------
CREATE TABLE IF NOT EXISTS crm_leads (
    lead_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    lead_name TEXT NOT NULL DEFAULT '',
    organization TEXT,
    email TEXT,
    phone TEXT,
    mobile TEXT,
    address TEXT,
    source TEXT,
    campaign TEXT,
    status TEXT NOT NULL DEFAULT 'New'
        CHECK (status IN ('New','Working','Qualified','Converted','Unqualified')),
    assigned_rep_code TEXT,
    converted_party_id TEXT,
    converted_opportunity_id TEXT,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_leads_company_status
    ON crm_leads(company_id, status) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_leads_company_rep
    ON crm_leads(company_id, assigned_rep_code) WHERE is_deleted = 0;

-- ---- Opportunities ---------------------------------------------------------------
CREATE TABLE IF NOT EXISTS crm_opportunities (
    opp_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    party_id TEXT,
    lead_id TEXT,
    stage_id TEXT,
    probability NUMERIC NOT NULL DEFAULT 0,
    expected_amount NUMERIC NOT NULL DEFAULT 0,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    expected_close_date TEXT,
    opp_type TEXT,
    source TEXT,
    campaign TEXT,
    assigned_rep_code TEXT,
    lost_reason_id TEXT,
    lost_notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_company_stage
    ON crm_opportunities(company_id, stage_id) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_party
    ON crm_opportunities(company_id, party_id) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_lead
    ON crm_opportunities(lead_id) WHERE lead_id IS NOT NULL AND is_deleted = 0;

-- ---- Stage-change history (mandate: pipeline movement is auditable) -------------
CREATE TABLE IF NOT EXISTS crm_opportunity_stage_history (
    history_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opp_id TEXT NOT NULL,
    from_stage_id TEXT,
    to_stage_id TEXT,
    from_probability NUMERIC,
    to_probability NUMERIC,
    changed_by TEXT,
    changed_at TEXT NOT NULL DEFAULT (datetime('now')),
    notes TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_opp_history_opp
    ON crm_opportunity_stage_history(company_id, opp_id, changed_at);

-- ---- Opportunity <-> document links (dedicated table, design section 4.2) -------
CREATE TABLE IF NOT EXISTS crm_opportunity_documents (
    link_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opp_id TEXT NOT NULL,
    doc_kind TEXT NOT NULL,
    doc_id INTEGER NOT NULL,
    link_type TEXT NOT NULL DEFAULT 'Quotation'
        CHECK (link_type IN ('Quotation','Order','Invoice')),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_opp_documents_doc
    ON crm_opportunity_documents(opp_id, doc_kind, doc_id);
CREATE INDEX IF NOT EXISTS ix_crm_opp_documents_opp
    ON crm_opportunity_documents(company_id, opp_id, created_at);

-- ---- Activities (follow-ups against exactly one of lead / opportunity / party) --
CREATE TABLE IF NOT EXISTS crm_activities (
    activity_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    subject TEXT NOT NULL DEFAULT '',
    activity_type TEXT NOT NULL DEFAULT 'Task'
        CHECK (activity_type IN ('Call','Meeting','Email','Task')),
    due_at TEXT NOT NULL,
    completed_at TEXT,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Done','Cancelled')),
    lead_id TEXT,
    opp_id TEXT,
    party_id TEXT,
    assigned_to TEXT,
    result_notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_activities_company_due
    ON crm_activities(company_id, status, due_at) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_activities_opp
    ON crm_activities(company_id, opp_id) WHERE opp_id IS NOT NULL AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_activities_lead
    ON crm_activities(company_id, lead_id) WHERE lead_id IS NOT NULL AND is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_activities_party
    ON crm_activities(company_id, party_id) WHERE party_id IS NOT NULL AND is_deleted = 0;

-- ---- Permissions (028/038/053-056 pattern; display_order continues at 358) ------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.LEAD.VIEW','CRM Lead - View','CRM','LEAD','VIEW','CRM Lead - View','CRM Lead - View','View CRM leads (Phase B 057)',358,0);
UPDATE permissions SET group_code='CRM',feature_code='LEAD',operation_code='VIEW',name_ar='CRM Lead - View',name_en='CRM Lead - View',display_order=358,is_sensitive=0 WHERE permission_code='CRM.LEAD.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.LEAD.EDIT','CRM Lead - Edit','CRM','LEAD','EDIT','CRM Lead - Edit','CRM Lead - Edit','Create and edit CRM leads (Phase B 057)',359,0);
UPDATE permissions SET group_code='CRM',feature_code='LEAD',operation_code='EDIT',name_ar='CRM Lead - Edit',name_en='CRM Lead - Edit',display_order=359,is_sensitive=0 WHERE permission_code='CRM.LEAD.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.LEAD.CONVERT','CRM Lead - Convert','CRM','LEAD','CONVERT','CRM Lead - Convert','CRM Lead - Convert','Convert a qualified lead into a party in the unified master plus an opportunity (Phase B 057)',360,1);
UPDATE permissions SET group_code='CRM',feature_code='LEAD',operation_code='CONVERT',name_ar='CRM Lead - Convert',name_en='CRM Lead - Convert',display_order=360,is_sensitive=1 WHERE permission_code='CRM.LEAD.CONVERT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.OPP.VIEW','CRM Opportunity - View','CRM','OPP','VIEW','CRM Opportunity - View','CRM Opportunity - View','View CRM opportunities and the pipeline board (Phase B 057)',361,0);
UPDATE permissions SET group_code='CRM',feature_code='OPP',operation_code='VIEW',name_ar='CRM Opportunity - View',name_en='CRM Opportunity - View',display_order=361,is_sensitive=0 WHERE permission_code='CRM.OPP.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.OPP.EDIT','CRM Opportunity - Edit','CRM','OPP','EDIT','CRM Opportunity - Edit','CRM Opportunity - Edit','Create and edit CRM opportunities, move stages and create quotations (Phase B 057)',362,0);
UPDATE permissions SET group_code='CRM',feature_code='OPP',operation_code='EDIT',name_ar='CRM Opportunity - Edit',name_en='CRM Opportunity - Edit',display_order=362,is_sensitive=0 WHERE permission_code='CRM.OPP.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.ACTIVITY.VIEW','CRM Activity - View','CRM','ACTIVITY','VIEW','CRM Activity - View','CRM Activity - View','View CRM activities and the overdue worklist (Phase B 057)',363,0);
UPDATE permissions SET group_code='CRM',feature_code='ACTIVITY',operation_code='VIEW',name_ar='CRM Activity - View',name_en='CRM Activity - View',display_order=363,is_sensitive=0 WHERE permission_code='CRM.ACTIVITY.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.ACTIVITY.EDIT','CRM Activity - Edit','CRM','ACTIVITY','EDIT','CRM Activity - Edit','CRM Activity - Edit','Schedule and complete CRM activities (Phase B 057)',364,0);
UPDATE permissions SET group_code='CRM',feature_code='ACTIVITY',operation_code='EDIT',name_ar='CRM Activity - Edit',name_en='CRM Activity - Edit',display_order=364,is_sensitive=0 WHERE permission_code='CRM.ACTIVITY.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.STAGE.EDIT','CRM Pipeline - Configure','CRM','STAGE','EDIT','CRM Pipeline - Configure','CRM Pipeline - Configure','Configure CRM pipeline stages and lost reasons (Phase B 057)',365,0);
UPDATE permissions SET group_code='CRM',feature_code='STAGE',operation_code='EDIT',name_ar='CRM Pipeline - Configure',name_en='CRM Pipeline - Configure',display_order=365,is_sensitive=0 WHERE permission_code='CRM.STAGE.EDIT';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('CRM.LEAD.VIEW','CRM.LEAD.EDIT','CRM.LEAD.CONVERT','CRM.OPP.VIEW','CRM.OPP.EDIT','CRM.ACTIVITY.VIEW','CRM.ACTIVITY.EDIT','CRM.STAGE.EDIT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('CRM.LEAD.VIEW','CRM.LEAD.EDIT','CRM.LEAD.CONVERT','CRM.OPP.VIEW','CRM.OPP.EDIT','CRM.ACTIVITY.VIEW','CRM.ACTIVITY.EDIT');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('CRM.LEAD.VIEW','CRM.OPP.VIEW','CRM.ACTIVITY.VIEW');
