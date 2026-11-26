-- ============================================================================
-- 030 - English-only seed cleanup (PostgreSQL)
--
-- Applied migrations are immutable (SHA-256 checksums are enforced by the
-- migrator), so the Arabic seed values written by 006/014/028 are normalized
-- here instead of editing those files:
--   * permission_groups.name_ar and permissions.name_ar now carry the
--     canonical English text (the name_ar columns are kept, not dropped).
--   * permissions.description: the seeded Arabic generic text becomes
--     "Operational permission within the <Group> section".
--   * accounts: Arabic enum-like stored values (posting_mode, status, risk,
--     cost_center) become the canonical English tokens the application uses,
--     and the column defaults change to English.
--   * Legacy Arabic stock movement type values become "Opening Stock" (the
--     export classification no longer matches Arabic text).
-- The legacy Arabic literals are written as Unicode code points (char() /
-- U&'...') so this file itself stays English-only. User-entered data (e.g.
-- accounts.name_ar values typed by users) is not touched.
-- ============================================================================

-- ---- Permission catalog -----------------------------------------------------
UPDATE permission_groups SET name_ar = name_en;

UPDATE permissions SET name_ar = name_en;

UPDATE permissions
SET description = 'Operational permission within the ' ||
    COALESCE((SELECT name_en FROM permission_groups
              WHERE permission_groups.group_code = permissions.group_code), 'System') ||
    ' section'
WHERE description LIKE U&'\0635\0644\0627\062d\064a\0629 \062a\0634\063a\064a\0644\064a\0629 \0636\0645\0646 \0642\0633\0645 ' || '%';

UPDATE permissions
SET description = 'Manage user permissions'
WHERE permission_code = 'SYS.PERMISSIONS.MANAGE';

-- ---- Accounts: normalize stored enum values ---------------------------------
UPDATE accounts SET posting_mode = 'Postable'    WHERE posting_mode = U&'\0642\0627\0628\0644 \0644\0644\062a\0631\062d\064a\0644';
UPDATE accounts SET posting_mode = 'Summary'     WHERE posting_mode = U&'\062a\062c\0645\064a\0639\064a';
UPDATE accounts SET status       = 'Active'      WHERE status = U&'\0641\0639\0627\0644';
UPDATE accounts SET status       = 'Disabled'    WHERE status = U&'\0645\0639\0637\0644';
UPDATE accounts SET risk         = 'Low'         WHERE risk = U&'\0645\0646\062e\0641\0636';
UPDATE accounts SET risk         = 'Medium'      WHERE risk = U&'\0645\062a\0648\0633\0637';
UPDATE accounts SET risk         = 'High'        WHERE risk = U&'\0639\0627\0644\064a';
UPDATE accounts SET cost_center  = 'Unspecified' WHERE cost_center = U&'\063a\064a\0631 \0645\062d\062f\062f';
UPDATE accounts SET cost_center  = 'Required'    WHERE cost_center = U&'\0625\0644\0632\0627\0645\064a';
UPDATE accounts SET cost_center  = 'Optional'    WHERE cost_center = U&'\0627\062e\062a\064a\0627\0631\064a';

-- ---- Accounts: English column defaults ---------------------------------------
ALTER TABLE accounts ALTER COLUMN posting_mode SET DEFAULT 'Postable';
ALTER TABLE accounts ALTER COLUMN status SET DEFAULT 'Active';
ALTER TABLE accounts ALTER COLUMN risk SET DEFAULT 'Low';
ALTER TABLE accounts ALTER COLUMN cost_center SET DEFAULT 'Unspecified';

-- ---- Legacy stock movement types ---------------------------------------------
UPDATE stock_movements SET movement_type = 'Opening Stock'
WHERE movement_type LIKE '%' || U&'\0627\0641\062a\062a\0627\062d' || '%' OR movement_type LIKE '%' || U&'\0623\0648\0644' || '%';

UPDATE material_stock_movements SET movement_type = 'Opening Stock'
WHERE movement_type LIKE '%' || U&'\0627\0641\062a\062a\0627\062d' || '%' OR movement_type LIKE '%' || U&'\0623\0648\0644' || '%';
