# Build Phase B3 — Quality Management (migration 056)

Branch: `build/phaseb-056-quality` (from `build/phaseb-055-budgets` @
`dd85b0d`). Design source: `~/workspace/research/phase-b-design.md` §5
(Quality) and §7 (decision list). This document maps design → built,
records the Q1–Q4 decisions taken, the gate state machine, the schema,
the comparison tables, the per-window pillars checklists, the headless
proof, the gates, and the owner flags.

## 1. What was built

| Layer | Artifact |
|---|---|
| Migration | `dba/mig/sqlite/056_quality.sql`, `dba/mig/postgresql/056_quality.sql` — quality schema + per-company gate mode + material/warehouse columns + `QUALITY.*` permissions (349–357) |
| Domain | `Entities/Quality.cs` (parameters, templates, inspections, readings, alerts, constants), `Interfaces/IQualityRepository.cs`, error codes `INV-6011..6016` (`ErrorCodes.cs`) |
| Gate | `Infrastructure/QualityGate.cs` — in-transaction gate used by the posting paths (BudgetGuard pattern): receipt diversion, completion release/disposition, sale draw, outgoing check, reading evaluation |
| Data/service | `Infrastructure/QualityRepository.cs`, `Application/Services/QualityService.cs` |
| Posting hooks | `StkPost.ApplyDocument` (diversion + outgoing check + quarantine draw), `MfgOrderRepository.PostAsync` (InProcess diversion), `CommercialDocumentRepository` DLV path (outgoing check) |
| Windows | `Views/QcInspectWin.cs`, `Views/QcTemplateWin.cs`, `Views/QcAlertWin.cs`, `Views/Reports/QcSummaryWin.cs` |
| Wiring | `AppHost.Quality` (+ `AppServiceHost` pass-through, rebound on company switch), `PermCat.Quality`, `RibbonNav` routes `Inventory.QualityInspections/QualityTemplates/QualityAlerts/QualitySummary`, audit script knows `AppHost.Quality` |
| i18n | `Quality.*` (92 keys incl. `Col.*`) + `Reports.QualitySummary` in En/Ar/Fr/Tr; Errors `INV-6011..6016` in all 4 catalogs + `docs/ERROR_CATALOG.md` |

## 2. Design vs built — with the Q decisions

