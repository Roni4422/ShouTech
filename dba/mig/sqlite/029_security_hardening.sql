-- Phase 4 enterprise security hardening (SQLite).
-- 1) Login lockout state on users: failed-attempt counter + lock expiry.
--    Enforced by AuthServ using SecuritySettings (MaxLoginAttempts / LockoutDurationMinutes).
ALTER TABLE users ADD COLUMN failed_login_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE users ADD COLUMN locked_until TEXT NULL;

-- 2) Durable security audit trail for authentication and data-governance events
--    (login success/failure, account lockout, data export). Written by
--    SecurityAuditLog (cod/app) on a best-effort basis; never stores passwords.
CREATE TABLE IF NOT EXISTS security_audit_log (
    security_audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NULL,
    user_id TEXT NULL,
    username TEXT NOT NULL,
    event_type TEXT NOT NULL,
    details TEXT NULL,
    occurred_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_security_audit_username_time
    ON security_audit_log(username, occurred_at);
CREATE INDEX IF NOT EXISTS ix_security_audit_event_time
    ON security_audit_log(event_type, occurred_at);
CREATE INDEX IF NOT EXISTS ix_security_audit_company_time
    ON security_audit_log(company_id, occurred_at);

-- 3) Missing indexes on high-traffic transactional tables (company + date/status
--    lookups used by reconciliation, aging and list windows; account statements
--    filter journal lines by account; document reversal lookups by original doc).
CREATE INDEX IF NOT EXISTS ix_sales_documents_company_date
    ON sales_documents(company_id, doc_date);
CREATE INDEX IF NOT EXISTS ix_sales_documents_company_status
    ON sales_documents(company_id, status);
CREATE INDEX IF NOT EXISTS ix_purchase_documents_company_date
    ON purchase_documents(company_id, doc_date);
CREATE INDEX IF NOT EXISTS ix_purchase_documents_company_status
    ON purchase_documents(company_id, status);
CREATE INDEX IF NOT EXISTS ix_journal_entry_lines_account
    ON journal_entry_lines(account_id);
CREATE INDEX IF NOT EXISTS ix_document_reversals_company_original
    ON document_reversals(company_id, original_doc_kind, original_doc_id);
