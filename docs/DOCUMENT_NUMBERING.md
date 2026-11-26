<!-- source: docs/DOCUMENT_NUMBERING.md -->

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

---

<!-- source: docs/DOCUMENT-NUMBERING-NAVIGATION.md -->

# Document Numbering and Record Navigation Standard

## Scope

This standard applies to every ShouTech desktop data-entry window: sales and purchase documents, receipts and payment vouchers, journal vouchers, inventory documents, master cards, manufacturing documents, and other persisted records. The implementation is WPF/C# with provider-neutral SQLite and PostgreSQL persistence.

## Accounting workflow

1. Operational transactions are entered and saved as source documents.
2. Posting a source document creates its accounting journal/voucher through the accounting posting engine in the same database transaction as the source posting.
3. The source document retains a durable reference to its generated journal voucher. The voucher retains a source-document reference.
4. Users navigate to the generated voucher from the source document; they must not create duplicate journal entries by manually reposting the same source.
5. Manual accounting vouchers use the same document-number and navigation standard, with journal balancing, period, account, permission, and audit validations enforced by the service/repository layer.

## Number field and toolbar placement

- The shared visual control is owned centrally by `BaseWin` (`CreateRecordNumberBar`). Entry windows supply only their record-specific navigation callback; they must not recreate the field, button sizing, spacing, tooltips, or accessibility labels locally.
- The strip uses a fixed compact physical order: next 10, next 1, number field, previous 1, previous 10. The four buttons sit immediately beside the field with minimal spacing.

- Every data-entry window has one compact, read-only automatic document-number field at the upper-left of the window.
- The number is system-generated and cannot be typed, pasted, or changed by users.
- The field is visually compact and consistent across all windows.
- Two navigation arrows are placed immediately to the right of the field and two to its left. Their physical placement must remain upper-left and must not be mirrored into an inconsistent layout by RTL/LTR changes.
- The arrows nearest the field move one record backward/forward.
- The next arrows move ten records backward/forward.
- An optional outermost pair moves to the first and last available records.
- In RTL and LTR layouts, arrow tooltips and accessibility names must express record movement, not ambiguous visual direction.
- Navigation is over persisted records of the current company and current document type, ordered by the stable record creation/sequence order. It is not arithmetic on the displayed number, because deleted records and legacy imports can create gaps.
- Soft-deleted records are excluded from normal navigation and remain accessible through Recycle Bin workflows. Permissions and company isolation apply to every navigation query.

## Number lifecycle

1. Opening a blank form must not reserve or consume a number.
2. The form may display a non-reserving next-number preview, clearly distinguished internally from a persisted number. The definitive number is assigned atomically when the new document is first saved.
3. Number allocation is database-backed, company-scoped, document-type-scoped, and fiscal-year/period-aware where the numbering policy requires it.
4. Allocation and first document insert occur in the same transaction. Concurrent users must never receive the same number.
5. Saving or editing an existing draft never changes its number.
6. Posting, reversal, return, cancellation, and amendment operations preserve their own document identities and links. A posted number is never silently recycled.
7. Soft delete moves the document to Recycle Bin and preserves its number and audit history. Restore brings back the same record and same number.
8. Permanent deletion removes the document and its number assignment only when business, legal, audit, and relationship rules permit it. Posted accounting or stock evidence must first be reversed/corrected and must not be erased. An issued posted number is never reused; an unposted draft number may only be reclaimed if policy permits and it is provably the latest uncommitted allocation.
9. Number allocation failures are hard errors. The application must never silently fall back to a default such as 0001.
10. Unique database constraints enforce (company, document type, number) uniqueness in both SQLite and PostgreSQL.

## Navigation behavior

- Previous/next one: move one persisted record in the ordered result set.
- Previous/next ten: move ten persisted records, clamped to the first/last record.
- First/last: move to the first/last persisted record when the optional outer pair is enabled.
- Disabled states are explicit at the ends of the result set.
- Navigation must prompt before discarding unsaved changes.
- Navigation must not mutate the record, allocate a number, or alter accounting state.
- The current record identity is its database primary key, not its displayed number.

## Implementation requirements

- Use a shared WPF control/view-model contract rather than duplicating arrow logic in every window.
- Use one provider-neutral numbering service/repository contract with SQLite and PostgreSQL implementations.
- Keep SQL schema definitions under dba migrations; no runtime DDL in service files.
- Use parameterized SQL and transaction-safe allocation. Use provider-appropriate row locking/atomic update semantics for PostgreSQL and SQLite.
- Apply the same behavior to all entry windows through a controlled rollout and window inventory; do not claim universal coverage until every entry window is audited and integrated.
- Preserve existing large, valuable files; add shared infrastructure and make focused edits rather than splitting files unnecessarily.
- English-only source code/comments and resource-based UI strings; localization must support RTL/LTR.
- Audit create, number assignment, edit, post, reverse, soft delete, restore, and permanent delete events.

## Acceptance tests

- Concurrent first-save attempts for the same company/type never produce duplicate numbers.
- A failed save rolls back both the new record and its number allocation.
- Opening/canceling a new form consumes no number.
- Editing a draft preserves its number.
- Posted documents cannot be hard-deleted or have their numbers recycled.
- Soft delete/restore preserves the exact number and database identity.
- A permitted permanent deletion removes the document-number association without erasing protected accounting evidence.
- Navigation by 1 and 10 works across gaps, filters, document types, and company boundaries.
- First/last navigation clamps correctly; empty result sets disable all arrows.
- Unsaved-change confirmation works for every navigation command.
- SQLite and PostgreSQL produce equivalent numbering and navigation behavior.
- All entry windows place the compact number field at the upper-left with the same four-arrow behavior; optional first/last arrows are consistent.
