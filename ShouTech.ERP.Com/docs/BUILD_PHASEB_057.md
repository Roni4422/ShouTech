# Build Phase B4 — CRM Pipeline (migration 057)

Branch: `build/phaseb-057-crm` (from `build/phaseb-056-quality` @
`87b3030`; the chain carries fix steps 1–13 + builds 054/055/056).
Design source: `~/workspace/research/phase-b-design.md` §4 (CRM) and
§7 (decision list). This document maps design → built, records the
C1–C3 decisions taken and the owner's entitlement postponement, the
schema, the pipeline/conversion design, the comparison tables, the
per-window pillars checklists, the headless proof, the gates, and the
owner flags.

## 1. What was built

| Layer | Artifact |
|---|---|
| Migration | `dba/mig/sqlite/057_crm.sql`, `dba/mig/postgresql/057_crm.sql` — `crm_stages`, `crm_leads`, `crm_opportunities`, `crm_opportunity_stage_history`, `crm_opportunity_documents`, `crm_activities`, `crm_lost_reasons` + `CRM.*` permissions (358–365) |
| Domain | `Entities/Crm.cs` (stages, lost reasons, leads, opportunities, history, document links, activities, pipeline board, constants), `Interfaces/ICrmRepository.cs`, error codes `CRM-9001..9006` (`ErrorCodes.cs`, new CRM 9000 block), `ModKey.Crm` registered |
| Data/service | `Infrastructure/CrmRepository.cs`, `Application/Services/CrmService.cs` (validation layer; repository re-checks state inside its transactions) |
| Windows | `Views/CrmLeadWin.cs`, `Views/CrmOppWin.cs`, `Views/CrmPipelineWin.cs`, `Views/CrmActivitiesWin.cs` — all `BaseWin`, bottom command bars, unique schedule tints (Orchid / Violet / Indigo / Coral) |
| Wiring | `AppHost.Crm` (+ `AppServiceHost` pass-through, rebound on company switch), `PermCat.Crm`, `RibbonNav` routes `CRM.Leads / CRM.Pipeline / CRM.Opportunities / CRM.Activities`, ribbon buttons on the MWin Customers tab (Follow-up group), audit script knows `AppHost.Crm` |
| i18n | `Crm.*` (95 leaf keys incl. `Col.*`) in En/Ar/Fr/Tr — identical key sets; Errors `CRM-9001..9006` in all 4 catalogs + `docs/ERROR_CATALOG.md` |
| Tests | `tests/Kern.Tests/CrmPipelineTests.cs` (6 facts: conversion, stage walk + weighted math, overdue activities, activity target rule, quotation no-effects, full round-trip) |

