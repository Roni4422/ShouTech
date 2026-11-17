# MatCard Fixes — Navigator, Group/Category Sources, Status Bar, Unit Autocomplete, Equivalent

Branch: `fix/matcard-navigator-groups` (from main @ `3dbfb3f`). Eight defects
reported by the owner from a live Windows run of the Material Card (MatCard),
fixed on this branch. Not pushed / not merged — owner approval pending.

Files touched:

- `cod/app/src/Views/MatCard.xaml` / `MatCard.xaml.cs` — defects 1, 2, 3, 4, 5 (labels)
- `cod/app/src/Services/Inventory.cs` — `MatRepo.LoadUnitNameSuggestions()` (defect 4)
- `cod/app/src/Services/StkPost.cs` — unit-equivalent conversion in document posting (defect 5)
- `src/ShouTech.Infrastructure/CommercialDocumentRepository.cs` — same conversion for delivery notes (defect 5); service-material skip (defect 7)
- `src/ShouTech.Infrastructure/MfgOrderRepository.cs` — service-material skip in manufacturing post/cancel (defect 7 expansion)
- `cod/app/src/Resources/Lang/{En,Ar,Fr,Tr}.json` — new key + renamed labels
- `tests/Kern.Tests/UnitEquivalentPostingTests.cs` (new), `tests/Kern.Tests/SalesChainPostingTests.cs` — defect 5 facts
- `tests/Kern.Tests/MaterialCardRoundTripTests.cs` (new) — defect 6 round-trip + isolation facts
- `tests/Kern.Tests/ServiceMaterialPostingTests.cs` (new) — defect 7 service-material facts

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

## Defect 6 — Full persistence round-trip audit (every field, every section)

Method (file-driven, per the owner's instruction): every input control,
checkbox, combo **and its option set**, grid column, JSON-backed
collection, image path, and record flag was enumerated from
`MatCard.xaml` + `MatCard.xaml.cs` and compared, column by column,
against the save SQL (`MatRepo.Save` INSERT/UPDATE + the four child
tables) and the load SQL/readers (`MatRepo.LoadById` → `Map`).
Repository mechanics were verified programmatically: the 164-column
SELECT list against `Map`'s readers (143 value + 20 boolean mappings —
**0 mismatches**), and Save's 165 bound parameters against its
INSERT/UPDATE column lists (every column bound, every parameter used).

### Comparison table — control/option → property → column → saved → loaded → verdict

**General — header + وصف المادة (PanelDesc)**

| Control | Record property | DB column/table | Saved | Loaded | Verdict |
|---|---|---|---|---|---|
| TxtCode | Code | materials.code | ✓ | ✓ | round-trips |
| CmbGroup (المجموعة) | GroupCode | materials.group_code | ✓ | ✓ | round-trips (defect 2) |
| CmbItemType (النوع) | ItemType | materials.item_type | ✓ | ✓ | round-trips; options below |
| CmbStatus ⇄ ChkInactive | Status | materials.status | ✓ | ✓ | synced pair, one store |
| TxtName / TxtNameEn | Name / NameEn | name_ar / name_en | ✓ | ✓ | round-trips |
| CmbCategory (الصنف) | Category | materials.category | ✓ | ✓ | round-trips (defect 2) |
| TxtBrand / TxtModel / TxtSizeSpec | Brand / Model / SizeSpec | brand / model / size_spec | ✓ | ✓ | round-trips |
| CmbColor | Color | materials.color | ✓ | ✓ | round-trips (F3 for out-of-list) |
| TxtManufacturer / TxtOriginCountry / TxtSource | Manufacturer / OriginCountry / Source | manufacturer / origin_country / source | ✓ | ✓ | round-trips |
| TxtAltCode / TxtGtin / TxtSupplierCode | AltCode / Gtin / SupplierCode | alt_code / gtin / supplier_code | ✓ | ✓ | round-trips (per-material numbers) |
| CmbAbcClass | AbcClass | materials.abc_class | ✓ | ✓ | round-trips |
| ChkIsStocked / IsPurchased / IsSold / IsProduced / IsService | IsStocked / IsPurchased / IsSold / IsProduced / IsService | is_stocked / is_purchased / is_sold / is_produced / is_service | ✓ | ✓ | round-trips (service interplay: defect 7) |
| TxtNotes | Notes | materials.notes | ✓ | ✓ | round-trips |

