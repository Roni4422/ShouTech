# RTL whole-window mirroring audit (owner directive 2026-11-24 14:55)

Owner rule: in Arabic the ENTIRE window mirrors — not only its
content. The window-level FlowDirection must flip the full layout
tree (navigation, panes, grids, scrollbars, bars, dialog ownership)
as a mirror image; in English/French/Turkish the same window is
fully LTR. The mechanism must live in the central files and its
application must be VERIFIED per window. CatWin (نافذة الصنف) is
named first by the owner.

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
- Popups/context menus do not inherit window FlowDirection in WPF;
  the windows that own them sync them explicitly (CoaWin propagates
  its FlowDirection to its context menus on every open).

## CatWin — explicit verdict (owner-named)

- Inherits `BaseWin` ✓; window direction comes from the central
  mechanism at open and on live language change ✓.
- Its local styles are direction-AWARE by construction: label/field
  alignment flips through DataTriggers bound to
  `Window.FlowDirection` (TextAlignment / HorizontalContentAlignment
  switch Right in RTL) — the strongest mirroring pattern in the set.
- Two element-level `FlowDirection="LeftToRight"` pins (lines 192,
  390) are direction-neutral content fields (code text must render in
  reading order); they do not block layout mirroring.
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
- Control-internal mechanics: WhsWin toggle-switch knob,
  SplWin progress indicator — physical parts of a control, not
  layout.
- MWin shell chrome: caption buttons (MWin.xaml:168) are window
  chrome; the shell mirrors its nav pane/command bar through its
  own ApplyUiDirection (same LangDirSv source, V3).
- LogWin / Lang: startup dialogs that manage their own direction
  deliberately (LogWin swaps its button bar in code-behind per
  language; Lang previews the candidate language by design).
- Control-level FlowDirection pins (7, from audit_window_context):
  English-name and code fields (AccCard TxtNameEn, MatCard ×2,
  WhsWin, CatWin ×2, MWin ×1) — direction-neutral content.

## Live language switching (steer point 3) — stated plainly

- Direction: SUPPORTED live for every open window.
  `App.SetApplicationLanguage` calls `LangDirSv.ApplyToAllWindows`,
  which re-applies the window-level FlowDirection to all open
  windows; BaseWin windows additionally re-localize their strings
  via the LanguageChanged hook, and all FlowAlign bindings
  re-evaluate with the inherited direction.
- Strings: BaseWin windows re-localize live. Window-derived startup
  dialogs (SplWin, Lang, LogWin, StpWiz, ComWin, BkpWin, PrmWin,
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
| AccCard | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
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
| Lang | Window | 0 | 3 | self-managed startup dialog (own direction hook/swap) |
| LicenseWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| LogWin | Window | 0 | 6 | self-managed startup dialog (own direction hook/swap) |
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
| PosSessionWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| PosSyncWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| PosWin | BaseWin | 7 | 1 | mirrors centrally (BaseWin + App handler); 7 cluster(s) end/start-bound |
| PrmWin | Window | 1 | 1 | direction via App class handler (ShellContext); 1 cluster(s) end/start-bound |
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
| SplWin | Window | 0 | 4 | splash artwork by design; direction via App handler |
| StpWiz | Window | 1 | 0 | direction via App class handler (ShellContext); 1 cluster(s) end/start-bound |
| SupplierQuotationCompareWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| SupplierQuotationEntryWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| SupportWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| TaxReportWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| TaxSetupWin | BaseWin | 1 | 0 | mirrors centrally (BaseWin + App handler); 1 cluster(s) end/start-bound |
| UpdatesWin | BaseWin | 0 | 0 | mirrors centrally (BaseWin + App handler) |
| WarrantyWin | BaseWin | 2 | 0 | mirrors centrally (BaseWin + App handler); 2 cluster(s) end/start-bound |
| WhsWin | BaseWin | 3 | 1 | mirrors centrally (BaseWin + App handler); 3 cluster(s) end/start-bound |

## Gates

Build 0 errors / 0 warnings; audit_window_context exit 0 (0
window-level pins, 7 justified control pins); verify_screenfit,
verify_english_source, schema_audit exit 0; migrator idempotent;
harnesses unchanged (see REDUCE_PLAN outcomes). Runtime visual
proof of mirroring is Windows-owed.
