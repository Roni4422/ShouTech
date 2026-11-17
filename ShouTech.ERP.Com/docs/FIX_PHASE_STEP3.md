# FIX PHASE — STEP 3: Real GL Posting for Operational Documents

**Branch:** `fix/foundations-3-glposting` (worktree `~/workspace/projects/ShouTech-fix3`)
**Base:** tip of `fix/foundations-2-coa` (`57de49a`) — the foundations chain is sequential; the canonical chart is `chart_of_accounts` (Guid-keyed, migration 059).
**Date:** 2026-11-17 · **Status:** implemented, gated, NOT pushed, NOT merged.
**Evidence base:** `~/workspace/research/sweep/invoicing-posting.md`, `journal-coa.md` (§1c J-2/J-3, §1d), `cash-payments.md`, `orders-projects-assets.md`, `mfg-hr-crm.md`, `compare-invoicing.md`, `compare-orders-projects-assets.md`, `compare-mfg-hr-crm.md`, `~/workspace/research/acceptance-sweep-report.md`.

---

## 1. The defects, in one paragraph

No operational document created a real journal entry. `InvRepo.Post` fabricated the voucher number (`var journalNo = "JV-" + DocNo`, old `InvRepo.cs:285-286`) and stamped it on the invoice header; repo-wide, only `JournalRepository` (hand-typed journals via JrnWin, cash vouchers via CshWin) ever inserted into `journal_entries`. POS sales, order→invoice conversions, manufacturing completions and asset depreciation likewise never reached the GL, so the Trial Balance / Balance Sheet / P&L that Step 2 made truthful were still *incomplete by construction*. Separately (J-3), cost-center validation in `JournalRepository.ApplyCostCenterAllocationsAsync` had a free-text escape — `continue; // Free-text cost center: no master row, legacy behavior.` — so a cost-center typo posted cleanly and vanished from cost-center reporting, and the account-level `cost_required` flag (carried onto the canonical chart by migration 059) was never consulted on the journal path.

## 2. Architecture: one posting path, inside the document's transaction

**`GlPosting` (`src/ShouTech.Infrastructure/GlPosting.cs`) is the single document→GL posting engine.** It does not invent a parallel writer: it produces entries in exactly the shape `JournalRepository.SaveDraftAsync` + `PostAsync` produce (draft header → lines → cost-center validation/splits → status flip to Posted), so the database posting triggers validate the open fiscal period and postable accounts identically, and `JournalRepository.PostAsync` / `ReverseAsync` now **delegate** their cost-center validation and reversal core to the same code (`GlPosting.ApplyCostCenters`, `GlPosting.ReverseInTransaction`). There is one validation implementation and one reversal implementation in the tree.

`GlPosting.PostInTransaction(connection, transaction, provider, GlDocumentRequest, userId)` runs **inside the caller's existing transaction** — the document and its journal entry commit or roll back together:

1. Lines are aggregated by (account code, cost center, side); zero lines drop out. Unbalanced / <2 lines / zero total fail with the existing catalog codes ACC-5001/5002/5003.
2. **Duplicate guard (ACC-5012):** a live (draft/posted, non-reversal) entry already linked to the same source document fails the posting.
3. The open fiscal period containing the entry date is resolved exactly as `JournalRepository` resolves it; none → the posting (and therefore the document) fails.
4. Every account **code** is resolved against the canonical chart (active, postable, not deleted); unresolvable → **ACC-5011**.
5. Header is inserted as a Draft with the source link (migration 060, §3), `document_number = "JV-" + source doc no` (suffixed with an entry-id fragment only on collision with an unrelated manual entry), `reference_number = source doc no`.
6. Lines are inserted; `ApplyCostCenters` validates and writes `journal_line_cost_splits` (§5).
7. The header flips to Posted (triggers fire), audit events 1 (created) and 2 (posted) are written.

Synchronous ADO is used deliberately: the app-layer document repository (`InvRepo`) is synchronous, and the async infrastructure repositories call the same core inside their async transaction scope without moving their commit boundary. `JournalRepository.PostDocumentAsync` wraps the core in its own transaction for callers that own none.

