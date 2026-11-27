<!-- source: docs/DOCUMENT_NUMBERING.md -->

# Document Numbering

## Purpose

Document numbers are company-scoped business identifiers. Each source-document family owns its own series. Most operational series continue across fiscal periods by default; the central general-ledger journal (JRN) is the explicit exception: it restarts at 1 for every fiscal period. Numeric suffixes have no leading zeroes.

## Runtime contract

- Source-document series such as sales, purchases, returns, JV, RCV and PAY remain independent from the central JRN sequence.
- The central JRN number is generated only inside the posting transaction, after the document's fiscal period has been resolved from its entry date.
- JRN uses a dedicated counter keyed by company, series and fiscal_period_id. The first posted entry in a period is JRN-1; subsequent postings are JRN-2, JRN-3, and so on.
- JRN's prefix and start value are system-owned (JRN, 1). They are not configurable and the user cannot edit or reserve a JRN number from a window.
- A JRN allocation and the journal-entry insert/status transition occur in the same database transaction. Concurrent postings serialize on the fiscal-period counter row.
- Existing JRN numbers in the same period seed the counter when upgrading a database, including reversed and soft-deleted entries. Numbers are never reused.
- SQLite and PostgreSQL use provider-specific migration definitions for the period counter table. The legacy document_number_sequences table remains responsible for other series.
- The database uniqueness constraint scopes journal numbers by company_id, fiscal_period_id and document_number, allowing JRN-1 to exist once in each distinct fiscal period.
- Journal lookup by a central JRN number requires a date so the fiscal period is unambiguous. Source numbers such as JV, RCV and PAY remain searchable through their preserved source_doc_no.

## Reservation and gaps

A JRN number is not reserved when a form opens or a draft is created. It is assigned only when the journal is posted. If posting fails, the transaction rolls back and the number remains available. Once a JRN number has been committed, it is never reissued after reversal, soft deletion, or permanent deletion. Gaps caused by a committed reversal or deletion are acceptable.

Source-document numbers follow their own family policies and must not be confused with the central JRN number. A voucher can retain its JV, RCV, or PAY source number while the posted ledger entry receives a separate JRN number.

## Schema ownership

The fiscal-period counter is defined only in forward DBA migrations:

- dba/mig/sqlite/128_fiscal_period_jrn_sequences.sql
- dba/mig/postgresql/128_fiscal_period_jrn_sequences.sql

Do not add runtime DDL to repositories or windows. DocSchema and the migration smoke workflow validate migrated schema.

## Required regression coverage

- First JRN in a fiscal period is JRN-1; the next posted journal in that same period is JRN-2.
- A different fiscal period can independently allocate JRN-1.
- A reversed, binned, or deleted JRN is never reused within its period.
- Concurrent postings in one company and period receive distinct, increasing JRN values.
- JRN lookup requires a fiscal-period date when a number could repeat in another period.
- JV/RCV/PAY source numbers remain searchable after the journal receives its central JRN number.
- SQLite and PostgreSQL migrations are repeatable and produce equivalent schema.

## Change policy

Treat numbering as an accounting control. JRN's period scope, immutable prefix/start, transaction boundary and non-reuse policy are fixed system rules. Any future change requires provider-parity tests and an explicit product decision.
