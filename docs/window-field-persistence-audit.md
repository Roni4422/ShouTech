<!-- source: docs/window-field-persistence-audit.md -->

# Window Field and Database Persistence Audit — COMPLETE FIELD-LEVEL MATRIX

- Repository: `Roni4422/ShouTech`
- Branch audited + fixed: `feat/legacy-windows-rebuild` (from `main` @ b792661)
- Audit date: 2026-11-17 (supersedes the 2026-10-16 partial audit, which covered 15 windows at handler level only)
- Audit type: static review of the actual save/load code paths for **every** window (Views/, code-built and XAML), extended on this branch with runtime round-trip smoke tests for every window whose persistence was (re)built here (see §8).
- Owner directives covered: (1) every field and every editable grid cell must round-trip to the database file; (2) old windows rebuilt to the Phase 3 standard; (3) shell-context directive — every window must follow the main window (MWin) in language+direction, company, theme and user preferences (§3).

## 1. Status legend

| Status | Meaning |
|---|---|
| VERIFIED | The UI value is written on save **and** read back into the UI on load (code-traced; smoke-tested where marked ◆) |
| GAP — NEVER_SAVED | Field is editable in the UI but never written |
| GAP — NEVER_LOADED | Value is saved but never read back into this window |
| GAP — NO_COLUMN | No database column exists for the field |
| GAP — GRID_EDIT_LOST | Grid cell edits are not committed before save |
| GAP — FAKE_SAVE | Handler shows success (or a placeholder) without persisting |
| GAP — PARTIAL | Round-trips with a lossy/asymmetric path |
| READ_ONLY_OK | Display-only by design; data comes from a verified query |
| FIXED | Was a gap at audit time; fixed on this branch (commit noted in §7) |

## 2. Executive summary

- **Windows audited: 59** (38 window families + 19 report windows + dialogs), **~700 fields / grid columns traced** to a DB column or a named settings store.
- The 2026-10-16 audit's P0 list is fully resolved on this branch for the windows owned here: CstCard, BnkCard, CurCard, RepCard, MfgWin were fake-save message boxes — all five now persist through the new `CardStore` service against migration **050** tables, with RBAC, transactions and soft delete (§4). JrnWin, listed as P0 in October, was found **already fully wired** (JournalService) — the old audit was stale for it; its real gaps (draft re-edit, hardcoded strings, hardcoded currency) are recorded in §5.
- New gaps found by this deeper audit and fixed here (§7): MatCard qty_on_hand **data loss on every save**, CoaWin chart import **fake save**, WhsWin Is Active broken in both directions, MatDirWin open/print fakes, PosCheckoutWin payment method **never persisted** (every POS sale posted as Cash), InvoiceWin lines persisted without material/warehouse ids, BarWin with **no save path at all**, PayRecWin allocation note and ProjWin task notes dropped, CatWin parent not settable, StpWiz fabricated sample company.
- Remaining gaps are listed in priority order in §9 — the largest is the StpWiz/ComWin dual-store illusion (companies.json round-trips ~30 fields; the DB `companies` table receives only 9–11), which needs its own migration + write-path pass (spec in §9.1).

## 3. Shell-context compliance (owner directive, 2026-11-17)

Every window must follow the main window's live context: **(a)** language + direction via Lang catalogs / central direction (no hardcoded strings, no window-pinned direction), **(b)** company via `AppState.CurrentCompanyCode` resolved at open/query time (not captured at construction), **(c)** theme via the central VisuId styles/accent only, **(d)** user preferences — currency from `UsrPref.CurrencyCode`, formats from the current culture services (no hardcoded currency/formats).

Marks: **FOLLOWS** / **HARDCODED** / **STALE-CAPTURED** / **PARTIAL**, with evidence. Windows rebuilt on this branch end FOLLOWS on all four.

| Window | (a) Language+direction | (b) Company | (c) Theme | (d) User prefs |
|---|---|---|---|---|
| CstCard ◆ | FOLLOWS — LocSvc keys incl. new `Cards.*` | FOLLOWS — CardStore resolves `AppState.CurrentCompanyCode` per call | FOLLOWS — central styles | FOLLOWS — culture formats; no currency hardcode |
| BnkCard ◆ | FOLLOWS | FOLLOWS — CardStore per call | FOLLOWS | FOLLOWS — currency default was `"SAR"`, now `UsrPref.CurrencyCode` |
| CurCard ◆ | FOLLOWS | FOLLOWS — CardStore per call | FOLLOWS | FOLLOWS — the Code field is currency *master data* (user-defined), not the operating currency |
| RepCard ◆ | FOLLOWS | FOLLOWS — CardStore per call | FOLLOWS | FOLLOWS |
| MfgWin ◆ | FOLLOWS (print title strings hardcoded EN — PARTIAL, noted) | FOLLOWS — CardStore per call | FOLLOWS | FOLLOWS — culture formats |
| JrnWin | FOLLOWS — was HARDCODED: 6 English MessageBox strings → new `Vouchers.*` keys in all 4 catalogs | FOLLOWS — JournalService scoped per call | FOLLOWS | FOLLOWS — journal currency default was `"SAR"`, now `UsrPref.CurrencyCode` |
| InvoiceWin | FOLLOWS — `Tf(key, fallback)` pattern | FOLLOWS — InvRepo scoped per call | FOLLOWS | FOLLOWS — currency fallback was `"SAR"` (×3 sites), now `UsrPref.CurrencyCode` |
| PosWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS — already read `UsrPref.Load().CurrencyCode` |
| PosCheckoutWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS — currency was HARDCODED `"SAR"`, now `UsrPref.CurrencyCode` |
| BarWin | FOLLOWS — `T(...)` keys; material name pick branches on `App.CurrentLanguageCode` (data choice, not UI language) | FOLLOWS — MatRepo scoped per call | FOLLOWS | FOLLOWS |
| BcdGenWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS |
| BcdDesWin | PARTIAL — designer strings via Loc with fallbacks | FOLLOWS (no company data; design is a user-level file setting) | FOLLOWS | FOLLOWS |
| CashDrawerOptionsWin | HARDCODED — mostly literal English strings (remaining, §9) | FOLLOWS (device settings file) | FOLLOWS | FOLLOWS |
| AccCard | HARDCODED — no Lang keys at all; XAML literals incl. placeholder `"Text"`/`"Content"` (remaining rebuild, §9) | FOLLOWS — AccRepo per call | FOLLOWS | FOLLOWS |
| MatCard | FOLLOWS — Loc keys extensive | FOLLOWS | FOLLOWS | FOLLOWS |
| MatDirWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS |
| CatWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS |
| WhsWin | FOLLOWS (one hardcoded `"[off]"` suffix; OpenStockCount dialog strings) | FOLLOWS | FOLLOWS | FOLLOWS |
| MatGroupWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS |
| CoaWin | HARDCODED — almost everywhere (remaining rebuild, §9) | FOLLOWS — AccRepo per call | FOLLOWS | FOLLOWS |
| PayRecWin | FOLLOWS | FOLLOWS — `CurrentCompanyCode` read at action time | FOLLOWS | FOLLOWS |
| ProjWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS |
| AssetWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS |
| 19 reports (RptBase) | FOLLOWS | FOLLOWS — RptQuery scoped per call | FOLLOWS | PARTIAL — column layouts persist via WinPref; filter values are session-only by design |
| MWin (shell) | Defines context (`UsrPref.Lang` via LangDirSv/LocSvc) | Defines context (`AppState.CurrentCompanyCode`) | Defines context (VisuId accent from UsrPref) | PARTIAL — ctor forces Maximized over saved WinState; current tab never persisted (`UsrPref.LastTab` unused) |
| LogWin | FOLLOWS (one hardcoded `"Verifying..."`) | FOLLOWS | FOLLOWS | FOLLOWS — UsrPref-backed |
| Lang | HARDCODED — the picker itself is not localized (by nature it precedes language choice; noted as dependency, §10) | n/a | FOLLOWS | Writes UsrPref.Lang/LangSet via App startup sequence |
| StpWiz | PARTIAL — `W(...)` keys with English fallbacks + hardcoded XAML defaults | Creates the company context | FOLLOWS | PARTIAL — company defaults are wizard inputs (persisted to companies.json) |
| ComWin | FOLLOWS — Loc.Get with fallbacks | Edits the company context | FOLLOWS | FOLLOWS |
| PrmWin | HARDCODED — XAML literals + VM strings in English (remaining, §9) | FOLLOWS | FOLLOWS | FOLLOWS |
| SplWin | HARDCODED — status strings; forces LTR in ApplyLocalization (central-mechanism dependency, §10) | n/a | FOLLOWS | n/a |
| SerWin | PARTIAL — ApplyLocalizedText is an empty stub; OK/Cancel set manually | FOLLOWS (caller-scoped data) | FOLLOWS | PARTIAL — dual geometry stores (WinPref + own serwin.json) |
| BkpWin | FOLLOWS — `Backup.*` keys with fallbacks | FOLLOWS — per-company backup folder | FOLLOWS | FOLLOWS |
| RestoreCoWin | FOLLOWS | FOLLOWS | FOLLOWS | FOLLOWS |
| BinWin | FOLLOWS | FOLLOWS — per-company catalog query | FOLLOWS | FOLLOWS |
| ColChsWin | FOLLOWS | FOLLOWS (via RptBase caller) | FOLLOWS | FOLLOWS — WinPref-backed |
| CcCard / CshWin / ChqWin / OrdWin | **owner = ribbon branch** — in this worktree they are at their pre-rebuild state (fake saves, §6); the completed ribbon branch addresses all four columns there | | | |
| DatWin | **owner = fix/rtl-and-datwin branch** — not audited here beyond the October findings | | | |

**Central-mechanism dependencies** (not fixed here — the central application point in BaseWin/Localization/VisuId is owned by the fix/rtl-and-datwin branch): ~11 windows derive from `Window` directly (BinWin, BkpWin, ComWin, DatWin, Lang, LogWin, MWin, PrmWin, RestoreCoWin, SplWin, StpWiz) and rely on the central mechanism for direction; AccCard.xaml hardcodes `FlowDirection="LeftToRight"`. See §10.

## 4. Cards rebuilt on this branch (migration 050 + CardStore) ◆

New service `cod/app/src/Services/CardStore.cs` — transactional, parameterized, provider-aware (SQLite + PostgreSQL), company resolved per call from `AppState.CurrentCompanyCode`. Schema: migration `050_card_manufacturing_persistence.sql` (both providers) creates `customers`, `suppliers` (referenced by TaxSvc/RptQuery/DataExportSvc but previously created by **no** migration), `bank_accounts`, `currencies` + `currency_rates`, `sales_reps` + `sales_rep_targets`, `manufacturing_orders` + `manufacturing_order_lines`. All five windows: BaseWin, central styles, bottom flat command bar (New / Load / Save / Delete / Close), RBAC via `AppHost.Permissions`, real error surfacing (no fake success).

### CstCard.cs — `customers` / `suppliers` (was FAKE_SAVE: every button a MessageBox)
Type selector picks the table (Customer → `customers`, Supplier → `suppliers`, Both → both). Save = `CardStore.SaveParty`, Load = `CardStore.LoadParty`, Delete = `CardStore.SoftDeleteParty` (recycle-bin semantics). Metrics bar = real `party_ledger` totals via `CardStore.PartyTotals`. Statement button opens the real customer-balances report.

| Field (UI) | DB table.column | Status |
|---|---|---|
| Code | customers.customer_code / suppliers.supplier_code | VERIFIED ◆ |
| Name / Name (EN) | .customer_name_ar / .customer_name_en (supplier_name_ar/en) | VERIFIED ◆ |
| Type (Customer/Supplier/Both) | table choice (+ both tables for "B") | VERIFIED ◆ |
| Group | .party_group | VERIFIED ◆ |
| Tax number | .tax_number | VERIFIED ◆ |
| Commercial registration | .commercial_reg | VERIFIED ◆ |
| Credit limit | .credit_limit | VERIFIED ◆ |
| Credit days | .credit_days | VERIFIED ◆ |
| Price tier | .price_tier (stable codes Retail/Wholesale/Distributor; localized combo is index-aligned) | VERIFIED ◆ |
| GL account code | .gl_account_code | VERIFIED ◆ |
| Phone / Mobile / Email | .phone / .mobile / .email | VERIFIED ◆ |
| Address | .address | VERIFIED ◆ |
| Metrics: balance / debit / credit (display) | SUM(party_ledger.debit/credit) | VERIFIED (computed) |

Permissions: `CRM.CUSTOMER.CREATE/UPDATE/DELETE` (PermCat.Customers). Pre-existing schema note: before migration 050 these tables did not exist in a fresh database at all — every consumer (TaxSvc.ResolveParty, RptQuery balances/aging, DataExportSvc, RecycleBinSvc) failed against them.

### BnkCard.cs — `bank_accounts` (was FAKE_SAVE)
Save/Load/Delete = `CardStore.SaveBank/LoadBank/SoftDeleteBank`; balance metric = linked GL account book balance (`accounts.balance`, fallback debit−credit) via `CardStore.BankBookBalance`. Permission: `BANK.BANK.MANAGE`.

| Field (UI) | DB table.column | Status |
|---|---|---|
| Code / Name | bank_accounts.bank_code / .bank_name | VERIFIED ◆ |
| Branch | .branch | VERIFIED ◆ |
| Account number / IBAN | .account_no / .iban | VERIFIED ◆ |
| Currency | .currency_code (default from UsrPref) | VERIFIED ◆ |
| Linked GL account | .gl_account_code | VERIFIED ◆ |
| Balance (display) | accounts.balance of linked GL | VERIFIED (computed) |

### CurCard.cs — `currencies` + `currency_rates` (was FAKE_SAVE; rate grid was in-memory only)
Save = `CardStore.SaveCurrency` (header upsert + full rate-history replace in ONE transaction; grid edits committed via `CommitEdit(Cell)` + `CommitEdit(Row)` before capture). The seeded fake sample rate row was removed. Permission: `ACC.CURRENCY.MANAGE`.

| Field (UI) | DB table.column | Status |
|---|---|---|
| Code / Name / Name (EN) | currencies.currency_code / .currency_name_ar / .currency_name_en | VERIFIED ◆ |
| Symbol / Fraction name | .symbol / .fraction_name | VERIFIED ◆ |
| Exchange rate / Min / Max rate | .exchange_rate / .min_rate / .max_rate | VERIFIED ◆ |
| Rate grid: Date | currency_rates.rate_date | VERIFIED ◆ |
| Rate grid: Rate / Buy / Sell | currency_rates.rate / .buy_rate / .sell_rate | VERIFIED ◆ |
| Rate grid: Notes | currency_rates.notes | VERIFIED ◆ |

### RepCard.cs — `sales_reps` + `sales_rep_targets` (was FAKE_SAVE; summary bar hardcoded 0.00)
Save = `CardStore.SaveRep` (header upsert + per-period target upserts via ON CONFLICT in one transaction; grid edits committed before capture). Summary totals are now computed from the actual grid rows. Permissions: `REP.REPRESENTATIVE.CREATE/UPDATE/DELETE`.

| Field (UI) | DB table.column | Status |
|---|---|---|
| Code / Name | sales_reps.rep_code / .rep_name | VERIFIED ◆ |
| Phone | .phone | VERIFIED ◆ |
| Commission % | .commission_percent | VERIFIED ◆ |
| Monthly target | .monthly_target | VERIFIED ◆ |
| Targets grid: Period | sales_rep_targets.period (part of UNIQUE(company, rep, period)) | VERIFIED ◆ |
| Targets grid: Target | sales_rep_targets.target_amount | VERIFIED ◆ |
| Targets grid: Actual sales / Commission earned / Status | .actual_sales / .commission_earned / .status (round-trip through the grid rows) | VERIFIED ◆ |

### MfgWin.cs — `manufacturing_orders` + `manufacturing_order_lines` (was FAKE_SAVE; Post only flipped buttons)
Save = `CardStore.SaveOrder` (header + lines replace, one transaction; refuses to edit Posted orders). Post = `CardStore.PostOrder` — one transaction: `material_stock_movements` MFG-OUT per raw-material line + MFG-IN for the finished good at computed unit cost, `material_stock_balances` updated with the StkPost weighted-average rule, status → Posted, then the UI locks read-only. Print = real A4 paginator (JrnWin pattern; was an unavailable stub). Permissions: `MFG.ORDER.CREATE/UPDATE`, `MFG.PRODUCTION.POST`.

| Field (UI) | DB table.column | Status |
|---|---|---|
| Order no. / Order date | manufacturing_orders.order_no / .order_date | VERIFIED ◆ |
| Finished material code → id + name | .finished_material_id / .finished_material_code / .finished_material_name (id resolved via materials.code) | VERIFIED ◆ |
| Quantity / Unit | .quantity / .unit_code | VERIFIED ◆ |
| Warehouse → id + code | .warehouse_id / .warehouse_code (resolved from warehouses code/name) | VERIFIED ◆ |
| Overhead cost | .overhead_cost | VERIFIED ◆ |
| Total cost / Unit cost | .total_cost / .unit_cost (computed at save: Σ lines + overheads, ÷ quantity) | VERIFIED ◆ |
| Status | .status (Draft/Posted/Cancelled, CHECK) | VERIFIED ◆ |
| Lines grid: Material code → id + name | manufacturing_order_lines.material_id / .material_code / .material_name | VERIFIED ◆ |
| Lines grid: Unit / Qty / Unit cost | .unit_code / .quantity / .unit_cost | VERIFIED ◆ |


