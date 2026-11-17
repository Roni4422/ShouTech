# MatCard Fixes — Navigator, Group/Category Sources, Status Bar, Unit Autocomplete, Equivalent

Branch: `fix/matcard-navigator-groups` (from main @ `3dbfb3f`). Five defects
reported by the owner from a live Windows run of the Material Card (MatCard),
fixed on this branch. Not pushed / not merged — owner approval pending.

Files touched:

- `cod/app/src/Views/MatCard.xaml` / `MatCard.xaml.cs` — defects 1, 2, 3, 4, 5 (labels)
- `cod/app/src/Services/Inventory.cs` — `MatRepo.LoadUnitNameSuggestions()` (defect 4)
- `cod/app/src/Services/StkPost.cs` — unit-equivalent conversion in document posting (defect 5)
- `src/ShouTech.Infrastructure/CommercialDocumentRepository.cs` — same conversion for delivery notes (defect 5)
- `cod/app/src/Resources/Lang/{En,Ar,Fr,Tr}.json` — new key + renamed labels
- `tests/Kern.Tests/UnitEquivalentPostingTests.cs` (new), `tests/Kern.Tests/SalesChainPostingTests.cs` — defect 5 facts

No migration was added.

---

## Defect 1 — Item number moved into the record navigator

**Was:** the navigator showed a `TxtNavPos` TextBlock ("1 / 4"); the item
number (`TxtItemNo`) sat in the header form next to the Inactive checkbox.

**Now:**

- `TxtNavPos` is deleted (XAML + its only code reference in `UpdateNav()`).
- The same `TxtItemNo` field (same `x:Name`, so `BindToUi`/`ClearForm` keep
  working unchanged) sits in the navigator between Previous and Next:
  width 72, height = `ControlHeight` (matches the nav buttons), centered,
  LTR (direction-neutral numeric content), tooltip from `MatCard.ItemNo`.
- The header form lost the Item # label + field; `TxtCode` now spans the
  freed field columns (`Grid.ColumnSpan="3"`) so every field edge stays on
  the shared form grid (proportions standard preserved).
- `UpdateNav()` now writes the current record's number into the field on
  every navigation; the arrows behave exactly as before.
- The field is editable in every mode (`SetEditMode` exempts it like
  `TxtGoto`): typing a number and pressing Enter jumps to the record
  carrying that number **within the same `_cache` set the navigator pages
  through** (`TxtItemNo_KeyDown`). A number with no matching record leaves
  the current record in place, restores the field, reports
  `ErrorCodes.NavigationFailed` (UI-7004) through `AppHost.Errors`, and
  shows the new localized warning `MatCard.RecordNumberNotFound`
  (added to all four catalogs).

## Defect 2 — Group vs Category sources

### Storage trace (what backs what)

| Concept | Window | Storage | Key columns |
|---|---|---|---|
| الأصناف (categories) | CatWin — "Category Card" (بطاقة صنف) | `material_groups` via `MatRepo.LoadGroups()` / `MatRepo.SaveGroup()` (`Services/Inventory.cs`) | `group_code`, `group_name`, `group_name_en`, `parent_code` (tree) |
| مجموعات المواد (material groups) | MatGroupWin (مجموعات المواد) | **the same** `material_groups` via `MatRepo.LoadGroups()`; membership in `material_group_items` (migration 018) via `MatGroupRepo` | same rows; membership keyed by `group_code` |
| (orphaned) product categories | — no window — | `product_categories` (sqlite mig 014 / pg mig 017), repo `ProductCategoryRepo` | `category_id`, `category_code`, names |

Findings, with references:

- **CatWin is the sole creator of `material_groups` rows** (`SaveGroup` is
  called only from CatWin); it presents them as a tree built from
  `parent_code` (CatWin `LoadCards`/`BuildTree`). MatGroupWin reads the
  same full list flat and manages which materials belong to each group
  (`material_group_items`, backfilled from `materials.group_code` by
  migration 018). Its "Manage group cards" button opens CatWin.
- **`product_categories` is orphaned**: `ProductCategoryRepo` has zero
  callers; no window populates the table; the only other references are a
  DataExportSvc dataset and the recycle bin. MatCard's old Category source
  (`MatRepo.LoadProductCategories()`) therefore listed a table nothing
  maintains — in practice an empty combo.
- **No discriminator exists** between "categories" and "groups" in
  storage: both windows read every non-deleted `material_groups` row.
  The `is_sales_class` flag on the table is never set by any UI (dead).
  Membership (`material_group_items`) distinguishes *member materials*,
  not kinds of groups.

### Resolution (minimal honest separation — no migration)

