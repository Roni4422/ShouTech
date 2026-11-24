# Invoice family — Phenix reference pass + posting rule (INV1, 2026-11-24)

Owner reference: Phenix invoice window photo (Arabic RTL) + two rules
(16:06) and the tint reinforcement (16:08). Structure/content modelled
on the reference; visuals stay on the modern central system.

## RULE 2 — stock entry/exit post INVENTORY ONLY (FIXED DEVIATION)

Audit found the deviation exactly as the owner suspected:
`InvPostingPolicy` gave StockIn/StockOut `Gl: true`, and
`InvRepo.BuildGlLines` carried mappings for them (StockIn: Dr
Inventory / Cr Purchases; StockOut: Dr COGS / Cr Inventory) — posting
a stock entry/exit created a journal voucher.

Fix (at the single enforcement point, `InvPostingPolicy.For`):
StockIn/StockOut are now `Effects(false, false, true, false, false,
false)` — stock only, like OpeningStock. The two BuildGlLines cases
were removed (the builder is never called for these kinds now).
Quantities and stock VALUES still update through StkPost exactly as
before — only the GL voucher is absent. `journal_doc_no` stays empty
for these documents. Reversal flows through the same policy, so it
is symmetric. The Kern.Tests policy-matrix test now pins
`StockIn/StockOut.Gl == false` (execution Windows-owed). All other
invoice types keep their normal GL posting, unchanged.

## RULE 1 — one design for all invoices

Already true structurally: ONE window (InvoiceWin) serves every
InvDocKind (sales, purchase, both returns, stock entry/exit,
transfers routed to their engine, POS, quotations). Against the
reference photo, per element:

| Reference element | ShouTech state |
|---|---|
| Record navigator + record no. | EXISTS (CreateRecordNumberBar + NavigateDocuments) |
| Header: type, date, customer code+name, warehouse, cost center, registration account, notes, confidentiality | EXISTS (V2 Phenix modelling; confidentiality = header classification field) |
| Quick search (F3) above the grid | **BUILT in INV1**: F3 focuses the quick-search box at the grid strip's start side; Enter runs the same material-entry pipeline as grid typing (unique/partial match fills a new line at once; no match opens the material directory) |
| Grid: numbered rows, zebra | EXISTS (central ShowRowNumbers + per-window tint — InvoiceWin Mint; NOT flattened to the reference's single blue, per the owner's tint rule) |
| Grid columns: material, qty, unit, price, total, expiry, line notes, serial, discount | First five in the owner's mandated order (R2a). **Line notes BUILT in INV1** (migration 120, both line tables; optional-column machinery). Expiry/serial are NOT inline columns: batch/serial capture happens at posting through the dedicated picker, persisted as stock_movement_allocations — functionally deeper than inline fields (per-batch expiry, per-unit serials, expired-batch override permission); kept, recorded as a deliberate presentation difference |
| Totals block: column totals, cash discount, net, tax, paid, remaining | EXISTS (V2) |
| Collect (تحصيل) action | **BUILT in INV1**: Collect button (posted sales invoices only) opens PayRecWin — the canonical receipt + allocation path — in context |
| Audit line "recorded by <user> at <datetime>" | **BUILT in INV1**: InvDoc.PostedBy/PostedAt loaded from the header; the footer shows "Posted by {0} at {1}" for posted documents |
| Bottom action buttons | EXISTS (bottom bar only, V5) |

## Grid refinement (owner, 16:13)

- Row numbers sit on the START side and mirror with FlowDirection
  (central DataGrid row headers: left in LTR, right in RTL) —
  conformed centrally, no per-window work.
- DEFAULT VISIBLE COLUMNS ARE SIX: material, quantity, unit, unit
  price, line total, line notes. All other optional columns
  (code, discount, tax code, tax rate, margin, warehouse) now start
  hidden; previously saved per-user layouts still win on open.
- The chooser button sits at the END of the grid strip (mirrors);
  the chooser sets visibility AND placement (Move Up/Down in
  ColChsWin; the resulting order is persisted per user via WinPref
  ColumnRec.Order and restored on open — verified in InvoiceWin,
  JrnWin, CshWin callers). Optional-column DisplayIndex base moved
  5 → 6 for the six-column core.

## Tint audit (owner, 16:08)

Mechanism: BaseWin.ScheduleTintName → window-local
TableAlternateRowBrush override; palette tokens ScheduleTint{Name}Brush
in Themes/VisuId.xaml. Findings before the fix: Peach was claimed by
9 windows, 17 other tints were shared by 2–3 windows each, and 24
schedule windows (incl. this branch's BcdDesWin and the purchase
family) claimed none and fell back to the generic default.

Fix: 53 new muted pastel tokens added to the central palette (99
total); every duplicate reassigned (canonical keepers per the
owner's recorded list: JrnWin Blue, InvoiceWin Mint, CshWin Teal,
ChqWin Periwinkle, OrdWin Sky, MatDirWin Sand, MatCard Amber,
CstCard Rose, CoaWin Lavender, AssetWin Stone, ProjWin Aqua,
PayRecWin Lilac, BankRecWin Slate, MfgWin Green, RptBase Silver,
PosWin Peach) and every schedule-bearing gap window assigned its
own. RptBase-derived report windows inherit the report family's
Silver by design (one family, one tint — the owner's own recorded
assignment). Dialogs without schedules (CalculatorWin,
ChangePasswordWin, ColChsWin, HelpWin, CashDrawerOptionsWin,
OptWin) carry no tint — nothing alternates in them. Result: **97
windows resolve 97 distinct tints**; the full window → tint table
is in docs/GRID_PROGRAM.md (Tint audit section).

New windows from this batch's tint assignments include:
PosTermWin Heather, PosSalesWin Fallow, PosSessionWin Gorse,
PosReturnWin Celadon, InvDesWin Zephyr, BcdDesWin Dapple.

## Comparison verdict (power + ease)

- Exceeds: one window for all kinds with kind-correct posting
  policy enforced in the repository (incl. the new inventory-only
  stock rule); batch/serial depth beyond the reference's inline
  fields; per-user column visibility + placement; encrypted design
  files driving print (B3); whole-window RTL mirroring.
- Trails: expiry/serial not visible inline in the grid (posting-time
  picker instead) — recorded above as a deliberate difference, not
  a gap in capability.
- Verdict: MORE POWERFUL — posting correctness is enforced
  centrally per kind, not per screen; EASIER — F3 quick search +
  type-ahead entry add a material in one step from anywhere in
  the window.

## Gates

INV1 (after two fix rounds: Arabic-in-comments removed per the
English-source rule; missing System.Windows.Input using):
build 0/0; migrator through 120 idempotent; 4 audits exit 0;
Errors parity PASS; P2pHarness 39/39; W1Harness 36/36 — ALL-GREEN.
