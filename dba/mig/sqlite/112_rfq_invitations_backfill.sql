-- 112 - RFQ supplier invitations (P1) + P5 backfill (SQLite).
-- Invitations record which suppliers were invited to quote against an
-- RFQ and how each responded; SQ creation flips the invitation to
-- Quoted. Dispatch by email is out of scope (no mail infrastructure
-- decision exists) — invitations are recorded and printable.

CREATE TABLE IF NOT EXISTS rfq_invitations (
    invitation_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    rfq_doc_id INTEGER NOT NULL REFERENCES purchase_documents(doc_id),
    party_id INTEGER NOT NULL,
    party_code TEXT,
    party_name TEXT,
    status TEXT NOT NULL DEFAULT 'Invited'
        CHECK (status IN ('Invited', 'Quoted', 'Declined')),
    invited_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    responded_at TEXT,
    created_by TEXT,
    UNIQUE (company_id, rfq_doc_id, party_id)
);

CREATE INDEX IF NOT EXISTS ix_rfq_invitations_rfq
    ON rfq_invitations (company_id, rfq_doc_id, status);

-- P5 backfill: stored per_received / per_billed for existing posted POs,
-- computed once from the linked receipt / invoice documents (net of
-- posted receipt returns). Mirrors GetProgressAsync semantics
-- (qty-weighted, capped at 100). Idempotent: recomputing yields the
-- same values, and the posting transactions maintain the columns from
-- here on.
UPDATE purchase_documents
SET per_received = CASE
        WHEN (SELECT COALESCE(SUM(qty), 0) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id) <= 0 THEN 0
        ELSE MIN(100, ROUND(100.0 * MAX(0,
            (SELECT COALESCE(SUM(l.qty), 0)
               FROM document_links dl
               JOIN purchase_documents r ON r.doc_id = dl.target_doc_id
                    AND r.doc_kind = 'RCV' AND r.status = 'Posted' AND r.is_deleted = 0
               JOIN purchase_document_lines l ON l.doc_id = r.doc_id
              WHERE dl.company_id = purchase_documents.company_id
                AND dl.source_doc_kind = 'PO' AND dl.source_doc_id = purchase_documents.doc_id
                AND dl.target_doc_kind = 'RCV')
            -
            (SELECT COALESCE(SUM(a.returned_qty), 0)
               FROM purchase_receipt_return_line_allocations a
               JOIN purchase_documents rd ON rd.doc_id = a.return_doc_id
                    AND rd.doc_kind = 'RCR' AND rd.status = 'Posted' AND rd.is_deleted = 0
              WHERE a.company_id = purchase_documents.company_id
                AND a.original_doc_id IN (
                    SELECT dl2.target_doc_id FROM document_links dl2
                     WHERE dl2.company_id = purchase_documents.company_id
                       AND dl2.source_doc_kind = 'PO' AND dl2.source_doc_id = purchase_documents.doc_id
                       AND dl2.target_doc_kind = 'RCV'))
            ) / (SELECT SUM(qty) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id), 2))
    END,
    per_billed = CASE
        WHEN (SELECT COALESCE(SUM(qty), 0) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id) <= 0 THEN 0
        ELSE MIN(100, ROUND(100.0 * MAX(0,
            (SELECT COALESCE(SUM(l.qty), 0)
               FROM document_links dl
               JOIN purchase_documents r ON r.doc_id = dl.target_doc_id
                    AND r.doc_kind = 'PUR' AND r.status = 'Posted' AND r.is_deleted = 0
               JOIN purchase_document_lines l ON l.doc_id = r.doc_id
              WHERE dl.company_id = purchase_documents.company_id
                AND dl.source_doc_kind = 'PO' AND dl.source_doc_id = purchase_documents.doc_id
                AND dl.target_doc_kind = 'PUR')
            ) / (SELECT SUM(qty) FROM purchase_document_lines WHERE doc_id = purchase_documents.doc_id), 2))
    END
WHERE doc_kind = 'PO' AND is_deleted = 0;