## 5. Cards and dictionaries (field-level, audited)

## 1. AccCard.xaml.cs — `accounts`

Window notes: Derives from `BaseWin` — **Yes** (`public partial class AccCard : BaseWin`). Bottom action bar — **Yes** (`DockPanel Grid.Row=3`: New/Save/Edit/Disable/Delete/Statement/Ledger, Close left). Lang keys — **No**: `ApplyLocalizedText()` is an empty stub ("future Loc keys"); all captions/MessageBoxes hardcoded English, several XAML labels are literal `"Text"`/`"Content"` placeholders.

Save path for all rows: `Save_Click` → `AccRepo.Save(_current, value, renumber)` in `Services/Accounting.cs` (`InsertSql()`/`UpdateSql()`). Load path: `LoadRecord()` ← `AccRepo.LoadAll()` / `AccRepo.FindById(id)` in `Services/Accounting.cs`.

| Window | Field/Grid column (UI) | Bound property/control | DB table.column | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| AccCard | Serial (display) | `TxtSerial` (read-only TextBox) | `accounts.account_id` (PK) | n/a — generated, set from returned id in `Save_Click`, AccCard.xaml.cs | `LoadRecord()` ← `AccRec.Id`, Accounting.cs | VERIFIED |
| AccCard | Code | `TxtCode.Text` → `AccRec.Code` | `accounts.account_code` | `Save_Click` → `AccRepo.Save`/`Bind`, Accounting.cs | `LoadRecord()` ← `AccRepo.LoadAll`, Accounting.cs | VERIFIED |
| AccCard | Name (Arabic) | `TxtNameAr.Text` → `AccRec.Name` | `accounts.name_ar` | as above | as above | VERIFIED |
| AccCard | English Name | `TxtNameEn.Text` → `AccRec.NameEn` | `accounts.name_en` | as above | as above | VERIFIED |
| AccCard | Parent Account | `CmbParent` → `SelectedParentCode()` → `AccRec.ParentCode` | `accounts.parent_code` | as above | `LoadRecord()` → `SelectParent(_current.ParentCode)` | VERIFIED |
| AccCard | Posting Mode | `CmbPostingMode.SelectedItem` → `AccRec.PostingMode` | `accounts.posting_mode` | as above | as above | VERIFIED |
| AccCard | Status | `CmbStatus.SelectedItem` → `AccRec.Status` | `accounts.status` | as above (also used by `Disable_Click` → `Save_Click`) | as above | VERIFIED |
| AccCard | Risk | `CmbRisk.SelectedItem` → `AccRec.Risk` | `accounts.risk` | as above | as above | VERIFIED |
| AccCard | Cost Center mode | `CmbCostCenter.SelectedItem` → `AccRec.CostCenter` | `accounts.cost_center` | as above | as above | VERIFIED |
| AccCard | Reconcile | `ChkReconcile.IsChecked` → `AccRec.Reconcile` | `accounts.allow_reconcile` | as above | as above | VERIFIED |
| AccCard | Taxable | `ChkTaxable.IsChecked` → `AccRec.Taxable` | `accounts.taxable` | as above | as above | VERIFIED |
| AccCard | Foreign Currency | `ChkForeign.IsChecked` → `AccRec.ForeignCurrency` | `accounts.foreign_currency` | as above | as above | VERIFIED |
| AccCard | Cost Required | `ChkCostRequired.IsChecked` → `AccRec.CostRequired` | `accounts.cost_required` | as above | as above | VERIFIED |
| AccCard | Budget | `ChkBudget.IsChecked` → `AccRec.Budget` | `accounts.budget` | as above | as above | VERIFIED |
| AccCard | Default | `ChkDefault.IsChecked` → `AccRec.IsDefault` | `accounts.is_default` | as above | as above | VERIFIED |
| AccCard | Tax Rate | `TxtTaxRate.Text` → `AccRec.TaxRate` | `accounts.tax_rate` | as above | as above | VERIFIED |
| AccCard | Description | `TxtDescription.Text` → `AccRec.Description` | `accounts.description` | as above | as above | VERIFIED |
| AccCard | Balance (display TextBlock) | `TxtBalance.Text` → `ParseAmount()` → `AccRec.Balance` | `accounts.balance` | as above (non-editable, but value is parsed and re-written on every save) | `LoadRecord()` sets `TxtBalance` from `AccRec.Balance` | VERIFIED |
| AccCard | Debit (display TextBlock) | `TxtDebit.Text` → `AccRec.Debit` | `accounts.debit` | as above | as above | VERIFIED |
| AccCard | Credit (display TextBlock) | `TxtCredit.Text` → `AccRec.Credit` | `accounts.credit` | as above | as above | VERIFIED |

**Gap summary — AccCard:** No field-level persistence gaps; all 20 fields round-trip. Non-field gaps: `Statement_Click`/`Ledger_Click` were FAKE handlers — **FIXED** on this branch (both open the real account statement report via `RptNav.OpenAccountStatement`). Still open: no Lang keys at all (see window notes) — AccCard remains a localization-rebuild candidate.

---

## 2. MatCard.xaml.cs — `materials` (+ child tables)

Window notes: Derives from `BaseWin` — **Yes**. Bottom action bar — **Yes** (footer `Border Grid.Row=1`: New/Save/Cancel/Edit/Delete/Excel Export/Import/Close + company/user status). Lang keys — **Yes**, extensively: `L(key,fallback)=Loc.Get(...)` in code, `loc:LocProps.Key` in XAML, `UiLoczr.ApplyToWindow`; exceptions: hardcoded Excel dialog titles and the `MaterialExcelExchange` stub messages.

Save path for all rows unless noted: `Save_Click` → `TryCommitGridEdits()` → `Collect()` (MatCard.xaml.cs) → `MatRepo.Save(record, _isNew)` in `Services/Inventory.cs` (INSERT/UPDATE `materials`; `SaveChildren()` for child tables). Load path: `LoadCurrent()` → `MatRepo.LoadById(id, true)` (fallback `MatRepo.LoadAll()`) in `Services/Inventory.cs` → `BindToUi()` (MatCard.xaml.cs). `Save_Click` commits both grids (`CommitEdit(Cell)` + `CommitEdit(Row)` for `DgUnits` and `DgAttributes`) before `Collect()` — no GRID_EDIT_LOST for the grids below.

| Window | Field/Grid column (UI) | Bound property/control | DB table.column | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| MatCard | Code | `TxtCode.Text` → `MatRec.Code` | `materials.code` | `Collect()` → `MatRepo.Save`, Inventory.cs (UPDATE does not change `code`; insert only) | `BindToUi()` ← `LoadById`, Inventory.cs | VERIFIED |
| MatCard | Item # (display) | `TxtItemNo` (read-only) ← `MatRec.Id` | `materials.material_id` (PK) | n/a — identity from `_currentId` in `Collect()` | `BindToUi()` | VERIFIED |
| MatCard | Name | `TxtName.Text` → `MatRec.Name` | `materials.name_ar` | as above | as above | VERIFIED |
| MatCard | Latin Name | `TxtNameEn.Text` → `MatRec.NameEn` | `materials.name_en` | as above | as above | VERIFIED |
| MatCard | Group | `CmbGroup` → `GetComboKey()` → `MatRec.GroupCode` | `materials.group_code` | as above; combo populated by `InitGroups()` ← `MatRepo.LoadGroups()` | `BindToUi()` → `SetCombo(CmbGroup, m.GroupCode)` | VERIFIED |
| MatCard | Type | `CmbItemType` → `MatRec.ItemType` | `materials.item_type` | as above | as above | VERIFIED |
| MatCard | Status / Inactive | `CmbStatus` ⇄ `ChkInactive` (synced in `Status_SelectionChanged`/`Inactive_Changed`) → `MatRec.Status` | `materials.status` | as above | as above | VERIFIED |
| MatCard | Category | `CmbCategory` (`MatCategoryOption.Key` = category Id) → `MatRec.Category` | `materials.category` (stores `product_categories.category_id`) | as above; combo from `InitCategories()` ← `MatRepo.LoadProductCategories()`, Inventory.cs | `BindToUi()` → `SetCategory(m.Category)` (matches Id/Code/Name/NameEn) | VERIFIED |
| MatCard | Brand | `TxtBrand.Text` → `MatRec.Brand` | `materials.brand` | as above | as above | VERIFIED |
| MatCard | Model | `TxtModel.Text` → `MatRec.Model` | `materials.model` | as above | as above | VERIFIED |
| MatCard | Color | `CmbColor` → `MatRec.Color` | `materials.color` | as above | as above | VERIFIED |
| MatCard | Size / Spec | `TxtSizeSpec.Text` → `MatRec.SizeSpec` | `materials.size_spec` | as above | as above | VERIFIED |
| MatCard | Manufacturer | `TxtManufacturer.Text` → `MatRec.Manufacturer` | `materials.manufacturer` | as above | as above | VERIFIED |
| MatCard | Origin | `TxtOriginCountry.Text` → `MatRec.OriginCountry` | `materials.origin_country` | as above | as above | VERIFIED |
| MatCard | Source | `TxtSource.Text` → `MatRec.Source` | `materials.source` | as above | as above | VERIFIED |
| MatCard | Alternate Code | `TxtAltCode.Text` → `MatRec.AltCode` | `materials.alt_code` | as above | as above | VERIFIED |
| MatCard | GTIN | `TxtGtin.Text` → `MatRec.Gtin` | `materials.gtin` | as above | as above | VERIFIED |
| MatCard | Supplier Code | `TxtSupplierCode.Text` → `MatRec.SupplierCode` | `materials.supplier_code` | as above | as above | VERIFIED |
| MatCard | ABC Class | `CmbAbcClass` → `MatRec.AbcClass` | `materials.abc_class` | as above | as above | VERIFIED |
| MatCard | Notes | `TxtNotes.Text` → `MatRec.Notes` | `materials.notes` | as above | as above | VERIFIED |
| MatCard | Stocked / Purchased / Sold / Produced / Service | `ChkIsStocked/IsPurchased/IsSold/IsProduced/IsService` → `MatRec.IsStocked/IsPurchased/IsSold/IsProduced/IsService` | `materials.is_stocked / is_purchased / is_sold / is_produced / is_service` | as above | as above | VERIFIED |
| MatCard | Min Qty / Max Qty | `TxtMinQty/TxtMaxQty.Text` → `MatRec.MinQty/MaxQty` | `materials.min_qty / max_qty` | as above | as above | VERIFIED |
| MatCard | Price Currency | `CmbPriceCurrency` → `MatRec.PriceCurrency` | `materials.price_currency` | as above | as above | VERIFIED |
| MatCard | Cost | `TxtCost.Text` → `MatRec.Cost` | `materials.cost` | as above — **but overwritten** in `Collect()` by Unit-1 `PricePurchase` when that cell is non-null | as above | VERIFIED (with override note) |
| MatCard | Cost Method | `CmbCostMethod` → `MatRec.CostMethod` | `materials.cost_method` | as above | as above | VERIFIED |
| MatCard | Tax % | `TxtTaxRate.Text` → `MatRec.TaxRate` | `materials.tax_rate` | as above | as above | VERIFIED |
| MatCard | Taxable / Price Includes Tax | `ChkIsTaxable/ChkPriceIncludesTax` → `MatRec.IsTaxable/PriceIncludesTax` | `materials.is_taxable / price_includes_tax` | as above | as above | VERIFIED |
| MatCard | Has Serial / Has Expiry / Has Batch | `ChkHasSerial/HasExpiry/HasBatch` → `MatRec.HasSerial/HasExpiry/HasBatch` | `materials.has_serial / has_expiry / has_batch` | as above | as above | VERIFIED |
| MatCard | Shelf Life Days | `TxtShelfLifeDays.Text` → `MatRec.ShelfLifeDays` | `materials.shelf_life_days` | as above | as above | VERIFIED |
| MatCard | Units grid: # (LineNo) | `MatUnitRow.LineNo` (read-only col, renumbered) | `materials.units_json` only (element of serialized list) | `Collect()` → `JsonSerializer.Serialize(active)` → `MatRec.UnitsJson` → `MatRepo.Save` | `BindToUi()` deserializes `UnitsJson` | VERIFIED |
| MatCard | Units grid: Unit | `MatUnitRow.UnitName` (`LostFocus`) | rows 1–5: `materials.unit1…unit5` **and** `units_json`; rows 6+: `units_json` only | `Collect()` maps first 5 to `Unit1…Unit5`, all to `UnitsJson` | `BindToUi()`: `UnitsJson` first; legacy fallback rebuilds from `unit1…unit5` only | VERIFIED |
| MatCard | Units grid: Factor | `MatUnitRow.Factor` | rows 1–5: `materials.unit1_factor…unit5_factor` + `units_json`; rows 6+: `units_json` only | as above | as above | VERIFIED |
| MatCard | Units grid: Barcode | `MatUnitRow.Barcode` | rows 1–5: `materials.unit1_barcode…unit5_barcode` + `units_json`; rows 6+: `units_json` only | as above | as above | VERIFIED |
| MatCard | Units grid: Sale | `MatUnitRow.PriceSale` | row 1: `materials.price_list` + `units_json`; rows 2+: `units_json` only (no per-unit column) | `Collect()`: `m.PriceList = Z(u1?.PriceSale)` | `BindToUi()`: from `UnitsJson`; legacy fallback maps `price_list` onto unit 1 only | VERIFIED |
| MatCard | Units grid: Purchase | `MatUnitRow.PricePurchase` | **No dedicated column** (`NO_COLUMN` for a `price_purchase` column); row 1 → `materials.cost`, all rows → `units_json` | `Collect()`: `if (u1?.PricePurchase is not null) m.Cost = u1.PricePurchase.Value` | From `UnitsJson` only; legacy fallback does **not** restore it (cost shown separately in `TxtCost`) | VERIFIED (via `units_json`; no dedicated column) |
| MatCard | Units grid: Wholesale | `MatUnitRow.PriceWholesale` | row 1: `materials.price_wholesale` + `units_json`; rows 2+: `units_json` only | `Collect()`: `m.PriceWholesale = Z(u1?.PriceWholesale)` | as Sale row | VERIFIED |
| MatCard | Units grid: Retail | `MatUnitRow.PriceRetail` | row 1: `materials.price_retail` + `units_json`; rows 2+: `units_json` only | as above | as above | VERIFIED |
| MatCard | Units grid: Special | `MatUnitRow.PriceSpecial` | row 1: `materials.price_special` + `units_json`; rows 2+: `units_json` only | as above | as above | VERIFIED |
| MatCard | Units grid: Export | `MatUnitRow.PriceExport` | row 1: `materials.price_export` + `units_json`; rows 2+: `units_json` only | as above | as above | VERIFIED |
| MatCard | Units grid: Distributor | `MatUnitRow.PriceDistributor` | row 1: `materials.price_distributor` + `units_json`; rows 2+: `units_json` only | as above | as above | VERIFIED |
| MatCard | Qty On Hand | `TxtQtyOnHand.Text` (forced read-only in `SetEditMode`) | `materials.qty_on_hand` | **Not assigned in `Collect()`; `CloneSparse(_loaded)` does not copy `QtyOnHand`, so `MatRepo.Save` binds `$qty_on_hand = 0` on every save** (MatCard.xaml.cs `Collect`/`CloneSparse`; Inventory.cs `Bind`) | `BindToUi()`: `TxtQtyOnHand.Text = m.QtyOnHand` ← `LoadById` | **FIXED** on this branch: `CloneSparse` now carries `QtyOnHand`, so an edit-save preserves the stock-maintained value instead of zeroing it |
| MatCard | Reorder Qty / Safety Stock | `TxtReorderQty/TxtSafetyStock.Text` → `MatRec.ReorderQty/SafetyStock` | `materials.reorder_qty / safety_stock` | `Collect()` → `MatRepo.Save` | `BindToUi()` ← `LoadById` | VERIFIED |
| MatCard | Default Warehouse | `CmbDefaultWarehouse` → `MatRec.DefaultWarehouse` (warehouse **code** as string) | `materials.default_warehouse` | as above; combo from `InitWarehouses()` ← `MatRepo.LoadWarehouses()` | as above | VERIFIED |
| MatCard | Shelf / Bin | `TxtShelfBin.Text` → `MatRec.ShelfBin` | `materials.shelf_bin` | as above | as above | VERIFIED |
| MatCard | Lead Time Days | `TxtLeadTimeDays.Text` → `MatRec.LeadTimeDays` | `materials.lead_time_days` | as above | as above | VERIFIED |
| MatCard | Weight Net / Gross | `TxtWeightNet/TxtWeightGross.Text` → `MatRec.WeightNet/WeightGross` | `materials.weight_net / weight_gross` | as above | as above | VERIFIED |
| MatCard | Allow Negative / Track Location / Quality Check | `ChkAllowNegative/ChkTrackLocation/ChkQualityCheck` → `MatRec.AllowNegative/TrackLocation/QualityCheckRequired` | `materials.allow_negative / track_location / quality_check_required` | as above | as above | VERIFIED |
| MatCard | Acc. Inventory / Sales / Sales Return / COGS / Purchase / Purchase Return / Expense / WIP | `TxtAccInventory/TxtAccSales/TxtAccSalesReturn/TxtAccCogs/TxtAccPurchase/TxtAccPurchaseReturn/TxtAccExpense/TxtAccWip` → `MatRec.Acc*` | `materials.acc_inventory / acc_sales / acc_sales_return / acc_cogs / acc_purchase / acc_purchase_return / acc_expense / acc_wip` | as above | as above | VERIFIED |
| MatCard | Allow Sale / Allow Purchase / Allow Return / Sales Blocked | `ChkAllowSale/ChkAllowPurchase/ChkAllowReturn/ChkSalesBlocked` → `MatRec.AllowSale/AllowPurchase/AllowReturn/SalesBlocked` | `materials.allow_sale / allow_purchase / allow_return / sales_blocked` | as above | as above | VERIFIED |
| MatCard | Image Path | `TxtImagePath.Text` (via `BrowseImage_Click`) → `MatRec.ImagePath` | `materials.image_path` | as above | `BindToUi()` → `LoadImage(m.ImagePath)` preview | VERIFIED |
| MatCard | Attributes grid: Group | `MatAttrRow.Group` (`PropertyChanged`) | `material_attributes.attr_group` | `Collect()`: `m.Attributes = _attrs.Where(Key≠"")` → `MatRepo.Save` → `SaveChildren()` INSERT, Inventory.cs (UPDATE path deletes existing rows first) | `LoadById(includeChildren:true)` → `LoadChildren()` SELECT from `material_attributes` → `BindToUi()` fills `_attrs` | VERIFIED |
| MatCard | Attributes grid: Key | `MatAttrRow.Key` | `material_attributes.attr_key` | as above (rows with empty Key are dropped in `Collect()`) | as above | VERIFIED |
| MatCard | Attributes grid: Value | `MatAttrRow.Value` | `material_attributes.attr_value` | as above | as above | VERIFIED |
| MatCard | Attributes grid: (no UI column) SortOrder | `MatAttrRow.SortOrder` — no grid column binds it | `material_attributes.sort_order` | `SaveChildren()` writes it, but new grid rows always carry `0`; loaded rows keep theirs via the bound object | `LoadChildren()` reads it | **GAP — PARTIAL** (persisted column exists and round-trips for loaded rows, but is not editable in the UI) |
| MatCard | Quick-search box | `TxtGoto.Text` | — (navigation only) | n/a | n/a — `NavigateToQuery()` searches in-memory `_cache` | VERIFIED (not a persisted field, by design) |

