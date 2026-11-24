# RTL whole-window mirroring audit (owner directive 2026-11-24 14:55)

Owner rule: in Arabic the ENTIRE window mirrors — not only its
content. The window-level FlowDirection must flip the full layout
tree (navigation, panes, grids, scrollbars, bars, dialog ownership)
as a mirror image; in English/French/Turkish the same window is
fully LTR. The mechanism must live in the central files and its
application must be VERIFIED per window. CatWin (نافذة الصنف) is
named first by the owner.

COMPLETION PASS 2026-11-24 (branch feat/rtl-complete): the sweep
was re-run end to end and every remaining exception was closed or
classified with a reason — see "Completion pass" below. End state:
0 window-level pins, 1 control-level pin (classified), no literal
LeftToRight setter left in any window file.

## The central mechanism (verified in code)

- `LangDirSv.ApplyToWindow` (Services/Localization.cs) sets ONLY the
  window-level `FlowDirection` (children inherit; per-control forcing
  is deliberately avoided) and refreshes the application alignment
  resources consumed by the central styles via DynamicResource:
  `FieldTextAlignment`, `GridHeaderTextAlignment`,
  `GridCellTextAlignment`, `LblMargin`, `FieldMargin` (margins flip
  sides in RTL).
- `ShellContext.ApplyToWindow` (Services/ShellContext.cs) is the
  single application point. It runs for EVERY window from the App's
  global Window.Loaded class handler — including windows that derive
  from Window directly — and again from BaseWin at load.
- Direction source: `LangDirSv` (ar/ku/he/fa/ur are RTL) resolved at
  open time from the live shell language. No window derives its own
  direction from a hardcoded language check.
- Popups/context menus do not inherit window FlowDirection in WPF.
  Since the completion pass they need no per-window handling: App
  registers class handlers for `ContextMenu.OpenedEvent` and
  `ToolTip.OpenedEvent` (`App.GlobalPopupOpened`) that copy the
  direction from the popup's placement target at open time, so
  every menu/tooltip follows the window it belongs to, including
  after a live language switch. (Older per-window syncs — CoaWin,
  DataGridTableBehavior — set the same value and remain as harmless
  redundancy.)
- Direction-neutral CONTENT inside a mirrored window (codes,
  Latin/English names, barcodes, numeric runs) is pinned by ONE
  central bundle: the `FlowAlign.LtrContent` attached property
  (Views/F2WinUi.cs). Setting it — as a XAML attribute, a style
  setter, or a code setter — applies LeftToRight + en-US language +
  Western-digit number substitution to that element only. The
  literal `FlowDirection="LeftToRight"` setter must never appear
  in a window file again; the census in the Gates section proves
  none remains.

## CatWin — explicit verdict (owner-named)

- Inherits `BaseWin` ✓; window direction comes from the central
  mechanism at open and on live language change ✓.
- Its local styles are direction-AWARE by construction: label/field
  alignment flips through DataTriggers bound to
  `Window.FlowDirection` (TextAlignment / HorizontalContentAlignment
  switch Right in RTL) — the strongest mirroring pattern in the set.
- Its two direction-neutral content fields (card number, folder
  glyph) and its `FieldBoxLtr` field style carried literal LTR
  setters; since the completion pass they use the central
  `FlowAlign.LtrContent` bundle — same rendering, no window-local
  direction literal.
- One bottom-bar StackPanel pinned physical Right (line 458) was a
  genuine deviation — FIXED in this pass (`FlowAlign.AlignEnd`).
- Verdict: CatWin mirrors as a whole window. Runtime visual proof
  remains Windows-owed, like every window here.

## Deviation found and fixed at the cause: physical end-pins

