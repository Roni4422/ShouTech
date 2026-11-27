-- Delete audit storage is DBA-owned; service code only records audit events.
CREATE TABLE IF NOT EXISTS deletion_audit_log (
    event_id TEXT PRIMARY KEY,
    company_code TEXT NOT NULL,
    table_name TEXT NOT NULL,
    record_key TEXT NOT NULL,
    action_type TEXT NOT NULL,
    actor TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    method TEXT NOT NULL,
    reason TEXT,
    record_snapshot TEXT
);
CREATE INDEX IF NOT EXISTS ix_deletion_audit_company_time
    ON deletion_audit_log(company_code, occurred_at);
