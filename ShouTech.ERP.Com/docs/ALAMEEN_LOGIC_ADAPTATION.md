# Al-Ameen Reference Review — Business Logic Adaptation

The archived `System.zip` contains legacy SQL Server objects. These are used as behavioral references only; SQL Server procedures, functions, hints, and syntax are not copied into the active SQLite/PostgreSQL runtime.

## Inventory lessons captured from the reference set

- `fnGetMaterialsTree.sql`: material groups and materials are presented as a hierarchy. ShouTech should preserve parent/child relationships and deterministic tree ordering instead of treating every material as a flat lookup.
- `fnFindMaterialByBarcode.sql`: barcode lookup supports multiple barcode fields and search modes. ShouTech's normalized barcode model should support exact matches first, then controlled prefix/contains searches, and return the material plus the matched unit context.
- `fnGetMaterialUnitFact.sql`: a material can have multiple units and unit conversion factors. Quantity entry, barcode lookup, sales, purchasing, and stock movement must use one shared conversion rule rather than reimplementing conversion in each screen.
- `fnMaterial_getQty.sql` and `fnMaterial_GetQty_WithUnposted.sql`: the legacy system distinguishes posted transactions from transactions not yet posted. ShouTech should expose posted on-hand quantity separately from pending/unposted quantities and must not silently mix them in accounting or availability reports.
- `fnAccount_getBalance.sql` and `prcEntry_Post.sql`: account balances depend on posted entries and their debit/credit lines. ShouTech's posting path now persists a draft atomically, validates debit/credit balance again at posting time, changes state only from Draft to Posted, and writes an audit event in the same transaction.

## ShouTech implementation status

- The provider-neutral `JournalRepository` uses `DbConnection`-level operations with SQLite and PostgreSQL connections, transaction-scoped writes, conditional state transitions, and audit events.
- The outbound queue is now a first-class migration-managed table for both providers. The worker claims messages conditionally to reduce duplicate dispatch, supports HTTP(S) webhooks, and applies bounded exponential retry delays. Email/SMS messages are retained and retried as unconfigured rather than falsely marked as sent.
- The legacy POS ticket sync worker depended on SQL Server-only tables (`PosTickets`, `PosSyncLog`) not defined by the active ShouTech migrations. It has not been reintroduced as a fake sync process. A real sync adapter requires a defined source/target contract and idempotency strategy.
- The old license watchdog queried SQL Server notification tables and was not a license validator. License enforcement remains in the application's license/security services; the Windows service does not bypass the native dongle/security validation or infer license validity from unrelated notification tables.

## Accounting caveat

The current `ReverseAsync` marks a posted entry as Reversed and records an audit event; it does not yet create a separate compensating journal entry. Before production accounting close, implement explicit reversal-entry linkage and verify balance-report rules against the business requirements. Do not interpret the status transition alone as a full double-entry reversal workflow.
