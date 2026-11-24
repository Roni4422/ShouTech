-- 123: per-series numbering configuration (NUMBERING_STUDY §3.4).
-- One row per (company, series): printed prefix, digit padding, yearly
-- segmentation and the start floor. No row = built-in defaults. The
-- counters themselves stay in document_number_sequences (migration 075);
-- configuration never renumbers history and never moves a counter back.
CREATE TABLE IF NOT EXISTS number_series_config (
    company_id     TEXT    NOT NULL,
    series_key     TEXT    NOT NULL,
    prefix         TEXT    NULL,
    padding        INTEGER NULL,
    yearly_segment INTEGER NOT NULL DEFAULT 1,
    start_value    INTEGER NOT NULL DEFAULT 1,
    updated_by     TEXT    NULL,
    updated_at     TEXT    NULL,
    PRIMARY KEY (company_id, series_key)
);