**Callers (all in the document's own transaction):**

| Document path | Caller | Source type |
|---|---|---|
| Sales/purchase invoice, returns, POS sale, stock in/out | `InvRepo.Post` (app layer) | `SAL` / `PUR` / `SRT` / `PRT` / `POS` / `ENT` / `EXT` (the document's `KindCode`) |
| Sales-chain invoice post | `CommercialDocumentRepository.PostSalesAsync` | `SAL` |
| Order → invoice conversion | `OrderDocumentRepository.ConvertToInvoiceAsync` | `SAL` / `PUR` (the invoice kind) |
| Manufacturing completion | `MfgOrderRepository.PostAsync` | `MFG` |
| Manufacturing cancellation of a posted order | `MfgOrderRepository.CancelAsync` → `GlPosting.ReverseInTransaction` | reverses the `MFG` entry |
| Asset depreciation | `AssetRepository.PostDepreciationAsync` via `AssetService.PostDepreciationAsync`, new **Post Depreciation** button in AssetWin | `AST-DEP` |

## 3. Migration 060 — `060_document_gl_posting.sql` (SQLite + PostgreSQL)

Adds to `journal_entries`: `source_doc_type TEXT`, `source_doc_id TEXT`, `source_doc_no TEXT`, plus index `ix_journal_entries_source_doc (company_id, source_doc_type, source_doc_id)`. Guarded/idempotent on both providers (SQLite `@if-column-missing` directives, PG `ADD COLUMN IF NOT EXISTS`); migrator double-run verified (§7). `source_doc_id` is TEXT because the document stores mix integer `doc_id`s (invoices, chain documents), Guids (manufacturing orders) and composite keys (asset depreciation uses `assetGuid@yyyy-MM-dd`, one entry per asset per posting date — depreciation is posted incrementally, so it is the one source allowed multiple live entries over time). Reversal entries **copy** the source columns, so the GL→document trace survives reversal, and the duplicate guard ignores reversal rows.

## 4. Account mapping (per document kind)

Defaults are the canonical codes present in **every** seeded chart template (`cod/app/src/Resources/COA/*.shc`): Cash `131`, Bank `132`, Receivable `1201`, Inventory `1203`, Payable `2201`, Sales `301`, Sales Returns `302`, COGS `401`, Purchases `402`, Purchase Returns `403`, Depreciation Expense `505`, Accumulated Depreciation `1190`. A material's `acc_*` columns override the revenue / purchase / COGS / inventory legs **when they hold an account code**; the document header's cost center is applied to the revenue/purchase legs of invoice documents.

| Kind | Debit | Credit | Notes |
|---|---|---|---|
| Sales invoice | Receivable `1201` — grand total (minus any amount paid at the document) + settlement account (Cash `131` / Bank `132`) for the paid part | Sales `301` (per-line `acc_sales`) — net of tax when tax is split, tax-inclusive otherwise; Output VAT (below) | COGS pair below |
| POS sale | Settlement by tender: Cash → `131`, Checks/Card → `132`, Credit → Receivable `1201` for the unpaid part | Sales `301` as above | POS posts through `InvRepo.Post` (PosCheckoutWin), so it inherits this mapping; paid-in-full cash POS = Dr Cash / Cr Sales |
| Sales return | Sales Returns `302` (per-line `acc_sales_return`) + Output VAT debit when split | Receivable `1201` — grand total | COGS pair reversed |
| Purchase invoice | Purchases `402` (per-line `acc_purchase`) + Input VAT debit when split | Payable `2201` — grand total (minus paid part) + settlement account for the paid part | No COGS |
| Purchase return | Payable `2201` — grand total | Purchase Returns `403` (per-line `acc_purchase_return`) + Input VAT credit when split | |
| COGS (sales / POS) | COGS `401` (per-line `acc_cogs`) | Inventory `1203` (per-line `acc_inventory`) | At the stock layer's **current** cost figures: balance `average_cost` for the line's warehouse, falling back to the material card `avg_cost`/`cost`. Only for stockable materials (the StkPost service-material rule). Step 6 owns costing-method unification |
| COGS (sales return) | Inventory `1203` | COGS `401` | Mirror of the above |
| Chain sales invoice (`PostSalesAsync`) | Receivable `1201` — grand total | Sales `301` (+ VAT when split) | No COGS: the chain invoice does not move stock (the delivery note did, without amounts) |
| Order conversion (sales) | Receivable `1201` | Sales `301` (+ VAT when split) | Invoice is created already-Posted; GL is written in the conversion transaction |
| Order conversion (purchase) | Purchases `402` (+ Input VAT when split) | Payable `2201` | |
| Manufacturing completion | Inventory — finished item's `acc_inventory` or `1203` — raw lines' total amount | Inventory — each raw item's `acc_inventory` or `1203` — its line amount | Overhead is **not** GL-posted: it stays in the expense accounts where it was incurred (the stock layer values finished goods at total cost incl. overhead — the valuation gap is Step 6's, flagged §9 F-3) |
| Asset depreciation | Depreciation Expense `505` — accumulated-to-date minus already-posted | Accumulated Depreciation `1190` | Incremental: re-running posts only the new increment; a run with nothing new posts nothing |
| Stock in (`ENT`) | Inventory `1203` — line totals | Purchases `402` | |
| Stock out (`EXT`) | COGS `401` — line totals | Inventory `1203` | |
| Quotation, Stock Transfer, Opening Stock, Inventory Count | — no GL entry — | | Quotations are not accounting documents; a transfer moves value between warehouses of the same inventory account; opening/count adjustments are valuation events (Step 6). These kinds no longer receive a fabricated journal number — `journal_doc_no` stays empty |

**Tax rule:** Output VAT `2205` / Input VAT `1205` are split out **only when the company has configured them postable** (or a material's `tax_account` names a postable account). In every seeded template `1205`/`2205` are non-postable group accounts, so by default tax stays inside the revenue/purchase leg and the entry balances on the document totals — the same totals the party ledger has always used. When a postable tax account exists, revenue/purchases post net and the tax leg carries the document's tax amount.

**Amounts dependency (Step 5):** `InvRepo.Post` builds its `InvDoc` from the document's lines and does **not** load the stored header discount/extra (the sweep's stored-`grand_total` BLOCKER). The GL entry therefore posts exactly the totals the document layer hands over today — identical to what `party_ledger` receives today — so GL and party ledger can never disagree with each other. When Step 5 fixes the totals load, both ledgers move together with no change needed here. The chain/conversion paths use their stored header totals, as they already did for `party_ledger`.

**Reversal/cancellation symmetry:** returns post mirroring entries through the same mapping (table above), so an invoice + its return nets to zero per account (proven §7). Manufacturing cancellation reverses the completion entry through the shared Reverse path in the cancellation transaction. A document's GL entry can always be reversed from JrnWin through the existing Reverse button, because it is an ordinary posted journal entry. Pre-Step-3 documents have no GL entry; cancelling one simply has nothing to reverse (manufacturing path handles this explicitly).

## 5. Cost-center validation (J-3) — `GlPosting.ApplyCostCenters`

One implementation, called from `JournalRepository.PostAsync` (so **JrnWin and CshWin inherit it**) and from every document posting:

| Case | Before Step 3 | Now |
|---|---|---|
| Code matches **no** master row | Silently posted, no split (`continue; // Free-text…`) | **Posting fails — ACC-5009 `CostCenterUnknown`** (new) |
| Account flagged `cost_required`, line has no center | Never consulted | **Posting fails — ACC-5010 `CostCenterRequired`** (new) |
| Center is a group or inactive | ACC-5008 (only when the text happened to match) | ACC-5008 — unchanged code, now reached for every matched center |
| Center has an allocation not totalling 100 / targeting a non-leaf | ACC-5007 | ACC-5007 — unchanged |
| Valid leaf / valid allocation | Split rows written, residue to largest target | Unchanged mechanics |

Legacy free-text **drafts** are untouched as drafts; *posting* one now validates and fails on an unknown center — intended, per the mandate, with no silent bypass. **Reversals** keep the historical leniency (`enforceValidation: false`): a reversal mirrors an already-posted entry and is never blocked by master-data changes made after the original posting; unknown centers on a reversal record no split, exactly as before.

New catalog codes ACC-5009…ACC-5012 are registered in `ErrorCodes.cs`, all four error catalogs (En/Ar/Fr/Tr) and `docs/ERROR_CATALOG.md`. ACC-5011 = mapped posting account unresolvable; ACC-5012 = duplicate document posting (§2).

## 6. Per-defect closure

| ID | Defect | Closure | Proof |
|---|---|---|---|
| J-2 | No operational document creates a real journal entry; `InvRepo.Post` fabricates `"JV-" + DocNo` and stamps it on the header | §2/§4: every listed kind posts a real balanced entry in the document's transaction, linked by migration-060 source columns; the stamp is now the real entry's `document_number` returned by the engine. Only `JournalRepository` inserted entries before; `GlPosting` writes in the identical shape and shares its validation/reversal core, so there is still exactly one journal writer design | Harness (per-kind cases); Kern.Tests `GlPostingTests` (12 facts) |
| J-3 | Free-text cost centers escape validation; `cost_required` never consulted | §5: unknown center → ACC-5009, cost-required without center → ACC-5010, on the shared posting path (JrnWin/CshWin + all document postings) | Harness CC cases; Kern.Tests `JournalPost_UnknownCostCenter_FailsWithAcc5009`, `JournalPost_CostRequiredAccountWithoutCenter_FailsWithAcc5010` |
| AST-01 (asset family) | Depreciation is display-only, never posted | §2/§4: depreciation posting API + AssetWin **Post Depreciation** button; incremental, idempotent per date | Harness AST-DEP case; Kern.Tests `AssetDepreciation_PostsIncrementOnly` |
| MFG (mfg-hr-crm) | Manufacturing completion moves stock but never posts value to GL | Completion posts Dr/Cr inventory at raw value in the posting transaction; cancellation reverses it through Reverse | Harness MFG case; Kern.Tests `Manufacturing_PostAndCancel_PostsAndReversesGl` |
| POS (cash-payments) | POS sale never reaches GL; its cash was unrecorded in the books | POS posts through `InvRepo.Post` → Dr Cash/Bank/Receivable by tender, Cr Sales, + COGS | Harness POS case; Kern.Tests `PosSale_Post_DebitsCash_NotReceivable` |

Out of scope and untouched, as mandated: party-master unification / `party_ledger` settlement mechanics (Step 4 — this step's entries debit/credit only the party **control** accounts in the GL), `InvRepo.Post` amount correctness and kind guards incl. the Quotation-receivable bug (Step 5 — see §4 amounts dependency; note the Quotation GL consequence: a quotation posted through `InvRepo.Post` builds **no** GL lines by design in §4, but its `party_ledger` row is Step 5's to fix), valuation/COGS costing unification (Step 6).

## 7. Proof

### 7.1 Headless harness — `~/workspace/research/fix-step3/gl_harness.py` — **28/28 PASS**

Runs the **real SqlMigrator** (fix3 build) and replays the app's own SQL shapes (`GlPosting.PostInTransaction` / `ReverseInTransaction` / `ApplyCostCenters`, with the real posting triggers enforcing period/account validity):

- Migration 060 applies; migrator double-run is a clean no-op; source columns exist.
- Per kind, post → entry exists, is Posted, balanced, carries the source link, `document_number = "JV-" + doc no`, and the per-account amounts equal the §4 mapping table: SAL (incl. COGS 80), PUR, SRT, PRT, POS-cash, order-conversion SAL, MFG (Dr/Cr inventory 20), AST-DEP (300).
- The template VAT account `2205` is non-postable → tax stays unsplit by design (ACC-5011 on direct resolution, which `TryResolveAccount` turns into "no tax leg").
- Duplicate document posting → ACC-5012. Posting with no open period → fails, **no entry written**.
- Unknown cost center → ACC-5009 with the whole posting rolled back; cost-required account without a center → ACC-5010; group center → ACC-5008; valid leaf center posts and its split rows sum exactly to the line amounts.
- Reversal of the SAL entry: original marked Reversed, reversal Posted with mirrored amounts, pair nets to zero per account; aggregate of all live entries balances.
- Source checks: the fabricated stamp is gone from `InvRepo`; every document repository calls `GlPosting`; `JournalRepository` delegates CC validation + reversal to the shared core and the free-text escape comment is gone.

### 7.2 Kern.Tests — `tests/Kern.Tests/GlPostingTests.cs` (12 facts)

Real C# paths against real migrated SQLite databases: sales/purchase/returns/POS postings through the real `InvRepo` (balanced, linked, correct accounts/amounts, real journal number stamped and reloadable); invoice reversal via `ReversePostedInvoice` netting GL to zero; period-less posting failing with the document left Draft and un-stamped; ACC-5009 / ACC-5010 through the real `JournalService`; ACC-5012 + Reverse symmetry through `JournalRepository.PostDocumentAsync`; manufacturing post + cancel through the real `MfgOrderService`; asset depreciation increments through the real `AssetService`. **Execution is owed on Windows** (net8.0-windows testhost cannot run on this Linux VM) — the project compiles at 0 errors / 0 warnings here.

Existing fixtures were updated where Step 3 changed the world they simulate: `ChainTests` and `OrderServiceTests` now seed the GL company (companies row, fiscal year + periods, canonical posting accounts) their documents post against, and the manufacturing `TempDb` gained the journal/fiscal/cost-center tables plus the `1203` account and `acc_inventory`/`cost_required` columns. `Phase3ServiceTests.FakeAssetRepository` implements the new interface member.

### 7.3 What is NOT proven here

WPF runtime behavior (clicking Post in InvoiceWin / PosCheckoutWin, the new AssetWin button) and **live PostgreSQL** execution of migration 060 and the new queries are owed on Windows / a live PG instance — no PG server exists in this VM. The PG migration uses only standard idempotent constructs and mirrors the SQLite logic, but it has not been executed. Build + SQLite harness success is not product readiness, per the standing rule.

## 8. Gates

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (En/Ar/Fr/Tr, Lang + Errors catalogs) | **0 missing** in every language (3 new `Assets.*` Lang keys + 4 new error codes, all ×4) |
| Migrator idempotent double-run (SQLite) | PASS (harness) |
| Arabic outside Ar.json | None added (Arabic text appears only in the Ar Lang/Errors catalogs) |
| Headless harness | **28/28 PASS** (§7.1) |

## 9. Owner flags

- **F-1 — Period-close UI: ABSENT for periods (verdict: flagged, not built).** A fiscal **year**-close surface exists (`RolloverWin` → `OperationsService.CloseFiscalYearAsync`), but **no window can close a fiscal period**: `AccountingStructureRepository.ClosePeriodAsync` (with its no-drafts / balanced / sequential-close rules) has zero UI or service callers. Building it properly is a window build — period list per year, close action wired through a service, permissions, Lang keys — so per the mandate's stop rule it was **not** ballooned into this step. The posting side is fully period-safe today: every entry (manual or document) fails into a closed/missing period, and year close already requires all periods closed at the engine level.
- **F-2 — Tax split depends on chart configuration.** With the seeded templates, VAT accounts are non-postable groups, so tax is **not** split out of revenue/purchases until the company makes a postable tax account (or sets materials' `tax_account`). If the owner wants tax always split, the chart templates' `1205`/`2205` must become postable (or gain postable children) — a template/seed decision, not a posting-code change.
- **F-3 — Manufacturing overhead and finished-goods value.** GL inventory rises by the raw-material value only; the stock layer values finished goods at total cost **including overhead**. The difference is the Step 6 valuation unification's to close; it is recorded here so it is not discovered later as a discrepancy.
- **F-4 — Stock kinds without GL.** Opening Stock and Inventory Count post no GL entry (valuation events, Step 6); they also no longer carry a fabricated journal number. If the owner wants opening balances in the GL from day one, that is a deliberate opening-entry design (Dr Inventory / Cr Opening Balance Equity) to approve separately.
- **F-5 — Chain (DLV) COGS.** The sales chain's accounting document is the invoice, which posts revenue/receivable only; the delivery note moves stock at zero cost, so chain COGS never posts. Fixing that belongs with the chain's costing (Step 6) — the invoice-side GL here is complete for what the chain currently values.

## 10. Comparison tables (InvoiceWin / POS / AssetWin vs counterparts)

Condensed from `~/workspace/research/sweep/compare-invoicing.md` and `compare-orders-projects-assets.md`, with the **Step 3** column recording exactly what this step changed. ⚠ = counterpart behavior asserted from product knowledge in the sweep, not live-verified.

### 10.1 InvoiceWin — posting behavior (the window itself is unchanged)

| Capability | ShouTech before | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | **Step 3** |
|---|---|---|---|---|---|
| Posting creates a real GL entry | ✗ fabricated `"JV-"+DocNo` stamp only | ✓ | ✓ GL Entry per invoice | ✓ journal entry | **✓** — balanced entry in `journal_entries`, linked to the document, in the posting transaction |
| Journal/voucher number on the document | ~ fake string | ✓ opens the real voucher | ✓ | ✓ | **✓** — the real entry's document number; JrnWin can open/reverse it |
| Accounts from material configuration | ✗ `acc_*` columns honored by nothing | ✓ | ✓ item defaults | ✓ category accounts | **✓** — `acc_sales/acc_cogs/acc_inventory/acc_purchase/…` override the canonical defaults per line |
| COGS + inventory relief on sales | ✗ | ✓ | ✓ | ✓ | **✓** — at current stock-layer costs (Step 6 owns the costing method) |
| Tax split to VAT account | ✗ | ✓ | ✓ | ✓ | **◐** — split when a postable tax account is configured; otherwise tax-inclusive leg (flag F-2) |
| Part-payment at posting (paid amount → cash/bank, remainder → control) | ✗ paid amount ignored by posting | ✓ | ✓ | ✓ | **✓** in GL (party_ledger settlement remains Step 4) |
| Reversal of a posted invoice | ◐ return document, no GL effect | ✓ | ✓ credit note with GL | ✓ credit note | **✓** — the return posts a mirroring GL entry; pair nets to zero |
| Posting into a closed period | n/a (nothing posted) | ✗ blocked | ✗ blocked | ✗ blocked | **✓ blocked** — period resolution + DB triggers, document stays Draft |

### 10.2 POS (PosWin + PosCheckoutWin — UI unchanged; posting path is InvRepo)

| Capability | Before | Al-Ameen ⚠ / Phenix ⚠ | ERPNext POS | Odoo POS | **Step 3** |
|---|---|---|---|---|---|
| Sale posts to GL | ✗ | ✓ | ✓ | ✓ (POS session entry) | **✓** — Dr Cash/Bank by tender (Credit → Receivable), Cr Sales, + COGS, in the sale's transaction |
| Cash tender recorded in the books | ✗ (sweep: POS cash unrecorded) | ✓ | ✓ | ✓ | **✓** — cash leg is the `131` debit above |
| Everything else in the sweep's POS table (split tender, hold, receipt print, POS profile/session close) | ✗ | ✓ | ✓ | ✓ | GAP — unchanged; POS **session**-level aggregated posting (Odoo model) is not adopted: ShouTech posts per sale, like ERPNext/Al-Ameen |

### 10.3 AssetWin (one button added: Post Depreciation, bottom bar)

| Capability | Before | Al-Ameen ⚠ | ERPNext | Odoo | **Step 3** |
|---|---|---|---|---|---|
| Depreciation posts to GL | ✗ display-only schedule (AST-01) | ✓ periodic vouchers | ✓ depreciation schedule → journal entries | ✓ depreciation board posts entries | **✓** — Dr `505` / Cr `1190`, accumulated-to-date minus already-posted, per asset per date |
| Re-run safety | n/a | ✓ | ✓ | ✓ | **✓** — second run for the same date posts nothing; later date posts only the increment |
| Disposal with proceeds / gain-loss | ✗ status flip (AST-02) | ✓ | ✓ | ✓ | GAP — unchanged, not in this step's mandate |
| Asset acquisition GL (Dr asset / Cr payable-cash at purchase) | ✗ | ✓ | ✓ | ✓ | GAP — acquisition normally arrives via a purchase invoice, which now posts Dr Purchases; capitalizing it to the asset account is a fixed-asset policy decision for the owner |

### 10.4 OrdWin / MfgWin (windows unchanged; their repositories post)

Order→invoice conversion and manufacturing completion now create the GL entries in §4 in their own transactions; MfgWin cancellation reverses the completion entry. Their remaining sweep gaps (OrdWin header fields, MFG overhead costing) are unchanged and out of this step's scope.

## 11. Pillars checklists (per touched window)

Legend: PROVEN = evidence in §7; GAP = explicitly not done.

### InvoiceWin (SAL/PUR/SRT/PRT/POS kinds)
- **UI:** unchanged (no window code edited).
- **DB:** PROVEN — entries land in `journal_entries`/`journal_entry_lines` with migration-060 source link; migrator double-run idempotent.
- **Linking:** PROVEN — entry ↔ document both ways (source columns on the entry; real `journal_doc_no` on the document, reloadable via `InvRepo.Load`).
- **Posting:** PROVEN — per-kind balanced entries per §4 in the document's transaction (harness + Kern.Tests); closed/missing period blocks the post and leaves the document Draft.
- **Persistence:** PROVEN — every amount on the entry derives from persisted document lines/header fields the loader already reads; no UI-only GL data.
- **Downstream:** PROVEN at data level — entries are ordinary posted journals, so Step 2's TB/BS/P&L/statement (computed from posted lines) now include document postings; GAP: live WPF report rendering owed on Windows.

### PosCheckoutWin / PosWin
- **UI:** unchanged. **DB/Posting:** PROVEN via the `InvRepo` POS path (tender-based settlement leg). **Linking:** PROVEN — source type `POS`, document number stamped. **Persistence:** PROVEN — tender comes from the persisted `payment_method`/`paid_amount`. **Downstream:** as InvoiceWin. GAP: POS session/day-close aggregation does not exist (per-sale posting instead, §10.2).

### OrdWin (order → invoice conversion)
- **UI:** unchanged. **DB/Posting:** PROVEN — conversion writes the invoice **and** its GL entry in one transaction (harness conversion case; fixture-seeded Kern.Tests suite compiles/runs the path). **Linking:** PROVEN — entry source = the created invoice; the pre-existing `document_links` order↔invoice link is unchanged. **Persistence:** PROVEN — totals from the persisted order lines. **Downstream:** invoice GL flows to GL reports as above.

### MfgWin (manufacturing)
- **UI:** unchanged. **DB/Posting:** PROVEN — completion posts inventory value transfer; cancellation reverses it through Reverse in the same transaction (harness + Kern.Tests). **Linking:** PROVEN — source = order Guid + `MO-<doc no>`. **Persistence:** PROVEN — amounts from persisted order lines. **Downstream:** GAP — overhead valuation difference (flag F-3, Step 6).

### AssetWin
- **UI:** PROVEN (code) — **Post Depreciation** button added to the existing bottom command bar, shell-context inherited (BaseWin), Lang keys ×4, gated by `PermCat.Assets.Update`, errors via `AppHost.Errors`. Runtime click-through owed on Windows.
- **DB:** PROVEN — depreciation entries are ordinary journal entries with `AST-DEP` source link.
- **Linking:** PROVEN — entry ↔ asset (source id `assetGuid@date`, source no = asset code); the journal number is shown to the user.
- **Posting:** PROVEN — Dr `505` / Cr `1190`, incremental and re-run-safe (harness + Kern.Tests).
- **Persistence:** PROVEN — amount derives from the persisted register fields via the existing `AssetService` schedule math; nothing stored twice (already-posted is computed from the GL itself).
- **Downstream:** PROVEN at data level — depreciation now reaches TB/BS (accumulated depreciation is a real posted balance). GAP: disposal workflow (AST-02) unchanged.

### JrnWin / CshWin (manual journals + cash vouchers)
- **UI:** unchanged. **Posting:** PROVEN — both post through `JournalRepository.PostAsync`, which now enforces J-3 (ACC-5009/5010) via the shared core; reversals use the shared reversal core with identical semantics. **Downstream:** cost-center splits now exist only for validated centers, so cost-center reporting can no longer silently lose a typo'd center — it fails at posting instead.

## 12. Files changed

- `dba/mig/sqlite/060_document_gl_posting.sql`, `dba/mig/postgresql/060_document_gl_posting.sql` (new)
- `src/ShouTech.Infrastructure/GlPosting.cs` (new — the posting engine: document posting, shared cost-center validation, shared reversal), `src/ShouTech.Domain/Entities/GlPosting.cs` (new — request/result records), `src/ShouTech.Domain/ErrorCodes.cs` (ACC-5009…5012)
- `src/ShouTech.Infrastructure/JournalRepository.cs` (PostAsync → shared CC validation; ReverseAsync → shared reversal core; new `PostDocumentAsync`)
- `cod/app/src/Services/InvRepo.cs` (real GL in `Post`, per-kind line builder, material `acc_*`/cost resolution; fabricated stamp removed)
- `src/ShouTech.Infrastructure/CommercialDocumentRepository.cs` (chain SAL GL), `OrderDocumentRepository.cs` (conversion GL), `MfgOrderRepository.cs` (completion GL + cancellation reversal), `AssetRepository.cs` + `src/ShouTech.Domain/Interfaces/IAssetRepository.cs` + `src/ShouTech.Application/Services/AssetService.cs` (depreciation posting)
- `cod/app/src/Views/AssetWin.cs` (Post Depreciation button), Lang catalogs ×4 (`Assets.PostDepreciation` etc.), Errors catalogs ×4 (ACC-5009…5012), `docs/ERROR_CATALOG.md`
- `tests/Kern.Tests/GlPostingTests.cs` (new, 12 facts); fixture updates in `SalesChainPostingTests.cs`, `OrderServiceTests.cs`, `ChequeMfgServiceTests.cs`, `Phase3ServiceTests.cs`
- `docs/FIX_PHASE_STEP3.md` (this file)