**Gap summary — MatCard:** 1 real data-loss gap: **QtyOnHand** (above). 1 minor gap: attribute `SortOrder` not UI-editable. Non-field gaps: `MaterialExcelExchange.Export/Import` (bottom of MatCard.xaml.cs) is a **stub — FAKE**: both only show "Excel export/import is temporarily unavailable" (Import always returns `null`), so the footer's Excel buttons do nothing. Preservation note: fields with no UI (e.g. `StandardCost`, `HasLot`, `PriceOnline`) survive edit-saves only because `Collect()` starts from `CloneSparse(_loaded)`; `QtyOnHand` is the notable field `CloneSparse` omits. Legacy-load caveat: if `units_json` is empty/corrupt, `BindToUi` rebuilds units from `unit1…unit5` + unit-1 price columns only, so legacy per-unit prices for units 2–5 would not reappear (current saves always write `units_json`).

---

## 3. MatDirWin.xaml.cs — directory (no card form)

Window notes: Derives from `BaseWin` — **Yes**. Bottom action bar — **Yes** (XAML comment "Action bar (bottom, layout rule)", `Grid.Row=3`: New/Delete/Refresh/Print; mode + search are at top). Lang keys — **Yes**: `Loc.Get(...)` for every caption in `ApplyDirection()` + `loc:LocProps.Key` in XAML.

This window has no editable form fields; its only DB write is delete. Save path (delete only): `Remove_Click` → `MatRepo.SoftDelete(item.Id)` in `Services/Inventory.cs` (`UPDATE materials SET is_deleted=…`). Load path: `Reload()` → `MatRepo.LoadAll()` (Material mode) or reflection `MatGroupRepo.LoadAll()` (Group mode), MatDirWin.xaml.cs.

| Window | Field/Grid column (UI) | Bound property/control | DB table.column | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| MatDirWin | List: Code (Material mode) | `MatDirListItem.Code` ← `MatRec.Code` (`{Binding Code}`) | `materials.code` | n/a in this window (read-only list; edited in MatCard) | `Reload()` ← `MatRepo.LoadAll()`, Inventory.cs | VERIFIED (load-only by design) |
| MatDirWin | List: Name (Material mode) | `MatDirListItem.DisplayName` ← `MatRec.Name` | `materials.name` | n/a in this window | as above | VERIFIED (load-only) |
| MatDirWin | List: Status (Material mode) | `MatDirListItem.StatusLabel` ← `MatRec.Status` | `materials.status` | n/a in this window | as above | VERIFIED (load-only) |
| MatDirWin | List: Code (Group mode) | `MatDirListItem.Code` ← reflection `Code` property | `material_groups.group_code` | n/a in this window (groups deleted only via Groups window — `Remove_Click` shows hint + opens it) | `LoadGroups()` → reflection `MatGroupRepo.LoadAll()`, MatDirWin.xaml.cs | VERIFIED (load-only) |
| MatDirWin | List: Name (Group mode) | `MatDirListItem.DisplayName` ← reflection `Name/NameEn` | `material_groups.group_name / group_name_en` | n/a in this window | as above | VERIFIED (load-only) |
| MatDirWin | List: Status (Group mode) | `MatDirListItem.StatusLabel` ← reflection `Status` | `material_groups.status` | n/a in this window | as above | VERIFIED (load-only) |
| MatDirWin | Search box | `TxtSearch.Text` → `ApplyFilter()` on in-memory `_all` | — (filter only) | n/a | n/a | VERIFIED (not a persisted field, by design) |
| MatDirWin | Mode toggle Material/Group | `RbItem/RbClass.IsChecked` → `_modeItem` | — (view state only) | n/a | n/a | VERIFIED (not a persisted field, by design) |
| MatDirWin | **Open Card / double-click on a selected row** | `LvItems.SelectedItem` (`MatDirListItem.Id/Code`) | intended: selected `materials.material_id` / `material_groups.group_id` | — | `OpenSelected()` creates `new MatCard()` / `OpenGroupsWindow()` with **no record argument**; `MatCard()` parameterless ctor loads `_cache[0]` (first material), MatGroupWin loads its first group | **FIXED** on this branch: `MatCard.OpenMaterial(id)` / `MatGroupWin.SelectGroup(id)` now honor the selection |
| MatDirWin | Print button | `BtnPrint` → `Print_Click` | — | `Print_Click`, MatDirWin.xaml.cs — only `MessageBox.Show("Print list: " + _view.Count)` | — | **FIXED** on this branch: real FlowDocument list print (same pattern as CoaWin) |

**Gap summary — MatDirWin:** List columns themselves load correctly for both modes and Delete works for materials (`MatRepo.SoftDelete`). Two gaps: (1) **Open-selected is broken** — selection is discarded, opened card always shows the first record; (2) **Print is fake**. Group-mode delete is intentionally delegated to the Groups window (hint message, not a silent fake).

---

## 4. CatWin.xaml.cs — `material_groups` (category/group card)

Window notes: Derives from `BaseWin` — **Yes**. Bottom action bar — **Yes** (footer `Grid.Row=2`: New/Edit/Delete/Save/Refresh/Close + status bar). Lang keys — **Yes**: `L(key,fallback)=Loc.Get("CatWin."+key, …)` in code + `loc:LocProps.Key` throughout XAML.

Save path for all rows: `Save_Click` → `SaveCurrent(false)` (CatWin.xaml.cs) → `MatRepo.SaveGroup(record, isNew)` in `Services/Inventory.cs` (INSERT/UPDATE `material_groups`). Load path: `LoadCards()` ← `MatRepo.LoadGroups()` (Inventory.cs) → `ShowCard(index)` (CatWin.xaml.cs); tree built from `ParentCode` in `BuildTree()`.

| Window | Field/Grid column (UI) | Bound property/control | DB table.column | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| CatWin | Card No. (display badge) | `TxtCardNo` (TextBlock) ← `MatGroupRec.CardNo` | `material_groups.card_no` | System-generated in `SaveGroup` via `NextGroupCardNo()` (sequence table `material_group_sequences`), Inventory.cs; preview via `MatRepo.GetNextGroupCardNoPreview()` | `ShowCard()` ← `LoadGroups()` | VERIFIED (system-generated, not user-editable) |
| CatWin | Item Code | `TxtCode.Text` → `MatGroupRec.Code` | `material_groups.group_code` | `SaveCurrent()` → `MatRepo.SaveGroup`, Inventory.cs | `ShowCard()` ← `LoadGroups()` | VERIFIED |
| CatWin | Item Name | `TxtName.Text` → `MatGroupRec.Name` | `material_groups.group_name` | as above | as above | VERIFIED |
| CatWin | Latin Name | `TxtNameEn.Text` → `MatGroupRec.NameEn` | `material_groups.group_name_en` | as above | as above | VERIFIED |
| CatWin | Warehouse | `CmbWarehouse.SelectedItem` (`WarehouseRec`) → `GetSelectedWarehouseValue()` → `MatGroupRec.Warehouse` | `material_groups.warehouse` (free-text) | as above — **stores the warehouse Name (`w.Name`, fallback `NameEn`, fallback Code string), not its code** | `ShowCard()` → `SelectWarehouse(_current.Warehouse)` matches by Code/CardNo/Name/NameEn against `MatRepo.LoadWarehouses()` | VERIFIED (round-trips via name matching; fragile if two warehouses share a name) |
| CatWin | General Specifications | `TxtSpecs.Text` → `MatGroupRec.GeneralSpecs` | `material_groups.general_specs` | as above | as above | VERIFIED |
| CatWin | Item Image | `_pendingImagePath` (via `PickImage_Click`/`ClearImage_Click`) → `MatGroupRec.ImagePath` | `material_groups.image_path` (full local path stored as-is) | as above | `ShowCard()` → `LoadImagePreview(_current.ImagePath)` | VERIFIED |
| CatWin | (no UI field) Parent | — no parent control exists in CatWin | `material_groups.parent_code` | New records: `SaveCurrent()` hardcodes `ParentCode = null`; updates: preserved implicitly because `SaveCurrent` mutates the loaded `_current` object | `LoadGroups()` reads it; used only to build the tree in `BuildTree()` | **FIXED** on this branch: Parent Category selector added (self + descendants excluded, cycle-proof), wired into create + update save paths |
| CatWin | Search box | `TxtSearch.Text` → `RunSearch()` over in-memory `_items` | — (search only) | n/a | n/a | VERIFIED (not a persisted field, by design) |

**Gap summary — CatWin:** All 6 actual form fields (Code, Name, NameEn, Warehouse, Specs, Image) plus system CardNo are VERIFIED. Only structural gap: **ParentCode has no UI** (PARTIAL) — hierarchy is display-only in this window. Also note `MatGroupRec` fields `Notes`, `Status`, `IsSalesClass` are written by `SaveGroup` but have no CatWin UI: on insert `Status` is hardcoded `"Active"` and `Notes`/`IsSalesClass` keep record defaults; on update they are preserved from the loaded record.

---

## 5. WhsWin.xaml.cs — `warehouses`

Window notes: Derives from `BaseWin` — **Yes** (XAML root `views:BaseWin`, `PrefKey => "WhsWin"`). Bottom action bar — **Yes** (`Border Grid.Row=2`: New/Edit/Save/Delete/Close + status text). Lang keys — **Yes**: `T("WhsWin.*")` for all labels/buttons in `ApplyLocalizedText()`; exceptions: hardcoded `"[off]"` suffix in `CreateNode()` and hardcoded English error strings inside the separate static `OpenStockCount` dialog (not part of the warehouse form).

Save path for all rows: `Save_Click` → `SaveNewWarehouse()`/`SaveExistingWarehouse()` → `BuildRecFromForm()` (WhsWin.xaml.cs) → `MatRepo.SaveWarehouse(rec, isNew)` in `Services/Inventory.cs` (`InsertWarehouse()`/`UpdateWarehouse()`). Load path: `ReloadAll()` → `LoadItems()` ← `MatRepo.LoadWarehouses()` (Inventory.cs) → `BindRecord()` (WhsWin.xaml.cs). **Caveat applying to the whole load path:** `LoadWarehouses()` → `ReadActiveWarehouses()` filters `WHERE is_deleted = 0 AND is_active = 1` (Inventory.cs).

| Window | Field/Grid column (UI) | Bound property/control | DB table.column | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| WhsWin | Auto Code (display) | `TxtAutoCode` (read-only) ← `WarehouseRec.Code` | `warehouses.warehouse_code` | System-generated in `InsertWarehouse()` (`MAX(warehouse_code)+1`); on update `SaveExistingWarehouse()` sets `rec.Code = _current.Code` and `UpdateWarehouse()` does not modify the column | `BindRecord()` ← `LoadWarehouses()` | VERIFIED (system-generated) |
| WhsWin | Card No. | `TxtCardNo.Text` → `WarehouseRec.CardNo` (validated unique in `ValidateInput()` + `EnsureCardNoIsUnique()`) | `warehouses.card_no` | `BuildRecFromForm()` → `MatRepo.SaveWarehouse`, Inventory.cs | as above | VERIFIED |
| WhsWin | Name | `TxtName.Text` → `WarehouseRec.Name` | `warehouses.warehouse_name_ar` | as above | as above | VERIFIED |
| WhsWin | Name (Latin) | `TxtNameEn.Text` → `WarehouseRec.NameEn` | `warehouses.warehouse_name_en` | as above | as above | VERIFIED |
| WhsWin | Parent Warehouse | `CmbParent.SelectedItem` → `GetParentCode()` → `WarehouseRec.ParentCode` | `warehouses.parent_code` | as above; combo from `FillParentCombo()` (active warehouses + "(No Parent)" sentinel `Code=0` → `null`) | `BindRecord()` selects by matching `Code` | VERIFIED |
| WhsWin | Is Default | `ChkDefault.IsChecked` → `WarehouseRec.IsDefault` | `warehouses.is_default` | as above (setting it clears the flag on all other rows via `ClearDefaultFlag()`) | as above | VERIFIED |
| WhsWin | Is Active — **on edit (update)** | `ChkActive.IsChecked` → `WarehouseRec.IsActive` | `warehouses.is_active` | `UpdateWarehouse()` writes `is_active = $act`, Inventory.cs | See next row — record then excluded by the load filter | **FIXED** on this branch: WhsWin now loads via `MatRepo.LoadAllWarehouses()` (no is_active filter, no auto-create side effect), so deactivated warehouses stay visible and can be reactivated |
| WhsWin | Is Active — **on new (insert)** | `ChkActive.IsChecked` → `WarehouseRec.IsActive` | `warehouses.is_active` | `InsertWarehouse()` hardcodes the value: `VALUES (…, $def, 1, 0, …)` — the form value is ignored, new warehouses are always created active, Inventory.cs | as above | **FIXED** on this branch: `InsertWarehouse` binds `$act` from the form value instead of hardcoding 1 |
| WhsWin | Address (Advanced panel) | `TxtAddress.Text` → `WarehouseRec.Address` | `warehouses.address_line1` | `BuildRecFromForm()` → `MatRepo.SaveWarehouse` | `BindRecord()` ← `LoadWarehouses()` | VERIFIED |
| WhsWin | City (Advanced) | `TxtCity.Text` → `WarehouseRec.City` | `warehouses.city` | as above | as above | VERIFIED |
| WhsWin | Region (Advanced) | `TxtRegion.Text` → `WarehouseRec.Region` | `warehouses.region` | as above | as above | VERIFIED |
| WhsWin | Country (Advanced) | `TxtCountry.Text` → `WarehouseRec.Country` | `warehouses.country` | as above | as above | VERIFIED |
| WhsWin | Phone (Advanced) | `TxtPhone.Text` → `WarehouseRec.Phone` | `warehouses.phone` | as above | as above | VERIFIED |
| WhsWin | Location: Aisle (Advanced) | `TxtAisle.Text` → `WarehouseRec.Aisle` | `warehouses.aisle` | as above | as above | VERIFIED |
| WhsWin | Location: Rack (Advanced) | `TxtRack.Text` → `WarehouseRec.Rack` | `warehouses.rack` | as above | as above | VERIFIED |
| WhsWin | Location: Shelf (Advanced) | `TxtShelf.Text` → `WarehouseRec.Shelf` | `warehouses.shelf` | as above | as above | VERIFIED |
| WhsWin | Notes (Advanced) | `TxtNotes.Text` → `WarehouseRec.Notes` | `warehouses.notes` | as above | as above | VERIFIED |

