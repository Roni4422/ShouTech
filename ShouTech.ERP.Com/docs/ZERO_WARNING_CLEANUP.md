# Zero-Warning Cleanup — feat/zero-warning-cleanup

Date: 2026-11-17 · Base: main @ `05d9296` · Result: **0 errors, 0 warnings**
(full solution build, `--no-incremental`, summary literally reports
`0 Warning(s)` / `0 Error(s)`).

## Baseline

A clean build of `05d9296` reports **67 warnings**. Every MainApp warning is
printed twice by the build (once for `MainApp.csproj`, once for the WPF
markup temp project `MainApp_*_wpftmp.csproj`), so the 67 reduce to
**35 unique source sites**: 32 in the app (×2) + 3 in Kern.Tests (×1).

| Rule | Build count | Unique sites | Meaning |
|---|---|---|---|
| CA1305 | 18 | 9 | Locale-sensitive formatting without an explicit `IFormatProvider` |
| CA1863 | 16 | 8 | `string.Format` re-parses the format string on every call — cache a `CompositeFormat` |
| CA1822 | 16 | 8 | Member does not access instance data — mark `static` |
| CS8604 | 6 | 3 | Possibly-null argument passed to a non-nullable parameter |
| CS1998 | 3 | 3 | `async` method with no `await` (test factories) |
| CS8602 | 2 | 1 | Dereference of a possibly null reference |
| CS0108 | 2 | 1 | Derived member hides a base member with different semantics |
| CS0105 | 2 | 1 | Duplicate `using` directive |
| CA1859 | 2 | 1 | Field typed as an interface where the concrete type is preferred |
| **Total** | **67** | **35** | |

## Suppressions

**None.** No `#pragma warning disable`, no `SuppressMessage`, no `NoWarn`,
no analyzer was disabled or reconfigured, and no code was deleted to silence
a warning. Every warning was fixed at its cause.

## How each class was fixed

### CA1305 — explicit culture (9 sites)
The app has two established conventions, verified by surveying the codebase
before touching anything:

- **On-screen values** are formatted with `CultureInfo.CurrentCulture`
  explicitly (36 existing `ToString("N2", CultureInfo.CurrentCulture)`-style
  call sites in cards/lists).
- **Printed documents** are formatted with `CultureInfo.InvariantCulture`
  (`Services/InvPrn.cs` formats invoice `UnitPrice`/`LineTotal` with
  `"N2", CultureInfo.InvariantCulture`).

Fixes follow those conventions:

- `Views/LicenseWin.cs` (3): `ExpiryDate` / `ActivatedAt` / `LastOnlineCheck`
  `.ToString("yyyy-MM-dd"[, " HH:mm"])` in the on-screen info list →
  explicit `CultureInfo.CurrentCulture`. Output is byte-identical to before
  (the parameterless overload already used `CurrentCulture`); the culture is
  now explicit instead of implicit.
- `Views/MaintenanceWin.cs` (1): latency `TotalMilliseconds.ToString("0")`
  in the on-screen results list → explicit `CultureInfo.CurrentCulture`.
  Byte-identical output, same reasoning.
- `Views/OrdWin.cs` (2): `BuildPrintPage` printed `UnitPrice`/`LineTotal`
  with `.ToString("N2")`. → `CultureInfo.InvariantCulture`. See
  "Behavior-sensitive fixes" below.
- `Views/JrnWin.cs` (3): `string.Format(T(...), ...)` without a provider →
  routed through the file's existing `FormatLocalized` helper (added by
  `d352f36`), which formats with `CultureInfo.CurrentCulture` explicitly.
  Byte-identical output; also clears the paired CA1863 at each site.

### CA1863 — cached CompositeFormat (8 sites)
The app already has a house pattern for this (BinWin, BcdGenWin, JrnWin,
RptBase): a per-class
`ConcurrentDictionary<string, CompositeFormat>` cache plus a private static
`FormatLocalized(pattern, args)` helper that parses each localized pattern
once and formats with `CultureInfo.CurrentCulture`. The localized patterns
come from the language catalogs at runtime, so the cache is keyed by the
pattern string — a single static `CompositeFormat` field would be wrong
(the pattern changes with the UI language).

- `Views/JrnWin.cs` (3): routed through the existing helper (see CA1305).
- `Views/CompressInvoicesWin.cs` (2), `Views/CustomReportsWin.cs` (1),
  `Views/SupportWin.cs` (1), `Views/RestoreCoWin.xaml.cs` (1): added the
  same cache + helper to the class (copied from the BinWin/JrnWin pattern)
  and routed the `string.Format(CultureInfo.CurrentCulture, T(...), ...)`
  calls through it. Formatted output is unchanged — same culture, same
  pattern, same arguments; only the parse is cached.

### CA1822 — mark static (8 sites)
All eight members are private and touch only static state
(`BaseWin.T`, `BaseWin.CurrentCompanyCode`, `BaseWin.CurrentUserId`,
`AppHost.*`, `AppState`, static services) — verified per member before
editing. Marking them `static` does not change any call site (all callers
are in the same class and call unqualified) and does not change behavior.

- `Views/ChangePasswordWin.cs`: `ChangeDatabasePasswordAsync`,
  `ChangeLocalAdminPasswordAsync`
- `Views/ChqListWin.cs`: `RequireManage`
- `Views/CshWin.cs`: `RequireJournalContext`
- `Views/CustomReportsWin.cs`: `UserId` (property), `ResolveCompanyGuidAsync`
- `Views/OrdWin.cs`: `ResolveMaterial`, `StatusText`

