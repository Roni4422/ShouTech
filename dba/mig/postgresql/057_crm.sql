-- ============================================================================
-- 057 - CRM pipeline (PostgreSQL)
-- PostgreSQL counterpart of sqlite/057_crm.sql (Phase B design section 4).
-- See the SQLite file for the design notes, the C1-C3 decisions, the
-- entitlement-postponement override, the ground-truth adaptations and
-- the numbering note (057 applies cleanly after 059-064).
-- ============================================================================

-- ---- Pipeline stages (per-company configuration, Odoo pattern) ---------------
CREATE TABLE IF NOT EXISTS crm_stages (
    stage_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    sequence INTEGER NOT NULL DEFAULT 0,
    probability NUMERIC NOT NULL DEFAULT 0,
    stage_type TEXT NOT NULL DEFAULT 'Open'
        CHECK (stage_type IN ('Open','Won','Lost')),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_stages_company
    ON crm_stages(company_id, sequence) WHERE is_deleted = FALSE;

-- ---- Lost reasons -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS crm_lost_reasons (
    reason_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_lost_reasons_company_name
    ON crm_lost_reasons(company_id, name_en) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_lost_reasons_company
    ON crm_lost_reasons(company_id, is_active) WHERE is_deleted = FALSE;

-- ---- Leads (own entity, decision C1) -------------------------------------------
CREATE TABLE IF NOT EXISTS crm_leads (
    lead_id uuid NOT NULL PRIMARY KEY,
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
    converted_party_id uuid,
    converted_opportunity_id uuid,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_leads_company_status
    ON crm_leads(company_id, status) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_leads_company_rep
    ON crm_leads(company_id, assigned_rep_code) WHERE is_deleted = FALSE;

-- ---- Opportunities ---------------------------------------------------------------
CREATE TABLE IF NOT EXISTS crm_opportunities (
    opp_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    party_id uuid,
    lead_id uuid,
    stage_id uuid,
    probability NUMERIC NOT NULL DEFAULT 0,
    expected_amount NUMERIC NOT NULL DEFAULT 0,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    expected_close_date DATE,
    opp_type TEXT,
    source TEXT,
    campaign TEXT,
    assigned_rep_code TEXT,
    lost_reason_id uuid,
    lost_notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_company_stage
    ON crm_opportunities(company_id, stage_id) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_party
    ON crm_opportunities(company_id, party_id) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_opportunities_lead
    ON crm_opportunities(lead_id) WHERE lead_id IS NOT NULL AND is_deleted = FALSE;

-- ---- Stage-change history (mandate: pipeline movement is auditable) -------------
CREATE TABLE IF NOT EXISTS crm_opportunity_stage_history (
    history_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opp_id uuid NOT NULL,
    from_stage_id uuid,
    to_stage_id uuid,
    from_probability NUMERIC,
    to_probability NUMERIC,
    changed_by TEXT,
    changed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    notes TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_opp_history_opp
    ON crm_opportunity_stage_history(company_id, opp_id, changed_at);

-- ---- Opportunity <-> document links (dedicated table, design section 4.2) -------
CREATE TABLE IF NOT EXISTS crm_opportunity_documents (
    link_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opp_id uuid NOT NULL,
    doc_kind TEXT NOT NULL,
    doc_id BIGINT NOT NULL,
    link_type TEXT NOT NULL DEFAULT 'Quotation'
        CHECK (link_type IN ('Quotation','Order','Invoice')),
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_opp_documents_doc
    ON crm_opportunity_documents(opp_id, doc_kind, doc_id);
CREATE INDEX IF NOT EXISTS ix_crm_opp_documents_opp
    ON crm_opportunity_documents(company_id, opp_id, created_at);

-- ---- Activities (follow-ups against exactly one of lead / opportunity / party) --
CREATE TABLE IF NOT EXISTS crm_activities (
    activity_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    subject TEXT NOT NULL DEFAULT '',
    activity_type TEXT NOT NULL DEFAULT 'Task'
        CHECK (activity_type IN ('Call','Meeting','Email','Task')),
    due_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'Planned'
        CHECK (status IN ('Planned','Done','Cancelled')),
    lead_id uuid,
    opp_id uuid,
    party_id uuid,
    assigned_to TEXT,
    result_notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_crm_activities_company_due
    ON crm_activities(company_id, status, due_at) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_activities_opp
    ON crm_activities(company_id, opp_id) WHERE opp_id IS NOT NULL AND is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_activities_lead
    ON crm_activities(company_id, lead_id) WHERE lead_id IS NOT NULL AND is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_activities_party
    ON crm_activities(company_id, party_id) WHERE party_id IS NOT NULL AND is_deleted = FALSE;

-- ---- Permissions (028/038/053-056 pattern; display_order continues at 358) ------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.LEAD.VIEW','CRM Lead - View','CRM','LEAD','VIEW','CRM Lead - View','CRM Lead - View','View CRM leads (Phase B 057)',358,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.LEAD.EDIT','CRM Lead - Edit','CRM','LEAD','EDIT','CRM Lead - Edit','CRM Lead - Edit','Create and edit CRM leads (Phase B 057)',359,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.LEAD.CONVERT','CRM Lead - Convert','CRM','LEAD','CONVERT','CRM Lead - Convert','CRM Lead - Convert','Convert a qualified lead into a party in the unified master plus an opportunity (Phase B 057)',360,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.OPP.VIEW','CRM Opportunity - View','CRM','OPP','VIEW','CRM Opportunity - View','CRM Opportunity - View','View CRM opportunities and the pipeline board (Phase B 057)',361,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.OPP.EDIT','CRM Opportunity - Edit','CRM','OPP','EDIT','CRM Opportunity - Edit','CRM Opportunity - Edit','Create and edit CRM opportunities, move stages and create quotations (Phase B 057)',362,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.ACTIVITY.VIEW','CRM Activity - View','CRM','ACTIVITY','VIEW','CRM Activity - View','CRM Activity - View','View CRM activities and the overdue worklist (Phase B 057)',363,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.ACTIVITY.EDIT','CRM Activity - Edit','CRM','ACTIVITY','EDIT','CRM Activity - Edit','CRM Activity - Edit','Schedule and complete CRM activities (Phase B 057)',364,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('CRM.STAGE.EDIT','CRM Pipeline - Configure','CRM','STAGE','EDIT','CRM Pipeline - Configure','CRM Pipeline - Configure','Configure CRM pipeline stages and lost reasons (Phase B 057)',365,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('CRM.LEAD.VIEW','CRM.LEAD.EDIT','CRM.LEAD.CONVERT','CRM.OPP.VIEW','CRM.OPP.EDIT','CRM.ACTIVITY.VIEW','CRM.ACTIVITY.EDIT','CRM.STAGE.EDIT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('CRM.LEAD.VIEW','CRM.LEAD.EDIT','CRM.LEAD.CONVERT','CRM.OPP.VIEW','CRM.OPP.EDIT','CRM.ACTIVITY.VIEW','CRM.ACTIVITY.EDIT') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('CRM.LEAD.VIEW','CRM.OPP.VIEW','CRM.ACTIVITY.VIEW') ON CONFLICT DO NOTHING;
