# Visual Pass — reduce branch (Windows 11 identity, Phenix/Sahli models, Explorer shell)

Owner directives (2026-11-24): windows must resemble Windows 11 system
windows (File Explorer / Settings); invoices + vouchers modelled on
PHENIX ERP v10; reports modelled on SAHLI SOFT; MWin shell converted to
the Explorer-style command bar + navigation pane. All changes go through
the existing central files only (Themes/VisuId.xaml, App.xaml,
Views/BaseWin.cs, Services/VisuId.cs, Services/Win11Chrome.cs) plus the
in-scope windows themselves. Visual/rendering verification is
**Windows-owed**: this document records design decisions and code
changes, never a claim that the rendered look was verified here.

## V1 — Central Windows 11 pass (commit: visual V1)

State found: the central system was already token-based and largely
Win11-shaped — DWM rounded corners + Mica (Win11Chrome), 32px slim
caption with 46px caption buttons, 4/8px radius scale, Segoe UI
Variable, red close-hover (#C42B1C, the exact WinUI value). The gaps
were identity values left from the pre-Win11 slate theme:

1. **Accent** — the palettes were Material Design colors and the
   default accent was slate gray (#607D8B). Added a `win11` palette
   (Primary #0067C0, Dark #005A9E, Light #E5F1FB, Hover #D6EBF9 —
   the Windows 11 default accent family) as the lead palette in
   Services/VisuId.cs, and made it the default/fallback accent
   everywhere in that file. The XAML accent tokens in
   Themes/VisuId.xaml carry the same values so first paint matches.
   A user-saved accent choice is untouched (a saved "gray" still
   applies). **OWNER DECISION (2026-11-24 13:59): the
   `Configuration/UsrPref.cs` default `AccentColor` STAYS `"gray"` —
   do NOT flip it to `"win11"`.** A profile with no stored choice
   keeps reading "gray"; the `win11` palette remains the applied /
   fallback styling inside Services/VisuId.cs and the XAML tokens
   exactly as this pass left it. This question is CLOSED: no later
   step on this branch (or a future one) should "fix" the default.
2. **Title bar** — was slate (#607D8B) with white text, the pre-Win11
   identity. Now the bar takes the client surface color
   (ChromeBackgroundBrush = Neutral100, the window BackgroundBrush)
   with Neutral900 text (ChromeForegroundBrush), the File Explorer /
   Settings arrangement; the hairline under the bar and around the
   window is Neutral300 (ChromeBorderBrush). Caption-button hover /
   pressed are neutral subtle fills (#E8EAED / #D5D9DE); the close
   button keeps its WinUI red hover. MWin.xaml consumes the same two
   chrome tokens in paired background/foreground, so the shell stays
   coherent until its own conversion phase.
3. **Interaction fills** — hover/pressed surfaces lightened one
   neutral step (SurfaceHoverBrush 200→150, SurfacePressedBrush
   250→200) toward Win11's subtle-fill values; selection/subtle
   accent brushes re-hued to the Win11 accent at the same alpha.
4. **Backdrop layering** — Win11Chrome's documented intent ("Mica Alt
   suits dense ERP dialogs") was never wired. BaseWin now applies it
   on load: the shell (ownerless) keeps the token backdrop (Mica);
   every owned secondary window gets Mica Alt, the transient-window
   material Windows 11 uses for dialogs. Best-effort, no-ops on
   older DWM.
5. **Grid hairlines** — TableHeaderBorderBrush slate → Neutral300;
   the opening-stock header tint re-hued to the accent dark (#005A9E);
   PrimaryTransparentBrush re-hued at the same alpha.
6. **App.xaml** — its app-level chrome button style now points at the
   central ChromeButtonHover/Pressed tokens instead of ribbon/border
   brushes, so every chrome surface shares one state palette.

No resource keys were removed or renamed; no window file was touched
in V1. RTL, shell-context inheritance, the 50% opening cap and the
bottom-button convention are unaffected (token values only).

## V1 addendum — default accent becomes BLUE-GRAY (slate) (2026-11-24 14:01)

**OWNER DECISION (2026-11-24 14:01): the default accent is
BLUE-GRAY (رمادي أزرق, slate) — this SUPERSEDES the 13:59 "keep
gray" decision recorded above, and supersedes the Win11 blue as
the default too.** The 13:59 decision is closed history: it
answered a different question (whether to flip the stored default
to the `win11` palette); the owner then directed slate instead.

What changed:

1. **New `slate` palette** in Services/VisuId.cs, lead entry:
   Primary #51708E, Dark #3D576E, Light #E8EEF3, Hover #D3DEE8,
   display name "Blue Gray". It is now the service default
   (`_accentKey`), the unknown-key fallback in `SetAccent`, the
   empty/invalid fallback in `ApplySavedAccent`, and the
   `ApplyAccentInternal` palette fallback. The `win11` and `gray`
   palettes remain available choices, unchanged.
2. **UsrPref**: `AccentColor` default is now `"slate"`; the
   normalizer accepts `"slate"` and `"win11"` as valid stored
   values (win11 was pickable in OptWin but previously normalized
   away) and falls back to `"slate"` on empty/invalid. A profile
   with an explicitly stored choice (including "gray") keeps it —
   only never-stored profiles read the new default.
3. **Themes/VisuId.xaml**: first-paint accent tokens carry the
   slate family; the accent washes (selection/subtle/primary
   transparent) re-hued to #51708E at the same alphas; the
   opening-stock table-header tint (re-hued to the accent dark in
   V1) follows to the slate dark #3D576E. The **neutral ramp
   itself is re-hued** with a muted blue cast at unchanged
   luminance steps, so window surfaces read blue-gray, not plain
   gray; ShellMenuForegroundBrush follows Neutral900.
4. **Pre-shell windows**: LogWin and StpWiz hardcoded the old
   #607D8B caption color via DWM; both now use the slate primary
   #51708E (white caption text unchanged).
5. **OptWin**: the accent combo's empty-value fallback is "slate";
   the picker lists the new palette from VisuId.AvailableAccents.

## V4 — Window content pass (owner directive 2026-11-24 14:01)

Improve window CONTENT — fields, buttons, texts — presentation and
wording only; no functionality, binding or persistence changes.
Method: audit first, fix what deviates from the proportions
standard (docs/WINDOW_PROPORTIONS.md) and the central form-label
contract.

**Audit results (what was already standard):** BaseWin form helpers
share one margin set (FormFieldMargin / FormLabelMargin); command
buttons across the windows are built with the ButtonMinWidth /
PrimaryButtonMinWidth tokens; the central label styles implement
the field-adjacent alignment contract with live FlowDirection
triggers; XAML forms pair design-time Content with LocProps keys.
Windows checked and found localized at runtime despite English
design-time strings: BinWin, BcdDesWin, AccCard.

**Deviations found and fixed:**

1. **WhsWin** defined a local `VisuFieldLabelStyle` — a partial
   copy of the central label style (secondary color, no alignment
   triggers, no trimming), exactly the local-copy pattern the
   label contract forbids. Removed; all 13 usages now take the
   central `VisuTextBlockLabelStyle`.
2. **CatWin** (the owner's compact card) duplicated the alignment
   triggers in its local `FieldLabel`. The style is now BasedOn
   the central `VisuTextBlockLabelStyle` and keeps only the card's
   compact deltas (FontSize11, SemiBold, secondary foreground,
   3px bottom margin). The card's compact 28px field height is
   the owner's own layout choice for this card and is unchanged.
3. **CashDrawerOptionsWin** was English-only below the title (its
   own comment admitted the keys were missing): heading, hint,
   buttons and status texts were hardcoded, and the status line
   sat BELOW the button row. All strings now resolve through the
   Lang catalogs — new POS.CashDrawer* keys in all 4 languages,
   Save/Close via Common.* — the drawer service's three
   user-facing messages are localized the same way (Loc.Get with
   cached CompositeFormat patterns), and the status line moved
   above the buttons so the action row is the window's bottom
   element, per the bottom-button rule.

**Not changed, deliberately:** StpWiz's WizardLabelStyle (a wizard
idiom built on the central margin tokens, side labels — not a form
label copy); WhsWin's 24px code boxes (SmallControlHeight, the
sanctioned dense-toolbar variant); BaseWin's 26px grid-cell
steppers (in-cell controls, not form fields); PosWin (touch
surface exception in the proportions standard).

## V2 — Phenix v10 invoices/vouchers + Sahli Soft reports

### Research (public sources, 2026-11-24)

Phenix: vendor site (phenixsoft.com) and the official Phenix channel
(feature posts for v9.3/v10-era builds). Sahli Soft: official site
(sahlisoft-sa.com, accounting + payroll pages) and Syrian market
coverage. Evidence strength, stated plainly: Sahli's report traits are
documented in the vendor's own feature text; Phenix's *behavioral*
traits are documented (below), while pixel-level layout detail comes
from the vendor's published screens/videos — the comparison therefore
works at the level of structure, field grouping and actions, which is
also the level this phase changes.

### Phenix document windows vs ShouTech (InvoiceWin, CshWin, JrnWin, PayRecWin, ChqWin)

| Trait (Phenix) | Evidence | ShouTech state | Action |
|---|---|---|---|
| Header band (pattern, no, date, party, warehouse, currency) → lines grid → totals strip → bottom command bar | Phenix screens/videos; classic Phenix layout | Same structure in all five windows (header band / grid / totals footer / bottom action bar); proportions already Phenix-modelled (WINDOW_PROPORTIONS: ControlHeight 32, FormLabelWidth 120) | None — already aligned |
| Totals strip with the NET set apart (semibold, emphasized) | Phenix totals band presentation | InvoiceWin footer totals were nine uniform fields; ChqWin total already bold; JrnWin already has a live balance badge (green Balanced / red Difference) | Added central `VisuGrandTotalStyle`; applied to InvoiceWin's Net field |
| One receipt voucher settles several invoices with partial amounts; statement shows one consolidated entry per voucher | Official channel walkthrough (receipt #2 settling invoices 12–15) | PayRecWin allocation does exactly this (sales + purchase cycles) | None — already aligned |
| Definable invoice patterns (أنماط) drive which columns/fields show (e.g. source/target warehouse columns on transfer invoices) | Channel posts on transfer invoices + invoice definitions | Document kinds are fixed per window; per-pattern column sets are a definition-layer feature, not presentation | Noted as a definition-layer gap, NOT built in a visual phase |
| Right-click a line → item overview (لمحة عن المادة, Alt+S) with period charts | Channel post | No line context overview in InvoiceWin | Noted as a feature gap, NOT built in a visual phase |
| Secondary actions grouped under an "Operations" (عمليات) button | Channel posts (return-from-invoice flow) | All actions sit on the bottom bar / secondary row, one click each | Considered and deferred: regrouping adds a click to every secondary action; every action is already reachable, which the owner's reconciliation rule prioritizes |
| Options under Tools → Options, per document family | Channel posts | Settings live in PrmWin/UsrPref; the Al-Ameen-style Options window is scoped in the services phase of this branch | Deferred to services phase (by the owner's own scoping) |

### Sahli Soft reports vs ShouTech RptBase print path

Sahli's documented report traits (vendor text): reports cover accounts
and materials at high speed; **the user controls the report's form and
content design to match the company's printouts**; **multiple print
styles for the same report**; formula cells in designed printables;
detailed customer statements; closing reports (trading, P&L, balance
sheet, inventory) with several inventory valuation methods.

| Sahli trait | ShouTech state | Action |
|---|---|---|
| Printed structure: company header, report title, filter/parameter line, table with totals, page footer | RptRend already renders exactly this: company name, 20pt title, FilterSummary line, generated-at/by meta, accent rule, zebra rows, totals band on the last page, "Page X / Y" footer | None — already aligned |
| Totals band visually emphasized | Present but pinned to a legacy Office-blue tint (#1F4E79) unrelated to the theme | Totals fill now derives from the resolved app accent (same alpha); the renderer accent fallback aligned to the Win11 accent |
| Multiple print styles per report + user-designed report forms with formula cells | Not present as a user feature | Belongs to the Report Designer build in this branch's services phase (owner-scoped there, with cell-level formulas); NOT improvised here |

### V2 changes (commits: visual V2)

1. Themes/VisuId.xaml: new `VisuGrandTotalStyle` (BasedOn
   VisuTextBoxStyle; strong font, FontSize14, AccentLight background,
   AccentDark foreground).
2. InvoiceWin: Net footer field takes VisuGrandTotalStyle.
3. RptRend: print totals band + accent fallback follow the app accent.

All bindings, fields, persistence and actions in the five windows are
untouched; every existing report and filter works as before (gates
below in the phase record).

## V3 — MWin Explorer-style shell conversion

Authorized by the owner 2026-11-24. Implemented as a new presentation
of the SAME navigation map — not a rewrite of navigation:

**What the shell was.** The "ribbon" was already a transient strip:
the menu bar selects a module, exactly one ribbon tab showed, and any
button click collapsed it again (ShowRibbonTab / CollapseRibbon).
Stack: title bar → menu bar → ribbon strip (row 2) → quick access bar
→ content → docked-window strip → status bar.

**What it is now.**
- **Navigation pane** (content row, leading column, 236px,
  collapsible from the command bar): one section per ribbon tab
  (18 tabs / 63 groups / **264 ribbon entries**), groups as subheads,
  in the ribbon's own tab/group order — the user's mental map is
  unchanged.
- **Command bar** (row 2, where the strip was): pane toggle (MDL2
  hamburger), the active module's title, and that module's commands
  as slim buttons with group separators. Menu module clicks
  (ShowRibbonTab) now expand the module's pane section + fill the
  command bar; clicking the same module again clears the command bar
  (the strip's toggle behavior). Any action click clears the command
  bar, as the strip used to hide (CollapseRibbon).
- Stack order preserved: menu bar → command bar → quick access bar →
  content; the quick bar is untouched and routes through the same
  keys as before.

**Why navigation cannot regress.** The pane and command bar are
*generated at startup from the ribbon map itself* (the tabs/groups/
buttons declared in MWin.xaml, walked over the logical tree, so
nested buttons are included). Every generated item invokes
`OnRibbonButtonClick` with the ORIGINAL RibbonButton as sender — the
handler's ReferenceEquals/LocProps routing is literally the same code
path. Items carry the source button's LocProps key/tooltip key (so
UiLoczr localizes them exactly like the ribbon's), and mirror the
source's IsEnabled/Visibility through bindings. The ribbon control
stays in the tree, permanently collapsed, so all shell state code
(freeze/unfreeze, GetVisualDescendants sweeps, FindName) keeps
operating on the same objects:
- Freeze (pre-login / company switch): tabs/buttons are disabled
  exactly as before (only File > Open/Exit enabled); the pane mirrors
  that state through the bindings and is explicitly kept enabled as
  the pre-login surface with the File section expanded — the role the
  strip played. The one repointed line: freeze no longer sets the
  strip Visible.
- Section headers localize through the same Menu.* keys UiLoczr uses
  for tab headers; the pane additionally covers TabHR → Menu.HR,
  which the strip's table missed (the key exists in the catalogs).
- RTL: the pane is column 0 of the root grid; WPF mirrors grid
  columns under RightToLeft flow, and ApplyUiDirection sets the
  pane/command bar direction with the other shell bands — the pane
  sits on the right in Arabic with no special-case code.

**Kept intact:** menu bar, quick access bar, docked-window strip,
status bar, recycle-bin layer, shell-context propagation, window
chrome. RibModMap / RibbonNav are untouched.

**Windows-owed:** the pane/command bar rendering, the pre-login
freeze flow through the pane, and RTL mirroring must be confirmed in
a Windows run; headless gates cannot see them.

## V5 — Bottom-buttons audit (owner rule restated 2026-11-24 14:29)

The owner restated the standing rule as an absolute: ALL window
action/command buttons live in the window's bottom command bar,
exclusively (حصراً) — no in-window top toolbars carrying action
buttons. Full audit of the window set on this branch (every
Views/*.cs and *.xaml window, code-built and XAML, plus RptBase and
the Reports subfolder):

**Moved into the bottom bar (placement only; handlers untouched):**
- **PurchaseAttachmentsWin** — its whole command bar (Upload /
  Open / Delete / Refresh / Close) was docked TOP; now docked
  Bottom.
- **BkpWin** — Open folder + Refresh sat in the header row and
  Create backup now inside the create panel; all three moved into
  the Row-3 footer bar (order: Create / Open folder / Refresh /
  Restore selected / Close; the note field stays in its panel).
- **InvDesWin** (built in B3) — the columns editor's Up / Down /
  Add / Remove buttons sat in a panel-top strip; folded into the
  window's bottom bar.

**Reviewed and compliant (no move needed):** the overwhelming
majority already follow the rule — F2Ui.BottomBar / bottom-docked
bars / last-row bars (verified per window, including InvoiceWin's
row-3 actionBar, RptBase's row-5 actionBar under the status line,
DatWin's explicitly-commented bottom bar, CatWin/WhsWin/AccCard
bottom borders, OptWin, CustomReportsWin, MaintenanceWin, the POS
dialogs, StpWiz/Lang/LogWin/SerWin dialogs).

**Reviewed and kept, with reasons (not window command bars):**
- Record navigators at the top (InvoiceWin recordBar, WhsWin /
  AccCard / MatCard first-prev-next-last arrows): data-navigation
  controls for the record being edited, not commands.
- Filter/search bars whose Search/Refresh sits beside its own
  inputs (InstallmentsWin, PartySearchWin, TaxReportWin,
  MatDirWin, HrAttendanceWin, BankRecWin, ChqListWin, CommWin):
  filter controls; the windows' commands are in their bottom bars.
- Section-scoped buttons inside master-detail windows (CRM / HR /
  QC section headers, CrmOppWin link bar, RmaWin load-source,
  MfgPlanWin feasibility check, OrdWin invite/decline, ColChsWin
  reorder arrows, MatGroupWin select/clear-visible, CatWin image
  pick/clear, DatWin browse "…" and mode cards): each acts on its
  own section, grid or field; the window command set is in the
  bottom bar.
- CoaWin's menu is a right-click context menu on the tree, not a
  toolbar; its commands are also in the bottom bar.
- MWin shell chrome (menu bar, ribbon, command bar, quick access
  bar, nav pane): shell, out of scope per the directive.

**Owner exception (Man, 2026-11-24 14:29): ComWin** — the
company-opening window is EXEMPT from this audit; its button
placement is left exactly as it is.

**Flagged, not restructured:** PosWin (and PosCheckoutWin) keep
the POS touch layout, per the directive.
