-- Product category names are user-language data, not separate Arabic/English fields.
-- Preserve the existing Arabic value when present; otherwise retain the English value.
ALTER TABLE product_categories RENAME COLUMN category_name_ar TO name;
UPDATE product_categories
SET name = category_name_en
WHERE TRIM(COALESCE(name, '')) = ''
  AND TRIM(COALESCE(category_name_en, '')) <> '';
ALTER TABLE product_categories DROP COLUMN category_name_en;
