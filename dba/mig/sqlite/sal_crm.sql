-- @shoutech-migration 057_crm.sql 337DB92BB63514C4998C95A2742E84FB0893ECB8F3ADBA50B5F2842CBD5A8115 D406F90B27CD295A6EA9C99CDD740159DCB43CD1633B6DE8B822D60F38DA1E1D D406F90B27CD295A6EA9C99CDD740159DCB43CD1633B6DE8B822D60F38DA1E1D D406F90B27CD295A6EA9C99CDD740159DCB43CD1633B6DE8B822D60F38DA1E1D
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
-- @shoutech-migration 092_crm_opportunity_items.sql 0D39C1DBE38FCF06F7C17A2423B7EF4FD40335863D847D3FBAF978D31743233D A7F20FCAA5C6778FCE416E76B623C9B8A215BAFA5C52BA0AF31B5F635C9E896B A7F20FCAA5C6778FCE416E76B623C9B8A215BAFA5C52BA0AF31B5F635C9E896B A7F20FCAA5C6778FCE416E76B623C9B8A215BAFA5C52BA0AF31B5F635C9E896B
-- ============================================================================
-- 092 - CRM opportunity items (SQLite)
--
-- Sales cycle upgrade B1 (gap G3): opportunities gain a real items
-- table so an opportunity -> quotation conversion carries actual
-- material lines (material, description, qty, unit, price, discount,
-- tax) instead of fabricating one title x expected-amount line.
--
-- Conventions mirror 057_crm.sql (company CODE keying, audit columns,
-- soft delete) and sales_document_lines (010): line_subtotal is the
-- NET amount after the line discount, line_tax is computed on the net
-- subtotal, line_total = line_subtotal + line_tax. All three are
-- stored (not generated) exactly like sales_document_lines so the
-- opportunity total and the quotation built from it can never drift
-- apart by a rounding-path difference.
--
-- material_id is the materials table's INTEGER identity (nullable:
-- a free-text opportunity line is legitimate before a material is
-- chosen); material_code keeps the display snapshot.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS; the checksum
-- ledger skips a re-run.
-- ============================================================================

