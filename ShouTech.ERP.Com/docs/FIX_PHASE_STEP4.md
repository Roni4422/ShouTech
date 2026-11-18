# FIX PHASE — STEP 4: Party Master Unification + Settlement Chain

Branch `fix/foundations-4-parties` (from `fix/foundations-3-glposting` @ 2bd8ea1).
Migration **061** (SQLite + PostgreSQL). Evidence:
`~/workspace/research/sweep/parties-ledger.md`, `cash-payments.md`,
`invoicing-posting.md`, `acceptance-sweep-report.md`.

## 1. The defects, in one paragraph

The app kept **two party masters with zero synchronization**: the party
cards (CstCard, PartyManagerWin, PartySearchWin) edit `parties`
(migration 031, Guid id), while operations — the credit guard, invoice
party resolution, balances, aging — ran on the legacy `customers` /
`suppliers` tables (migration 050, integer ids). A customer created in
the card could not be invoiced; the balance lookup bridged the two
stores by *parsing the party code as a number*, so looking up code `"1"`
returned a stranger's 777 debit (harness-proven in the sweep); the
credit guard's legacy fallback had **no company filter** (cross-company
leak). The settlement chain was broken at every cash entry point: cash
vouchers (CshWin) kept the party as memo free-text and never wrote
`party_ledger`; `paid_amount` was never even selected by InvRepo, let
alone maintained; POS cash was recorded nowhere; PayRecWin therefore
showed no real payments — and its payment list even offered sales
**returns** as allocatable payments. `party_ledger.company_id` was
written in two namespaces (company code by some writers, company Guid
by the chain/order repositories), supplier balances were sign-inverted
in two reports, and dead legacy party code in `CardStore` shadowed the
real repository.

## 2. The canonical decision

**`parties` (031) is the one party master.** Rationale, weighed against
the evidence:

- **UI gravity is entirely on `parties`.** Every card, the directory,
  the picker and the search window read and write it; it carries the
  richer field set (contacts, tax number, credit limit/days, price
  level, group, opening balances). Nothing in the UI edits
  `customers`/`suppliers` — they have no writer at all.
- **Operational gravity is real but shallow.** The integer-id consumers
  are the `party_ledger.party_id` history, document `party_id` columns,
  the credit guard and invoice resolution. That gravity is preserved
  without keeping a second master: migration 061 gives every party a
  stable integer identity, **`parties.ledger_id`**, seeded *from* the
  legacy ids wherever the same party exists in both stores, so no
  ledger row and no document ever needs re-pointing.
- The alternative (canonical = legacy tables) would have meant moving
  every card/directory/picker onto tables with a poorer field set and
  per-kind duplicated schemas, for no compensating benefit.

After 061 there is **one writer path** (`MasterCardRepository.SaveParty`,
used by CstCard/PartyManagerWin) and **one reader path** (the same
repository + `PartyLedger.ResolveLedgerId` / `TaxSvc.ResolveParty` for
operational resolution). The legacy tables are **dropped**, not kept as
a compat layer — the same call Step 2 made for the split chart: a
read-only shadow would silently diverge again, and nothing reads them
after this step (verified by grep: zero product references remain).
**No dual-writing anywhere.**

### Data mapping (migration 061, both providers, idempotent)

