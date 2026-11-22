-- 082 - Company-scoped purchase three-way match policy and immutable decisions.
-- Policies are opt-in. Absence of a row means enforcement is not configured.
-- This migration only creates persistence; enforcement is integrated separately.
CREATE TABLE IF NOT EXISTS purchase_match_policies (
    company_code varchar(50) PRIMARY KEY REFERENCES companies(company_code),
    is_enabled boolean NOT NULL DEFAULT false,
    require_receipt_match boolean NOT NULL DEFAULT true,
    price_warning_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (price_warning_percent >= 0),
    price_block_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (price_block_percent >= price_warning_percent),
    tax_warning_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (tax_warning_percent >= 0),
    tax_block_percent numeric(12, 6) NOT NULL DEFAULT 0 CHECK (tax_block_percent >= tax_warning_percent),
    base_variance_warning_amount numeric(20, 6) NOT NULL DEFAULT 0 CHECK (base_variance_warning_amount >= 0),
    base_variance_block_amount numeric(20, 6) NOT NULL DEFAULT 0 CHECK (base_variance_block_amount >= base_variance_warning_amount),
    policy_version integer NOT NULL DEFAULT 1 CHECK (policy_version > 0),
    created_by varchar(100),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_by varchar(100),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS purchase_match_evaluations (
    evaluation_id uuid PRIMARY KEY,
    company_code varchar(50) NOT NULL REFERENCES companies(company_code),
    invoice_doc_id bigint NOT NULL,
    evaluation_version integer NOT NULL CHECK (evaluation_version > 0),
    policy_version integer NOT NULL CHECK (policy_version > 0),
    decision varchar(20) NOT NULL CHECK (decision IN ('NotConfigured', 'Pass', 'Warning', 'Blocked')),
    reason_codes_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    policy_snapshot_json jsonb NOT NULL,
    result_snapshot_json jsonb NOT NULL,
    evaluated_by varchar(100),
    evaluated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (company_code, invoice_doc_id, evaluation_version)
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_evaluations_invoice
    ON purchase_match_evaluations(company_code, invoice_doc_id, evaluation_version DESC);

CREATE TABLE IF NOT EXISTS purchase_match_overrides (
    override_id uuid PRIMARY KEY,
    company_code varchar(50) NOT NULL REFERENCES companies(company_code),
    invoice_doc_id bigint NOT NULL,
    evaluation_id uuid NOT NULL REFERENCES purchase_match_evaluations(evaluation_id),
    previous_decision varchar(20) NOT NULL CHECK (previous_decision = 'Blocked'),
    released_by varchar(100) NOT NULL,
    release_reason text NOT NULL CHECK (length(trim(release_reason)) > 0),
    released_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_purchase_match_overrides_invoice
    ON purchase_match_overrides(company_code, invoice_doc_id, released_at);

-- Evaluation and override rows are historical accounting evidence.
-- Corrections must append a new evaluation or override, never mutate history.
CREATE OR REPLACE FUNCTION reject_purchase_match_history_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Purchase match history is immutable.';
END;
$$;

DROP TRIGGER IF EXISTS trg_purchase_match_evaluations_immutable ON purchase_match_evaluations;
CREATE TRIGGER trg_purchase_match_evaluations_immutable
BEFORE UPDATE OR DELETE ON purchase_match_evaluations
FOR EACH ROW EXECUTE FUNCTION reject_purchase_match_history_mutation();

DROP TRIGGER IF EXISTS trg_purchase_match_overrides_immutable ON purchase_match_overrides;
CREATE TRIGGER trg_purchase_match_overrides_immutable
BEFORE UPDATE OR DELETE ON purchase_match_overrides
FOR EACH ROW EXECUTE FUNCTION reject_purchase_match_history_mutation();