**Combo option sets — every option value persists and loads back as the same option**

| Combo | Options | Store | Verdict |
|---|---|---|---|
| CmbItemType | Stock / Service / NonStock / Kit | item_type (exact string) | ✓ each option re-selects on load; Service also normalizes the flags (defect 7) and still loads back as Service |
| CmbStatus | Active / Inactive | status | ✓ (ChkInactive mirrors the same value both ways) |
| CmbCostMethod | WeightedAvg / FIFO / LIFO / Standard / LastPurchase | cost_method | ✓ all five present in both option sources (initial array + RefreshStaticCombo) |
| CmbAbcClass | A / B / C (+ empty = none) | abc_class | ✓ |
| CmbPriceCurrency | SYP / USD / EUR / TRY / SAR / AED | price_currency | ✓ built-ins; any other stored code now survives via F3 |
| CmbColor | None / White / Black / Red / Green / Blue / Yellow / Orange / Brown / Gray / Silver / Gold | color | ✓ "None" persists literally and re-selects |
| CmbGroup / CmbCategory / CmbDefaultWarehouse | dynamic keys (group codes / category cards / warehouse codes) | group_code / category / default_warehouse | ✓ with stored-value fallbacks (defect 2, F3) |

**Units & Prices (PanelUnits + DgUnits)**

| Control / column | Record property | DB column/table | Saved | Loaded | Verdict |
|---|---|---|---|---|---|
| TxtMinQty / TxtMaxQty | MinQty / MaxQty | min_qty / max_qty | ✓ | ✓ | round-trips |
| CmbPriceCurrency | PriceCurrency | price_currency | ✓ | ✓ | round-trips |
| TxtCost | Cost | materials.cost | ✓ | ✓ | **fixed F2** |
| CmbCostMethod | CostMethod | cost_method | ✓ | ✓ | round-trips |
| TxtTaxRate | TaxRate | tax_rate | ✓ | ✓ | round-trips |
| ChkIsTaxable / ChkPriceIncludesTax | IsTaxable / PriceIncludesTax | is_taxable / price_includes_tax | ✓ | ✓ | round-trips |
| ChkHasSerial / ChkHasExpiry / ChkHasBatch | HasSerial / HasExpiry / HasBatch | has_serial / has_expiry / has_batch | ✓ | ✓ | round-trips (serial/expiry/batch settings are per-material) |
| TxtShelfLifeDays | ShelfLifeDays | shelf_life_days | ✓ | ✓ | round-trips |
| DgUnits # (LineNo) | MatUnitRow.LineNo | units_json (renumbered on collect) | ✓ | ✓ | round-trips |
| DgUnits Unit | UnitName | units_json + unit1..unit5 | ✓ | ✓ | round-trips (autocomplete: defect 4) |
| DgUnits Equivalent | Factor | units_json + unit1_factor..unit5_factor | ✓ | ✓ | round-trips (meaning: defect 5) |
| DgUnits Sale | PriceSale | units_json + price_list (unit 1) | ✓ | ✓ | round-trips |
| DgUnits Purchase | PricePurchase | units_json | ✓ | ✓ | round-trips |
| DgUnits Wholesale / Retail / Special / Export / Distributor | PriceWholesale / PriceRetail / PriceSpecial / PriceExport / PriceDistributor | units_json + price_wholesale / price_retail / price_special / price_export / price_distributor (unit 1) | ✓ | ✓ | round-trips |
| DgUnits Barcode | Barcode | units_json + unit1_barcode..unit5_barcode | ✓ | ✓ | round-trips |