CREATE TABLE IF NOT EXISTS crm_opportunity_items (
    item_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    opportunity_id TEXT NOT NULL,
    line_no INTEGER NOT NULL,
    material_id INTEGER,
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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_opp_items_line
    ON crm_opportunity_items(company_id, opportunity_id, line_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_crm_opp_items_opp
    ON crm_opportunity_items(company_id, opportunity_id) WHERE is_deleted = 0;
-- @shoutech-migration 093_sales_chain_progress.sql 27D113B7425752E54B7A1F88106CD465902C7C0EB643B58DCBD95FCD983E2A81 24A6E35A25F1980CC7D8FDFA3E82D8B6887800925EB01EFE027DD2F7F8A8E184 24A6E35A25F1980CC7D8FDFA3E82D8B6887800925EB01EFE027DD2F7F8A8E184 24A6E35A25F1980CC7D8FDFA3E82D8B6887800925EB01EFE027DD2F7F8A8E184
-- 093 - Stored sales-chain progress + documented cancellation (SQLite).
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
-- @if-column-missing sales_documents.per_delivered
ALTER TABLE sales_documents ADD COLUMN per_delivered NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.per_billed
ALTER TABLE sales_documents ADD COLUMN per_billed NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.cancel_reason
ALTER TABLE sales_documents ADD COLUMN cancel_reason TEXT;
-- @endif
-- @if-column-missing sales_documents.cancelled_by
ALTER TABLE sales_documents ADD COLUMN cancelled_by TEXT;
-- @endif
-- @if-column-missing sales_documents.cancelled_at
ALTER TABLE sales_documents ADD COLUMN cancelled_at TEXT;
-- @endif
-- @if-column-missing purchase_documents.cancel_reason
ALTER TABLE purchase_documents ADD COLUMN cancel_reason TEXT;
-- @endif
-- @if-column-missing purchase_documents.cancelled_by
ALTER TABLE purchase_documents ADD COLUMN cancelled_by TEXT;
-- @endif
-- @if-column-missing purchase_documents.cancelled_at
ALTER TABLE purchase_documents ADD COLUMN cancelled_at TEXT;
-- @endif
-- @shoutech-migration 096_sales_delivery_fulfilment.sql 76EA3ED0735D3BEF772B61329BB2E113EC95101DAB03B9B9BE00F678F8476BBD B298859D2310B0BAD8D4B7F45973D59560D337BBB8A22EE8318EEAFBB2F12ADF B298859D2310B0BAD8D4B7F45973D59560D337BBB8A22EE8318EEAFBB2F12ADF B298859D2310B0BAD8D4B7F45973D59560D337BBB8A22EE8318EEAFBB2F12ADF
-- 096 - Sales delivery fulfilment (SQLite): picking, dispatch linkage +
-- proof of delivery, delivery cost, returned deliveries.
-- Sales cycle upgrade W4 (docs/SALES_CYCLE_UPGRADE.md Part II, spec §4).
--
-- Picking: sales_picking / sales_picking_lines stage a posted sales
-- order's remaining-to-deliver quantities for the warehouse before a
-- delivery note is created. Lines carry the chain's line identity
-- (material_id + source_line_no = the order line's line_no).
--
-- Dispatch: delivery_requests (migration 036) gains a traceable link to
-- the sales delivery note it ships (sales_doc_id / sales_doc_kind), the
-- vehicle that carried it (vehicle_no, defaulted from the assigned
-- driver's card), and proof of delivery (delivered_at / received_by /
-- signature_image). The 'Returned' status joins the desk flow in code
-- (DeliveryStatuses) — no schema change needed for the status value.
--
-- Delivery cost: sales_documents.delivery_cost records what a delivery
-- note cost to ship. RECORDING ONLY — no GL posting (auto-posting the
-- cost is an accounting-policy decision, flagged in the upgrade doc).
--
-- Idempotent: CREATE TABLE IF NOT EXISTS + @if-column-missing guards;
-- the checksum ledger also skips a re-run.

CREATE TABLE IF NOT EXISTS sales_picking (
    picking_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    sales_order_id INTEGER NOT NULL REFERENCES sales_documents(doc_id),
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft', 'Confirmed', 'Cancelled')),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    confirmed_by TEXT,
    confirmed_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_sales_picking_order
    ON sales_picking(company_id, sales_order_id, status);

CREATE TABLE IF NOT EXISTS sales_picking_lines (
    picking_line_id INTEGER PRIMARY KEY AUTOINCREMENT,
    picking_id INTEGER NOT NULL REFERENCES sales_picking(picking_id) ON DELETE CASCADE,
    source_line_no INTEGER NOT NULL,
    material_id INTEGER,
    material_code TEXT,
    description TEXT NOT NULL DEFAULT '',
    unit TEXT,
    ordered_qty NUMERIC NOT NULL DEFAULT 0,
    picked_qty NUMERIC NOT NULL DEFAULT 0,
    warehouse_id INTEGER,
    UNIQUE (picking_id, source_line_no)
);

-- @if-column-missing delivery_requests.sales_doc_id
ALTER TABLE delivery_requests ADD COLUMN sales_doc_id INTEGER;
-- @endif
-- @if-column-missing delivery_requests.sales_doc_kind
ALTER TABLE delivery_requests ADD COLUMN sales_doc_kind TEXT;
-- @endif
-- @if-column-missing delivery_requests.vehicle_no
ALTER TABLE delivery_requests ADD COLUMN vehicle_no TEXT;
-- @endif
-- @if-column-missing delivery_requests.delivered_at
ALTER TABLE delivery_requests ADD COLUMN delivered_at TEXT;
-- @endif
-- @if-column-missing delivery_requests.received_by
ALTER TABLE delivery_requests ADD COLUMN received_by TEXT;
-- @endif
-- @if-column-missing delivery_requests.signature_image
ALTER TABLE delivery_requests ADD COLUMN signature_image BLOB;
-- @endif

-- @if-column-missing sales_documents.delivery_cost
ALTER TABLE sales_documents ADD COLUMN delivery_cost NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @shoutech-migration 097_sales_commissions.sql 8D8FE2D7129F558093657D14A2FA86D58B619C5D7A51766CD7697565D29DAFA1 36F5C26F9204984B75DD0390EF51B6B7C753B3026D0A7279BFFD870F89ECE59E 36F5C26F9204984B75DD0390EF51B6B7C753B3026D0A7279BFFD870F89ECE59E 36F5C26F9204984B75DD0390EF51B6B7C753B3026D0A7279BFFD870F89ECE59E
-- ============================================================================
-- 097 - Sales commissions (SQLite)
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
    material_id INTEGER,
    material_group_code TEXT,
    customer_scope TEXT NOT NULL DEFAULT 'All' CHECK (customer_scope IN ('All', 'SpecificParty')),
    party_id INTEGER,
    area_code TEXT,
    valid_from TEXT,
    valid_to TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_sco_policies_company
    ON sco_commission_policies(company_id, basis) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS sco_commission_policy_tiers (
    tier_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    from_amount NUMERIC NOT NULL DEFAULT 0,
    rate_pct NUMERIC NOT NULL DEFAULT 0,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_sco_tiers_policy
    ON sco_commission_policy_tiers(company_id, policy_id, from_amount) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS sco_commission_entries (
    entry_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    policy_id TEXT NOT NULL,
    rep_code TEXT NOT NULL,
    source_doc_kind TEXT NOT NULL,
    source_doc_id INTEGER NOT NULL,
    source_doc_no TEXT,
    source_ref TEXT,
    party_id INTEGER,
    party_name TEXT,
    basis TEXT NOT NULL CHECK (basis IN ('Sales', 'Collection', 'Profit')),
    basis_amount NUMERIC NOT NULL DEFAULT 0,
    rate_pct NUMERIC NOT NULL DEFAULT 0,
    commission_amount NUMERIC NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Approved', 'Rejected', 'Settled')),
    period_date TEXT NOT NULL,
    settlement_id TEXT,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

-- Generation idempotency: one entry per (policy, source event). The
-- COALESCE keeps invoice/return entries (source_ref NULL) unique too.
CREATE UNIQUE INDEX IF NOT EXISTS ux_sco_entries_source
    ON sco_commission_entries(company_id, policy_id, source_doc_kind, source_doc_id, COALESCE(source_ref, ''))
    WHERE is_deleted = 0;

CREATE INDEX IF NOT EXISTS ix_sco_entries_rep_period
    ON sco_commission_entries(company_id, rep_code, period_date) WHERE is_deleted = 0;

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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_sco_settlements_rep
    ON sco_commission_settlements(company_id, rep_code, period_from) WHERE is_deleted = 0;

-- ----------------------------------------------------------------------------
-- RBAC seeds (SALES.COMMISSION.*; display_order continues after 385)
-- ----------------------------------------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.VIEW','Sales Commission - View','SALES','COMMISSION','VIEW','عمولات المبيعات - عرض','Sales Commission - View','View commission policies, entries and settlements (Phase 09, migration 097)',390,0);
UPDATE permissions SET group_code='SALES',feature_code='COMMISSION',operation_code='VIEW',name_ar='عمولات المبيعات - عرض',name_en='Sales Commission - View',display_order=390,is_sensitive=0 WHERE permission_code='SALES.COMMISSION.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.MANAGE','Sales Commission - Manage','SALES','COMMISSION','MANAGE','عمولات المبيعات - إدارة','Sales Commission - Manage','Define commission policies and tiers, run period generation and create settlements (Phase 09, migration 097)',391,0);
UPDATE permissions SET group_code='SALES',feature_code='COMMISSION',operation_code='MANAGE',name_ar='عمولات المبيعات - إدارة',name_en='Sales Commission - Manage',display_order=391,is_sensitive=0 WHERE permission_code='SALES.COMMISSION.MANAGE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.APPROVE','Sales Commission - Approve','SALES','COMMISSION','APPROVE','عمولات المبيعات - اعتماد','Sales Commission - Approve','Approve or reject commission entries and approve settlements (Phase 09, migration 097)',392,1);
UPDATE permissions SET group_code='SALES',feature_code='COMMISSION',operation_code='APPROVE',name_ar='عمولات المبيعات - اعتماد',name_en='Sales Commission - Approve',display_order=392,is_sensitive=1 WHERE permission_code='SALES.COMMISSION.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.COMMISSION.PAY','Sales Commission - Pay','SALES','COMMISSION','PAY','عمولات المبيعات - دفع','Sales Commission - Pay','Pay an approved commission settlement through a journal voucher (Phase 09, migration 097)',393,1);
UPDATE permissions SET group_code='SALES',feature_code='COMMISSION',operation_code='PAY',name_ar='عمولات المبيعات - دفع',name_en='Sales Commission - Pay',display_order=393,is_sensitive=1 WHERE permission_code='SALES.COMMISSION.PAY';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.COMMISSION.VIEW','SALES.COMMISSION.MANAGE','SALES.COMMISSION.APPROVE','SALES.COMMISSION.PAY');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.COMMISSION.VIEW');
-- @shoutech-migration 098_sales_document_lifecycle.sql A2A7716CDCD049D2AAADDABC3E281B4BD3F160F8DFC83EF13C9FB0BBE08ED6AD EE1B466770BC4254210F80E243DF441DB7B0D6F47F7DD8ED5C985B89114A4157 EE1B466770BC4254210F80E243DF441DB7B0D6F47F7DD8ED5C985B89114A4157 EE1B466770BC4254210F80E243DF441DB7B0D6F47F7DD8ED5C985B89114A4157
-- ============================================================================
-- 098 - Sales document lifecycle: revisions, approval workflow, generalized
-- attachments (SQLite). Sales cycle W23 (Sales_Cycle.txt sections 2, 3, 5, 14).
--
--   * sales_documents.revision_no / approval_state: every chain + native
--     sales document carries its revision number (starts at 1, incremented
--     by every amendment) and its approval state (NotRequired / Pending /
--     Approved / Rejected). approval_state is meaningful only when the
--     company enables approval in sau_sales_settings (default OFF, so all
--     existing flows and documents are unaffected).
--   * sau_document_revisions (sau = Sales Audit, spec section 18): the full
--     prior version (header + lines as one JSON snapshot) preserved by an
--     amendment, keyed by the revision number it supersedes. Read-only.
--   * sau_approval_history: one row per workflow event (Submitted /
--     Approved / Rejected / Amended) with actor, reason and timestamps.
--   * sau_sales_settings: per-company policy; keyed by company CODE like
--     company_tax_settings (067). require_approval defaults OFF.
--   * purchase_document_attachments generalized (migration 086 extended,
--     no parallel table): owner_kind discriminates PURDOC (legacy purchase
--     documents, owner_ref = document id), SALDOC (sales documents,
--     owner_ref = document id) and PARTY (parties, owner_ref = party id).
--     Existing rows default to PURDOC with owner_ref = document_id.
--
-- RBAC: SALES.QUOTATION.APPROVE / SALES.ORDER.APPROVE / SALES.INVOICE.APPROVE
-- (sensitive, separated duty), SALES.QUOTATION.DIRECTINVOICE (sensitive,
-- authorized QUO -> SAL shortcut) and SALES.ATTACH.VIEW / UPLOAD / DELETE
-- seeded for ROLE_ADMINISTRATOR; plain users get attachment VIEW/UPLOAD
-- only (the 086 pattern). Display order continues after 097 (393).
--
-- Idempotent: @if-column-missing guards, CREATE TABLE/INDEX IF NOT EXISTS
-- and INSERT OR IGNORE + UPDATE seeds; the checksum ledger skips a re-run.
-- ============================================================================

-- @if-column-missing sales_documents.revision_no
ALTER TABLE sales_documents ADD COLUMN revision_no INTEGER NOT NULL DEFAULT 1;
-- @endif
-- @if-column-missing sales_documents.approval_state
ALTER TABLE sales_documents ADD COLUMN approval_state TEXT NOT NULL DEFAULT 'NotRequired';
-- @endif

CREATE TABLE IF NOT EXISTS sau_document_revisions (
    revision_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id INTEGER NOT NULL,
    doc_kind TEXT NOT NULL,
    revision_no INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL,
    change_reason TEXT,
    created_by TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, doc_id, revision_no)
);
CREATE INDEX IF NOT EXISTS ix_sau_revisions_doc
    ON sau_document_revisions(company_id, doc_id, revision_no);

CREATE TABLE IF NOT EXISTS sau_approval_history (
    history_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_id INTEGER NOT NULL,
    doc_kind TEXT NOT NULL,
    action TEXT NOT NULL CHECK (action IN ('Submitted', 'Approved', 'Rejected', 'Amended')),
    from_state TEXT NOT NULL,
    to_state TEXT NOT NULL,
    actor TEXT NOT NULL,
    reason TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_sau_approval_history_doc
    ON sau_approval_history(company_id, doc_id, created_at);

CREATE TABLE IF NOT EXISTS sau_sales_settings (
    company_code TEXT PRIMARY KEY,
    require_approval INTEGER NOT NULL DEFAULT 0 CHECK (require_approval IN (0, 1)),
    updated_by TEXT,
    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- @if-column-missing purchase_document_attachments.owner_kind
ALTER TABLE purchase_document_attachments ADD COLUMN owner_kind TEXT NOT NULL DEFAULT 'PURDOC';
-- @endif
-- @if-column-missing purchase_document_attachments.owner_ref
ALTER TABLE purchase_document_attachments ADD COLUMN owner_ref TEXT;
-- @endif
UPDATE purchase_document_attachments
SET owner_ref = CAST(document_id AS TEXT)
WHERE owner_ref IS NULL;
CREATE INDEX IF NOT EXISTS ix_purchase_document_attachments_owner
    ON purchase_document_attachments(company_id, owner_kind, owner_ref, is_deleted, uploaded_at);

-- ----------------------------------------------------------------------------
-- RBAC seeds (097 pattern: INSERT OR IGNORE + UPDATE)
-- ----------------------------------------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.APPROVE','Sales Quotation - Approve','SALES','QUOTATION','APPROVE','عروض الأسعار - اعتماد','Sales Quotation - Approve','Approve or reject sales quotations submitted for approval (migration 098)',394,1);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='APPROVE',name_ar='عروض الأسعار - اعتماد',name_en='Sales Quotation - Approve',display_order=394,is_sensitive=1 WHERE permission_code='SALES.QUOTATION.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ORDER.APPROVE','Sales Order - Approve','SALES','ORDER','APPROVE','طلبات البيع - اعتماد','Sales Order - Approve','Approve or reject sales orders submitted for approval (migration 098)',395,1);
UPDATE permissions SET group_code='SALES',feature_code='ORDER',operation_code='APPROVE',name_ar='طلبات البيع - اعتماد',name_en='Sales Order - Approve',display_order=395,is_sensitive=1 WHERE permission_code='SALES.ORDER.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.INVOICE.APPROVE','Sales Invoice - Approve','SALES','INVOICE','APPROVE','فواتير البيع - اعتماد','Sales Invoice - Approve','Approve or reject sales invoices submitted for approval (migration 098)',396,1);
UPDATE permissions SET group_code='SALES',feature_code='INVOICE',operation_code='APPROVE',name_ar='فواتير البيع - اعتماد',name_en='Sales Invoice - Approve',display_order=396,is_sensitive=1 WHERE permission_code='SALES.INVOICE.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.QUOTATION.DIRECTINVOICE','Sales Quotation - Direct Invoice','SALES','QUOTATION','DIRECTINVOICE','عروض الأسعار - تحويل مباشر لفاتورة','Sales Quotation - Direct Invoice','Convert a posted quotation directly to a sales invoice, skipping the order stage (migration 098)',397,1);
UPDATE permissions SET group_code='SALES',feature_code='QUOTATION',operation_code='DIRECTINVOICE',name_ar='عروض الأسعار - تحويل مباشر لفاتورة',name_en='Sales Quotation - Direct Invoice',display_order=397,is_sensitive=1 WHERE permission_code='SALES.QUOTATION.DIRECTINVOICE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.VIEW','Sales Attachment - View','SALES','ATTACHMENT','VIEW','مرفقات المبيعات - عرض','Sales Attachment - View','View sales document and party attachments (migration 098)',398,0);
UPDATE permissions SET group_code='SALES',feature_code='ATTACHMENT',operation_code='VIEW',name_ar='مرفقات المبيعات - عرض',name_en='Sales Attachment - View',display_order=398,is_sensitive=0 WHERE permission_code='SALES.ATTACH.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.UPLOAD','Sales Attachment - Upload','SALES','ATTACHMENT','UPLOAD','مرفقات المبيعات - رفع','Sales Attachment - Upload','Upload sales document and party attachments (migration 098)',399,0);
UPDATE permissions SET group_code='SALES',feature_code='ATTACHMENT',operation_code='UPLOAD',name_ar='مرفقات المبيعات - رفع',name_en='Sales Attachment - Upload',display_order=399,is_sensitive=0 WHERE permission_code='SALES.ATTACH.UPLOAD';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.ATTACH.DELETE','Sales Attachment - Delete','SALES','ATTACHMENT','DELETE','مرفقات المبيعات - حذف','Sales Attachment - Delete','Soft-delete sales document and party attachments (migration 098)',400,1);
UPDATE permissions SET group_code='SALES',feature_code='ATTACHMENT',operation_code='DELETE',name_ar='مرفقات المبيعات - حذف',name_en='Sales Attachment - Delete',display_order=400,is_sensitive=1 WHERE permission_code='SALES.ATTACH.DELETE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.QUOTATION.APPROVE','SALES.ORDER.APPROVE','SALES.INVOICE.APPROVE','SALES.QUOTATION.DIRECTINVOICE','SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD','SALES.ATTACH.DELETE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.ATTACH.VIEW','SALES.ATTACH.UPLOAD');
-- @shoutech-migration 099_sales_pricing.sql 842F59C52A9E461A46F1EA8F5FD2247583CA1F6A01C5A1751616FBBB5162D0DA 2C7A3D74996E6AF14DC1FCE8DE586E2B82689265E629D1A8CE53529C01F0FD61 2C7A3D74996E6AF14DC1FCE8DE586E2B82689265E629D1A8CE53529C01F0FD61 2C7A3D74996E6AF14DC1FCE8DE586E2B82689265E629D1A8CE53529C01F0FD61
-- ============================================================================
-- 099 - Sales pricing & promotions engine (SQLite) — sales cycle W7a, spr
--
--   * materials.bonus_buy_qty: the BUY side of the dormant BXGY pair.
--     bonus_qty (013) alone could not express "buy X get Y free" — it
--     records only the free quantity. bonus_buy_qty is the paid quantity
--     that earns bonus_qty free units of bonus_item_code (or of the
--     material itself when bonus_item_code is empty).
--   * spr_price_overrides: audit row SalesPriceGuard writes when a
--     SALES.PRICE.OVERRIDE / SALES.DISCOUNT.OVERRIDE holder authorizes a
--     line past price_min (SAL-7018), margin_target_pct (SAL-7019) or
--     discount_max_pct (SAL-7020) — the PartyCreditGuard/credit_overrides
--     pattern (095), per line and per rule.
--   * spr_price_history: master price-change audit (old -> new, who,
--     when) for material card prices, material_price_list prices and
--     customer_prices, written by SalesPriceHistory at the save seams.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, column add guarded by
-- the migrator directive; the checksum ledger skips a re-run.
-- ============================================================================

