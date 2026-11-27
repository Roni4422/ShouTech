# PosCheckoutWin minimum fix (2026-11-20)

## Defect

`tools/verify_screenfit.py` failed: **PosCheckoutWin minimum 540×420
exceeded the 684×384 cap** (MinHeight 420 > 384). That contradicted the
step-12 claim that every window minimum is ≤ cap: `BaseWin.ClampToWorkArea`
floors at the minimum, so a 420 minimum defeated the owner's 50% opening
rule for this window.

## What forced the 420 height

PosCheckoutWin is code-built (`cod/app/src/Views/PosCheckoutWin.cs`,
no XAML) and its old minimum was the honest sum of its content:

| Block | Old cost |
|---|---|
| Root margin 16 (top+bottom) | 32 |
| Header: 19 pt title + 2 form rows (32 + 8 gap each) + margin 10 | 115 |
| Total panel: padding 12 + 26 pt total + margin 10 | ~70 |
| Tenders grid: 3 rows × (32 + 8 gap) | 120 |
| Change (18 pt) + margin 10, Remaining line | ~51 |
| Actions: 32 px buttons + margin 10 | 42 |
| **Total** | **~420** |

The form rows (32 px control + 8 px central gap) are the app standard
and were NOT touched.

## Changes (`PosCheckoutWin.cs` only)

- Root margin 16 → 10.
- Title 19 pt → 16 pt; header margin 10 → 6.
- Total panel padding 12 → 8, margin 10 → 6; total 26 pt → 20 pt
  (still the largest text in the window by far, bold).
- Change margin 10 → 4; change 18 pt → 16 pt.
- Actions margin 10 → 6.
- The middle band (total + tenders + change + remaining) is now wrapped
  in a vertical `ScrollViewer`: at the new minimum the compacted content
  is ~374 DIP, so a few DIP of font-metric variance degrades to a tiny
  scroll instead of clipping. The Complete/Cancel buttons live in the
  bottom row, outside the scroll region, so they stay visible at any size.

**Before / after minimums: 540×420 → 540×372** (MinWidth unchanged,
already ≤ 684). Opening size unchanged at 620×560; the 50% opening cap
(`BaseWin.ClampToWorkArea`) is untouched.

Honest note: the harness cap is 684×384 (half of 1366×768). The work-area
half-height at that resolution is ~364, so like every other window at the
384 cap this window opens at its 372 minimum there — consistent with the
codified step-12 rule. WPF runtime confirmation at small sizes is still
owed on Windows.

## Companion fixes (same pass)

- `tools/verify_english_source.py`: the Arabic error catalog
  (`Resources/Errors`) is now allow-listed like Lang and COA/Lang.
  The audit also covered the rest of the approved Option A locations
  the script had never listed — immutable migrations (`dba/mig`,
  Arabic seed data, never edited), the intentional STEI fixture
  (`tests/Kern.Tests/SteiRoundTripTests.cs`), and the design docs
  (`docs/`, which quote Arabic UI terms) — and its docstring now states
  that policy. The two real violations, Arabic comments in
  `MatCard.xaml.cs:748,899`, are translated to English. The script now
  reports 0 Arabic lines outside approved locations.

## Gates

- Full solution build `--no-incremental`: 0 errors / 0 warnings.
- `tools/audit_window_context.py`: exit 0.
- `tools/verify_screenfit.py`: exit 0 (ALL PASS).
- `tools/verify_english_source.py`: exit 0.
- SqlMigrator on scratch SQLite: applies cleanly, second run no-op
  (no migration changes in this branch).