**Stock (PanelStock)**

| Control | Record property | DB column/table | Saved | Loaded | Verdict |
|---|---|---|---|---|---|
| TxtQtyOnHand | QtyOnHand | qty_on_hand | preserved | ✓ | display-only in the window (read-only); the loaded value is carried through every save via CloneSparse — stock itself lives in material_stock_balances |
| TxtReorderQty / TxtSafetyStock | ReorderQty / SafetyStock | reorder_qty / safety_stock | ✓ | ✓ | round-trips |
| CmbDefaultWarehouse | DefaultWarehouse | default_warehouse | ✓ | ✓ | round-trips |
| TxtShelfBin / TxtLeadTimeDays | ShelfBin / LeadTimeDays | shelf_bin / lead_time_days | ✓ | ✓ | round-trips |
| TxtWeightNet / TxtWeightGross | WeightNet / WeightGross | weight_net / weight_gross | ✓ | ✓ | round-trips |
| ChkAllowNegative / ChkTrackLocation / ChkQualityCheck | AllowNegative / TrackLocation / QualityCheckRequired | allow_negative / track_location / quality_check_required | ✓ | ✓ | round-trips |

**Accounts (PanelAccounts)**

| Control | Record property | DB column/table | Saved | Loaded | Verdict |
|---|---|---|---|---|---|
| TxtAccInventory / AccSales / AccSalesReturn / AccCogs | AccInventory / AccSales / AccSalesReturn / AccCogs | acc_inventory / acc_sales / acc_sales_return / acc_cogs | ✓ | ✓ | round-trips |
| TxtAccPurchase / AccPurchaseReturn / AccExpense / AccWip | AccPurchase / AccPurchaseReturn / AccExpense / AccWip | acc_purchase / acc_purchase_return / acc_expense / acc_wip | ✓ | ✓ | round-trips |
| ChkAllowSale / AllowPurchase / AllowReturn / SalesBlocked | AllowSale / AllowPurchase / AllowReturn / SalesBlocked | allow_sale / allow_purchase / allow_return / sales_blocked | ✓ | ✓ | round-trips |

**Specifications & Images (PanelExtra + side panel)**

| Control / column | Record property | DB column/table | Saved | Loaded | Verdict |
|---|---|---|---|---|---|
| DgAttributes Group | MatAttrRow.Group | material_attributes.attr_group | ✓ | ✓ | round-trips (child table) |
| DgAttributes Key | MatAttrRow.Key | material_attributes.attr_key | ✓ | ✓ | round-trips |
| DgAttributes Value | MatAttrRow.Value | material_attributes.attr_value | ✓ | ✓ | round-trips |
| (row order) | MatAttrRow.SortOrder | material_attributes.sort_order | ✓ | ✓ | **fixed F4** (renumbered by grid position) |
| TxtImagePath + ImgPreview | ImagePath | materials.image_path | ✓ | ✓ | round-trips; preview clears when empty |

**JSON-backed collections vs their columnar counterparts**

| Representation | Written by | Read by | Divergence verdict |
|---|---|---|---|
| units_json (complete unit list) vs unit1..unit5 / factors / barcodes / unit-1 price columns | ONLY MatRepo.Save, both from the same MatRec in the same transaction; DatWin/STEI exchange moves whole materials rows (both representations travel together); no other code writes either representation (verified by search) | Card prefers units_json, falls back to columns; POS and the defect-5 posting resolver use the same json-first/columns-fallback rule | **cannot diverge** through any app path |
| prices_json | nobody in the app writes it today (preserved verbatim by CloneSparse); live price data is units_json + price columns + material_price_list child rows | loaded by Map, consumed by nothing | inert legacy column — own line per the audit rule: persisted ✓ (preserved), shown ✗ (no control), verdict: harmless, flagged for the owner's awareness |
| attributes_json | same as prices_json | same | inert legacy column — the live store is the material_attributes child table; preserved on save, never shown |

