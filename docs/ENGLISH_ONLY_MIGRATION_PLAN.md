<!-- source: docs/ENGLISH_ONLY_MIGRATION_PLAN.md -->

# English-Only Source and Localization Plan

## Target architecture

- English is the canonical language for source code, identifiers, comments, database identifiers, seed labels, logs, and developer documentation.
- Runtime UI strings are resolved by stable resource keys. `Resources/Lang/En.json` is the canonical key catalog; other language JSON files provide translations only.
- Language catalogs remain localized data and are the sole approved source files for non-English UI wording.
- Language selection and right-to-left layout remain supported for RTL languages. RTL support is behavior, not a reason to embed translated text in source.

## Migration sequence

1. Audit all text sources and localization catalog key parity.
2. Remove locale-specific label parsing from runtime code; resolve labels through the resource catalogs generically.
3. Replace hard-coded non-English UI strings with resource keys.
4. Normalize database identifiers to language-neutral English names, with explicit migrations for existing SQLite and PostgreSQL databases.
5. Replace localized seed data and report labels with canonical English identifiers or resource keys.
6. Clean developer documentation, generated logs, test fixtures, and comments.
7. Run the audit, validate JSON catalogs, build the Windows WPF application on a Windows/.NET 8 environment, and run database migration/integration tests.

## Safety constraints

- Do not delete user-entered business data as part of a language cleanup.
- Database column renames require tested migrations for both SQLite and PostgreSQL; editing `CREATE TABLE IF NOT EXISTS` statements alone does not upgrade existing databases.
- Do not claim the project is English-only until the audit returns zero findings outside approved localization catalogs and the application build and migration tests pass.

---

<!-- source: docs/ENGLISH_ONLY_STATUS.md -->

# English-Only Conversion Status

## Completed in this first pass

- Replaced the Arabic-specific label reverse lookup with a locale-neutral label index built from all language JSON files.
- Renamed the runtime resolver to `TryResolveKeyFromLabel` and updated its caller.
- Converted Arabic comments in the localization service contract to English.
- Removed 882 Arabic-containing comment lines/blocks across 90 source files without changing string literals or database values.
- Added `tools/verify_english_source.py` to detect Arabic text outside approved language catalogs and report localization catalog key coverage.
- Added `tools/clean_non_english_comments.py` as a conservative repeatable first-pass comment cleanup tool.

## Remaining work

The current audit still reports Arabic in source/UI strings, database seed data, schemas, documentation, and other non-localization files. This is expected at this stage; the audit must reach zero findings before the conversion can be called complete. The current audit output is kept outside the source tree in the delivered working package's `audit/` folder.

The database schema still requires a carefully tested migration from locale-specific fields to language-neutral names. This has deliberately not been done with a blind global replacement, because existing SQLite and PostgreSQL databases must continue to open without losing user data.

## Validation limits

The language JSON files were checked by the audit script. A full application build was not run because the current environment does not have the .NET SDK installed. Build and database migration tests remain required on the supported Windows/.NET 8 environment.
