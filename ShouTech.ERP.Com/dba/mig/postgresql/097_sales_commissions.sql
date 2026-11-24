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