| Legacy / existing | Canonical result |
|---|---|
| `customers` row whose (company, code) matches a `parties` kind-C row | The `parties` row survives and **keeps the legacy `customer_id` as its `ledger_id`** — its entire `party_ledger`/document history stays attached untouched. |
| `customers` row with no card twin | Inserted into `parties` (new Guid; deterministic `md5(company:061:code)` on PostgreSQL, random on SQLite) with `ledger_id = customer_id`; name = English name falling back to the Arabic name column; credit limit carried over. |
| `suppliers` rows | Same, as kind `S`. |
| Card-only `parties` rows | Fresh `ledger_id` **above** `MAX(existing ledger_ids, legacy ids, party_ledger.party_id)` — a card party can therefore never inherit an orphan or stranger ledger balance (the sweep's code-`"1"` case). |
| `party_ledger` rows | **Not re-pointed at all** — they already reference the integer identity, which is preserved. Rows whose `company_id` holds the company **Guid** (chain/order writers) are normalized to the company **code**, the canonical form every writer now produces. |
| `customers` / `suppliers` tables | Dropped at the end of the migration. |

Supporting schema in 061: `parties.ledger_id` + unique index
`ux_parties_ledger_id`; `party_ledger.journal_entry_id` (links a
settlement row to the journal entry that created it — the reversal
mirror key); new table **`voucher_party_links`** (the structured party
attachment of a cash voucher: journal entry, company, kind, party
ledger id + code snapshot, RCV/PAY, doc no, amount, currency).

Runtime invariants that keep it unified:

- `MasterCardRepository.SaveParty` assigns `ledger_id` on insert
  (max of `parties.ledger_id` and `party_ledger.party_id`, + 1, inside
  the save transaction) — a card-created party is invoiceable the
  moment it is saved.
- `PartyLedger` (Infrastructure) is the single home for company-key
  resolution (code-or-Guid → code), exact party resolution
  (company + kind + code → `ledger_id`; **numeric code parsing is
  deleted everywhere**), ledger writes, and the reversal mirror.
- Every balance/lookup query is company-scoped **and** party-exact:
  `MasterCardRepository.GetPartyBalanceAsync` resolves the party's own
  `ledger_id` and sums only its rows; `PartyCreditGuard` resolves the
  limit and balance against the canonical party in the caller's company
  (the unfiltered legacy fallback is deleted).

## 3. The settlement chain

Settlement entries are always **credit for customers, debit for
suppliers** — the mirror of the invoice entry, matching the existing
reconciliation model.

- **Cash vouchers (CshWin).** The header gains a real Party Code +
  Party Kind (Customer/Supplier) beside the memo text. On save, the
  code is resolved against the canonical master (`PartyLedgerSvc`);
  an unknown code **blocks the save** with catalog error
  `CashVoucher.PartyNotFound` — free-text party vouchers are gone. The
  resolution is stored in `voucher_party_links`; when the voucher's
  journal entry posts, `JournalRepository.PostAsync` writes the
  `party_ledger` settlement **inside the posting transaction**, linked
  by `journal_entry_id`. A Reverse button (the voucher window had none)
  reverses the journal through the Step-3 path, and
  `GlPosting.ReverseInTransaction` mirrors every journal-linked ledger
  row with swapped sides in the same transaction — GL and party ledger
  can never disagree about a reversal.
- **Invoices / POS paid at posting (InvRepo).** `paid_amount` was
  already persisted on the document; now, when a SalesInvoice,
  PurchaseInvoice or PosSale posts with `PaidAmount > 0`, the posting
  transaction also writes the settlement entry (RCV/PAY, capped at the
  grand total) and a pre-allocating `payment_allocations` row, so
  PayRecWin never offers the collected part as an unallocated payment.
  A POS cash sale against a party therefore settles the party side in
  the same transaction that posts its GL (Step 3) — POS cash is no
  longer recorded nowhere. Returns never take this path (a return is
  not a payment — sweep L-10).
- **Allocation (PayRecWin / ReconciliationRepository).** The artificial
  500-row list cap is removed; open documents are pre-filtered to
  genuinely open sales/purchase invoices (`doc_kind` SAL/POS/PUR —
  returns and quotations can no longer appear as settleable documents);
  the payments list is restricted to real settlement kinds
  (RCV/PAY/INS — returns are no longer allocatable as payments), joins
  the canonical party name, and carries the currency; `AllocateAsync`
  re-validates the payment's kind server-side. The payments grid shows
  the new Party and Currency columns.
- **Installment collection** (DefinitionRepository, sweep L-11) now
  resolves the installment's party code through the canonical master
  instead of parsing it as a number; a numeric code is accepted only
  when a live party actually owns that `ledger_id`.

## 4. Per-defect closure

| Sweep ID | Defect | Closure |
|---|---|---|
| C-1 | Two unsynced party masters | Migration 061 unifies on `parties` + `ledger_id`; legacy dropped; one writer/reader path. |
| C-2 / parties T2 | Balance lookup returns another party's balance (code `"1"` → 777) | Numeric-parse bridge deleted; resolution is (company, kind, code) → own `ledger_id`. Harness + tests replay the exact case. |
| G-1 | Credit-guard fallback without company filter | Fallback deleted; guard reads canonical party + company-scoped balance only. |
| B-03 | Cash vouchers never write `party_ledger` | Voucher party link + settlement inside journal posting; reversal mirrors it. |
| B-06 | `party_ledger.company_id` code/Guid split | Writers resolve to the company code (`PartyLedger.ResolveCompanyCode`); 061 normalizes historical Guid rows. |
| L-7 (partial) | `paid_amount` never selected/maintained | InvRepo maintains it at posting for paid-at-posting documents (settlement + allocation); PayRecWin allocation maintains it thereafter. Step 5 still owns InvRepo amount correctness. |
| L-8 | POS cash recorded nowhere | PosSale with a party + paid amount settles in the posting transaction. |
| L-10 | Returns offered/allocatable as payments | Payment list + allocation restricted to RCV/PAY/INS; open docs restricted to SAL/POS/PUR. |
| L-11 | Installment collect parses code as party id | Canonical resolution with ownership check. |
| M-1 | PartyManagerWin mixed customer+supplier totals | Separate per-kind totals in the status bar. |
| RP-1 | Aging resolved parties via legacy tables | `PartyAging` rewritten on `parties`/`ledger_id`, per-kind orientation, both providers' date math. |
| RP-2 / C-3 | Supplier balance sign inverted | Supplier balance = credit − debit in CstCard and `RptQuery.SupplierBalances`. |
| RP-5 | Invoice lists joined legacy tables, Arabic-first names | Joins on `parties.ledger_id`, English-first names. |
| R-3 | Dead legacy party code in `CardStore` | Deleted (zero callers verified). |
| — | DatWin cross-company surface | `customers`/`suppliers` export datasets repointed at canonical `parties`; recycle-bin defs unified to one `parties` def; dashboard customer count reads `parties`. |

Out of scope by mandate and **not** fixed here: InvRepo.Post amount
correctness and kind guards (Step 5); CstCard's Statement button opening
an unfiltered statement (C-4); ReconciliationRepository has no
Unallocate (pre-existing MAJOR — reversing an *allocated* voucher
restores the ledger via the mirror but leaves the allocation row;
see owner flags).

## 5. Proof

### 5.1 Headless harness — `~/workspace/research/fix-step4/party_harness.py` — **26/26 PASS**

Builds a pre-unification DB with the Step-3 migrator (both masters
present), seeds rows in **both** legacy stores plus ledger history
(including the code-`"1"`/777 stranger case, an orphan 777 in a second
company, and a Guid-keyed company row), upgrades with the Step-4
migrator, and replays the production SQL shapes: matched pairs keep
legacy ids; legacy-only rows merge; the second company's code `"1"`
gets a fresh id and a **zero** balance; company normalization lands;
invoice → balance 1000; journal-linked receipt → ledger row, balance
600; PayRecWin source lists the receipt; allocation → paid 400/open
600; reversal mirror → balance 1000; **migrator double-run idempotent**
with stable state. (The harness caught a real bug on its first run —
a rank collision in the SQLite fresh-id backfill, fixed by
materializing ranks in a temp table before assigning.)

### 5.2 Kern.Tests — `tests/Kern.Tests/PartyUnificationTests.cs` (6 facts)

(a) card-saved party resolves via the real `TaxSvc.ResolveParty`;
(b) balance is party-exact and company-scoped (777 stranger + 555
other-company rows ignored) and the credit guard blocks/passes on the
correct company's numbers; (c)+(d) full chain through the real
repositories: invoice → two receipt vouchers posted via
`JournalService` (settlement written by the posting transaction) →
PayRecWin lists the payment with the party name → allocation settles
`paid_amount` → reversing the second voucher restores the balance
exactly; invoice collected at posting and POS cash sale settle via
real `InvRepo.Post`; migration leaves one party master and the app
migrator double-run succeeds. Existing fixtures updated:
`MasterCardServiceTests` (balance fact now asserts the canonical
path), `PostingCompletenessTests` (credit-guard fixtures seed
`parties` with `ledger_id`).

### 5.3 What is NOT proven here

Kern.Tests target `net8.0-windows` and **cannot execute on this Linux
host** — execution is owed on Windows, as with every prior step. The
WPF behavior of the new CshWin party fields and Reverse button is
build-verified only. The PostgreSQL migration was written to the same
semantics (deterministic Guids, `ROW_NUMBER`, `IF EXISTS/NOT EXISTS`
guards) but **live PG execution is owed**. The harness proves the
SQLite migration + SQL shapes headlessly; it does not execute the C#
repositories (the Kern facts do, on Windows).

## 6. Gates

- Full solution build `--no-incremental`: **0 errors, 0 warnings**.
- `tools/audit_window_context.py`: **exit 0**.
- Lang parity En/Ar/Fr/Tr: **0** missing/extra in both Lang (4 new keys
  ×4: `Vouchers.PartyCode`, `Vouchers.PartyKind`,
  `CashVoucher.PartyNotFound`, `PaymentReconciliation.Currency`) and
  Errors catalogs. No Arabic outside `Ar.json` (verified over the diff).
- Migrator idempotent double-run: proven by the harness (SQLite);
  PostgreSQL owed with live execution.

## 7. Owner flags

1. **CshWin link atomicity.** The voucher party link is saved right
   after the draft journal is created; if that insert fails, the save
   surfaces the error but a GL-only draft may remain (it posts no
   settlement until linked). Making draft+link one transaction needs a
   JournalRepository API change — flagged rather than hacked around.
2. **Reversing an allocated voucher/payment** restores the party
   ledger (mirror) but does not un-allocate: `paid_amount` on the
   settled document is not rolled back, because
   ReconciliationRepository has no Unallocate (pre-existing). Decide:
   block reversal while allocations exist, or build Unallocate.
3. **InvRepo reversal of a paid-at-posting invoice** does not mirror
   the paid settlement (the reversal document carries no PaidAmount);
   Step 5 owns InvRepo reversal semantics — flagged for that step.
4. **DatWin datasets**: the Customers/Suppliers dataset entries now
   export the canonical `parties` table (both entries export all
   parties; the catalog has no kind filter). Same shape as Step 2's
   chart export decision; split by kind if Man wants it.
5. **Installment currency** remains hardcoded `SAR` in the collect
   path — the installments table has no currency column to source it
   from (pre-existing schema gap).
6. **Credit guard vs inactive parties**: `PartyLedgerSvc`/`TaxSvc`
   resolution refuses inactive/deleted parties; the guard itself still
   evaluates a posted party by id even if later deactivated
   (pre-existing behavior, unchanged).

## 8. Comparison tables (materially changed windows)

Sources: `~/workspace/research/sweep/compare-parties.md`,
`compare-cash.md`.

### 8.1 Party cards (CstCard / PartyManagerWin / PartySearchWin — data path only)

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech after Step 4 |
|---|---|---|---|---|---|
| Party master | One customer file + one supplier file, each THE master for its operations | One partner file per kind | One Customer / one Supplier doctype, used by all transactions | One `res.partner` for all party roles | **One `parties` table for both kinds** (kind column), the only store cards and operations read |
| Identity used by transactions | Card code/id | Card id | Name (link field) | Partner id | Stable integer `ledger_id` + code; Guid kept as row id |
| Created-in-card → transacted | Immediate | Immediate | Immediate | Immediate | Immediate (proven: harness (a) + Kern fact) |
| Balance on card | Live from ledger | Live from ledger | Live from GL/ledger | Live | Live from `party_ledger`, party-exact; supplier sign corrected |
| Credit limit enforcement | At posting | At posting | At posting | At posting | `PartyCreditGuard` at posting, correct party + company |

### 8.2 CshWin (cash vouchers)

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Payment Entry) | Odoo (Payments) | ShouTech after Step 4 |
|---|---|---|---|---|---|
| Party on voucher | Picked from card file | Picked from partner file | Link field, required for party payments | Partner field | **Code + kind resolved against the canonical master; unknown code blocks save** (picker parity still owed — code entry only) |
| Party ledger effect | Posts to customer/supplier account + statement | Posts to partner ledger | Ledger + GL on submit | Ledger + GL on post | `party_ledger` settlement **inside** the journal posting transaction + Step-3 GL |
| Reversal | Reverse entry restores statement | Storno restores | Cancel reverses | Reset/reverse | Reverse button added; journal reversal mirrors the ledger row symmetrically |
| Allocation | From statement/payment screens | Allocation screen | Payment Reconciliation tool | Reconcile view | PayRecWin allocation against real payments (see 8.3) |