-- @if-column-missing materials.bonus_buy_qty
ALTER TABLE materials ADD COLUMN bonus_buy_qty NUMERIC NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS spr_price_overrides (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_kind TEXT,
    doc_no TEXT,
    doc_id INTEGER,
    line_no INTEGER NOT NULL DEFAULT 0,
    material_id INTEGER,
    material_code TEXT,
    rule_code TEXT NOT NULL,
    limit_value REAL NOT NULL DEFAULT 0,
    actual_value REAL NOT NULL DEFAULT 0,
    unit_price REAL NOT NULL DEFAULT 0,
    discount_pct REAL NOT NULL DEFAULT 0,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_spr_price_overrides_doc
    ON spr_price_overrides(company_id, doc_kind, doc_no);

CREATE TABLE IF NOT EXISTS spr_price_history (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('Material','PriceList','CustomerPrice')),
    source_key TEXT NOT NULL,
    field_name TEXT NOT NULL,
    old_value REAL,
    new_value REAL,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_spr_price_history_source
    ON spr_price_history(company_id, source_kind, source_key);
-- @shoutech-migration 100_srt_returns_completion.sql 06A97F7B424501B95D6F98A12E997BE29BB46082BC35BE9229112D401BA6B182 0DC1CFD8E91C531E1129668829E5EE3258FFE1C8926B915AD2FAA4A96557B985 0DC1CFD8E91C531E1129668829E5EE3258FFE1C8926B915AD2FAA4A96557B985 0DC1CFD8E91C531E1129668829E5EE3258FFE1C8926B915AD2FAA4A96557B985
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
-- @shoutech-migration 101_sales_contracts.sql 6386C33D63A283414BAF432905A4CE898452295D65CA2BAF72FDA6185D96C8DE 02B107E6EDA142BD90645EE71BAE5AA87C7DED8B6F3457D5A7E74187D8D5A954 02B107E6EDA142BD90645EE71BAE5AA87C7DED8B6F3457D5A7E74187D8D5A954 02B107E6EDA142BD90645EE71BAE5AA87C7DED8B6F3457D5A7E74187D8D5A954
-- ============================================================================
-- 101 - Sales contracts (SQLite)
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
-- by the migrator directive, seeds INSERT OR IGNORE + UPDATE; the
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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sct_contracts_company_no
    ON sct_contracts(company_id, contract_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sct_contracts_company_status
    ON sct_contracts(company_id, status, end_date) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sct_contracts_party
    ON sct_contracts(company_id, party_id) WHERE is_deleted = 0;

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
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_sct_lines_contract_line
    ON sct_contract_lines(company_id, contract_id, line_no) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sct_lines_material
    ON sct_contract_lines(company_id, material_id) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sct_lines_due
    ON sct_contract_lines(next_delivery_date) WHERE is_deleted = 0 AND next_delivery_date IS NOT NULL;

CREATE TABLE IF NOT EXISTS sct_contract_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    contract_id TEXT NOT NULL,
    event_kind TEXT NOT NULL,
    from_status TEXT,
    to_status TEXT,
    detail TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS ix_sct_events_contract
    ON sct_contract_events(company_id, contract_id, created_at) WHERE is_deleted = 0;

-- ---- Chain link: a sales order may draw down against a contract ------------
-- @if-column-missing sales_documents.contract_id
ALTER TABLE sales_documents ADD COLUMN contract_id TEXT;

CREATE INDEX IF NOT EXISTS ix_sales_documents_contract
    ON sales_documents(contract_id) WHERE contract_id IS NOT NULL;

-- ----------------------------------------------------------------------------
-- RBAC seeds (SALES.CONTRACT.*; display_order continues from 401)
-- ----------------------------------------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.CONTRACT.VIEW','Sales Contract - View','SALES','CONTRACT','VIEW','عقود المبيعات - عرض','Sales Contract - View','View sales contracts, fulfillment and profitability (sales spec section 10, migration 101)',402,0);
UPDATE permissions SET group_code='SALES',feature_code='CONTRACT',operation_code='VIEW',name_ar='عقود المبيعات - عرض',name_en='Sales Contract - View',display_order=402,is_sensitive=0 WHERE permission_code='SALES.CONTRACT.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.CONTRACT.MANAGE','Sales Contract - Manage','SALES','CONTRACT','MANAGE','عقود المبيعات - إدارة','Sales Contract - Manage','Create and edit sales contracts, change status, renew and generate scheduled deliveries (sales spec section 10, migration 101)',403,0);
UPDATE permissions SET group_code='SALES',feature_code='CONTRACT',operation_code='MANAGE',name_ar='عقود المبيعات - إدارة',name_en='Sales Contract - Manage',display_order=403,is_sensitive=0 WHERE permission_code='SALES.CONTRACT.MANAGE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.CONTRACT.VIEW','SALES.CONTRACT.MANAGE');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.CONTRACT.VIEW');
-- @shoutech-migration 102_sales_invoice_completion.sql 51C713F08D33102D0ACA794C01E837DC3FC2B6F15E4F4603B1037F034CB3ED8A CA44FB8E3B00CFA2B497A94F632AE8A7303FFE3E9D3D907759BBA80A56877A16 CA44FB8E3B00CFA2B497A94F632AE8A7303FFE3E9D3D907759BBA80A56877A16 CA44FB8E3B00CFA2B497A94F632AE8A7303FFE3E9D3D907759BBA80A56877A16
-- ============================================================================
-- 102 - Sales invoice completion (SQLite)
--
-- Owner sales spec section 5 (Sales_Cycle.txt), workstream W5: the three
-- remaining invoice gaps —
--   1. CONSOLIDATED INVOICES: one SAL invoice drawing lines from MULTIPLE
--      posted delivery notes. document_links already supports many
--      DLV -> SAL links for one invoice, but fulfilment, order progress
--      and the B4 delivery-COGS (1215) relief all matched invoice lines
--      to a source by line_no alone, which is ambiguous the moment two
--      deliveries contribute lines. sales_document_lines therefore gains
--      explicit per-line source attribution:
--        source_doc_id   — the delivery note the line was drawn from
--        source_line_no  — that delivery's line number the line draws on
--      Chain-created single-source lines keep NULL attribution and the
--      historical line_no identity; consolidated lines always carry it.
--      Every consumer (conversion remaining-qty, per-order per_billed,
--      posting-time progress refresh, 1215 relief) prefers the explicit
--      attribution when present and falls back to the legacy rule when
--      it is absent, so pre-102 documents behave exactly as before.
--   2. EXPORT INVOICE FIELDS on the sales header: is_export flags an
--      export invoice; incoterm / ports / country of origin / customs
--      reference / vessel-or-flight / gross+net weights ride alongside.
--      Stored for every sales document kind, edited on InvoiceWin for
--      sales invoices flagged as export.
--
-- Idempotent: column adds guarded by the migrator directive, index
-- CREATE IF NOT EXISTS; the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Consolidated-invoice line source attribution ---------------------------
-- @if-column-missing sales_document_lines.source_doc_id
ALTER TABLE sales_document_lines ADD COLUMN source_doc_id INTEGER;
-- @if-column-missing sales_document_lines.source_line_no
ALTER TABLE sales_document_lines ADD COLUMN source_line_no INTEGER;
CREATE INDEX IF NOT EXISTS ix_sales_document_lines_source_doc
    ON sales_document_lines(source_doc_id) WHERE source_doc_id IS NOT NULL;

