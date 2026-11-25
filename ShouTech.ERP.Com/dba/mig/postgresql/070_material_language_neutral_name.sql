-- 070 - Introduce a language-neutral canonical material name.
-- Preserve the legacy name_ar column during the compatibility period.
ALTER TABLE materials ADD COLUMN IF NOT EXISTS name TEXT NOT NULL DEFAULT '';
ALTER TABLE materials ALTER COLUMN name_en SET DEFAULT '';
UPDATE materials
SET name = CASE
    WHEN NULLIF(BTRIM(COALESCE(name_ar, '')), '') IS NOT NULL THEN name_ar
    WHEN NULLIF(BTRIM(COALESCE(name_en, '')), '') IS NOT NULL THEN name_en
    ELSE code
END
WHERE NULLIF(BTRIM(COALESCE(name, '')), '') IS NULL;

CREATE OR REPLACE FUNCTION sync_material_language_neutral_name()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NULLIF(BTRIM(COALESCE(NEW.name, '')), '') IS NULL THEN
            NEW.name := COALESCE(NULLIF(NEW.name_ar, ''), NULLIF(NEW.name_en, ''), NEW.code);
        END IF;
        IF NULLIF(BTRIM(COALESCE(NEW.name_ar, '')), '') IS NULL THEN
            NEW.name_ar := COALESCE(NULLIF(NEW.name, ''), NULLIF(NEW.name_en, ''), NEW.code);
        END IF;
    ELSIF NEW.name IS DISTINCT FROM OLD.name THEN
        NEW.name_ar := NEW.name;
    ELSIF NEW.name_ar IS DISTINCT FROM OLD.name_ar THEN
        NEW.name := NEW.name_ar;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_materials_name_sync ON materials;
CREATE TRIGGER trg_materials_name_sync
BEFORE INSERT OR UPDATE OF name, name_ar ON materials
FOR EACH ROW EXECUTE FUNCTION sync_material_language_neutral_name();
