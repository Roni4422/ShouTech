-- 117 - Barcode Designer store (services phase B4).
-- label_designs holds named barcode-label designs per company
-- (JSON payload, LabelDesign): label size, symbology (Code 128 /
-- Code 39), module/bar sizes, fonts, colors, visible elements.
-- IsDefault marks the design the label print paths use. The
-- designer's local working file (barcode-design.cfg) remains as
-- the fallback when no DB design exists.

CREATE TABLE IF NOT EXISTS label_designs (
    DesignID INTEGER PRIMARY KEY AUTOINCREMENT,
    CompanyID TEXT NOT NULL,
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

CREATE INDEX IF NOT EXISTS ix_label_designs_company
    ON label_designs(CompanyID, IsDeleted);