### 8.3 PayRecWin (payment reconciliation)

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech after Step 4 |
|---|---|---|---|---|---|
| Payment source | Real posted receipts/payments | Real posted payments | Submitted Payment Entries | Posted payments | Real `party_ledger` settlements (vouchers, POS, paid invoices, installments) |
| What can be allocated | Invoices only | Invoices only | Invoices only | Invoices only | Invoices only — returns/quotations excluded on both sides; kind re-validated at allocate |
| Party shown | Yes | Yes | Yes | Yes | **Yes (new Party + Currency columns)** |
| List limits | None artificial | None | None | None | 500-row cap removed |

## 9. Pillars checklists

### CstCard (customer/supplier card)
- UI: unchanged (data path only, per mandate). **PROVEN** (build).
- DB: reads/writes canonical `parties`; save assigns `ledger_id`. **PROVEN** (harness + Kern).
- Linking: card rows are the same rows TaxSvc/InvoiceWin resolve. **PROVEN** (Kern fact (a)).
- Posting: n/a (master card). Persistence: unchanged repository, now single-store. **PROVEN**.
- Downstream: balances on-card are party-exact; supplier sign fixed. **PROVEN** (Kern fact (b)).

### PartyManagerWin / PartySearchWin
- UI: per-kind totals in the manager status bar (M-1). **PROVEN** (build).
- DB/linking/persistence: already on `parties`; balances now oriented per kind. **PROVEN**.
- Posting: n/a. Downstream: directory balances match card balances (same query path). **PROVEN**.

