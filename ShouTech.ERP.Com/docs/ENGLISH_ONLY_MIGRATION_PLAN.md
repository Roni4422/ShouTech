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
