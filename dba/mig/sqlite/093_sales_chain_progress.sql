-- 093 - Stored sales-chain progress + documented cancellation (SQLite).
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
-- @if-column-missing sales_documents.per_delivered
ALTER TABLE sales_documents ADD COLUMN per_delivered NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.per_billed
ALTER TABLE sales_documents ADD COLUMN per_billed NUMERIC NOT NULL DEFAULT 0;
-- @endif
-- @if-column-missing sales_documents.cancel_reason
ALTER TABLE sales_documents ADD COLUMN cancel_reason TEXT;
-- @endif
-- @if-column-missing sales_documents.cancelled_by
ALTER TABLE sales_documents ADD COLUMN cancelled_by TEXT;
-- @endif
-- @if-column-missing sales_documents.cancelled_at
ALTER TABLE sales_documents ADD COLUMN cancelled_at TEXT;
-- @endif
-- @if-column-missing purchase_documents.cancel_reason
ALTER TABLE purchase_documents ADD COLUMN cancel_reason TEXT;
-- @endif
-- @if-column-missing purchase_documents.cancelled_by
ALTER TABLE purchase_documents ADD COLUMN cancelled_by TEXT;
-- @endif
-- @if-column-missing purchase_documents.cancelled_at
ALTER TABLE purchase_documents ADD COLUMN cancelled_at TEXT;
-- @endif
