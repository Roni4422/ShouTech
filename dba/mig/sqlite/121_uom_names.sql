-- 121 - Unit-of-measure names (R4 study outcome). units_of_measure
-- carried only uom_code since migration 001; every benchmark names
-- its units, and the definition editor should too. Additive:
-- all existing consumers key on uom_code and are untouched.

-- @if-column-missing units_of_measure.name_en
ALTER TABLE units_of_measure ADD COLUMN name_en TEXT;
-- @if-column-missing units_of_measure.name_ar
ALTER TABLE units_of_measure ADD COLUMN name_ar TEXT;