| Design element (§5) | Built | Notes |
|---|---|---|
| `quality_parameters` (code, names, type, unit) | ✅ as designed | Types: `Numeric` / `Value` / `PassFail` |
| `quality_templates` + `quality_template_lines` (criteria, mandatory flag, sample size) | ✅ as designed | Line criteria: `min_value`/`max_value` (Numeric), `accepted_value` (Value); sample size on the template |
| `quality_inspections` (type, ref document, material, batch, warehouse pair, quantities, status, disposition, inspector, due date) | ✅ as designed + `template_id`, `total_qty`, `party_id` | Status set is the design's `Draft/Accepted/PartiallyAccepted/Rejected`; `party_id` powers the supplier reject-rate summary |
| `quality_inspection_lines` + readings (up to 3 readings per characteristic) | ✅ as designed | Lines **snapshot** the template criteria (`min_value`/`max_value`/`accepted_value` copied at creation) so later template edits never rewrite inspection history; result per line `Pass/Fail` with an explicit override flag |
| `quality_alerts` (root cause, corrective/preventive action, owner, status) | ✅ as designed + `alert_type` + `message` | Alert types: `FailedInspection`, `GateWarning`; closing requires root cause + corrective action (Odoo rule) |
| **Q1 — gate default: Warn** | ✅ **Taken: Warn**, per-company | Stored in a dedicated `quality_settings` table (the schema has no generic per-company settings table — recorded adaptation). Settable from QcInspectWin (`QUALITY.TEMPLATE.EDIT`) |
| **Q2 — rejected stock home: dedicated quarantine warehouse** | ✅ **Taken** | `warehouses.is_quarantine`; one quarantine warehouse per company, auto-created by the gate on first use ("Quarantine"); never hand-picked as a document warehouse by the gate logic |
| **Q3 — formula criteria: DEFER** | ✅ **Taken: deferred** | Numeric / Value / PassFail only; no formula engine |
| **Q4 — outgoing inspection: INCLUDE** | ✅ **Taken: included** | Same Stop/Warn semantics on sales documents (`StkPost`) and chain deliveries (DLV): Stop refuses without an accepted outgoing inspection for the document (`INV-6013`); Warn posts and auto-creates the pending outgoing inspection + GateWarning alert; a REJECTED outgoing inspection blocks in **both** modes (`INV-6011`) |
| Quarantine warehouse concept | ✅ | Receipts of QC materials post into it through the valuation engine, so balances/batches/serials/cost are first-class 054 state |
| Receipt diversion (purchase / stock-in / MFG completion) | ✅ | `StkPost` inbound kinds + `MfgOrderRepository` completion (InProcess type). A Draft inspection is auto-created in the same transaction (idempotent per document+material+batch) |
| PASS release through the valuation engine | ✅ | Value-exact transfer pair (TRANSFER_OUT at the engine's computed cost + TRANSFER_IN with `ReceiptCostAmount` + `SourceMovementId`), so batch balances move and serials return to `InStock` at the destination under their original identities |
| Partial pass | ✅ | Accepted/rejected split per inspection; batch slice released under the same `batch_id`; status `PartiallyAccepted` |
| FAIL dispositions | ✅ | `ReturnToSupplier` (engine issue `QCR` out of quarantine), `Scrap` (engine issue `QCS`; serials marked `Scrapped`), `UseAsIs` (release), `Rework` / `Quarantine` (stay in quarantine). Disposition + quantities recorded on the inspection; a failure alert is raised for any rejected quantity |
| Alerts: failed + near/overdue | ✅ | Failed inspections and gate warnings are alert rows; the pending worklist carries a due date (receipt date + 3 days) and QcInspectWin shows the overdue count; `GetOverdueInspectionsAsync` feeds it (ExpiredWin pattern) |
| Quality summary / reject rate | ✅ | `QcSummaryWin` (RptBase): inspections, total/accepted/rejected qty and reject-rate % grouped by material or by supplier |
| CRM entitlement gating (design §5.6) | N/A | Quality is core (not the CRM module); no entitlement gate applies |

### Gate semantics — how the design's Stop/Warn meets the mandate

The mandate requires quarantine-first in all modes ("the received
quantity lands in QUARANTINE … hard gate = stock stays quarantined
until PASS; soft gate = design semantics"). The design's Warn mode
("warn but allow use") is therefore realized at the **point of use**,
which is also where the design puts the mode's teeth:

- **Both modes:** QC receipts divert to quarantine + Draft inspection.
  Quarantined stock is never part of the sellable balance.
- **Stop:** a sale whose quantity is only available in quarantine is
  refused (`INV-6012`); a delivery of an outgoing-QC material without
  an accepted outgoing inspection is refused (`INV-6013`); a rejected
  outgoing inspection refuses in both modes (`INV-6011`).
- **Warn:** the sale proceeds, drawing the shortfall from quarantine
  through the engine, and a `GateWarning` alert records the bypass;
  outgoing deliveries post and auto-raise the pending inspection.
- **Completion is the only release path** in both modes.

## 3. Gate state machine

```
Receipt (QC material) ──► stock @ quarantine warehouse (engine)
                      └─► Inspection [Draft] (due = receipt + 3d)
Draft ──SaveResults──► Draft (readings stored; auto result per line)
Draft ──Complete(accepted = total)──► Accepted ──► release pair ──► stock @ destination
Draft ──Complete(0 < accepted < total, disposition)──► PartiallyAccepted
        ├─► accepted part released (same batch/serial identity)
        └─► rejected part per disposition + FailedInspection alert
Draft ──Complete(accepted = 0, disposition)──► Rejected
        └─► rejected part per disposition + FailedInspection alert
Dispositions: ReturnToSupplier = QCR issue out of quarantine
              Scrap            = QCS issue out of quarantine (serials → Scrapped)
              UseAsIs          = released like a pass
              Rework/Quarantine= stays in quarantine (no movement)
Outgoing: delivery ──► CheckOutgoing: Accepted exists → pass
                       Rejected exists → INV-6011 (both modes)
                       none + Stop → INV-6013
                       none + Warn → post + Draft Outgoing inspection + alert
Sale: sellable ≥ qty → normal issue
      shortfall in quarantine + Stop → INV-6012
      shortfall in quarantine + Warn → split issue (sellable part +
      quarantine draw) + GateWarning alert
```

Invariants: completion validates accepted + rejected = inspected qty
(`INV-6015`), a rejected part requires a valid disposition, completed
inspections are immutable (`INV-6014`), mandatory template lines must
be judged before completion (auto-evaluation runs in-transaction;
unjudged mandatory lines block with `INV-6014`).

## 4. Schema (migration 056)

New tables: `quality_settings` (company PK, `gate_mode`),
`quality_parameters`, `quality_templates`, `quality_template_lines`,
`quality_inspections`, `quality_inspection_lines`,
`quality_inspection_readings`, `quality_alerts`.
Column adds: `materials.qc_template_in_id`, `materials.qc_template_out_id`,
`materials.qc_outgoing_required`, `warehouses.is_quarantine`.
Permissions: `QUALITY.PARAM.VIEW/EDIT`, `QUALITY.TEMPLATE.VIEW/EDIT`,
`QUALITY.INSPECT.VIEW/EDIT/COMPLETE`, `QUALITY.ALERT.VIEW/EDIT`
(Admin: all; User: views + edits + complete; Viewer: views).
Company keying follows the stock world (company **code**), consistent
with 054/055. Both providers; idempotent (SQLite `INSERT OR IGNORE` +
column guards; PG `ON CONFLICT` + `ADD COLUMN IF NOT EXISTS`).

## 5. Comparison tables (acceptance gate)

### QcInspectWin — inspection worklist + entry + completion

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Quality Inspection) | Odoo (Quality Checks) | ShouTech 056 |
|---|---|---|---|---|---|
| Trigger | Manual QC on goods-receipt | QC on receipt/production | Linked to Receipt/Delivery/Job Card, template-driven | Control points auto-create checks on operations | Auto-created on QC receipt (In), MFG completion (InProcess), delivery (Out, Warn); manual Outgoing creation |
| Result entry | Pass/fail per item | Measured values | Readings per specification with min/max, status auto | Measure / pass-fail check types | Up to 3 readings per characteristic; Numeric band / accepted value auto Pass/Fail; manual override is flagged |
| Quantities | Accept/reject qty | Accept/reject qty | Accepted + rejected qty fields | Whole-check pass/fail | Accepted + rejected split with PartiallyAccepted status |
| Rejection handling | Return / scrap vouchers | Return note | Rejected qty to a rejected warehouse | Alerts + corrective actions | Disposition on the inspection (Return/Scrap/Rework/UseAsIs/Quarantine) with the stock effect posted by the engine |
| Gate | Receipt blocked options | Optional | Stock blocked until inspection (rejected warehouse) | Control point can block the operation | Quarantine warehouse + Stop/Warn per company |
| Alerts | — | — | Review/action workflow | Quality Alerts with root cause | Quality alerts: close requires root cause + corrective action |

### QcTemplateWin — parameters, templates, material assignment

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Quality Goal / template) | Odoo (Control Points) | ShouTech 056 |
|---|---|---|---|---|---|
| Characteristics library | Test definitions | Test definitions | Item Quality Inspection Template parameters | Control point instructions | Quality parameters (Numeric/Value/PassFail, unit) |
| Template per item | Per item card | Per item | Template linked to item | Control point per product/operation | Templates by inspection type; materials assigned incoming/outgoing templates + QC flags in one grid (MatCard keeps its QC-required flag) |
| Tolerances | Min/max | Min/max | Min/max per reading row | Tolerance on measure checks | Min/max band or accepted value per line; mandatory flag; sample size |
| Formula criteria | — | — | Formula-based criteria exist | — | Deferred per Q3 |

