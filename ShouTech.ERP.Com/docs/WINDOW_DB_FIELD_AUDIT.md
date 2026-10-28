# Window ↔ Database Field Persistence Audit

Branch: `main`
Scope: static source audit, first pass
Rule: a control bound in memory is **not** counted as persisted unless the save path writes it and the load path reads it back. A successful build is not a field round-trip test.

## Status definitions

- **Blocked**: save/post is deliberately rejected because there is no persistence implementation in the window.
- **Repository path exists**: the window calls a repository, but this does not prove every field/child row is mapped correctly.
- **Runtime round-trip pending**: save → close/reopen → compare every scalar field and child-grid row has not yet been demonstrated.

## High-priority data-entry windows

| Window | Entity / intended data | DB/repository path observed | Static finding | Field + child-row round-trip |
|---|---|---|---|---|
| `CstCard.cs` | Customers | No customer repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `BnkCard.cs` | Banks | No bank repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `CcCard.cs` | Cost centers | No cost-center repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `RepCard.cs` | Sales representatives | No representative repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `CurCard.cs` | Currencies / exchange rates | No currency repository call in window | **Blocked**; save reports unavailable | Not passed; exchange-rate rows need explicit coverage |
| `CshWin.cs` | Receipt/payment voucher + allocation lines | No voucher repository call in window | **Blocked**; save/post report unavailable | Not passed; header and every allocation row pending |
| `JrnWin.cs` | Journal header + debit/credit lines | `JournalService` + `JournalRepository`; company/account codes resolved through the active database | Real save/post path implemented; failures are logged and shown as failures | SQLite repository round-trip test added for header, lines, post, reversal, and cost centers; CI result pending; UI reopen/load is still not implemented |
| `ChqWin.cs` | Cheque batch + cheque rows | No cheque repository call in window | **Blocked**; save reports unavailable | Not passed; batch and every cheque row pending |
| `OrdWin.cs` | Sales/purchase order + item rows | No order repository call in window | **Blocked**; save is rejected and conversion now explicitly reports unavailable instead of opening an empty invoice (commit `ace64e2`) | Not passed |
| `MfgWin.cs` | Manufacturing order + item rows | No manufacturing repository call in window | **Blocked**; save/post report unavailable | Not passed |
| `InvoiceWin.cs` | Sales/purchase invoices and returns | `InvRepo.SaveDraft`, `InvRepo.Load`, `InvRepo.Post`, reversal path; material lookup via `MatRepo` | Real persistence path exists | Not proven for every header field and line field; round-trip pending |
| `PosCheckoutWin.cs` | POS sale | `InvRepo.SaveDraft`, `InvRepo.Post` | Real invoice persistence path exists | Tender/payment metadata and all invoice rows need verification |
| `AccCard.xaml.cs` | Chart-of-accounts card | `AccRepo.LoadAll`, `AccRepo.Save`, `AccRepo.Delete` | Real repository path exists | Every account field and parent/child behavior need round-trip tests |
| `MatCard.xaml.cs` | Material card, units/prices | `MatRepo.LoadAll`, `LoadById`, `Save`, `SoftDelete`; lookup helpers | Real repository path exists | Every material field plus all units/prices/barcodes need round-trip tests |
| `CatWin.xaml.cs` | Material groups/categories | `MatRepo.LoadGroups`, `SaveGroup`, `SoftDeleteGroup` | Real repository path exists | Every group field and parent relationship need round-trip tests |
| `WhsWin.xaml.cs` | Warehouses | `MatRepo.LoadWarehouses`, `SaveWarehouse`, `SoftDeleteWarehouse` | Real repository path exists | Every warehouse field and parent relationship need round-trip tests |

## Initial conclusions

1. **The audit cannot certify that every field is saved yet.** Static repository calls alone do not prove complete field mapping.
2. The ten blocked windows above do not currently provide a real database save path from their UI. Their forms must not be treated as production-ready data-entry screens.
3. `InvoiceWin`, `PosCheckoutWin`, `AccCard`, `MatCard`, `CatWin`, and `WhsWin` have repository paths, but require field-by-field verification against the actual SQLite and PostgreSQL schema.
4. Each document grid must be tested as parent + child records: save, reopen, compare every field and row count, edit one field, save again, reopen, and compare again. Posting must also be checked for atomicity and ledger/stock effects.
5. Reports, setup/security/backup/import-export windows, and the remaining master-data/document windows are not yet covered in this first pass. The complete inventory is still in progress; this file must not be read as a completed all-window certification.

## Required test matrix for each persistent form

- Record every input control and its backing property.
- Trace property → repository parameter/column → schema column.
- Trace schema column → repository mapper → input control on reload.
- Include nullable/optional fields, decimal precision, dates, checkboxes, combo-box foreign keys, and localized text.
- Include every DataGrid child row and add/remove/edit cases.
- Verify company/tenant scope, transaction boundaries, and error logging.
- Run SQLite and PostgreSQL tests where both providers are supported.


## Additional windows checked (second pass)

