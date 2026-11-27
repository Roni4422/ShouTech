-- @shoutech-migration 010_commercial_documents.sql 127E27ADF70B9894F89994DCCC657819E986F2E0CDEA34CAC77A1100067F1CBA C507EE0F4E429CA3F7E5CA755852B23104591A0352E09D279A9DC08C48D97007 C507EE0F4E429CA3F7E5CA755852B23104591A0352E09D279A9DC08C48D97007 C507EE0F4E429CA3F7E5CA755852B23104591A0352E09D279A9DC08C48D97007
-- Owns commercial document and reversal schema. Runtime services must not create tables.
        CREATE TABLE IF NOT EXISTS sales_documents (
            doc_id INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id TEXT NOT NULL,
            doc_kind TEXT NOT NULL DEFAULT 'SAL',
            doc_no TEXT NOT NULL,
            doc_date TEXT NOT NULL,
            party_id INTEGER,
            party_name TEXT,
            warehouse_id INTEGER,
            currency_code TEXT NOT NULL DEFAULT 'SAR',
            exchange_rate NUMERIC NOT NULL DEFAULT 1,
            subtotal NUMERIC NOT NULL DEFAULT 0,
            discount_amount NUMERIC NOT NULL DEFAULT 0,
            tax_amount NUMERIC NOT NULL DEFAULT 0,
            grand_total NUMERIC NOT NULL DEFAULT 0,
            paid_amount NUMERIC NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'Draft',
            tax_code TEXT,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            notes TEXT,
            created_by TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now')),
            posted_by TEXT,
            posted_at TEXT,
            is_deleted INTEGER NOT NULL DEFAULT 0,
            UNIQUE (company_id, doc_kind, doc_no)
        );

        CREATE TABLE IF NOT EXISTS sales_document_lines (
            line_id INTEGER PRIMARY KEY AUTOINCREMENT,
            doc_id INTEGER NOT NULL REFERENCES sales_documents(doc_id) ON DELETE CASCADE,
            line_no INTEGER NOT NULL,
            material_id INTEGER,
            material_code TEXT,
            description TEXT NOT NULL,
            unit TEXT,
            qty NUMERIC NOT NULL DEFAULT 0,
            unit_price NUMERIC NOT NULL DEFAULT 0,
            discount_pct NUMERIC NOT NULL DEFAULT 0,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            line_subtotal NUMERIC NOT NULL DEFAULT 0,
            line_tax NUMERIC NOT NULL DEFAULT 0,
            line_total NUMERIC NOT NULL DEFAULT 0,
            warehouse_id INTEGER,
            UNIQUE (doc_id, line_no)
        );

        CREATE TABLE IF NOT EXISTS purchase_documents (
            doc_id INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id TEXT NOT NULL,
            doc_kind TEXT NOT NULL DEFAULT 'PUR',
            doc_no TEXT NOT NULL,
            doc_date TEXT NOT NULL,
            party_id INTEGER,
            party_name TEXT,
            warehouse_id INTEGER,
            currency_code TEXT NOT NULL DEFAULT 'SAR',
            exchange_rate NUMERIC NOT NULL DEFAULT 1,
            subtotal NUMERIC NOT NULL DEFAULT 0,
            discount_amount NUMERIC NOT NULL DEFAULT 0,
            tax_amount NUMERIC NOT NULL DEFAULT 0,
            grand_total NUMERIC NOT NULL DEFAULT 0,
            paid_amount NUMERIC NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'Draft',
            tax_code TEXT,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            notes TEXT,
            created_by TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now')),
            posted_by TEXT,
            posted_at TEXT,
            is_deleted INTEGER NOT NULL DEFAULT 0,
            UNIQUE (company_id, doc_kind, doc_no)
        );

        CREATE TABLE IF NOT EXISTS purchase_document_lines (
            line_id INTEGER PRIMARY KEY AUTOINCREMENT,
            doc_id INTEGER NOT NULL REFERENCES purchase_documents(doc_id) ON DELETE CASCADE,
            line_no INTEGER NOT NULL,
            material_id INTEGER,
            material_code TEXT,
            description TEXT NOT NULL,
            unit TEXT,
            qty NUMERIC NOT NULL DEFAULT 0,
            unit_price NUMERIC NOT NULL DEFAULT 0,
            discount_pct NUMERIC NOT NULL DEFAULT 0,
            tax_rate NUMERIC NOT NULL DEFAULT 0,
            line_subtotal NUMERIC NOT NULL DEFAULT 0,
            line_tax NUMERIC NOT NULL DEFAULT 0,
            line_total NUMERIC NOT NULL DEFAULT 0,
            warehouse_id INTEGER,
            UNIQUE (doc_id, line_no)
        );

        CREATE TABLE IF NOT EXISTS party_ledger (
            ledger_id INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id TEXT NOT NULL,
            party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
            party_id INTEGER NOT NULL,
            doc_kind TEXT,
            doc_id INTEGER,
            doc_no TEXT,
            entry_date TEXT NOT NULL,
            debit NUMERIC NOT NULL DEFAULT 0,
            credit NUMERIC NOT NULL DEFAULT 0,
            currency_code TEXT NOT NULL DEFAULT 'SAR',
            note TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS ix_party_ledger_party
            ON party_ledger(company_id, party_kind, party_id);

        CREATE TABLE IF NOT EXISTS stock_movements (
            stock_movement_id INTEGER PRIMARY KEY AUTOINCREMENT,
            company_id TEXT NOT NULL,
            warehouse_id INTEGER,
            product_id INTEGER,
            movement_date TEXT NOT NULL,
            movement_type TEXT NOT NULL,
            document_no TEXT,
            qty_in NUMERIC NOT NULL DEFAULT 0,
            qty_out NUMERIC NOT NULL DEFAULT 0,
            unit_cost NUMERIC NOT NULL DEFAULT 0,
            note TEXT
        );
CREATE TABLE IF NOT EXISTS document_reversals (
    reversal_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    original_doc_kind TEXT NOT NULL,
    original_doc_id INTEGER NOT NULL,
    reversal_doc_kind TEXT NOT NULL,
    reversal_doc_id INTEGER,
    reason TEXT NOT NULL,
    actor TEXT NOT NULL,
    created_at TEXT NOT NULL,
    status TEXT NOT NULL,
    UNIQUE (company_id, original_doc_kind, original_doc_id)
);
-- @shoutech-migration 023_invoice_header_fields_sqlite.sql 6DC8E7AD154351807D875EB5E7D910F11E4BB45039B953467371E4141BD48369 877ACB1C17984746CF2FBC1D873C54876D26456126147E3E9A79B53566B83B22 877ACB1C17984746CF2FBC1D873C54876D26456126147E3E9A79B53566B83B22 877ACB1C17984746CF2FBC1D873C54876D26456126147E3E9A79B53566B83B22
-- Migration 023 (SQLite): expand commercial invoice headers for Phenix-parity fields.
-- Adds payment method, accounts, cost centre, sales rep, reference fields,
-- extra amount, and ensures paid/discount are usable on save.

-- sales_documents
ALTER TABLE sales_documents ADD COLUMN payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE sales_documents ADD COLUMN receipt_no TEXT;
ALTER TABLE sales_documents ADD COLUMN reference_no TEXT;
ALTER TABLE sales_documents ADD COLUMN branch_name TEXT;
ALTER TABLE sales_documents ADD COLUMN sales_rep_name TEXT;
ALTER TABLE sales_documents ADD COLUMN cost_center TEXT;
ALTER TABLE sales_documents ADD COLUMN party_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN goods_account_name TEXT;
ALTER TABLE sales_documents ADD COLUMN warehouse_name TEXT;
ALTER TABLE sales_documents ADD COLUMN extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE sales_documents ADD COLUMN party_code TEXT;

-- purchase_documents
ALTER TABLE purchase_documents ADD COLUMN payment_method TEXT NOT NULL DEFAULT 'Cash';
ALTER TABLE purchase_documents ADD COLUMN receipt_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN reference_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN branch_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN sales_rep_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN cost_center TEXT;
ALTER TABLE purchase_documents ADD COLUMN party_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN goods_account_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN warehouse_name TEXT;
ALTER TABLE purchase_documents ADD COLUMN extra_amount NUMERIC NOT NULL DEFAULT 0;
ALTER TABLE purchase_documents ADD COLUMN party_code TEXT;

-- line warehouse name (optional display; id remains authoritative)
ALTER TABLE sales_document_lines ADD COLUMN warehouse_name TEXT;
ALTER TABLE purchase_document_lines ADD COLUMN warehouse_name TEXT;

-- Original invoice link and posted journal voucher number.
ALTER TABLE sales_documents ADD COLUMN original_doc_id INTEGER;
ALTER TABLE sales_documents ADD COLUMN original_doc_kind TEXT;
ALTER TABLE sales_documents ADD COLUMN original_doc_no TEXT;
ALTER TABLE sales_documents ADD COLUMN journal_doc_no TEXT;

ALTER TABLE purchase_documents ADD COLUMN original_doc_id INTEGER;
ALTER TABLE purchase_documents ADD COLUMN original_doc_kind TEXT;
ALTER TABLE purchase_documents ADD COLUMN original_doc_no TEXT;
ALTER TABLE purchase_documents ADD COLUMN journal_doc_no TEXT;
-- @shoutech-migration 024_document_links.sql C2FCF9020D15C0B8F0505E62EA9DE81C0D4EC7059B226DA3E8F26AE715E148DF EB449BB9CE0FB0529BBE182E6951727489F593ED84824793B150610801A8A564 EB449BB9CE0FB0529BBE182E6951727489F593ED84824793B150610801A8A564 EB449BB9CE0FB0529BBE182E6951727489F593ED84824793B150610801A8A564
-- Owns cross-document chain links (Quotation -> Sales Order -> Delivery Note -> Invoice,
-- and RFQ -> Purchase Order -> Receipt -> Purchase Invoice).
-- A link records that the target document was created from the source document.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS document_links (
    link_id INTEGER PRIMARY KEY AUTOINCREMENT,
    company_id TEXT NOT NULL,
    source_doc_kind TEXT NOT NULL,
    source_doc_id INTEGER NOT NULL,
    target_doc_kind TEXT NOT NULL,
    target_doc_id INTEGER NOT NULL,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, source_doc_kind, source_doc_id, target_doc_kind, target_doc_id)
);