**Persisted columns the card never shows** (own line per the audit rule):
the ~60 sparse materials columns with no MatCard control — print/search
names, old/customer/manufacturer codes, HS code, SKU, POS name,
sub-category, grade, classes 1–5, product line, season, lifecycle,
make-or-buy, stock stage, weight/volume/dimension units and measures,
pack quantities, brand owner, warranty, catalogue/web/tags, purchase/sales
unit, standard/last-purchase/average cost, cost currency, overhead,
landed cost, last purchase date, online price, margin/discount limits,
lot/order-multiple/EOQ, replenishment method, expiry-alert days, lot
flag, tax code/account/type, zero-rated, commission, bonus, min/max sale
quantities, second image, attachment, description, internal notes, spec
sheet, label text, prices_json, attributes_json — are all **loaded by
Map and preserved verbatim on every card save via CloneSparse**
(mechanically verified: Collect ∪ CloneSparse covers every MatRec
property MatRepo.Save binds). One exception was found and fixed (F1:
PriceMin was in neither and was actively zeroed).

**Intentionally non-persistent (UI state, not material data):** TxtGoto
(search box), TxtItemNo (record identity — display + navigator jump,
defect 1), TxtMeta / TxtStatusCompany / TxtStatusUser / TxtStockHint
(status texts).

### Gaps found and fixed

- **F1 — `price_min` zeroed on every save.** `Collect()` assigned the
  constant `m.PriceMin = 0` and CloneSparse did not carry the loaded
  value: the historical constant-overwrite class. Fix: the constant is
  gone; `PriceMin` is preserved via CloneSparse like every other
  non-UI field.
- **F2 — the Cost field did not round-trip.** After collecting
  `TxtCost`, Collect overwrote `m.Cost` with unit 1's grid purchase
  price whenever that cell was filled, so the section's Cost field came
  back showing a different value than the user saved. Fix: TxtCost is
  authoritative for `materials.cost`; the grid purchase price keeps its
  own store (`units_json`). **Behavior change, flagged:** cards saved
  after this fix keep the Cost the user typed even when unit 1 has a
  purchase price.
- **F3 — non-editable combos silently cleared out-of-list stored
  values.** Status/ItemType/CostMethod/AbcClass/PriceCurrency/Color are
  non-editable combos with fixed option lists. A stored value outside
  the list (written by an import or another module — e.g. currency
  "GBP", color "Purple") matched no option: the combo showed blank and
  the next save wrote null, erasing the value. Fix: `SetCombo` now
  appends a single stored-value fallback option (the same pattern as
  InitCategories/InitGroups), so such values display and round-trip.
- **F4 — attribute row order drifted.** Grid-added attribute rows carry
  SortOrder 0 and jumped to the top on reload (load orders by
  sort_order). Fix: Collect renumbers SortOrder by grid position, like
  the unit rows' LineNo.
- **F5 — isolation bleed in ClearForm (see below).**

### Per-material isolation verdicts

- **Navigation isolation — PASS.** `BindToUi` clears `_units` and
  `_attrs` before refilling them from the newly loaded record (a record
  with no child rows leaves both grids empty); every scalar field is
  overwritten; `LoadImage` nulls the preview when the record has no
  image. `ClearForm` resets every field, both grids, and the image.
- **Save isolation — PASS at the repository.** On update, Save deletes
  child rows scoped `WHERE company_id=$company AND material_id=$id`
  for all four child tables, then inserts the record's own rows under
  its own id — saving material A cannot write or delete material B's
  specifications, barcodes, price lines, or suppliers. Units live in
  the material's own row (units_json + unit columns).
- **F5 — typed-text bleed in editable combos — FIXED.** `ClearForm`
  cleared `SelectedIndex` but not `Text` on the editable combos
  (CmbGroup, CmbDefaultWarehouse): free text typed for one material
  survived into the New form and could be saved onto the next material.
  ClearForm now clears their text as well.

