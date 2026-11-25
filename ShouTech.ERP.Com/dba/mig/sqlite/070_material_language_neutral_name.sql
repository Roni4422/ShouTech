-- 070 - Introduce a language-neutral canonical material name.
-- Preserve the legacy name_ar column during the compatibility period.
-- @if-column-missing materials.name
ALTER TABLE materials ADD COLUMN name TEXT NOT NULL DEFAULT '';
UPDATE materials
SET name = CASE
    WHEN TRIM(COALESCE(name_ar, '')) <> '' THEN name_ar
    WHEN TRIM(COALESCE(name_en, '')) <> '' THEN name_en
    ELSE code
END
WHERE TRIM(COALESCE(name, '')) = '';

CREATE TRIGGER IF NOT EXISTS trg_materials_name_sync_insert
AFTER INSERT ON materials
BEGIN
    UPDATE materials
    SET name = CASE WHEN TRIM(COALESCE(NEW.name, '')) = '' THEN COALESCE(NULLIF(NEW.name_ar, ''), NULLIF(NEW.name_en, ''), NEW.code) ELSE NEW.name END,
        name_ar = CASE WHEN TRIM(COALESCE(NEW.name_ar, '')) = '' THEN COALESCE(NULLIF(NEW.name, ''), NULLIF(NEW.name_en, ''), NEW.code) ELSE NEW.name_ar END
    WHERE material_id = NEW.material_id AND company_id = NEW.company_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_materials_name_sync_from_name
AFTER UPDATE OF name ON materials
WHEN NEW.name IS NOT OLD.name
BEGIN
    UPDATE materials SET name_ar = NEW.name
    WHERE material_id = NEW.material_id AND company_id = NEW.company_id AND name_ar IS NOT NEW.name;
END;

CREATE TRIGGER IF NOT EXISTS trg_materials_name_sync_from_legacy
AFTER UPDATE OF name_ar ON materials
WHEN NEW.name_ar IS NOT OLD.name_ar
BEGIN
    UPDATE materials SET name = NEW.name_ar
    WHERE material_id = NEW.material_id AND company_id = NEW.company_id AND name IS NOT NEW.name_ar;
END;
