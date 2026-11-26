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