- **الصنف (`CmbCategory`)** now lists the cards CatWin manages:
  `InitCategories()` reads `MatRepo.LoadGroups()`, presents them in
  CatWin's tree order (new `OrderCardsAsTree` helper: roots first, each
  followed by its children depth-first), option key = `group_code`,
  display language-aware "code - name". Selection still persists to
  **`materials.category`** via the existing `Collect()`/`SetCategory()`
  paths, and the pre-existing stored-value fallback is preserved: a stored
  value missing from the list (e.g. a legacy `product_categories` id) is
  added as a single fallback option so it displays and re-saves instead of
  being silently cleared.
- **المجموعة (`CmbGroup`)** keeps listing MatGroupWin's operational
  groups: `MatRepo.LoadGroups()` → persists to **`materials.group_code`**
  (unchanged). The wholesale `"[Auto]"` fallback (every distinct
  `materials.group_code` presented as a competing source) is **removed**;
  `InitGroups(preferredValue)` now mirrors `InitCategories`: a stored
  value missing from the list survives only as a single fallback entry.
- `MatRepo.LoadProductCategories()` remains in place (now unused by
  MatCard) for the export/recycle-bin surface that still references the
  table.

**Ambiguity the owner should know:** because CatWin and MatGroupWin share
one table with no discriminator, the two combos now draw on the same card
store — they differ in *role and persistence column* (classification →
`materials.category`; operational group → `materials.group_code`), which
is the separation the current data model supports. A true store-level
separation (adopting the orphaned `product_categories` for الصنف, or a
card-kind discriminator column on `material_groups`) requires a migration
and a data decision — deliberately **not** done here, per the task gate.

## Defect 3 — UserInfo type name in MatCard's status bar

**Was:** `MatCard.GetCurrentUsername()` reflected over `AppState`
property names; `"CurrentUser"` matched the `UserInfo` **object**, and
stringifying it rendered `"...pp.Services.UserInfo"` into `TxtStatusUser`
in MatCard's own bottom bar (next to the company name).

**Fix:** `GetCurrentUsername()` now reads `AppState.CurrentUser` directly
and returns `FullName`, falling back to `Username` — the same value
`ShellContext.CurrentUserName` exposes. The reflection pattern existed
only in MatCard (verified by search); the company-name reflection beside
it reads a string property and works, so it was left as is.

## Defect 4 — Units-grid autocomplete

**Was:** the الوحدة column of `DgUnits` was a plain text cell; no
type-to-search pattern existed anywhere in the app (searched: no
`DataGridComboBoxColumn`/`DataGridTemplateColumn` anywhere; InvoiceWin's
material "find" only focuses a cell).

**Now:**

- The unit column is a `DataGridTemplateColumn` (still column index 1,
  width 130, same localized header): display template = the existing
  `UnitTextCellStyle` TextBlock; editing template = an editable
  `ComboBox` (`IsEditable`, `IsTextSearchEnabled=False`,
  `StaysOpenOnEdit=True`) bound to `UnitName` with
  `UpdateSourceTrigger=LostFocus` — the same commit semantics as the old
  text cell, so persistence (`Collect()` → unit1..unit5 + units_json) is
  unchanged and a genuinely new typed name saves exactly as before.
- Suggestions come from the new `MatRepo.LoadUnitNameSuggestions()`:
  `units_of_measure.uom_code` for the current company (active, not
  deleted; provider-aware boolean literals) **merged with** the distinct
  unit names already saved on materials' `unit1..unit5` rows — the master
  can be empty in a fresh company (nothing else populates it), so the
  merge guarantees suggestions exist whenever units exist anywhere. Each
  source reports failures separately via
  `ErrorCodes.InventoryOperationFailed`; results are case-insensitively
  distinct and sorted. MatCard additionally merges the current card's
  in-memory unit rows and refreshes the list after every save.
- Typing filters by **CONTAINS** (`OrdinalIgnoreCase`, Arabic-safe):
  `DgUnits.PreparingCellForEdit` feeds the editor the suggestion list and
  hooks its text box; each keystroke re-filters and opens the dropdown
  when matches exist.
- Enter behavior stays consistent with the central grid rules: the
  central `DataGridTableBehavior` already leaves Enter to a ComboBox
  whose dropdown is open (it accepts the highlighted suggestion, then
  the behavior commits and advances horizontally); Enter with the
  dropdown closed moves horizontally as before. The column is not
  read-only, so it stays part of the behavior's editable-column walk.

## Defect 5 — المعامل renamed to المعادل, and the equivalent now drives stock posting

### Rename