**Gap summary — WhsWin:** All fields except Is Active are fully VERIFIED, including the whole Advanced panel. **Is Active is broken in both directions**: ignored on insert (hardcoded `1` in `InsertWarehouse`) and, once set to `0` via update, the record is filtered out of `MatRepo.LoadWarehouses()` forever within this window (same filter also means MatCard's `CmbDefaultWarehouse` and CatWin's `CmbWarehouse` never offer inactive warehouses — defensible there, but WhsWin reuses that consumer-oriented loader for its own master list).

---

## 6. MatGroupWin.xaml.cs — `material_group_items` (group membership)

Window notes: Derives from `BaseWin` — **Yes** (XAML root `views:BaseWin`). Bottom action bar — **Yes** (`Grid.Row=3`: Manage Groups/Refresh/Save Membership/Close + selection count/status). Lang keys — **Yes**: `L(key,fallback)=Loc.Get("MatGroupWin."+key, …)` in `ApplyLocalizedText()` for title, labels, buttons and all grid headers.

Save path: `BtnSave_Click` → `GridMaterials.CommitEdit(Cell)` + `CommitEdit(Row)` → `MatGroupRepo.SaveMembers(group.Id, _rows.Where(IsAssigned).Select(Id))` in `Services/MatGroupRepo.cs` (transaction: verify group in `material_groups`, `DELETE FROM material_group_items WHERE group_id=…`, INSERT one row per assigned material). Load path: `LoadGroupMaterials(groupId)` ← `MatGroupRepo.LoadMemberIds(groupId)` (SELECT `material_id` FROM `material_group_items`) + `MatRepo.LoadAll()` (Inventory.cs); group combo ← `ReloadGroups()` ← `MatRepo.LoadGroups()`.

| Window | Field/Grid column (UI) | Bound property/control | DB table.column | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| MatGroupWin | Material Group selector | `CmbGroup.SelectedItem` (`GroupOption.Id/Code/DisplayName`) ← `MatGroupRec.Id/Code/Name/NameEn` | selector key: `material_groups.group_id` (display: `group_code`, `group_name`/`group_name_en`) | n/a — selection only; it is the `groupId` argument to `SaveMembers` | `ReloadGroups()` ← `MatRepo.LoadGroups()`, Inventory.cs | VERIFIED (load-only selector, by design) |
| MatGroupWin | Grid: Assigned (checkbox, editable) | `GroupMaterialRow.IsAssigned` (`TwoWay, PropertyChanged`; dirty-tracked via `PropertyChanged`) | `material_group_items.group_id + material_id` (row existence = assigned) | `BtnSave_Click` → `MatGroupRepo.SaveMembers`, MatGroupRepo.cs | `LoadGroupMaterials()` ← `MatGroupRepo.LoadMemberIds`, MatGroupRepo.cs | VERIFIED |
| MatGroupWin | Grid: Code (read-only) | `GroupMaterialRow.Code` ← `MatRec.Code` | `materials.code` | n/a in this window (read-only display) | `LoadGroupMaterials()` ← `MatRepo.LoadAll()`, Inventory.cs | VERIFIED (load-only) |
| MatGroupWin | Grid: Material (read-only) | `GroupMaterialRow.DisplayName` ← `MatRec.Name` (ar) / `NameEn` (other), language-resolved in `LoadGroupMaterials()` | `materials.name_ar / name_en` | n/a in this window | as above | VERIFIED (load-only) |
| MatGroupWin | Grid: English Name (read-only) | `GroupMaterialRow.NameEn` ← `MatRec.NameEn` | `materials.name_en` | n/a in this window | as above | VERIFIED (load-only) |
| MatGroupWin | Grid: Status (read-only) | `GroupMaterialRow.Status` ← `MatRec.Status` | `materials.status` | n/a in this window | as above | VERIFIED (load-only) |
| MatGroupWin | Search box | `TxtSearch.Text` → `FilterRow()` on the `ICollectionView` | — (filter only; also scopes Select Visible/Clear Visible) | n/a | n/a | VERIFIED (not a persisted field, by design) |

**Gap summary — MatGroupWin:** No persistence gaps found. The single editable field (Assigned) is committed from the grid before save, saved transactionally (delete-all + re-insert, with per-material existence check), and loaded back via `LoadMemberIds`. Unsaved-change protection (`_isDirty` + `ConfirmDiscard()` on group switch/refresh/close) is present. Note: membership is keyed to the group selected in `CmbGroup`; group cards themselves are managed via the embedded `CatWin` (Manage Groups button), audited separately above.

---

## 6. Documents, transactions, reports, and other-branch windows

### 6.1 Documents and transactions (field-level, audited)

#### JrnWin.cs — journal vouchers (`journal_entries` via JournalService, src projects)
The October audit listed JrnWin as P0 fake-save; that is **no longer true** — Save calls `JournalService.CreateDraftAsync` with grid `CommitEdit` on save, Post and Reverse are real (Reverse creates the compensating linked entry), Load works by document number. All header fields (DocNo, Date, RefNo, Currency, ExchangeRate) and every line-grid column (AccountCode, AccountName, Debit, Credit, CostCenter, Memo) round-trip — VERIFIED.

| Item | Status |
|---|---|
| Header + line grid round-trip | VERIFIED |
| Loaded Draft forced read-only; `JournalService` has **no UpdateDraft** — a saved draft cannot be edited, only posted/reversed | GAP — PARTIAL (service API missing; documented, not hacked around in the UI) |
| Hardcoded English message strings (6) | FIXED — `Vouchers.EntryNumberRequired/EntryLoaded/EntryAlreadySaved/EntryDraftSaved/EntryPosted/SaveFailedMsg/SaveFailedTitle` added to all 4 catalogs |
| Currency default `"SAR"` | FIXED — `UsrPref.CurrencyCode` |

#### InvoiceWin.cs — commercial documents (`sales_documents`/`purchase_documents` via InvRepo)
Every visible header text field was verified to round-trip in October-scope fields and re-verified here (DocNo, Date, PartyCode/Name, Currency, TaxRate, Notes, ReceiptNo, Reference, Branch, SalesRep, WarehouseName, CostCenter, PartyAccountName, GoodsAccountName, LinesDiscount, LinesExtra, PaidAmount, PaymentMethod). Party_id is set on PartyCode LostFocus via `TaxSvc.ResolveParty`.

| Item | Status |
|---|---|
| Header fields round-trip | VERIFIED |
| Line grid: MaterialId / WarehouseId — `LineVm.ToLine/FromLine` dropped both; lines persisted as free text and posting silently skipped stock for them | FIXED — LineVm carries both ids; `CaptureDoc` resolves typed material codes and warehouse names to ids (visible text stays the source of truth, loaded id is the fallback) |
| Header WarehouseId never resolved from the typed name | FIXED — same resolution in `CaptureDoc` |
| `exchange_rate` / `tax_code` columns + repo support exist but have no UI | GAP — PARTIAL (backend-ready, no UI field) |
| `InvDoc.PartyAccountId` / `GoodsAccountId` — dead model fields (never bound or persisted) | GAP — PARTIAL (model only) |
| Currency fallback `"SAR"` (3 sites) | FIXED — `UsrPref.CurrencyCode` |

#### PosWin.cs — POS cart
Cart lines flow correctly into checkout (MaterialId, Qty, UnitPrice, TaxRate, WarehouseId all carried). 

| Item | Status |
|---|---|
| Cart → checkout line fidelity | VERIFIED |
| Held/draft cart persistence | GAP — NEVER_SAVED (no hold/recall; a closed POS loses the cart — by current design, recorded as a gap) |
| Line discount UI (discount_pct always 0 from POS) | GAP — PARTIAL (column + checkout support exist, no POS UI) |

#### PosCheckoutWin.cs — POS posting
| Item | Status |
|---|---|
| **Payment method never assigned to `_doc.PaymentMethod`** — every POS sale posted as `Cash` in `sales_documents.payment_method`; the tender survived only as free text in Notes. K-Net/Visa had no enum value | FIXED — new `InvPaymentMethod.Card = 3`, `InvRepo.ParsePaymentMethod` maps Card/K-Net/Visa, checkout maps the tender label onto the enum |
| Currency hardcoded `"SAR"` | FIXED — `UsrPref.Load().CurrencyCode` |
| `paid_amount` never set (no tendered UI) | FIXED for the completed-sale semantic: PaidAmount = GrandTotal for cash/card, 0 for credit (a tendered/change UI remains a product gap, §9) |
| Warehouse never set on header | PARTIAL — lines carry WarehouseId; header stays null by InvRepo design |

#### BarWin.cs — create barcode (was: NO save path at all)
| Item | Status |
|---|---|
| Barcode / price for a picked material never written anywhere (print-only) | FIXED — "Save to Material" persists `materials.unit1_barcode` + `price_retail` via `MatRepo.LoadById(includeChildren) → Save` (the BcdGenWin pattern, so no other material field is lost); RBAC `INV.ITEM.UPDATE`; picker now loads the material's real barcode/price |
| Fake sample defaults (`ITEM-001`, `Sample Product`, `6281001234567`, `25.00`) | FIXED — fields start empty |
| Picker failures silently swallowed (`catch {}`) | FIXED — logged via AppHost.Logging |
| Copies / Include name / Include price / Type | Session-only print options — VERIFIED (not persisted fields, by design) |

#### BcdGenWin.cs — barcode generation
Generated barcodes persist correctly (`MatRepo.LoadById → Unit1Barcode → MatRepo.Save`) — VERIFIED. Prefix/Include/Status are session-only generation options. Generated-but-unsaved barcodes are silently discarded on close — GAP — PARTIAL (no dirty warning).

#### BcdDesWin.cs — barcode label designer
All 16 design fields round-trip — to the **file** `%AppData%/ShouTech/barcode-design.cfg`; there is no DB column. Recorded as a **justified settings exception** (per-user design asset, like WinPref): VERIFIED (file store), NO_COLUMN by design.

#### CashDrawerOptionsWin (CashDrawer.cs) — POS printer/drawer settings
Printer selection round-trips to `%AppData%/ShouTech/pos-settings.json` via CashDrawerService — VERIFIED (file store, settings exception). No bottom command bar; mostly hardcoded English strings — PARTIAL (standard/localization, §9).

#### Reports — 19 windows on RptBase (AccBalWin, AccStmtWin, BalSheetWin, CustAgingWin, CustBalWin, IncStmtWin, ItemProfitWin, JrnListWin, MatListWin, MatMoveWin, PriceListWin, PurListWin, SalListWin, ShortListWin, StkAdjRptWin, StkBalWin, SupAgingWin, SupBalWin, TrialBalanceWin)
All READ_ONLY_OK: grids are `IsReadOnly=true`, data comes from RptQuery/MatRepo queries traced per report. Column layouts (visibility/order/width) persist via WinPref (see ColChsWin in §6.2). Filter values do not persist across sessions — session state by design.

#### Ribbon-owned windows (owner = ribbon branch — audited, NOT fixed here)
In this worktree these are at their pre-rebuild state; the completed `feat/ribbon-windows-complete` branch rebuilds all four (merging is a separate decision).

| Window | State in this worktree |
|---|---|
| CcCard.cs (cost centers) | FAKE_SAVE — Save/Delete are message boxes; no cost_centers table in this branch's migrations |
| CshWin.cs (receipt/payment vouchers) | FAKE_SAVE — SaveVoucher/PostVoucher change UI state only |
| ChqWin.cs (cheques) | FAKE_SAVE — batch held in an ObservableCollection, never persisted |
| OrdWin.cs (sales/purchase orders) | FAKE_SAVE — Save is a message box; convert-to-invoice records no persisted link to the order |

#### DatWin (owner = fix/rtl-and-datwin branch)
Export/import defects are owned by that branch (directive 3) and are not re-audited here. Note from §4: migration 050 now creates the `customers`/`suppliers` tables that DataExportSvc already referenced.


### 6.2 Phase 3 windows, shell, admin and setup windows (field-level, audited)

## Phase 3 reference windows

### PayRecWin.cs
*Per-window: BaseWin-derived YES · bottom action bar YES (Dock.Bottom, Allocate/Refresh/Close) · Lang keys only (`T("PaymentReconciliation.*")`, no hardcoded UI text except invariant exception in `ParseDecimal`). Grids `IsReadOnly=true` (selection-only) in `ConfigureGrid`.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| PayRecWin | Party kind (filter) | `_partyKind` ComboBox → `PartyKind` | payment_allocations.party_kind (on write) | `AllocateAsync` → `ReconciliationService.AllocateAsync` → `ReconciliationRepository.AllocateAsync` (src/ShouTech.Infrastructure) | n/a (defaults "C") | VERIFIED |
| PayRecWin | Doc grid: DocNo / Date / Party / Total / Paid / Allocated / Open | `OpenDocumentRow.DocNo/DocDate/PartyName/Total/Paid/Allocated/OpenAmount` on `_docGrid` | sales_documents/purchase_documents (doc_no, doc_date, party_name, grand_total, paid_amount) + SUM(payment_allocations.amount) | n/a (read-only display) | `RefreshAsync` → `ReconciliationService.GetOpenDocumentsAsync` → `ReconciliationRepository` SELECT | VERIFIED |
| PayRecWin | Doc grid hidden data: Currency / PartyId / DocTable+DocId | `OpenDocumentRow.CurrencyCode/PartyId/DocTable/DocId` | currency_code, party_id (source docs) | DocTable/DocId used as allocation target in `AllocateAsync` | Same SELECT as above | PARTIAL — loaded, never displayed (no column); CurrencyCode unused anywhere in window |
| PayRecWin | Pay grid: Date / DocNo / Amount / Allocated / Available | `UnallocatedPaymentRow.EntryDate/DocNo/Amount/Allocated/Available` on `_payGrid` | party_ledger (entry_date, doc_no, credit/debit) + SUM(payment_allocations.amount) | n/a (read-only display) | `RefreshAsync` → `GetPaymentsAsync` → repository SELECT | VERIFIED |
| PayRecWin | Pay grid hidden data: DocKind / Note / PartyId | `UnallocatedPaymentRow.DocKind/Note/PartyId` | party_ledger.doc_kind/note/party_id | — | Same SELECT as above | PARTIAL — loaded, never displayed (no column) |
| PayRecWin | Allocation amount | `_amount` TextBox (auto-suggested by `SuggestAmount`) | payment_allocations.amount; side-effect UPDATE sales/purchase_documents.paid_amount += amount | `AllocateAsync` (PayRecWin.cs) → `ReconciliationService.AllocateAsync` → `ReconciliationRepository.AllocateAsync` INSERT + UPDATE in one transaction | n/a (input only) | VERIFIED |
| PayRecWin | Allocation note | — (no control) | payment_allocations.note (column + service param `note=null` exist) | Window calls `AllocateAsync(...)` with 7 args, never passes note (`PayRecWin.AllocateAsync`) | — | **FIXED** on this branch: note field added to the window and passed to `AllocateAsync` |
| PayRecWin | Allocated by / at | implicit `CurrentUserId` | payment_allocations.allocated_by / allocated_at (DB default) | Same INSERT as above | — | VERIFIED |
| PayRecWin | Window geometry | BaseWin | WinPref store | `WinPref.SaveGeometry` (BaseWin.OnGeometryClosing) | `WinPref.RestoreGeometry` (BaseWin) | VERIFIED |

**Gap summary:** 1 GAP (allocation note never collected/passed). 2 PARTIAL display omissions (CurrencyCode, DocKind/Note/PartyId loaded but no column). Everything the window actually edits is VERIFIED — it is a valid reference standard.