### Evidence

`tests/Kern.Tests/MaterialCardRoundTripTests.cs` (2 facts, execution
owed on Windows): a fully populated record round-trips every card field
through Save → LoadById (including attributes, extra barcodes, price
lines, suppliers, PriceMin, QtyOnHand), and an update that rewrites
material A's children leaves material B's name, attributes, and units
untouched. The window-level Collect/BindToUi mapping is proven by the
trace table above (the WPF window is not instantiated by the test host).

## Defect 7 — Service materials are non-stock, end to end

Owner rule: a service material (مادة خدمية) can be sold and purchased
but is not warehouse stock and must never affect stock quantities.

**Link 1 — MatCard: DIVERGED, FIXED.** Three controls define
service-ness — the النوع combo (Service option), the خدمية checkbox
(ChkIsService), and the مخزنية checkbox (ChkIsStocked) — and nothing
kept them in agreement: a material could be saved as type Service with
IsStocked on, or flagged Service with type Stock. Now each control's
gesture brings the others into agreement (choosing the Service type
checks Service and unchecks Stocked; checking Service does the same;
checking Stocked ends service state), `BindToUi` normalizes legacy
inconsistent rows on display, and `Collect` normalizes the record
itself (any service indication ⇒ type Service, IsService on,
IsStocked off).

**Link 2 — Invoice/POS posting: HELD for flags, HARDENED for type.**
`InvRepo.Post` writes the party ledger / journal from the document
totals first (all lines, services included — the accounting side posts
normally), then calls `StkPost.ApplyDocument` for inventory effects
only; its guard already skipped lines whose material is not stocked
or is flagged service. POS posts through the same InvRepo.Post path.
Gap: the guard never consulted `item_type`, so a legacy row typed
Service with unsynced flags still moved stock. `TryGetStockableMaterial`
now also treats `item_type = 'Service'` as non-stock. Stock documents
(in/out, counts via balances, opening) share the same guard.
Purchases are symmetric (no stock-in for services).

**Link 3 — Chain delivery notes: DIVERGED, FIXED.**
`CommercialDocumentRepository`'s delivery-note posting had NO
stockability check at all — a service on a delivery note deducted
stock. It now skips lines whose material card marks them non-stock
(same three-view rule); a line whose material has no card row keeps
the historical behavior.

**Link 4 — Balances/reports: HELD.** Stock reports read
`material_stock_balances`; a balance row is only ever created by the
posting paths above, so a material that has always been a service
never appears with a stock quantity. (A balance created while a
material was stocked truthfully remains if the material is later
converted to a service — that stock physically exists; the report
shows balances, not card types.) Stock counts are built from balance
rows, so services are never counted.

**Evidence:** `tests/Kern.Tests/ServiceMaterialPostingTests.cs`
(5 facts: service by flags, service by type only, service purchase,
mixed stock+service document, not-stocked purchase) and a chain fact
(delivery note for a service material creates no movement and no
balance). Execution owed on Windows, as with the rest of the suite.

### Expansion — every consumer of materials, per-window verdicts

The audit was broadened from the posting paths to every window/report
that reads or acts on materials:

| Window / path | Knows service? | Treatment | Verdict |
|---|---|---|---|
| InvoiceWin (sales/purchase/returns/quotations) | via StkPost guard | Quotation posts nothing at all; invoice lines for services skip only the inventory effect — ledger/journal post in full; the material picker does not filter services out (they stay sellable/purchasable); no entry-time stock checks block them | already correct (guard hardened for type-only rows) |
| PosWin / PosCheckoutWin | via InvRepo.Post → StkPost | Same guard; POS finds services by scan/lookup (LoadById, unfiltered) | already correct |
| OrdWin (sales/purchase orders) | n/a | Orders post no GL and no stock (by design); conversion creates invoices/deliveries through the guarded paths | already correct |
| Chain delivery notes (CommercialDocumentRepository) | **no → fixed** | Had no stockability check at all; now skips non-stock lines (three-view rule) | **fixed** |
| WhsWin stock count / adjustment | yes | Rows come from `MatRepo.LoadStockRows`, which filters `is_stocked=1 AND is_service=0` — services are never listed, so they can never be counted into stock; the type-only case is now filtered too | already correct + hardened |
| WhsWin warehouse transfer | by construction | Transfer grid is fed from stock balances; a service has none and cannot be selected; `StkPost.PostTransfer` additionally refuses insufficient-balance lines | already correct |
| WhsWin move-history (merge) tool | n/a | Administrative merge of one material's history into another; existence operation, no stock creation | already correct |
| Manufacturing (MfgWin → MfgOrderRepository) | **no → fixed** | Post consumed/produced ANY material into stock. Now: non-stock components contribute cost but no movement/balance; a non-stock finished item posts costs/status without entering stock; Cancel mirrors the same skips. (The unused CardStore.PostOrder copy has no callers.) | **fixed** |
| Stock balances / valuation (StkBalWin, RptQuery.StockBalancesAsync) | by construction | Read material_stock_balances only; services never have rows | already correct |
| Shortage / reorder list (ShortListWin) | by construction | Built from StockBalancesAsync rows only | already correct |
| Movement reports | by construction | Read material_stock_movements; services have none | already correct |
| Expiry report (ExpiredWin) | data-driven | Lists materials flagged has_expiry with on-hand summed from balances (0 for a service); creates nothing. A service carrying has_expiry is a card-data choice, not a stock effect | already correct (noted) |
| MatListWin / PriceListWin | n/a | Directory/price listings — no stock quantities shown; services correctly included | already correct |
| MatDirWin / SerWin lookups | n/a | Unfiltered LoadAll — services are FINDABLE for selling/buying (exemption is from stock, not existence) | already correct |
| BarWin / BcdGenWin (barcode labels) | n/a | LoadAll for label printing; services legitimately carry barcodes | already correct |
| DatWin exports (DataExportSvc) | n/a | Raw table datasets (materials incl. its flags; balances/movements as stored) — no stock computation; a service simply has no balance/movement rows to export | already correct |
| Dashboard (MWin) | n/a | No direct stock computation over materials found | already correct |

## Defect 8 — Labels floated far from their fields

**Before:** MatCard's own local `Lbl` label style aligned label text to
the reading-start edge in LTR but, in RTL, its DataTrigger set
`TextAlignment=Left` — the opposite of the app-wide convention — which
pushed Arabic labels to the FAR edge of their label column, leaving a
wide empty band (up to ~80px for short labels) between the label text
and its field in the owner's Arabic UI. The description (PanelDesc),
stock (PanelStock), and accounts (PanelAccounts) sections also used
literal label-column widths (115 / 100 / 110) instead of the shared
token, so field edges did not coincide across sections.

**After (layout only — no field order, content, or behavior change):**

- `Lbl` now mirrors the app convention exactly (CatWin's `FieldLabel`
  pattern): default Left, RTL → Right, LTR → Left — label text sits at
  the field-adjacent edge of its column in both directions, with the
  few-pixel margin as the only gap. The label margin is symmetric
  (`5,0,5,0`) so the field-adjacent side keeps its small gap in RTL too.
- PanelDesc / PanelStock / PanelAccounts label columns now use
  `{StaticResource FormLabelColumnWidth}` (120), joining the header
  form on the one shared grid; field edges align vertically across all
  sections.
- The Units section's compact numeric strip keeps its content-sized
  70px columns (six small fields in a row by design); the alignment
  fix closes its label gap as well. PanelExtra's Auto label column is
  content-sized by construction.

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
- Kern.Tests execution on Windows (includes the new facts: 5 unit-equivalent
  + 2 delivery-note, 2 round-trip/isolation, 5 service-material + 1 chain
  service).
- Live PostgreSQL pass for the new queries (provider-aware literals are
  written, not yet exercised against a live PG).
