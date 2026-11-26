# Window-state persistence (WINSTATE, 2026-11-24)

Owner rule (16:16): EVERYTHING the user does to windows must be
saved and restored — centrally, per user. Research refinements
(owner-forwarded, from the window-state best-practice report)
applied the same day.

## What existed

WinPref (per company+user JSON store) already saved window
geometry (RestoreBounds + Maximized, on Closing) and report/grid
column layouts (visibility/order/width) — but restore ran at
Loaded, validation clamped to the PRIMARY display only, the 50%
first-open cap re-trimmed restored windows on every load (so a
saved geometry never actually won), and there was no store for
panes/tabs/sort, no schema version, no defaults layer, no reset.

## What was built

- **Restore point**: BaseWin restores geometry in
  OnSourceInitialized (platform guidance; constructor/Loaded
  restore misplaces maximized windows on multi-monitor setups).
- **Saved geometry wins**: BaseWin skips ClampToWorkArea when a
  saved record was restored — the ≤50% cap is now strictly the
  first-open default (owner rule). RestoreGeometry returns whether
  a record applied.
- **Off-screen safety**: the saved rectangle is kept when a usable
  part (≥100×40) intersects the VIRTUAL screen — negative
  coordinates are legal and validated by intersection, never by
  sign — clamped inside the virtual bounds; otherwise it falls
  back to the primary work-area clamp (removed display, changed
  resolution). A saved Minimized state is never restored:
  SaveGeometry records Normal/Maximized only, so a window
  minimized at close reopens at its RestoreBounds.
- **Schema version**: PrefBag carries Version (current 1). A file
  from a NEWER version is never trusted — defaults apply. Legacy
  unversioned files are accepted and upgraded on next save. A
  corrupt file already fell back to an empty bag (kept).
- **Company/admin defaults layer**: reads fall back user bag →
  "{company}/defaults" bag for geometry, columns, values and grid
  states. User overrides always win. (Read path built; authoring
  defaults is an admin/deploy action — no writer UI yet.)
- **Named values + grid states**: WinPref.Values (per element:
  pane widths, selected tab, expanded state, last-used values —
  the F&O personalization set) and WinPref.GridStates (sort key +
  direction), with BaseWin hooks OnRestoreWindowState /
  OnSaveWindowState and PrefGet/PrefSet helpers for derived
  windows. Report grids (RptBase) capture their view sort on
  close and re-apply it on bind (guarded: an un-sortable row
  shape falls back to natural order, reported once, never
  blocking). NOTE: no current window exposes a user splitter
  (checked MWin/CoaWin/MatDirWin — fixed panes), so no pane
  wiring was faked; the first window that gains a splitter uses
  the hooks. Fonts/theme/accent/language already persist in
  UsrPref — the same per-user file-store family; access stays
  through UsrPref's existing API.
- **Reset (OptWin)**: new "Window layouts" section — Reset window
  positions / Reset grid layouts / Reset all layouts, each
  confirmed, clearing USER-scoped state only (defaults and other
  users untouched). Windows already open re-save their state on
  close, as usual — the reset takes effect for windows opened
  afterwards.

## Chosen vs rejected (find-the-best record)

- Store: the research norm is a DATABASE user-profile store (SAP
  B1/BC). REJECTED for now, deliberately: ShouTech windows exist
  BEFORE any company database is open (splash, setup, company
  picker, login), and the app is offline-first — a DB store
  cannot serve the startup chain, and two stores would be worse
  than one. The hardened per-user file store (versioned,
  layered, corruption-safe) is the chosen pattern; a DB-backed
  profile sync is a named commercial-phase candidate, not a
  silent omission.
- Group/filter grid state: reports group through their report
  DEFINITIONS (B2 designer), not ad-hoc in-grid grouping, so
  there is no in-grid group state to persist — recorded as
  by-design, not a gap.

## Gates

WINSTATE: build 0/0; migrator through 120 idempotent; 4 audits 0;
Errors parity PASS; P2pHarness 39/39; W1Harness 36/36 — ALL-GREEN.
Runtime proof (multi-monitor restore, DPI) is Windows-owed.