### ProjWin.cs
*Per-window: BaseWin YES · bottom bar YES (New/Save/Delete/AddTask/DeleteTask/Refresh/Close) · Lang keys only (`T("Projects.*")`). Grids read-only.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| ProjWin | Code | `_code` / `Project.Code` | projects.project_code | `SaveAsync` → `ProjectService.SaveAsync` → `ProjectRepository` INSERT/UPDATE (src) | `OnProjectSelectedAsync` ← `RefreshAsync`/`GetAllAsync` SELECT | VERIFIED |
| ProjWin | Name | `_name` / `Project.Name` | projects.project_name | same | same | VERIFIED |
| ProjWin | Status | `_status` / `Project.Status` | projects.status | same | same | VERIFIED |
| ProjWin | Customer Id | `_customerId` / `Project.CustomerId` | projects.customer_id | same | same | VERIFIED |
| ProjWin | Customer (name) | `_customerName` / `Project.CustomerName` | projects.customer_name | same | same | VERIFIED |
| ProjWin | Start / End date | `_start`/`_end` / `Project.StartDate/EndDate` | projects.start_date / end_date | same | same | VERIFIED |
| ProjWin | Budget | `_budget` / `Project.Budget` | projects.budget | same | same | VERIFIED |
| ProjWin | Notes | `_notes` / `Project.Notes` | projects.notes | same | same | VERIFIED |
| ProjWin | Project grid (Code/Name/Customer/Status/Start/End/Budget) | `_grid` ← `Project.*` | projects.* (as above) | n/a read-only | `RefreshAsync` → `GetAllAsync` | VERIFIED |
| ProjWin | Task: Name | `_taskName` / `ProjectTask.Name` | project_tasks.task_name | `SaveTaskAsync` → `ProjectService.SaveTaskAsync` → repo INSERT/UPDATE | `OnTaskSelected` ← `LoadTasksAsync`/`GetTasksAsync` | VERIFIED |
| ProjWin | Task: Status | `_taskStatus` / `ProjectTask.Status` | project_tasks.status | same | same | VERIFIED |
| ProjWin | Task: Due date | `_taskDue` / `ProjectTask.DueDate` | project_tasks.due_date | same | same | VERIFIED |
| ProjWin | Task: Assigned to | `_taskAssigned` / `ProjectTask.AssignedTo` | project_tasks.assigned_to | same | same | VERIFIED |
| ProjWin | Task grid (Name/Status/Due/Assigned) | `_taskGrid` ← `ProjectTask.*` | project_tasks.* | n/a read-only | `LoadTasksAsync` | VERIFIED |
| ProjWin | Task notes | — (no control; `ProjectTask.Notes` + project_tasks.notes + repo read/write all exist) | project_tasks.notes | `SaveTaskAsync` constructs `ProjectTask` without `Notes` (always null) | Repo SELECTs notes into entity; window never shows it | **FIXED** on this branch: task notes field added to the editor, saved and loaded |
| ProjWin | Window geometry | BaseWin | WinPref | `WinPref.SaveGeometry` | `WinPref.RestoreGeometry` | VERIFIED |

**Gap summary:** 1 GAP — task Notes has full backend support but no UI field and is never set by the window. All present fields VERIFIED.

### AssetWin.cs
*Per-window: BaseWin YES · bottom bar YES (New/Save/Delete/Refresh/Close) · Lang keys only (`T("Assets.*")`). Grids read-only.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| AssetWin | Code / Name / Category / Location / Notes | `_code/_name/_category/_location/_notes` / `FixedAsset.*` | fixed_assets.asset_code/asset_name/category/location/notes | `SaveAsync` → `AssetService.SaveAsync` → `AssetRepository` INSERT/UPDATE (src) | `OnAssetSelected` ← `RefreshAsync`/`GetAllAsync` SELECT | VERIFIED |
| AssetWin | Purchase date | `_purchase` / `FixedAsset.PurchaseDate` | fixed_assets.purchase_date | same | same | VERIFIED |
| AssetWin | Cost / Salvage | `_cost/_salvage` / `Cost/SalvageValue` | fixed_assets.cost / salvage_value | same | same | VERIFIED |
| AssetWin | Life years | `_life` / `UsefulLifeYears` | fixed_assets.useful_life_years | same | same | VERIFIED |
| AssetWin | Method / Status | `_method/_status` / `DepreciationMethod/Status` | fixed_assets.depreciation_method / status | same | same | VERIFIED |
| AssetWin | Asset grid (Code/Name/Category/Purchase/Cost/Status) | `_grid` ← `FixedAsset.*` | fixed_assets.* | n/a read-only | `RefreshAsync` | VERIFIED |
| AssetWin | Accumulated / Book value (editor, read-only) | `_accumulated/_bookValue` (IsReadOnly=true) | — computed, never stored (by design, mig 027) | n/a | Computed in `OnAssetSelected` via `AssetService.AccumulatedDepreciation/BookValue` | VERIFIED (computed) |
| AssetWin | Schedule grid (Year/Depreciation/Accumulated/BookValue) | `_scheduleGrid` ← `DepreciationYearRow.*` | — computed, never stored | n/a | `AssetService.BuildSchedule(asset)` in `OnAssetSelected` | VERIFIED (computed) |
| AssetWin | Window geometry | BaseWin | WinPref | `WinPref.SaveGeometry` | `WinPref.RestoreGeometry` | VERIFIED |

**Gap summary:** No gaps. Fully VERIFIED reference window.

---

## Shell / admin / setup windows

### MWin.xaml.cs
*Per-window: NOT BaseWin — `RibbonWindow` · no bottom action bar (top ribbon + status bar) · Lang keys via `LocProps`/`UiLoczr`/`RefreshLocalizedText` with hardcoded English fallbacks; some hardcoded strings (recycle-bin tooltips, "Restore"/"Maximize").*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| MWin | — business data-entry fields | — | — | — | — | NO_DATA_FIELDS (shell/navigation only) |
| MWin | Window geometry/state | `UsrPref.WinW/WinH/WinL/WinT/WinState` | UsrPref store (userprefs.json) | `OnClosed` → `pref.SaveFromWindow(this); pref.Save()` (MWin.xaml.cs) | `ApplyWindowPreferences` → `UsrPref.Load().ApplyToWindow(this)` | PARTIAL — ctor then forces `WindowState=Maximized`, overriding saved WinState |
| MWin | UI language (menu switch) | `UsrPref.Lang/LangSet` | UsrPref store | `SwitchLanguage` → `App.SetApplicationLanguage` + `pref.Save()` | `App.OnStartup` → `UsrPref.Load()` | VERIFIED |
| MWin | Accent color | `UsrPref.AccentColor` via `VisuId.SetAccent` | UsrPref store | `OnAccentPickerClick` → `VisuId.SetAccent` → `pref.Save()` (Services/VisuId.cs) | `VisuId.ApplySavedAccent` → `UsrPref.Load()` | VERIFIED |
| MWin | Current ribbon tab | `_currentTab` (in-memory only) | UsrPref.LastTab exists but is never read/written anywhere | — | — | GAP NEVER_SAVED |
| MWin | Recycle-bin badge count | tooltip on `RecycleBinButton/CornerButton` | source tables is_deleted (count) | n/a | `RefreshRecycleBinBadgeAsync` → `RecycleBinSvc.CountAsync` | VERIFIED (display) |

**Gap summary:** No data fields by design. 1 GAP (current tab not persisted despite `UsrPref.LastTab` existing), 1 PARTIAL (saved window state overridden to Maximized).

### LogWin.xaml.cs
*Per-window: NOT BaseWin (`Window`) · no bottom bar (OK/Cancel `btnBar` inside the login card) · Lang keys via `LogWinVM` (`Loc.Get("Login.*")`) except hardcoded XAML `"Verifying..."` and English `AutomationProperties`.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| LogWin | Username | `txtUsername` ↔ `LogWinVM.Username` | UsrPref.RememberedUsername (store); authenticated against users.username | `LogWinVM.LoginAsync` → `UsrPref.Load(); prefs.Save()` (ViewModels/LogWinVM.cs); also saved on Hide-username toggle | `LogWinVM.InitializeFromSelectedCompany` → `UsrPref.Load()` | VERIFIED (note: hardcoded default `"admin"` when no pref) |
| LogWin | Password | `txtPassword`/`txtPasswordVisible` ↔ `LogWinVM.Password` | users.password_hash/password_salt (verify only) | Never persisted — `AppHost.Auth.LoginAsync(user,pass)` → `Auth` service checks hash, updates users.failed_login_count/locked_until (Services/Auth.cs) | n/a | VERIFIED (never saved by design; `UsrPref.RememberPassword` exists but is never used) |
| LogWin | Hide username on next launch | CheckBox ↔ `LogWinVM.HideUsernameOnNextLaunch` | UsrPref.HideUsernameOnNextLaunch | `OnHideUsernameOnNextLaunchChanged` → `prefs.Save()` (immediate, pre-login) | `InitializeFromSelectedCompany` → `UsrPref.Load()` | VERIFIED |
| LogWin | Show password toggle | `LogWinVM.IsPasswordVisible` | — | — | — | Transient, never saved (by design) |
| LogWin | Last-opened company (side effect of login) | `AppState.CurrentCompanyCode` | companies.json: StoredCoRec.LastOpenedAt + ordering | `OnLoginSucceeded` → `CoLocalStore.MarkAsLastOpenedAsync` (LogWin.xaml.cs) | `ComWin.LoadCompaniesAsync` → `CoLocalStore.LoadAsync` ordering | VERIFIED |
| LogWin | Company name / title (display) | `LogWinVM.CompanyName/WindowTitle` ← `App.Current.Properties["SelectedCompany"]` | — | n/a | `InitializeFromSelectedCompany` | VERIFIED (display) |

**Gap summary:** No persistence gaps for its fields. Password correctly never stored.

### Lang.xaml.cs
*Per-window: NOT BaseWin (`Window`) · buttons in in-card `btnBar` (not a window bottom bar) · HARDCODED English XAML ("Select Language", "Preferred language", language names) — the picker itself is not localized.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| Lang | Language choice | `cmbLanguage` (ComboBoxItem Tag en/ar/fr/tr) → `Lang.Selected` | UsrPref.Lang + UsrPref.LangSet (userprefs.json) | Window itself only calls `App.SetApplicationLanguage(Selected)` (runtime); persistence by caller: `App.RunStartupSequence` → `pref.Lang=chosen; pref.LangSet=true; pref.Save()` (App.xaml.cs) | `App.OnStartup` → `UsrPref.Load()`; picker always re-opens at "en" (SelectedIndex=0 in `OnLoaded`) | VERIFIED (indirect — save lives in App, not the window; Cancel saves nothing and shuts down) |

**Gap summary:** No field gap. Note the indirect save path: if any other caller shows `Lang` expecting it to persist, it will not.

### StpWiz.xaml.cs (Setup Wizard, create-only — no load path by design; `LoadDefaults` supplies defaults, and in `FirstRun` mode hardcoded sample data: "ShouTech Software Solutions", tax `123456789`, Damascus address, etc. — **REMOVED on this branch**: all identity fields now start empty so a click-through can no longer persist a fabricated company)
*Per-window: NOT BaseWin (`Window`) · bottom nav bar YES (`navigationButtons`, Grid.Row=2) · Lang keys `W("…")` with English fallbacks + hardcoded XAML defaults. Save for all rows: `BtnFinish_Click → FinishWizardAsync → BuildRequest() → CoCreateSvc.CreateAsync` (Services/Company.cs) → `CoLocalStore.SaveCompanyAsync` (companies.json, full record) + `TrySaveToDatabaseAsync/TrySaveToSqliteAsync` (DB, subset only). Success is reported if **either** store succeeded (`savedLocally || savedToDb`).*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path | Load path | Status |
|---|---|---|---|---|---|---|
| StpWiz | Company name (local) | `txtCompanyName` → `NewCoReq.CompanyNameAr` | companies.company_name_ar + companies.json Name | CreateAsync (above) | — create-only | VERIFIED |
| StpWiz | Company name (EN) | `txtCompanyNameEn` → CompanyNameEn | companies.company_name_en + json NameEn | same | — | VERIFIED |
| StpWiz | Tax number | `txtTaxNumber` → TaxNumber | json TaxNumber only — DB INSERT puts **License** into companies.commercial_registration, TaxNumber is never written to DB | same | — | GAP PARTIAL / NEVER_SAVED (DB) — NO_COLUMN for a distinct tax column |
| StpWiz | License | `txtLicense` → License | companies.commercial_registration + json License | same | — | VERIFIED |
| StpWiz | Email / Phone / Mobile | `txtEmail/txtPhone/txtMobile` | companies.email/phone/mobile + json | same | — | VERIFIED |
| StpWiz | Website | `txtWebsite` → Website | json only | same | — | GAP PARTIAL — NO_COLUMN (companies has no website column) |
| StpWiz | Address line 1 | `txtAddressLine1` | companies.address_line1 + json | same | — | VERIFIED |
| StpWiz | Address line 2 / City / Country / Notes | `txtAddressLine2/txtCity/txtCountry/txtNotes` | json only | same | — | GAP PARTIAL — NO_COLUMN in companies |
| StpWiz | Currency | `cmbCurrency` → CurrencyCode | companies.currency_code + json | same | — | VERIFIED |
| StpWiz | Decimal places / Calendar / Digit grouping | `cmbDecimalPlaces/cmbCalendarType/cmbNumberGrouping` | json only (DecimalPlaces/CalendarType/NumberGrouping) | same | — | GAP PARTIAL — NO_COLUMN in companies |
| StpWiz | Primary UI language | `cmbLanguageCode` → LanguageCode | companies.language_code + json (note: this is the *company* language, not UsrPref.Lang) | same | — | VERIFIED |
| StpWiz | COA template | `cmbCoaTemplate` → CoaTemplateCode | json only; **never consumed anywhere** (grep: only stored in StoredCoRec/NewCoReq/ComWin edit) — no chart seeding reads it | same | — | GAP FAKE_SAVE (saved, zero effect) + NO_COLUMN |
| StpWiz | Default warehouse name | `txtDefaultWarehouse` → DefaultWarehouseName | warehouses (seeded) + json | CreateAsync → `SeedDefaultWarehouseAsync(record)` INSERT INTO warehouses | — | VERIFIED |
| StpWiz | Fiscal start / end | `dpFiscalStart/dpFiscalEnd` | fiscal_years.start_date/end_date (+ fiscal_year) + json FiscalYearStart/End | TrySaveToDatabaseAsync INSERT fiscal_years | — | VERIFIED |
| StpWiz | Admin username | `txtAdminUser` → AdminUsername | users.username + json AdminUsername | INSERT INTO users | — | VERIFIED |
| StpWiz | Admin full name | `txtAdminFullName` → AdminFullName (falls back to username in BuildRequest) | users.full_name/full_name_ar/full_name_en + json | INSERT INTO users | — | VERIFIED |
| StpWiz | Admin password | `txtAdminPassword` → AdminPassword | users.password_hash/password_salt (PBKDF2 via `PwdHash.Create`, policy-checked) + json AdminPasswordHash/Salt on StoredCoRec | CreateAsync | — | VERIFIED (hashed, never plaintext) |
| StpWiz | Confirm password | `txtAdminPasswordConfirm` | — validation only | `ValidateStep(3)` equality check | — | VERIFIED (correctly not stored) |
| StpWiz | Database path | `txtDbPath` (IsReadOnly, auto-filled) | json DbPath (it *is* the DB location) | `PopulateManagedStoragePathsAsync` fills; CreateAsync saves | — | VERIFIED (local store) |
| StpWiz | Alt DB path / Backup path / Alt backup path | `txtAltDbPath/txtBkpPath/txtAltBkpPath` | json AltDbPath/BackupPath/AltBackupPath only | CreateAsync | — | VERIFIED (local store; no DB column exists/needed) |
| StpWiz | Auto backup / Schedule / Backup time | `chkAutoBackup/cmbBackupSchedule/txtBackupTime` | json AutoBackup/BackupSchedule/BackupTime only | CreateAsync | — | GAP PARTIAL — NO_COLUMN; no consumer of these json values was found in the wizard path |
| StpWiz | Database provider | — (no control; hardcoded `"SQLite"` in `BuildRequest`) | json DatabaseProvider + users/companies provider choice | CreateAsync | — | PARTIAL — user cannot choose PostgreSQL in the wizard despite PG code paths existing |

**Gap summary:** Core identity/fiscal/admin fields VERIFIED dual-store. 1 FAKE_SAVE (COA template never applied), TaxNumber never reaches DB, ~10 fields local-json-only with NO_COLUMN in `companies` (001_sqlite_core.sql confirms the table has only name_ar/en, email, phone, mobile, address_line1, commercial_registration, currency_code, language_code). FirstRun mode pre-fills fabricated sample company data that will persist verbatim if the user clicks through.

