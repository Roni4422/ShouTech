# Window Proportions Standard

Branch: `feat/window-proportions` (from main @ `05d9296`). Status: implemented,
not merged — awaiting owner approval.

This document defines the ONE proportion standard for every ShouTech window,
what the audit found before it, and what changed window by window.

---

## 1. The core rule — the Phenix alignment rule

Modelled on Phenix ERP v10 form layout (and consistent with Al-Ameen V10,
ERPNext, Odoo and Windows 11 Fluent forms):

1. **One shared column grid per form.** Every form is a single grid whose
   columns alternate *label / field / label / field …*. Field edges align
   vertically across **all** rows of the form.
2. **Rows subdivide the same total width proportionally.** A row with two
   fields splits the form width into two equal halves; a row with one field
   spans both halves. Boundaries always coincide with the rows above and
   below — no ragged, independently-sized fields. The span scheme is
   *full / half / third / quarter* of the form width, expressed as pair
   spans (`spanPairs` in code).
3. **Field groups are proportional to each other.** The same pair scheme
   repeats in every group of a window (header group, totals group, filter
   group), so the whole window reads as one harmonious grid.
4. **Labels sit BESIDE their fields** — in the aligned label column, never
   above the field. Label column width is one token for the whole app.
5. **Buttons align to the same grid edges where practical**; the bottom
   command bar rules are unchanged (bottom of window, standard height and
   minimum widths, primary action widest).

Under RTL (Arabic) the grid mirrors automatically: label columns and field
columns swap sides with `FlowDirection`, because no layout depends on fixed
left/right positioning.

---

## 2. The standard (tokens)

All tokens live in the existing central design system
(`Themes/VisuId.xaml`, consumed by `App.xaml`, `Views/BaseWin.cs`,
`Services/VisuId.cs`) — primitives → semantic → component, no parallel
theme system.

### 2.1 One height for every input and button

| Token | Value | Meaning |
|---|---|---|
| `ControlHeight` | **32** | THE height of every TextBox, ComboBox, DatePicker, PasswordBox and button (Fluent standard) |
| `SmallControlHeight` | 24 | Compact variant — only where the design system already had one (grid pager buttons, dense toolbars) |
| `TouchControlHeight` | 40 | POS touch surfaces only |

Before this pass the central styles disagreed with themselves: the `Visu*`
styles used 32 while the `App*` styles used `AppTextInputHeight` 36,
`PrimaryButtonHeight` 38 and `SecondaryButtonHeight` 34. All three are now
pinned to `ControlHeight`, so **every** style in the system renders inputs
and buttons at exactly 32.

### 2.2 Buttons

| Token | Value | Meaning |
|---|---|---|
| `ButtonMinWidth` | 88 | Minimum width of every standard button |
| `PrimaryButtonMinWidth` | 100 | Minimum width of the primary action (Save/Post/OK) — always ≥ secondary |
| `SecondaryButtonMinWidth` | 88 | = `ButtonMinWidth` |

Rules: bottom command bar stays at the bottom; buttons in a bar are
separated by an 8px gap; dialog button groups are equal-width; the primary
action uses `PrimaryButtonStyle` and the primary minimum width.

### 2.3 Form grid

| Token | Value | Meaning |
|---|---|---|
| `FormLabelWidth` | 120 | The label column width — one width for every form in the app |
| `FormLabelColumnWidth` | GridLength 120 | XAML form of the same |
| `MarginFormLabel` | `0,0,8,8` | Label cell margin |
| `MarginFormField` | `0,0,12,8` | Field cell margin (12 = gap before the next label column) |

Code windows build forms **only** through the central helpers:

- `BaseWin.CreateFormGrid(pairs)` — creates the alternating
  label(120)/field(*) grid.
- `BaseWin.AddFormField(grid, row, pair, key, control, spanPairs = 1)` —
  side label (text from the Lang key, secondary brush) + field; applies the
  central style by control type (TextBox → `VisuTextBoxStyle`, ComboBox →
  `VisuComboBoxStyle`, DatePicker → `Win11DatePickerStyle`, PasswordBox →
  `VisuPasswordBoxStyle`) and the standard field margin.
- `BaseWin.AddFormField(labels, grid, …)` — same, for windows that keep a
  label list for re-localization.
- `BaseWin.AddFormElement(grid, row, pair, control, spanPairs = 1)` —
  self-captioned controls (CheckBox) placed in the field cell, label cell
  left empty so edges stay aligned.