-- ---- Export invoice header fields --------------------------------------------
-- @if-column-missing sales_documents.is_export
ALTER TABLE sales_documents ADD COLUMN is_export INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing sales_documents.incoterm
ALTER TABLE sales_documents ADD COLUMN incoterm TEXT;
-- @if-column-missing sales_documents.port_of_loading
ALTER TABLE sales_documents ADD COLUMN port_of_loading TEXT;
-- @if-column-missing sales_documents.port_of_discharge
ALTER TABLE sales_documents ADD COLUMN port_of_discharge TEXT;
-- @if-column-missing sales_documents.country_of_origin
ALTER TABLE sales_documents ADD COLUMN country_of_origin TEXT;
-- @if-column-missing sales_documents.customs_ref
ALTER TABLE sales_documents ADD COLUMN customs_ref TEXT;
-- @if-column-missing sales_documents.vessel_flight
ALTER TABLE sales_documents ADD COLUMN vessel_flight TEXT;
-- @if-column-missing sales_documents.gross_weight
ALTER TABLE sales_documents ADD COLUMN gross_weight NUMERIC;
-- @if-column-missing sales_documents.net_weight
ALTER TABLE sales_documents ADD COLUMN net_weight NUMERIC;
-- @shoutech-migration 105_sales_level3_crm_warranty_rma.sql 53C84A8C600AEC6E580D12C4A4ADD662AD322A533BE714AA8DA0FB3C8D7FB02C D61E04C574F4A1D42EE75679942561953689DEF3D17CEB6DC25B3D0E8BE68104 D61E04C574F4A1D42EE75679942561953689DEF3D17CEB6DC25B3D0E8BE68104 D61E04C574F4A1D42EE75679942561953689DEF3D17CEB6DC25B3D0E8BE68104
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
-- @shoutech-migration 106_sre_recurring_schedules.sql DE2AD667C05AFACBA6CA11588F9574E17D73F1A8B2EE67FE39CE4498537E6B3D 7B03B0955F3C513B0E80AC40924BC37E2F04B6FCD53DE14AFFEBF7BEF1F24542 7B03B0955F3C513B0E80AC40924BC37E2F04B6FCD53DE14AFFEBF7BEF1F24542 7B03B0955F3C513B0E80AC40924BC37E2F04B6FCD53DE14AFFEBF7BEF1F24542
-- ============================================================================
-- 106 - Recurring sales schedules (SQLite)
--
-- Owner sales spec sections 3 + 5 (Sales_Cycle.txt), workstream W11: sales
-- orders and sales invoices recur from a TEMPLATE document. This migration
-- builds the generic template-based recurring engine (the mechanical basis
-- the L4 subscription layer will later sit on; no subscription-specific
-- shape is baked in here):
--   1. sre_recurring_schedules — one row per schedule: the template sales
--      document (sales_documents doc_id, kind SO or SAL — the generated
--      document always has the SAME kind as its template), the interval
--      (Daily / Weekly / Monthly / Yearly x count), the start / next-run /
--      end dates, an optional occurrence cap, the occurrences-created
--      counter, the lifecycle status (Active / Paused / Completed /
--      Cancelled), the auto-post flag and the last-run outcome, so a
--      failed generation or a failed auto-post is RECORDED on the
--      schedule and never silent.
--   2. sre_recurring_runs — the per-occurrence ledger. The UNIQUE
--      (schedule_id, occurrence_no) index is the idempotency marker of
--      the engine: an occurrence is CLAIMED (row inserted + schedule
--      advanced, one transaction) before its document is cloned, so
--      re-running generation for the same date can never create a second
--      document for the same occurrence.
--   3. sales_documents.recurring_schedule_id — the stamp every generated
--      document carries back to its schedule (a document_links row
--      template -> generated is also written by the generation service,
--      following the chain-link convention of migration 024).
--
-- Conventions mirror 097/100: company keying is the company CODE (TEXT) —
-- the party / master-data world; the template document itself is reached
-- through sales_documents, whose company key the service resolves.
-- Dates are ISO yyyy-MM-dd TEXT, like every other sales-cycle table.
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, the column add is
-- guarded by the migrator directive, permission seeds are
-- INSERT OR IGNORE + UPDATE; the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Recurring schedules ----------------------------------------------------
CREATE TABLE IF NOT EXISTS sre_recurring_schedules (
    schedule_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    template_doc_id INTEGER NOT NULL,
    template_doc_kind TEXT NOT NULL CHECK (template_doc_kind IN ('SO','SAL')),
    template_doc_no TEXT NOT NULL DEFAULT '',
    interval_type TEXT NOT NULL CHECK (interval_type IN ('Daily','Weekly','Monthly','Yearly')),
    interval_count INTEGER NOT NULL CHECK (interval_count >= 1),
    start_date TEXT NOT NULL,
    next_run_date TEXT NOT NULL,
    end_date TEXT,
    max_occurrences INTEGER CHECK (max_occurrences IS NULL OR max_occurrences >= 1),
    occurrences_created INTEGER NOT NULL DEFAULT 0,
    auto_post INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Paused','Completed','Cancelled')),
    last_run_date TEXT,
    last_run_status TEXT,
    last_generated_doc_id INTEGER,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at TEXT,
    created_by TEXT,
    updated_by TEXT
);
CREATE INDEX IF NOT EXISTS ix_sre_schedules_company_status
    ON sre_recurring_schedules(company_id, status) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_sre_schedules_due
    ON sre_recurring_schedules(status, next_run_date) WHERE is_deleted = 0;