### ComWin.xaml.cs
*Per-window: NOT BaseWin (`Window, INotifyPropertyChanged`) · bottom bars YES (`btnBar` + `editBtnBar`, Grid.Row=2) · Lang keys `Loc.Get("Company*…")` with English fallbacks. Edit save for all rows: `BtnEditSave_Click → CoRegSvc.UpdateCompanyProfileAsync(profile)` (Services/Company.cs) → companies.json (full StoredCoRec) + `TryUpdateInDatabaseAsync` (DB subset). Edit load for all rows: `EnterEditModeAsync ← CoLocalStore.LoadAllAsync()` — **local json only; the DB is never read back in this window**.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path | Load path | Status |
|---|---|---|---|---|---|---|
| ComWin | Company list (Code/Name/NameEn) | `lstCompanies` ← `Companies`/`CompanyItem` | companies.json index; fallback DB via `CoRegSvc.GetCompaniesAsync` | Reindex: `CoRegSvc.RebuildCompanyIndexAsync` | `LoadCompaniesAsync` → `TryLoadCachedIndex()` (json) or GetCompaniesAsync | VERIFIED (display/selection) |
| ComWin | Name | `txtEditName` → StoredCoRec.Name | companies.company_name_ar + json | UpdateCompanyProfileAsync | EnterEditModeAsync (json) | VERIFIED |
| ComWin | Name (EN) | `txtEditNameEn` → NameEn | companies.company_name_en + json | same | same — but load blanks NameEn when it equals Name, so re-save writes "" | PARTIAL (load/save asymmetry) |
| ComWin | Tax number | `txtEditTax` → TaxNumber | json only; DB gets `commercial_registration = TaxNumber ?? License` (conflated in TryUpdateInDatabaseAsync) | same | same | GAP PARTIAL — no distinct DB column; collides with License |
| ComWin | License | `txtEditLicense` → License | json License; DB only if TaxNumber empty (see above) | same | same | GAP PARTIAL — NEVER_SAVED to DB when TaxNumber is present |
| ComWin | Email / Phone / Mobile / Address 1 / Currency / Language | `txtEditEmail/Phone/Mobile/Address/Currency/Language` | companies.email/phone/mobile/address_line1/currency_code/language_code + json | same | same (json) | VERIFIED |
| ComWin | Website / Address 2 / City / Country / Notes | `txtEditWebsite/Address2/City/Country/Notes` | json only | same | same | GAP PARTIAL — NO_COLUMN in companies |
| ComWin | Decimal places / Calendar / Number grouping / COA template / Default warehouse | `txtEditDecimalPlaces/Calendar/Grouping/Coa/Warehouse` | json only (COA template additionally never consumed — see StpWiz) | same | same | GAP PARTIAL — NO_COLUMN |
| ComWin | Fiscal start / end | `dpEditFiscalStart/dpEditFiscalEnd` | json FiscalYearStart/End only — edit path never updates fiscal_years (unlike wizard create) | same | same | GAP PARTIAL — NEVER_SAVED to DB on edit |
| ComWin | DB path / Alt DB path / Backup path / Alt backup path / Backup schedule / Backup time / Auto backup | `txtEditDbPath/AltDbPath/BackupPath/AltBackupPath/BackupSchedule/BackupTime`, `chkEditAutoBackup` | json only | same | same | PARTIAL — local-store VERIFIED, NO_COLUMN in DB (paths are config, but schedule/time/auto-backup have no DB home or consumer) |
| ComWin | Admin username / Admin name / DB provider | `txtEditAdminUser/AdminName/DbProvider` — all `IsReadOnly="True"` in XAML | json AdminUsername/AdminFullName/DatabaseProvider (display) | Not editable by design; values carried through unchanged in UpdateCompanyProfileAsync | EnterEditModeAsync (json) | VERIFIED (read-only display) |

**Gap summary:** The edit form *looks* fully persisted because the local json round-trips perfectly, but the DB (`companies`) only receives 9 columns; ~19 fields are PARTIAL/NO_COLUMN, Tax↔License share one DB column inconsistently with the wizard (wizard writes License, edit writes Tax-first), and fiscal dates edited here never reach `fiscal_years`.

### PrmWin.xaml.cs
*Per-window: NOT BaseWin (`Window`) · bottom row YES (Grid.Row=2: Cancel/Save) · HARDCODED English throughout (XAML literals "User permissions"/"Save"/"Cancel", VM strings "Permissions saved successfully.", error "Could not load permissions." in PrmWin.xaml.cs).*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| PrmWin | Permission allowed (per row, per tab) | CheckBox ↔ `PermissionItemVM.IsAllowed` (PrmWinVM) | user_permissions.effect (override; row deleted when equal to role baseline) | `Save_Click` (PrmWin.xaml.cs) → `PrmWinVM.SaveAsync` → `PermAdminSvc.SaveForUserAsync` → INSERT/DELETE user_permissions, transactional, both providers (Services/Permissions.cs) | `OnLoaded` → `PrmWinVM.LoadAsync` → `PermAdminSvc.LoadForUserAsync` (permissions ⨝ role_permissions ⨝ user_permissions) | VERIFIED |
| PrmWin | Permission Name / Code / Operation (display) | `PermissionItemVM.Name/Code/Operation` | permissions.name_en/name_ar (via AppLanguage pick), permission_code, operation_code | n/a | Same LoadForUserAsync + `LoadGroupsAsync` (permission_groups) | VERIFIED (display) |
| PrmWin | Tab grouping | `PermissionTabVM` built by `PrefixForGroup` mapping in PrmWinVM.LoadAsync | permission_groups.group_code | n/a | LoadAsync filters `x.Code.Split('.')[0] == PrefixForGroup(group.Code)` | PARTIAL — any permission whose code prefix doesn't match its group's mapped prefix is silently dropped from the UI and can never be edited/saved here |
| PrmWin | UserId header (display) | `PrmWinVM.UserId` (ctor arg) | users.user_id (key for all rows) | n/a | ctor | VERIFIED |

**Gap summary:** The one editable field is fully VERIFIED on both providers. PARTIAL risk in tab filtering (unmapped prefixes invisible). Fully hardcoded English.

### SplWin.xaml.cs
*Per-window: NOT BaseWin (`Window`) · no bottom bar · fully HARDCODED English (status strings, title; `ApplyLocalization` only forces LTR).*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path | Load path | Status |
|---|---|---|---|---|---|---|
| SplWin | — data-entry fields | — | — | — | — | NO_DATA_FIELDS |
| SplWin | App name / EN name / Version / Copyright (display) | `txtAppName/txtAppNameEn/txtVersion/txtCopyright` | appset.json `Application.*` (AppCfgSvc) | n/a | `ApplyConfigTexts` → `AppCfgSvc.Current` | VERIFIED (display) |
| SplWin | Progress / status | `txtStatus`, progress bar | — transient startup pipeline output | n/a | n/a | Transient, not persisted (by design) |

**Gap summary:** None — splash/display only.

### SerWin.xaml.cs
*Per-window: BaseWin YES (but `ApplyLocalizedText` override is an empty stub — captions set manually in `ApplyDirectionAndLanguage` for OK/Cancel only, via `Loc.Get`) · OK/Cancel buttons in XAML · Lang keys with English fallbacks for buttons; title/columns supplied by caller.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| SerWin | Search text | `TxtSearch` | — filter only | n/a | n/a | Transient, never saved (by design) |
| SerWin | Results grid columns (dynamic, caller-defined) | `DgResults` columns `Binding=Values[{Key}]`, `IsReadOnly=true`; rows `SerWinItem.Values/Payload` | Caller's tables (e.g. materials via `MatRepo.LoadAll()` in `PickMaterial`) | SerWin saves nothing — `Accept()` returns `SelectedItem` to caller, caller persists | `Configure(items, columns)` / `Pick(...)` — caller loads | VERIFIED (display/selection; persistence is caller's) |
| SerWin | Window size/position | `SerWinPrefs.Width/Height/Left/Top` | serwin.json store | `OnClosing → SaveWindowState → SerWinPrefs.Save()` (atomic tmp+move) | `OnLoaded → RestoreWindowState → SerWinPrefs.Load()` | VERIFIED — but PARTIAL redundancy: BaseWin also restores/saves WinPref geometry for the same window; SerWin's restore runs after and wins |
| SerWin | (dead code) UsrPref access | `GetPrefs()/_cachedPrefs` | UsrPref store | — defined, never called anywhere in the file | — | GAP (dead code, no effect) |

**Gap summary:** No user-data gaps (dialog is read-only by design). Redundant dual geometry persistence (WinPref + serwin.json) and dead `GetPrefs()`.

### BkpWin.xaml.cs
*Per-window: NOT BaseWin (`Window`) · bottom bar YES (Grid.Row=3: status/progress + Restore/Close; Create/Refresh/OpenFolder in header area) · Lang keys `Backup.*` via `AppHost.Localization.GetString` with English fallbacks.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| BkpWin | Backup note | `txtNotes` TextBox | No DB column/store — encoded only into the .stb **filename** (`BuildBackupFileName`: sanitized, truncated to 40 chars, Services/Backup.cs) | `Create_Click` → `AppHost.Backup.CreateBackupAsync(CompanyCode, notes)` | Never loaded back as a note; only visible as part of FileName in the list | GAP PARTIAL — lossy (sanitized/truncated), no metadata store |
| BkpWin | Backup list: Name / Created / Size | `lstBackups` ← `BkpRow.FileName/LocalDateText/SizeText` ← `BkpFileInfo` | Filesystem: `AppPaths.CompanyBackup(code)/*.stb` (FileInfo) | n/a | `RebuildIndexAsync` → `AppHost.Backup.ListBackups(CompanyCode)` | VERIFIED (display) |
| BkpWin | Backup list: Status / IsValid | `BkpRow.Status/IsValid` | — session only; `ListBackups` always returns `Status="NotValidated"` | Set in-memory after Validate in `Restore_Click`/`RebuildIndexAsync(verifiedFilePath)` | Never reloaded — resets to "Not checked" every open | GAP NEVER_LOADED (validation state not persisted, by apparent design) |
| BkpWin | Restore (action on selected row) | `lstBackups.SelectedItem` → `BkpRow.FilePath` | Whole company DB/files replaced from .stb | `Restore_Click` → `ValidateBackupDetailedAsync` → `RestoreBackupAsync` | n/a | VERIFIED (action) |

**Gap summary:** 1 PARTIAL (note survives only mangled into a filename), 1 NEVER_LOADED (validation status). Backup/restore mechanics themselves verified.

### RestoreCoWin.xaml.cs
*Per-window: NOT BaseWin (`Window`) · buttons in bottom grid row of XAML · Lang keys `Loc.Get("RestoreCompany.*")` with English fallbacks; XAML defaults hardcoded.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| RestoreCoWin | Deleted-company list (Code/Name/NameEn → DisplayName) | `lstDeleted` ← `DeletedCompanyItem.Code/Name/NameEn` | companies (is_deleted=1 rows) + companies.json | n/a | `ReloadAsync` → `CoRegSvc.GetDeletedCompaniesAsync()` (Services/Company.cs, both providers) | VERIFIED (display) |
| RestoreCoWin | Restore (action on selected row) | `lstDeleted.SelectedItem.Code` | companies.is_deleted=0, is_active=1 + reindex into companies.json | `BtnRestore_Click` → `CoRegSvc.RestoreCompanyAsync(item.Code)` UPDATE companies + SaveCompanyAsync | List reloads via `ReloadAsync` after restore | VERIFIED |

**Gap summary:** No gaps. No editable fields; the single action is fully persisted on both providers.

### BinWin.xaml.cs
*Per-window: NOT BaseWin (`Window`) · bottom button row in XAML (Refresh/Restore/Purge/Close) · Lang keys `Loc.Get("RecycleBin.*")` with English fallbacks.*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| BinWin | Grid: Dataset / Label / Table / Deleted at / Deleted by | `ItemsGrid` columns ← `RecycleBinItem.DatasetKey/DisplayLabel/TableName/DeletedAt/DeletedBy`; grid `IsReadOnly="True"` | Whitelisted master tables (RecycleBinSvc.Catalog: products, customers, suppliers, accounts, chart_of_accounts, branches, warehouses, …) WHERE is_deleted=1, per-company | n/a (read-only) | `ReloadAsync` → `RecycleBinSvc.ListAsync(companyCode)` (Services/RecycleBinSvc.cs) | VERIFIED (display) |
| BinWin | Restore selected (action) | `ItemsGrid.SelectedItems` | Source table is_deleted=0 (+ audit event) | `Restore_Click` → `RecycleBinSvc.RestoreManyAsync` UPDATE … SET is_deleted=0 | Reload after action | VERIFIED |
| BinWin | Delete permanently (action) | `ItemsGrid.SelectedItems` | Source table row DELETE (+ audit event) | `Purge_Click` → `RecycleBinSvc.PurgeAsync` DELETE FROM {table} | Reload after action | VERIFIED |

**Gap summary:** No gaps. No editable fields; grid is read-only, both actions write through with audit.

### ColChsWin.xaml.cs
*Per-window: BaseWin YES (`PersistGeometry=false` override) · bottom bar YES (DockPanel Grid.Row=3: SelectAll/ClearAll left, OK/Cancel right) · full Lang keys (`T("Report.Columns.*")`, `T("Common.*")`).*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| ColChsWin | Column visibility (per row) | CheckBox ↔ `ColChsWin.Item.Visible` in `LstColumns` | WinPref store: ReportColumns[scope].Visible | Dialog itself saves nothing; caller `RptBase.OnColumnsClick` → `WinPref.SaveReportColumns(ColumnScope(), recs)` (Views/RptBase.cs) | Caller `RptBase.ApplyColumnLayout` → `WinPref.LoadReportColumns(ColumnScope())` | VERIFIED (indirect via RptBase) |
| ColChsWin | Column order (Move up/down) | `_items` ObservableCollection order → `ColChsWin.Result` | WinPref store: ReportColumns[scope].Order (+ Width carried from report def) | Same `RptBase.OnColumnsClick` path | Same `ApplyColumnLayout` (OrderBy Order) | VERIFIED (indirect) |
| ColChsWin | Column header/key | `Item.Header/Key` (init-only, caller-supplied) | — identity, not user data | n/a | ctor from RptBase column defs | VERIFIED |

**Gap summary:** No gaps, provided the caller is RptBase (the only caller found). The dialog alone persists nothing — cancellation correctly discards.

