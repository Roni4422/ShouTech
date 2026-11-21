# Document Numbering

## Purpose

Document numbers are company-scoped, document-kind-scoped, year-scoped business identifiers. They are not database row IDs and must never be generated from a row count.

## Runtime contract

- Use `InvRepo.NextDocNo` for the preview/number-allocation path.
- `SaveDraft` allocates a number through the same database transaction that inserts the draft whenever the document does not already carry a number.
- A number is reserved by an atomic write to `document_number_sequences`; concurrent callers cannot receive the same sequence value.
- SQLite uses an immediate write transaction for this allocation. PostgreSQL uses an atomic `INSERT ... ON CONFLICT ... DO UPDATE ... RETURNING` operation.
- Existing document numbers are inspected when a sequence is first initialized or when legacy data is ahead of the sequence row. This allows upgrades from databases created before the sequence table existed.
- The database uniqueness constraint on `(company_id, doc_kind, doc_no)` remains the final integrity guard.

## Reservation and gaps

A number requested for a new-entry window is a reservation, not proof that a document was saved. If a user opens a form and abandons it, the reserved number may remain unused. Gaps are acceptable; reusing an issued number is not. Do not decrement or recycle sequence values after draft deletion, cancellation, or reversal.

## Schema ownership

The sequence table is defined only in forward DBA migrations:

- `dba/mig/sqlite/075_document_number_sequences.sql`
- `dba/mig/postgresql/075_document_number_sequences.sql`

Do not add runtime DDL to `InvRepo`, `DocSchema`, or a window. `DocSchema` validates that the migrated table exists.

## Required regression coverage

- Legacy documents with gaps: next number is greater than the highest existing numeric suffix, not the row count.
- Concurrent callers: all allocated values are unique and monotonically increasing.
- Save path: a missing draft number is allocated inside the same transaction as the draft insert.
- Provider parity: run the migration smoke workflow for both SQLite and PostgreSQL.
- Failure behavior: if the company database is unavailable or allocation fails, stop creation and report an error. Never silently fall back to `0001`.

## Change policy

Treat document numbering as an accounting control. Any change to prefixes, fiscal-year boundaries, reset policies, or concurrency behavior requires a forward migration where needed, provider-parity tests, and an explicit product decision. Do not change the current calendar-year policy implicitly.
