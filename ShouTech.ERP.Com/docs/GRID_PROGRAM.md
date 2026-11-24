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

**Where ShouTech exceeds the benchmarks:** whole-window RTL
mirroring with live language switching (the web benchmarks reload;
the legacy desktop benchmarks are Arabic-first UIs of their era);
per-window zebra tints in a modern Windows 11 surface; the
directory-as-search pattern reuses the SAME MatDirWin the user
manages materials in — one surface to learn, not a separate
lookup dialog per window.
**Where ShouTech still trails:** no scan-gun continuous-entry
mode (barcode wedge auto-advance row) — named gap for the
commercial-readiness phase; ERPNext's per-line warehouse/SO
linkage prompts are richer on the sales side.
**Verdict (power + ease):** more powerful — every optional column
is backed by a real persisted field and the matcher resolves
codes, five unit barcodes, GTIN and names in one pass; easier —
type part of a name and the line fills itself (unit, tax, price),
the search opens only when the match is genuinely ambiguous, and
the five core columns never move.

## Family 2 — voucher grids (JrnWin, CshWin, PayRecWin, ChqWin)

**Verdict: DONE (batch R2b), gates ALL-GREEN.**

- Row numbers: all four windows now use the central
  ShowRowNumbers. JrnWin/CshWin/ChqWin each carried a LineNo DATA
  column duplicating the row header — dropped from the grids
  (LineNo is still maintained in code and persisted exactly as
  before). PayRecWin's two read-only allocation grids had
  RowHeaderWidth=0 — now numbered.
- Account-first: JrnWin/CshWin already led with the account
  (code + read-only name) — conformed. ChqWin's account column
  sits mid-grid by the cheque register's nature (cheque number
  first) — kept, with the lookup added.
- Account entry: NEW `AccRepo.MatchAccount` (postable accounts
  only; exact code/name, then unique contains) mirrors the
  material matcher. Committing the account cell in JrnWin,
  CshWin and ChqWin fills code + name immediately on an exact or
  unique-partial match; ambiguous/no match opens CoaWin as the
  search window (new picker mode: editing actions hidden,
  double-click/Enter accepts a postable account, pre-filtered
  with the typed text). Per-line processed-text guards prevent
  tab-through re-opens. Unmatched text reports into the window's
  status line (Vouchers.AccountNotMatched ×4 languages).
- Column chooser: JrnWin (CostCenter, Memo) and CshWin
  (CostCenter, InvoiceRef, Memo) gained the ColChsWin chooser +
  WinPref per-user persistence (Vch: scopes), with headers
  converted from index-based to identity-based. ChqWin: the only
  candidate was Memo — chooser does not fit a one-column case;
  recorded as not fitting. PayRecWin grids are fixed allocation
  layouts — chooser not applicable.