### QcAlertWin — quality alerts

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext (Quality Review/Action) | Odoo (Quality Alerts) | ShouTech 056 |
|---|---|---|---|---|---|
| Source | — | — | Failed reviews | Failed checks auto-create alerts | Failed inspections + gate warnings auto-created |
| Workflow | — | — | Reviews with resolution | Root cause, corrective + preventive actions, owner, stages | Root cause + corrective required to close; preventive action + owner fields; Open/InProgress/Closed |

### QcSummaryWin — inspection summary / reject rate

| Aspect | Al-Ameen V10 | Phenix v10 | ERPNext | Odoo | ShouTech 056 |
|---|---|---|---|---|---|
| View | QC reports | QC reports | Quality Inspection summary reports | Quality check/alert analysis | Inspections, total/accepted/rejected qty, reject-rate % by material or supplier, RptBase totals/print |

## 6. Pillars checklists

### QcInspectWin
- UI ✅ BaseWin, Jade schedule tint, bottom command bar, shell context via AppHost (audit row present), RTL via central LangDirSv.
- DB ✅ reads/writes `quality_inspections/lines/readings`, `quality_settings` — all persisted; no UI-only fields.
- Linking ✅ ribbon route `Inventory.QualityInspections`; inspections link to source documents (kind/id/no), materials, batches; Alerts button cross-opens QcAlertWin.
- Posting ✅ completion posts the real engine movements (release pair / QCR / QCS) in one transaction with the inspection update.
- Persistence ✅ readings (≤3/line), results + override flag, completion quantities/disposition/inspector all saved via QualityService.
- Downstream ✅ balances, batch/serial state, alerts and the summary report all consume the same rows (harness-proven).

