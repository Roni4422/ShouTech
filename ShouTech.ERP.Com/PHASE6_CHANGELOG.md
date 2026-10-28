# Phase 6 — Chart of Accounts and Fiscal Closing Controls

- Added company-scoped `chart_of_accounts` with account hierarchy, type, and posting eligibility.
- Added PostgreSQL `fiscal_years` and both-provider `fiscal_periods` schemas, indexes, and monthly period seeding for years represented by legacy journal history.
- Added backfill to assign legacy journal entries to matching periods without deleting business data.
- Journal draft creation now requires an open matching period and active, non-deleted, postable accounts from the same company.
- Posting is checked by both repository validation and database triggers; direct inserts as posted are also rejected when controls fail.
- Added `AccountingStructureRepository` methods to create fiscal years and accounts, close periods sequentially, and close fiscal years only after draft/balance checks and zero P&L balances.
- Prevented direct reopening of closed/locked periods and closed fiscal years.
- Exposed the repository from `AppHost.AccountingStructure`.
- Added `docs/ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md`.

Validation performed: all nine SQLite migrations execute on a fresh in-memory database; test cases verified period backfill for legacy entries, successful posting with active accounts and an open period, rejection of posting to a closed period, rejection of non-postable accounts, rejection of direct posted inserts into closed periods, and rejection of direct period reopening.

Limitations: the .NET SDK and a live PostgreSQL server were not available for this run, so C# compilation and PostgreSQL integration remain required. The income-statement closing entry is not auto-generated because retained-earnings account mapping and year-end policy must be configured per company; year closure blocks until revenue/expense balances are zero.
