-- @shoutech-migration 057_crm.sql 3D3DA921F994566037CFD1B0C65D8DD99B441B079341139C1D1C3C16B300E941 3D3DA921F994566037CFD1B0C65D8DD99B441B079341139C1D1C3C16B300E941
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
-- @shoutech-migration 092_crm_opportunity_items.sql 104E529242491CBC89DA8DC1E4B8CD5D43199847938CDDF959B8F27203E59726 104E529242491CBC89DA8DC1E4B8CD5D43199847938CDDF959B8F27203E59726
-- ============================================================================
-- 092 - CRM opportunity items (PostgreSQL)
-- PostgreSQL counterpart of sqlite/092_crm_opportunity_items.sql
-- (sales cycle upgrade B1, gap G3). See the SQLite file for the
-- design notes: company CODE keying, audit columns, soft delete, and
-- stored line_subtotal / line_tax / line_total mirroring
-- sales_document_lines.
-- ============================================================================

CREATE TABLE IF NOT EXISTS crm_opportunity_items (
    item_id uuid NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opportunity_id uuid NOT NULL,
    line_no INTEGER NOT NULL,
    material_id BIGINT,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    qty NUMERIC NOT NULL DEFAULT 0,
    unit TEXT,
    unit_price NUMERIC NOT NULL DEFAULT 0,
    discount_pct NUMERIC NOT NULL DEFAULT 0,
    tax_rate NUMERIC NOT NULL DEFAULT 0,
    line_subtotal NUMERIC NOT NULL DEFAULT 0,
    line_tax NUMERIC NOT NULL DEFAULT 0,
    line_total NUMERIC NOT NULL DEFAULT 0,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_opp_items_line
    ON crm_opportunity_items(company_id, opportunity_id, line_no) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_crm_opp_items_opp
    ON crm_opportunity_items(company_id, opportunity_id) WHERE is_deleted = FALSE;
-- @shoutech-migration 093_sales_chain_progress.sql 8EC620B67A9D324B0E08CC69484F376CCAE58A89E3FBE9EE9C889D8DE43CDE63 8EC620B67A9D324B0E08CC69484F376CCAE58A89E3FBE9EE9C889D8DE43CDE63
-- 093 - Stored sales-chain progress + documented cancellation (PostgreSQL).
-- per_delivered / per_billed are the ERPNext-style stored fulfilment
-- percentages (quantity-weighted, 0-100) for chain documents. They are
-- recomputed transactionally by OrderDocumentRepository whenever a chain
-- document is created, posted, or converted, and back the derived status
-- (To Deliver and Bill / To Deliver / To Bill / Completed) shown by the
-- order windows. Existing rows start at 0 and are refreshed on the next
-- chain event for their order.
-- cancel_reason / cancelled_by / cancelled_at implement the sales-cycle
-- specification's documented cancellation (spec sections 3/14): every
-- cancellation of an order or quotation records WHY, by whom, and when.
-- The columns are added to purchase_documents as well because
-- OrderDocumentRepository.SetStatusAsync and OrdWin serve both families
-- through one code path; a reason must never be collected and dropped.
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS per_delivered NUMERIC(9,2) NOT NULL DEFAULT 0;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS per_billed NUMERIC(9,2) NOT NULL DEFAULT 0;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS cancelled_by TEXT;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT;
ALTER TABLE purchase_documents
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT;
ALTER TABLE purchase_documents
    ADD COLUMN IF NOT EXISTS cancelled_by TEXT;
ALTER TABLE purchase_documents
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT;
-- @shoutech-migration 096_sales_delivery_fulfilment.sql CD15DED5C77D702259BE7D273036B90EAAD3266B0D671CA24226C5446F969D15 CD15DED5C77D702259BE7D273036B90EAAD3266B0D671CA24226C5446F969D15
-- 096 - Sales delivery fulfilment (PostgreSQL): picking, dispatch
-- linkage + proof of delivery, delivery cost, returned deliveries.
-- Sales cycle upgrade W4 (docs/SALES_CYCLE_UPGRADE.md Part II, spec §4).
-- See the SQLite twin for the full rationale. Delivery cost is
-- RECORDING ONLY — no GL posting (accounting-policy decision pending).

CREATE TABLE IF NOT EXISTS sales_picking (
    picking_id BIGSERIAL PRIMARY KEY,
    company_id TEXT NOT NULL,
    sales_order_id BIGINT NOT NULL REFERENCES sales_documents(doc_id),
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Confirmed', 'Cancelled')),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (to_char(now(), 'YYYY-MM-DD HH24:MI:SS')),
    confirmed_by TEXT,
    confirmed_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_sales_picking_order
    ON sales_picking(company_id, sales_order_id, status);

CREATE TABLE IF NOT EXISTS sales_picking_lines (
    picking_line_id BIGSERIAL PRIMARY KEY,
    picking_id BIGINT NOT NULL REFERENCES sales_picking(picking_id) ON DELETE CASCADE,
    source_line_no INTEGER NOT NULL,
    material_id BIGINT,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    unit TEXT,
    ordered_qty NUMERIC NOT NULL DEFAULT 0,
    picked_qty NUMERIC NOT NULL DEFAULT 0,
    warehouse_id BIGINT,
    UNIQUE (picking_id, source_line_no)
);

ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS sales_doc_id BIGINT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS sales_doc_kind TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS vehicle_no TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS delivered_at TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS received_by TEXT;
ALTER TABLE delivery_requests ADD COLUMN IF NOT EXISTS signature_image BYTEA;

ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS delivery_cost NUMERIC NOT NULL DEFAULT 0;
-- @shoutech-migration 097_sales_commissions.sql BD3BDFEE3FE6EA062DD3666A0636C7966285E4EB70CBE2FE3B8C61309A626C88 BD3BDFEE3FE6EA062DD3666A0636C7966285E4EB70CBE2FE3B8C61309A626C88
-- ============================================================================
-- 097 - Sales commissions (PostgreSQL)
--
-- Owner sales spec Phase 09 (Sales_Cycle.txt section 9), workstream W7b
-- (docs/SALES_CYCLE_UPGRADE.md Part II): commission policies with tiered
-- slabs, generated commission entries, and settlements paid by journal
-- voucher. Before this migration NO commission was ever calculated: the
-- only commission data in the system was sales_reps.commission_pct (031,
-- a flat hint never consumed), materials.commission_pct (013, never
-- consumed) and the dead sales_rep_targets.actual_sales /
-- commission_earned columns (050). Documents still carry the rep as
-- free-text sales_rep_name (023); generation resolves it to
-- sales_reps.code by exact match.
--
-- Conventions mirror 057_crm.sql: company keying is the party world's
-- company CODE (TEXT), exactly like parties / sales_reps (031) — the
-- generation code resolves a document's Guid-or-code company key to the
-- code through the companies registry (PartyLedger.ResolveCompanyCode
-- pattern). Audit columns + soft delete on every table.
--
-- Tables (dba prefix sco = Sales Commissions, spec section 18):
--   sco_commission_policies        rate definitions; basis Sales /
--                                  Collection / Profit; scope dimensions
--                                  rep / product (material or group) /
--                                  customer / territory; validity window.
--   sco_commission_policy_tiers    slab tiers: the slab whose
--                                  from_amount is the largest <= the
--                                  evaluated base overrides the flat rate.
--   sco_commission_entries         one row per (policy, source event):
--                                  generated at invoice posting (Sales /
--                                  Profit basis), by the period run
--                                  (Collection basis, source_ref =
--                                  payment_allocations.allocation_id) and
--                                  as NEGATIVE adjustments when an SRT
--                                  posts against a commissioned invoice.
--                                  source_ref is the one column beyond
--                                  the spec sketch: without it a second
--                                  allocation on the same invoice could
--                                  not be told apart from the first, and
--                                  generation could not be idempotent.
--   sco_commission_settlements     per-rep period settlement of Approved
--                                  entries; Paid stamps the journal
--                                  voucher reference.
--
-- RBAC: SALES.COMMISSION.VIEW / MANAGE / APPROVE / PAY seeded below
-- (step-1 pattern: INSERT OR IGNORE + UPDATE; administrator gets all,
-- plain users get VIEW only — approving and paying commissions is a
-- sensitive, separated duty). APPROVE and PAY are is_sensitive = 1.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS and guarded seeds; the
-- checksum ledger skips a re-run.
-- ============================================================================

CREATE TABLE IF NOT EXISTS sco_commission_policies (
    policy_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    policy_name TEXT NOT NULL,
    basis TEXT NOT NULL CHECK (basis IN ('Sales', 'Collection', 'Profit')),
    rate_pct NUMERIC NOT NULL DEFAULT 0,
    rep_scope TEXT NOT NULL DEFAULT 'All' CHECK (rep_scope IN ('All', 'SpecificRep')),
    rep_code TEXT,
    product_scope TEXT NOT NULL DEFAULT 'All' CHECK (product_scope IN ('All', 'Material', 'Group')),
    material_id BIGINT,
    material_group_code TEXT,
    customer_scope TEXT NOT NULL DEFAULT 'All' CHECK (customer_scope IN ('All', 'SpecificParty')),
    party_id BIGINT,
    area_code TEXT,
    valid_from TEXT,
    valid_to TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_sco_policies_company
    ON sco_commission_policies(company_id, basis) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS sco_commission_policy_tiers (
    tier_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    from_amount NUMERIC NOT NULL DEFAULT 0,
    rate_pct NUMERIC NOT NULL DEFAULT 0,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_sco_tiers_policy
    ON sco_commission_policy_tiers(company_id, policy_id, from_amount) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS sco_commission_entries (
    entry_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    rep_code TEXT NOT NULL,
    source_doc_kind TEXT NOT NULL,
    source_doc_id BIGINT NOT NULL,
    source_doc_no TEXT,
    source_ref TEXT,
    party_id BIGINT,
    party_name TEXT,
    basis TEXT NOT NULL CHECK (basis IN ('Sales', 'Collection', 'Profit')),
    basis_amount NUMERIC NOT NULL DEFAULT 0,
    rate_pct NUMERIC NOT NULL DEFAULT 0,
    commission_amount NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Approved', 'Rejected', 'Settled')),
    period_date TEXT NOT NULL,
    settlement_id TEXT,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TEXT
);

-- Generation idempotency: one entry per (policy, source event). The
-- COALESCE keeps invoice/return entries (source_ref NULL) unique too.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sco_entries_source
    ON sco_commission_entries(company_id, policy_id, source_doc_kind, source_doc_id, COALESCE(source_ref, ''))
    WHERE is_deleted = FALSE;

CREATE INDEX IF NOT EXISTS ix_sco_entries_rep_period
    ON sco_commission_entries(company_id, rep_code, period_date) WHERE is_deleted = FALSE;

CREATE INDEX IF NOT EXISTS ix_sco_entries_settlement
    ON sco_commission_entries(settlement_id) WHERE settlement_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS sco_commission_settlements (
    settlement_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    rep_code TEXT NOT NULL,
    period_from TEXT NOT NULL,
    period_to TEXT NOT NULL,
    total_amount NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Approved', 'Paid')),
    journal_ref TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_sco_settlements_rep
    ON sco_commission_settlements(company_id, rep_code, period_from) WHERE is_deleted = FALSE;

-- ----------------------------------------------------------------------------
-- RBAC seeds (SALES.COMMISSION.*; display_order continues after 385)
-- ----------------------------------------------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.VIEW','Sales Commission - View','SALES','COMMISSION','VIEW','عمولات المبيعات - عرض','Sales Commission - View','View commission policies, entries and settlements (Phase 09, migration 097)',390,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.MANAGE','Sales Commission - Manage','SALES','COMMISSION','MANAGE','عمولات المبيعات - إدارة','Sales Commission - Manage','Define commission policies and tiers, run period generation and create settlements (Phase 09, migration 097)',391,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.APPROVE','Sales Commission - Approve','SALES','COMMISSION','APPROVE','عمولات المبيعات - اعتماد','Sales Commission - Approve','Approve or reject commission entries and approve settlements (Phase 09, migration 097)',392,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.PAY','Sales Commission - Pay','SALES','COMMISSION','PAY','عمولات المبيعات - دفع','Sales Commission - Pay','Pay an approved commission settlement through a journal voucher (Phase 09, migration 097)',393,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.COMMISSION.VIEW','SALES.COMMISSION.MANAGE','SALES.COMMISSION.APPROVE','SALES.COMMISSION.PAY') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.COMMISSION.VIEW') ON CONFLICT DO NOTHING;
-- @shoutech-migration 098_sales_document_lifecycle.sql 3522148326F97D5EA0868C5FF9EB25EA3EAF15864A62157E23960B24F3F7D14E 3522148326F97D5EA0868C5FF9EB25EA3EAF15864A62157E23960B24F3F7D14E
-- ============================================================================
-- 098 - Sales document lifecycle: revisions, approval workflow, generalized
-- attachments (PostgreSQL). Sales cycle W23 (Sales_Cycle.txt sections 2, 3,
-- 5, 14). See the SQLite twin for the full rationale.
--
--   * sales_documents.revision_no / approval_state
--   * sau_document_revisions  - JSON snapshot of every superseded version
--   * sau_approval_history    - Submitted / Approved / Rejected / Amended
--   * sau_sales_settings      - per-company require_approval flag (default OFF)
--   * purchase_document_attachments generalized with owner_kind / owner_ref
--     (PURDOC legacy default, SALDOC sales documents, PARTY parties)
--
-- RBAC: SALES.QUOTATION.APPROVE / SALES.ORDER.APPROVE / SALES.INVOICE.APPROVE
-- / SALES.QUOTATION.DIRECTINVOICE (sensitive) and SALES.ATTACH.VIEW / UPLOAD
-- / DELETE seeded for ROLE_ADMINISTRATOR; plain users get attachment
-- VIEW/UPLOAD only (the 086 pattern). Display order continues after 097.
--
-- Idempotent: ADD COLUMN IF NOT EXISTS, CREATE TABLE/INDEX IF NOT EXISTS,
-- ON CONFLICT seeds; the checksum ledger skips a re-run.
-- ============================================================================

ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS revision_no INTEGER NOT NULL DEFAULT 1;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS approval_state TEXT NOT NULL DEFAULT 'NotRequired';

CREATE TABLE IF NOT EXISTS sau_document_revisions (
    revision_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id BIGINT NOT NULL,
    doc_kind TEXT NOT NULL,
    revision_no INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL,
    change_reason TEXT,
    created_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (company_id, doc_id, revision_no)
);
CREATE INDEX IF NOT EXISTS ix_sau_revisions_doc
    ON sau_document_revisions(company_id, doc_id, revision_no);

CREATE TABLE IF NOT EXISTS sau_approval_history (
    history_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id BIGINT NOT NULL,
    doc_kind TEXT NOT NULL,
    action TEXT NOT NULL CHECK (action IN ('Submitted', 'Approved', 'Rejected', 'Amended')),
    from_state TEXT NOT NULL,
    to_state TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_sau_approval_history_doc
    ON sau_approval_history(company_id, doc_id, created_at);

CREATE TABLE IF NOT EXISTS sau_sales_settings (
    company_code TEXT PRIMARY KEY,
    require_approval BOOLEAN NOT NULL DEFAULT false,
    updated_by TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

ALTER TABLE purchase_document_attachments ADD COLUMN IF NOT EXISTS owner_kind TEXT NOT NULL DEFAULT 'PURDOC';
ALTER TABLE purchase_document_attachments ADD COLUMN IF NOT EXISTS owner_ref TEXT;
UPDATE purchase_document_attachments
SET owner_ref = CAST(document_id AS TEXT)
WHERE owner_ref IS NULL;
CREATE INDEX IF NOT EXISTS ix_purchase_document_attachments_owner
    ON purchase_document_attachments(company_id, owner_kind, owner_ref, is_deleted, uploaded_at);

-- ----------------------------------------------------------------------------
-- RBAC seeds (095 pattern: ON CONFLICT DO UPDATE + role grants)
-- ----------------------------------------------------------------------------
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.APPROVE','Sales Quotation - Approve','SALES','QUOTATION','APPROVE','عروض الأسعار - اعتماد','Sales Quotation - Approve','Approve or reject sales quotations submitted for approval (migration 098)',394,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.APPROVE','Sales Order - Approve','SALES','ORDER','APPROVE','طلبات البيع - اعتماد','Sales Order - Approve','Approve or reject sales orders submitted for approval (migration 098)',395,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.APPROVE','Sales Invoice - Approve','SALES','INVOICE','APPROVE','فواتير البيع - اعتماد','Sales Invoice - Approve','Approve or reject sales invoices submitted for approval (migration 098)',396,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.DIRECTINVOICE','Sales Quotation - Direct Invoice','SALES','QUOTATION','DIRECTINVOICE','عروض الأسعار - تحويل مباشر لفاتورة','Sales Quotation - Direct Invoice','Convert a posted quotation directly to a sales invoice, skipping the order stage (migration 098)',397,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.VIEW','Sales Attachment - View','SALES','ATTACHMENT','VIEW','مرفقات المبيعات - عرض','Sales Attachment - View','View sales document and party attachments (migration 098)',398,false)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.UPLOAD','Sales Attachment - Upload','SALES','ATTACHMENT','UPLOAD','مرفقات المبيعات - رفع','Sales Attachment - Upload','Upload sales document and party attachments (migration 098)',399,false)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;
INSERT INTO permissions (permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.DELETE','Sales Attachment - Delete','SALES','ATTACHMENT','DELETE','مرفقات المبيعات - حذف','Sales Attachment - Delete','Soft-delete sales document and party attachments (migration 098)',400,true)
ON CONFLICT(permission_code) DO UPDATE SET permission_name=EXCLUDED.permission_name, group_code=EXCLUDED.group_code, feature_code=EXCLUDED.feature_code, operation_code=EXCLUDED.operation_code, name_ar=EXCLUDED.name_ar, name_en=EXCLUDED.name_en, description=EXCLUDED.description, display_order=EXCLUDED.display_order, is_sensitive=EXCLUDED.is_sensitive, is_active=true;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.QUOTATION.APPROVE','SALES.ORDER.APPROVE','SALES.INVOICE.APPROVE','SALES.QUOTATION.DIRECTINVOICE','SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD','SALES.ATTACH.DELETE')
ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD')
ON CONFLICT DO NOTHING;
-- @shoutech-migration 099_sales_pricing.sql 12C4426A4A9B9DD6539905616D9865AD1F55ED3C8BA817B34EF0549A3C2F0E54 12C4426A4A9B9DD6539905616D9865AD1F55ED3C8BA817B34EF0549A3C2F0E54
-- ============================================================================
-- 099 - Sales pricing & promotions engine (PostgreSQL) — sales cycle W7a, spr
-- Mirror of the SQLite migration: materials.bonus_buy_qty (the BUY side
-- of the dormant BXGY pair), spr_price_overrides (audited SAL-7018/7019/
-- 7020 overrides) and spr_price_history (master price-change audit).
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, ADD COLUMN IF NOT
-- EXISTS; the checksum ledger skips a re-run.
-- ============================================================================

ALTER TABLE materials
    ADD COLUMN IF NOT EXISTS bonus_buy_qty NUMERIC NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS spr_price_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    doc_id BIGINT,
    line_no INTEGER NOT NULL DEFAULT 0,
    material_id BIGINT,
    material_code TEXT,
    rule_code TEXT NOT NULL,
    limit_value NUMERIC NOT NULL DEFAULT 0,
    actual_value NUMERIC NOT NULL DEFAULT 0,
    unit_price NUMERIC NOT NULL DEFAULT 0,
    discount_pct NUMERIC NOT NULL DEFAULT 0,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_spr_price_overrides_doc
    ON spr_price_overrides(company_id, doc_kind, doc_no);

CREATE TABLE IF NOT EXISTS spr_price_history (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('Material','PriceList','CustomerPrice')),
    source_key TEXT NOT NULL,
    field_name TEXT NOT NULL,
    old_value NUMERIC,
    new_value NUMERIC,
    actor TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_spr_price_history_source
    ON spr_price_history(company_id, source_kind, source_key);
-- @shoutech-migration 100_srt_returns_completion.sql D56CDA651C9A7B6889621950CEA824E3267FE1C4BE679BEEC6E3EF040E4E8E17 D56CDA651C9A7B6889621950CEA824E3267FE1C4BE679BEEC6E3EF040E4E8E17
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
-- @shoutech-migration 101_sales_contracts.sql 0E9E6BFA3CA233481EFEAEFB8EDE495D55CBE35BF64D84C699DC5F6D78B60E6D 0E9E6BFA3CA233481EFEAEFB8EDE495D55CBE35BF64D84C699DC5F6D78B60E6D
-- ============================================================================
-- 101 - Sales contracts (PostgreSQL)
--
-- Owner sales spec section 10 (Sales_Cycle.txt), workstream W7c: the
-- contracts module was entirely absent. A contract fixes prices and
-- quantities for one party over a validity window; sales orders draw
-- down against it, scheduled deliveries are generated from it, and its
-- fulfillment / profitability is read back from the chain documents.
--
-- Tables (dba prefix sct = Sales Contracts, spec section 18):
--   sct_contracts        header: party, title, validity window, status
--                        (Draft/Active/Suspended/Expired/Completed/
--                        Renewed), currency, payment terms, guarantee,
--                        renewal_of_id self-link to the successor.
--   sct_contract_lines   material, contracted qty, contract unit price,
--                        delivered_qty / invoiced_qty (STORED progress,
--                        maintained by ContractRepository.RefreshProgress
--                        from the posted chain documents linked to the
--                        contract's orders — the per_delivered/per_billed
--                        stored-progress pattern of Part I, never a
--                        parallel tracker), scheduled-delivery cadence
--                        (next_delivery_date + interval_days +
--                        scheduled_qty per generation).
--   sct_contract_events  status history. A dedicated table is used
--                        because the existing audit tables do not fit:
--                        deletion_audit_log records deletions only and
--                        security_audit_log records security events;
--                        neither carries a per-document status trail.
--
-- contract_no is assigned by ContractRepository from the sales
-- document numbering pattern (kind prefix + max numeric suffix + 1,
-- 'SCT-1001' first), inside the save transaction.
--
-- sales_documents.contract_id links a chain sales order to its
-- contract; posting/saving such an order validates the contract
-- (SAL-7023 inactive / out of validity, SAL-7015 quantity exceeded).
--
-- Company keying is the party world's company CODE (TEXT), exactly
-- like parties / sco_* (097); chain documents carry Guid-or-code keys,
-- resolved through PartyLedger.ResolveCompanyCode at the seams.
--
-- RBAC: SALES.CONTRACT.VIEW / MANAGE seeded below (display_order
-- continues from 401; administrator gets both, plain users VIEW).
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, column adds guarded
-- by the migrator directive, seeds ON CONFLICT upserts; the
-- checksum ledger skips a re-run.
-- ============================================================================

CREATE TABLE IF NOT EXISTS sct_contracts (
    contract_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    contract_no TEXT NOT NULL,
    party_id INTEGER,
    party_code TEXT,
    party_name TEXT,
    title TEXT NOT NULL DEFAULT '',
    start_date TEXT NOT NULL,
    end_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft','Active','Suspended','Expired','Completed','Renewed')),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    payment_terms TEXT,
    guarantee_text TEXT,
    guarantee_amount NUMERIC NOT NULL DEFAULT 0,
    renewal_of_id TEXT,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sct_contracts_company_no
    ON sct_contracts(company_id, contract_no) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_sct_contracts_company_status
    ON sct_contracts(company_id, status, end_date) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_sct_contracts_party
    ON sct_contracts(company_id, party_id) WHERE is_deleted = false;

CREATE TABLE IF NOT EXISTS sct_contract_lines (
    line_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    contract_id TEXT NOT NULL,
    line_no INTEGER NOT NULL,
    material_id INTEGER NOT NULL,
    material_code TEXT,
    description TEXT,
    unit TEXT,
    contracted_qty NUMERIC NOT NULL DEFAULT 0,
    unit_price NUMERIC NOT NULL DEFAULT 0,
    delivered_qty NUMERIC NOT NULL DEFAULT 0,
    invoiced_qty NUMERIC NOT NULL DEFAULT 0,
    next_delivery_date TEXT,
    interval_days INTEGER,
    scheduled_qty NUMERIC NOT NULL DEFAULT 0,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sct_lines_contract_line
    ON sct_contract_lines(company_id, contract_id, line_no) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_sct_lines_material
    ON sct_contract_lines(company_id, material_id) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_sct_lines_due
    ON sct_contract_lines(next_delivery_date) WHERE is_deleted = false AND next_delivery_date IS NOT NULL;

CREATE TABLE IF NOT EXISTS sct_contract_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    contract_id TEXT NOT NULL,
    event_kind TEXT NOT NULL,
    from_status TEXT,
    to_status TEXT,
    detail TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT false,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_sct_events_contract
    ON sct_contract_events(company_id, contract_id, created_at) WHERE is_deleted = false;

-- ---- Chain link: a sales order may draw down against a contract ------------
-- @if-column-missing sales_documents.contract_id
ALTER TABLE sales_documents ADD COLUMN contract_id TEXT;

CREATE INDEX IF NOT EXISTS ix_sales_documents_contract
    ON sales_documents(contract_id) WHERE contract_id IS NOT NULL;

-- ----------------------------------------------------------------------------
-- RBAC seeds (SALES.CONTRACT.*; display_order continues from 401)
-- ----------------------------------------------------------------------------
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.CONTRACT.VIEW','Sales Contract - View','SALES','CONTRACT','VIEW','عقود المبيعات - عرض','Sales Contract - View','View sales contracts, fulfillment and profitability (sales spec section 10, migration 101)',402,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.CONTRACT.MANAGE','Sales Contract - Manage','SALES','CONTRACT','MANAGE','عقود المبيعات - إدارة','Sales Contract - Manage','Create and edit sales contracts, change status, renew and generate scheduled deliveries (sales spec section 10, migration 101)',403,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.CONTRACT.VIEW','SALES.CONTRACT.MANAGE') ON CONFLICT DO NOTHING;
INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.CONTRACT.VIEW') ON CONFLICT DO NOTHING;
-- @shoutech-migration 102_sales_invoice_completion.sql DF706AE789C5F363307AC220A98D69C280C3AA63D6420A5ADFABBC55275D0B53 DF706AE789C5F363307AC220A98D69C280C3AA63D6420A5ADFABBC55275D0B53
-- ============================================================================
-- 102 - Sales invoice completion (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/102_sales_invoice_completion.sql —
-- see that file for the full design notes (consolidated invoices drawing
-- lines from multiple posted delivery notes with per-line source
-- attribution on sales_document_lines, and the export invoice header
-- fields on sales_documents). Dialect differences only: BOOLEAN flags
-- and ADD COLUMN IF NOT EXISTS.
-- ============================================================================

-- ---- Consolidated-invoice line source attribution ---------------------------
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS source_doc_id BIGINT;
ALTER TABLE sales_document_lines ADD COLUMN IF NOT EXISTS source_line_no INTEGER;
CREATE INDEX IF NOT EXISTS ix_sales_document_lines_source_doc
    ON sales_document_lines(source_doc_id) WHERE source_doc_id IS NOT NULL;

-- ---- Export invoice header fields --------------------------------------------
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS is_export BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS incoterm TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS port_of_loading TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS port_of_discharge TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS country_of_origin TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS customs_ref TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS vessel_flight TEXT;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS gross_weight NUMERIC;
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS net_weight NUMERIC;
-- @shoutech-migration 105_sales_level3_crm_warranty_rma.sql 92630145DD7D3CAA7F6EB98B878A8D2F6BCADB8D3768927BFA78B50F0CAEBB53 92630145DD7D3CAA7F6EB98B878A8D2F6BCADB8D3768927BFA78B50F0CAEBB53
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
-- @shoutech-migration 106_sre_recurring_schedules.sql AA8974547D458B2FDD8BBF506BF719FFA63729342F70C31E994E2C60E3BD5E7F AA8974547D458B2FDD8BBF506BF719FFA63729342F70C31E994E2C60E3BD5E7F
-- ============================================================================
-- 106 - Recurring sales schedules (PostgreSQL)
--
-- PostgreSQL twin of dba/mig/sqlite/106_sre_recurring_schedules.sql — see
-- that file for the full design notes (template-based recurring sales
-- orders / invoices, the per-occurrence run ledger whose UNIQUE
-- (schedule_id, occurrence_no) index is the generation idempotency
-- marker, and the sales_documents.recurring_schedule_id stamp).
-- Dialect differences only: BOOLEAN flags, TIMESTAMPTZ audit stamps,
-- ADD COLUMN IF NOT EXISTS, and ON CONFLICT seed upserts.
-- ============================================================================

-- ---- Recurring schedules ----------------------------------------------------
CREATE TABLE IF NOT EXISTS sre_recurring_schedules (
    schedule_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    template_doc_id BIGINT NOT NULL,
    template_doc_kind TEXT NOT NULL CHECK (template_doc_kind IN ('SO','SAL')),
    template_doc_no TEXT NOT NULL DEFAULT '',
    interval_type TEXT NOT NULL CHECK (interval_type IN ('Daily','Weekly','Monthly','Yearly')),
    interval_count INTEGER NOT NULL CHECK (interval_count >= 1),
    start_date TEXT NOT NULL,
    next_run_date TEXT NOT NULL,
    end_date TEXT,
    max_occurrences INTEGER CHECK (max_occurrences IS NULL OR max_occurrences >= 1),
    occurrences_created INTEGER NOT NULL DEFAULT 0,
    auto_post BOOLEAN NOT NULL DEFAULT FALSE,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Paused','Completed','Cancelled')),
    last_run_date TEXT,
    last_run_status TEXT,
    last_generated_doc_id BIGINT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_sre_schedules_company_status
    ON sre_recurring_schedules(company_id, status) WHERE is_deleted = FALSE;
CREATE INDEX IF NOT EXISTS ix_sre_schedules_due
    ON sre_recurring_schedules(status, next_run_date) WHERE is_deleted = FALSE;

-- ---- Per-occurrence run ledger (idempotency marker + audit) -----------------
CREATE TABLE IF NOT EXISTS sre_recurring_runs (
    run_id TEXT NOT NULL PRIMARY KEY,
    schedule_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    occurrence_no INTEGER NOT NULL,
    run_date TEXT NOT NULL,
    generated_doc_id BIGINT,
    outcome TEXT NOT NULL DEFAULT 'Pending'
        CHECK (outcome IN ('Pending','Draft','Posted','PostFailed','Failed')),
    error_text TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at TEXT,
    created_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sre_runs_schedule_occurrence
    ON sre_recurring_runs(schedule_id, occurrence_no);
CREATE INDEX IF NOT EXISTS ix_sre_runs_schedule
    ON sre_recurring_runs(schedule_id, occurrence_no);

-- ---- Generated-document stamp -------------------------------------------------
ALTER TABLE sales_documents ADD COLUMN IF NOT EXISTS recurring_schedule_id TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_recurring_schedule
    ON sales_documents(recurring_schedule_id) WHERE recurring_schedule_id IS NOT NULL;

-- ---- Permissions: SALES.RECURRING.VIEW / MANAGE (display_order after 400) ----
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.VIEW','Sales Recurring - View','SALES','RECURRING','VIEW','المبيعات المتكررة - عرض','Sales Recurring - View','View recurring sales schedules and their generation history (sales spec sections 3 and 5, migration 106)',401,false) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;
INSERT INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.MANAGE','Sales Recurring - Manage','SALES','RECURRING','MANAGE','المبيعات المتكررة - إدارة','Sales Recurring - Manage','Create, pause, resume, cancel and run recurring sales schedules (sales spec sections 3 and 5, migration 106)',402,true) ON CONFLICT(permission_code) DO UPDATE SET group_code=EXCLUDED.group_code,feature_code=EXCLUDED.feature_code,operation_code=EXCLUDED.operation_code,name_ar=EXCLUDED.name_ar,name_en=EXCLUDED.name_en,display_order=EXCLUDED.display_order,is_sensitive=EXCLUDED.is_sensitive;

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = true AND p.is_active = true AND p.permission_code IN ('SALES.RECURRING.VIEW','SALES.RECURRING.MANAGE') ON CONFLICT DO NOTHING;
