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
