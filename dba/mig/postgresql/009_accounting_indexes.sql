CREATE INDEX IF NOT EXISTS ix_journal_entries_company_date_status
    ON journal_entries(company_id, entry_date, status, journal_entry_id);
CREATE INDEX IF NOT EXISTS ix_journal_entry_lines_entry_account
    ON journal_entry_lines(journal_entry_id, account_id, line_number);
CREATE INDEX IF NOT EXISTS ix_journal_audit_events_entry_time
    ON journal_audit_events(journal_entry_id, event_at, journal_audit_event_id);
