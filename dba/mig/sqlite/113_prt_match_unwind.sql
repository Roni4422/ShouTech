-- 113 - PRT match unwind audit (SQLite, I4).
-- Posting a purchase return (debit note, PRT) decrements the mig-078
-- receipt<->invoice matched quantities so returned goods can be
-- re-invoiced. The exact decrement per match row is recorded here so
-- reversing the PRT re-winds EXACTLY what the posting unwound (never a
-- recomputation), mirroring the landed-cost stored-split rule (L3).

CREATE TABLE IF NOT EXISTS purchase_return_match_unwinds (
    unwind_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    return_doc_id INTEGER NOT NULL REFERENCES purchase_documents(doc_id),
    invoice_doc_id INTEGER NOT NULL,
    match_id TEXT NOT NULL,
    receipt_doc_id INTEGER NOT NULL,
    receipt_line_id INTEGER NOT NULL,
    invoice_line_id INTEGER NOT NULL,
    unwound_qty NUMERIC NOT NULL CHECK (unwound_qty > 0),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT
);

CREATE INDEX IF NOT EXISTS ix_prt_unwinds_return
    ON purchase_return_match_unwinds (company_id, return_doc_id);

CREATE INDEX IF NOT EXISTS ix_prt_unwinds_invoice
    ON purchase_return_match_unwinds (company_id, invoice_doc_id);
