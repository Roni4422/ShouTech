-- 125 - Print settings + company logo (docs/OPTWIN_SECTIONS.md §3.3,
-- Landing 2). One row per company: which company-profile fields the
-- print header block carries, the free-text print header, the
-- balance/user lines on printed invoices, and the default printer.
-- The company logo itself lives on the companies row (BLOB) so it
-- travels with the company database on both providers.
CREATE TABLE IF NOT EXISTS print_settings (
    company_id            TEXT    NOT NULL PRIMARY KEY,
    header_name           INTEGER NOT NULL DEFAULT 1,
    header_address        INTEGER NOT NULL DEFAULT 1,
    header_phone          INTEGER NOT NULL DEFAULT 0,
    header_mobile         INTEGER NOT NULL DEFAULT 0,
    header_email          INTEGER NOT NULL DEFAULT 0,
    header_website        INTEGER NOT NULL DEFAULT 0,
    header_license        INTEGER NOT NULL DEFAULT 0,
    header_tax_number     INTEGER NOT NULL DEFAULT 0,
    header_text           TEXT    NULL,
    print_previous_balance INTEGER NOT NULL DEFAULT 0,
    print_current_balance  INTEGER NOT NULL DEFAULT 0,
    print_user_on_invoice  INTEGER NOT NULL DEFAULT 0,
    default_printer       TEXT    NULL,
    updated_by            TEXT    NULL,
    updated_at            TEXT    NULL
);

-- @if-column-missing companies.logo_data
ALTER TABLE companies ADD COLUMN logo_data BLOB;
-- @if-column-missing companies.logo_media_type
ALTER TABLE companies ADD COLUMN logo_media_type TEXT;
