<!-- source: docs/MERGE_BATCH1_FOUNDATION.md -->

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

---

<!-- source: docs/BATCH2_REPORTS_INVOICES.md -->

# Batch 2 — Core Reports & Commercial Invoices (2026-10-05)

## Goal
Ship a competitive **core** of standard reports and invoice documents on top of Batch 1 foundation (`RptBase`, export, crypto).

## Reports (`Views/Reports/`)
| Window | ReportKey | Purpose |
|--------|-----------|---------|
| TrialBalanceWin | Fin.TrialBalance | Trial balance |
| AccStmtWin | Fin.AccountStatement | Account statement + running balance |
| JrnListWin | Fin.JournalList | Journal register |
| StkBalWin | Inv.StockBalances | Stock balances + valuation |
| MatMoveWin | Inv.MaterialMoves | Material movement card |
| CustBalWin | AR.CustomerBalances | Customer balances |
| SupBalWin | AP.SupplierBalances | Supplier balances |
| SalListWin | Sales.InvoiceList | Sales document register |
| PurListWin | Purch.InvoiceList | Purchase document register |

Open via `RptNav.Open*` helpers.

## Invoices
| Component | Role |
|-----------|------|
| Reporting/Invoices/InvDoc.cs | Document + line model, tax per line |
| Services/DocSchema.cs | sales/purchase documents, lines, party_ledger, stock_movements |
| Services/InvRepo.cs | Draft save, post (immutable), numbering |
| Services/InvPrn.cs | Print + PDF |
| Views/SalInvWin.cs | Sales invoice UI |
| Views/PurInvWin.cs | Purchase invoice UI |

## Accounting rules applied
- Posted documents are immutable; corrections via return documents.
- Party ledger updates on post (customers debit / suppliers credit for normal invoices).
- Line-level tax rates for mixed VAT (GCC / Levant / EU-ready).
- Company-scoped queries only.

## Honest scope note
This is a **production core**, not full Al-Ameen/Phoenix parity. Missing by design for later batches:
- Full COA drill-down, multi-currency revaluation reports
- Advanced print designer templates per customer
- Stock postings from invoice lines into cost layers/FIFO
- POS receipt specialized layout
- ZATCA/e-invoice XML payloads

## Localization
`Resources/Lang/*.json` sections: `Reports.*`, `Invoice.*` (ar/en/fr/tr).

---

<!-- source: docs/BATCH3_INTEGRATION.md -->

# Batch 3 — Shell Integration + Stock Post + Tax + Deep Reports (2026-10-05)

## Done
1. **Main window wiring**: `MWin.OnRibbonButtonClick` → `RptNav.TryHandle(LocProps key)`.
2. **TabReports expanded** with financial / inventory / parties groups.
3. **Sales/Purchase return** ribbon buttons → `SalInvWin(SalesReturn)` / `PurInvWin(PurchaseReturn)`.
4. **Stock posting** on document post via `StkPost.ApplyDocument` (movements + balances + avg cost).
5. **TaxSvc**: default rate from `tax_codes` or country defaults (GCC / Levant / N.Africa / EU); party + material resolve from DB.
6. **Invoice fields ↔ DB**: party code LostFocus, material code CellEditEnding fill name/price/unit/tax.
7. **Deep reports**: customer/supplier aging, item profitability, stock movements register.

## How to open
Ribbon keys handled by `RptNav.TryHandle` (see Services/RptNav.cs switch).
