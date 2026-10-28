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
