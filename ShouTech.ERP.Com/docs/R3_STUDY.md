# R3 study — the five router-approximated reports (phase-gate study)

Owner phase-gate rule (2026-11-24 16:28): every phase opens with
its comparison study — POWER, FLEXIBILITY, USER-FRIENDLINESS,
FORM — committed before the phase's first build commit. This is
R3's study; the builds/adoptions follow it. Benchmarks: ERPNext,
Odoo, Al-Ameen (classic help text — the line's logic/field sets;
v10 specifics rest on earlier research), Phenix v10 (vendor
material). Evidence strength: ERPNext/Odoo report catalogs are
public and deep; Al-Ameen/Phenix report lists are thinner —
stated where it matters.

## The five concepts across the benchmarks

| Concept | ERPNext | Odoo | Al-Ameen (classic) | Phenix v10 |
|---|---|---|---|---|
| Material profits | Item-wise Profitability / Gross Profit report (per item, invoice-level margin) | Product margin on sales analysis (pivot) | Material profits report per material/group | Material profitability in inventory reports |
| Material GROUP movement | Stock Ledger grouped by item group (custom report / query) | Stock moves by product category (pivot grouping) | Group movement report (حركة مجموعة) is a named inventory report | Group movement among warehouse reports |
| Sales report | Sales Register (document register with totals) + Sales Analytics | Sales Analysis pivot | Sales register + sales reports family | Sales reports family incl. register |
| Daily report | Daily summary via journal/GL views | Daily journal reports | Daily report (التقرير اليومي) = the day's journal | Daily movement/journal reports |
| Receivables aging | Accounts Receivable summary (aging buckets) | Aged Receivable report | Customer aging (أعمار الذمم) | Customer aging report |

## Per-report verdicts (power / flexibility / ease / form)

1. **MaterialProfits → ItemProfitWin — ADOPT.**
   Power: ItemProfitWin computes per-item profit from posted
   documents (the same depth as ERPNext's Gross Profit at item
   level). Flexibility: period + filters via the standard
   RptBase chrome. Ease: one click from the ribbon to the exact
   concept named. Form: standard report form (filter row, one
   grid, totals bar, slim status bar) — identical proportions
   to every other report window, which is the harmony the
   benchmarks themselves don't offer across their report sets.
   No new window would add anything but a duplicate.
2. **MaterialGroupMovementReport → BUILD GroupMoveWin.**
   Power: the mapped MatMoveWin answers a DIFFERENT question
   (per-material ledger); group-aggregated in/out qty + value
   per group is a named report in Al-Ameen and a grouping in
   Odoo/ERPNext — a real gap. Built as an RptBase report:
   RptQuery.GroupMovesAsync aggregates material_stock_movements
   by material_group_items membership (+ Ungrouped bucket).
   Flexibility: warehouse + period filters; group membership is
   the user's own MatGroupWin data. Ease: one row per group —
   the summary a manager reads, drill-down stays in MatMoveWin.
   Form (counted): 3 filters (warehouse, from, to); 9 columns
   (group code, group, materials count, qty in, qty out, net
   qty, value in, value out, net value); totals row over all
   numeric columns; the standard RptBase chrome (refresh /
   print / export / columns / close in the bottom bar) and the
   report family's Silver tint. Proportions: the standard
   report grid (full-width schedule, totals pinned at the
   bottom) — no bespoke layout, by design.
   Semantics recorded: a material in several groups
   contributes to each (many-to-many membership); group totals
   can therefore exceed the single-count movement total.
3. **SalesReport → SalListWin — ADOPT.** The posted sales
   register with totals is the sales report of the inventory
   module in substance (ERPNext's own Sales Register is the
   same shape); analytics depth lives in the profitability
   reports, one menu away.
4. **DailyReport → JrnListWin — ADOPT.** The daily report is
   the journal register scoped to the day (Al-Ameen's daily
   report is the day's journal); the register's date filters
   do exactly this over the same ledger rows.
5. **AgingReceivables → CustAgingWin — ADOPT.** Receivables
   aging IS customer aging in every benchmark's catalog.

## Deviations recorded against this study

None — the phase executed exactly the five verdicts above.
The four adoptions are recorded in RibbonReportRouter's
comments (the "closest existing report" wording is gone) and
in docs/RIBBON_AUDIT.md; GroupMoveWin follows the study's
form counts.
