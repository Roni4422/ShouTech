-- 091 - Persist requisition conversion reservations without expanding the status CHECK constraint (SQLite).
-- The UI projects Approved + conversion_started_at as the transient Converting state.
-- @if-column-missing purchase_requisition_workflow.conversion_started_at
ALTER TABLE purchase_requisition_workflow ADD COLUMN conversion_started_at TEXT;
-- @endif