| Window | Observed data path | Static result | Round-trip |
|---|---|---|---|
| `CoaWin.xaml.cs` | `AccRepo.EnsureSchema`, `AccRepo.LoadAll`, `AccRepo.Save`, account import/export | Real account repository path; inspect every account property and import mapping | Not passed |
| `PosWin.cs` | `MatRepo.LoadById` and inventory/unit lookups; final checkout delegated to invoice workflow | Sales workspace itself is not the final persistence boundary; checkout and payment metadata need tracing | Not passed |
| `MatDirWin.xaml.cs` | `MatRepo.LoadAll`, `LoadGroups`, `SoftDelete` | Directory/search and delete use repository reads; verify delete confirmation/audit and all entry points | Not passed |
| `BcdGenWin.cs` | `MatRepo.LoadAll`, `LoadById(includeChildren: true)`, `MatRepo.Save` | Real material repository path. Commit `85d9e69` removes the unconditional success dialog: it now displays the saved/failed summary with an outcome-appropriate icon and logs per-material exceptions. | Not passed; generated barcode still needs reload verification |
| `SerWin.xaml.cs` | `MatRepo.LoadAll`; saves search-window preferences separately | Search dialog payload is a selected material, not a business-record save form | Not applicable to material persistence; preference round-trip not tested |
| `BkpWin.xaml.cs` / `RestoreCoWin.xaml.cs` | Backup/restore service workflow | These operate on backup packages/companies rather than ordinary form-row CRUD; require separate restore integrity tests | Not passed |
| `BarWin.cs` / `BcdDesWin.cs` | Barcode printing/design; design is documented as local configuration | Printing is not DB persistence; template/config persistence must be distinguished from business data | Not passed |

## Repository/schema cross-check observations

- `Inventory.cs` exposes real `MatRepo.LoadAll`, `LoadById`, `Save`, `SoftDelete`, `SaveWarehouse`, and group operations. The presence of these methods does not by itself prove that every `MatRec` field and each child table (extra barcodes, price list, suppliers, stock-related rows, units) is fully mapped in both directions.
- `InvRepo.cs` exposes `SaveDraft` and `Load`, with separate SQLite/PostgreSQL migration files for commercial documents. The invoice header and every line/tax/discount/payment-related field still need an explicit mapping comparison and database round-trip test.
- `Accounting.cs` exposes `AccRepo.LoadAll`, `Save`, and `Delete`; account-card and chart import paths need tests for all account properties, parent/child renumbering, and provider parity.
- The current PostgreSQL migration files `003_modules.sql` and `004_modules.sql` were fetched from `main` and have identical content (6,735 bytes and the same blob SHA). This is a migration-chain duplication risk that should be resolved only after checking the migration runner's applied-history compatibility; do not simply delete a migration that may already be recorded in deployed databases.
- The historical `docs/SCHEMA_AUDIT_PHASE1.md` explicitly marks itself superseded. Its 540/120/55 counts are historical, not a fresh current count. A fresh `tools/schema_audit.py` run and the strict mode should be run in CI before claiming that no service DDL remains.

## Next verification order

1. Audit each repository record type against SQL columns and its write/read mappers: `MatRec`, `WarehouseRec`, `MatGroupRec`, `AccRec`, `InvDoc` and `InvLine`.
2. Add automated integration tests using temporary SQLite databases for full create/read/update/re-read and child-row add/edit/remove; then run equivalent PostgreSQL tests.
3. Build a machine-readable per-window field matrix from controls and repository mappings. Mark a field **PASS** only after the persisted value is read back and compared; mark unsupported fields **BLOCKED**, never assume success.
4. Finish inventory of remaining `cod/app/src/Views/**` windows (reports, administration, import/export, company/user/security, configuration and document modules). Those windows are not certified by this partial report.


## Third-pass correction (2026-10-16)

- `BcdGenWin.SaveGenerated()` previously showed `Messages.SaveSuccess` even when `saved == 0` and all selected writes failed. Fixed on `main` in commit `85d9e69d47c74cede31e8075e2225cda73699983`: the dialog now shows the actual saved/failed summary and uses warning/error icons for partial/total failure; individual persistence exceptions are sent to `AppHost.Logging.LogError` with the material ID.
- This is a source-level correction only. It has **not** been runtime-tested here; barcode persistence must still be proven by reloading each changed material from the database. This audit remains partial and does not certify all windows or fields.

- `OrdWin.ConvertToInvoice()` was also corrected in commit `ace64e241bc38a65ff4cf26b883b7e7df20b1015`: the previous handler opened a blank invoice without transferring or persisting the order. It now reports the operation as unavailable, avoiding a misleading conversion workflow. Actual order persistence and conversion remain unimplemented.


## Fourth-pass findings (2026-10-16)

- **Dashboard data is hard-coded, not database-backed:** `cod/app/src/Services/Data.cs` implements `DataServ.GetTotalCustomersAsync()` as the constant `1284`, `GetTotalProductsAsync()` as `3567`, `GetTotalPendingApprovalsAsync()` as `12`, and returns a fixed sample list from `GetRecentDocumentsAsync()`. Dashboard figures and recent documents must not be presented as live company data. This needs replacement with provider-aware repository queries and empty/error states.
- **Account card failure logging:** commit `3a83615` adds a structured error log when `AccRepo.Save` fails, including the account code; the UI still reports failure rather than success.
- **Barcode design configuration failure logging:** commit `93f89f0` logs exceptions when the local design file cannot be saved. This configuration is stored in a file, not the business database, and should be audited separately from DB-backed forms.
- These are source-level checks only. No CI status was returned for the changes, and no database round-trip tests have been demonstrated.