### QcTemplateWin
- UI ✅ BaseWin, Peridot tint, bottom bar. DB ✅ parameters/templates/lines/material flags fully persisted. Linking ✅ ribbon route; templates referenced by materials and snapshotted by inspections. Posting — N/A (definitions window; its effect flows through the gate). Persistence ✅ incl. soft-delete of lines on template save. Downstream ✅ gate + inspection creation consume definitions (harness-proven).

### QcAlertWin
- UI ✅ BaseWin, Garnet tint, bottom bar. DB ✅ alert workflow fields persisted. Linking ✅ ribbon route + open-from QcInspectWin; alerts link inspection + document. Posting — N/A. Persistence ✅ close rule enforced in the service. Downstream ✅ alert counts surface in QcInspectWin's status line (overdue) and the worklist.

### QcSummaryWin
- UI ✅ RptBase standard (filters, totals, print). DB ✅ aggregate over `quality_inspections` (+ parties for supplier names). Linking ✅ ribbon route `Inventory.QualitySummary`. Posting — N/A. Persistence — N/A (read model). Downstream ✅ reject-rate by material/supplier proven in the harness (20/50 = 40%).

### Touched windows/paths
- `StkPost` (all document windows using it): diversion + gates — PROVEN at the gate-sequence level by the harness/tests (the harness performs the exact calls `ApplyDocument` makes, in the same order, against the real engine).
- `MfgWin` path (`MfgOrderRepository.PostAsync`): QC finished goods divert + InProcess inspection in the posting transaction.
- Chain DLV (`CommercialDocumentRepository`): outgoing check per line.

## 7. Proof (headless, SQLite)

