-- Migration 036 (PostgreSQL): Definitions framework tables (ribbon completion F1). PostgreSQL counterpart of sqlite/036_definitions.sql.
-- Owns: doc-type definitions, payment methods (+ global seed rows), POS
-- terminals, areas, targets, discount rules, customer prices, loyalty
-- accounts/entries, handheld devices, device routes, drivers, delivery
-- prices/requests, driver settlements, custom fields (+values),
-- installments (+schedule). Dates are ISO text (yyyy-MM-dd) on both
-- providers. Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS doc_type_definitions (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    set_kind TEXT NOT NULL CHECK (set_kind IN ('VOUCHER','SECURITY','PAYMENT','INVOICE','ORDER')),
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    gl_account_code TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_doc_type_definitions_company_kind_code
    ON doc_type_definitions(company_id, set_kind, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS payment_methods (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    kind TEXT NOT NULL CHECK (kind IN ('Cash','BankTransfer','Cheque','Card','Other')),
    gl_account_code TEXT,
    settlement_bank_code TEXT,
    commission_pct REAL NOT NULL DEFAULT 0 CHECK (commission_pct >= 0 AND commission_pct <= 100),
    commission_account_code TEXT,
    requires_reference BOOLEAN NOT NULL DEFAULT FALSE,
    is_pos_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    pos_sort_order INTEGER NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_methods_company_code
    ON payment_methods(company_id, code) WHERE is_deleted = FALSE;

-- Global default payment methods (company_id '' = shared defaults; a company
-- saving one of these creates its own override row).
INSERT INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-cash', '', 'CASH', 'Cash', NULL, 'Cash', TRUE, 1, 'seed') ON CONFLICT DO NOTHING;
INSERT INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-bank', '', 'BANK', 'Bank Transfer', NULL, 'BankTransfer', FALSE, 2, 'seed') ON CONFLICT DO NOTHING;
INSERT INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-cheque', '', 'CHEQUE', 'Cheque', NULL, 'Cheque', FALSE, 3, 'seed') ON CONFLICT DO NOTHING;
INSERT INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-visa', '', 'VISA', 'Visa', NULL, 'Card', TRUE, 4, 'seed') ON CONFLICT DO NOTHING;
INSERT INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-mc', '', 'MC', 'Mastercard', NULL, 'Card', TRUE, 5, 'seed') ON CONFLICT DO NOTHING;
INSERT INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-mada', '', 'MADA', 'mada', NULL, 'Card', TRUE, 6, 'seed') ON CONFLICT DO NOTHING;
INSERT INTO payment_methods (id, company_id, code, name_en, name_ar, kind, is_pos_enabled, pos_sort_order, created_by)
VALUES ('seed-pm-amex', '', 'AMEX', 'American Express', NULL, 'Card', TRUE, 7, 'seed') ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS pos_terminals (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    warehouse_code TEXT,
    cash_account_code TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_terminals_company_code
    ON pos_terminals(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS areas (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    parent_id TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_areas_company_code
    ON areas(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS targets (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    owner_kind TEXT NOT NULL CHECK (owner_kind IN ('PARTY','REP')),
    owner_code TEXT NOT NULL,
    period_kind TEXT NOT NULL CHECK (period_kind IN ('M','Q','Y')),
    period_start TEXT NOT NULL,
    target_amount REAL NOT NULL CHECK (target_amount >= 0),
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_targets_company_owner
    ON targets(company_id, owner_kind, owner_code);

CREATE TABLE IF NOT EXISTS discount_rules (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    scope TEXT NOT NULL CHECK (scope IN ('MATERIAL','CUSTOMER','POS')),
    target_code TEXT NOT NULL,
    discount_pct REAL NOT NULL CHECK (discount_pct >= 0 AND discount_pct <= 100),
    min_qty REAL NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_discount_rules_company_scope
    ON discount_rules(company_id, scope, target_code);

CREATE TABLE IF NOT EXISTS customer_prices (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_code TEXT NOT NULL,
    material_code TEXT NOT NULL,
    price REAL NOT NULL CHECK (price >= 0),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_customer_prices_company_party_material
    ON customer_prices(company_id, party_code, material_code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS loyalty_accounts (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_code TEXT NOT NULL,
    points_balance REAL NOT NULL DEFAULT 0,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_loyalty_accounts_company_party
    ON loyalty_accounts(company_id, party_code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS loyalty_entries (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    account_id TEXT NOT NULL REFERENCES loyalty_accounts(id),
    entry_date TEXT NOT NULL,
    points REAL NOT NULL,
    doc_ref TEXT,
    notes TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_loyalty_entries_account
    ON loyalty_entries(account_id);

CREATE TABLE IF NOT EXISTS handheld_devices (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    rep_code TEXT,
    device_serial TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_handheld_devices_company_code
    ON handheld_devices(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS device_routes (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    device_code TEXT NOT NULL,
    rep_code TEXT,
    area_code TEXT,
    weekday INTEGER NOT NULL CHECK (weekday >= 0 AND weekday <= 6),
    visit_order INTEGER NOT NULL DEFAULT 0,
    party_code TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_device_routes_company_device
    ON device_routes(company_id, device_code);

CREATE TABLE IF NOT EXISTS drivers (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    phone TEXT,
    vehicle_no TEXT,
    license_no TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_drivers_company_code
    ON drivers(company_id, code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS delivery_prices (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    area_code TEXT NOT NULL,
    price REAL NOT NULL CHECK (price >= 0),
    min_order REAL NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_prices_company_area
    ON delivery_prices(company_id, area_code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS delivery_requests (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_no INTEGER NOT NULL,
    request_date TEXT NOT NULL,
    party_code TEXT,
    address TEXT,
    area_code TEXT,
    driver_code TEXT,
    status TEXT NOT NULL DEFAULT 'Pending',
    amount REAL NOT NULL DEFAULT 0,
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_delivery_requests_company_docno
    ON delivery_requests(company_id, doc_no) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS driver_settlements (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    driver_code TEXT NOT NULL,
    settle_date TEXT NOT NULL,
    collected_amount REAL NOT NULL DEFAULT 0,
    fees_amount REAL NOT NULL DEFAULT 0,
    net_amount REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open',
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_driver_settlements_company_driver
    ON driver_settlements(company_id, driver_code);

CREATE TABLE IF NOT EXISTS custom_field_definitions (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL,
    field_code TEXT NOT NULL,
    name_en TEXT NOT NULL,
    name_ar TEXT,
    field_type TEXT NOT NULL CHECK (field_type IN ('TEXT','NUMBER','DATE','BOOL')),
    is_required BOOLEAN NOT NULL DEFAULT FALSE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_custom_field_definitions_company_entity_field
    ON custom_field_definitions(company_id, entity_kind, field_code) WHERE is_deleted = FALSE;

CREATE TABLE IF NOT EXISTS custom_field_values (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL,
    entity_code TEXT NOT NULL,
    field_code TEXT NOT NULL,
    value_text TEXT,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_custom_field_values_company_entity_field
    ON custom_field_values(company_id, entity_kind, entity_code, field_code);

CREATE TABLE IF NOT EXISTS installments (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_code TEXT NOT NULL,
    doc_ref TEXT,
    total_amount REAL NOT NULL CHECK (total_amount > 0),
    down_payment REAL NOT NULL DEFAULT 0 CHECK (down_payment >= 0),
    installment_count INTEGER NOT NULL CHECK (installment_count > 0),
    start_date TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Active',
    notes TEXT,
    is_deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_installments_company_party
    ON installments(company_id, party_code);

CREATE TABLE IF NOT EXISTS installment_schedule (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    installment_id TEXT NOT NULL REFERENCES installments(id),
    seq INTEGER NOT NULL,
    due_date TEXT NOT NULL,
    amount REAL NOT NULL CHECK (amount >= 0),
    paid_amount REAL NOT NULL DEFAULT 0,
    paid_date TEXT,
    status TEXT NOT NULL DEFAULT 'Due',
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by TEXT,
    updated_at TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS ix_installment_schedule_installment
    ON installment_schedule(installment_id);
