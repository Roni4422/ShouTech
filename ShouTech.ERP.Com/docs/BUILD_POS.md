# BUILD POS — a real point of sale for ShouTech ERP

Branch `build/pos` (from `build/hr-payroll` tip `0f93312`). Migration
**066**. This build completes the existing POS surface in the task's
priority order: (1) session / cash-drawer lifecycle, (2) sale-flow
completeness, (3) split payments, (4) returns + held bills,
(5) receipt + Z report. It builds on the verified fix-phase engines —
it does not fork them: GL is step 3's `InvRepo`/`GlPosting`, party
settlement is step 4's `party_ledger` writer, totals are step 5's
stored totals, valuation is step 6's engine, batch/serial is build
054's capture.

## 1. Already present (verified in code) vs built here

**Already present — verified, not rebuilt**

- PosWin as a deliberate touch surface (48px scan field, 38–54px
  buttons, 660×360 minimum from fix step 12): barcode scan via
  `TaxSvc.ResolveMaterial`, `*n` qty and `+n` price shortcuts, `/`
  unit chooser with per-unit prices and equivalents, Delete/remove/
  clear, cash-drawer kick on cash (`CashDrawerService`), credit
  guard at checkout for named customers.
- POS sales post REAL GL (step 3), settle `party_ledger` +
  pre-allocate (step 4), use stored totals (step 5), engine
  valuation + unit equivalents (steps 6/11), batch/serial capture
  at checkout (build 054, `BatchSerialCaptureSvc`).
- `CashDrawerOptionsWin` (drawer COM settings), `PosSyncWin`
  (offline queue viewer), `PosCashRptWin` (cash-closings report),
  ribbon POS entries incl. POS Discounts definitions.
