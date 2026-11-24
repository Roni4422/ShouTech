-- 127 - Period close support (docs/PERIOD_CLOSE_STUDY.md).
-- (1) fiscal_period_events: the period state-change ledger. CLOSE rows are
-- written by the close engine inside the close transaction; REOPEN rows are
-- written by the audited reopen path and are what re-arms the reopen
-- trigger below. LOCK is reserved for a future lock action.
-- (2) fiscal_years.period_end_activation: the fiscal-period activation
-- control (owner evidence, study SS7). Default ON: every existing year and
-- every newly created year starts activated; deactivating it is the
-- audited roll-forward into the next financial year.
-- (3) trg_prevent_period_reopen is re-armed: CLOSED -> OPEN is allowed only
-- when a REOPEN event for the period exists recorded at or after the
-- period's closed_at. LOCKED stays terminal in both directions.
CREATE TABLE IF NOT EXISTS fiscal_period_events (
    event_id         INTEGER PRIMARY KEY AUTOINCREMENT,
    fiscal_period_id TEXT    NOT NULL,
    company_id       TEXT    NOT NULL,
    event_type       TEXT    NOT NULL CHECK (event_type IN ('CLOSE','REOPEN','LOCK')),
    actor            TEXT    NOT NULL,
    reason           TEXT    NULL,
    recorded_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_fiscal_period_events_period
    ON fiscal_period_events (fiscal_period_id, event_type, recorded_at);

ALTER TABLE fiscal_years ADD COLUMN period_end_activation INTEGER NOT NULL DEFAULT 1;

DROP TRIGGER IF EXISTS trg_prevent_period_reopen;
CREATE TRIGGER trg_prevent_period_reopen
BEFORE UPDATE OF status ON fiscal_periods
WHEN (OLD.status='LOCKED' AND NEW.status<>'LOCKED')
  OR (OLD.status='CLOSED' AND NEW.status='OPEN' AND NOT EXISTS (
        SELECT 1 FROM fiscal_period_events e
        WHERE e.fiscal_period_id = OLD.fiscal_period_id
          AND e.event_type = 'REOPEN'
          AND e.recorded_at >= COALESCE(OLD.closed_at, '')))
BEGIN
    SELECT RAISE(ABORT, 'Closed or locked fiscal periods cannot be reopened through direct database updates');
END;