CREATE INDEX IF NOT EXISTS ix_document_links_source
    ON document_links(company_id, source_doc_kind, source_doc_id);

CREATE INDEX IF NOT EXISTS ix_document_links_target
    ON document_links(company_id, target_doc_kind, target_doc_id);
-- @shoutech-migration 025_payment_allocations.sql 312A8BF98B01CF43397D2F842C9C2747F9BBBBC3C1DD160D26522B2AF150287F 4B78D4239E4C1C8B767C4C6A582941F74C64BF633AB454657EE8A293020F07C8 4B78D4239E4C1C8B767C4C6A582941F74C64BF633AB454657EE8A293020F07C8 4B78D4239E4C1C8B767C4C6A582941F74C64BF633AB454657EE8A293020F07C8
-- Owns payment-to-document allocations for the Payment Reconciliation window.
-- An allocation records that part of a party_ledger payment entry settles part
-- of an open sales/purchase document. Amounts are validated again inside the
-- allocation transaction (open amount and unallocated payment amount are
-- re-read under the transaction), so a double submission cannot over-allocate.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS payment_allocations (
    allocation_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    party_kind TEXT NOT NULL CHECK (party_kind IN ('C','S')),
    party_id INTEGER NOT NULL,
    payment_ledger_id INTEGER NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES','PURCHASE')),
    doc_id INTEGER NOT NULL,
    doc_no TEXT,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    allocated_at TEXT NOT NULL DEFAULT (datetime('now')),
    note TEXT
);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_payment
    ON payment_allocations(company_id, payment_ledger_id);