- PartySearchWin remains the party picker where windows ask for
  a party explicitly; the voucher account cell uses the chart,
  because under the canonical model parties ARE chart accounts
  and one matcher covers both (spec item 5's "one logic").

### Four-system comparison — voucher grids

| Dimension | ERPNext | Odoo | Al-Ameen 10 | Phenix v10 | ShouTech after R2b |
|---|---|---|---|---|---|
| First field | account (Journal Entry) | account label search | account code/name | account | account code + name, first |
| Account lookup | link-field search dialog | autocomplete dropdown | directory search | chart lookup | type-ahead match; chart-as-search on ambiguity |
| Row numbers | idx column | sequence handle | # column | numbered | fixed row-header numbers |
| Optional columns | grid config | optional columns | column settings | per-user show/hide | ColChsWin + WinPref (Jrn/Csh) |
| Balance feedback | totals + difference row | totals bar | bottom totals | bottom bar | live totals + balanced badge (Jrn), totals/status (Csh) |

**Where ShouTech exceeds:** the chart-as-search reuses the real
CoaWin (the user sees the whole tree with balances, not a flat
lookup list); matching covers Arabic and Latin names equally in
a four-language UI; live re-mirroring on language switch.
**Where ShouTech still trails:** no account-balance preview
inline in the account cell while typing (benchmarks show the
account's current balance in the lookup) — buildable, recorded
as a gap for the commercial-readiness phase; journal templates /
recurring entries are a separate feature family, out of scope.
**Verdict (power + ease):** more powerful — one matcher over the
canonical chart (parties and GL accounts unified), postable-only
so a summary account can never be posted to by accident; easier —
partial-name typing fills the account, the full chart opens only
when needed, and the balanced badge confirms the entry before
saving.

## Family 3 — report grids (RptBase family)

**Verdict: DONE (batch R2c), gates ALL-GREEN.**

RptBase already conformed on the chooser + WinPref per-user
persistence (item 4), zebra rows with per-report tints, totals
footers and unlimited rows. The one deviation was row headers:
`HeadersVisibility = Column` hid them. Fixed centrally —
`DataGridTableBehavior.SetShowRowNumbers` in RptBase's grid
setup covers all 71 RptBase-derived report windows at once, and
the custom-report grid (ReportGridVw, used by CustomReportsWin)
got the same switch in its constructor. No other report surface
builds its own grid.

**Where ShouTech exceeds:** one central switch modernized 72
report surfaces at once; report designs (B2) carry cell-level
formulas and encrypted .strd portability none of the four
benchmarks offer in this form; the chooser persists per user
per report. **Where ShouTech still trails:** in-grid grouping
by drag-to-group-header (Odoo/ERPNext web grids) is not in the
desktop grid — grouping lives in the report definition instead;
recorded as a named gap, not papered over.
**Verdict (power + ease):** more powerful — the same grid the
user reads is the grid the designer shapes (columns, formulas,
totals), exportable encrypted; easier — row numbers, zebra and
totals are simply always there, and a user's column layout is
remembered per report.

## Family 4 — material directory tree (MatDirWin)

**Verdict: DONE (batch R2d), gates ALL-GREEN.**

- The flat ListView (with its Materials/Groups mode radios) is
  now ONE TreeView: group (category) nodes nest by parent_code,
  materials attach through material_group_items membership (a
  material in several groups appears under each), ungrouped
  materials sit at the root, empty groups show. Folder glyphs
  are Segoe MDL2 (closed \uE8B7 / open \uE838 by the node's
  IsExpanded, two-way bound to the TreeViewItem); materials use
  the document glyph \uE8A5. Search prunes to matches plus their
  ancestor groups, expanded; a matching group keeps its whole
  subtree. Picker mode (invoice-grid search) runs on the same
  tree unchanged.
- **Defect found and fixed at the cause:** the old Groups mode
  loaded through reflection against a `MatGroupRepo.LoadAll`
  method that did not exist — Groups mode silently rendered
  nothing. `MatGroupRepo.LoadAll` + `LoadMembership` now exist
  and are called directly; the reflection path is deleted.
- Al-Ameen evidence for this family now rests on the full
  classic help text (owner upload 2026-11-24 15:33,
  `~/workspace/research/al-ameen-help-utf8.txt`): the material
  directory/card topics confirm the group-tree + card pattern.
  Limit recorded: classic generation's logic/fields, not
  Al-Ameen 10's screens.

**Where ShouTech exceeds:** the directory is simultaneously the
management surface, the invoice-grid search and the POS lookup
— one tree everywhere; four-language names on every node with
whole-window RTL mirroring. **Where ShouTech still trails:** no
drag-and-drop re-grouping (benchmarks' trees allow dragging a
material onto a group) — named gap for the commercial phase;
group membership is edited in MatGroupWin meanwhile.
**Verdict (power + ease):** more powerful — true M2M membership
shown faithfully (a material under every group it belongs to,
nested sub-groups by parent code); easier — the user browses
folders instead of filtering a flat thousand-row list, and
search keeps the ancestor chain visible so a match is never
orphaned from its context.

## Family 5 — chart of accounts + account card (CoaWin + AccCard)

Pending batch R2e: tree + level indicators 1–5 + search (logic
modelled on the Al-Ameen 2004 reference image; visuals modern
central), account card inside the directory family with the
image's field set checked against chart_of_accounts — missing
fields (estimated budget, reconciliation date, confidentiality
level, warning side, account side, closing account) go through a
migration as part of the complete-pillars build, flagged in the
batch report. Phenix v9 consulted as the additional reference.
