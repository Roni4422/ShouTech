-- 127 - Recalculate Costs GL restatement (feat/recalc-costs-gl).
-- The recalculation run record gains the range end date and the list
-- of journal entries its approved adjustments posted; the new lines
-- table carries every cost change of a run (per warehouse + material)
-- and the journal entry that restated that material, so each posted
-- adjustment traces back to the exact cost changes that produced it.
CREATE TABLE IF NOT EXISTS stock_revaluation_job_lines (
    job_id           TEXT    NOT NULL,
    company_id       TEXT    NOT NULL,
    warehouse_id     INTEGER NOT NULL,
    material_id      INTEGER NOT NULL,
    qty_on_hand      NUMERIC NOT NULL DEFAULT 0,
    old_avg_cost     NUMERIC NOT NULL DEFAULT 0,
    new_avg_cost     NUMERIC NOT NULL DEFAULT 0,
    old_value        NUMERIC NOT NULL DEFAULT 0,
    new_value        NUMERIC NOT NULL DEFAULT 0,
    value_delta      NUMERIC NOT NULL DEFAULT 0,
    journal_entry_id TEXT    NULL,
    PRIMARY KEY (job_id, warehouse_id, material_id)
);
CREATE INDEX IF NOT EXISTS ix_stock_revaluation_job_lines_job
    ON stock_revaluation_job_lines(job_id);

-- @if-column-missing stock_revaluation_jobs.to_date
ALTER TABLE stock_revaluation_jobs ADD COLUMN to_date TEXT;
-- @if-column-missing stock_revaluation_jobs.gl_entry_ids
ALTER TABLE stock_revaluation_jobs ADD COLUMN gl_entry_ids TEXT;
