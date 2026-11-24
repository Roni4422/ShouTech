# Grid + directory program (owner spec 2026-11-24 15:07 + reference image 15:10, attribution corrected 15:19/15:20)

Owner spec items: (1) Excel-like grids in invoice/voucher/report
windows — numbered row-header column, header row, zebra rows
(per-window tint system), unlimited rows with virtualization;
(2) invoice grid column order — item name, quantity, unit, unit
price, line total, then optional columns; (3) material entry —
partial-name match inserts immediately, no match opens the
material search window, name/barcode entry pulls all data
(default unit, price); (4) optional columns via a chooser at the
end of the grid header row, checkboxes, per-user persistence;
(5) voucher grids are account-first with the same lookup logic;
(6) MatDirWin as a category tree with folder glyphs; (7) CoaWin
tree + account card as part of the chart.

Reference split (owner, 15:19 + 15:20): the forwarded screenshot
is **Al-Ameen 2004** — an old program. Its role is STRUCTURE /
CONTENT / LOGIC only (tree organization, card fields, workflow,
level indicators, balance panel). All VISUAL design is the modern
central system: Windows 11 Fluent/Mica, slate accent, Segoe UI,
central tokens, modern glyphs — no legacy visual copying. Phenix
v9 remains an additional account-card reference to consult.

## Family 1 — invoice grids (InvoiceWin: sales/purchase/returns/stock docs/quotations)

**Verdict: DONE (batch R2a), gates ALL-GREEN.**

- Row numbers: NEW central `DataGridTableBehavior.ShowRowNumbers`
  (fixed row-header column, 1-based position stamped per realized
  row — virtualization-safe, O(1) via GetIndex). InvoiceWin opts
  in. Zebra: already conformed (central styles + Sand/… per-window
  tint via AlternatingRowBackground). Unlimited rows: conformed
  (no caps; CanUserAddRows=false with explicit Add-line flow).
- Column order: REORDERED to the spec — Description (name), Qty,
  Unit, UnitPrice, LineTotal first; Code, Discount %, Tax code,
  Tax %, Margin, Warehouse after. Header rewriting on language
  change now binds by column identity (it was index-based and
  would have mislabeled columns after any reorder — fixed at the
  cause).
- Material entry: NEW `TaxSvc.MatchMaterial` on the proven
  ResolveMaterial predicates (codes, alt/old codes, GTIN, all five
  unit barcodes, exact names, name-contains). Exact or unique
  contains-match fills the line immediately (id, code, canonical
  name, default unit, tax rate; price through the existing sales
  resolver / card price on purchase). Ambiguous or no match opens
  MatDirWin as the search window (new picker mode: pre-filtered
  with the typed text, double-click/Enter accepts, editing buttons
  hidden) — the directory IS the search surface; no parallel
  lookup was built. The Find Material button, previously a
  focus-only stub, runs the same search. A per-line processed-text
  guard keeps tab-through from re-opening the search.
- Column chooser: the existing ColChsWin (checkboxes + reorder),
  opened from a Columns button at the END of the grid's header
  strip; it manages ONLY the optional columns (the five core
  columns are fixed by the spec and never enter the chooser).
  Every optional column is backed by a real LineVm field.
  Visibility/order/width persist per user via WinPref
  (company/user/Inv:{doc kind} scope — the RptBase store).

### Four-system comparison — invoice grid

| Dimension | ERPNext | Odoo | Al-Ameen 10 | Phenix v10 | ShouTech after R2a |
|---|---|---|---|---|---|
| Row numbers | child-grid idx column | line sequence handle | # column (help-index evidence) | numbered rows (vendor material) | fixed row-header numbers, central behavior |
| First columns | item, qty, rate, amount | product, qty, unit, price, subtotal | name, qty, unit, price, total | name, qty, unit, price, total (V2 pass) | name, qty, unit, price, total — spec order |
| Item entry | search query + barcode | autocomplete + "search more" dialog | F-key directory search | lookup + barcode | exact/unique-partial auto-insert; else directory-as-search pre-filtered |
| Optional columns | grid column config per user | optional columns toggle (web) | column settings | per-user column show/hide | ColChsWin checkboxes + reorder, WinPref per-user |
| Status bar | document status + totals | chatter/status bar | bottom totals bar | bottom bar | existing slim status + totals footer (unchanged) |

Evidence strength: ERPNext/Odoo — documented product behavior
(strong). Al-Ameen 10 — help-index level (thin; stated plainly).
Phenix v10 — vendor material (medium).

## Family 2 — voucher grids (JrnWin, CshWin, PayRecWin, ChqWin)

Pending batch R2b: account-first column + account lookup mirroring
item 3 (PartySearchWin exists for parties/accounts), row numbers,
chooser where the pattern fits.

## Family 3 — report grids (RptBase family)

Pending batch R2c: RptBase already has the chooser + WinPref
persistence (conformed for item 4) and zebra/totals; row numbers
to be switched on centrally there.

## Family 4 — material directory tree (MatDirWin)

Pending batch R2d: tree presentation (categories as folder nodes,
modern folder glyphs from the central icon approach — Segoe MDL2
glyphs, NOT legacy bitmap folders; materials as children;
uncategorized materials at root; empty categories shown).

## Family 5 — chart of accounts + account card (CoaWin + AccCard)

Pending batch R2e: tree + level indicators 1–5 + search (logic
modelled on the Al-Ameen 2004 reference image; visuals modern
central), account card inside the directory family with the
image's field set checked against chart_of_accounts — missing
fields (estimated budget, reconciliation date, confidentiality
level, warning side, account side, closing account) go through a
migration as part of the complete-pillars build, flagged in the
batch report. Phenix v9 consulted as the additional reference.
