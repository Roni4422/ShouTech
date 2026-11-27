# R4 study — definition sets (phase-gate study)

Owner phase-gate rule (2026-11-24 16:28): study first, builds
follow. Scope: the 23 ribbon entries still routed to the generic
DefManagerWin over the DefSets registry (21 registered sets;
POS.DefinePOS left the generic path in P1; PosTerminals' registry
entry remains but is unrouted).

## Method

Each set was measured three ways: (1) registry columns vs the
backing TABLE's columns (migration 036 + 001/013 legacy tables);
(2) downstream consumption — does any engine outside
DefManagerWin read the table? (3) benchmark field depth
(ERPNext/Odoo masters, Al-Ameen classic definition windows,
Phenix v10 definition screens — evidence: ERPNext/Odoo strong,
Al-Ameen classic help strong for its generation, Phenix
marketing-level).

## FORM facts (counted, one generic editor)

DefManagerWin is ONE editor for all sets: rows grid (zebra,
Jade tint) + one editor field per registry column + bottom bar
with 6 buttons (New, Save, Delete, Activate toggle, Refresh,
Close) + slim status bar. Harmony verdict: perfect consistency
across sets (the benchmarks' per-set bespoke windows vary
wildly); the cost of the generic form is that lookup columns
render as validated text — the editor has NO picker control
today (IsLookup is metadata only). Named gap, recorded below.

## Per-set findings

Registry columns MATCH table columns for every 036 set — the
registry under-exposes nothing. The differences are consumption
and two field gaps:

| Set (entries) | Fields | Consumed by | Verdict |
|---|---|---|---|
| PaymentMethods (2) | 12 | POS/voucher tender paths (1 file) | COMPLETE |
| PartyTargets / RepTargets (2) | 4 | targets engine (17 files) | COMPLETE |
| MaterialDiscounts / CustomerDiscounts / PosDiscounts (3) | 3 | pricing paths (3 files) | COMPLETE (pct + min-qty is the engine's actual rule shape) |
| CustomerPrices (1) | 3 | pricing resolver (3 files) | COMPLETE |
| Drivers (1) | 6 | delivery (1 file) | COMPLETE |
| MaterialSpecs (2 entries) | 5 | material card paths (2 files) | COMPLETE |
| UnitsOfMeasure (1) | **1 (code only)** | 5 files | **THIN — BUILD**: units have no names anywhere; benchmarks name their units (ERPNext UOM name, Al-Ameen unit names). Migration 121 adds name_en/name_ar; registry exposes them |
| VoucherTypes / SecurityTypes / PaymentTypes / InvoiceDefinitions / OrderDefinitions (5) | 4 | **nothing reads doc_type_definitions** | FIELD-ADEQUATE, CONSUMER-MISSING: document kinds/numbering are engine-fixed (InvDocKind + repository numbering). A document-type ENGINE (user-defined types driving numbering/accounts) is a module-phase build, not a definition-set completion — recorded as a named gap; sets stay (b) |
| Areas (1) | 4 | nothing reads areas yet | Master data ahead of its consumer (delivery zones); field-adequate (parent_id is a raw id — picker gap below). Stays (b) pending the delivery consumer |
| Devices / DeviceRoutes (2) | 5–6 | nothing reads them yet | Master data for the handheld/rep module; field-adequate vs the domain (device↔rep↔area↔weekday↔order). Stay (b) pending that module's consumer |
| DeliveryPrices (1) | 3 | nothing reads delivery_prices yet | Field-adequate (area + price + min order); consumer = delivery pricing in the sales flow — named gap. Stays (b) |
| CustomFields (1) | 6 | nothing renders custom fields yet | Field-adequate as a DEFINITION registry; the rendering engine (fields appearing on cards/documents) is a platform build — named gap. Stays (b) |

## Builds this phase (from the study)

1. **UnitsOfMeasure names** — migration 121 (both dialects):
   name_en + name_ar on units_of_measure; DefSets registry
   gains both columns. Additive: all 5 consumers key on
   uom_code and are untouched.
2. Everything else: NO cosmetic field inflation. Adding
   columns no engine reads would repeat the exact "data
   island" pattern this audit exists to expose.

## Named gaps (recorded, not papered over)

- Document-type engine (user-defined document types with
  numbering/accounts) — module phase; the five DocTypes sets
  are its definition surface, ready when it is built.
- Picker-backed lookup editing in DefManagerWin (accounts,
  areas, materials as real pickers instead of validated text) —
  editor-platform improvement, commercial/module phase.
- Consumers for Areas / Devices / DeviceRoutes /
  DeliveryPrices / CustomFields rendering — belong to the
  delivery, handheld and platform modules respectively.
- (b) accounting after R4: of the 23 DefManager entries, 12
  flip to (a) (PaymentMethods ×2 entries, PartyTargets,
  RepTargets, Discounts ×3, CustomerPrices, Drivers,
  MaterialSpecs ×2 entries, UnitsOfMeasure) and 11 stay (b)
  with named missing consumers (DocTypes ×6 entries, Areas,
  Devices, DeviceRoutes, DeliveryPrices, CustomFields).
  Audit (b) total: 29 → 17 (11 definition entries + the 6 R5
  thin windows).

## Power / ease verdict for the phase

MORE POWERFUL where it counts: the sets that drive money
(pricing, discounts, targets, payment methods) are complete
and consumed. EASIER: one consistent editor everywhere. The
honest trailing edge is consumer-less master data — named
above with its owning module, not hidden.