## Fifth-pass findings (2026-10-16): remaining high-priority forms and reports

| Window | Persistence/data path observed | Current assessment | Required verification |
|---|---|---|---|
| `CurCard.cs` | Save button calls `ShowPersistenceUnavailable()` | **Blocked**: currencies/rates cannot be saved from this form | Add provider-neutral currency/rate repository and test currency fields plus rate history |
| `MfgWin.cs` | Save and post call `ShowPersistenceUnavailable()` | **Blocked**: manufacturing orders are not persisted | Define header/material-consumption/output rows and atomic posting effects |
| `CshWin.cs` | Save/post handlers ultimately call `ShowPersistenceUnavailable()` | **Blocked**: voucher header and allocations are not persisted | Implement receipt/payment repository; test header, allocations, posting and balancing |
| `JrnWin.cs` | Save/post call `JournalService` backed by `JournalRepository` | Database save/post path now exists; no false-success fallback | Verify CI and add database-backed load/reopen before claiming full form round-trip |
| `ChqWin.cs` | Save button calls `ShowPersistenceUnavailable()` | **Blocked**: cheque batch and cheque rows are not persisted | Implement batch/row persistence and audit lifecycle |
| `InvoiceWin.cs` | `InvRepo.SaveDraft/Load/Post/ReversePostedInvoice` | Repository path exists | Verify all header/line fields and transactional stock/accounting effects by round-trip |
| `PosCheckoutWin.cs` | Saves and posts through `InvRepo` | Repository path exists | Verify payment/tender fields, change due, customer, and every line survive reload |
| `MatCard.xaml.cs` | `MatRepo.LoadById/Save/SoftDelete` | Repository path exists | Compare all controls to `MatRec` mappings, especially units, barcodes, prices, suppliers and optional Arabic names |
| `WhsWin.xaml.cs` | `MatRepo.LoadWarehouses/SaveWarehouse/SoftDeleteWarehouse` | Repository path exists | Verify parent ID, code/name, active state, update and soft-delete persistence |
| `ComWin.xaml.cs` | Company store and configuration save/load paths | Mixed storage: company registry and local configuration are distinct | Verify which fields belong to the company database vs local settings; remove swallowed save errors and test provider-specific paths |
| `DatWin.xaml.cs` | Import/export options and provider-specific paths | Data movement workflow, not a business-record editor | Test import/upsert/replace modes, company matching, rollback, and accurate per-row failure reporting |
| `TrialBalanceWin.cs`, `AccStmtWin.cs`, `SalListWin.cs` | Async report data loaders | Report query path exists, but this scan alone does not prove data is live or correctly filtered | Test against seeded DB entries; verify company/date/account filters, totals and empty/error states |

### Fifth-pass conclusions

- Confirmed blocked data-entry forms include customer, bank, cost center, representative, currency, cash/payment voucher, cheque batch, order and manufacturing forms. `JrnWin` was removed from the blocked list after adding database save/post wiring and a repository round-trip test. These should remain explicitly unavailable rather than claim a save succeeded until real repository operations are wired.
- `AccServ` currently contains an in-memory `Dictionary<Guid, JournalEntry>`; it is not durable database persistence. It must not be used as proof that journal entries survive application restart.
- A repository call is only a starting point: report correctness and all-field persistence still require database-backed tests. No field is marked PASS without a create/save, reload, and value comparison.



## Sixth-pass correction (2026-10-16): journal persistence wired

- `JrnWin.cs` now resolves the active company code to the database company UUID, resolves account codes against active/postable company accounts, saves the journal header and all debit/credit lines through `JournalService`, and posts only after database validation. Exceptions are logged to the application log and displayed as failures, not success messages.
- The account-name grid field is now read-only and populated from the database lookup, so users cannot enter an account name that is silently ignored.
- New migrations persist the journal header's external reference, currency, and exchange rate in both SQLite (`012_journal_header_fields.sql`) and PostgreSQL (`014_journal_header_fields.sql`). The document number is persisted by the preceding provider-specific migrations. Existing records receive safe defaults for currency and exchange rate.
- Tests added: an end-to-end SQLite database test applies DBA migrations, resolves company/account codes, creates a fiscal year and accounts, saves a journal, verifies reference/currency/rate/document number and line memo/cost centers, posts it, reverses it, and checks that reversal retains the reference/currency/rate/cost centers while swapping debit/credit. CI also checks required journal-header columns after clean migration and idempotent re-run.
- **Not yet certified:** the WPF window does not yet provide a database-backed open/reload flow for a previously saved journal, and live CI results must be green before calling the change verified. PostgreSQL is schema-checked in migration CI; the full journal repository round-trip currently targets SQLite.
