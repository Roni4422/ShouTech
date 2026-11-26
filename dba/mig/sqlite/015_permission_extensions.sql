-- Permission extension columns formerly installed by Data.cs.
-- Conditional directives make the SQLite migration safe for existing company databases.
-- @if-column-missing permissions.group_code
ALTER TABLE permissions ADD COLUMN group_code TEXT NOT NULL DEFAULT 'SYSTEM';
-- @if-column-missing permissions.feature_code
ALTER TABLE permissions ADD COLUMN feature_code TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.operation_code
ALTER TABLE permissions ADD COLUMN operation_code TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.name_ar
ALTER TABLE permissions ADD COLUMN name_ar TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.name_en
ALTER TABLE permissions ADD COLUMN name_en TEXT NOT NULL DEFAULT '';
-- @if-column-missing permissions.description
ALTER TABLE permissions ADD COLUMN description TEXT;
-- @if-column-missing permissions.display_order
ALTER TABLE permissions ADD COLUMN display_order INTEGER NOT NULL DEFAULT 0;
-- @if-column-missing permissions.is_sensitive
ALTER TABLE permissions ADD COLUMN is_sensitive INTEGER NOT NULL DEFAULT 0;