### CS8604 — honest nullability (3 sites, ClosingAccountsWin)
`PickName(...)` legitimately returns `string?` (an account row can have no
usable name in either language column), and the rows it feeds are
`Dictionary<string, object?>`, which accepts null. The local
`Add(string section, string code, string name, decimal amount)` helper
declared the parameter non-nullable while already receiving possible nulls
at runtime. Fix: the parameter is now `string? name`. **Zero runtime
change** — the signature now tells the truth about a value that could
already be null.

### CS8602 — null dereference (1 site, CustomReportsWin)
In the designer save path, `ExecuteSourceAsync` can return null (its other
call site in the same file checks `rows is null` and reports the source as
not runnable). The save path dereferenced `rows.Count` unchecked — a null
return would have thrown `NullReferenceException`. Fix:
`rows is { Count: > 0 }`, so a null (or empty) result falls back to the
default `{ "Code", "Name" }` column seed the code already uses for empty
results. See "Behavior-sensitive fixes" below.

### CS0108 — hidden member with different semantics (1 site, OrdWin)
`OrdWin` had a private `ShowError(string message)` that hid
`BaseWin.ShowError(string messageKey)`. The two are **not** the same
operation: the base takes a localization **key**, localizes it, and shows
the error dialog (Error icon, localized "Error" title); OrdWin's takes an
already-localized **literal** (call sites pass `T(...)` results,
`ex.Message`, interpolated text) and shows the warning dialog (Warning
icon, window title). Deleting OrdWin's method would have silently rebound
15 call sites to the base semantics — so instead the private method was
**renamed to `ShowWarningMessage`** (definition + all 15 call sites in the
same file), with a comment at the definition explaining the distinction.
The `MessageBox.Show` call itself is untouched: same text, same caption,
same icon.

### CS0105 — duplicate using (1 site, BarWin)
Removed the duplicated `using ShouTech.App.Services;`.

### CA1859 — concrete dictionary type (1 site, NumWords)
`NumWords.Units` is a private static lookup table that is only ever read
(`TryGetValue`). Its declared type changed from
`IReadOnlyDictionary<string, (string Major, string Minor)>` to
`Dictionary<string, (string Major, string Minor)>` (the initializer already
constructed a `Dictionary`). No behavior change.

### CS1998 — test factories (3 sites, Kern.Tests)
`CreateAsync()` factories in `OrderServiceTests.cs`,
`SalesChainTests.cs`, and `SalesChainPostingTests.cs` were `async` with
fully synchronous bodies and a single tuple return. Following the exact
precedent set by `d352f36` for `PostingCompletenessTests.cs`: the methods
are no longer `async` and return `Task.FromResult<(...)>((...))` with the
explicit tuple type. Names and signatures are unchanged, so every
`await CreateAsync()` call site compiles and behaves as before (the work
was always done synchronously before the first — nonexistent — await).

## Behavior-sensitive fixes (flagged explicitly)

1. **OrdWin print culture (CA1305 ×2).** `BuildPrintPage` now formats
   `UnitPrice`/`LineTotal` with `InvariantCulture` instead of the implicit
   `CurrentCulture`. Under an invariant or Latin-digit UI culture the
   printed output is identical; under a culture with different digits or
   decimal separators (e.g. Arabic UI), the printed order's prices will now
   use Western digits and `.` decimal separator instead of localized ones.
   Rationale: this is the app's established print convention — the same
   statement already formats `LineNo` and `Qty` with `InvariantCulture`,
   and the dedicated print service (`InvPrn.cs`) formats exactly these two
   values (`UnitPrice`, `LineTotal`, `"N2"`) with `InvariantCulture`.
   The implicit-culture call was the outlier. If the owner prefers
   culture-localized printouts, this is the one line to revisit.
2. **CustomReportsWin null source (CS8602).** Previously, a null result
   from `ExecuteSourceAsync` in the designer save path crashed with
   `NullReferenceException` (surfaced as a generic failure). It now falls
   back to the default two-column seed, matching the empty-result path in
   the same method. This changes behavior only in a case that previously
   faulted.
3. **Test factories (CS1998 ×3).** Test-only code. One theoretical
   difference: an exception thrown inside the factory now propagates
   synchronously at the call instead of being captured into a faulted
   task that the immediate `await` rethrows. Every call site awaits the
   result immediately, so test outcomes are identical. This matches the
   `d352f36` precedent.

All other fixes are provably output-identical: explicit `CurrentCulture`
where the implicit default was already `CurrentCulture`, cached
`CompositeFormat` with the same culture/pattern/arguments, `static`
marking of members that touched only static state, a widened nullability
annotation on a parameter that already received nulls, a duplicate
`using` removal, a concrete type for a read-only lookup table, and a
private method rename with all call sites in the same file.

## Scope notes

- 17 files changed; no DatWin files, no migrations, no Lang/Error
  catalogs, no project files, no analyzer configuration.
- No new `try`/`catch` was added anywhere (no new silent catches).
- Pre-existing bare `catch` in `CustomReportsWin.ResolveCompanyGuidAsync`
  (returns `Guid.Empty`) was left untouched — behavior preservation; it is
  not a warning site.

## Gates (all run on this branch)

| Gate | Result |
|---|---|
| Full solution build `--no-incremental` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | exit 0 (71 window classes, 0 window-level FlowDirection pins) |
| SqlMigrator, fresh SQLite | 41 APPLY, second run 41 SKIP / 0 APPLY, 90 tables |
| `tools/verify_english_source.py` | output byte-identical to base `05d9296` (its exit-1 Tr-catalog finding is pre-existing on base) |

Still owed (unchanged from base): Kern.Tests execution and live
PostgreSQL verification require a Windows/PG environment — the test
project targets `net8.0-windows`.
