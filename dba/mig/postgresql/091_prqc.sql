-- 091 - Persist requisition conversion reservations without expanding the status CHECK constraint (PostgreSQL).
-- The UI projects Approved + conversion_started_at as the transient Converting state.
ALTER TABLE purchase_requisition_workflow
    ADD COLUMN IF NOT EXISTS conversion_started_at TIMESTAMPTZ;