### CoaWin.xaml.cs
*Per-window: BaseWin YES (but `ApplyLocalizedText` is an empty stub) · bottom bar YES (Grid.Row=3: Close left, Export/Import/Refresh right) · HARDCODED English almost everywhere (XAML literals, menu items, messages) plus placeholder XAML strings `"Text"`/`"ToolTip"`. Account editing itself is delegated to AccCard (outside this audit's scope).*

| Window | Field/Grid column (UI) | Bound property/control | DB table.column (or store) | Save path (method, file) | Load path (method, file) | Status |
|---|---|---|---|---|---|---|
| CoaWin | Tree: account code column | XAML `{Binding DisplayCode}` | accounts.account_code | — | `LoadChart → AccRepo.LoadAll()` loads `AccRec.Code` into `AccountNode.Code`, but **no `DisplayCode` property exists anywhere in the codebase** (grep: only the XAML binding) — column renders blank | **FIXED** on this branch: `AccountNode.DisplayCode` property added |
| CoaWin | Tree: account name | `{Binding DisplayName}` → `AccountNode.DisplayName` (NameEn-first, always) | accounts.name_en / name_ar | Via AccCard → `AccRepo.Save` (Services/Accounting.cs) | `LoadChart → BuildTreeFromRows` maps Name/NameEn | PARTIAL — loaded, but template uses NameEn-first `DisplayName` while code-behind `GetDisplayName` switches by language; the two disagree for Arabic |
| CoaWin | Tree: type | `AccountNode.TypeLabel` | accounts.posting_mode (collapsed: Summary→"Group", everything else→"Account") | Via AccCard | `BuildTreeFromRows` from `AccRec.PostingMode` | PARTIAL — lossy derivation (Postable and any other mode are indistinguishable) |
| CoaWin | Tree: all other account fields (Status, Risk, CostCenter, Reconcile, Taxable, ForeignCurrency, CostRequired, Budget, IsDefault, TaxRate, Description, Balance, Debit, Credit) | Not mapped into `AccountNode` at all (only Id/Code/ParentCode/Name/NameEn/TypeLabel are) | accounts.status/risk/cost_center/allow_reconcile/taxable/foreign_currency/cost_required/budget/is_default/tax_rate/description/balance/debit/credit | Via AccCard → `AccRepo.Save` (full AccRec) | `AccRepo.LoadAll()` reads all 20 columns, `BuildTreeFromRows` discards 14 of them | PARTIAL — in DB and in AccRec, invisible/unavailable in this window |
| CoaWin | Search box | `TxtSearch` | — filter over in-memory tree only | n/a | n/a | Transient, never saved (by design) |
| CoaWin | Tree expand/collapse state | `AccountNode.IsExpanded` (TwoWay TreeViewItem binding) | — | User expand/collapse only mutates memory (`ExpandAll/CollapseAll/SetExpanded`); initial value reset to `ParentCode==null` on every `LoadChart` | — | GAP NEVER_SAVED (UI state) |
| CoaWin | Import CSV / XLSX / encrypted .sta | File → `ImportAccounts(IReadOnlyCollection<StaAccount>)` | accounts.* | **`ImportAccounts` only rebuilds `_allRoots` in memory and calls `RebindTree` — there is NO `AccRepo.Save` call anywhere in the import path**, yet `ImportFile`/`ImportEncrypted` show "Chart of accounts imported successfully." Data is lost on Refresh/close (`LoadChart` reloads from DB) | n/a | **FIXED** on this branch: import upserts every account by code via `AccRepo.Save` and reloads from the DB |
| CoaWin | Export CSV / XLSX / .sta | `BuildDelimitedContent`/`BuildXlsxSheet`/`ToStaAccount` | File only: Code, ParentCode, Name, NameEn, Type (5 of 20 AccRec fields) | Export writes the file correctly for those 5 fields | Import reads the same 5 | PARTIAL — round-trip silently drops the other 15 fields (status, flags, tax, balances…) even before the FAKE_SAVE import bug |
| CoaWin | Seed default chart (when DB empty) | `SeedDefaultChart` hardcodes 48 accounts | accounts.* via `AccRepo.Save(null, rec, false)` | `LoadChart` when `LoadAll().Count==0` | `LoadAll` | VERIFIED — note: seed passes identical strings for name_ar and name_en on every row |
| CoaWin | Open account / Add child (actions) | Context menu → `AccCard(record)` / `AccCard(parentCode)` | accounts.* | AccCard.Saved → `RefreshChart` (AccCard itself uses `AccRepo.FindById/Save`) | `AccRepo.FindById(node.Id)` in `OpenSelectedAccount` | VERIFIED (delegated) |
| CoaWin | Statement / Ledger (menu actions) | `Statement_Click/Ledger_Click → ShowAction(...)` | — | No query at all — shows a MessageBox "Action: … Account: …" | — | **FIXED** on this branch: both open the real account statement report via `RptNav.OpenAccountStatement` |
| CoaWin | Window geometry | BaseWin WinPref + own positioning | WinPref store | `WinPref.SaveGeometry` (BaseWin) | BaseWin restore, then CoaWin `Loaded` unconditionally calls `PositionOnRightWithPadding()` (overrides Left/Top) and `SizeToLongestAccountName()` (overrides Width) | PARTIAL — saved position/width never actually honored |

**Gap summary:** Worst window in the set: 1 FAKE_SAVE (import reports success, persists nothing), 1 NEVER_LOADED (broken `DisplayCode` binding → blank code column), export/import round-trip drops 15 of 20 fields, 14 loaded DB fields never surface in the window, expand state never saved, geometry fight between BaseWin and the window's own Loaded handler, placeholder Statement/Ledger actions, and no localization.

---

## 7. Fix log (this branch)

| Commit | Windows | What was wrong → what was done |
|---|---|---|
| 44ced5e | schema | Migration **050** (sqlite + postgresql): creates customers, suppliers, bank_accounts, currencies + currency_rates, sales_reps + sales_rep_targets, manufacturing_orders + manufacturing_order_lines |
| b5ce2b4 | CstCard, BnkCard, CurCard, RepCard, MfgWin | All five were fake-save MessageBox windows → full CardStore persistence, RBAC, bottom bars, Load buttons, real metrics; MfgWin Post writes real stock movements; new `Cards.*` Lang section in all 4 catalogs |
| 0ed53b1 | PosCheckoutWin, InvoiceWin, JrnWin, BnkCard, BarWin | POS payment method persisted (new `Card` enum value); POS/Invoice/JrnWin currency hardcodes → UsrPref; InvoiceWin line material/warehouse ids resolved + carried; JrnWin messages localized (8 new keys × 4 catalogs); BarWin gained a real save-to-material path and lost its fake sample data |
| b98e6a3 | MatCard, WhsWin, CoaWin, MatDirWin, AccCard, PayRecWin, ProjWin | QtyOnHand data loss; warehouse Is Active both directions; CoaWin import fake save + DisplayCode binding; MatDirWin selection + print; AccCard/CoaWin Statement/Ledger wired to the real report; allocation note; task notes |
| e5cb2ad | CatWin, StpWiz | Parent-category selector (cycle-proof); fabricated FirstRun sample company removed |
| (error-reporting sweep) | CardStore, PosCheckoutWin, InvoiceWin, PayRecWin, ProjWin, CoaWin, MatDirWin, BarWin, CatWin, MatRepo, JrnWin | Owner rule 2026-11-17: every rebuilt/changed load/save path now reports via AppHost.Logging.LogError (see §8.1); silent resolver/rollback catches log; JrnWin dead ShowOperationUnavailable helper removed (zero call sites in owned scope) |

## 8. Verification

All runs on this branch (feat/legacy-windows-rebuild), 2026-11-17:

- **Full solution build**: `dotnet build ShouTech.sln -p:EnableWindowsTargeting=true` → **0 Error(s)**, 16 warnings (pre-existing classes: CS1998 in SalesChain tests, DatWin nullability, Backup/BkpWin/UsrWin/RptBase warnings; plus CA1305/CA1863 string.Format notes in JrnWin).
- **SqlMigrator (SQLite, fresh file)**: 31 migrations APPLY (001–030 + 050); second run 31 SKIP (idempotent); **57 tables**; all 9 migration-050 tables present.
- **Runtime smoke harness** (real save→reload round trips against a fresh migrated SQLite DB, exercising CardStore / MatRepo / AccRepo / InvRepo — the exact services the windows call): **16/16 PASS**:
  CstCard customer save→load (all 16 fields) · CstCard Both-kind writes customers+suppliers · CstCard soft delete · BnkCard save→load + soft delete · CurCard save→load (header + 2-row rate history, matched by date) · RepCard save→load (header + targets) + soft delete · WhsWin inactive warehouse (visible to master loader, hidden from consumer loader) · CardStore.ResolveMaterial · MfgWin order save→load (incl. TotalCost 90 / UnitCost 9 computed by the window's CaptureOrder formula) · MfgWin Post (status Posted, 1 MFG-OUT + 1 MFG-IN movement, finished balance 10) · CoaWin import upsert insert + update · POS Card payment method (enum + `sales_documents.payment_method='Card'`) · POS currency KWD + paid_amount persisted.
- **Stub sweep**: zero `ShowOperationUnavailable` call sites remain in owned-scope Views (the only remaining call sites are in ribbon-owned CshWin/ChqWin/OrdWin, replaced on that branch; JrnWin's now-dead helper was removed). Fake sample data removed from BarWin (ITEM-001 / "Sample Product" / 6281001234567 no longer present).
- **Not run here** (needs Windows / live PG): Kern.Tests execution, PostgreSQL migration run, WPF visual/RTL confirmation.

### 8.1 Error reporting (owner rule, 2026-11-17)

Every window rebuilt or whose load/save paths were changed on this branch reports failures through the central mechanism (`AppHost.Logging.LogError` with window + operation context) — no silent catches remain in those paths:

| Window / file | Reports centrally |
|---|---|
| CstCard, BnkCard, CurCard, RepCard, MfgWin | YES — every catch routes to `ReportFailure(op, ex)` which logs + shows |
| CardStore (new service) | YES — resolver/balance fallback catches now log before falling back |
| JrnWin | YES — all catches route to `ReportPersistenceFailure` (logs) |
| InvoiceWin | YES for operations — SaveDraft/Post/Reverse/OpenVoucher/OpenOriginal + reversal-state check now log (styling best-effort guards in BuildUi remain for the catalog sweep) |
| PosCheckoutWin | YES — CompleteSale logs |
| PayRecWin, ProjWin | YES — all catches log (added this branch) |
| CoaWin | YES — LoadChart + all export/import catches log (added this branch) |
| MatDirWin | YES — Reload/Remove/OpenGroups/Print + LoadGroups now log |
| BarWin | YES — SaveToMaterial/picker/localization fallback log |
| CatWin | Save path logs (SaveCurrent); remaining focus/image guard catches inventoried for the sweep |
| MatRepo (Inventory.cs) | Warehouse save rollback failures now log via the file's LogWarehouseError |

Remaining pre-existing best-effort guard catches (focus, theme-resource, image-preview, parse fallbacks) in files edited but **not rebuilt** here — MatCard.xaml.cs, CatWin.xaml.cs, WhsWin.xaml.cs, StpWiz.xaml.cs, InvRepo.cs, AccCard.xaml.cs — are inventoried for the feat/error-catalog branch's codebase sweep, whose coverage order starts at the startup chain. No load/save failure in any window touched by this branch is swallowed.

## 9. Remaining gaps — priority order

### 9.1 P1 — StpWiz/ComWin dual-store illusion (companies)
`companies.json` round-trips ~30 fields perfectly, which hides that the DB `companies` table (migration 001) has only 9–11 writable columns. Concretely: **TaxNumber has no DB column** (wizard writes License into `commercial_registration`; ComWin edit writes Tax-first into the same column — the two paths disagree), Website/AddressLine2/City/Country/Notes/DecimalPlaces/CalendarType/NumberGrouping/CoaTemplateCode have NO_COLUMN, fiscal dates edited in ComWin never reach `fiscal_years`, and the saved COA template code is **never consumed** by any seeding code (FAKE_SAVE effect). Remediation spec: migration 051 adds `tax_number, website, address_line2, city, country, notes, decimal_places, calendar_type, number_grouping, coa_template_code` to `companies` (both providers); `CoCreateSvc.TrySaveToDatabaseAsync/TrySaveToSqliteAsync` and `CoRegSvc.TryUpdateInDatabaseAsync` write all fields distinctly (License → commercial_registration, TaxNumber → tax_number) in both provider paths; ComWin edit upserts `fiscal_years`; COA template selection seeds the chart on company creation or the field is removed from both windows. Not done here: it is one coherent change across 4 SQL paths × 2 providers and must not be half-applied (create/edit would diverge).

**REMEDIATED 2026-11-17** (branch `feat/companies-dual-store-051`, Phase A1): migration 051 (both providers) adds exactly the columns listed above; all 4 write paths (`CoCreateSvc.TrySaveToDatabaseAsync` PG, `TrySaveToSqliteAsync`, and both provider branches of `CoRegSvc.TryUpdateInDatabaseAsync` — now transactional) write every field distinctly (License → commercial_registration, TaxNumber → tax_number); the edit path also upserts `fiscal_years` (edited year becomes current, missing row inserted). The COA template is now consumed at creation: `CoCreateSvc` resolves the selected code via the new `CoaTemplatesSvc.LoadByCode` before saving, refuses creation with the new catalog code **CO-3007** when the code cannot be resolved (never silently substitutes another chart), and seeds the template's accounts inside the creation transaction, keyed by company code with parent codes resolved through the template id map (posting_mode 'Postable'/'Summary'). Verified: full solution build 0 errors; SqlMigrator fresh SQLite 40 APPLY (incl. 051) / 40 SKIP; headless harness on the real services — create (template GCC) and full-field edit round-trips with **diff = 0** across all 19 DB profile fields (TaxNumber ≠ License throughout), fiscal_years upsert proven, 88 GCC accounts seeded, CO-3007 negative path proven. PostgreSQL paths are compile-verified only (no live PG), as with prior phases.

### 9.2 P1 — AccCard + CoaWin localization rebuild
Persistence is fully VERIFIED for both, but AccCard has **no Lang keys at all** (hardcoded English + XAML placeholder literals) and CoaWin is hardcoded almost everywhere; both predate the Phase 3 standard in text only. Rebuild = full key coverage in 4 catalogs, following the CatWin/MatCard pattern. (CoaWin additionally: export format carries only 5 of 20 account fields — extend StaAccount/delimited format or document the subset on the export UI; tree expand state and window geometry are not honored.)

### 9.3 P2 — Journal draft editing
`JournalService` has no `UpdateDraft`: a loaded draft in JrnWin is read-only. Add `UpdateDraftAsync` (transactional line replace, balanced check, audit) in src/ShouTech.Application + repository support, then enable editing for Draft status in JrnWin.

### 9.4 P2 — Smaller recorded gaps
- MatCard: `MaterialExcelExchange` Export/Import is a stub (FAKE) — implement real XLSX/CSV exchange or remove the buttons; attribute `SortOrder` has no grid column (PARTIAL).
- InvoiceWin: exchange-rate/tax-code UI for the existing columns; POS: held-cart persistence + line-discount UI + tendered/change UI at checkout.
- PrmWin: full localization (hardcoded English); tab filter drops permissions whose code prefix doesn't match the group mapping (PARTIAL — invisible permissions can't be edited).
- CashDrawerOptionsWin: bottom bar + Lang keys. BcdGenWin: warn on close with unsaved generated barcodes. SerWin: remove dead `GetPrefs()` and the duplicate geometry store. MWin: persist current tab (`UsrPref.LastTab` exists), honor saved WinState instead of forcing Maximized.
- BkpWin: backup note survives only mangled into the .stb filename (PARTIAL); validation status never persisted (by design — confirm or store).
- PayRecWin: CurrencyCode / party ids are loaded but have no grid column (display PARTIAL). WhsWin: OpenStockCount dialog strings hardcoded.
- ComWin: NameEn load/save asymmetry (load blanks NameEn when equal to Name, re-save writes "").

### 9.5 Settings-store exceptions (verified, by design — not DB gaps)
BcdDesWin design → `%AppData%/ShouTech/barcode-design.cfg`; CashDrawer options → `%AppData%/ShouTech/pos-settings.json`; WinPref geometry/columns → `%LocalAppData%/ShouTech/ERP/Prefs/`; UsrPref → `userprefs.json`; companies.json is the company-registry store (its DB mirror is §9.1).

## 10. Dependencies on the central mechanism (owned by fix/rtl-and-datwin)
- Direction/localization is applied centrally (BaseWin/Localization/VisuId). Windows deriving from `Window` directly (BinWin, BkpWin, ComWin, DatWin, Lang, LogWin, MWin, PrmWin, RestoreCoWin, SplWin, StpWiz) depend on that branch's central application point; this branch did not edit central files.
- AccCard.xaml hardcodes `FlowDirection="LeftToRight"` — flagged for the central-direction pass (removing it per-window without the central fix risks double-application).
- The Lang picker precedes any language choice, so its own hardcoding is partly by nature; localizing it via the *selected* language at runtime is a central-mechanism decision.
- SplWin forces LTR in its own ApplyLocalization — same dependency.

---

<!-- source: docs/WINDOW_CONTEXT_AUDIT.md -->

# Window shell-context audit

Generated by `tools/audit_window_context.py`. One row per window class.
Central routine: `ShellContext.ApplyToWindow` (Services/ShellContext.cs),
invoked for every window by the App-level Window.Loaded class handler and
by BaseWin; all values are resolved at open time from the central holders
(language/direction: LangDirSv/LocSvc + UsrPref.Lang; company: AppState +
AppHost.RebindDatabaseContext; theme/accent: UsrPref.Theme/AccentColor via
VisuId app resources; currency/formats: UsrPref; user/session: AppHost).

| Window | Base | Direction source | Company source | Theme/accent source |
|---|---|---|---|---|
| AccCard | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |
| AssetWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppHost service (Journal/Projects/Assets/Reconciliation) — connection-bound, REBOUND on company switch by AppHost.RebindDatabaseContext | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| BarWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |
| BcdDesWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| BcdGenWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| BinWin | Window | central — App class handler -> ShellContext.ApplyToWindow | AppState.CurrentCompanyCode read at use time + per-call services | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| BkpWin | Window | central — App class handler -> ShellContext.ApplyToWindow | AppState.CurrentCompanyCode read at use time + per-call services | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| BnkCard | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| CashDrawerOptionsWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |
| CatWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | per-call DatabaseRuntime.GetConnectionString() — current company at use time | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |
| CcCard | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| ChqWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| CoaWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| ColChsWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| ComWin | Window | central — App class handler -> ShellContext.ApplyToWindow | AppState.CurrentCompanyCode read at use time + per-call services | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| CshWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| CstCard | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| CurCard | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| DatWin | Window | central — App class handler -> ShellContext.ApplyToWindow | per-call DatabaseRuntime.GetConnectionString() — current company at use time | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| InvoiceWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| JrnWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppHost service (Journal/Projects/Assets/Reconciliation) — connection-bound, REBOUND on company switch by AppHost.RebindDatabaseContext | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |
| Lang | Window | central — App class handler -> ShellContext.ApplyToWindow (language picker: previews the candidate language, by design) | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| LogWin | Window | central — App class handler -> ShellContext.ApplyToWindow + own language-change hook (LangDirSv.ApplyToWindow) | AppState.CurrentCompanyCode read at use time + per-call services | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| MWin | RibbonWindow | central — App class handler -> ShellContext.ApplyToWindow + own ApplyUiDirection (same LangDirSv source) | AppState.CurrentCompanyCode read at use time + per-call services | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| MatCard | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppState.CurrentCompanyCode read at use time + per-call services | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| MatDirWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| MatGroupWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| MatMergeWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppState.CurrentCompanyCode read at use time + per-call services | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |
| MfgWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| ModuleWorkspaceView | System.Windows.Controls.UserControl | central — App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| OrdWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| PayRecWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppHost service (Journal/Projects/Assets/Reconciliation) — connection-bound, REBOUND on company switch by AppHost.RebindDatabaseContext | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| PosCheckoutWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| PosWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | per-call DatabaseRuntime.GetConnectionString() — current company at use time | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |
| PrmWin | Window | central — App class handler -> ShellContext.ApplyToWindow + own OnLoaded (UiLoczr + LangDirSv) | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| ProjWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppHost service (Journal/Projects/Assets/Reconciliation) — connection-bound, REBOUND on company switch by AppHost.RebindDatabaseContext | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| RepCard | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| RestoreCoWin | Window | central — App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| SerWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppState.CurrentCompanyCode read at use time + per-call services | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| SplWin | Window | central — App class handler -> ShellContext.ApplyToWindow + own ApplyLocalization -> LangDirSv.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | custom splash artwork (by design); direction central |
| StpWiz | Window | central — App class handler -> ShellContext.ApplyToWindow | no direct data access in the window file — delegates to services/ViewModels that resolve per call | central — DynamicResource against app-level VisuId resources (live accent/theme) |
| WhsWin | BaseWin | central — BaseWin + App class handler -> ShellContext.ApplyToWindow | AppState.CurrentCompanyCode read at use time + per-call services | central — app-level VisuId resources; code-resolved brush SNAPSHOTS keep the open-time accent until the window reopens |

Window classes audited: 42
Window-level FlowDirection pins remaining: 0
Control-level FlowDirection pins (direction-neutral content, justified): 7
  control pin: cod/app/src/Views/AccCard.xaml:153: <TextBox x:Name="TxtNameEn" Grid.Row="0" Grid.Column="3" Height="29" Padding="6,3" Margin="6,0,0,5" FlowDirect
  control pin: cod/app/src/Views/CatWin.xaml:199: FlowDirection="LeftToRight"
  control pin: cod/app/src/Views/CatWin.xaml:387: FlowDirection="LeftToRight"/>
  control pin: cod/app/src/Views/MWin.xaml:762: FlowDirection="LeftToRight"
  control pin: cod/app/src/Views/MatCard.xaml:825: FlowDirection="LeftToRight"
  control pin: cod/app/src/Views/MatCard.xaml:866: FlowDirection="LeftToRight"/>
  control pin: cod/app/src/Views/WhsWin.xaml:318: FlowDirection="LeftToRight"

Notes:
- Localized STRINGS: BaseWin windows re-localize live on language change;
  Window-derived dialogs that build strings once (e.g. SplWin, Lang) show the
  language that was active when they were constructed — they are transient
  startup dialogs; direction and theme still update centrally.
- Theme: the app ships a Light theme dictionary (app-level). UsrPref.Theme
  accepts Light/Dark but no Dark dictionary exists yet — pre-existing gap,
  unrelated to context propagation.
- Company switching closes secondary windows (ComWin flow) and rebinds the
  connection-bound AppHost services, so a window opened after a switch can
  only ever see the new company's database.

---

<!-- source: docs/WINDOW_DB_FIELD_AUDIT.md -->

# Window ↔ Database Field Persistence Audit

Branch: `main`
Scope: static source audit, first pass
Rule: a control bound in memory is **not** counted as persisted unless the save path writes it and the load path reads it back. A successful build is not a field round-trip test.

## Status definitions

- **Blocked**: save/post is deliberately rejected because there is no persistence implementation in the window.
- **Repository path exists**: the window calls a repository, but this does not prove every field/child row is mapped correctly.
- **Runtime round-trip pending**: save → close/reopen → compare every scalar field and child-grid row has not yet been demonstrated.

## High-priority data-entry windows

| Window | Entity / intended data | DB/repository path observed | Static finding | Field + child-row round-trip |
|---|---|---|---|---|
| `CstCard.cs` | Customers | No customer repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `BnkCard.cs` | Banks | No bank repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `CcCard.cs` | Cost centers | No cost-center repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `RepCard.cs` | Sales representatives | No representative repository call in window | **Blocked**; save/delete report unavailable | Not passed |
| `CurCard.cs` | Currencies / exchange rates | No currency repository call in window | **Blocked**; save reports unavailable | Not passed; exchange-rate rows need explicit coverage |
| `CshWin.cs` | Receipt/payment voucher + allocation lines | No voucher repository call in window | **Blocked**; save/post report unavailable | Not passed; header and every allocation row pending |
| `JrnWin.cs` | Journal header + debit/credit lines | `JournalService` + `JournalRepository`; company/account codes resolved through the active database | Real save/post path implemented; failures are logged and shown as failures | SQLite repository round-trip test added for header, lines, post, reversal, and cost centers; CI result pending; UI reopen/load is still not implemented |
| `ChqWin.cs` | Cheque batch + cheque rows | No cheque repository call in window | **Blocked**; save reports unavailable | Not passed; batch and every cheque row pending |
| `OrdWin.cs` | Sales/purchase order + item rows | No order repository call in window | **Blocked**; save is rejected and conversion now explicitly reports unavailable instead of opening an empty invoice (commit `ace64e2`) | Not passed |
| `MfgWin.cs` | Manufacturing order + item rows | No manufacturing repository call in window | **Blocked**; save/post report unavailable | Not passed |
| `InvoiceWin.cs` | Sales/purchase invoices and returns | `InvRepo.SaveDraft`, `InvRepo.Load`, `InvRepo.Post`, reversal path; material lookup via `MatRepo` | Real persistence path exists | Not proven for every header field and line field; round-trip pending |
| `PosCheckoutWin.cs` | POS sale | `InvRepo.SaveDraft`, `InvRepo.Post` | Real invoice persistence path exists | Tender/payment metadata and all invoice rows need verification |
| `AccCard.xaml.cs` | Chart-of-accounts card | `AccRepo.LoadAll`, `AccRepo.Save`, `AccRepo.Delete` | Real repository path exists | Every account field and parent/child behavior need round-trip tests |
| `MatCard.xaml.cs` | Material card, units/prices | `MatRepo.LoadAll`, `LoadById`, `Save`, `SoftDelete`; lookup helpers | Real repository path exists | Every material field plus all units/prices/barcodes need round-trip tests |
| `CatWin.xaml.cs` | Material groups/categories | `MatRepo.LoadGroups`, `SaveGroup`, `SoftDeleteGroup` | Real repository path exists | Every group field and parent relationship need round-trip tests |
| `WhsWin.xaml.cs` | Warehouses | `MatRepo.LoadWarehouses`, `SaveWarehouse`, `SoftDeleteWarehouse` | Real repository path exists | Every warehouse field and parent relationship need round-trip tests |

## Initial conclusions

1. **The audit cannot certify that every field is saved yet.** Static repository calls alone do not prove complete field mapping.
2. The ten blocked windows above do not currently provide a real database save path from their UI. Their forms must not be treated as production-ready data-entry screens.
3. `InvoiceWin`, `PosCheckoutWin`, `AccCard`, `MatCard`, `CatWin`, and `WhsWin` have repository paths, but require field-by-field verification against the actual SQLite and PostgreSQL schema.
4. Each document grid must be tested as parent + child records: save, reopen, compare every field and row count, edit one field, save again, reopen, and compare again. Posting must also be checked for atomicity and ledger/stock effects.
5. Reports, setup/security/backup/import-export windows, and the remaining master-data/document windows are not yet covered in this first pass. The complete inventory is still in progress; this file must not be read as a completed all-window certification.

## Required test matrix for each persistent form

- Record every input control and its backing property.
- Trace property → repository parameter/column → schema column.
- Trace schema column → repository mapper → input control on reload.
- Include nullable/optional fields, decimal precision, dates, checkboxes, combo-box foreign keys, and localized text.
- Include every DataGrid child row and add/remove/edit cases.
- Verify company/tenant scope, transaction boundaries, and error logging.
- Run SQLite and PostgreSQL tests where both providers are supported.


## Additional windows checked (second pass)

| Window | Observed data path | Static result | Round-trip |
|---|---|---|---|
| `CoaWin.xaml.cs` | `AccRepo.EnsureSchema`, `AccRepo.LoadAll`, `AccRepo.Save`, account import/export | Real account repository path; inspect every account property and import mapping | Not passed |
| `PosWin.cs` | `MatRepo.LoadById` and inventory/unit lookups; final checkout delegated to invoice workflow | Sales workspace itself is not the final persistence boundary; checkout and payment metadata need tracing | Not passed |
| `MatDirWin.xaml.cs` | `MatRepo.LoadAll`, `LoadGroups`, `SoftDelete` | Directory/search and delete use repository reads; verify delete confirmation/audit and all entry points | Not passed |
| `BcdGenWin.cs` | `MatRepo.LoadAll`, `LoadById(includeChildren: true)`, `MatRepo.Save` | Real material repository path. Commit `85d9e69` removes the unconditional success dialog: it now displays the saved/failed summary with an outcome-appropriate icon and logs per-material exceptions. | Not passed; generated barcode still needs reload verification |
| `SerWin.xaml.cs` | `MatRepo.LoadAll`; saves search-window preferences separately | Search dialog payload is a selected material, not a business-record save form | Not applicable to material persistence; preference round-trip not tested |
| `BkpWin.xaml.cs` / `RestoreCoWin.xaml.cs` | Backup/restore service workflow | These operate on backup packages/companies rather than ordinary form-row CRUD; require separate restore integrity tests | Not passed |
| `BarWin.cs` / `BcdDesWin.cs` | Barcode printing/design; design is documented as local configuration | Printing is not DB persistence; template/config persistence must be distinguished from business data | Not passed |

## Repository/schema cross-check observations

- `Inventory.cs` exposes real `MatRepo.LoadAll`, `LoadById`, `Save`, `SoftDelete`, `SaveWarehouse`, and group operations. The presence of these methods does not by itself prove that every `MatRec` field and each child table (extra barcodes, price list, suppliers, stock-related rows, units) is fully mapped in both directions.
- `InvRepo.cs` exposes `SaveDraft` and `Load`, with separate SQLite/PostgreSQL migration files for commercial documents. The invoice header and every line/tax/discount/payment-related field still need an explicit mapping comparison and database round-trip test.
- `Accounting.cs` exposes `AccRepo.LoadAll`, `Save`, and `Delete`; account-card and chart import paths need tests for all account properties, parent/child renumbering, and provider parity.
- The current PostgreSQL migration files `003_modules.sql` and `004_modules.sql` were fetched from `main` and have identical content (6,735 bytes and the same blob SHA). This is a migration-chain duplication risk that should be resolved only after checking the migration runner's applied-history compatibility; do not simply delete a migration that may already be recorded in deployed databases.
- The historical `docs/SCHEMA_AUDIT_PHASE1.md` explicitly marks itself superseded. Its 540/120/55 counts are historical, not a fresh current count. A fresh `tools/schema_audit.py` run and the strict mode should be run in CI before claiming that no service DDL remains.

## Next verification order

1. Audit each repository record type against SQL columns and its write/read mappers: `MatRec`, `WarehouseRec`, `MatGroupRec`, `AccRec`, `InvDoc` and `InvLine`.
2. Add automated integration tests using temporary SQLite databases for full create/read/update/re-read and child-row add/edit/remove; then run equivalent PostgreSQL tests.
3. Build a machine-readable per-window field matrix from controls and repository mappings. Mark a field **PASS** only after the persisted value is read back and compared; mark unsupported fields **BLOCKED**, never assume success.
4. Finish inventory of remaining `cod/app/src/Views/**` windows (reports, administration, import/export, company/user/security, configuration and document modules). Those windows are not certified by this partial report.


## Third-pass correction (2026-10-16)

- `BcdGenWin.SaveGenerated()` previously showed `Messages.SaveSuccess` even when `saved == 0` and all selected writes failed. Fixed on `main` in commit `85d9e69d47c74cede31e8075e2225cda73699983`: the dialog now shows the actual saved/failed summary and uses warning/error icons for partial/total failure; individual persistence exceptions are sent to `AppHost.Logging.LogError` with the material ID.
- This is a source-level correction only. It has **not** been runtime-tested here; barcode persistence must still be proven by reloading each changed material from the database. This audit remains partial and does not certify all windows or fields.

- `OrdWin.ConvertToInvoice()` was also corrected in commit `ace64e241bc38a65ff4cf26b883b7e7df20b1015`: the previous handler opened a blank invoice without transferring or persisting the order. It now reports the operation as unavailable, avoiding a misleading conversion workflow. Actual order persistence and conversion remain unimplemented.


## Fourth-pass findings (2026-10-16)

- **Dashboard data is hard-coded, not database-backed:** `cod/app/src/Services/Data.cs` implements `DataServ.GetTotalCustomersAsync()` as the constant `1284`, `GetTotalProductsAsync()` as `3567`, `GetTotalPendingApprovalsAsync()` as `12`, and returns a fixed sample list from `GetRecentDocumentsAsync()`. Dashboard figures and recent documents must not be presented as live company data. This needs replacement with provider-aware repository queries and empty/error states.
- **Account card failure logging:** commit `3a83615` adds a structured error log when `AccRepo.Save` fails, including the account code; the UI still reports failure rather than success.
- **Barcode design configuration failure logging:** commit `93f89f0` logs exceptions when the local design file cannot be saved. This configuration is stored in a file, not the business database, and should be audited separately from DB-backed forms.
- These are source-level checks only. No CI status was returned for the changes, and no database round-trip tests have been demonstrated.

## Fifth-pass findings (2026-10-16): remaining high-priority forms and reports

| Window | Persistence/data path observed | Current assessment | Required verification |
|---|---|---|---|
| `CurCard.cs` | Save button calls `ShowPersistenceUnavailable()` | **Blocked**: currencies/rates cannot be saved from this form | Add provider-neutral currency/rate repository and test currency fields plus rate history |
| `MfgWin.cs` | Save and post call `ShowPersistenceUnavailable()` | **Blocked**: manufacturing orders are not persisted | Define header/material-consumption/output rows and atomic posting effects |
| `CshWin.cs` | Save/post handlers ultimately call `ShowPersistenceUnavailable()` | **Blocked**: voucher header and allocations are not persisted | Implement receipt/payment repository; test header, allocations, posting and balancing |
| `JrnWin.cs` | Save/post call `JournalService` backed by `JournalRepository` | Database save/post path now exists; no false-success fallback | Verify CI and add database-backed load/reopen before claiming full form round-trip |
| `ChqWin.cs` | Save button calls `ShowPersistenceUnavailable()` | **Blocked**: cheque batch and cheque rows are not persisted | Implement batch/row persistence and audit lifecycle |
| `InvoiceWin.cs` | `InvRepo.SaveDraft/Load/Post/ReversePostedInvoice` | Repository path exists | Verify all header/line fields and transactional stock/accounting effects by round-trip |
| `PosCheckoutWin.cs` | Saves and posts through `InvRepo` | Repository path exists | Verify payment/tender fields, change due, customer, and every line survive reload |
| `MatCard.xaml.cs` | `MatRepo.LoadById/Save/SoftDelete` | Repository path exists | Compare all controls to `MatRec` mappings, especially units, barcodes, prices, suppliers and optional Arabic names |
| `WhsWin.xaml.cs` | `MatRepo.LoadWarehouses/SaveWarehouse/SoftDeleteWarehouse` | Repository path exists | Verify parent ID, code/name, active state, update and soft-delete persistence |
| `ComWin.xaml.cs` | Company store and configuration save/load paths | Mixed storage: company registry and local configuration are distinct | Verify which fields belong to the company database vs local settings; remove swallowed save errors and test provider-specific paths |
| `DatWin.xaml.cs` | Import/export options and provider-specific paths | Data movement workflow, not a business-record editor | Test import/upsert/replace modes, company matching, rollback, and accurate per-row failure reporting |
| `TrialBalanceWin.cs`, `AccStmtWin.cs`, `SalListWin.cs` | Async report data loaders | Report query path exists, but this scan alone does not prove data is live or correctly filtered | Test against seeded DB entries; verify company/date/account filters, totals and empty/error states |

### Fifth-pass conclusions

- Confirmed blocked data-entry forms include customer, bank, cost center, representative, currency, cash/payment voucher, cheque batch, order and manufacturing forms. `JrnWin` was removed from the blocked list after adding database save/post wiring and a repository round-trip test. These should remain explicitly unavailable rather than claim a save succeeded until real repository operations are wired.
- `AccServ` currently contains an in-memory `Dictionary<Guid, JournalEntry>`; it is not durable database persistence. It must not be used as proof that journal entries survive application restart.
- A repository call is only a starting point: report correctness and all-field persistence still require database-backed tests. No field is marked PASS without a create/save, reload, and value comparison.



## Sixth-pass correction (2026-10-16): journal persistence wired

- `JrnWin.cs` now resolves the active company code to the database company UUID, resolves account codes against active/postable company accounts, saves the journal header and all debit/credit lines through `JournalService`, and posts only after database validation. Exceptions are logged to the application log and displayed as failures, not success messages.
- The account-name grid field is now read-only and populated from the database lookup, so users cannot enter an account name that is silently ignored.
- New migrations persist the journal header's external reference, currency, and exchange rate in both SQLite (`012_journal_header_fields.sql`) and PostgreSQL (`014_journal_header_fields.sql`). The document number is persisted by the preceding provider-specific migrations. Existing records receive safe defaults for currency and exchange rate.
- Tests added: an end-to-end SQLite database test applies DBA migrations, resolves company/account codes, creates a fiscal year and accounts, saves a journal, verifies reference/currency/rate/document number and line memo/cost centers, posts it, reverses it, and checks that reversal retains the reference/currency/rate/cost centers while swapping debit/credit. CI also checks required journal-header columns after clean migration and idempotent re-run.
- **Not yet certified:** the WPF window does not yet provide a database-backed open/reload flow for a previously saved journal, and live CI results must be green before calling the change verified. PostgreSQL is schema-checked in migration CI; the full journal repository round-trip currently targets SQLite.