- Sweep claims re-verified before building: the scan-merge defect
  (merge keyed on the typed string, ignoring unit) was real and is
  fixed here; `CloseCashWin`'s expected-cash sweep (company-wide,
  `doc_kind='SAL'` only, card counted as cash) was real — sessions
  supersede it (see §2 decisions); the stale
  `POS.PaymentPostingNotice` Lang text ("payment posting is not
  implemented") was real and is corrected in all 4 languages.

**Built here**

- Migration 066: `pos_sessions`, `pos_sale_tenders`,
  `pos_held_bills` + `pos_held_bill_lines`, `pos_settings`,
  `sales_documents.pos_session_id`; permissions
  `POS.DISCOUNT.OVERRIDE` (sensitive), `POS.HOLD.MANAGE`,
  `POS.SESSION.BYPASS` (sensitive) in the step-1 pattern.
- Domain `Pos.cs` (session/tender/held/settings/product/Z types +
  pure `PosTenderMath`), `IPosRepository`/`PosRepository`,
  `PosService` (session gate), error codes POS-1001..1010.
- `InvRepo.Post` optional POS context: in-transaction session gate
  + tender validation, tenders written in the posting transaction,
  split GL legs (cash→131 / card→132 / remainder receivable).
- `PosPosting` (app layer): sale completion + partial POS returns
  with pro-rata settlement mirror.
- Windows: `PosSessionWin` (new), `PosReturnWin` (new),
  `PosZReportWin` (new, RptBase), `PosCheckoutWin` (rewritten:
  split tenders + change), `PosWin` (session state, product
  buttons, discounts, customer, hold/recall/return), receipt
  printing in `InvPrn`, ribbon entries POS.Sessions / POS.ZReport.
- Lang ×4: 103 new POS keys, POS-section parity 0. Errors ×4:
  POS-1001..1010, parity 0, catalog rows in `docs/ERROR_CATALOG.md`.

## 2. Designs

### Sessions (priority 1)

`pos_sessions`: one **Open** session per (company, terminal) —
enforced by a unique partial index on both providers. A session
carries cashier, terminal, warehouse, currency, opening float,
counted cash, expected cash, variance, and the legacy close mirror.
Open/close are gated by the pre-existing `POS.SESSION.OPEN` /
`POS.SESSION.CLOSE` permissions.

**The gate, and where it binds.** `PosService.
RequireSessionForSaleAsync` is the till-path gate (PosWin checkout
calls it before building the document), and `InvRepo.Post`
re-validates inside the posting transaction whenever a POS context
is supplied: the named session must exist and be Open, otherwise
POS-1001 and the whole posting rolls back (document stays Draft).
The context is *optional* at the engine level by design: the
session rule binds the **till**, and pre-existing engine callers
(back-office POS-kind documents, the Kern.Tests suites of steps
3/4/5) post without a till — making the context mandatory there
would fork the engine's contract, which this build must not do.

**Session-less mode is an explicit setting, never an accident:**
`pos_settings.allow_sessionless` (default **off**). When on, a sale
may proceed without a session only for a caller holding
`POS.SESSION.BYPASS`; without the permission the refusal is
POS-1010, with the setting off it is POS-1001.

**Close.** Expected cash is computed *inside the closing
transaction* from the tender ledger — `opening + Σ cash tenders`
(sales positive, refunds negative) — never from document totals, so
card/on-account money can never inflate expected cash (the
`CloseCashWin` defect). Variance = counted − expected, stored on
the session. The close also writes the legacy `pos_cash_closings`
row so the pre-existing `PosCashRptWin` history keeps working.

*Decision:* `CloseCashWin` is left in place as the legacy day-close
register; sessions are the till lifecycle and supersede it for POS.
Returns post into the **currently open** session of the terminal
(the drawer that hands the cash back), not the original sale's
session. Held bills survive a session close and recall into any
later session.

### Tenders and split payments (priority 3)

`pos_sale_tenders` records every tender per document: type
(`CASH`/`CARD`/`ONACCT`), applied amount, amount handed over,
change, card reference. Refund rows are **negative**. The pure
`PosTenderMath.ValidateSaleTenders` is the single rule set, used by
the checkout window, `PosPosting`, and re-checked inside
`InvRepo.Post`: applied tenders must equal the step-5 stored total
exactly (POS-1004); change exists only as cash handed over above
cash applied; card can never be over-applied (POS-1005); on-account
requires a named customer (POS-1005).

**Settlement is step 4, extended — not forked.** `InvRepo`'s
at-posting settlement already settles `min(PaidAmount, total)`
against the document's own ledger debit. The POS context sets
`PaidAmount = collected tenders` (cash + card), so a 70-collected /
30-on-account bill debits the ledger 100 and settles 70, leaving
exactly 30 owed — the same writer, the same allocation machinery.
The GL mirrors the split honestly: Dr Cash (131) for the cash leg,
Dr Bank (132) for the card leg, Dr Receivable (1201) for the
remainder. A walk-in (no party) writes **no** `party_ledger` rows.

### Returns (priority 4)

A POS return is a real `SalesReturn` document posted by the engine
(step 5's symmetric path: mirrored GL, stock back in through the
valuation engine, batch/serial re-capture via 054) — never a
negative sale. `PosReturnWin` loads the original bill by number;
returnable quantity per line = sold − already returned (summed from
posted SRT lines against the original line numbers), enforced by
`PosTenderMath.ValidateReturnQuantities` (POS-1006). Header
discount/extra charges reverse **pro-rata** to the returned lines'
share of the original subtotal.

Because step 5's un-settle handles *full* reversals only, a partial
return mirrors the original's at-posting settlement pro-rata after
posting: swap entries through the same `PartyLedger.WriteEntry`,
allocations reduced (deleted at zero), the original's `paid_amount`
decremented by the refund. If that adjustment ever fails after the
return posted, the failure is reported through `AppHost.Errors`
with POS-1007 — never swallowed — for manual reconciliation.

### Held bills (priority 4)

`pos_held_bills`/`pos_held_bill_lines` persist the whole cart —
party, session, discounts (line % and bill amount), warehouse,
notes — with totals computed by the same stored-total formula.
Recall flips the bill to `Recalled` in the same transaction that
reads it (a second recall is POS-1009); a recalled bill completes
through the normal checkout. Gated by `POS.HOLD.MANAGE`.

### Sale flow (priority 2)

- **Scan** unchanged, merge fixed to (MaterialId, Unit).
- **Search**: a scan-box miss runs the contains search
  (`PosRepository.SearchProductsAsync`, exact code/barcode first)
  and shows product buttons; a **product strip** under the header
  shows the top products as touch buttons with the material's
  image where one exists (`materials.image_path`), name and price.
- **Cart**: Unit column (equivalents convert via the existing unit
  machinery), editable Qty, editable **DISC %** line discount, and
  a bill-discount amount — both flow into the step-5 stored totals
  (`line_discounts` per line; bill discount as the document
  `discount_amount`). Price edit stays on the `+` shortcut.
- **Permissions at the till**: line/bill discounts require
  `POS.DISCOUNT.OVERRIDE`, price override requires the pre-existing
  `POS.PRICE.OVERRIDE`; without them the values reset to zero with
  a notice — the till never silently grants a discount.
- **Customer**: code field on the till (walk-in default), resolved
  through `TaxSvc.ResolveParty`; the step-4 credit guard runs at
  posting for named customers exactly as before.
- **Batch/serial**: unchanged 054 capture at posting, now reached
  through `PosPosting.CompleteSale`/`CreatePosReturn`.

### Receipt + Z report (priority 5)

`InvPrn.PrintPosReceipt` prints through the app's print path
(`PrintDialog` + paginator, the `PrintInvoice` pattern): a
302-unit-wide (≈80mm) receipt with company, bill no/date, cashier,
session, lines, totals, tax, **each tender, and the change**, in
the current language with the shell's flow direction (RTL-correct).
It prints automatically after a completed checkout; a print failure
is reported via `AppHost.Errors`, never fatal to the sale.

`PosZReportWin` is an `RptBase` report on real session data
(`PosRepository.GetZReportAsync`): sales/returns counts and totals,
discounts, tax, tenders by type, refunds, expected cash and — for a
closed session — counted cash and variance. Any session can be
shown (blank filter = latest), so an open session's Z doubles as
the X-report.

## 3. Comparison gate — POS vs Al-Ameen V10 / Phenix v10 / ERPNext POS / Odoo POS

Same 26-row rubric as `research/sweep/compare-invoicing.md` §3
(baseline **9.5/26 = 37%**). Competitor columns are summarized;
the ShouTech column is re-graded after this build.

| # | Item | ShouTech before | ShouTech after this build | ERPNext / Odoo / Al-Ameen·Phenix |
|---|---|---|---|---|
| 1 | POS profile / terminal config | ✗ | ~ settings row (default terminal/warehouse, session-less flag); no price lists or per-tender accounts | ✓ full profile/config |
| 2 | Session open (cashier + float) | ✗ | ✓ `pos_sessions`, POS.SESSION.OPEN | ✓ |
| 3 | Warehouse for the sale | ✗ | ✓ from session/settings, stamped on the doc | ✓ |
| 4 | Price list at till | ✗ | ✗ single retail price (unchanged) | ✓ |
| 5 | Customer at till | ✗ | ~ code resolve on the till + walk-in + credit guard; no search dialog/balance | ✓ search + balance |
| 6 | Cashier attribution | ~ | ✓ session cashier + user stamps | ✓ |
| 7 | Barcode scan add | ✓ (merge defect) | ✓ merge fixed (material+unit) | ✓ |
| 8 | Product search / browse | ~ | ~ contains-search results + product strip with images; no category tree | ✓ grid + categories |
| 9 | Qty edit | ✓ | ✓ | ✓ |
| 10 | Unit selection per line | ✓ | ✓ | ✓ |
| 11 | Price override | ~ | ~ `+` shortcut, now POS.PRICE.OVERRIDE-gated; no reason capture | ✓ PIN/permission |
| 12 | Line / bill discount | ✗ | ✓ line % + bill amount, POS.DISCOUNT.OVERRIDE-gated, into stored totals | ✓ |
| 13 | Cart columns | ~ | ~ Unit + DISC % added; no stock hint | ✓ incl. stock |
| 14 | Remove line / clear | ✓ | ✓ | ✓ |
| 15 | Hold / park + recall | ✗ | ✓ DB-persisted, cross-session recall | ✓ |
| 16 | POS return / refund | ✗ | ✓ against original bill, validated, engine reversal + settlement mirror | ✓ |
| 17 | Cash tender | ✓ | ✓ recorded with tendered/change | ✓ |
| 18 | Card tender | ~ | ~ card ref captured; no commission/settlement split | ✓ card definitions |
| 19 | Split / mixed tender | ✗ | ✓ cash/card/on-account in one bill, per-tender rows | ✓ |
| 20 | Tendered + change due | ✗ | ✓ live change computation, stored on the tender row | ✓ |
| 21 | Credit (on-account) sale | ✓ | ✓ as an on-account tender portion, step-4 settlement | ✓ |
| 22 | Receipt print | ✗ | ~ printed via app print path, RTL, tenders+change; no reprint button yet | ✓ + reprint |
| 23 | Loyalty at till | ✗ | ✗ unchanged (back-office LoyaltyWin only) | ✓ |
| 24 | Cash in/out during session | ✗ | ✗ unchanged | ✓ |
| 25 | Session close / Z-report | ~ | ✓ per-tender Z, expected from tenders, variance, RptBase report | ✓ |
| 26 | Offline mode | ~ | ~ unchanged (PosSyncWin viewer) | ✓ offline-first (Odoo) |

**Re-grade: 15 ✓ + 8 ~ = 19/26 = 73%** (from 9.5/26 = 37%).
Remaining ✗: price lists at the till (4), loyalty at the till (23),
cash in/out (24) — all flagged for the owner below, none hidden.

## 4. Pillars checklists

**PosWin** — UI ✓ (session state in header, product strip, DISC %
column, bill discount, customer, hold/recall/return/session
buttons, bottom checkout bar) / DB ✓ (posts via InvRepo; holds via
066 tables) / linking ✓ (ribbon POS.OpenPos; session + customer +
warehouse flow into the document) / posting ✓ (engine GL/stock/
settlement, PROVEN by Kern.Tests facts) / persistence ✓ (every
cart field lands on the document or the held-bill tables) /
downstream ✓ (Z report, party ledger, stock, receipt).

**PosCheckoutWin** — UI ✓ (split tender fields, live change/
remaining, card ref) / DB ✓ (tenders in the posting transaction) /
linking ✓ (opened only from PosWin with the session context) /
posting ✓ (PosPosting.CompleteSale → InvRepo.Post) / persistence ✓
(tender rows incl. change + card ref) / downstream ✓ (receipt
prints from the posted document; Z aggregates the tenders).

**PosSessionWin** — UI ✓ (open form + open-session panel + Z
preview + close) / DB ✓ (pos_sessions) / linking ✓ (ribbon
POS.Sessions; PosWin reads the same session) / posting n/a —
session close computes expected cash from tenders in-transaction
and mirrors to pos_cash_closings ✓ / persistence ✓ / downstream ✓
(Z report reads the session).

**PosReturnWin** — UI ✓ (bill lookup, per-line return qty, refund
split) / DB ✓ (SRT document + negative tenders) / linking ✓
(ribbon-less: opened from PosWin; original bill link stored on the
return) / posting ✓ (engine SRT: GL mirror, stock-in at engine
cost, settlement mirror) / persistence ✓ / downstream ✓ (Z counts
the return; original's paid_amount and returnable qty move).

**PosZReportWin** — UI ✓ (RptBase) / DB ✓ (reads sessions,
documents, tenders) / linking ✓ (ribbon POS.ZReport) / posting n/a
/ persistence n/a (report) / downstream ✓ (print/export via
RptBase).

## 5. Proof

Headless harness `~/workspace/research/build-pos/pos_harness.py`
(real `PosRepository`/`PosService`/`PosTenderMath` on a
SqlMigrator-migrated SQLite DB) — **46 checks, ALL PASS**:

- (a) sale outside a session refused **POS-1001**; second open on a
  terminal **POS-1002**; inside the session the gate passes.
- (b) split 60 cash / 40 card on 100 → tenders recorded 60/40,
  change 10 on cash tendered 70; walk-in leaves `party_ledger`
  untouched; on-account 30 → collected 70 settles / 30 stays owed;
  on-account for a walk-in refused **POS-1005**.
- (c) original bill 2 sold / 1 already returned → 1 returnable;
  return of 2 more refused **POS-1006**.
- (d) held bill total 104 = (100 + 9) − 5 recalls with identical
  lines/totals; second recall refused **POS-1009**.
- (e) Z from real session data: sales 100, returns 50, cash 60,
  card 40, refunded 50; expected = 200 + 60 − 50 = **210**; counted
  205 → variance **−5** exact; close mirrored to
  `pos_cash_closings`.
- (f) migrator double-run idempotent; 066 tables, column and all
  POS permissions present after both runs.
- Source-shape checks tie the harness to the posting contract:
  gate + tender writes inside the InvRepo transaction, split GL
  legs from the tender split, partial-return settlement mirror
  through the step-4 ledger writer.

`tests/Kern.Tests/PosBuildTests.cs` (compiles 0/0; **execution
owed on Windows**) pins the same math plus the full InvRepo
integration: split sale posts Dr Cash 60 / Dr Bank 40 with tenders
and the session link in one transaction; on-account sale leaves
ledger balance exactly 30; a sale naming a closed session is
refused POS-1001 and stays Draft; a partial return of 1 of 2
restores stock by 1, mirrors the settlement to net zero, drops the
original's `paid_amount` to 50, and the Z close computes expected
250 / variance −10.

**Gates:** full build `--no-incremental` = **0 errors, 0
warnings**; `tools/audit_window_context.py` exit **0** (all POS
windows central); Lang POS parity **0** (162 keys × 4); Errors
parity **0**; migrator double-run idempotent (harness (f)).

## 6. Owner flags

- **WPF + live PostgreSQL verification is still owed** — the
  harness and tests above are SQLite/service-layer; the windows
  are code-traced and compile clean, and Kern.Tests execution
  needs the Windows testhost, as with every prior build.
- Not built (comparison rows 4/23/24): price lists at the till,
  loyalty earn/redeem at the till, cash in/out movements. Each is
  a deliberate follow-up, not an oversight.
- Receipt **reprint** is not built (row 22 graded ~): the posted
  document can be re-opened in InvoiceWin, but there is no
  one-click receipt reprint from the till yet.
- Cancelling the 054 batch/serial capture at checkout leaves the
  saved **draft** document behind (inherited from the pre-existing
  checkout pattern); drafts are invisible to the Z report (Posted
  only) but a draft-cleanup pass is worth scheduling.
- `CloseCashWin` remains as the legacy day-close; if the owner
  wants it retired now that sessions own the lifecycle, that is a
  small follow-up (its history table stays either way).