CREATE INDEX IF NOT EXISTS ix_payment_allocations_doc
    ON payment_allocations(company_id, doc_table, doc_id);
-- @shoutech-migration 032_document_order_fields.sql B2BF6110F96D5C28C15C7ECF19224CA7BA796910A85BE46C625E2A69468EE451 3D04D593486FED613DF31BAA957EDFC6CEA437AA8C87DA141BF6B81D27E87A20 3D04D593486FED613DF31BAA957EDFC6CEA437AA8C87DA141BF6B81D27E87A20 3D04D593486FED613DF31BAA957EDFC6CEA437AA8C87DA141BF6B81D27E87A20
-- Migration 032 (SQLite): order/quotation scheduling fields for the
-- commercial document engine (ribbon completion, RS-5/RS-6).
-- delivery_date: promised delivery (sales) / required-by (purchase) date.
-- valid_till: quotation validity deadline (ERPNext Quotation pattern);
-- conversion of an expired quotation is blocked in the service layer.
-- Runtime services must not create tables.

ALTER TABLE sales_documents ADD COLUMN delivery_date TEXT;
ALTER TABLE sales_documents ADD COLUMN valid_till TEXT;

ALTER TABLE purchase_documents ADD COLUMN delivery_date TEXT;
ALTER TABLE purchase_documents ADD COLUMN valid_till TEXT;

CREATE INDEX IF NOT EXISTS ix_sales_documents_kind_status
    ON sales_documents (company_id, doc_kind, status, doc_date);

CREATE INDEX IF NOT EXISTS ix_purchase_documents_kind_status
    ON purchase_documents (company_id, doc_kind, status, doc_date);
