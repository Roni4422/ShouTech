-- PostgreSQL counterpart of sqlite/015_permission_extensions.sql.
-- Additive only: safe for databases whose permissions table predates these fields.
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS group_code TEXT NOT NULL DEFAULT 'SYSTEM';
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS feature_code TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS operation_code TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS name_ar TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS name_en TEXT NOT NULL DEFAULT '';
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS description TEXT;
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS display_order INTEGER NOT NULL DEFAULT 0;
ALTER TABLE permissions ADD COLUMN IF NOT EXISTS is_sensitive INTEGER NOT NULL DEFAULT 0;
