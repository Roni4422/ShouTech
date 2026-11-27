-- 126 - Composite barcode template (docs/OPTWIN_SECTIONS.md §3.4,
-- Landing 3). One row per company: the active scale-label template
-- (prefix + fixed segment positions) the scan paths split incoming
-- codes with. No row (or is_active = 0) = no composite parsing.
CREATE TABLE IF NOT EXISTS barcode_templates (
    company_id      TEXT    NOT NULL PRIMARY KEY,
    prefix          TEXT    NOT NULL,
    total_length    INTEGER NOT NULL,
    code_start      INTEGER NOT NULL,
    code_length     INTEGER NOT NULL,
    wint_start      INTEGER NOT NULL,
    wint_length     INTEGER NOT NULL,
    wfrac_start     INTEGER NOT NULL,
    wfrac_length    INTEGER NOT NULL,
    is_active       INTEGER NOT NULL DEFAULT 1,
    updated_by      TEXT    NULL,
    updated_at      TEXT    NULL
);
