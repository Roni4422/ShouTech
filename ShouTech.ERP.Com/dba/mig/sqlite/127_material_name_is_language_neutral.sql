-- 127 - Make the language-neutral material name authoritative.
-- Keep name_ar only as a blank legacy compatibility column; no triggers
-- may copy a user's language-neutral name into an Arabic-specific field.
DROP TRIGGER IF EXISTS trg_materials_name_sync_insert;
DROP TRIGGER IF EXISTS trg_materials_name_sync_from_name;
DROP TRIGGER IF EXISTS trg_materials_name_sync_from_legacy;
