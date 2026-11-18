# FIX PHASE — STEP 7: Base-Currency Amounts on Every Money Line

**Branch:** `fix/step7-base-currency` (worktree `~/workspace/projects/ShouTech-fix7`)
**Base:** tip of `fix/foundations-6-valuation` (`11d82b6`) — the foundations chain is sequential (steps 1–6).
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/journal-coa.md` (J-5),
`~/workspace/research/sweep/invoicing-posting.md` (multi-currency MAJOR),
`~/workspace/research/sweep/cash-payments.md` (B1 cash BLOCKER, cheque BLOCKER, PayRec cross-currency MAJOR),
`~/workspace/research/acceptance-sweep-report.md`.

---

## 1. The defect, in one paragraph

Multi-currency was decorative (J-5). Journal headers stored `currency_code` +
`exchange_rate`, but `journal_entry_lines` had **no base-currency amounts**, no
report converted by rate, and the FX master table (`currency_rates`) was never
applied to any money anywhere. A USD entry at rate 3.75 posted and reported as
if 1 unit = 1 base unit — wrong money in every total. Document lines feeding
the GL and `party_ledger` had the same disease: a foreign-currency invoice
debited the party at face value, and `PartyCreditGuard` compared that raw
foreign sum against a base-currency credit limit. Cheques pre-multiplied their
amounts by the rate and posted them under a header claiming rate 1, conflating
transaction and base amounts in the same columns.

## 2. Design: dual amounts + one rate-resolution path

**Every money line that feeds the GL or a party balance now carries BOTH
amounts** (migration 062, §3): the transaction amount (unchanged columns) and
its base-currency counterpart (`base_debit` / `base_credit` = transaction ×
effective rate, rounded to 4dp AwayFromZero), plus the **effective rate
actually applied** stored on the line itself, so the rate source is auditable
per line. Journal headers additionally record `base_currency_code` — the base
currency the amounts are expressed in.

**Rate-source precedence** — enforced by the ONE resolver,
`FxRates.Resolve` / `ResolveByCompanyCode` (`src/ShouTech.Infrastructure/FxRates.cs`):

| Priority | Source | Rule |
|---|---|---|
| 1 | Base currency | Transaction currency == company base currency (`companies.currency_code`) → rate **1** |
| 2 | Document/entry rate | A header/document `exchange_rate` > 0 stored on the document → that rate (explicit) |
| 3 | Master | Stored rate ≤ 0 (absent) → `currency_rates`: latest rate on or before the entry date for the company (the master is keyed by company **code**, the namespace CurCard maintains; the Guid form is also consulted for journal-side callers), latest overall as fallback |
| 4 | — | No rate anywhere → **posting fails with ACC-5015 `ExchangeRateMissing`** (new catalog code, §6), inside the posting transaction, so nothing is written |

Two enabling changes make priority 3 reachable: the document repositories no
longer **fabricate** a rate at insert — `CommercialDocumentRepository` and
`OrderDocumentRepository` stored `draft.ExchangeRate <= 0 ? 1 : rate`, which
converted "no rate entered" into a fake explicit rate of 1 and made the master
table unreachable. They now store the rate as given (0 = absent). The journal
header written by `GlPosting` / `JournalRepository` stores the **effective**
(post-resolution) rate, so header, lines, and reports can never disagree about
which rate was applied.

**Balance rule (mixed-rate definition).** An entry carries ONE effective rate
(the journal model has no per-line rates), so a transaction-balanced entry is
base-balanced after conversion; `FxRates.ConvertBalanced` absorbs the 4dp
rounding residue on the largest line so the base sums are *exactly* equal.
`JournalRepository.PostAsync` additionally **validates the stored base sums**
and refuses a base-imbalanced entry with ACC-5001 — an entry whose lines were
converted at mixed rates that do not net (the only way to construct one is
direct data manipulation or a legacy partial write) cannot post even though it
balances in transaction currency. Legacy drafts whose lines lack base amounts
are healed from the header rate inside the posting transaction (the same
conversion the 062 backfill applies).

**COGS treatment (Step 6 boundary).** Stock values are base-currency already
(the valuation engine's `cost_amount`). `InvRepo.BuildGlLines` therefore
converts the COGS leg **from base to transaction** (÷ effective rate) before
handing it to GlPosting, whose uniform conversion brings its base amount back
to exactly the engine's cost. At rate 1 nothing changes; the revenue /
receivable / payable legs are transaction amounts throughout, as before.
Step 3's account mapping and Step 5's stored-totals logic are untouched —
this step consumes the stored totals + the stored rate, exactly as mandated.

## 3. Migration 062 — `062_base_currency_amounts.sql` (SQLite + PostgreSQL)

- `journal_entry_lines` gains `base_debit`, `base_credit`, `exchange_rate`
  (the effective rate applied to that line).
- `journal_entries` gains `base_currency_code`.
- `party_ledger` gains `base_debit`, `base_credit`, `exchange_rate`.
- **Backfill (idempotent — only rows whose base amounts are still NULL):**
  journal lines convert at their **header rate** (1.0 when the entry currency
  is the company's base currency); header `base_currency_code` comes from
  `companies.currency_code`; party_ledger rows convert at, in order, the rate
  of the journal entry they settle (migration 061 link), their source
  sales/purchase document's rate, the `currency_rates` master rate on or
  before the entry date, else 1.0. Historical reports therefore stay
  consistent with the entries as posted.
- Position: numbered 062 in step order (053 was Step 6's; 059–061 used).
  Guarded on both providers (SQLite `@if-column-missing`, PG
  `ADD COLUMN IF NOT EXISTS`); migrator double-run verified (§7).

## 4. Per-surface treatment

| Surface | Treatment |
|---|---|
| `JournalRepository` (JrnWin / CshWin manual entries + cash vouchers) | SaveDraft/UpdateDraft resolve the rate once and write dual amounts per line; PostAsync validates base balance (ACC-5001) + heals missing base; the voucher settlement written at posting inherits the voucher's currency + header rate via `PartyLedger.WriteEntry(exchangeRate:)` |
| `GlPosting` (Step 3's document→GL engine) | Resolves the effective rate before writing anything (ACC-5015 fails the whole document transaction); header stores effective rate + base currency; lines written with dual amounts (residue-adjusted); reversal mirrors the **stored** base amounts swapped — never a recomputation at today's rate — so a reversed pair nets to zero in base exactly |
| `InvRepo.Post` (InvoiceWin / POS) | Resolves the rate once at the top of the posting transaction; the credit guard is fed the **base** exposure; the party_ledger row and the collected-at-posting settlement carry dual amounts at the same rate; COGS legs converted per §2; the GL request still carries the stored document rate and GlPosting resolves identically |
| `CommercialDocumentRepository.PostSalesAsync` (chain SAL) | Same: FxRates resolution, guard fed base total, ledger row via `PartyLedger.WriteEntry` with dual amounts; stored-rate fabrication removed at draft insert |
| `OrderDocumentRepository.ConvertToInvoiceAsync` | Same treatment for the converted invoice's guard + ledger + GL |
| `PartyLedger.WriteEntry` (the shared writer) | Computes dual amounts from explicit base overrides (reversal mirrors) or by resolving the caller's document rate through FxRates; every caller now passes its rate: InvRepo settlement + unsettle-mirror (stored base swapped), chain, order conversion, journal settlements, DefinitionRepository installment collections (SAR plans resolve their SAR rate) |
| `PartyCreditGuard` | Sums `base_debit/base_credit` (COALESCE fallback to transaction amounts for pre-062 rows); callers pass base totals — a foreign document can no longer slip under a base-currency limit at face value |
| `ChequeService` | A batch whose cheques share one currency + rate posts as a proper transaction-currency entry (header = batch currency + rate, lines = raw amounts, base derived by JournalRepository); single-cheque Clear/Endorse entries likewise. Mixed-currency/rate batches keep the legacy pre-converted shape — see flag F-2 |
| Reports (`RptQuery`) | §5 |

Untouched, as mandated: account mapping (Step 3), document totals logic
(Step 5 — stored totals consumed as-is), valuation (Step 6), pricing and
document entry UX (no window gains or loses a field — see §8 comparison gate).

## 5. Report changes (`cod/app/src/Services/RptQuery.cs`)

All GL reports now total in **base currency** — they sum
`COALESCE(base_debit, debit)` / `COALESCE(base_credit, credit)` (the fallback
keeps any row the backfill could not reach honest instead of invisible):

- **Trial Balance, Balance Sheet, P&L** (the mandated trio), plus the rest of
  the line-based GL family for consistency: Account Statement, Journal List,
  Cost Center Report + Cost Center Trial, Account Movement Chart, Account
  Comparison, Cash Journal, Cash Position, Cash Flow Sections, Dormant
  Accounts, Bank Balances / Bank Accounts GL.
- **Party-balance reports** sum party_ledger base amounts: Customer Balances,
  Supplier Balances, Party Aging, Customer Due, Collection Rates.
- **Exchange Rate Differences** now reads the **stored** base amounts
  (BaseDebit = Σ base_debit; Difference = base − transaction) instead of
  re-deriving them from the header rate at report time; **Average Currency
  Rate** weights by base debit.
- **Transaction-currency views keep working**: document-list reports (sales /
  purchase invoice lists, price history, targets, sales-by-party, POS cash)
  still show document amounts in the document's own currency, and journal
  lines keep their transaction `debit/credit` columns — the FX-differences
  report shows both side by side. Allocation matching in PayRec
  (ReconciliationRepository) stays transaction-currency (same-currency
  matching); see flag F-3.

`journal_line_cost_splits` remain transaction-currency: their amounts are
written by `GlPosting.ApplyCostCenters` from transaction line amounts, and no
report totals splits directly (Cost Center Trial reads the lines, now in
base). Flagged as F-3 for a future conversion if a splits-based report appears.

## 6. New error code

**ACC-5015 `ExchangeRateMissing`** — registered in `ErrorCodes.cs`, all four
error catalogs (En/Ar/Fr/Tr, parity 0), and `docs/ERROR_CATALOG.md`. Thrown by
`FxRates` when a foreign-currency posting has no document rate and no master
rate; the posting transaction rolls back, so the document/entry stays Draft
and no GL or ledger row exists (proven §7d). No new Lang keys: no window's
visible surface changed.

## 7. Proof

### 7.1 Headless harness — `~/workspace/research/fix-step7/fx_harness.py` — **22/22 PASS** (plus 13/13 C# scenario checks)

Runs the **real SqlMigrator** and the **real** `JournalRepository` /
`GlPosting` / `PartyLedger` / `FxRates` (C# harness referencing the fix7
Infrastructure project) against migrated SQLite databases:

- Migration 062 applies on a fresh DB; double-run idempotent; all new columns present.
- **(a) J-5 case:** USD journal @ 3.75 → base total **375** (transaction 100); header `base_currency_code = SAR`; TB-style base sum = 375.
- **(b) Foreign sales invoice** (invoice-shaped GlPosting + shared party-ledger writer): receivable base = 200 × 3.75 = **750**; **COGS base = 80** (the engine's base cost, not 300); party_ledger base_debit = 750 with transaction debit 200.
- **(c) Mixed currencies:** USD @ 3.75 and EUR @ 4.2 entries each balance in base; an entry corrupted to balance in transaction but not in base is **refused at posting (ACC-5001)**.
- **(d) Missing rate:** foreign document, stored rate 0, no master → **ACC-5015, nothing written**; after seeding a `currency_rates` master row, the same document posts at the master rate (base 375, header rate stored as 3.75).
- **(e) Base-currency document:** SAR @ 1 → base == transaction, identical to before.
- **(f) Backfill:** pre-062 fixture (062 withheld, then applied) — foreign journal lines gain 375 @ 3.75, base-currency lines 50 @ 1.0, header base currency filled, party_ledger doc row gains 750 @ document rate, plain SAR row 40 @ 1.0; double-run idempotent and values stable.
- Reversal of the (a) entry mirrors stored base amounts: net base per account = 0.
- Source checks: TB/party reports sum base amounts; the document repositories no longer fabricate rate 1 at insert; InvRepo resolves through FxRates.

### 7.2 Kern.Tests — `tests/Kern.Tests/BaseCurrencyTests.cs` (7 facts)

Real C# paths against real migrated SQLite databases (company base = SAR):
USD journal base amounts + header base currency; foreign sales invoice GL +
party ledger in base with COGS base intact; mixed-currency base balance +
ACC-5001 refusal; missing-rate invoice → ACC-5015 with document left Draft and
zero GL/ledger rows; master-rate-only invoice posts at the master rate;
base-currency invoice identity; legacy draft healed at posting.
**Execution is owed on Windows** (net8.0-windows testhost cannot run on this
Linux VM) — the project compiles at 0 errors / 0 warnings here.

### 7.3 What is NOT proven here

WPF runtime behavior (posting from InvoiceWin/JrnWin/CshWin/ChqWin) and **live
PostgreSQL** execution of migration 062 and the new queries are owed on
Windows / a live PG instance — no PG server exists in this VM. The PG
migration mirrors the SQLite logic with standard idempotent constructs but
has not been executed. Build + SQLite harness success is not product
readiness, per the standing rule.

## 8. Comparison gate

JrnWin / InvoiceWin **visible behavior does not change**: no new fields, no
new columns, no moved buttons — this step is storage/posting/report-basis
only, so their comparison tables are **skipped with justification** per the
mandate's own condition. What a user *sees* change is report **values**:
GL and party-balance reports now total in base currency (§5), which is the
mandated correction itself — a USD 100 receipt @ 3.75 previously added 100
to every total and now adds 375. Document lists still show document-currency
amounts, unchanged.

## 9. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| J-5 (journal-coa) | Multi-currency decorative: no base amounts on lines, no report converts, master rates never applied | Migration 062 dual amounts + FxRates precedence + GL/party reports in base (§2–§5) | Harness (a)(c)(d)(e)(f); Kern.Tests `UsdJournal_…`, `MixedCurrencyEntries_…` |
| Invoicing multi-currency MAJOR | Currency free-text, `ExchangeRate` stays 1, ledger posts foreign GrandTotal as base, guard compares unconverted | InvRepo resolves the stored rate (or master, or ACC-5015), GL + ledger + guard all consume base at that rate; COGS kept at engine base cost | Harness (b)(d); Kern.Tests `ForeignSalesInvoice_…`, `ForeignInvoice_WithoutAnyRate_…`, `ForeignInvoice_WithMasterRateOnly_…` |
| Cash B1 (cash-payments) | Exchange rate never applied to money on cash vouchers (lines at face value, no base columns) | Vouchers post through JournalRepository, which now writes dual amounts at the header rate; settlements inherit it | Harness (a) path (same repository); code |
| Cheque BLOCKER (cash-payments) | Foreign cheques post at face value; header rate hardcoded 1 while lines pre-multiplied | Uniform batches post raw amounts under the batch's real currency + rate; single-cheque entries likewise (§4) | Code + build; mixed batches flagged F-2 |
| PayRec cross-currency MAJOR (partial) | Party balances sum mixed currencies raw, feeding decisions at face value | Party balances/aging/due/collection reports and the credit guard are base-consistent (§5). Allocation *matching* itself remains transaction-currency — F-3 | Harness (b) ledger base; code |

## 10. Pillars checklists (touched surfaces)

**Journal (JrnWin/CshWin path)** — UI: unchanged (PROVEN by diff: no window
files touched) · DB: 062 columns + backfill (PROVEN harness f) · Linking:
snapshot carries BaseDebit/BaseCredit/ExchangeRate; reversal mirrors stored
base (PROVEN harness) · Posting: dual amounts written at save/update, base
balance enforced at post (PROVEN a/c) · Persistence: base amounts round-trip
through FindByDocumentNumber (PROVEN Kern.Tests fact 1, owed on Windows) ·
Downstream: TB/BS/P&L + GL family in base (PROVEN harness a TB-style sum;
report SQL source-checked).

**Documents (InvoiceWin/chain/orders)** — UI: unchanged · DB: stored rate no
longer fabricated (0 = absent) · Linking: GL entry header stores effective
rate + base currency; source link unchanged (Step 3) · Posting: ACC-5015
gate, COGS base preserved (PROVEN b/d) · Persistence: party_ledger dual
amounts on every writer (PROVEN b) · Downstream: credit guard + party
reports in base (code + harness b).

**Cheques** — UI: unchanged · DB: no schema change (journal lines carry the
dual amounts) · Linking: batch entry currency/rate now truthful for uniform
batches · Posting: raw amounts + real rate (code) · Persistence: GAP —
mixed-currency batches keep the legacy shape (F-2) · Downstream: GL reports
in base include cheque entries at their stored base.

**Reports** — UI: unchanged shape · DB: read-only · Linking: n/a · Posting:
n/a · Persistence: n/a · Downstream: the reports ARE the downstream —
base-basis totals PROVEN by harness sums + source checks; WPF rendering
owed on Windows (§7.3).

## 11. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr, Lang + Errors catalogs) | **0 missing** in every language (1 new error code ×4; no new Lang keys) |
| Migrator idempotent double-run (SQLite) | PASS (harness, fresh + pre-062 fixture) |
| Arabic outside Ar.json | None added (Arabic only in the Ar Errors catalog entry for ACC-5015) |
| Headless harness | **22/22 PASS** + 13/13 C# scenarios (§7.1) |

## 12. Owner flags

- **F-1 — Stored rate 1 is an explicit rate (interpretation, STOP-flag).**
  Precedence rule 2 treats a stored document/entry rate of 1 as *entered*
  (parity), so the master is consulted only when the stored rate is absent
  (≤ 0). Consequence: an invoice typed in a window with **no rate field**
  (InvoiceWin — the sweep noted ExchangeRate "stays 1, ctor default") whose
  stored rate is the legacy default 1 posts at 1 even if a master rate
  exists. Making the master win over a stored 1 for such documents is a
  one-line precedence change in FxRates, but it would silently revalue
  every legacy foreign document whose 1 was deliberate — flagged for the
  owner's decision rather than guessed. The missing-rate failure
  (ACC-5015) applies exactly where no rate exists anywhere: stored ≤ 0
  and no master row.
- **F-2 — Mixed-currency cheque batches.** The journal model carries one
  currency + rate per entry, so a batch mixing currencies/rates cannot be
  represented truthfully in one entry; those batches keep the legacy
  pre-converted shape (lines in base, header rate 1). Splitting mixed
  batches into one entry per currency is a workflow change for ChqWin —
  owner decision.
- **F-3 — Cost splits + allocation matching stay transaction-currency.**
  `journal_line_cost_splits` amounts and PayRec allocation matching are
  transaction-currency by design (allocations match a payment to a document
  in the document's currency). The cash-payments sweep's cross-currency
  allocation defect (a SAR payment settling a USD invoice unit-for-unit)
  needs an allocation-policy decision (require same currency, or allocate
  at the payment's rate with a difference leg) — not made here; balances
  feeding credit decisions are base-consistent regardless.
- **F-4 — Base-currency source divergence (pre-existing).** The base
  currency of record for posting is `companies.currency_code`, but the
  schema default is `'USD'` (migration 001), app config defaults to SYP,
  UsrPref defaults to SAR, and CurCard reads its "base currency" from app
  config. This step does not reconcile those defaults — the owner should
  confirm `companies.currency_code` is maintained per company, since every
  base amount now depends on it.