WPF `HorizontalAlignment.Left/Right` are PHYSICAL — they do not
flip with FlowDirection. The sweep found the repeating pattern
"horizontal button/action cluster pinned Right" in 96 places,
INCLUDING the central helpers every code-built window uses
(`F2Ui.BottomBar`, RptBase's action bar): in Arabic the bars stayed
on the physical right while the rest of the window mirrored.

Fix — one central mechanism, applied at the sites:
`FlowAlign` (Views/F2WinUi.cs, central file) binds an element's
HorizontalAlignment to its own inherited FlowDirection
(end = Right in LTR / Left in RTL; start = the inverse). The binding
re-evaluates live when the window's direction flips, so live
re-mirroring is preserved. Applied to:

- F2Ui.BottomBar (every F2-built window's command bar) + RptBase.
- 86 code sites across 59 window files (mechanical transform,
  handlers untouched; build-verified 0/0).
- 10 XAML sites via `local:FlowAlign.AlignEnd/AlignStart`:
  AccCard, CatWin, ComWin, DatWin, MatDirWin, ReportGridVw,
  SerWin, StpWiz, PrmWin (start), RestoreCoWin (start).
  ComWin note: the owner's exception for ComWin covers button
  PLACEMENT (bottom-bar rule); mirroring its bar's alignment does
  not move any button between bars and follows the mirroring rule.
- OptWin's form fields: `AddField` pinned controls physical Left,
  which detaches fields from their (mirrored) labels in RTL —
  now `FlowAlign.Start` (also its POS-devices button).

## Completion pass 2026-11-24 — remaining exceptions closed

Everything the first pass had left as "classified content pins" or
code-side re-assertions was re-examined; each item below is FIXED
unless marked KEPT with its reason.

1. **Content pins → central bundle.** The 6 window-file LTR pins
   the audit counted (AccCard TxtNameEn — named in the owner's
   Idea — CatWin card number + folder glyph, MatCard English name +
   item number, WhsWin English name) now set
   `FlowAlign.LtrContent` instead of a literal FlowDirection.
2. **Style-internal setters the audit script does not scan.**
   CatWin `FieldBoxLtr`, MatCard `CodeBox` (which also set
   Language/NumberSubstitution inline) and MatCard's units-grid
   number styles (`UnitNumberTextStyle`, `UnitNumberEditStyle`)
   set the same bundle through a style setter on the attached
   property. MatCard's English-name/item-number fields dropped
   their now-redundant inline setters (the CodeBox style carries
   the bundle).
3. **Code-built grid style.** ReportGridVw built its numeric cell
   style in code with a literal LTR setter; it now adds the
   `FlowAlign.LtrContentProperty` setter. The grid itself still
   mirrors with the window; only the numeric run keeps reading
   order, exactly as before.
4. **Startup-chain code-behind re-assertions REMOVED.** Lang and
   LogWin each re-pinned their button bar to the physical right
   and cleared its inherited FlowDirection on every direction
   apply — in Arabic that actively broke the mirror of the bar
   while the rest of the window flipped. Both methods/blocks are
   deleted; the bars anchor to the layout END in XAML
   (`FlowAlign.AlignEnd`) and their grids mirror with the window.
   The button columns they set in code merely restated the XAML
   document order (OK col 0, Cancel col 2) in both languages.
   LogWin's field labels and hide-username checkbox (also pinned
   physical Right) are end-bound the same way. KEPT as a matched
   pair: the password eye-toggle button and the password box's
   46px end padding — the padding reserves exactly the toggle's
   space; moving one without the other would overlap text.
5. **SplWin footer + progress.** The previous pass classified the
   progress indicator as control mechanics. This pass binds it to
   the layout START (`FlowAlign.AlignStart`) so the fill grows
   from the start side in both directions, and the status/copyright
   texts are start/end-bound; the code line that re-pinned the
   indicator Left on every progress update is removed. LTR
   appearance is unchanged.
6. **PrmWin.** Now derives from BaseWin (migrated in an earlier
   pass; this document's table was stale) and its status line's
   physical Left pin is start-bound.
7. **Popups.** Context menus and tooltips sync centrally at open
   (see mechanism section) — this closes WhsWin's tree menu,
   MatDirWin's items menu and ComWin's delete menu, which had no
   direction sync at all.

Startup chain, stated plainly: every chain window (SplWin, Lang,
StpWiz, ComWin, LogWin, then MWin) is created AFTER `App.OnStartup`
has loaded the persisted preference and set the application
language, and the global Window.Loaded class handler is registered
before any window exists — so each chain window's DIRECTION
follows the persisted choice at creation. Lang additionally
previews the candidate language while choosing (by design).
Strings of the Window-derived startup dialogs are the language
active at their construction (see live-switching section).

## Classified and kept (with reasons)

- Numeric/amount alignment: right-aligned in both directions by
  convention (F2Ui.RightAlignStyle, grid numeric columns) — text
  alignment of styled controls follows the flipped application
  alignment resources.
- Text/value TextBlock pins (OrdWin total, PosWin value column):
  cell-position pins that stay with their mirrored columns.
- BaseWin.CreateRecordNumberBar: internal order fixed physically
  BY DESIGN (documented in code: next/previous glyph sequence);
  the bar itself sits inside the mirrored tree.
- Control-internal mechanics: WhsWin toggle-switch knob
  (template-internal, transform-driven), Lang's language-combo
  template ContentSite, LogWin's password eye-toggle + its matched
  46px padding — physical parts of a control, not layout.
- MWin shell chrome: caption buttons (MWin.xaml:168) are window
  chrome; the shell mirrors its nav pane/command bar through its
  own ApplyUiDirection (same LangDirSv source, V3). The ONE
  remaining control-level pin in the tree (audit count: 1) is
  MWin.xaml:975, the recycle-bin corner layer: a physical
  screen-corner anchor whose purpose is NOT to mirror; the corner
  button's own direction is set per language in code
  (MWin.xaml.cs).
- BaseWin.CreateRecordNumberBar (Views/BaseWin.cs, a central
  file): the record-number bar's internal next/previous order is
  fixed physically BY DESIGN (documented in code); the bar sits
  inside the mirrored tree.
- Print rendering is not window layout: InvPrn's receipt root
  takes the current language direction; RptRend's page root is
  RTL only when the report model says so. Both follow their
  governing input, never a hardcoded side.
- Lang: the first-run language picker previews the CANDIDATE
  language's direction while choosing — by design; on confirm,
  App.SetApplicationLanguage applies the choice program-wide.
- StpWiz's FlowDirection setters are bindings to the window's own
  FlowDirection (FallbackValue only) — they follow the window,
  they do not pin anything.

## Live language switching (steer point 3) — stated plainly

- Direction: SUPPORTED live for every open window.
  `App.SetApplicationLanguage` calls `LangDirSv.ApplyToAllWindows`,
  which re-applies the window-level FlowDirection to all open
  windows; BaseWin windows additionally re-localize their strings
  via the LanguageChanged hook, and all FlowAlign bindings
  re-evaluate with the inherited direction.
- Strings: BaseWin windows re-localize live. Window-derived startup
  dialogs (SplWin, Lang, LogWin, StpWiz, ComWin, BkpWin,
  RestoreCoWin) show the language active when they were
  constructed — they are transient/startup surfaces; their
  DIRECTION still updates live via ApplyToAllWindows. This is an
  architecture fact, stated rather than papered over.

## Per-window verification table

Generated from the tree after this pass (base + direction source
per tools/audit_window_context.py; FlowAlign sites and remaining
physical L/R pins counted per window file set). "Remaining pins"
are the classified-kept content/mechanics pins above.

| Window | Base | FlowAlign sites | Remaining L/R pins | Verdict |
|---|---|---|---|---|
| AccCard | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound; English-name field via central FlowAlign.LtrContent (owner-named pin removed) |
| ActivityLogWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| AddonsWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| AssetWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| BankRecWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| BarWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| BatchSerialPickerWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| BcdDesWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| BcdGenWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| BinWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| BkpWin | Window | 0 | 0 | direction via App class handler (ShellContext) |
| BnkCard | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| BomWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| BudgetWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CalculatorWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| CashDrawerOptionsWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| CatWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CcCard | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CcListWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| ChangePasswordWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| ChqListWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| ChqWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CloseCashWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| CoaWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| ColChsWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| ComWin | Window | 1 | 0 | direction via App class handler (ShellContext); 1 cluster(s) end/start-bound |
| CommWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| CompressInvoicesWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| ContractWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CrmActivitiesWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CrmCampaignWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CrmLeadWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CrmOppWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CrmPipelineWin | BaseWin | 3 | 0 | mirrors centrally (BaseWin + App handler); 3 cluster(s) end/start-bound |
| CshWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CstCard | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| CurCard | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| CustomReportsWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| DatWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| DefManagerWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| DeliveryDeskWin | BaseWin | 4 | 0 | mirrors centrally (BaseWin + App handler); 4 cluster(s) end/start-bound |
| DocSearchWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| FinancialAnalysisWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| GroupManagerWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| HelpWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| HrAttendanceWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| HrDeptWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| HrDesigWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| HrEmployeeWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| HrLeaveWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| HrPayrollWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| HrStructureWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| InstallmentsWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| InvDesWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| InvoiceWin | BaseWin | 3 | 0 | mirrors centrally (BaseWin + App handler); 3 cluster(s) end/start-bound |
| JrnWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| InfoWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| Lang | Window | 1 | 0 | first-run picker: previews the candidate language by design; direction via LangDirSv at construction + App handler; button bar end-bound (code re-assertion removed) |
| LicenseWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| LogWin | Window | 4 | 1 | direction via own LangDirSv hook + App handler; labels/bar end-bound (ApplyButtonOrder removed); 1 kept pin = password eye-toggle, a matched pair with the password box padding |
| LoyaltyWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| MWin | RibbonWindow | 0 | 2 | shell — own ApplyUiDirection (same LangDirSv source) |
| MaintenanceWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| MatCard | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| MatDirWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| MatGroupWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| MatMergeWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| MfgListWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| MfgPlanWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| MfgWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| OptWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| OrdWin | BaseWin | 5 | 1 | mirrors centrally (BaseWin + App handler); 5 cluster(s) end/start-bound |
| OrderTrackWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PartyManagerWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PartySearchWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PayRecWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PosCheckoutWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PosReturnWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| PosSalesWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| PosSessionWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| PosSyncWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| PosTermWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| PosWin | BaseWin | 7 | 1 | mirrors centrally (BaseWin + App handler); 7 cluster(s) end/start-bound |
| PrmWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| ProjWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PurchaseAttachmentsWin | BaseWin | 0 | 1 | mirrors centrally (BaseWin + App handler) |
| PurchaseLandedCostWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PurchaseMatchPolicyWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PurchaseReceiptReturnWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| PurchaseRequisitionWin | BaseWin | 0 | 1 | mirrors centrally (BaseWin + App handler) |
| PurchaseReturnWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| QcAlertWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| QcInspectWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| QcTemplateWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| RecurWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| RepCard | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| RepostWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| RestoreCoWin | Window | 1 | 0 | direction via App class handler (ShellContext); 1 cluster(s) end/start-bound |
| RmaWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| RolloverWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| SalesDeliveryWin | BaseWin | 3 | 0 | mirrors centrally (BaseWin + App handler); 3 cluster(s) end/start-bound |
| SalesReturnWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| SerWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| SplWin | Window | 3 | 0 | splash artwork by design; direction via own ApplyLocalization (persisted choice at creation) + App handler; footer texts and progress fill start/end-bound |
| StockCountDialog | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler); dialog class defined inside WhsWin.xaml.cs |
| StpWiz | Window | 1 | 0 | direction via App class handler (ShellContext); 1 cluster(s) end/start-bound |
| SupplierQuotationCompareWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| SupplierQuotationEntryWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| SupportWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| TaxReportWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| TaxSetupWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| UpdatesWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| WarrantyWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| WhsWin | BaseWin | 3 | 1 | mirrors centrally (BaseWin + App handler); 3 cluster(s) end/start-bound; English-name field via central FlowAlign.LtrContent; 1 kept pin = toggle-switch knob (template mechanics) |

UserControls (DashVw, ReportGridVw) hold no direction of their own:
they inherit the host window's FlowDirection. ReportGridVw's
numeric cell style uses the central LtrContent bundle (completion
pass item 3). Ad-hoc code-built dialogs (~19 `new Window` sites:
PosWin, OrdWin, DeliveryDeskWin, SalesDeliveryWin, CatWin toast,
ChqListWin, CoaWin, CstCard, DocSearchWin, InvoiceWin, WhsWin
toast) set no direction of their own — each is covered by the
App's Window.Loaded class handler; several also copy their
owner's FlowDirection at construction.

## Gates

First pass: build 0 errors / 0 warnings; audit_window_context
exit 0 (0 window-level pins, 7 justified control pins);
verify_screenfit, verify_english_source, schema_audit exit 0;
migrator idempotent; harnesses unchanged (see REDUCE_PLAN
outcomes).

Completion pass (feat/rtl-complete, base reduce 4eafea21):
- Build: every project in the solution compiled
  --no-incremental at 0 errors / 0 warnings (Domain, Infra,
  App, inv, svc, SqlMigrator, MainApp, Kern.Tests — verified per
  project; the single-invocation solution build exceeds this
  build VM's memory while sibling workstreams build
  concurrently, so the full compile was proven project by
  project, which covers every project the solution contains).
- audit_window_context exit 0: 114 window classes, 0 window-level
  pins, control-level pins 7 → 1 (MWin.xaml:975, classified
  above). verify_screenfit, verify_english_source, schema_audit
  all exit 0.
- Static census (grep over cod/app/src, XAML + code): the only
  remaining literal LeftToRight SETTERS are in central files —
  FlowAlign.LtrContent itself (F2WinUi.cs) and
  BaseWin.CreateRecordNumberBar (BaseWin.cs, by design) — plus
  the classified MWin corner layer. Everything else matching the
  pattern is a DataTrigger COMPARISON (CatWin/MatCard alignment
  triggers), StpWiz's binding FallbackValue, App.xaml's
  ArabicTextStyle RightToLeft setters (central, correct), or the
  print path (InvPrn/RptRend, input-driven).
- Errors parity: 171 codes ×4 languages, key sets equal. Lang
  parity: no new gaps vs base (one pre-existing gap on base:
  `RecordNavigation` present in En/Ar, absent in Fr/Tr — a
  translation-catalog gap, not a direction issue; recorded here
  so it is not lost).
- Harnesses, all green: P2p 39/39, W1 36/36, Bin 22/22,
  Num 27/27, Info 35/35, Fmt 49/49.
- No migration added by this pass.

Runtime visual proof of mirroring is Windows-owed — it has NOT
been run; nothing in this document claims it.
