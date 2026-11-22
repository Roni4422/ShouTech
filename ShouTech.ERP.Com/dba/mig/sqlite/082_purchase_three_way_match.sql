-- 082 - Company-scoped purchase three-way match policy and immutable decisions.
-- Policies are opt-in. Absence of a row means enforcement is not configured.
-- This migration only creates persistence; enforcement is integrated separately.
CREATE TABLE IF NOT EXISTS purchase_match_policies (
    company_code TEXT PRIMARY KEY REFERENCES companies(company_code),
    is_enabled INTEGER NOT NULL DEFAULT 0 CHECK (is_enabled IN (0, 1)),
    require_receipt_match INTEGER NOT NULL DEFAULT 1 CHECK (require_receipt_match IN (0, 1)),
    price_warning_percent NUMERIC NOT NULL DEFAULT 0 CHECK (price_warning_percent >= 0),
    price_block_percent NUMERIC NOT NULL DEFAULT 0 CHECK (price_block_percent >= price_warning_percent),
    tax_warning_percent NUMERIC NOT NULL DEFAULT 0 CHECK (tax_warning_percent >= 0),
    tax_block_percent NUMERIC NOT NULL DEFAULT 0 CHECK (tax_block_percent >= tax_warning_percent),
    base_variance_warning_amount NUMERIC NOT NULL DEFAULT 0 CHECK (base_variance_warning_amount >= 0),
    base_variance_block_amount NUMERIC NOT NULL DEFAULT 0 CHECK (base_variance_block_amount >= base_variance_warning_amount),
    policy_version INTEGER NOT NULL DEFAULT 1 CHECK (policy_version > 0),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by TEXT,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS purchase_match_evaluations (
    evaluation_id TEXT PRIMARY KEY,
    company_code TEXT NOT NULL REFERENCES companies(company_code),
    invoice_doc_id INTEGER NOT NULL,
    evaluation_version INTEGER NOT NULL CHECK (evaluation_version > 0),
    policy_version INTEGER NOT NULL CHECK (policy_version > 0),
    decision TEXT NOT NULL CHECK (decision IN ('NotConfigured', 'Pass', 'Warning', 'Blocked')),
    reason_codes_json TEXT NOT NULL DEFAULT '[]',
    policy_snapshot_json TEXT NOT NULL,
    result_snapshot_json TEXT NOT NULL,
    evaluated_by TEXT,
    evaluated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (company_code, invoice_doc_id, evaluation_version)
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_evaluations_invoice
    ON purchase_match_evaluations(company_code, invoice_doc_id, evaluation_version DESC);

CREATE TABLE IF NOT EXISTS purchase_match_overrides (
    override_id TEXT PRIMARY KEY,
    company_code TEXT NOT NULL REFERENCES companies(company_code),
    invoice_doc_id INTEGER NOT NULL,
    evaluation_id TEXT NOT NULL REFERENCES purchase_match_evaluations(evaluation_id),
    previous_decision TEXT NOT NULL CHECK (previous_decision = 'Blocked'),
    released_by TEXT NOT NULL,
    release_reason TEXT NOT NULL CHECK (length(trim(release_reason)) > 0),
    released_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_overrides_invoice
    ON purchase_match_overrides(company_code, invoice_doc_id, released_at);