-- @shoutech-migration 033_cheques.sql 8DC87DBB88C6B05CA3C23899F47663E936C665E73E532C04D4BA4BBAF23560F8 CAB714473A48DA3A59C88929B28BD4569ADCDBF572386746A1B5D5DA7B59099F CAB714473A48DA3A59C88929B28BD4569ADCDBF572386746A1B5D5DA7B59099F CAB714473A48DA3A59C88929B28BD4569ADCDBF572386746A1B5D5DA7B59099F
-- Owns the Cheques portfolio (RS-4). A received cheque is not money until it
-- clears: Save posts Dr Portfolio/Box Cr Party (incoming) or Dr Party Cr Box
-- (outgoing) through the journal engine and stores journal_entry_id here.
-- Dates are stored as ISO text (yyyy-MM-dd) on both providers.
-- Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS cheques (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    direction TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
    cheque_no TEXT NOT NULL,
    bank_name TEXT NOT NULL,
    branch_name TEXT,
    drawer_name TEXT,
    party_account_code TEXT,
    party_name TEXT,
    amount REAL NOT NULL CHECK (amount > 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    exchange_rate REAL NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
    issue_date TEXT,
    due_date TEXT,
    status TEXT NOT NULL DEFAULT 'InPortfolio'
        CHECK (status IN ('InPortfolio','Issued','Deposited','Cleared','Bounced','Endorsed','Cancelled')),
    portfolio_account_code TEXT,
    deposit_bank_account_code TEXT,
    deposit_date TEXT,
    clearance_date TEXT,
    endorsed_to_account_code TEXT,
    bounce_date TEXT,
    bounce_reason TEXT,
    fees_amount REAL NOT NULL DEFAULT 0 CHECK (fees_amount >= 0),
    journal_entry_id TEXT,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_cheques_company_bank_drawer_no
    ON cheques(company_id, bank_name, drawer_name, cheque_no) WHERE is_deleted = 0;

CREATE INDEX IF NOT EXISTS ix_cheques_company_status_due
    ON cheques(company_id, status, due_date);
-- @shoutech-migration 035_bank_reconciliation.sql 386431A760265131DF59AACCBE1B10218D087D1487574852748B17656994F9B1 2877A0B6D8EBB95C92C4A222F8B00C2797A2752864D0C628141D8B4D358E3ED9 2877A0B6D8EBB95C92C4A222F8B00C2797A2752864D0C628141D8B4D358E3ED9 2877A0B6D8EBB95C92C4A222F8B00C2797A2752864D0C628141D8B4D358E3ED9
-- Migration 035 (SQLite): bank reconciliation marks (ribbon completion RS-9).
--
-- journal_line_key choice: journal_entry_lines HAS a stable surrogate key,
-- journal_entry_line_id (INTEGER PRIMARY KEY AUTOINCREMENT on SQLite,
-- bigint identity on PostgreSQL), unique per line and never reused within a
-- company database. The mark therefore stores CAST(journal_entry_line_id AS
-- TEXT) as journal_line_key -- no composite fallback is needed. The entry id
-- and account code are denormalized onto the mark for reporting and for
-- resilience if a line is ever renumbered.
--
-- A mark means: this journal line appeared on a bank statement (cleared).
-- Unmarking deletes the row. Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS bank_reconciliation_marks (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    journal_entry_id TEXT NOT NULL,
    journal_line_key TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    cleared_amount REAL NOT NULL DEFAULT 0,
    is_cleared INTEGER NOT NULL DEFAULT 1 CHECK (is_cleared IN (0, 1)),
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    UNIQUE (company_id, journal_line_key)
);

CREATE INDEX IF NOT EXISTS ix_bank_rec_marks_account
    ON bank_reconciliation_marks(company_id, account_code, statement_date);
-- @shoutech-migration 063_cheques_bankrec.sql 567FCA30546145152A088F88A3829DFCB9BFAAB93B06670AEE04CF72ACAC20E1 863895E0677288D643D349203F85801131BE28C30763450977DB1B6FDC4B146B 863895E0677288D643D349203F85801131BE28C30763450977DB1B6FDC4B146B 863895E0677288D643D349203F85801131BE28C30763450977DB1B6FDC4B146B
-- ============================================================================
-- 063 - Cheques per-cheque GL link + real bank-statement model (SQLite)
--
-- Fix Phase Step 8 (sweep cheques-bankrec.md: CHQ-01..05, BNK-01/02,
-- BNKC-01):
--
-- 1. cheques gains batch_doc_no (the voucher number shown in ChqWin,
--    CHQ-05: it was displayed but never persisted) and cancel_reason
--    (the stop-payment note for outgoing cancellation). Per-cheque
--    journal entries themselves need no new column: journal_entry_id
--    already links a cheque to a journal entry -- from this step the
--    service posts ONE entry per cheque (source link, migration 060:
--    source_doc_type CHQ-ISSUE / CHQ-CLEAR / CHQ-BOUNCE / CHQ-ENDORSE /
--    CHQ-CANCEL / CHQ-FEE, source_doc_id = the cheque id), so the id
--    stored here is that cheque's OWN issue entry, never a batch entry
--    shared with siblings. Historical batch entries keep the shared id
--    they were posted with; the service detects the sharing at
--    bounce/cancel time and posts a per-cheque compensating entry
--    instead of reversing the shared entry (see FIX_PHASE_STEP8.md).
--
-- 2. The bank-statement side of reconciliation becomes DATA (BNK-01:
--    "Difference" was identically 0.00 because no statement existed):
--      bank_statements       one statement per bank account + period:
--                            opening balance, closing (statement)
--                            balance, status Open/Closed.
--      bank_statement_lines  the statement's own lines (entered in
--                            BankRecWin; file import is a separate
--                            flagged gap), each optionally matched to
--                            one book (journal) line.
--    Difference = closing_balance - opening_balance - SUM(matched book
--    lines) is then a REAL quantity: it is non-zero whenever the bank
--    statement disagrees with the books, moves by the matched amounts
--    as matching proceeds, and is legitimately 0 only when every
--    statement movement is matched in the books.
--    bank_reconciliation_marks gains the statement link so a mark
--    records WHICH statement line cleared the book line.
--
-- 3. Banks canonical store (BNKC-01): `banks` (migration 031) is the
--    ONE bank-account store -- BnkCard / MasterCardService /
--    BankRecWin already read and write it. The migration-050
--    `bank_accounts` table was written only by CardStore methods with
--    zero callers; any rows it somehow holds are copied into `banks`
--    here (code-keyed, only where the code is not already present),
--    the CardStore bank path and the DatWin "BankAccounts" dataset are
--    removed in code, and no code path writes bank_accounts any more.
--    The table itself is left in place as a legacy artifact (dropping
--    it would break STEI packages that still carry it); it is dead.
--
-- Idempotent: column adds are guarded by the migrator directives, the
-- tables/indexes are IF NOT EXISTS, and the banks copy only inserts
-- codes that are not already present; the checksum ledger also skips
-- a re-run.
-- ============================================================================

-- ---- Cheques: persisted batch voucher number + cancel reason ----------------
-- @if-column-missing cheques.batch_doc_no
ALTER TABLE cheques ADD COLUMN batch_doc_no TEXT;
-- @if-column-missing cheques.cancel_reason
ALTER TABLE cheques ADD COLUMN cancel_reason TEXT;

-- ---- Bank statements ---------------------------------------------------------
CREATE TABLE IF NOT EXISTS bank_statements (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    statement_date TEXT NOT NULL,
    statement_ref TEXT,
    period_from TEXT,
    period_to TEXT,
    opening_balance REAL NOT NULL DEFAULT 0,
    closing_balance REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closed')),
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_bank_statements_company_account
    ON bank_statements(company_id, account_code, statement_date);

CREATE TABLE IF NOT EXISTS bank_statement_lines (
    id TEXT PRIMARY KEY,
    statement_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    account_code TEXT NOT NULL,
    line_date TEXT NOT NULL,
    reference TEXT,
    description TEXT,
    amount REAL NOT NULL DEFAULT 0,
    matched_journal_line_key TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_statement
    ON bank_statement_lines(statement_id, line_date);

CREATE INDEX IF NOT EXISTS ix_bank_statement_lines_matched
    ON bank_statement_lines(company_id, matched_journal_line_key);

-- ---- Marks learn which statement line cleared them ---------------------------
-- @if-column-missing bank_reconciliation_marks.statement_id
ALTER TABLE bank_reconciliation_marks ADD COLUMN statement_id TEXT;
-- @if-column-missing bank_reconciliation_marks.statement_line_id
ALTER TABLE bank_reconciliation_marks ADD COLUMN statement_line_id TEXT;

-- ---- Canonical banks: copy any legacy bank_accounts rows into banks ---------
INSERT INTO banks (id, company_id, code, name_en, branch_name, account_no, iban,
                   currency_code, gl_account_code, is_active)
SELECT lower(hex(randomblob(16))), ba.company_id, ba.bank_code, ba.bank_name,
       NULLIF(ba.branch, ''), NULLIF(ba.account_no, ''), NULLIF(ba.iban, ''),
       ba.currency_code, NULLIF(ba.gl_account_code, ''), ba.is_active
FROM bank_accounts ba
WHERE ba.is_deleted = 0
  AND NOT EXISTS (
      SELECT 1 FROM banks b
      WHERE b.company_id = ba.company_id AND b.code = ba.bank_code AND b.is_deleted = 0);
-- @shoutech-migration 075_document_number_sequences.sql 96E7E382FD39B9F923975C224CA654F60E5C07833093099127F6D102A24C25ED 77C1438AD126E54ACC91C5569D33C1F110F87F79B781A382481FE1A340E18FB8 77C1438AD126E54ACC91C5569D33C1F110F87F79B781A382481FE1A340E18FB8 77C1438AD126E54ACC91C5569D33C1F110F87F79B781A382481FE1A340E18FB8
-- Atomic, company-scoped document number allocation.
CREATE TABLE IF NOT EXISTS document_number_sequences (
    company_id TEXT NOT NULL,
    doc_kind TEXT NOT NULL,
    doc_year INTEGER NOT NULL,
    last_value INTEGER NOT NULL CHECK (last_value >= 0),
    PRIMARY KEY (company_id, doc_kind, doc_year)
);
-- @shoutech-migration 077_payment_allocation_company_scope.sql C2A720BADF10F155B87A9E498A3EDED3E16E5147343C82A2B4A10320C718A9DC 9EEE8725D9E94890FDACA42A2EC26FEF5A584DEE260B689C2326299BF81DAB9E 9EEE8725D9E94890FDACA42A2EC26FEF5A584DEE260B689C2326299BF81DAB9E 9EEE8725D9E94890FDACA42A2EC26FEF5A584DEE260B689C2326299BF81DAB9E
-- Keep the party-ledger company key separate from the operational document key.
-- Legacy document writers use the company code; the commercial order repository
-- uses the company's stable GUID. Reconciliation must retain both scopes.
-- @if-column-missing payment_allocations.document_company_id
ALTER TABLE payment_allocations ADD COLUMN document_company_id TEXT;

UPDATE payment_allocations
SET document_company_id = CASE
    WHEN doc_table = 'SALES' THEN
        (SELECT d.company_id FROM sales_documents d WHERE d.doc_id = payment_allocations.doc_id)
    WHEN doc_table = 'PURCHASE' THEN
        (SELECT d.company_id FROM purchase_documents d WHERE d.doc_id = payment_allocations.doc_id)
    ELSE NULL
END
WHERE document_company_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_payment_allocations_document_company
    ON payment_allocations(document_company_id, doc_table, doc_id);
-- @shoutech-migration 079_payment_allocation_reversal_audit.sql 91830B4667E9264EE6272F4E72C95D932CDC99A1B289BD385C85A3F5A8E74033 C3D3023F051A18DE0888DA5CB2AE837C1D071BBAC2E6276A6AB51199E8AD0B97 C3D3023F051A18DE0888DA5CB2AE837C1D071BBAC2E6276A6AB51199E8AD0B97 C3D3023F051A18DE0888DA5CB2AE837C1D071BBAC2E6276A6AB51199E8AD0B97
-- Preserve allocation provenance when a linked receipt/payment voucher is reversed.
CREATE TABLE IF NOT EXISTS payment_allocation_reversal_audit (
    audit_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    allocation_id TEXT NOT NULL,
    payment_ledger_id INTEGER NOT NULL,
    doc_table TEXT NOT NULL CHECK (doc_table IN ('SALES', 'PURCHASE')),
    doc_id INTEGER NOT NULL,
    document_company_id TEXT NOT NULL,
    amount NUMERIC NOT NULL CHECK (amount > 0),
    allocated_by TEXT,
    note TEXT,
    released_by TEXT NOT NULL,
    reversal_journal_id TEXT NOT NULL,
    released_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE (company_id, allocation_id)
);

CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_payment
    ON payment_allocation_reversal_audit(company_id, payment_ledger_id);
CREATE INDEX IF NOT EXISTS ix_payment_allocation_reversal_audit_document
    ON payment_allocation_reversal_audit(company_id, doc_table, doc_id);
-- @shoutech-migration 103_sre_receipts_audit_recon.sql 8EC841B8AE177D33393ED7FE37188A761884C8A9887705B3DC160BF3798C5833 A99064E8E107CDB164D9A8FEB3BFA16DCA3A72D6337492636A9CB8F5C7FECDFD A99064E8E107CDB164D9A8FEB3BFA16DCA3A72D6337492636A9CB8F5C7FECDFD A99064E8E107CDB164D9A8FEB3BFA16DCA3A72D6337492636A9CB8F5C7FECDFD
-- ============================================================================
-- 103 - Receipts completion: settlement discounts, cross-currency allocation
-- with realized FX, unified document events + denied-access logging (SQLite).
-- Sales cycle W8 (Sales_Cycle.txt sections 7, 14, 15; sre = Sales Receipts,
-- sau = Sales Audit prefixes, spec section 18).
--
--   1. SETTLEMENT DISCOUNTS at allocation: payment_allocations gains
--      discount_amount (DOCUMENT currency). Allocating a receipt may settle
--      an invoice by payment + discount; the discount posts Dr 305 Sales
--      Discounts / Cr 1201 Receivable (suppliers: Dr 2201 / Cr 405 Purchase
--      Discounts) in the allocation journal. Gated by the new sensitive
--      permission SALES.SETTLEMENT.DISCOUNT (this migration).
--   2. CROSS-CURRENCY ALLOCATION: payment_allocations gains doc_amount
--      (the amount settled on the document, in DOCUMENT currency; NULL on
--      legacy rows where it equals amount) and fx_amount (the realized FX
--      difference in BASE currency, signed: positive = loss debited to 509,
--      negative = gain credited to 306 Other Income). The hard currency
--      block in ReconciliationRepository is removed in code.
--      COA DECISION (W8, verified against every seeded template in
--      cod/app/src/Resources/COA/*.shc): all templates already carry
--        305 Sales Discounts (postable, debit-balance contra revenue),
--        405 Purchase Discounts (postable, credit-balance),
--        509 Foreign Exchange Losses (postable expense),
--        306 Other Income (postable revenue).
--      NO template carries a dedicated FX-gain account, so realized gains
--      credit 306 Other Income and losses debit 509 — both EXISTING
--      accounts; nothing is seeded into the chart.
--   3. DOCUMENT EVENTS (sau_document_events): the unified event log for
--      sales/purchase documents — Created/Modified/Submitted/Approved/
--      Rejected/Posted/Reversed/Cancelled/Allocated plus AccessDenied
--      (denied-access logging rides this table: one audit surface, the
--      event_type discriminates; a separate denials table would split the
--      per-document history the DocSearchWin viewer reads). doc_side
--      discriminates SALES vs PURCHASE because doc_id is only unique
--      within its own document table. company_id carries the DOCUMENT's
--      company key (Guid text for chain/native documents, matching
--      sau_approval_history); AccessDenied rows written before a document
--      context exists carry the company CODE.
--   4. Permission SALES.SETTLEMENT.DISCOUNT (sensitive; display_order 401
--      continues after migration 100's 394 / 098's 394-400 block — the
--      highest seeded order in the chain is 400).
--
-- Idempotent: CREATE TABLE / INDEX IF NOT EXISTS, column adds guarded by
-- the migrator directive, permission seeds INSERT OR IGNORE + UPDATE;
-- the checksum ledger skips a re-run.
-- ============================================================================

-- ---- Allocation: settlement discount + cross-currency amounts --------------
-- @if-column-missing payment_allocations.discount_amount
ALTER TABLE payment_allocations ADD COLUMN discount_amount NUMERIC NOT NULL DEFAULT 0;
-- @if-column-missing payment_allocations.doc_amount
ALTER TABLE payment_allocations ADD COLUMN doc_amount NUMERIC;
-- @if-column-missing payment_allocations.fx_amount
ALTER TABLE payment_allocations ADD COLUMN fx_amount NUMERIC NOT NULL DEFAULT 0;

-- ---- Unified document events (sau) ------------------------------------------
CREATE TABLE IF NOT EXISTS sau_document_events (
    event_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    doc_side TEXT NOT NULL DEFAULT 'SALES' CHECK (doc_side IN ('SALES','PURCHASE')),
    doc_kind TEXT NOT NULL DEFAULT '',
    doc_id INTEGER NOT NULL DEFAULT 0,
    doc_no TEXT,
    event_type TEXT NOT NULL CHECK (event_type IN ('Created','Modified','Submitted','Approved','Rejected','Posted','Reversed','Cancelled','Allocated','AccessDenied')),
    actor TEXT NOT NULL DEFAULT '',
    details TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS ix_sau_document_events_doc
    ON sau_document_events(company_id, doc_side, doc_kind, doc_id, created_at);
CREATE INDEX IF NOT EXISTS ix_sau_document_events_company_type
    ON sau_document_events(company_id, event_type, created_at);

-- ---- Permission: SALES.SETTLEMENT.DISCOUNT (sensitive) -----------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('SALES.SETTLEMENT.DISCOUNT','Sales Settlement - Discount','SALES','SETTLEMENT','DISCOUNT','تسوية المبيعات - خصم تسوية','Sales Settlement - Discount','Grant a settlement (early-payment) discount while allocating a receipt or payment (sales spec section 7, migration 103)',401,1);
UPDATE permissions SET group_code='SALES',feature_code='SETTLEMENT',operation_code='DISCOUNT',name_ar='تسوية المبيعات - خصم تسوية',name_en='Sales Settlement - Discount',display_order=401,is_sensitive=1 WHERE permission_code='SALES.SETTLEMENT.DISCOUNT';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('SALES.SETTLEMENT.DISCOUNT');
-- @shoutech-migration 110_supplier_advances.sql 00B2088129055EB9C2D5A4BEC1D3DB3475213564C1055D83B644BA8F7C3F912D 6ED18E21749E3FDFBADDCF741FCB84A91E59F8D926A262AC8166F9D7CB950EEC 6ED18E21749E3FDFBADDCF741FCB84A91E59F8D926A262AC8166F9D7CB950EEC 6ED18E21749E3FDFBADDCF741FCB84A91E59F8D926A262AC8166F9D7CB950EEC
-- 110 - Supplier advances (SQLite): a PAY- cash voucher may be flagged as
-- an advance against one purchase order; the advance posts to the new
-- Advances to Suppliers account (1215) instead of AP control (2201), and
-- PayRecWin allocation consumes it (Dr AP 2201 / Cr Advances 1215) only
-- against invoices of that same PO.
-- PostgreSQL counterpart: postgresql/110_supplier_advances.sql.

-- Advance marking on the voucher's party link (migration 061 table):
-- po_doc_id = purchase_documents.doc_id of the PO the advance belongs to;
-- is_advance = 1 only for PAY links flagged as advances at draft save.
-- @if-column-missing voucher_party_links.po_doc_id
ALTER TABLE voucher_party_links ADD COLUMN po_doc_id INTEGER;
-- @if-column-missing voucher_party_links.is_advance
ALTER TABLE voucher_party_links ADD COLUMN is_advance INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_voucher_party_links_po
    ON voucher_party_links(company_id, po_doc_id) WHERE is_advance = 1;

-- Allocation provenance: an allocation row now names its source.
-- 'PAYMENT' = a normal party-ledger payment (the historical default);
-- 'ADVANCE' = consumption of a PO advance: advance_po_doc_id carries the
-- PO the advance belongs to and clearing_journal_id the allocation
-- transaction's Dr AP / Cr Advances journal entry, so the allocation can
-- be reversed exactly (advance restored, clearing entry mirrored).
-- @if-column-missing payment_allocations.source_kind
ALTER TABLE payment_allocations ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'PAYMENT';
-- @if-column-missing payment_allocations.advance_po_doc_id
ALTER TABLE payment_allocations ADD COLUMN advance_po_doc_id INTEGER;
-- @if-column-missing payment_allocations.clearing_journal_id
ALTER TABLE payment_allocations ADD COLUMN clearing_journal_id TEXT;

CREATE INDEX IF NOT EXISTS ix_payment_allocations_advance_po
    ON payment_allocations(company_id, advance_po_doc_id) WHERE source_kind = 'ADVANCE';

-- Advances to Suppliers account (1215), seeded into every existing
-- company that carries the purchase group (account 402), under the same
-- parent as Inventory (1203) in the 12xx current-asset block (1201 AR,
-- 1203 Inventory, 1205 Input VAT; 1210-1214 are manufacturing inventory
-- accounts in the industrial templates, so 1215 is the first code free
-- in every template). New companies receive it from the COA templates
-- (cod/app/src/Resources/COA/*.shc). Deterministic per-company account
-- id is not required on SQLite; the random GUID form matches
-- migration 059.
INSERT INTO chart_of_accounts
    (account_id, company_id, account_code, account_name, name_ar, account_type,
     parent_account_id, is_postable, is_active, is_deleted)
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
       substr(lower(hex(randomblob(2))), 2) || '-a' ||
       substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))),
       c.company_id,
       '1215',
       'Advances to Suppliers',
       'Advances to Suppliers',
       'ASSET',
       (SELECT p.parent_account_id FROM chart_of_accounts p
         WHERE p.company_id = c.company_id AND p.account_code = '1203'),
       1, 1, 0
FROM companies c
WHERE EXISTS (SELECT 1 FROM chart_of_accounts a
              WHERE a.company_id = c.company_id AND a.account_code = '402')
  AND NOT EXISTS (SELECT 1 FROM chart_of_accounts a
                  WHERE a.company_id = c.company_id AND a.account_code = '1215');
-- @shoutech-migration 116_doc_designs.sql 9DD24AD2A4B2462CAAA06C5BFA637A405A5CCE580D763A175C8EDC0768480E9E FB6ACF5A1BA115221D9BE74A4369FEE33FA9D6A857F52D40F5D0B8F2BD4DAF34 FB6ACF5A1BA115221D9BE74A4369FEE33FA9D6A857F52D40F5D0B8F2BD4DAF34 FB6ACF5A1BA115221D9BE74A4369FEE33FA9D6A857F52D40F5D0B8F2BD4DAF34
-- 116 - Invoice Designer store (services phase B3).
-- doc_designs holds print-layout designs for the six printable
-- document kinds (sales invoice, purchase invoice, sales return,
-- purchase return, stock entry, stock exit). One row per named
-- design, company-scoped; the design itself is a versioned JSON
-- payload (DocDesign) so the band/element model can evolve without
-- schema churn. IsDefault marks the single design InvPrn uses for
-- that kind when the user prints; no row = the built-in default
-- layout compiled into InvPrn.

CREATE TABLE IF NOT EXISTS doc_designs (
    DesignID INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyID TEXT NOT NULL,
    DocKind TEXT NOT NULL,
    DesignName TEXT NOT NULL,
    IsDefault INTEGER NOT NULL DEFAULT 0,
    PayloadJson TEXT NOT NULL,
    IsDeleted INTEGER NOT NULL DEFAULT 0,
    CreatedBy TEXT,
    UpdatedBy TEXT,
    CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt TEXT,
    DeletedAt TEXT,
    DeletedBy TEXT
);

CREATE INDEX IF NOT EXISTS ix_doc_designs_company_kind
    ON doc_designs(CompanyID, DocKind, IsDeleted);
-- @shoutech-migration 120_invoice_line_notes.sql D26EDA2AC23B3213CA26A9E452E02658B7BE7A8A50B81209C0775517413CF987 AA4DA638D908987C1171C114E436438617AA2157A98E44388E8261442CCFE134 AA4DA638D908987C1171C114E436438617AA2157A98E44388E8261442CCFE134 AA4DA638D908987C1171C114E436438617AA2157A98E44388E8261442CCFE134
-- 120 - Per-line notes on commercial documents (invoice family
-- batch INV1, owner reference 2026-11-24 16:06: the Phenix invoice
-- grid carries a line-notes column). One design for all invoice
-- types: the notes ride on both line tables and surface in
-- InvoiceWin as an optional column (column chooser, per-user).

-- @if-column-missing sales_document_lines.notes
ALTER TABLE sales_document_lines ADD COLUMN notes TEXT;
-- @if-column-missing purchase_document_lines.notes
ALTER TABLE purchase_document_lines ADD COLUMN notes TEXT;
-- @shoutech-migration 123_number_series_config.sql 2100D25CE295E3D86FA7F7238D50635740B4D70B0165936CE7B15E988437F7EB 3DBECE8FCEA212A5779B67B1C50406E24EBE4B202CEFB573965BF35D5E6A611F 3DBECE8FCEA212A5779B67B1C50406E24EBE4B202CEFB573965BF35D5E6A611F 3DBECE8FCEA212A5779B67B1C50406E24EBE4B202CEFB573965BF35D5E6A611F
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
-- @shoutech-migration 125_print_settings_logo.sql F2615682611A18052B5287B2C2E1B4740C769C2F912AE1AC1E350D5A8463B5FC 3B02F2017E125A610887ADC0BADD8ABDEF99C3262E9A47BF58DE7ED2332776BB 3B02F2017E125A610887ADC0BADD8ABDEF99C3262E9A47BF58DE7ED2332776BB 3B02F2017E125A610887ADC0BADD8ABDEF99C3262E9A47BF58DE7ED2332776BB
-- 125 - Print settings + company logo (docs/OPTWIN_SECTIONS.md §3.3,
-- Landing 2). One row per company: which company-profile fields the
-- print header block carries, the free-text print header, the
-- balance/user lines on printed invoices, and the default printer.
-- The company logo itself lives on the companies row (BLOB) so it
-- travels with the company database on both providers.
CREATE TABLE IF NOT EXISTS print_settings (
    company_id            TEXT    NOT NULL PRIMARY KEY,
    header_name           INTEGER NOT NULL DEFAULT 1,
    header_address        INTEGER NOT NULL DEFAULT 1,
    header_phone          INTEGER NOT NULL DEFAULT 0,
    header_mobile         INTEGER NOT NULL DEFAULT 0,
    header_email          INTEGER NOT NULL DEFAULT 0,
    header_website        INTEGER NOT NULL DEFAULT 0,
    header_license        INTEGER NOT NULL DEFAULT 0,
    header_tax_number     INTEGER NOT NULL DEFAULT 0,
    header_text           TEXT    NULL,
    print_previous_balance INTEGER NOT NULL DEFAULT 0,
    print_current_balance  INTEGER NOT NULL DEFAULT 0,
    print_user_on_invoice  INTEGER NOT NULL DEFAULT 0,
    default_printer       TEXT    NULL,
    updated_by            TEXT    NULL,
    updated_at            TEXT    NULL
);

-- @if-column-missing companies.logo_data
ALTER TABLE companies ADD COLUMN logo_data BLOB;
-- @if-column-missing companies.logo_media_type
ALTER TABLE companies ADD COLUMN logo_media_type TEXT;