- `F2Ui.CreateFormGrid/AddField/AddElement` — the same helpers for the
  F2-window family (labels registered in the window's list).

**The central label contract.** A form label's geometry is owned by the
central styles, never by a window: `VisuTextBlockLabelStyle` (TextBlock —
what XAML forms use and what `AddFormField` applies) and `VisuLabelStyle`
(Label control), both in `Themes/VisuId.xaml`. Applying one guarantees:
the label occupies the `FormLabelColumnWidth` column beside its field;
its text aligns to the **reading-start edge of that column** — Left in
LTR, Right in RTL — which is the edge adjacent to the field, so the label
rests a few pixels from its field in both directions; the alignment is
resolved from the window's live `FlowDirection` by the style's data
triggers (never hardcoded per window); the label is vertically centered
on its field and ellipsizes inside its column instead of overflowing it.
Cell spacing stays with the margin tokens above (`MarginFormLabel` /
`MarginFormField`). A window must not define a local form-label style —
the one time a window carried its own copy (MatCard), the copy inverted
the RTL trigger and pushed Arabic labels to the far edge of their
columns; MatCard now uses the central style like every other form.

Fields carry **no Width/Height of their own** inside a form grid: height
comes from the central style (32), width from the star-sized column.

### 2.4 Field width classes (non-grid contexts only)

For standalone fields (filter bars outside a grid, search boxes, dialogs):

| Token | Value | Use |
|---|---|---|
| `FieldWidthTiny` | 80 | Page numbers, tiny codes |
| `FieldWidthShort` | 120 | Codes, dates, short amounts |
| `FieldWidthMedium` | 180 | Names, combo boxes |
| `FieldWidthLong` | 240 | Long names, paths |
| `FieldWidthWide` | 400 | Addresses, login card, wide text |
| `MultilineFieldHeight` | 64 | Notes/description editors (the one sanctioned non-32 field height) |

### 2.5 Spacing rhythm

The pre-existing 4/8 scale is the only rhythm: `Spacing0..10` =
0/2/4/6/8/12/16/20/24/32/40, `PaddingWindow` 16, `PaddingCard` 16,
`MarginSection` `0,0,0,16`, `MarginField` `0,0,0,8`. Form rows breathe
through `MarginFormField` (bottom 8); button gaps are 8.

### 2.6 Report filter scheme

All reports share `RptBase`'s filter area: ONE 3-pair form grid; filters
flow row by row (filter *i* → row *i*/3, pair *i*%3), so every report's
filters have identical proportions and aligned edges.

### 2.7 Window sizing — the 50% rule

Owner rule (2026-11-17): *"Ribbon windows must be smaller than or equal
to 50% of the screen, or of the main window when it is full-screen."*
Only the main shell window (MWin) is meant to fill the screen. Every
child window (all `BaseWin` windows) opens at most **half the work area
in each dimension**: at load, `BaseWin.ClampToWorkArea` caps width at
50% of the work-area width and height at 50% of the work-area height
(`WorkAreaSizeCapFraction`). A maximized shell fills the work area, so
the work-area halves are also the main-window halves — one computation
implements both halves of the rule. The same pass pulls any off-screen
position back inside, so no window opens with its bottom command bar
out of reach. The cap is a load-time trim, not a limit: no
`MaxWidth`/`MaxHeight` is set, the user can resize larger or maximize
freely, geometry persistence keeps the size they leave, and the trim
re-applies on the next load only if that size still exceeds the cap.
Windows smaller than the cap are never grown. One exception: a window
is never shrunk below its declared `MinWidth`/`MinHeight`, so a window
whose minimum exceeds the cap opens at its minimum instead — the
acceptance sweep catalogues those windows, and reducing their minimums
is a separate layout pass. (This rule supersedes the earlier
work-area-minus-24-DIP comfort-margin cap.)

---

## 3. Audit findings (before the pass)

Measured across all windows on main @ `05d9296`:

**Code-built windows (C#)**
- **137** input initializers with a fixed `Height` — 128 × `Height = 26`,
  6 × 28, 1 × 34, 1 × 40, 1 × 48.
- **143** initializers with a fixed `Width` across 24 files.
- **Two competing per-window button helpers**: 16 files with
  `StyleButton` family A (MinWidth 76 / Height 28 / Padding 9,3) and
  13 files with family B (MinWidth 92 / MinHeight 32), plus one-off
  variants (MinWidth 72/82/90/105/112, heights 28/30/34/36/38).
- **~35 windows** built fields with a private `Labeled()` helper that
  stacks the label **above** the field in a WrapPanel/StackPanel — every
  field independently sized, no shared edges.
- Spacer `TextBlock`s with `Width = 25/30` used as horizontal gaps.
- Central split-brain: `Visu*` styles at 32 vs `App*` styles at 36/38/34
  (see §2.1).

**XAML windows (21 files, DatWin excluded)**
- **68** numeric `Height` attributes on input controls (values 22–130),
  **42** numeric `Width` attributes, **4** window-local input styles with
  their own heights (MatCard `FieldBox`/`FieldCombo` 30, StpWiz wizard
  styles 26/28, LogWin login styles 34, AccCard nav style 44).
- Label columns at 55/78/80/105/145 — a different width in almost every
  window; AccCard's header strip alone used three (78/55/55) plus a fixed
  100px field among star fields.
- StpWiz (setup wizard) was fully label-above on all four pages.

**Deliberately not counted as violations:** window sizes, DataGrid column
widths, print page sizes (A4 794×1123), icon/glyph sizes, image previews.

---

## 4. Per-window results

Legend: **Grid** = converted to the Phenix form grid (side labels, aligned
edges). **LA→S** = had label-above layout, converted to side labels.
**Norm** = sizes normalized to tokens/styles without layout change.

### 4.1 Central

| File | Change |
|---|---|
| `Themes/VisuId.xaml` | Divergent height tokens pinned to `ControlHeight` 32; added `ButtonMinWidth`, `FormLabelWidth`/`FormLabelColumnWidth`, `FieldWidth*` classes, `MultilineFieldHeight`, `MarginFormLabel`/`MarginFormField` |
| `App.xaml` | `PrimaryButtonStyle` padding normalized to `16,0` |
| `Services/VisuId.cs` | `ResolveMetric` + static accessors for all size tokens |
| `Views/BaseWin.cs` | `CreateFormGrid` / `AddFormField` (×2) / `AddFormElement` / `ApplyFieldStyle` form helpers |
| `Views/F2WinUi.cs` | Mirrored form helpers for the F2 family; `StyleButton`, `BottomBar`, `StatusBar` normalized |

### 4.2 Code-built windows

| Window | Result |
|---|---|
| CstCard, BnkCard, RepCard, CcCard | **Grid, LA→S** — 3-pair editor grids |
| CurCard | **Grid, LA→S** — 1-pair side-card grid |
| JrnWin, CshWin, ChqWin, OrdWin, MfgWin | **Grid, LA→S** — 4-pair header grids |
| InvoiceWin | **Grid, LA→S** — header groups + totals groups rewritten to side-label cells; forced 24px heights removed; notes → `MultilineFieldHeight` |
| BarWin | **Grid, LA→S** — 1-pair grid |
| DocSearchWin | **Grid, LA→S** — 4-pair filter grid |
| GroupManagerWin | **Grid, LA→S** — 4-pair editor grid |
| BankRecWin, ActivityLogWin, FinancialAnalysisWin, RepostWin | **Grid, LA→S** — filter grids via F2 helpers |
| ChangePasswordWin | **Grid, LA→S** — 1-pair grid |
| CloseCashWin | **Grid, LA→S** — 4-pair grid |
| CustomReportsWin | **Grid, LA→S** — key row + designer grids (column-info editor keeps its 90px content height, §5) |
| SupportWin | **Grid, LA→S** — 1-pair grid (message body keeps 110px content height, §5) |
| ProjWin | **Grid, LA→S** — both editors, 1-pair grids |
| AssetWin | **Grid, LA→S** — editor, 13 fields, 1-pair grid |
| DeliveryDeskWin | **Grid, LA→S** — editor + settle editor, 1-pair grids |
| InstallmentsWin | **Grid, LA→S** — editor + collect editor; search bar → 2-pair grid |
| LoyaltyWin | **Grid, LA→S** — editor, 1-pair grid |
| DefManagerWin | **Grid, LA→S** — dynamic editor rebuilt as a 1-pair form grid (one row per descriptor column) |
| PayRecWin | **Grid, LA→S** — selector row → 3-pair grid |
| ChqListWin | **Grid, LA→S** — filters → 3-pair grid |
| PartyManagerWin | **Grid, LA→S** — filter → 1-pair grid |
| PartySearchWin | **Grid, LA→S** — search form → 2-pair grid |
| PosCheckoutWin | **Grid, LA→S** — document/customer/payment rows → form grids; primary button → `PrimaryButtonStyle` + `PrimaryButtonMinWidth` |
| OrderTrackWin | **Grid, LA→S** — filter → 3-pair scheme (pair 0) |
| RptBase (+ ~40 report windows) | **Grid, LA→S** — filter bar → shared 3-pair grid (§2.6); toolbar buttons normalized; the ComboBox/DatePicker Border-wrapper hack removed in favour of the central styles |
| CcListWin, MfgListWin | **Norm** — pure list windows; button helpers normalized |
| BcdDesWin, BcdGenWin | **Norm** — designer tools; labels were already beside fields; widths snapped to `FieldWidth` classes, buttons normalized |
| CashDrawerOptionsWin | **Norm** — combo/button heights removed, MinWidths tokenized |
| WhsWin code dialogs (stock count, transfer, MatMergeWin) | **Norm** — all `Height = 28` removed from styled controls, button MinWidths tokenized, lookup buttons → `ControlHeight` squares |

### 4.3 XAML windows

| Window | Result |
|---|---|
| AccCard | **Norm** — header strip + main form label columns → `FormLabelColumnWidth`; fixed 100px field → star; heights 28/29 removed; buttons tokenized; description → `MultilineFieldHeight`; nav style height → `ControlHeight` |
| ComWin | **Norm** — label columns 145 → token; button heights 32/36 removed (styles supply 32); notes `MinHeight` → `MultilineFieldHeight`; OK/Cancel columns → `PrimaryButtonMinWidth` |
| MatCard | **Norm** — `FieldBox`/`FieldCombo` heights → `ControlHeight` token; label columns 80 → token; utility buttons → `ControlHeight` squares; notes → `MultilineFieldHeight` |
| StpWiz | **Grid, LA→S** — all four pages restructured from label/field row pairs to 2-pair side-label form grids; wizard styles → `ControlHeight`; nav buttons → MinWidth tokens |
| LogWin | **Norm** — login styles' height 34 → `ControlHeight`; field panel → `FieldWidthWide`; field widths removed (stretch) |
| WhsWin | **Norm** — combo height removed; notes → `MultilineFieldHeight`; auto-code box → `FieldWidthShort`; button MinWidths tokenized |
| BinWin, BkpWin, RestoreCoWin, PrmWin | **Norm** — fixed button Width/Height → MinWidth tokens (primary → `PrimaryButtonMinWidth`) |
| Lang, SerWin, MatDirWin, MatGroupWin, CoaWin, ColChsWin, CatWin | **Norm** — stray heights removed; MinWidths tokenized; move buttons → `ControlHeight`; specs → `MultilineFieldHeight` |
| Modules/ReportGridVw | **Norm** — pager buttons → `SmallControlHeight` squares |
| SplWin, Modules/DashVw, Modules/ModuleWorkspaceView | Already conforming — no sized input controls |
| MWin | Shell — the ribbon is shell chrome styled by `Themes/ShRibn.xaml` (out of scope per brief); MWin.xaml contains no sized input controls |

### 4.4 Already conforming (no changes needed)

AddonsWin, CalculatorWin, CompressInvoicesWin, HelpWin, LicenseWin,
MaintenanceWin, PosSyncWin, RolloverWin, UpdatesWin — F2-family windows
whose only sizes were window geometry; they inherit the normalized
`F2Ui.StyleButton`/`BottomBar` and the central styles.

---

## 5. Exceptions (deliberate, documented)

| Window / element | Why it is not on the standard |
|---|---|
| **PosWin** | Touch POS terminal surface with its own deliberate proportions (48px scan field at 20pt, 38–54px touch buttons via one `MakeButton` helper, 320px summary rail). Forcing the office-form standard would break its touch purpose; it stays on the `TouchControlHeight` family. |
| **DatWin** | Excluded by the brief — being rebuilt on a separate branch; it will inherit these central tokens when that lands. |
| CustomReportsWin column-info editor (90px), SupportWin message body (110px) | Content editors, not form fields — a message/report body needs more than the 64px notes height. Kept as content heights. |
| PartySearchWin / InstallmentsWin search buttons | Sit beside the filter grid (docked right, top-aligned) as the filter's search trigger — pre-existing placement kept; sizes are standard. |
| BcdDesWin / BcdGenWin | Barcode designer tools — fields already side-labelled in tool panels; widths snapped to the `FieldWidth` classes instead of grid conversion. |
| Report Top-N `Limit` filter, grid column widths, window sizes, print sizes | By design (ranked report parameter / data presentation / geometry), not form proportions. |

---

## 6. Gates (all run on this branch)

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors** |
| Warnings vs fresh build of base `05d9296` | **67 = 67 — 0 net-new, 0 net-gone** (multiset diff by file/rule/message) |
| `tools/audit_window_context.py` | **Exit 0** (71 windows, 0 window-level FlowDirection pins) |
| Resource sweep (Static/DynamicResource refs vs defined keys, mine vs base) | **0 new missing keys** (same 2 pre-existing sweep artifacts in both; +11 defined keys = the new tokens, +7 references) |
| Lang catalogs | Untouched — no new keys needed; parity as on main |
| Final greps | No `Labeled(` helpers remain anywhere; no input `Height = 26/28` remains in code outside the documented PosWin exception |
