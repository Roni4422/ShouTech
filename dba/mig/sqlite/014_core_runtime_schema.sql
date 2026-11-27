-- These two lookup tables were historically created by Data.cs and were absent
-- from the ordered SQLite migration chain. They are now DBA-owned.
CREATE TABLE IF NOT EXISTS product_categories (
    category_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    parent_category_id TEXT,
    category_code TEXT NOT NULL,
    category_name_ar TEXT NOT NULL,
    category_name_en TEXT,
    category_level INTEGER NOT NULL DEFAULT 1,
    icon_path TEXT,
    image_path TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    deleted_at TEXT,
    deleted_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    modified_at TEXT,
    modified_by TEXT,
    UNIQUE(company_id, category_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id),
    FOREIGN KEY(parent_category_id) REFERENCES product_categories(category_id)
);

CREATE TABLE IF NOT EXISTS brands (
    brand_id TEXT NOT NULL PRIMARY KEY,
    company_id TEXT NOT NULL,
    brand_code TEXT NOT NULL,
    brand_name_ar TEXT NOT NULL,
    brand_name_en TEXT,
    logo_path TEXT,
    website TEXT,
    description TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    deleted_at TEXT,
    deleted_by TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by TEXT,
    modified_at TEXT,
    modified_by TEXT,
    UNIQUE(company_id, brand_code),
    FOREIGN KEY(company_id) REFERENCES companies(company_id)
);
