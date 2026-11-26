# SVC1 study — Tools/Services coverage + OptWin completion (2026-11-24)

Phase-gate study (owner rule, 2026-11-24 16:28), written BEFORE
the phase's first build commit. Sources: the Phenix tools
research (`~/workspace/research_notes/phenix-tools-tab-inventory-20261124-1335/report.md`
— screenshot-verified Options categories + function-verified
capabilities, menu placement unproven where the report says so),
the owner's Phenix Options screenshot (2026-11-24 16:43 —
LAYOUT reference only: category rail + settings body + bottom
buttons; its setting labels are NOT legible and are not
transcribed), docs/SERVICES_GAP_INVENTORY.md, and code audit of
this branch. HARD LIMITS honored: the item contents of Phenix's
أدوات / أدوات أخرى menus are not publicly documented — nothing
is invented for them; no Phenix data-repair command is evidenced
— none is listed.

## 1. POWER — Tools/Services coverage vs Phenix (function-verified)

| Phenix capability | ShouTech status |
|---|---|
| Fiscal-year rollover wizard (opening journal voucher, opening-goods invoice per warehouse, end-of-period pricing patterns) | RolloverWin exists (fiscal years, close/lock, continuous ledger). TRAILING: no opening-goods-invoice generation or pricing-pattern choices — named gap, module phase |
| Backup (toolbar command; Options category; dashboard overdue alert) | BkpWin + RestoreCoWin + MaintenanceWin Backup Now (IBkpServ). Staleness warning: ADOPTED in this phase (see §3) |
| Export / import operations | DatWin (STEI/Excel/PDF round-trip, fixed in the fix phase) ✓ |
| Program update / Smart Update | Absent — belongs to the commercial phase's installer/packaging workstream; named there |
| Attachments system | Platform-level support exists (AppHost); per-record attachment UI is partial — named gap, not this phase |
| Advanced printing + invoice designer (FastReport) | B2 Report Designer + B3 Invoice Designer (own engine, cell formulas, encrypted design files — exceeds on portability) ✓ |
| Composite barcode design | B4 Barcode Designer (multi-field labels, .stld) ✓ |
| Inventory settlement | Stock-count family (count → variance → posting) ✓ |
| Manual account posting | JrnWin ✓ |
| Manual reference closing | Partial (document close states exist per family) — recorded as partial, no build claimed |
| Cost-control options | Recalculate Costs + integrity audit (B5, MaintenanceWin) ✓ |

Al-Ameen 10 services (from SERVICES_GAP_INVENTORY.md) were
closed by B0–B5. ERPNext/Odoo converge on the same service set
(backup, data import, fiscal-year closing, audit log) — all
present. Extras scan (owner weighting rule): Dynamics 365 F&O
per-module "Parameters" pages and SAP B1 "General Settings"
tabs both converge on the pattern ShouTech already follows
(domain settings live with their domain; one user-preferences
surface) — nothing extra taken.

## 2. FLEXIBILITY + FORM — OptWin vs the Phenix 20 categories

OptWin today: ONE scrolling form, 6 sections, 24 controls, all
bound to real UsrPref properties (no placeholder fields — the
owner's 16:43 criterion is met on bindings; the LAYOUT is what
trails). Phenix: a category RAIL (20 categories) + per-category
body + bottom buttons (owner screenshot). FORM verdict: adopt
the rail — with 24+ controls the single scroll forces hunting;
the rail makes each domain's settings one click away (EASE) and
gives future sections a home (FLEXIBILITY). Field counts per
section are OURS (Phenix's per-category field counts are not
published; not invented).

Per-section inventory (field → binding), current + this phase:

| Section | Fields | Bindings (all UsrPref unless noted) |
|---|---|---|
| General (5) | Language, Currency, Date format, Decimal places, Thousand separator | Lang (live apply), CurrencyCode, DateFormat, DecimalPlaces (+DecimalDigits), ThousandSeparator |
| Appearance (6) | Accent, Font size, Compact mode, Show toolbar, Show status bar, Animations | AccentColor (live apply), FontSize, CompactMode, ShowToolBar, ShowStatusBar, EnableAnimations |
| Behavior (6) | Confirm on exit, Confirm on delete, Auto-complete, Auto-focus first field, Tool tips, Auto-save interval | ConfirmOnExit, ConfirmOnDelete, EnableAutoComplete, AutoFocusFirstField, ShowToolTips, AutoSaveIntervalMinutes |
| Session (3) | Auto-open last company, Skip splash, Auto-lock minutes | AutoOpenLastCompany, SkipSplash, AutoLockMinutes |
| Point of sale (1 action) | POS devices button → CashDrawerOptionsWin (real device settings: drawer/printer) | company POS device settings (that window's own store) |
| Window layouts (3 actions) | Reset positions / layouts / all | WinPref.ResetUserState (user scope only) |
| **Backup (NEW, this phase)** | Last-backup readout + staleness warning + Open Backup button → BkpWin | DERIVED, not stored: newest entry of IBkpServ.ListBackups(company).LastWriteUtc — cannot drift from reality |

Phenix's 20 categories mapped: General ✓ · Advanced ✓
(Behavior) · Accounts — ABSENT WITH REASON (account defaults
live in CoaWin/AccCard and company setup, not user prefs) ·
Restaurant — ABSENT (no restaurant mode; named POS gap) ·
Invoice extras — ABSENT WITH REASON (invoice behavior lives in
document settings + B3 designs, per company, not per user) ·
Tables — ABSENT (restaurant) · Printing — PARTIAL (POS devices
section; general print design lives in B2/B3) · Backup ✓ NEW ·
User ✓ (Session+Behavior) · Barcode — ABSENT WITH REASON
(label defaults are company-level, set in BcdDesWin/B4) ·
Export — ABSENT WITH REASON (DatWin carries its own options) ·
Manufacturing — ABSENT WITH REASON (no per-user mfg prefs
exist; nothing to bind — no filler) · Activity recorder ✓
(ActivityLogWin; always-on audit, no toggle faked) · Cash sales
invoices ✓ (POS section) · Numbers/date/time ✓ (General) ·
Font ✓ (Appearance) · POS ✓ · Add-ons ✓ (AddonsWin) · SMS —
ABSENT (no SMS gateway exists) · Email — ABSENT (no SMTP store;
the SMTP question is an open owner decision from the sales
cycle — not pre-decided here).

## 3. USER-FRIENDLINESS — the backup-staleness adoption

Phenix's dashboard warns "no backup for 8 days" (screenshot).
Adopted within this phase's reach: OptWin's Backup section
shows the current company's last-backup age and a warning
state when older than 7 days (our threshold, recorded), and
BkpWin already lists every backup with its date. The full
dashboard placement (MWin company overview) is named for the
commercial phase — MWin is the Fluent.Ribbon shell, and an
alert surface there deserves the Windows visual pass rather
than a headless guess.

## 4. Build list for SVC1 (only what this study justifies)

1. OptWin restructured to the category-rail layout (rail on the
   start side, mirrors in RTL; one section body visible at a
   time; bottom Save/Close unchanged). All fields, bindings and
   handlers unchanged — presentation only. The selected section
   persists per user via the BaseWin window-state hooks
   (last-selected tab, per the window-state scope).
2. OptWin Backup section per §2/§3 (readout + warning + button
   opening BkpWin as a dialog owned by OptWin).
3. Lang keys ×4 for the new section; gates; docs.

Power + ease verdict for the phase: POWER — the services
surface is already at parity or beyond (designers, encrypted
files, integrity audits Phenix does not evidence); EASE — the
rail turns a 24-control scroll into one-click domains, and the
backup warning moves a silent risk into the open.
