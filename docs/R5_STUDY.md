# R5 study — the thin windows (phase-gate study)

Owner phase-gate rule (2026-11-24 16:28) + benchmark weighting
(16:31/16:32): the core four are primary; outside products
enter per-item only when demonstrably better on a specific
pattern. Scope: the audit's six thin-window entries —
PosSyncWin, ActivityLogWin, AddonsWin, PrmWin (×2 entries),
LicenseWin — plus CloseCashWin's standing question.

## Ground truth (inspected, not assumed)

| Window | Reality today | Actual thinness |
|---|---|---|
| PosSyncWin (139 lines) | Real viewer over outbound_messages (006) + a REAL retry write (FAILED → QUEUED); permission-gated; bottom bar; status bar | No status filter; grid has NO row numbers (F2Ui.ConfigureGrid sets RowHeaderWidth=0 — every F2Ui grid shares this); both catches show raw ex.Message with NO cataloged error report (violates the error-reporting rule) |
| ActivityLogWin (144) | Real two-tab audit viewer (security_audit_log 029 + journal_audit_events) with date-range filter; permission-gated | No user/event filter; catch not reported centrally; selected tab not persisted |
| AddonsWin (120) | Honest read-only module catalogue; its own doc comment records there is NO runtime module-toggle API, so no fake switches | The completion IS the licensing build (commercial phase; D1/D2/D5 are the owner's pending decisions) |
| LicenseWin (149) | Real license info + real online validation over ILicenSvc; Renew honestly disabled (no renewal API exists) | Same: completion = the licensing build (signed license files per the design doc) |
| PrmWin (83 + XAML 58) | REAL editor: PrmWinVM loads permission groups + per-user exceptions via PermAdminSvc and saves them; bottom buttons; but the window derives from **Window, not BaseWin** — manual VisuId/LangDirSv calls, hardcoded English XAML strings, no tint, no geometry persistence, no central status bar/F1 | Platform conformance, not function |
| CloseCashWin | Legacy cash-close window beside PosSessionWin's close flow | Retirement is the OWNER's open decision — not taken here |

## Benchmark scan (core four primary; extras per-item)

- Audit/activity logs: Al-Ameen classic has user + event
  filtered audit review; ERPNext's Activity Log / Audit Log
  filters by user and reference; Odoo's chatter is per-record
  (different model). **Taken (from the core four):** text
  filtering over user/event for ActivityLogWin.
  Extra scan: Dynamics 365 audit summary and QuickBooks'
  audit log both converge on the same user/event/date filter
  shape — no pattern beyond the four's was demonstrably
  better, so nothing extra was taken.
- Permissions editors: ERPNext's Role Permission Manager is a
  doctype×role matrix — a DIFFERENT security model from
  ShouTech's per-user exceptions over role bases. **Not
  taken** (would replace the RBAC model, not improve this
  window). Al-Ameen/Phenix user-permission windows are
  per-user grant lists like PrmWin's — the model stays; the
  fix is platform conformance (BaseWin).
- Module/license windows: SAP B1 and Dynamics show license
  allocation per user/module; all presuppose the licensing
  ENGINE. ShouTech's engine is the commercial phase (owner
  decisions pending) — **no UI fakery now**; AddonsWin and
  LicenseWin are recorded as that phase's foundation and
  stay (b) BY DESIGN, with the dependency named.
- POS sync/outbox: Odoo POS's offline order queue and
  ERPNext's POS sync logs both filter by state and retry
  failures — the shape PosSyncWin already has, plus the
  status filter this phase adds.

## FORM (counted)

- PosSyncWin: 8 grid columns, 3 bottom buttons (Retry,
  Refresh, Close), slim status bar (count + failed count).
  After: +1 filter control (status choice) in the filter
  strip; row numbers on (central F2Ui change — every F2Ui
  grid gains them, per the owner's grid spec).
- ActivityLogWin: 2 tabs (security grid + journal grid),
  2 date filters, 2 bottom buttons (Refresh, Close). After:
  +1 text filter; selected tab persisted (WINSTATE hooks —
  the F&O personalization set applied).
- PrmWin: tabbed grant editor (groups as tabs, checkbox
  rows), 2 bottom buttons (Cancel, Save) — already bottom.
  After migration: same form on BaseWin (slim status bar,
  tint, geometry, F1, whole-window mirroring from the
  central path; static texts localized via LocProps).

## Builds this phase

1. F2Ui.ConfigureGrid: numbered row headers centrally
   (SetShowRowNumbers) instead of RowHeaderWidth=0.
2. PosSyncWin: status filter (All/Queued/Failed/Sent) +
   cataloged error reporting in both catches.
3. ActivityLogWin: user/event text filter (client-side over
   the loaded rows) + tab persistence via OnSave/OnRestore
   WindowState + cataloged error reporting.
4. PrmWin → BaseWin migration: XAML root + base class,
   LocProps localization of the static texts, manual
   VisuId/LangDirSv calls removed (central path replaces
   them), own tint (new palette token Hickory). The VM and
   the save path are untouched.
5. AddonsWin / LicenseWin: no build — recorded as the
   licensing phase's foundation (stay (b), dependency
   named). CloseCashWin: untouched (owner's decision).

## (b) accounting

17 → 14 if the builds land: PosSyncWin, ActivityLogWin and
PrmWin ×2 flip to (a); AddonsWin and LicenseWin stay (b) by
design until the commercial licensing phase.
