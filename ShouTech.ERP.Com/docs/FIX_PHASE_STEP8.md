# FIX PHASE — STEP 8: Cheques + Bank Reconciliation

**Branch:** `fix/step8-cheques-bankrec` (worktree `~/workspace/projects/ShouTech-fix8`)
**Base:** tip of `fix/step7-base-currency` (`d6c95db`) — the chain carries Steps 1–7 (RBAC, COA unification, GlPosting, party unification, InvRepo correctness, valuation engine, dual base-currency amounts).
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/cheques-bankrec.md`, `~/workspace/research/sweep/compare-cheques-bankrec.md`, `~/workspace/research/acceptance-sweep-report.md` (§7 cheques & bank rec).

---

## 1. The defects, in one paragraph

Issuing a cheque from the ribbon recorded a **receipt**: both cheque keys opened `ChqWin` in its Incoming default, so an outgoing cheque posted Dr Box / Cr Party — the exact inverse GL, silently (CHQ-01). A saved batch shared **one** journal entry, so bouncing or cancelling any single cheque reversed the whole batch's GL and permanently blocked its siblings (CHQ-02), and lifecycle GL/status writes were non-atomic, so a retry could double-post (CHQ-03). Outgoing clearance posted no GL at all (CHQ-04). BankRecWin's "Difference" was identically 0.00 **by construction** — no bank statement existed anywhere in the model to differ from the books (BNK-01). And the bank-account master existed twice: the live `banks` table (031) and the dead `bank_accounts` table (050), written only by caller-less code yet still offered as a DatWin export dataset (BNKC-01).

## 2. Direction inversion (CHQ-01) — routing + GL legs

`RptNav.TryHandle` now routes the keys separately:

| Ribbon key | Before | Now |
|---|---|---|
| `Bonds.ReceiptCheque` ("Incoming check") | `new ChqWin()` = Incoming | `OpenIncomingChequeVoucher` → `ChqWin(ChqKind.Incoming)` |
| `Bonds.PaymentCheque` ("Outgoing check") | `new ChqWin()` = Incoming (**wrong**) | `OpenOutgoingChequeVoucher` → `ChqWin(ChqKind.Outgoing)` |
| `Accounts.ChequeVoucher` / `Vouchers.ChequeTitle` (generic) | Incoming | Incoming (unchanged default) |

The generic `RptNav.Show` dedupes by window **type** only — the router mode-stickiness defect — so a visible Incoming window would have been re-activated for an Outgoing request. Cheques therefore open through a dedicated `ShowCheque(kind, owner)` that dedupes on **(type, kind)**: a visible window of the other kind is left alone and a new window of the requested kind opens.

GL legs per direction (through Step 3's GlPosting, Step 7's FX — §3), proven in the harness:

| Event | Incoming | Outgoing |
|---|---|---|
| Issue / receive | Dr Portfolio/Box, Cr Party (per cheque) | Dr Party, Cr Portfolio/Box (per cheque) |
| Clear | Dr Deposit Bank, Cr Portfolio | Dr Portfolio, **Cr Paying Bank** (new — CHQ-04) |
| Bounce / Cancel | Reverse own issue entry (or compensate, §3) | Reverse own issue entry (or compensate, §3) |

ChqWin's lifecycle buttons are now kind-gated (CHQ-08): Deposit/Endorse/Bounce are disabled for Outgoing, Cancel for Incoming.

## 3. Journal-per-cheque design (CHQ-02, CHQ-03, CHQ-04, CHQ-05, CHQ-07)

**One journal entry per cheque per event.** `ChequeService` no longer builds an aggregated batch entry through `SaveDraftAsync`/`PostAsync`; every event posts through `IJournalRepository.PostDocumentAsync` — Step 3's GlPosting engine — with the migration-060 source link pointing at the cheque itself:

| Source type | Event | Legs (transaction amounts; base derived per Step 7) |
|---|---|---|
| `CHQ-ISSUE` | Batch save, per cheque | IN: Dr Portfolio / Cr Party · OUT: Dr Party / Cr Portfolio |
| `CHQ-CLEAR` | Clear | IN: Dr Deposit Bank / Cr Portfolio · OUT: Dr Portfolio / Cr Paying Bank |
| `CHQ-BOUNCE` | Bounce compensation (shared legacy entries only, below) | Dr Party / Cr Portfolio |
| `CHQ-ENDORSE` | Endorse | Dr Endorsed-To / Cr Portfolio |
| `CHQ-CANCEL` | Cancel compensation (shared legacy entries only) | Dr Portfolio / Cr Party |
| `CHQ-FEE` | Bounce fees > 0 (CHQ-07: fees are now **posted**, not just recorded) | Dr Party / Cr Deposit Bank (or Portfolio when not deposited) |

Each entry carries the cheque's own currency + exchange rate; GlPosting/FxRates resolve and store the dual base amounts (migration 062) exactly as for every other document — no pre-multiplication, no rate-1 fabrication. The GlPosting duplicate guard (one live entry per source type + cheque id) makes every event idempotent per cheque. `cheques.journal_entry_id` now stores **that cheque's own** `CHQ-ISSUE` entry.

**Atomicity (CHQ-03).** A lifecycle transition first *claims* its new status through the repository's compare-and-swap update, then posts its GL; if the GL step fails, the claim is rolled back with a compensating CAS update (status and event fields restored) before the error propagates — a retry can never double-post, and a cheque is never stranded in a terminal status without its GL. If the rollback itself fails, the service throws an explicit "stranded — reconcile manually" error rather than swallowing either failure. Batch save posts each cheque's entry, then inserts the batch; any failure reverses every entry posted so far (the pre-existing compensation pattern, now per-cheque).

**Historical batch entries — honest treatment.** Pre-Step-8 cheques may still share one batch entry. That entry **cannot be split honestly** — its lines are aggregated totals with no per-cheque identity — so it is never fabricated into splits and never reversed for a single cheque. Instead, at bounce/cancel time the service counts the live cheques referencing the entry (`IChequeRepository.CountByJournalEntryAsync`):

- count ≤ 1 → the entry is effectively the cheque's own: reverse it outright;
- count > 1 → post a **per-cheque compensating entry** (`CHQ-BOUNCE` / `CHQ-CANCEL`) with the mirror legs for exactly that cheque's amount, leaving the shared entry posted and every sibling's GL exact. Each sibling later receives its own compensation the same way. The shared entry remains in the GL as the historical batch record; the sum of compensations plus the remaining live cheques always equals it.

**Also closed in this design:** the header voucher number is persisted on every cheque as `batch_doc_no` (migration 063; CHQ-05 — it was displayed but stored nowhere); outgoing cancellation records a stop-payment note (`cancel_reason`, 063) collected in both windows; the register gains the missing **Cancel** action; ChqListWin gains Branch / Currency / Issue-Date columns (CHQ-15) and its due-soon filter excludes terminal cheques (CHQ-16); the 1,000-row caps on the cheque register (CHQ-13) and the bank-rec book lines (BNK-03) are removed (standing unlimited-rows rule); ChqListWin and BankRecWin failures now report through the central mechanism with catalog code APP-2010 (CHQ-18 / BNK-07), following ChqWin's existing pattern.

**Not changed:** Step 3's account-mapping philosophy (the portfolio/box account remains the user-chosen account recorded on the cheque — no new designated under-collection/under-payment accounts were invented) and Step 7's FX conventions (consumed, including its F-1 stored-rate-1 interpretation for window-typed rates). POS/card payments are untouched — they are not cheques.

## 4. Real BankRec statement model (BNK-01, BNK-02)

**Migration 063** (both providers, idempotent) introduces the statement side as data:

- `bank_statements` — one statement per bank account: `account_code`, `statement_date`, `statement_ref`, `period_from/to`, `opening_balance`, **`closing_balance` (the balance as printed on the statement — bank data, independent of the books)**, `status` Open/Closed.
- `bank_statement_lines` — the statement's own movements (date, reference, description, signed amount: + into the account / − out of it), each optionally matched to one book line (`matched_journal_line_key`).
- `bank_reconciliation_marks` gains `statement_id` / `statement_line_id`, so a cleared mark records *which statement line* cleared the book line.

**The Difference is now a real quantity:**

```
Difference = closing_balance − opening_balance − Σ matched book lines
```

It is non-zero whenever the bank statement disagrees with the books, moves by exactly each matched amount as matching proceeds, and is legitimately zero only when the entire statement movement is matched. The old formula (`Book − Cleared − Uncleared ≡ 0`) is deleted, and the unit test that enshrined it now documents why. `OperationsService.ComputeTotals(bookLines, statement, statementLines)` computes both sides, including matched/unmatched statement totals; the window footer shows Book / Cleared / Uncleared / Statement closing / Difference plus unmatched counts on **both** sides.

**Matching rules:** one statement line ↔ one book line, **equal amounts only** — a mismatched or partial match would hide exactly the discrepancy the Difference exists to surface; a genuinely different bank amount stays unmatched until the book side is corrected or the missing entry (charges, interest) is posted and then matched. Matching and unmatching are single-transaction operations (statement-line link + mark upsert/delete together). A statement **closes only at Difference = 0** — otherwise `CloseBankStatementAsync` fails with the new catalog code **ACC-5016**; matching an already-matched line fails with **ACC-5017**. Closed statements are immutable (line add/remove/match/unmatch all refuse). The legacy bare Mark/Unmark (no statement line) remains available for ad-hoc clearing.

**Window (BankRecWin):** statement header fields (account, statement date/ref, opening + closing balances, Save Statement), a statement-lines grid with an entry row (date / ref / description / amount, Add / Remove), Match / Unmatch / Close Statement in the bottom command bar, and both grids visible together so unmatched items show on both sides. Statement lines are **entered** in the window; file import (CSV/OFX/CAMT) is **not** built — see flag F-1.

## 5. Banks / bank_accounts resolution (BNKC-01)

**Decision: `banks` (migration 031) is the one canonical bank-account store.** It is what BnkCard / `MasterCardService` / `MasterCardRepository` read and write, what BankRecWin's account picker reads (`banks.gl_account_code` first), and what migration 063 backfills. Following the Steps 2/4 pattern — one writer, one reader path, no silent dual-write:

- Migration 063 **copies** any live `bank_accounts` rows into `banks` (keyed by company + code, only where the code is not already present; the copy SQL is itself idempotent). In practice the table is empty everywhere: its only writers were `CardStore.LoadBank/SaveBank/SoftDeleteBank`, which had **zero callers** in the entire codebase.
- The CardStore bank path (`BankCardRec`, `LoadBank`, `SaveBank`, `SoftDeleteBank`, `BankBookBalance`) is **deleted**, with a comment recording the decision where it stood.
- DatWin's export catalogue no longer offers the always-empty **"BankAccounts"** dataset; bank accounts export through the canonical **"Banks"** dataset.
- The `bank_accounts` table itself is left in place as a legacy artifact (dropping it would break STEI packages that still carry it); no code reads or writes it.

BNKC-02 (bank balance reading only the canonical chart) was already resolved by Step 2's COA unification — `chart_of_accounts` is the only chart. BnkCard's remaining sweep items (delete reference guard BNKC-03, SWIFT/BIC, bank master split) are **not** in this step's mandate and are listed under flags.

## 6. Migration 063 — `063_cheques_bankrec.sql` (SQLite + PostgreSQL)

- `cheques`: `batch_doc_no`, `cancel_reason` (guarded column adds).
- `bank_statements`, `bank_statement_lines` (new tables + indexes).
- `bank_reconciliation_marks`: `statement_id`, `statement_line_id` (guarded).
- Canonical-banks copy from `bank_accounts` (§5).
- Idempotent on both providers (SQLite `@if-column-missing` directives / PG `ADD COLUMN IF NOT EXISTS`; tables `IF NOT EXISTS`; copy inserts only missing codes). Migrator double-run verified (§8).

## 7. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| CHQ-01 ★ | "Outgoing check" ribbon button opens an Incoming window; inverse GL posted silently | §2: keys split, outgoing constructs `ChqWin(ChqKind.Outgoing)`, kind-aware dedupe; GL legs per direction verified | Harness (a) source checks + outgoing issue/clear legs; Kern.Tests `Outgoing_IssueThenClear_…` |
| CHQ-02 ★ | Bouncing/cancelling ONE cheque reverses the WHOLE batch journal | §3: per-cheque entries; bounce/cancel reverse only the cheque's own entry; shared legacy entries compensated per-cheque, never reversed | Harness (b) + legacy case; Kern.Tests `BatchOfThree_…`, `LegacySharedBatchEntry_…` |
| CHQ-03 ★ | Lifecycle GL + status not atomic; retry double-posts / strands | §3: status-claim → GL → compensating rollback saga; GlPosting duplicate guard per (event, cheque) | Code + harness happy paths; fault-injection owed on Windows (flag F-4) |
| CHQ-04 | Outgoing clearance has no GL; box credited at issue with no bank leg | OUT Clear posts Dr Portfolio / Cr Paying Bank as the cheque's own `CHQ-CLEAR` entry; both windows collect the paying bank for OUT | Harness (a); Kern.Tests `Outgoing_IssueThenClear_…` |
| CHQ-05 | Header Voucher No is a dead field | Persisted as `cheques.batch_doc_no` (063) on every row of the batch | Migration + repository round-trip (harness schema checks; Kern fixture columns) |
| CHQ-07 | Bounce fees recorded, never posted | Fees > 0 post a `CHQ-FEE` entry Dr Party / Cr Bank-or-Portfolio in the bounce operation | Harness (b) exact balances incl. fee; Kern.Tests `BatchOfThree_…` |
| CHQ-08 | Lifecycle buttons not kind/status gated | ChqWin disables Deposit/Bounce/Endorse for Outgoing and Cancel for Incoming | Code (window diff) |
| CHQ-13 | Register capped at 1,000 cheques; totals over the truncated set | `LIMIT` removed from `ChequeRepository.GetAllAsync` | Code |
| CHQ-15 | Register omits Branch / Currency / Issue Date | Columns added to ChqListWin grid + row wrapper | Code (window diff) |
| CHQ-16 | Due-soon includes terminal cheques | Filter excludes Cleared/Bounced/Endorsed/Cancelled | Code (window diff) |
| CHQ-18 / BNK-07 | Register / BankRec errors bypass the central mechanism | Both windows report via APP-2010 + `AppHost.Logging` (ChqWin pattern) | Code (window diffs) |
| BNK-01 ★ | "Difference" ≡ 0.00 by construction; no statement balance exists | §4: statement model (063) + real Difference formula; close gated at zero (ACC-5016) | Harness (c) full lifecycle; Kern.Tests `BankRec_StatementDifference_…` |
| BNK-02 | No statement-side data, no matching, no partial/adjustment path | Statement lines entered + equal-amount matching/unmatching in-window; adjustment entries are posted as journals and then matched (the harness's 50-interest flow). Partial clearing remains unbuilt — flag F-2 | Harness (c); code |
| BNK-03 | Journal lines capped at 1,000; totals wrong beyond it | `LIMIT` removed from `OperationsRepository.GetBankJournalLinesAsync` | Code |
| BNKC-01 | Two competing bank tables; dead one leaks into exports | §5: `banks` canonical; 063 copy; CardStore path deleted; DatWin dataset removed | Harness banks-copy + source checks |

Out of scope and untouched, as mandated: CHQ-06 (source-voucher/invoice allocation link on cheques — the PayRec seam; a bounce still cannot re-open a settled invoice), CHQ-09/10/11/12 (grid parse behavior, drawer/beneficiary split, rate UI, print pagination), CHQ-17 (register prompt dialog styling), BNK-04/05/06, BNKC-03/04 — recorded as flags where relevant.

## 8. Proof

### 8.1 Headless harness — `~/workspace/research/fix-step8/step8_harness.py` — **18/18 PASS** (incl. 21 C# scenario checks)

Runs the **real SqlMigrator** and the **real** `ChequeService` / `JournalRepository` (GlPosting) / `OperationsService` (C# harness referencing the fix8 Infrastructure + Application projects) against migrated SQLite databases:

- **(e)** Migration 063 applies on a fresh DB; migrator double-run idempotent; statement tables + new columns present. Withholding 063, seeding a legacy `bank_accounts` row, then applying 063 copies it into `banks`; re-running the copy SQL does not duplicate it.
- **(a)** Source: `Bonds.ReceiptCheque` → incoming opener, `Bonds.PaymentCheque` → outgoing opener constructing `ChqWin(ChqKind.Outgoing)` with kind-aware dedupe. Runtime legs: outgoing issue Dr Party 400 / Cr Box 400 (the inverse of incoming); outgoing clear posts `CHQ-CLEAR` and credits the bank 400.
- **(b)** Batch of 3 (100/200/300): three **distinct** per-cheque entries; GL Dr Portfolio 600 / Cr Party 600. Bounce the 200 cheque with a 10 fee → exactly its entry reversed, siblings still `InPortfolio` and posted, fee posted as `CHQ-FEE`, balances exact (Portfolio Dr 390 / Party Cr 390); the second sibling then bounces independently (the old code threw forever). Legacy case: two cheques sharing one posted batch entry — bouncing one leaves the shared entry **posted**, posts a `CHQ-BOUNCE` compensation of exactly 200, sibling untouched.
- **(c)** BankRec: statement (opening −400 framing the period, closing 650; lines 600/400/50) → Difference **1050** unmatched; match 600 → **450**; match 400 → exactly the known **50** discrepancy; close refused with **ACC-5016**; post the missing 50 book entry, match it → Difference **0** legitimately, no unmatched statement lines; statement closes.
- **(d)** After every lifecycle above, the trial balance over all ever-posted entries (posted + reversed, so reversal pairs net to zero — the Step-3 harness's own definition) nets to exactly 0 across all accounts, and every per-account figure asserted in (a)/(b) is the register-consistent GL figure. Cheques settle through the GL party **control** accounts (they write no `party_ledger` rows — that ledger belongs to the Step-4 document/voucher settlement chain); the reconciliation proven here is register ↔ GL.

### 8.2 Kern.Tests — `tests/Kern.Tests/ChequeBankRecStep8Tests.cs` (5 facts)

Real C# paths against real migrated SQLite databases: batch-of-three bounce isolation with fees and exact balances; outgoing issue + clear bank credit; legacy shared-entry compensation; the full BankRec difference lifecycle incl. ACC-5016 and legitimate-zero close; migration-063 double-run idempotence. Existing fixtures were updated where Step 8 changed the world they simulate: `ChequeMfgServiceTests` (fake journal gains document postings; assertions moved to per-cheque entries; fixture gains the 063 columns) and `OperationsServiceTests` (the tautological `Difference == 0` assertion removed with a pointer here). **Execution is owed on Windows** (net8.0-windows testhost cannot run on this Linux VM) — the project compiles at 0 errors / 0 warnings here.

### 8.3 What is NOT proven here

WPF runtime behavior (ribbon clicks opening the correct cheque kind, BankRecWin matching gestures, ChqListWin Cancel prompt) and **live PostgreSQL** execution of migration 063 and the new queries are owed on Windows / a live PG instance — no PG server exists in this VM. The PG migration mirrors the SQLite logic with standard idempotent constructs but has not been executed. Build + SQLite harness success is not product readiness, per the standing rule.

## 9. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr, Lang + Errors catalogs) | **0 missing** in every language (18 new Lang keys + 2 new error codes, all ×4) |
| Migrator idempotent double-run (SQLite) | PASS (harness (e) + Kern.Tests) |
| Arabic outside Ar.json | None added (Arabic only in the Ar Lang/Errors catalog entries) |
| Headless harness | **18/18 PASS** (§8.1) |

## 10. Comparison tables (vs Al-Ameen V10 / Phenix v10 / ERPNext / Odoo)

Condensed from `~/workspace/research/sweep/compare-cheques-bankrec.md`, with the **Step 8** column recording exactly what this step changed. ⚠ = counterpart behavior asserted from the regional pattern in the sweep, not live-verified.

### 10.1 ChqWin (CORE ≈50% before)

| Capability | Before | Al-Ameen ⚠ / Phenix ⚠ | ERPNext / Odoo | **Step 8** |
|---|---|---|---|---|
| Outgoing document reachable from the ribbon | ✗ both keys opened Incoming | ✓ separate documents | ✓ Receive/Pay types | **✓** — PaymentCheque opens Outgoing; kind-aware dedupe |
| Per-cheque GL reversal (bounce/cancel one of a batch) | ✗ whole batch reversed | ✓ per-cheque | ✓ per payment | **✓** — one entry per cheque per event |
| Outgoing clearance GL (bank debited) | ✗ status only | ✓ Dr Under-Payment / Cr Bank | ✓ | **✓** — Dr Portfolio / Cr Paying Bank (portfolio account stands in for the under-payment leg; no new designated accounts invented) |
| Bounce fees posted | ✗ recorded only | ✓ Dr Party / Cr Bank-or-income | n/a core | **✓** — `CHQ-FEE` entry in the bounce operation |
| Saved voucher number | ✗ displayed, never stored | ✓ | ✓ | **✓** — `batch_doc_no` persisted per cheque |
| Stop-payment note on cancel | ✗ no input | ✓ | ✓ (void reason) | **✓** — `cancel_reason` collected in both windows |
| Party master lookup, invoice allocation link, cheque books, cheque-paper print, cost-center column, rate UI | ✗ | ✓ | ◐ | GAP — unchanged (CHQ-06/10/11 and §6 parity list; not in this step's mandate) |

### 10.2 ChqListWin (CORE ≈44% before)

| Capability | Before | Counterparts | **Step 8** |
|---|---|---|---|
| Cancel / stop issued cheque from the register | ✗ button absent | ✓ | **✓** — Cancel action with stop-note prompt |
| Branch / Currency / Issue Date columns | ✗ stored, not shown | ✓ | **✓** |
| Row cap | ✗ 1,000, totals over truncated set | unlimited/paged honestly | **✓** — cap removed |
| Due-soon excludes terminal cheques | ✗ | ✓ | **✓** |
| Errors via central catalog | ✗ raw MessageBox | — | **✓** — APP-2010 pattern |
| Search, date-range filter, batch deposit, per-currency totals, multi-page print, export | ✗ | ✓ | GAP — unchanged |

### 10.3 BankRecWin (CORE ≈20% before)

| Capability | Before | ERPNext / Odoo / SAP B1 | **Step 8** |
|---|---|---|---|
| Statement (closing) balance as data | ✗ no field existed | ✓ | **✓** — `bank_statements` (063) |
| Statement lines as data (manual entry) | ✗ no object existed | ✓ | **✓** — entered in-window; **import (CSV/OFX/CAMT) still GAP (F-1)** |
| Difference that can be non-zero | ✗ ≡ 0.00 by construction | ✓ must be 0 to validate | **✓** — closing − opening − matched; close gated at 0 (ACC-5016) |
| Two-sided manual match | ✗ checkbox on book line only | ✓ core gesture | **✓** — statement line ↔ book line, equal amounts, single transaction |
| Unmatched items on both sides | ✗ | ✓ | **✓** — both grids + unmatched counts/totals |
| Close / lock a statement | ✗ nothing ever closed | ✓ validate/finalize | **✓** — Closed statements immutable |
| Auto-match, split/merge, partial clearing, create-adjustment-from-rec, print/export rec report | ✗ | ✓ | GAP — adjustments are posted as journals then matched (proven flow); the rest unchanged (F-2) |

### 10.4 BnkCard (CORE ≈61% before)

| Capability | Before | Counterparts | **Step 8** |
|---|---|---|---|
| One canonical bank-account store | ✗ `banks` live + `bank_accounts` dead, both exported | ✓ one master | **✓** — `banks` only; legacy rows copied by 063; dead path + dataset deleted |
| Card fields / persistence / live GL balance | ✓ (cleanest card in the sweep) | ✓ | Unchanged |
| SWIFT/BIC, bank (institution) master split, account type, opening balance, default flag, delete reference guard, bank list window | ✗ | ✓ | GAP — unchanged (F-3) |

## 11. Pillars checklists (per touched window)

Legend: PROVEN = evidence in §8; GAP = explicitly not done.

### ChqWin
- **UI:** PROVEN (code) — kind-gated lifecycle buttons; header voucher number now meaningful. Runtime click-through owed on Windows (§8.3).
- **DB:** PROVEN — per-cheque rows carry own `journal_entry_id` + `batch_doc_no` (063); migrator double-run idempotent.
- **Linking:** PROVEN — every GL entry links back to its cheque (060 source columns, source = cheque id); ribbon routing fixed incl. kind-aware dedupe.
- **Posting:** PROVEN — per-direction legs (§2 table) through GlPosting with Step-7 base amounts; harness (a)/(b).
- **Persistence:** PROVEN — batch doc no + cancel reason round-trip through the repository; all prior fields unchanged.
- **Downstream:** PROVEN at data level — cheque entries are ordinary posted journals, so TB/BS/statement include them per-account exactly (harness (d)). GAP: live WPF report rendering owed on Windows.

### ChqListWin
- **UI:** PROVEN (code) — Cancel action added; Branch/Currency/Issue-Date columns; due-soon excludes terminal. **DB/Linking/Posting:** PROVEN — all actions delegate to the same ChequeService (per-cheque GL, §3). **Persistence:** PROVEN — register reads the uncapped repository query. **Downstream:** GAP — print remains the single-page register list; per-currency footer totals and export unchanged.

### BankRecWin
- **UI:** PROVEN (code) — statement header (opening/closing), statement-lines grid + entry row, Match/Unmatch/Close in the bottom bar; unmatched shown on both sides. Runtime gestures owed on Windows.
- **DB:** PROVEN — statements/lines/marks-with-link persist (063); repository round-trips in harness (c) + Kern.Tests.
- **Linking:** PROVEN — mark ↔ statement line ↔ book line chain is stored and reloadable; cheque clearing does **not** auto-mark here ( GAP — the sweep's cheque↔rec tie-in remains unbuilt, F-2).
- **Posting:** n/a (reconciliation posts nothing; adjustment journals are posted through the journal engine and then matched — proven flow).
- **Persistence:** PROVEN — every statement field and line round-trips; closed statements immutable.
- **Downstream:** GAP — no print/export of the reconciliation report (BNK-06, unchanged).

### BnkCard
- **UI:** unchanged. **DB:** PROVEN — the card's store (`banks`) is now the *only* bank store; 063 copies legacy rows in. **Linking:** PROVEN — BankRecWin's picker and the card read the same store. **Posting:** n/a. **Persistence:** PROVEN (pre-existing, re-verified by the copy harness). **Downstream:** PROVEN — DatWin exports bank accounts only via the canonical "Banks" dataset. GAP: delete reference guard (BNKC-03) unchanged.

## 12. Owner flags

- **F-1 — Statement file import NOT built (STOP/flag, per mandate).** The statement model + entered-lines matching are complete, but CSV/OFX/CAMT.053/MT940 import is a separate surface (format parsers, per-bank format settings, import preview/mapping) and was not ballooned into this step. BankRecWin works fully on entered lines today.
- **F-2 — BankRec depth gaps remain:** auto-match proposals, split/merge and partial clearing, create-adjustment-journal *from inside* the window (today: post the journal in JrnWin, then match it — the proven flow), print/export of the reconciliation report, and the cheque↔rec tie-in (clearing a cheque in ChqWin does not mark its line here). None blocks a correct manual reconciliation.
- **F-3 — BnkCard backlog unchanged:** SWIFT/BIC, a separate Bank (institution) master, account type, opening balance/date, Is-Default flag, bank list/search window, delete reference guard (BNKC-03). The store decision (§5) makes these safe to add later on the one canonical table.
- **F-4 — CHQ-03 fault-injection is code-proven, not runtime-proven.** The claim/rollback saga is exercised on happy paths by the harness; a mid-flight GL failure (e.g. period closed between claim and post) returning the cheque to its prior status should be confirmed on Windows, where the real period/fiscal fixtures exist.
- **F-5 — Reversal reporting observation (pre-existing, NOT cheque-specific; discovered while proving (d)).** GL reports (Trial Balance family, `RptQuery`) sum lines of entries with `status = 1` only. A reversal pair therefore contributes **only the reversal entry** to those reports: post-then-reverse nets to *minus* the original per account in the TB, not zero — for **every** reversal in the system (JrnWin, cash vouchers, manufacturing), not just cheques. The Step-3 harness's own net-zero check summed posted + reversed entries (the definition this step's proof (d) uses). Whether reports should include status-2 originals (pairs net to zero) is a reporting-semantics decision for the owner; changing it here would have altered Steps 2/3/7 verified behavior, so it is flagged, not changed.
- **F-6 — CHQ-06 seam remains:** cheques still carry no source-voucher/invoice allocation, so a bounce cannot re-open a settled invoice's outstanding balance. The per-cheque GL is now correct; the allocation link is the PayRec seam's next step.

## 13. Files changed

- `dba/mig/sqlite/063_cheques_bankrec.sql`, `dba/mig/postgresql/063_cheques_bankrec.sql` (new)
- `src/ShouTech.Domain/Entities/Cheque.cs` (BatchDocNo, CancelReason, ChequeSourceTypes), `Entities/Operations.cs` (BankStatement, BankStatementLine, mark statement link, real totals), `ErrorCodes.cs` (ACC-5016/5017), `Interfaces/IJournalRepository.cs` (PostDocumentAsync), `Interfaces/IChequeRepository.cs` (CountByJournalEntryAsync), `Interfaces/IOperationsRepository.cs` (statement contract)
- `src/ShouTech.Application/Services/ChequeService.cs` (per-cheque posting rewrite), `OperationsService.cs` (statement workflow + real ComputeTotals)
- `src/ShouTech.Infrastructure/ChequeRepository.cs` (063 columns, uncapped list, sharing count), `OperationsRepository.cs` (statement CRUD/matching, uncapped book lines)
- `cod/app/src/Services/RptNav.cs` (direction routing + kind-aware dedupe), `Services/CardStore.cs` (dead bank path removed), `Services/DataExportSvc.cs` (BankAccounts dataset removed), `Views/ChqWin.cs` (batch doc no, OUT clear bank, cancel note, kind gating), `Views/ChqListWin.cs` (Cancel action, columns, due-soon, central errors), `Views/BankRecWin.cs` (statement UI + matching)
- Lang catalogs ×4 (18 keys), Errors catalogs ×4 (ACC-5016/5017), `docs/ERROR_CATALOG.md`
- `tests/Kern.Tests/ChequeBankRecStep8Tests.cs` (new, 5 facts); fixture/double updates in `ChequeMfgServiceTests.cs`, `OperationsServiceTests.cs`, `UnitTest1.cs`
- `docs/FIX_PHASE_STEP8.md` (this file)
