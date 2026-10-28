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
