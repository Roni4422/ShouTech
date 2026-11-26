-- Owns the Fixed Assets register. Depreciation schedules are computed by the
-- application service from cost / salvage / useful life; only the register
-- itself is stored. Runtime services must not create tables.

CREATE TABLE IF NOT EXISTS fixed_assets (
    asset_id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    asset_code TEXT NOT NULL,
    asset_name TEXT NOT NULL,
    category TEXT,
    purchase_date TEXT NOT NULL,
    cost NUMERIC NOT NULL CHECK (cost >= 0),
    salvage_value NUMERIC NOT NULL DEFAULT 0 CHECK (salvage_value >= 0),
    useful_life_years INTEGER NOT NULL CHECK (useful_life_years > 0),
    depreciation_method TEXT NOT NULL DEFAULT 'StraightLine'
        CHECK (depreciation_method IN ('StraightLine','DecliningBalance')),
    location TEXT,
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Disposed','FullyDepreciated')),
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_fixed_assets_company_code
    ON fixed_assets(company_id, asset_code) WHERE is_deleted = 0;
