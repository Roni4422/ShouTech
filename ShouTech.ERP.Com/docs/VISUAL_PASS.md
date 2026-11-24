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
   applies). **Flagged follow-up (outside this pass's file list):**
   `Configuration/UsrPref.cs` still defaults `AccentColor` to
   `"gray"`, so a profile that never stored a choice reads "gray"
   back; flipping that one default to `"win11"` is a one-line change
   awaiting the owner's word.
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

(Record appended when the phase runs.)
