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

- Shared policy is applied through `app/Services/GridEngine.cs`.
  It centralizes virtualization/recycling, row numbering, standard
  context behavior and table palette; domain windows retain their
  own columns and persistence. Zebra rows use each window's palette.
  Rows are uncapped; document lines are added through the validated
  explicit add-line action.
- Default columns: Material, Unit, Qty, UnitPrice, calculated
  LineTotal and Statement. Additional fields (Code, Discount %,
  Tax code, Tax %, Margin, Warehouse) remain chooser-managed. Header rewriting on language
  change now binds by column identity (it was index-based and
  would have mislabeled columns after any reorder — fixed at the
  cause).
- Material entry: `TaxSvc.MatchMaterial` resolves exact/unique
  partial matches for codes, alternate codes, GTIN, unit barcodes
  and names. Ambiguous/no match opens the shared SerWin picker with
  the typed fragment pre-filled; selection fills the line from the
  active company's material record. The scanner follows the same
  cell-commit pipeline; weighted labels also populate quantity. The Find Material button, previously a
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

**Verdict: DONE (batch R2e), gates ALL-GREEN (incl. migration
118).**

Reference split (owner 15:19/15:20): the screenshot is
**Al-Ameen 2004** — structure/content/logic only (tree
organization, level indicators, card fields, balance panel,
navigator). All visuals are the modern central system (Win11
surfaces, slate accent, Segoe MDL2 glyphs). Phenix v9 stands as
the additional account-card reference the owner named; its
screens are not publicly documented in comparable detail, so
the field-level model follows the Al-Ameen 2004 image, which we
hold. Al-Ameen's classic help text (owner upload 15:33) backs
the account-card topics (168 mentions) with the same limit:
classic-generation logic/fields, not Al-Ameen 10's screens.

What already conformed (verified, unchanged): CoaWin was
already a TreeView with search-at-top, hierarchical numbering,
expand/collapse-all, and AccCard opened from within the chart;
AccCard already carried the record navigator (first/prev/next/
last), the CURRENT BALANCE panel (debit/credit/balance from
posted journal lines), bottom buttons, parent lookup, Latin
name, posting-mode (type) dropdown, and the risk/confidentiality
level with its colored indicator.

Built in R2e:
- **Folder glyphs** in the CoaWin tree template: group accounts
  show the closed folder (E8B7) / open folder (E838 while
  expanded, MultiDataTrigger on TypeLabel + IsExpanded);
  postable accounts show the document glyph (E8A5).
- **Level indicators 1–5** above the tree (header row, end
  aligned, FlowAlign): one click expands the displayed tree to
  exactly N levels (works on search-filtered clones too).
- **Migration 118 (BOTH dialects) — schema additions, flagged
  explicitly per the owner's instruction:** chart_of_accounts
  gains `closing_account_id`, `warning_side` (NONE/DEBIT/CREDIT),
  `account_side` (DEBIT/CREDIT/BOTH), `estimated_budget`,
  `currency_code`, `exchange_parity`, `reconciliation_date` —
  the reference card's fields the schema lacked. AccRepo reads
  and writes them end-to-end (closing resolved by code like the
  parent; AccRec gains them as optional trailing parameters so
  existing constructions, incl. Kern.Tests, are untouched).
- **AccCard** gains the seven fields as real persisted inputs:
  closing-account combo (same account list as parent), warning
  side combo, account side combo, estimated budget, currency
  (editable combo seeded with the company currency — no central
  currency-list API exists in the app layer; free ISO entry
  stays possible), exchange parity (validated > 0, shown at
  5 decimals as in the reference), reconciliation date picker.
  Validation mirrors the card's existing rules; the new-field
  labels are Lang keys ×4.

### Four-system comparison — chart + account card

| Dimension | ERPNext | Odoo | Al-Ameen 2004 (reference) | Phenix v9 | ShouTech after R2e |
|---|---|---|---|---|---|
| Chart presentation | tree (web) | list/tree views | tree + level indicators 1–5 | tree (vendor claim) | tree + level indicators 1–5 + search |
| Node glyphs | web icons | web icons | legacy folder bitmaps | — | modern MDL2 folder/document glyphs |
| Card inside directory | separate form | separate form | panel within the chart window | card from chart | AccCard opened from CoaWin (same family) |
| Card fields | name, type, parent, currency, frozen | name, type, parent, reconcile | code, names, type, parent, closing, warning, budget, currency/parity, side, reconcile date, confidentiality, notes | comparable card (thin public evidence) | all of the reference set (118 added the missing seven) |
| Balance panel | on-account balances | balances in list | debit/credit/balance panel | — | debit/credit/balance panel from posted lines |
| Navigator | list prev/next | pager | first/prev/next/last | — | first/prev/next/last |