-- ---- Per-occurrence run ledger (idempotency marker + audit) -----------------
CREATE TABLE IF NOT EXISTS sre_recurring_runs (
    run_id TEXT NOT NULL PRIMARY KEY,
    schedule_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    occurrence_no INTEGER NOT NULL,
    run_date TEXT NOT NULL,
    generated_doc_id INTEGER,
    outcome TEXT NOT NULL DEFAULT 'Pending'
        CHECK (outcome IN ('Pending','Draft','Posted','PostFailed','Failed')),
    error_text TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at TEXT,
    created_by TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sre_runs_schedule_occurrence
    ON sre_recurring_runs(schedule_id, occurrence_no);
CREATE INDEX IF NOT EXISTS ix_sre_runs_schedule
    ON sre_recurring_runs(schedule_id, occurrence_no);

-- ---- Generated-document stamp -------------------------------------------------
-- @if-column-missing sales_documents.recurring_schedule_id
ALTER TABLE sales_documents ADD COLUMN recurring_schedule_id TEXT;
CREATE INDEX IF NOT EXISTS ix_sales_documents_recurring_schedule
    ON sales_documents(recurring_schedule_id) WHERE recurring_schedule_id IS NOT NULL;

-- ---- Permissions: SALES.RECURRING.VIEW / MANAGE (display_order after 400) ----
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.VIEW','Sales Recurring - View','SALES','RECURRING','VIEW','المبيعات المتكررة - عرض','Sales Recurring - View','View recurring sales schedules and their generation history (sales spec sections 3 and 5, migration 106)',401,0);
UPDATE permissions SET group_code='SALES',feature_code='RECURRING',operation_code='VIEW',name_ar='المبيعات المتكررة - عرض',name_en='Sales Recurring - View',display_order=401,is_sensitive=0 WHERE permission_code='SALES.RECURRING.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.RECURRING.MANAGE','Sales Recurring - Manage','SALES','RECURRING','MANAGE','المبيعات المتكررة - إدارة','Sales Recurring - Manage','Create, pause, resume, cancel and run recurring sales schedules (sales spec sections 3 and 5, migration 106)',402,1);
UPDATE permissions SET group_code='SALES',feature_code='RECURRING',operation_code='MANAGE',name_ar='المبيعات المتكررة - إدارة',name_en='Sales Recurring - Manage',display_order=402,is_sensitive=1 WHERE permission_code='SALES.RECURRING.MANAGE';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.RECURRING.VIEW','SALES.RECURRING.MANAGE');
