CREATE VIEW IF NOT EXISTS journal_events AS
SELECT
    journal_audit_event_id AS event_id,
    journal_entry_id,
    event_type,
    actor,
    event_at,
    details
FROM journal_audit_events;