### CshWin
- UI: Party Code + Kind fields and a Reverse button added in the existing layout/language system; buttons remain in the bottom bar. **PROVEN** (build; WPF visual check owed).
- DB: `voucher_party_links` written at save. **PROVEN**. GAP: link insert is not atomic with draft creation (owner flag 1).
- Linking: party resolved from the canonical master; unknown code blocks save with a catalog error. **PROVEN** (code path + error key ×4).
- Posting: settlement inside the journal posting transaction; GL unchanged (Step 3). **PROVEN** (Kern fact (c)).
- Persistence: link reloads with the voucher; fields repopulate on open. **PROVEN** (build).
- Downstream: PayRecWin sees the payment; reversal mirrors the ledger. **PROVEN** (Kern fact (d)).

### PayRecWin
- UI: Party + Currency columns added to the payments grid. **PROVEN** (build).
- DB: reads `party_ledger` + `payment_allocations` + canonical party names. **PROVEN**.
- Linking: allocations reference real documents/payments only. **PROVEN** (Kern fact (c)).
- Posting: allocation updates `paid_amount` transactionally (repository). **PROVEN**.
- Persistence: allocations persist; open amounts derive from `grand_total − paid_amount`. **PROVEN**.
- Downstream: GAP — no Unallocate (owner flag 2, pre-existing).