**Where ShouTech exceeds:** the same chart doubles as the
voucher grids' account search (picker mode) — no benchmark
reuses its chart that way; whole-window RTL mirroring and live
language switching on a desktop chart; balances computed from
posted journal lines only (never cached counters).
**Where ShouTech still trails:** the closing account is stored
but the year-end closing routine does not yet consume it
(closing entries exist as a report family; wiring the card's
closing_account_id into the fiscal-close posting is a named
commercial-phase gap); warning_side is persisted but not yet
enforced at journal posting (same gap class).
**Verdict (power + ease):** more powerful — the card now holds
the full reference field set with real persistence, and the
chart's levels/glyphs/search make a 7-root, deep chart
navigable at a glance; easier — one click sets the exact tree
depth, the card opens in place from the chart with its balance
in front of the user, and nothing about the legacy reference
look was copied.

## Tint audit (owner reinforcement 2026-11-24 16:08) — outcome

Every schedule window resolves a DISTINCT tint from the central
palette (97 windows / 97 tints after the INV1 sweep; RptBase report
family shares Silver by design). Full record: docs/INVOICE_FAMILY.md.
The unified Tools window (TOOLS_WINDOW_STUDY, feat/tools-window) takes
Hickory — the one palette tint no window had claimed (98 windows /
98 tints). CloseCashWin (Coral) was retired by owner decision
(2026-11-24) in favour of the POS session close — the census
returns to 97 windows / 97 tints and Coral is again the palette's
unclaimed tint.

| Window | Tint |
|---|---|
| AccCard | Cinder |
| ActivityLogWin | Olive |
| AddonsWin | Wheat |
| AssetWin | Stone |
| BankRecWin | Slate |
| BatchSerialPickerWin | Bracken |
| BcdDesWin | Dapple |
| BcdGenWin | Lagoon |
| BinWin | Bronze |
| BnkCard | Elder |
| BomWin | Fern |
| BudgetWin | Copper |
| CcCard | Flint |
| CcListWin | Orchid |
| ChqListWin | Indigo |
| ChqWin | Periwinkle |
| CoaWin | Lavender |
| CommWin | Tundra |
| CompressInvoicesWin | Solace |
| ContractWin | Quince |
| CrmActivitiesWin | Meadow |
| CrmCampaignWin | Opal |
| CrmLeadWin | Russet |
| CrmOppWin | Bramble |
| CrmPipelineWin | Ochre |
| CshWin | Teal |
| CstCard | Rose |
| CurCard | Peridot |
| CustomReportsWin | Violet |
| DatWin | Hazel |
| DefManagerWin | Jade |
| DeliveryDeskWin | Lime |
| DocSearchWin | Denim |
| FinancialAnalysisWin | Plum |
| GroupManagerWin | Topaz |
| HrAttendanceWin | Cyan |
| HrDeptWin | Sage |
| HrDesigWin | Mist |
| HrEmployeeWin | Basalt |
| HrLeaveWin | Sienna |
| HrPayrollWin | Cobalt |
| HrStructureWin | Alder |
| InstallmentsWin | Quartz |
| InvDesWin | Zephyr |
| InvoiceWin | Mint |
| JrnWin | Blue |
| LicenseWin | Jasper |
| LoyaltyWin | Garnet |
| MaintenanceWin | Keel |
| MatCard | Amber |
| MatDirWin | Sand |
| MatGroupWin | Honey |
| MatMergeWin | Willow |
| MfgListWin | Birch |
| MfgPlanWin | Sorrel |
| MfgWin | Green |
| OrdWin | Sky |
| OrderTrackWin | Dune |
| PartyManagerWin | Blush |
| PartySearchWin | Mauve |
| PayRecWin | Lilac |
| PosCheckoutWin | Yarrow |
| PosPerfRptWin | Damson |
| PosReturnWin | Celadon |
| PosSalesWin | Fallow |
| PosSessionWin | Gorse |
| PosSyncWin | Harbor |
| PosTermWin | Heather |
| PosWin | Peach |
| PosZReportWin | Juniper |
| ProjWin | Aqua |
| PurchaseAttachmentsWin | Larkspur |
| PurchaseLandedCostWin | Mallow |
| PurchaseMatchPolicyWin | Nectar |
| PurchaseReceiptReturnWin | Osier |
| PurchaseRequisitionWin | Pollen |
| PurchaseReturnWin | Quill |
| QcAlertWin | Claret |
| QcInspectWin | Rowan |
| QcTemplateWin | Marl |
| RecurWin | Kelp |
| RepCard | Grove |
| RepostWin | Thicket |
| RmaWin | Linden |
| RolloverWin | Cedar |
| RptBase | Silver |
| SalesDeliveryWin | Rattan |
| SalesReturnWin | Sable |
| SerWin | Onyx |
| StockCountDialog | Pumice |
| SupplierQuotationCompareWin | Tallow |
| SupplierQuotationEntryWin | Umber |
| SupportWin | Ember |
| TaxReportWin | Vervain |
| TaxSetupWin | Wicker |
| ToolsWin | Hickory |
| WarrantyWin | Reed |