`~/workspace/research/fix-step56/quality56_harness.py` — migrates a
fresh DB twice with the real SqlMigrator, then runs the C# harness
(real QualityGate + StockValuationEngine + QualityRepository/Service).
**Result: ALL PASS** (driver + 60 harness checks), covering:

- (a) QC receipt → quarantine balance 100, sellable 0, Draft inspection auto-created with template lines snapshotted;
- (b) PASS → sellable 100, quarantine 0, stock value preserved (1000); partial pass on a batch → 30 released under the same batch id, 20 scrapped, one failure alert, `PartiallyAccepted`; serials InStock at quarantine → same serials InStock at destination;
- (c) FAIL ReturnToSupplier → quarantine drained, disposition recorded, sellable untouched; Quarantine disposition keeps stock quarantined;
- (d) Stop → sale refused `INV-6012`, stock untouched; Warn → sale draws from quarantine + GateWarning alert; over-sale still fails as insufficient; outgoing gate: Stop `INV-6013`, Warn posts + pending Outgoing inspection, accepted inspection unblocks Stop;
- (e) non-QC receipt/sale unchanged (no diversion, no inspection);
- (f) seeded overdue Draft inspection returned by the overdue query;
- (g) migrator double-run idempotent (APPLY then SKIP, one
  `schema_migrations` row), schema objects present, late arrival
  (056 applied onto a 064 database) works;
- alert close rule and summary reject-rate (40%) checks.

`tests/Kern.Tests/QualityGateTests.cs` — 10 xUnit facts mirroring the
harness scenarios (execution owed on Windows, see flags).

## 8. Gates

| Gate | Result |
|---|---|
| Full build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** (Qc* rows present, 0 window-level direction pins) |
| Lang parity | **0 missing keys** in Ar/Fr/Tr vs En (pre-existing extras untouched) |
| Errors catalogs | INV-6011..6016 in En/Ar/Fr/Tr + `docs/ERROR_CATALOG.md` |
| Migrator idempotent double-run | ✅ (driver gate g) |

## 9. Owner flags / gaps

1. **WPF runtime owed** — the four windows compile and follow the
   BaseWin/RptBase patterns, but no Windows run has exercised them
   (open, RTL, save/complete clicks). Kern.Tests execution is likewise
   owed on Windows (net8.0-windows testhost).
2. **Live PostgreSQL owed** — the PG migration follows the 053–055
   patterns (`ADD COLUMN IF NOT EXISTS`, `ON CONFLICT`, uuid ids) but
   has not run against a live PG instance from this Linux machine.
3. **ReturnToSupplier document gap (bounded)** — the disposition posts
   the stock-out effect (`QCR` issue) and records the disposition, but
   does **not** auto-create a purchase-return draft document; the
   returns document flow does not exist yet. Flagged per the mandate.
4. **MFG order cancel vs QC-diverted production (bounded)** — cancel
   reads on-hand at the order's warehouse; for a QC-diverted order the
   stock sits in quarantine, so cancel fails honestly with the
   engine's insufficient-stock error until the inspection completes.
   No silent corruption; a QC-aware cancel is future work.
5. **Warn-mode draw with explicit batch/serial captures** — when a
   sale line carries explicit batch/serial selections they are honored
   exactly (no quarantine draw split for that line); the draw applies
   to auto-allocated lines, which is the standard invoice/POS path.
6. **MatCard template pickers** — the material card keeps its existing
   `quality_check_required` checkbox (same flag the gate reads);
   template assignment lives in QcTemplateWin's material grid.
   Extending the MatCard XAML with template combos is a possible
   follow-up, not a functional gap.
7. **Decisions taken** (flagged per the brief): Q1 = Warn default
   (per-company `quality_settings` table — adaptation recorded in §2);
   Q2 = dedicated quarantine warehouse; Q3 = formulas deferred;
   Q4 = outgoing gate included. Gate-mode reconciliation (quarantine
   in both modes, mode bites at point of use) is documented in §2.
