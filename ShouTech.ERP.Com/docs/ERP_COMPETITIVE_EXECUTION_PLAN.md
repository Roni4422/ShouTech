# ERP Competitive Development — Execution Baseline

Baseline repository: `Roni4422/ShouTech`
Baseline branch: `reduce`
Baseline commit: `6864f035a2ee22ef0a05a1ec69deca21ea2282f3`
Development branch: `work/erp-competitive-development`

This branch is intentionally separated from `reduce`. The reduce branch's
own directive defines it as a file-layout consolidation with no behavior
changes; functional ERP development must not destabilize that work.

## Working rules

1. Inspect the current implementation and its callers before changing code.
2. Preserve existing public APIs and database compatibility unless a migration
   is explicitly part of the work.
3. Every business feature must be complete across UI, permissions, service,
   persistence, posting, audit, localization, and reports where applicable.
4. SQL DDL belongs under `dba/mig/{sqlite,postgresql}`; runtime services must
   not create tables.
5. Source code and comments remain English-only; UI text comes from language
   resources.
6. Posted accounting documents are reversed by compensating transactions;
   never delete or rewrite posted history.
7. A feature is not complete until its regression scenarios run. Record
   separately what was executed on SQLite, PostgreSQL, and Windows.
8. Do not claim parity from a window or a migration alone. Compare complete
   business workflows and their accounting/inventory outcomes.

## Baseline findings from the current reduce tree

### Procure-to-pay

The purchase completion record reports 21 of 29 acceptance checks proven,
7 partial, and 1 gap. Accounting checks are reported proven, but several
technical and functional checks still need execution or end-to-end evidence.

Open items recorded in `docs/PURCHASE_CYCLE_COMPLETION.md` include:

- Reversing a posted purchase return (PRT) must restore its match ledger and
  stored PO billed percentage. `RewindPurchaseReturnMatches` exists, but the
  completion record says no production reversal caller invokes it.
- Cross-currency payment allocation remains intentionally unsupported pending
  an owner decision.
- Batch/serial traceability needs a complete end-to-end harness.
- Attachment authorization/audit needs an end-to-end test.
- Document-number concurrency stress is still owed.
- Full purchasing report-catalog coverage has not been audited.
- PostgreSQL migration and integration execution is still owed.
- Windows WPF build and click-through verification is still owed.

The earlier sections of the purchase completion document contain stale
statements (for example, withholding is labelled missing in the initial
summary although the later W1 section records its implementation). Reconcile
the document after each verified work package; do not treat the opening
summary as current evidence.

### Order-to-cash

The sales completion record reports the core lead-to-cash flow substantially
implemented. Its §18 scoring identifies these remaining gaps:

- Sales orders do not yet reserve stock, although `reserved_qty` exists in
  stock-balance schemas and SAL-7011 is catalogued.
- The complete 17-category sales report catalog and its full filter matrix
  are not delivered as one verified set.
- PostgreSQL execution, Windows test execution, concurrent numbering stress,
  and WPF visual verification remain outstanding.

The opening comparison tables in `docs/SALES_CYCLE_UPGRADE.md` are stale in
places. For example, the current `InvoiceWin` and `SalesReturnWin` contain
sales permission checks that the opening gap table still describes as absent.
Use the later §18 scoring and current source as the baseline, and reconcile
the older tables only after tracing each item.

## First implementation work package: sales-order stock reservation

### Required behavior

- A posted/confirmed sales order reserves available stock for its eligible
  stock-managed lines; it must not create a stock movement or GL entry.
- Reservations are company-, warehouse-, material-, and source-line-scoped.
- Service and non-stock materials are excluded.
- A reservation cannot make total reserved quantity exceed eligible on-hand
  quantity after existing reservations.
- Concurrent order posting must serialize the availability check and reserve
  write in one transaction.
- Partial delivery consumes only the reservation quantity attributable to the
  delivered source line; undelivered balance remains reserved.
- Direct invoicing consumes the relevant reservation while issuing stock once.
- Cancellation releases remaining reservations; amendments/revisions reconcile
  the old and new order quantities without double-reserving.
- Return documents must not recreate a sales-order reservation.
- Batch/serial-controlled items must not claim a specific batch/serial unless
  the reservation model can preserve that identity safely. Until then, reserve
  at material/warehouse level and let the posting allocator enforce tracking.

### Persistence and accounting invariants

- Add a dedicated reservation ledger/table through paired SQLite and
  PostgreSQL migrations. Do not rely only on a mutable aggregate
  `reserved_qty` field as the source of truth.
- Maintain aggregate stock-balance and batch-balance reserved quantities
  transactionally from the reservation ledger.
- Every reserve, consume, release, and adjustment must be idempotent and
  auditable with document, source line, actor, timestamp, and reason.
- Reservation changes must never alter inventory valuation, GL, or on-hand
  quantity.
- Stock availability checks in all issue paths must consistently use
  on-hand less active reservations, with a documented negative-stock policy.

### Required verification

1. New order reserves eligible stock; stock on-hand and GL remain unchanged.
2. Insufficient stock blocks posting with the catalogued sales reservation
   error and leaves no partial reservations.
3. Duplicate material lines reserve independently by source line.
4. Two concurrent orders cannot reserve the same final available quantity.
5. Partial delivery consumes only its own source-line reservation.
6. Cancellation releases the undelivered remainder exactly once.
7. Amended order adjusts reservations without duplication.
8. Direct invoice consumes reservation and issues stock once.
9. Service/non-stock lines never reserve.
10. SQLite and PostgreSQL produce equivalent reservation and posting results.

No production behavior is claimed as changed by this baseline commit.
