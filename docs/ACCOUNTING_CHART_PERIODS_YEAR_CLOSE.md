<!-- source: docs/ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md -->

# Chart of Accounts, Fiscal Periods, and Year-End Controls

## Scope

This phase adds a provider-neutral accounting structure for SQLite and PostgreSQL. It does not delete existing business records. Legacy journal rows are assigned to generated monthly periods where their dates fall within a fiscal year.

## Chart of accounts

`chart_of_accounts` stores company-scoped account codes, names, account types, optional parent accounts, and active/deleted/postable flags. Account types are `ASSET`, `LIABILITY`, `EQUITY`, `REVENUE`, and `EXPENSE`. Parent accounts must be non-postable. Journal draft creation now checks that every account exists in the same company and is active, not deleted, and postable. Database posting triggers repeat this check to protect against code paths that bypass the repository.

Before posting journals in an existing database, import or create the company's chart of accounts and map legacy account IDs to the new `account_id` values. The migration intentionally does not invent account definitions from opaque legacy IDs.

## Fiscal years and periods

A fiscal year has explicit start/end dates and a closed flag. Each new fiscal year created by `AccountingStructureRepository.CreateFiscalYearAsync` receives twelve monthly periods. A journal draft is assigned to the single open period that matches its company and date. Posting is rejected when no period matches, the period is closed/locked, or the fiscal year is closed.

Existing fiscal years are retained. For legacy journal history, migration 009/011 creates fiscal-year/period records for years represented by journal entries and attaches matching entries to periods. If the organization uses a non-calendar fiscal year, review and correct the dates before posting new entries; do not assume calendar years fit every company.

## Closing a period

Call `AccountingStructureRepository.ClosePeriodAsync(periodId, actor)`. The method:

- refuses closure while draft entries remain;
- checks that posted/reversed entries in the period balance;
- requires periods to close in chronological order;
- records the closing actor and timestamp;
- only transitions `OPEN` to `CLOSED`.

The database trigger independently prevents a journal from being posted into a closed period or closed year.

## Closing a fiscal year

Call `AccountingStructureRepository.CloseFiscalYearAsync(fiscalYearId, actor)` only after all twelve periods are `CLOSED` or `LOCKED`. It refuses closure if drafts remain, posted journals are unbalanced, or any active revenue/expense account retains a non-zero net balance. This last check requires an approved income-statement closing journal to have been posted first; this repository does not automatically choose retained-earnings accounts or create that closing journal because those mappings are company/accounting-policy specific.

A closed fiscal year cannot accept new journal postings. There is intentionally no automatic reopen method; reopening requires a separately authorized and audited workflow.

## Al-Ameen reference adaptation

The design follows the useful accounting patterns seen in the supplied Al-Ameen SQL references: account code/name lookup, account hierarchy, unit/period-aware balances, and posted-document semantics. SQL Server stored procedures were treated as business-rule references only and were not copied into active SQLite/PostgreSQL migrations.

## Deployment checks

1. Back up each company database before applying migrations.
2. Apply migrations in the existing migration runner, not by manually replaying individual SQL files.
3. Populate/migrate the chart of accounts and verify all legacy journal account IDs.
4. Verify fiscal-year start/end dates and period mappings for each company.
5. Run the trial balance and reconcile it before closing periods or years.
6. Test PostgreSQL against a real supported server before production use; SQLite-only smoke tests do not certify PostgreSQL behavior.

---

<!-- source: docs/ACCOUNTING_REVERSALS_AND_OUTBOX.md -->

# Accounting reversals and outbound queue reliability

## Journal reversal behavior

A reversal is an auditable accounting operation, not just a status change. `JournalRepository.ReverseAsync` now performs the following work in one database transaction:

1. Reads the source entry and verifies that it is currently posted.
2. Reads and validates the source lines, requiring at least two lines and equal, non-zero debit/credit totals.
3. Creates a new posted journal entry linked through `reversal_of_entry_id`.
4. Copies every line while swapping debit and credit amounts.
5. Marks the original entry as reversed, writes audit events for both entries, and commits atomically.

A unique partial index on `reversal_of_entry_id` prevents more than one reversal entry from referencing the same source entry. The method returns the new reversal entry ID. A failure at any point rolls back the new entry, copied lines, status change, and audit events together.

The reversal uses the source entry date to keep it in the same accounting period. If business policy requires reversal in the current/open period instead, that must be decided and enforced alongside fiscal-period closing rules before production rollout.

## Outbound message recovery

The outbound worker re-queues messages left in `SENDING` for more than 15 minutes. The HTTP dispatch timeout is 15 seconds, so this recovery window is deliberately conservative. It protects against process termination after a message was claimed but before its status was updated.

Webhook dispatch is the only active channel. EMAIL and SMS stay queued/retry and eventually become `FAILED` until real provider connectors and secret management are configured. Webhook delivery is at-least-once rather than exactly-once: a remote endpoint may receive a request even if the worker crashes before recording `SENT`. Consumers should use an application-level idempotency key before this is used for financial or other non-repeatable side effects.

## Validation performed

The SQLite migration sequence was applied to an in-memory database. A database-level smoke test exercised the reversal schema, balanced swapped lines, original status transition, duplicate-reversal prevention, and stale outbox recovery. A C# build and a live PostgreSQL integration test still need to be run in an environment with the .NET SDK and PostgreSQL available.