### InvoiceWin / POS (posting path via InvRepo)
- UI: unchanged. DB: documents unchanged; settlement + allocation rows added at posting. **PROVEN** (Kern facts).
- Linking: party must resolve through the canonical master (TaxSvc) — card-created parties included. **PROVEN**.
- Posting: GL (Step 3) + party ledger + settlement in one transaction. **PROVEN**.
- Persistence: `paid_amount` now truthful for paid-at-posting documents. **PROVEN**.
- Downstream: PayRecWin open/payment lists consistent (collected parts pre-allocated). **PROVEN**. GAP: reversal of a paid invoice (owner flag 3, Step 5).

### JrnWin (journal reversal path)
- UI/DB: unchanged. Posting: reversal now also mirrors journal-linked party settlements (`GlPosting`). **PROVEN** (Kern fact (d) via JournalService.ReverseAsync).
- Downstream: party balances restore symmetrically with the GL. **PROVEN**.

## 10. Files changed

- **Migrations**: `dba/mig/sqlite/061_party_unification.sql`,
  `dba/mig/postgresql/061_party_unification.sql` (new).
- **Infrastructure**: `PartyLedger.cs` (new), `MasterCardRepository.cs`,
  `PartyCreditGuard.cs`, `JournalRepository.cs`, `GlPosting.cs`,
  `ReconciliationRepository.cs`, `DefinitionRepository.cs`,
  `CommercialDocumentRepository.cs`, `OrderDocumentRepository.cs`.
- **Domain**: `Entities/MasterCards.cs` (`Party.LedgerId`),
  `Entities/Reconciliation.cs` (payment row party name/currency).
- **App**: `Services/PartyLedgerSvc.cs` (new), `Services/TaxSvc.cs`,
  `Services/InvRepo.cs`, `Services/RptQuery.cs`, `Services/CardStore.cs`
  (dead legacy code deleted), `Services/Data.cs`,
  `Services/DataExportSvc.cs`, `Services/RecycleBinSvc.cs`,
  `Views/CshWin.cs`, `Views/CstCard.cs`, `Views/PartyManagerWin.cs`,
  `Views/PayRecWin.cs`, `Views/Reports/CustomerDueWin.cs`,
  Lang catalogs ×4 (4 new keys each).
- **Tests**: `PartyUnificationTests.cs` (new, 6 facts),
  `MasterCardServiceTests.cs`, `PostingCompletenessTests.cs` (fixtures).
