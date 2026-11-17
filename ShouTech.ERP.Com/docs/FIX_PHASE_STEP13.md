# FIX PHASE — STEP 13: Visual leftovers (COA localization, accented-primary convention, schedule tints, shell font token)

**Branch:** `fix/step13-visual` (worktree `~/workspace/projects/ShouTech-fix13`), based on `fix/step12-screenfit` @ `08e0498` (chain carries steps 1–12). **NOT pushed / NOT merged** — awaiting owner approval.
**Scope source:** `research/acceptance-sweep-report.md` §summary step 13 + `research/sweep/visual-*.md` (12 files) + `research/sweep/journal-coa.md` C-4/C-5.
**Excluded by scope discipline:** the parallel central branch merged into main separately (central label contract in `VisuId.xaml`/`BaseWin.AddFormField`, the 50% opening cap, ownership/z-order) — checked against main @ `82615e7`; none of it is redone here. This step changes **text sources and style claims only**: no behavior, data, posting, or schema change anywhere (diff evidence in §7).

**Windows visual verification is OWED on Windows** — everything below is code/XAML-traced and programmatically proven; no claim here says the running UI was seen.

---

## 1. Item-by-item closure vs the sweep

| # | Sweep item (source) | Closure |
|---|---|---|
| 13-1 | **C-4/C-5 — CoaWin + AccCard localization-dead** (empty `ApplyLocalizedText()` stubs, 59 placeholder literals rendering as UI text, ~150 hardcoded literals, combo values doubling as display text) — `journal-coa.md`, `visual-journal-coa.md` worst #1/#2 | **CLOSED** — §2. Both windows fully wired into the central localization system (LocProps + `T()`/`Tf()`), live relocalization implemented, placeholders = 0, combo display separated from stored canonical values. |
| 13-2 | **No primary-button distinction anywhere** (Save/Post identical to Close in nearly every window; DatWin's accent convention never generalized) — every `visual-*.md`, e.g. visual-cash #3, visual-cheques #4, visual-parties #2, visual-mfg-hr-crm #5, visual-orders #3 | **CLOSED centrally** — §3. `BaseWin.ApplyPrimaryButtonConvention` accents the one primary action (Post > Save, by localization key) in every BaseWin window; windows that already accent their own primary are respected untouched. Residual non-Save/Post primaries flagged in §8 (F-3). |
| 13-3 | **Missing schedule tints** — named: CloseCashWin (visual-cash #4), ChqListWin (visual-cheques #2), PosWin + DocSearchWin (visual-invoicing #8), MatGroupWin (visual-materials #3), MfgListWin + DefManagerWin (visual-mfg-hr-crm #3), OrderTrackWin + DocSearchWin + DeliveryDeskWin (visual-orders #2/#3), PartyManagerWin + PartySearchWin (visual-parties #1), CustomReportsWin + FinancialAnalysisWin (visual-report-family §2), ActivityLogWin (visual-shell-admin §12 note), BinWin (visual-shell-admin §BinWin), StockCountDialog (visual-stock-reports #3) | **CLOSED** — §4. All 16 named windows claimed a unique tint; the palette was extended (16 → 43 tokens) because uniqueness is arithmetically impossible inside the old palette (15 used + Peach spare). The same standing rule was then completed for the remaining grid windows the audits did not name (12 more claims) so the proof in §7 holds app-wide. |
| 13-4 | **Tahoma shell font token** (`VisuId.xaml:273` `ShellChromeFontFamily` = Tahoma; MWin menu style hardcodes Tahoma at `MWin.xaml:153`) — acceptance report §13/visual-shell-admin | **CLOSED** — §5. Token now resolves to the central Segoe UI family in both dictionaries that define it (`VisuId.xaml`, `ShMenu.xaml`); MWin's literal setter routes through the token. Zero Tahoma remains in the shell/themes. Side effect, intended: BinWin/DatWin, which set their window font from this token, now render in Segoe UI like every other window (the audit's G-2 complaint). |
| 13-5 | Other small/mechanical leftovers in the same class (§5 sweep) | **CLOSED (small):** MWin dead "Option 1/2/3" RadioPanel scaffold + its orphaned style removed (visual-shell-admin); AccCard Close button literal `MinWidth="62"`/`Padding="8,4"` → `ButtonMinWidth` token (visual-journal-coa Axis 2 note); CoaWin password dialog rebuilt beside-field on the central form grid with token sizes (visual-journal-coa Axes 1+3 #6) as part of its localization; `F2WinUi.ParseDecimal` hardcoded `"Invalid number format."` → `Common.InvalidNumberFormat` ×4 (visual-cash, CloseCashWin shared-helper note). **Larger items listed as GAPs in §8, not built.** |

---

## 2. COA localization (C-4/C-5) — what was done

### 2.1 Mechanism (the app's own central system, no parallel path)
- **XAML:** every static caption in `CoaWin.xaml` / `AccCard.xaml` now carries `loc:LocProps.Key` / `loc:LocProps.ToolTipKey` and the windows carry `loc:LocProps.WindowKey` (`CoaWin.Title` / `AccountCard`) — the exact CatWin/MatCard pattern, resolved by `UiLoczr` on open and on every live language switch.
- **Code-behind:** both `ApplyLocalizedText()` stubs are now real implementations: they run the `UiLoczr` walk (errors reported via `AppHost.Errors.Report` — no silent catch), then refresh the dynamic surfaces the walk cannot reach:
  - CoaWin: status readouts (`TxtCount`, `TxtSelected` via `Tf` format keys), the tree (rebind so node display/tooltips re-resolve), and the **popup context menu** — a `ContextMenu` is not in the window's logical tree, so `LocalizeContextMenu()` applies its LocProps keys explicitly (its FlowDirection sync, pre-existing, is kept).
  - AccCard: the four option combos are rebuilt in the new language (see 2.2) and the risk indicator is refreshed.
- **Formatted messages** go through the new central `BaseWin.Tf(key, args)` helper (cached `CompositeFormat` + `CultureInfo.CurrentCulture` — the BinWin/BcdGenWin CA1863 pattern, held centrally so every window formats one way). This was required: plain `string.Format(T(...))` trips CA1305/CA1863 and the gate is 0 warnings.
- **All user-facing literals** in both code-behinds were replaced: load/export/import dialogs (titles + file-filter display text), success/failure messages, the `.sta` import validation exceptions (they surface verbatim in message boxes), the PDF print heading/date/job name, the password dialog, and every AccCard validation/lifecycle message box (16 sites). The dead, caller-less `CoaWin.ShowAction` (last two literals) was removed.
- **AccountNode:** `TypeLabel` stays canonical (`Group`/`Account`) because it is the export file's Type column; the tree now binds the new localized `TypeDisplay`, and `ToolTipText` is composed from `CoaWin.NodeTypeLine` / `NodeChildren` / `NodePostable` keys via `AppHost.Localization`.

### 2.2 Combo values ≠ display text (AccCard)
The four combos (Status, Risk, PostingMode, CostCenter) previously held raw English strings that were simultaneously display text **and** the stored/compared values. They now hold an `AccOption { Value, Display }`: `Value` is the canonical token the schema has always stored (`Active`, `Low`, `Postable`, `Unspecified`, …), `Display` is `T()` of the matching `AccCard.*` key, rendered via `ToString()`. All reads go through `SelectedValue()`, all writes through `SelectValue()` — **stored data is byte-identical to before in every language**; only the rendering localizes. The parent combo still lists `"code - name"` data strings (account data, not UI text) and its code-prefix parsing is unchanged (see flag F-1, defect C-6).

### 2.3 Key inventory
**145 new keys, added to all four catalogs (En/Ar/Fr/Tr), parity 0 missing / 0 new extras:**

| Section | Keys | Covers |
|---|---|---|
| `CoaWin.*` | 87 | title/tree header/search, 13 context-menu items + 13 tooltips, export/import menus + dialogs + filters, success/failure messages, password dialog, print, node type/tooltip lines, 14 `.sta`/import validation messages, selection/count formats |
| `AccCard.*` | 57 | 19 field/balance captions, nav + serial tooltips, Disable/Statement/Ledger, 10 combo option displays, 9 message-box titles, 14 validation/lifecycle/save messages |
| `Common.InvalidNumberFormat` | 1 | shared F2 helper exception (item 13-5) |
| **Total** | **145** | every one is referenced from code/XAML (programmatic check, §7) |

Reused existing keys where they already existed: `Common.Close/Print/Refresh/OK/Cancel/New/Save/Edit/Delete/First/Prev/Next/Last`, top-level `AccountCard` (window title).

### 2.4 Justified remaining literals in the two code-behinds (explicit list)
- Canonical **stored tokens** compared/persisted, never displayed raw: `"Summary"/"Postable"/"Group"/"Account"/"Active"/"Low"/"Unspecified"` etc. (display goes through §2.2 / `TypeDisplay`).
- **File-format internals:** XLSX package XML, the CSV header row (`Code,ParentCode,Name,NameEn,Type` — the import format itself), `.sta` magic/format strings, default export file names.
- **Format/product names** in menus: `Excel`, `CSV`, `PDF` (identical in all four languages' catalogs elsewhere in the app).
- `"ShouTech ERP"` message-box title (brand name, as in the rest of the app).
- **Seed chart account names** (`SeedDefaultChart`) — these are *database seed data*, not UI text; see flag F-2 (defect C-8).

---

## 3. Accented-primary convention (item 13-2)

**Central rule, implemented once in `BaseWin.ApplyPrimaryButtonConvention()`** (called from `OnBaseLoaded`, after localization):
1. Walk the window's logical tree for `Button`s; read each button's localization key (`LocProps.GetKey`, else the `Tag` the code-built `StyleButton` helpers store the key in).
2. If any button's style chain already contains `VisuAccentButtonStyle`/`PrimaryButtonStyle`, the window has chosen its own primary (DatWin Start, WhsWin Post, MatGroupWin Save, PosCheckoutWin Complete, …) — **do nothing**.
3. Otherwise accent the first **Post** button (key's last segment `Post`: `Invoice.Post`, `OrderDoc.Post`, `Vouchers.Post`), else the first **Save** button (`Common.Save`, `Buttons.Save`, `CloseCash.Save`, …): apply `VisuAccentButtonStyle` via `SetResourceReference` and raise `MinWidth` to `VisuId.PrimaryButtonMinWidth` (100) if below it. At most one button per window is accented.
4. All failures are reported through `AppHost.Errors.Report` (no silent catch).

This closes the audit finding for the document/card family the audits sampled (JrnWin, CshWin, OrdWin, InvoiceWin, ChqWin, BankRecWin, PayRecWin via Save, AccCard, ProjWin, AssetWin, MfgWin, the F2 family, the master cards, …) without a single per-window style edit. Windows whose culminating action is neither Save nor Post are **not** force-accented by guesswork — they are flagged (F-3).

---

## 4. Schedule tints (item 13-3)

Palette: `Themes/VisuId.xaml` grew from 16 → **43 `ScheduleTint*Brush` tokens**, all in the same light pastel band as the originals (hover/selection states stay legible). Peach (the old spare) is now used by PosWin.

**New claims this step (28):**

| Window | Tint | Source |
|---|---|---|
| PosWin | Peach | audit-named (visual-invoicing #8) |
| CloseCashWin | Coral | audit-named (visual-cash #4) |
| ChqListWin | Indigo | audit-named (visual-cheques #2) |
| DocSearchWin | Denim | audit-named (visual-invoicing #8, visual-orders #2) |
| MatGroupWin | Honey | audit-named (visual-materials #3) |
| MfgListWin | Fern | audit-named (visual-mfg-hr-crm #3) |
| DefManagerWin | Jade | audit-named (visual-mfg-hr-crm #3) |
| OrderTrackWin | Cyan | audit-named (visual-orders #2) |
| DeliveryDeskWin | Lime | audit-named (visual-orders #2) |
| PartyManagerWin | Blush | audit-named (visual-parties #1) |
| PartySearchWin | Mauve | audit-named (visual-parties #1) |
| CustomReportsWin | Violet | audit-named (visual-report-family §2.1) |
| FinancialAnalysisWin | Plum | audit-named (visual-report-family §2.2) |
| ActivityLogWin | Olive | audit-named (visual-shell-admin §12) |
| BinWin | Bronze | audit-named (visual-shell-admin, BinWin) |
| StockCountDialog (in WhsWin.xaml.cs) | Sage | audit-named (visual-stock-reports #3) |
| AddonsWin | Wheat | rule completion (grid, unnamed by audits) |
| BcdGenWin | Lagoon | rule completion |
| CcListWin | Orchid | rule completion |
| CurCard | Peridot | rule completion |
| GroupManagerWin | Topaz | rule completion |
| InstallmentsWin | Quartz | rule completion |
| LoyaltyWin | Garnet | rule completion |
| PosSyncWin | Cobalt | rule completion |
| RolloverWin | Cedar | rule completion |
| SerWin | Onyx | rule completion |
| SupportWin | Ember | rule completion |
| MatMergeWin (in WhsWin.xaml.cs) | Willow | rule completion |

**Pre-existing claims (15, unchanged):** JrnWin Blue · OrdWin Sky · ProjWin Aqua · CshWin Teal · InvoiceWin Mint · MfgWin Green · MatCard Amber · MatDirWin Sand · CstCard Rose · CoaWin Lavender · PayRecWin Lilac · ChqWin Periwinkle · BankRecWin Slate · AssetWin Stone · RptBase Silver (whole report family — deliberate documented exception, flag F-4) · (Peach was spare).

Uniqueness: every tint name is claimed by exactly one window class (the one apparent exception in raw file grep — "Sage" inside `WhsWin.xaml.cs` — is the nested `StockCountDialog` class span; the WhsWin card itself claims nothing and has no schedule). Three claims are inert-by-design and pre-date this step: CoaWin (tree, no DataGrid), CstCard and MatDirWin (no DataGrid surface for the tint to reach — MatDirWin's is a ListView; converting it is a behavior-adjacent rebuild, flag F-5).

---

## 5. Shell font token + small leftovers (items 13-4 / 13-5)

- `ShellChromeFontFamily`: `Tahoma` → `Segoe UI Variable Text, Segoe UI` (identical value to the central `ApplicationFontFamily` token) in **both** dictionaries defining it: `Themes/VisuId.xaml` and `Themes/ShMenu.xaml`. Consumers (MWin, BinWin, DatWin window fonts; `App.xaml` chrome styles; `ShRibn.xaml` ribbon styles) all pick it up through the token — no consumer was edited.
- `MWin.xaml`: the menu style's literal `Value="Tahoma"` → `{DynamicResource ShellChromeFontFamily}`; dead RadioPanel (`Option 1/2/3`, shown only when minimized, no code references) and its orphaned `RadioPanelVisibility` style removed.
- Remaining `Tahoma` strings in the repo are **not** the shell: `Configuration/AppConfig.cs` `SecondaryFont`/`ArabicFont` defaults (user-configurable report/print font settings — changing a stored-config default is a data decision, flag F-6) and `BcdDesWin`'s font-picker list (Tahoma offered as one selectable font among eight — correct as-is).

---

## 6. Comparison tables — CoaWin / AccCard (comparison gate)

Source: `research/sweep/compare-journal-coa.md` §2/§3 (grades unchanged by this step — it closes the **localization row**, which the comparison file recorded as defect C-4/C-5 in `journal-coa.md`; all functional rows stand exactly as graded there and in steps 1–12).

### 6.1 CoaWin — Chart of Accounts

| Capability | ShouTech CoaWin | Al-Ameen V10 ⚠ / Phenix v10 ⚠ | ERPNext | Odoo | SAP B1 |
|---|---|---|---|---|---|
| Tree / hierarchy display | ✓ tree, code + name | ✓ | ✓ tree | ✗ flat list + groups | ✓ tree by Drawer |
| Search / filter | ✓ code/name with ancestor paths | ✓ | ✓ + company filter | ✓ | ✓ |
| Add Child | ✓ (context menu → AccCard) | ✓ | ✓ | n/a | ✓ |
| Add Root | ✗ (flag F-7 / defect C-7) | ✓ | ✓ | ✓ | ✓ |
| Edit account | ✓ opens AccCard | ✓ | ✓ | ✓ | ✓ |
| Delete / Disable from manager | ◐ only inside AccCard | ✓ | ✓ | ✓ | ✓ |
| Import tree | ✓ CSV/XLSX/`.sta` upsert | ✓ ⚠ | ✓ Importer | ✓ CSV | ✓ DTW |
| Export / Print tree | ✓ XLSX/CSV/PDF/`.sta` | ✓ | ✓ | ✓ | ✓ |
| Account Type / Root Type per node | ✗ (defect family C-1/R-2, steps 1–3 scope) | ✓ ⚠ | ✓ Root Type + Account Type | ✓ Type | ✓ |
| Balance per node | ✗ | ✓ ⚠ | ✓ | ✓ | ✓ |
| Drill to Statement / Ledger | ◐ both open the one statement report | ✓ | ✓ | ✓ | ✓ |
| Move / reparent | ◐ via AccCard parent combo (flag F-1 / C-6) | ✓ ⚠ | ✓ | n/a | ✓ |
| Merge accounts | ✗ | ◐ ⚠ | ✓ | ◐ | ✗ |
| Template charts | ◐ one hard-coded seed (flag F-2 / C-8) | ✓ ⚠ | ✓ per-country | ✓ localization packages | ✓ |
| Status visibility in tree | ◐ Group/Account label yes; status not shown | ✓ | ✓ | ✓ | ✓ |
| Opening balances from CoA | ✗ | ✓ ⚠ | ✓ | ✓ | ✓ |
| Freeze account | ✗ | ◐ | ✓ | ✓ | ✓ |
| **Localization (4 languages, live switch)** | **✓ — CLOSED BY STEP 13** (was ✗: empty stub, 36 placeholder literals, menu items headed "Header") | ✓ | ✓ | ✓ | ✓ |

Grade (unchanged): **9.5 / 18 = 53%** CORE — localization was not a scored CORE row; the scored gaps (types, balances, root creation, merge, freeze, opening balances) are functional builds outside this step's mandate.

### 6.2 AccCard — Account Card

| AccCard field | ShouTech | Al-Ameen ⚠ | ERPNext `Account` | Odoo `account.account` | SAP B1 |
|---|---|---|---|---|---|
| Code / Account Number | ✓ digits-only, prefix = parent | ✓ | ✓ | ✓ Code | ✓ |
| Name (bilingual) | ✓ Name + English Name | ✓ | ◐ single name | ◐ single name | ◐ single name |
| Parent | ✓ combo (flag F-1 / C-6) | ✓ | ✓ Parent Account | ✓ via Account Group | ✓ Father Account |
| Is Group / postable | ✓ Posting Mode | ✓ | ✓ Is Group | ✓ | ✓ |
| Status | ✓ Active/Disabled | ✓ | ✓ Disabled | ✓ Deprecated | ✓ |
| Root Type | ✗ inferred from code prefix | ✓ | ✓ mandatory | ✓ derived | ✓ Drawer |
| Report Type | ✗ | ✓ | ✓ | ✓ derived | ✓ derived |
| Account Type | ✗ | ✓ | ✓ (~30) | ✓ Type (17) | ✓ |
| Currency per account | ◐ Foreign-Currency checkbox only | ✓ | ✓ Account Currency | ✓ Currency | ✓ |
| Opening balance | ✗ | ✓ via opening voucher | ✓ via Opening Entry | ✓ Opening Dr/Cr | ✓ |
| Balance Must Be | ✗ | ◐ | ✓ | ◐ | ◐ |
| Freeze | ✗ (Disable only) | ◐ | ✓ | ✓ Deprecated | ✓ Lock |
| Allow reconciliation | ✓ | ✓ | ✓ | ✓ | ✓ |
| Tax on the account | ◐ flag + rate, unconsumed on journal path | ✓ | ✓ | ✓ default Taxes | ✓ Tax Group |
| Cost-center behaviour | ◐ mode combo + checkbox, unenforced at posting | ✓ | ✓ | ✓ analytic rules | ✓ Distribution Rule |
| Default cost center value | ✗ | ✓ | ◐ | ✓ | ✓ |
| Default account flag | ✓ (ShouTech plus) | ◐ | ◐ company-level | ◐ | ◐ |
| Live Balance / Debit / Credit | ✗ in substance — cached counters (C-2, addressed by step-3 GL work, not this step) | ✓ | ✓ | ✓ | ✓ |
| Budget flag | ◐ checkbox; budget engine is Phase-B | ✓ ⚠ | ✓ separate documents | ✓ | ✓ |
| Risk level | ✓ (ShouTech-only) | ✗ | ✗ | ✗ | ✗ |
| Statement / Ledger buttons | ◐ both open the same statement (C-9) | ✓ distinct | ✓ | ✓ | ✓ |
| **Localization (4 languages, live switch)** | **✓ — CLOSED BY STEP 13** (was ✗: empty stub, 23 placeholder literals incl. 3 bottom-bar buttons captioned "Content", 16 hardcoded message boxes, combo text = stored values) | ✓ | ✓ | ✓ | ✓ |

Grade (unchanged): **11.0 / 22 = 50%** CORE — the missing classification spine (Root Type, Account Type, currency code, opening balance, freeze) is functional scope for a later step, not visual leftovers.

---

## 7. Proof & gates

| Check | Result |
|---|---|
| Full build `dotnet build ShouTech.sln --no-incremental -p:EnableWindowsTargeting=true` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** |
| Lang parity (`tools/verify_english_source.py` catalog section) | **missing = 0 in all 4 catalogs**; extras unchanged from base profile (Ar 90 / Fr 9 / Tr 9, all pre-existing); En 2458 → 2603 keys (+145) |
| Placeholder literals in CoaWin/AccCard XAML (`Text="Text"`, `Content="Content"`, `Header="Header"`, `ToolTip="ToolTip"`) | **0** (was 59) |
| `ApplyLocalizedText()` empty stubs in CoaWin/AccCard | **0** — both are real implementations (§2.1) |
| User-facing literals in CoaWin/AccCard code-behind | **0** beyond the justified list in §2.4 (verified by grep over MessageBox/dialog/menu/format sites) |
| Every new catalog key referenced | **145/145 used** (programmatic scan of both windows + F2WinUi + BaseWin) |
| Tint inventory (per-class brace-matched scan) | 43 claims, **every schedule-bearing window class has a tint, every tint unique** (RptBase family Silver = documented exception, F-4); all claimed names resolve to a `ScheduleTint*Brush` token |
| Tahoma in shell (`MWin.xaml[.cs]`, `Themes/`, `App.xaml`) | **0** |
| Behavior/data diff evidence | Diff touches only: XAML text/key attributes, the two windows' text-source code, `BaseWin` (additive: `Tf` + primary convention), theme tokens, one-line tint claims, catalog JSONs. No SQL, no repository, no posting, no schema file in the diff (`git diff --stat` vs `08e0498`) |
| WPF visual verification | **OWED on Windows** — placeholder removal, live relocalization, accent rendering, tint rendering, Segoe shell font all need a Windows run |

## 8. Owner flags / GAPs (not built — by mandate or by size)

- **F-1 (C-6, functional — do not fix here per mandate):** AccCard parent identity is still string-parsed (`SelectedParentCode()` splits the `"code - name"` combo text on `" -"`); CoaWin reparenting has no cycle check. Exposed by the C-4/C-5 work, untouched by it.
- **F-2 (C-8):** `SeedDefaultChart` writes English names into **both** name fields of the seed chart (data, not UI text). Localizing seed data per first-run language is a data decision for the owner.
- **F-3:** Primary actions that are neither Save nor Post get no central accent (e.g. DocSearchWin Search, PayRecWin Allocate — that window has no Save/Post at all, MfgListWin Open, PartySearchWin Select, BkpWin Restore). Accenting them needs a per-window primary declaration — proposed: an opt-in `LocProps`-style attached marker in a follow-up, not guesswork in the central rule.
- **F-4:** Report family still shares RptBase **Silver** (deliberate, documented family exception to the unique-tint rule) — owner confirmation that the exception stands is still open from the sweep.
- **F-5:** MatDirWin's schedule is a ListView, so its Sand tint (and now any tint) cannot render; CoaWin's Lavender is inert on a tree. Converting MatDirWin's list to the central DataGrid is a separate rebuild.
- **F-6:** `AppConfig.SecondaryFont/ArabicFont` default to `"Tahoma"` — user-facing *settings defaults* for report fonts, not the shell token. Changing stored-config defaults is the owner's call.
- **F-7 (C-7):** CoaWin's primary actions remain context-menu-only (Add Child; no root creation; Delete only inside AccCard) — visual-journal-coa Axis 2 FAIL-SCATTERED. Adding a bottom-bar New/Add Child button is a UI-structure change beyond this step's text-source mandate.
- **F-8:** CoaWin's local TreeViewItem template still bypasses `VisuTreeViewStyle`, and its title block still docks Right — identity items larger than this step's localization mandate.
- **F-9:** JrnWin's 17 error-path `InvalidOperationException` literals and the six remaining per-window `"Invalid number format."` copies (AssetWin, CstCard, CurCard, DeliveryDeskWin, ProjWin, RepCard) — exception-text localization beyond the shared F2 helper fixed here; the per-window copies are the known local-helper duplication class.
- **F-10:** Windows whose buttons are built in their own `Loaded` handlers (after BaseWin's) won't receive the central accent until a later pass — the convention applies at BaseWin load; a `OnLanguageChanged`-time re-application is a possible follow-up if a concrete window is found.

## 9. Pillars checklists (per touched window)

**CoaWin** — UI: **PROVEN** (all captions/menus/dialogs/print localized ×4, placeholders 0, live relocalization implemented) · DB: unchanged — text-source edits only (no AccRepo/SQL in diff) · Linking: unchanged (AccCard/statement navigation untouched) · Posting: n/a (no posting in this window; unchanged) · Persistence: unchanged (import/export file formats byte-identical; canonical Type tokens preserved, §2.2/§2.4) · Downstream: unchanged (exported CSV/XLSX/`.sta` payloads identical; only dialog chrome text localized).
**AccCard** — UI: **PROVEN** (as above; Close token fix; Save now centrally accented) · DB: unchanged (stored combo values byte-identical via AccOption.Value) · Linking: unchanged · Posting: n/a unchanged · Persistence: **PROVEN unchanged** — every save path reads `SelectedValue()` canonical tokens; defaults identical · Downstream: unchanged.
**BaseWin** — UI: additive central convention (§3) + `Tf` helper · all other pillars: unchanged (no data path touched).
**Tint-claim windows (28 files, one line each)** — UI: tint claim only · DB/linking/posting/persistence/downstream: unchanged — one-line override diff evidence per file.
**MWin** — UI: font token + dead scaffold removal · DB/linking/posting/persistence/downstream: unchanged (no code-behind change).
**F2WinUi** — UI: one exception message now catalog-sourced · everything else unchanged (same message text in English).
