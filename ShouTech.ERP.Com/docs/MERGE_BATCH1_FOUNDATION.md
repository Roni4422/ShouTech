# Merge Log — Batch 1 Foundation (2026-10-05)

## Source
`_Foundation.zip` → `ShouTech_Batch1_Foundation/`

## Decision
Accepted as enterprise-grade and suitable for multi-year evolution:
- Report windows inherit `RptBase` → only define columns/filters + data load.
- Excel-like column chooser with per-user persistence (`WinPref`).
- Export: professional XLSX (no deps), PDF via WPF visual rasterization (Arabic shaping intact), encrypted branch exchange (AES-256-GCM `.shx`).
- Invoice templates encrypted with integrity (`InvTpl` + `XptCry`).
- Full 4-language keys (`Report.*`, `Export.*`, extended `Common.*`).
- Namespaces match project (`ShouTech.App.Views|Services|Reporting`).
- No new top-level folders; services merged into existing `cod/app/src/Services/`.

## Files added (paths under `cod/app/src/`)
| Path | Notes |
|------|--------|
| Views/BaseWin.cs | Mandatory base for secondary windows |
| Views/RptBase.cs | Report window base |
| Views/ColChsWin.xaml(+.cs) | Column chooser |
| Reporting/RptDef.cs | Standard report models (complements existing ReportModels designer) |
| Reporting/RptRend.cs | Print/PDF page renderer |
| Reporting/InvTpl.cs | Encrypted invoice templates |
| Services/WinPref.cs | Geometry + report columns prefs (coexists with Configuration/UsrPref) |
| Services/XptCry.cs | AES-256-GCM container |
| Services/XptServ.cs | Unified export |
| Services/XlsxWrt.cs | XLSX writer |
| Services/PdfWrt.cs | PDF writer |

## Files replaced
- `Resources/Lang/{Ar,En,Fr,Tr}.json` — foundation is strict superset (+Report, +Export, +Common keys).

## Fixes applied during merge
1. `BaseWin.GeometryScope`: `UserName` → `Username` to match `UserInfo.Username`.
2. `MainApp.csproj`: explicit `Resources\Lang\*.json` CopyToOutputDirectory.
3. `docs/FILE_ABBR.md`: abbreviations for all new symbols.

## Not done (by design / optional)
- Wiring `XptCry.SetKeyProvider` to native `sec/` — optional; DPAPI local key works until hardened build.
- Migrating existing secondary windows to inherit `BaseWin` — incremental future work.
- No duplicate service files created; only net-new service classes in existing folder.

## Quality gate notes
- English-only comments in foundation sources.
- Tamper detection on `.shx` / invoice templates.
- Existing custom-report designer (`ReportModels` / `FormulaEngine` / `ReportRepo`) untouched and complementary.
