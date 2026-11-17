# Window Field and Database Persistence Audit

- Repository: `Roni4422/ShouTech.ERP.Company-fresh`
- Reference branch audited: `main`
- Audit date: 2026-10-16
- Audit type: static review of the code, event paths, save and load paths. **Not a comprehensive GUI run test**.
- Inventory scope: the UI files in `cod/app/src/Views`, including XAML windows, code-built windows, and reports. Save paths were inspected directly in the main windows, and the visible handlers were compared against repository and database calls.

## Executive Result

**No — not every field is accepted and saved to the database in the version audited.** Some windows display a success message without performing an actual save, while others save through real repositories. It is therefore not valid to declare all fields sound.

## Confirmed High-Priority Defects

| Priority | Window | Evidence from the code | Result |
|---|---|---|---|
| P0 | Customer card `CstCard.cs` | Save/Delete/Statement buttons only show `MessageBox.Show` | Customer data is not saved/deleted to the database from this window, and the success message is misleading |
| P0 | Bank card `BnkCard.cs` | Save/Delete only show a message; no repository call in the handler | Bank data is not actually saved/deleted |
| P0 | Cost center card `CcCard.cs` | Save/Delete only show a message | No actual saving of the fields |
| P0 | Sales representative card `RepCard.cs` | Save/Delete only show a message | No actual saving of the fields |
| P0 | Currency card `CurCard.cs` | Save only shows a message; adding a historical rate only modifies an in-memory collection | The currency and exchange rates are not saved from this window |
| P0 | Receipt and payment vouchers `CshWin.cs` | `SaveVoucher` only shows a message; `PostVoucher` only changes the UI state | No actual save or accounting posting from this window |
| P0 | Journal vouchers `JrnWin.cs` | `SaveVoucher` checks that lines exist and then only shows a message; `PostVoucher` locks the UI and shows success only | No actual save/posting in the database |
| P0 | Cheques `ChqWin.cs` | Save/Print only show a message; cheque data is collected in an ObservableCollection | No actual saving of the batch or the cheque details |
| P0 | Sales and purchase orders `OrdWin.cs` | Save only shows a message; converting the order opens a new invoice without saving the order itself | The order and its fields are not saved, and the conversion does not record a persisted relationship to the original order |
| P0 | Manufacturing orders `MfgWin.cs` | Save/Post/Print show messages, and posting only changes the state of the buttons and the grid | No actual saving of the order or of inventory/production-cost movements |
| P1 | Point of sale window `PosWin.cs` | The payment path goes to `PosCheckoutWin`, which calls `InvRepo.SaveDraft` then `InvRepo.Post` | A real save path exists at checkout, but a full test is required to confirm that every field/payment method reaches the correct document |
| P1 | Material card `MatCard.xaml.cs` | A material record is built from the fields, including units, prices, and properties, with `MatRepo` calls | The path exists; it is not yet proven that every additional field is written and read back round-trip on SQLite and PostgreSQL |
| P1 | Account card `AccCard.xaml.cs` | `Save_Click` validates the tax rate, builds an `AccRec`, calls `AccRepo.Save`, and reloads the record | Actual saving exists; all fields, relationships, and boundary values must be tested |
| P1 | Invoices `InvoiceWin.cs` | `CaptureDoc` collects the document header and lines, then `InvRepo.SaveDraft`; posting via `InvRepo.Post` | Actual saving exists; comprehensive field tests and reopening the document after save are required |
| P1 | Warehouses `WhsWin.xaml.cs` | Warehouse code/name validation and a `MatRepo.SaveWarehouse` call | Actual saving exists; all address fields and relationships must be tested |
| P1 | Categories `CatWin.xaml.cs` | Load/edit/delete via `MatRepo` | A data path exists; saving of every field and reading the values back after reopening must be confirmed |

> Note: P0 here means a confirmed functional defect in the visible save path, not a security risk estimate.

## What Must Be Fixed Before the System Is Approved

1. Replace the fake success handlers in the P0 windows with real services/repositories, and never show a success message until the database operation has succeeded.
2. Add consistent data schemas and save/load/edit/delete operations, with parameterized SQL where needed.
3. Validate required fields, decimal values to the approved precision, dates, currencies, accounts, customers/suppliers, warehouses, and cost centers.
4. Ensure line details are saved with the document header in a single transaction, that the entry balances before posting, and that document numbers are not duplicated.
5. Implement round-trip tests: create a record with all fields, save it, read it back from the database, compare all values, then edit it, save, and read it back again.
6. Run the tests on SQLite and PostgreSQL, with separate company data and tests that prevent cross-company data leakage.
7. Add UI smoke/automation tests on Windows; the current unit tests alone do not prove that every window works from the UI.

## UI File Inventory

The inventory covers every known UI file in the tree; not every file is a data-entry window. Report files mostly display data and do not save operational records.

### Core and administration windows
- `MWin`, `LogWin`, `Lang`, `StpWiz`, `ComWin`, `PrmWin`, `SplWin`, `SerWin`, `DatWin`, `BkpWin`, `RestoreCoWin`, `BinWin`, `ColChsWin`.

### Cards and dictionaries
- `AccCard`, `MatCard`, `MatDirWin`, `CatWin`, `WhsWin`, `CstCard`, `BnkCard`, `CcCard`, `CurCard`, `RepCard`.

### Documents and transactions
- `InvoiceWin`, `OrdWin`, `JrnWin`, `CshWin`, `ChqWin`, `PosWin`, `PosCheckoutWin`, `MfgWin`, `BarWin`, `BcdGenWin`, `BcdDesWin`, `CashDrawer`.

### Display modules and reports
- `DashVw`, `ModuleWorkspaceView`, `ReportGridVw`, `RptBase`.
- `AccBalWin`, `AccStmtWin`, `BalSheetWin`, `CustAgingWin`, `CustBalWin`, `IncStmtWin`, `ItemProfitWin`, `JrnListWin`, `MatListWin`, `MatMoveWin`, `PriceListWin`, `PurListWin`, `SalListWin`, `ShortListWin`, `StkAdjRptWin`, `StkBalWin`, `SupAgingWin`, `SupBalWin`, `TrialBalanceWin`.

## Limits of This Report

This is a static audit report based on the code available on `main`. It does not claim that every field in every window was exercised at runtime; UI and database testing is required to complete that. However, save handlers that merely show a success message without calling storage are defects proven by the code and must be addressed before the accounting data can be relied upon.