The units-grid column is the unit's **equivalent relative to the
material's first (base) unit** (owner's definition: first unit = 1 kg →
equivalent 1; second unit = 250 g → equivalent 0.25; base quantity =
quantity × equivalent). Only the MatCard units labels changed — the Lang
*keys* (`MatCard.Factor`, `MatCard.GridFactor`) are unchanged, the
*values* became: En "Equivalent", Ar "المعادل", Fr "Équivalent",
Tr "Eşdeğer". No other "coefficient" concept in the app uses these keys.
Corroboration: the POS unit chooser already labeled this same value
"Equivalent".

### Consumption trace — the posting path ignored the unit entirely ⚠

- InvoiceWin saves through `InvRepo.Post`, which calls
  **`StkPost.ApplyDocument`** — the single posting choke point for sales
  invoices, purchase invoices, POS sales, returns, and stock documents.
  Document lines carry the chosen unit name (`InvLine.Unit`), but
  `ApplyDocument` **never read it**: it posted `line.Qty` raw. Selling
  4 × 250 g deducted 4 (kg) instead of 1 kg. Purchases were symmetric
  (raw quantity in). Invoice reversal posts a mirror document through
  the same path, so it inherits the fix symmetrically.
- The chain writer, `CommercialDocumentRepository.PostSalesAsync`,
  posts stock only for **delivery notes** — it did not even select the
  line's `unit` column and deducted raw quantity. Its sales-invoice
  branch posts only the party ledger (no double stock movement).
- No other consumer converts quantities by the factor (POS's unit
  chooser sets name + price only; manufacturing writes base-unit BOM
  quantities and is out of scope).

### ⚠ STOCK MATH CHANGE (owner-defined rule, implemented)

Both posting writers now convert every line through a resolver
(`StkPost.ResolveUnitEquivalent` / the chain repo's
`ResolveUnitEquivalentAsync`, same rules):

- base_qty = line_qty × equivalent(chosen unit), resolved from the unit
  rows saved on the material card (`units_json` first — it carries the
  complete list; `unit1..unit5` column pairs as the legacy fallback).
- The chosen unit **is** the first unit → equivalent 1 by definition.
  No unit chosen, an unknown/free-typed unit, or a stored equivalent
  ≤ 0 → 1 (quantity treated as already in base units; no conversion is
  invented for units the card does not define).
- The movement's `unit_cost` is restated per base unit
  (`line price / equivalent`, 6 dp) so movement qty × unit_cost keeps
  equalling the line value, and the moving-average math stays coherent.
- Stored numbers keep their values; only the interpretation is aligned
  to the owner's definition. Documents posted **before** this change
  moved raw quantities — their movements are historical records and are
  not rewritten.

Note (base-row affordance, as instructed — noted, not changed): the
first unit row's equivalent cell stays freely editable in MatCard;
posting always treats the first unit as equivalent 1 regardless of what
is stored there.

### Evidence

New Kern.Tests facts (execution owed on Windows — the net8.0-windows
testhost cannot run on this Linux machine, same as the rest of the
suite):

- `UnitEquivalentPostingTests` (5 facts): sales in the second unit
  deducts exactly qty × 0.25 (4 × g250 → 1 kg out, balance 10 → 9,
  movement unit_cost restated to 10); purchase adds symmetrically
  (10 → 11); base-unit line posts raw; unknown unit posts raw;
  legacy columns-only card (no `units_json`) converts.
- `SalesChainPostingTests.PostingDeliveryNote_InSecondUnit_DeductsQuantityTimesEquivalent`:
  delivering 10 PCS (equivalent 0.25) deducts exactly 2.5 base units.
  The pre-existing delivery-note fact (no material card seeded) still
  expects raw quantities — the resolver returns 1 when no card exists.

---

## Gates

- Full solution build `--no-incremental`: **0 errors, 0 warnings**
  (result recorded at commit time).
- `tools/audit_window_context.py`: exit 0 (MatCard row central; the new
  navigator field's LTR pin is in the audit's justified direction-neutral
  control list, like the position text it replaced).
- Lang parity: 0 missing keys in every catalog relative to En;
  `MatCard.RecordNumberNotFound` present in all four; renamed
  Factor/GridFactor values present in all four.
- Migrator: unchanged — no migration added.

## Still owed (Windows / live checks)

- Runtime pass on Windows: navigator Enter-jump, autocomplete dropdown
  interaction (including RTL + Arabic input), status-bar name.
- Kern.Tests execution on Windows (includes the six new facts).
- Live PostgreSQL pass for the new queries (provider-aware literals are
  written, not yet exercised against a live PG).
