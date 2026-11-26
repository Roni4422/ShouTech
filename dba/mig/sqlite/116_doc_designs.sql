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