**Pre-existing CRM surface (honest baseline):** the sweep measured CRM
at 13% — an `appset.json` module entry ("Manage leads, opportunities
and follow-ups", enabled, no code behind it) plus the unrelated
`CRM.CUSTOMER.*` core customer-master permissions in `PermCat.Customers`.
There were no CRM tables, windows, routes or services. This build keeps
the module entry (now true), leaves the `CRM.CUSTOMER.*` permissions
untouched (they are core customer-card permissions), and builds the
pipeline fresh on migration 057.

## 2. Design vs built — with the C decisions

| Design element (§4) | Built | Notes |
|---|---|---|
| `ModKey.CRM` added to the canonical vocabulary | ✅ `ModKey.Crm = "CRM"` | Registered only — see the entitlement note below |
| CRM windows register `CRM.*` nav keys | ✅ RibbonNav `CRM.Leads/Pipeline/Opportunities/Activities` + MWin Customers-tab buttons with those keys | Shell gating can find them the day licensing lands |
| `CrmService` calls the entitlement gate at entry points | ❌ **Not built — owner override** | Licensing/entitlement work is **postponed by the owner**. CRM is fully functional under normal RBAC (`CRM.*` permissions, step-1 pattern) only. **No fake gate stands in its place.** Entitlement gating arrives with the postponed licensing work; the service and repository carry the note where the gate will go |
| `crm_stages` (per-company, editable, seeded New 10 → Qualified 25 → Proposition 50 → Negotiation 75 → Won 100 / Lost 0) | ✅ as designed | Seeded **per company on first use** by the repository (a migration cannot know future companies); stages are configuration (Odoo pattern), edited in CrmPipelineWin under `CRM.STAGE.EDIT` |
| `crm_leads` (own entity, status, source, campaign, rep, conversion links) | ✅ as designed | **C1 — Taken: lead is its own entity** (recommendation). Unqualified data never touches `parties` |
| `crm_opportunities` (party/lead links, stage, probability, amount, currency, close date, type, lost reason) | ✅ as designed | Status is **derived** from the stage's `stage_type` — no second status field |
| `crm_opportunity_documents` (dedicated link table) | ✅ as designed + surrogate `link_id` PK + `company_id` | Kept separate from `document_links` for the design's stated reason (TEXT CRM ids vs the integer document world) |
| `crm_activities` (exactly one of lead/opp/party, due + completion) | ✅ as designed | Exactly-one-target enforced in service **and** repository (`CRM-9005`); overdue is derived (Planned + past due), never a stored status |
| `crm_lost_reasons` (seeded Price / Competitor / No Budget / No Response / Timing / Project Cancelled) | ✅ as designed | Seeded per company on first use; required on a Lost move (`CRM-9006`, Odoo rule) |
| Stage-change history | ✅ **Added beyond the sketch** — `crm_opportunity_stage_history` | The task mandate requires every stage change recorded; creation writes the first row (from-stage null). Rows are read in insertion order (SQLite `rowid` / PG timestamp): `datetime('now')` has 1-second resolution and Guid ids are random, so timestamp+id ordering scrambled same-second moves — caught by the harness, fixed, pinned in tests |
| Lead conversion (one transaction → party + opportunity + links) | ✅ as designed | Party is inserted into the **unified master** (`parties`, kind `C`) with the same columns and the same `ledger_id` rule `MasterCardRepository` uses (above every parties/ledger identity in use) — no duplicated party logic, no second store. Party code auto-assigned `CUST-nnnn`; name prefers the lead's organization, else the lead name; phone/mobile/email/address/notes carried |
| Quotation = existing `InvoiceWin` Quotation kind | ✅ | **C3 — Taken: the app-layer quotation** (`InvDocKind.Quotation`, `sales_documents` doc_kind `QUO`), per the task mandate — NOT a separate quote module and NOT a parallel chain-only quote store. Ground truth that shapes the write: `InvRepo.Load` accepts **both** company keys since Fix Phase Step 9, while the step-9 chain (`OrderService.ConvertQuotationToOrderAsync`) requires the **Guid** key — so `CrmRepository.CreateQuotationAsync` writes the quotation row with the company's Guid key (resolved from `companies` by code). The same row therefore opens in InvoiceWin **and** converts onward through the step-9 chain. Multiple quotations per opportunity are supported (each create adds a link row) |
| Quotation → order → invoice follows the step-9 chain | ✅ proven end-to-end | Harness (d): CRM quotation (Draft, no effects) → chain `PostSalesAsync` (still effect-free) → `ConvertQuotationToOrderAsync` (order Draft + `document_links` QUO→SO) → CRM links the order → post + `ConvertToInvoiceAsync` → CRM links the invoice; only the invoice moves stock/GL |
| `CrmPipelineWin` board | ✅ grouped-list board | **C2 — Taken: grouped lists + move buttons** (recommendation), one section per stage with raw + weighted footers; Advance/Regress skip the Lost stage (losing needs a reason — done in CrmOppWin) |
| `CrmLeadWin` / `CrmOppWin` / `CrmActivitiesWin` | ✅ | OppWin carries the stage stepper, stage-history grid, documents grid (live status from `sales_documents`), activities are worked in CrmActivitiesWin (mine/open/overdue filters, complete-with-result in place) |
| Reports (funnel / win rate / stale opportunities) | ⚠️ **Partial** | The pipeline board's per-stage raw + weighted totals and the open weighted total are the funnel surface in v1; dedicated funnel / win-rate-by-rep / stale-opportunity **report windows** are a GAP (see §7) |
| Sales reps | ✅ stay CORE | CRM only references `sales_reps.code` (`assigned_rep_code`); reps, groups and targets were not moved, gated or rewritten |
| Permissions `CRM.LEAD.* / CRM.OPP.* / CRM.ACTIVITY.*` | ✅ + `CRM.STAGE.EDIT` | 8 permissions, display_order 358–365; ADMIN all, USER all but STAGE.EDIT, VIEWER the three VIEWs |
| Errors: small CRM range | ✅ `CRM-9001..9006` | New CRM 9000 block in `ErrorCodes.cs` (the LIC-precedent pattern): already-converted, lead state, stage invalid, quotation party/company unresolved, activity target, lost reason required |

## 3. Schema (migration 057, both providers)

Company keying is the **party world's company CODE** (TEXT) throughout
— the key `parties`/`sales_reps` use — except the quotation document
row itself, which carries the company Guid for the chain (see C3).

- `crm_stages(stage_id, company_id, name_en, name_ar, sequence, probability, stage_type[Open/Won/Lost], is_active, is_deleted, audit)`
- `crm_lost_reasons(reason_id, company_id, name_en, name_ar, is_active, is_deleted, audit)` — unique (company, name_en) among live rows
- `crm_leads(lead_id, company_id, lead_name, organization, email, phone, mobile, address, source, campaign, status[New/Working/Qualified/Converted/Unqualified], assigned_rep_code, converted_party_id → parties.id, converted_opportunity_id, notes, is_deleted, audit)`
- `crm_opportunities(opp_id, company_id, title, party_id → parties.id, lead_id → crm_leads, stage_id, probability, expected_amount, currency_code, expected_close_date, opp_type, source, campaign, assigned_rep_code, lost_reason_id, lost_notes, is_deleted, audit)`
- `crm_opportunity_stage_history(history_id, company_id, opp_id, from_stage_id, to_stage_id, from_probability, to_probability, changed_by, changed_at, notes)`
- `crm_opportunity_documents(link_id, company_id, opp_id, doc_kind, doc_id INTEGER, link_type[Quotation/Order/Invoice], created_by, created_at)` — unique (opp, kind, doc)
- `crm_activities(activity_id, company_id, subject, activity_type[Call/Meeting/Email/Task], due_at, completed_at, status[Planned/Done/Cancelled], lead_id/opp_id/party_id — exactly one, assigned_to, result_notes, is_deleted, audit)`

PG adaptations mirror 055/056: `uuid` PKs, `BOOLEAN` flags,
`TIMESTAMPTZ` audit stamps, `DATE` close date, `BIGINT` doc_id,
`ON CONFLICT` permission seeds. All writes are parameterized; every
multi-row write is one transaction; deletes are soft.

## 4. Pipeline & conversion design

- **Weighted value** = `expected_amount × probability / 100`, rounded
  to 4 dp (`CrmOpportunity.WeightedValue`); board column totals and the
  open-pipeline total are sums of the same property, so the card, the
  board and the harness can never disagree.
- **Stage moves** (`MoveStageAsync`, one transaction): stage +
  probability (stage default unless explicitly overridden), lost
  reason/notes written only for Lost targets (and required there),
  cleared when leaving Lost, plus the history row. Card saves never
  move the stage — moves exist only through this path, so the history
  is complete by construction.
- **Conversion** (`ConvertLeadAsync`, one transaction): refuse
  converted (`CRM-9001`) / unqualified (`CRM-9002`) → insert party
  (kind `C`, next `CUST-nnnn` code probed in-transaction, `ledger_id`
  = max(parties.ledger_id, party_ledger.party_id) + 1) → insert
  opportunity at the first Open stage (probability = stage default,
  type New Business, source/campaign/rep inherited) + creation history
  row → stamp lead `Converted` with both ids. Any failure rolls all
  three writes back.
- **Quotation creation** (`CreateQuotationAsync`, one transaction):
  opportunity must carry a party with a `ledger_id` (`CRM-9004`
  otherwise — convert/link first, the ERPNext/Odoo flow) → resolve the
  company Guid → next `QUO-yyyy-nnnn` number in the Guid namespace →
  insert the `sales_documents` Draft (party = `ledger_id`, totals from
  the supplied lines, or one line from title + expected amount) + line
  rows + the `crm_opportunity_documents` link. **No** `party_ledger`,
  journal or stock writes — Step 5's effect-free quotation, proven in
  the harness before and after the chain posts the quotation.
- **Activities**: due stored as `yyyy-MM-dd HH:mm` text; the overdue
  worklist is `status = Planned AND due_at < asOf` (lexicographic on
  the ISO text — correct for the stored format); completion stamps
  `completed_at` and keeps the result notes.

## 5. Comparison tables (acceptance gate)

### 5.1 Lead

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 057 |
|---|---|---|---|---|---|
| Lead as separate entity | Rep/visit oriented; no public lead master | Rep management focus; no public lead master | ✅ Lead (person + organisation, source, campaign, territory) | ✅ `crm.lead` (lead/opportunity flag) | ✅ `crm_leads` (C1) with organisation, source, campaign |
| Lead statuses | — | — | Open / Working / Qualified / Converted… | Pipeline stages | ✅ New / Working / Qualified / Converted / Unqualified |
| Convert → customer + opportunity in one action | — | — | ✅ one action | ✅ create-or-link customer | ✅ one transaction → party (unified master) + opportunity + both links |
| Duplicate party store on conversion | — | — | No (Customer is the master) | No | ✅ No — inserts into `parties` itself; legacy stores stay dropped (061) |
| Assignment | Rep routes/targets | Rep + visits | Sales person / territory | Owner / team | ✅ CORE sales rep by code (reps stay core) |

### 5.2 Opportunity & pipeline

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 057 |
|---|---|---|---|---|---|
| Pipeline stages | Not a headline module | Not a headline module | Status pipeline (Open → Quotation → Converted/Lost/Closed) | ✅ Kanban stages with default probability, fully configurable | ✅ Configurable stages (Open/Won/Lost) with default probability, seeded design pipeline |
| Probability / expected value / expected close | — | — | ✅ probability + expected closing | ✅ expected revenue + expected closing + probability | ✅ all three; weighted = amount × probability |
| Stage movement audited | — | — | Communications/timeline | Chatter | ✅ `crm_opportunity_stage_history` row per move (incl. creation) |
| Lost requires a reason | — | — | ✅ lost reasons recorded | ✅ lost reason required | ✅ `CRM-9006` + seeded reasons + notes |
| Board | — | — | List/report | ✅ Kanban (drag) | Grouped-list board + Advance/Regress buttons (C2); drag-and-drop deferred |
| Direct opportunity on an existing customer | — | — | ✅ | ✅ | ✅ party picker over the unified master |

### 5.3 Activities & quotations

| Capability | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 057 |
|---|---|---|---|---|---|
| Follow-up activities with deadlines | Visit plans (rep side) | Visit routes | ToDo / communications on the record | ✅ Activities (call/meeting/email) with deadlines + plans | ✅ Call/Meeting/Email/Task, due + completion + result, against lead/opp/party (exactly one) |
| Overdue worklist | — | — | ToDo lists | ✅ activity views/badges | ✅ Overdue filter + overdue counts in the worklist status bar |
| Opportunity → quotation | — | — | ✅ Quotation (standard sales document) | ✅ New Quotation (multiple/revisions normal) | ✅ Existing InvoiceWin Quotation kind (C3); multiple quotations per opportunity, each linked |
| Quotation → order → invoice chain | — | — | ✅ linked chain | ✅ sale order flow | ✅ Step-9 chain (`document_links` QUO→SO→…), CRM links order + invoice back onto the opportunity |
| Quotation side effects before conversion | — | — | None until order | None until order confirmed | ✅ None (Step 5): no GL / party ledger / stock — proven |

## 6. Pillars checklists

Legend: ✅ proven (harness/test/build evidence in §7) · 🪟 window code
complete but WPF runtime owed on Windows · GAP = listed in §8.

### CrmLeadWin
| Pillar | State |
|---|---|
| UI | ✅ code — list + full card (all lead fields), bottom bar (New/Save/Convert/Delete/Refresh/Close), Orchid tint |
| DB | ✅ `crm_leads` (057); every editor field persists (round-trip (e)) |
| Linking | ✅ rep picker from CORE `sales_reps`; conversion links stamped both ways; RibbonNav `CRM.Leads` + MWin button |
| Posting | ✅ conversion = the document effect: real party + opportunity through the existing master/engine paths in one transaction (no private GL/stock, ever) |
| Persistence | ✅ save/delete/convert all transactional via CrmService |
| Downstream | ✅ converted party is invoiceable immediately (unified master + ledger_id); opportunity appears on the board |

### CrmOppWin
| Pillar | State |
|---|---|
| UI | ✅ code — list + card + stage stepper + history grid + documents grid + link/open actions, Violet tint |
| DB | ✅ `crm_opportunities` + history + documents (057); card fields round-trip (e) |
| Linking | ✅ party picker (unified master), lead origin kept, stage/reason combos from company config, `CRM.Opportunities` route + button; opens quotations in InvoiceWin |
| Posting | ✅ quotation creation = effect-free document through the existing document store (Step 5); onward conversion is the step-9 chain's posting, not CRM's |
| Persistence | ✅ stage moves only via MoveStageAsync (history complete); lost reason enforced |
| Downstream | ✅ documents tab shows live doc status/totals; board totals include the opportunity |

### CrmPipelineWin
| Pillar | State |
|---|---|
| UI | ✅ code — grouped-list board (C2) with per-stage raw + weighted footers, stage + lost-reason configuration panel, Indigo tint |
| DB | ✅ reads `GetPipelineAsync`; stage/reason edits persist to `crm_stages`/`crm_lost_reasons` |
| Linking | ✅ Advance/Regress move through the same MoveStageAsync as the editor; Open launches CrmOppWin focused on the record |
| Posting | n/a by design — the board moves pipeline state (audited in history); it writes no documents |
| Persistence | ✅ every move lands in `crm_opportunity_stage_history` |
| Downstream | ✅ open weighted total is the funnel headline; Won/Lost columns close the pipeline picture |

### CrmActivitiesWin
| Pillar | State |
|---|---|
| UI | ✅ code — worklist with Open/Overdue filters + editor (target kind + target pickers), Coral tint |
| DB | ✅ `crm_activities` (057); subject/type/due/target/assignee/result all persist |
| Linking | ✅ targets resolve against leads/opportunities/parties of the same company; `CRM.Activities` route + button |
| Posting | n/a by design — activities carry no GL/stock effect in any compared ERP |
| Persistence | ✅ complete stamps `completed_at` + result in one transaction; completed rows leave the overdue list (c) |
| Downstream | ✅ overdue surface = the follow-up worklist the design requires; per-record activities readable via `GetActivitiesForAsync` |

## 7. Proof (headless, SQLite, real services)

Driver: `~/workspace/research/phaseb-057/crm57_harness.py`
(C# harness in `~/workspace/research/phaseb-057/harness/`) — migrates
with the real `tools/SqlMigrator`, then runs the real
`CrmService`/`CrmRepository` + the real step-9 chain services.
**Result: DRIVER ALL PASS / HARNESS ALL PASS** (57 checks).

- **(a) Conversion:** lead → qualify → convert → party row in `parties`
  with the lead's name/org/email/phone/mobile/address and a fresh
  `ledger_id`; lead `Converted` with both links; exactly one party row;
  `customers`/`suppliers` tables absent (no duplicate store); second
  conversion → `CRM-9001`; CORE rep offered by reference only.
- **(b) Stage walk:** New→Qualified→Proposition→Negotiation with
  weighted values 1000 → 2500 → 5000 → 7500 on amount 10,000 (10/25/50/
  75%), column totals equal at each stage; 4 history rows (creation +
  3 moves) with chained from/to probabilities; Lost without reason →
  `CRM-9006`; with reason → probability 0, reason + notes stored.
- **(c) Activities:** due-yesterday activity appears in
  `GetOverdueActivitiesAsync`; completing it removes it and leaves it
  Done with its result notes.
- **(d) Quotation chain:** quotation Draft `QUO-…` total 50 for the
  opportunity's party; **zero** `party_ledger` / `journal_entries` /
  `material_stock_movements` rows after creation **and** after chain
  posting; chain converts to a sales-order Draft (total carried,
  `document_links` QUO→SO written); CRM links order + invoice;
  converted invoice posts GL and deducts stock 100 → 98 — effects begin
  at conversion, exactly per Step 5/9.
- **(e) Round-trip:** fully populated lead and opportunity reload
  field-by-field equal (incl. party `ledger_id` resolution and the
  12,345.67 × 55% = 6,790.1185 weighted value).
- **(f) Migration:** first run `APPLY 057_crm`, second run
  `SKIP 057_crm` (idempotent double-run), exactly one ledger row, 7
  tables, 8 permissions; late arrival on a 064 database re-applies
  cleanly and grants all 8 permissions to an existing
  `ROLE_ADMINISTRATOR` (roles are app-seeded; a migrator-only DB has
  none — same as 053–056).

Kern.Tests: `CrmPipelineTests` (6 facts mirroring (a)–(e)) compile
green; **execution is owed on Windows** (net8.0-windows testhost, the
standing limitation of every phase).

## 8. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | ✅ **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | ✅ exit 0; all four CRM windows listed as BaseWin/central |
| Lang parity | ✅ `Crm.*` = 95 leaf keys, identical sets in En/Ar/Fr/Tr; Errors `CRM-9001..9006` in all 4 (repo-wide pre-existing extras — Ar +90, Fr/Tr +9 vs En — are unchanged from the base branch and not CRM keys) |
| Migrator idempotent double-run | ✅ APPLY then SKIP (harness (f)), incl. late arrival |
| No Arabic outside Ar.json | ✅ new/changed code + SQL files contain no Arabic (stage/reason seed `name_ar` values are empty strings; Arabic lives only in the Ar catalogs) |
| WPF runtime | ⚠️ **Owed on Windows** — windows compile and follow the proven BaseWin patterns; visual/RTL pass not possible from Linux |
| Live PostgreSQL | ⚠️ **Owed** — PG migration + repository are provider-mirrored after 055/056; no live PG run in this environment |

## 9. Owner flags / gaps

1. **Entitlement gating is NOT built** (owner postponement): no
   `EnsureModule("CRM")` call exists anywhere; CRM is RBAC-only. When
   licensing lands, the gate hooks are `CrmService` entry points +
   the already-registered `ModKey.Crm` and `CRM.*` route keys.
2. **Dedicated CRM reports are a GAP:** funnel-by-stage beyond the
   board totals, win-rate by rep, and stale-opportunity (no activity
   in N days) report windows from design §4.2 were not built; the
   board + worklist cover the operational surfaces in v1.
3. **Drag-and-drop kanban** remains deferred per C2 (grouped-list
   board shipped).
4. **Quotation company-key note (C3):** CRM quotations are written
   Guid-keyed so the step-9 chain can convert them; `InvRepo.NextDocNo`
   counts only code-keyed rows, so a company mixing hand-made
   InvoiceWin quotations and CRM quotations could see both families
   number from their own sequences (numbers stay unique per
   (company, kind, doc_no) only within each key namespace — the
   pre-existing dual-key reality of the document tables, surfaced here
   rather than papered over).
5. **Converted-party code** is auto-assigned (`CUST-nnnn`); if the
   owner wants a dedicated CRM conversion numbering series or a code
   prompt at conversion time, that is a small follow-up in
   `CrmLeadWin`/`CrmRepository.NextPartyCodeAsync`.
6. **WPF + live PG verification owed** (see §8) — including the
   Windows test-suite run for `CrmPipelineTests`.
