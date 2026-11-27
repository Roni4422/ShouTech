-- 093 - Stored sales-chain progress + documented cancellation (PostgreSQL).
-- per_delivered / per_billed are the ERPNext-style stored fulfilment
-- percentages (quantity-weighted, 0-100) for chain documents. They are
-- recomputed transactionally by OrderDocumentRepository whenever a chain
-- document is created, posted, or converted, and back the derived status
-- (To Deliver and Bill / To Deliver / To Bill / Completed) shown by the
-- order windows. Existing rows start at 0 and are refreshed on the next
-- chain event for their order.
-- cancel_reason / cancelled_by / cancelled_at implement the sales-cycle
-- specification's documented cancellation (spec sections 3/14): every
-- cancellation of an order or quotation records WHY, by whom, and when.
-- The columns are added to purchase_documents as well because
-- OrderDocumentRepository.SetStatusAsync and OrdWin serve both families
-- through one code path; a reason must never be collected and dropped.
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS per_delivered NUMERIC(9,2) NOT NULL DEFAULT 0;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS per_billed NUMERIC(9,2) NOT NULL DEFAULT 0;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS cancelled_by TEXT;
ALTER TABLE sales_documents
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT;
ALTER TABLE purchase_documents
    ADD COLUMN IF NOT EXISTS cancel_reason TEXT;
ALTER TABLE purchase_documents
    ADD COLUMN IF NOT EXISTS cancelled_by TEXT;
ALTER TABLE purchase_documents
    ADD COLUMN IF NOT EXISTS cancelled_at TEXT;
