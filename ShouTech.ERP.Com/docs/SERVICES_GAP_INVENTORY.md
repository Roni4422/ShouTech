# Services-phase gap inventory — Al-Ameen 10 vs ShouTech (2026-11-24)

Research-first record for the final phase of the `reduce` branch
(owner decision 2026-11-24 13:01–13:07). Al-Ameen evidence: vendor
sites (alameenksa.com, alameensoft.com), official release notes
(downloads.alameensoft.com), and practitioner documentation of the
maintenance windows (accountingw.com). Evidence strength stated
plainly: Al-Ameen's *menu-level* contents are documented; screen-level
detail is partly inferred from those sources. ShouTech state was
audited in code on this branch — every "exists" below names the file.

## Al-Ameen 10's services surface (from the sources)

- **Tools → Administration → File maintenance** (أدوات/إدارة/صيانة
  الملفات): reindex files; update the database (restore default
  files); compact the database; audit account balances (recompute
  from vouchers); audit material balances & prices (negative-stock
  and broken-unit report, fix what's fixable); audit FX parity rates;
  audit files.
- **File → Backup** (and continuous/automatic backups in the cloud
  offering); **File → Close fiscal year → Roll accounts** (تدوير
  الحسابات) with options (new-year start, what rolls forward).
- **Program Options** (خيارات البرنامج): a central options surface
  referenced throughout the release notes (material field names, POS
  naming, per-document behavior).
- **Report generator** (مولّد التقارير): pick sources (invoices,
  vouchers, securities), content (account balance, material qty),
  period; format the printed form inside the program (font, header,
  footer); save reports inside the program; export to Excel; ready
  print templates; unlimited design of commercial papers (cheques).
- **Permissions**: hierarchical per-user permissions; audit trail.
- **Barcode**: barcode handling in inventory, label printing.

## ShouTech state — what already exists (do NOT rebuild)

| Area | State |
|---|---|
| Backup / restore | BkpWin (File tab), RestoreCoWin, MaintenanceWin "Backup Now" via IBkpServ |
| Data maintenance core | MaintenanceWin (Tools.MaintenanceWizard): DatabaseHealth check, provider integrity summary, backup, SQLite VACUUM/ANALYZE (PostgreSQL honestly disabled with explanation) |
| Fiscal years | RolloverWin (OtherTools.Rotation): fiscal_years list, create next year, close/lock a year; continuous ledger carries balances; period locks enforced in posting (GlPosting/JournalRepository) |
| Users & permissions | PrmWin (per-user permissions + templates, OtherTools.Permissions), roles, ActivityLogWin audit trail |
| Companies / data tools | ComWin; DatWin dataset import/export (STEI/Excel/PDF) |
| Recalculate costs | StockRecalculationService engine exists (posting-order replay, revaluation jobs) — but has NO window (standing flag from the fix phase) |
| Report designer core | ReportDefinition/ReportColumnDef + ReportRepo (tables CustomReportDefinitions/Columns/Groupings/Filters): columns add/remove/reorder/resize, field binding, captions, data formats, visibility; **formula columns** over a real expression engine (FormulaEngine: fields, ROUND/ABS/MIN/MAX/IF/COALESCE, compile-time validation with errors); aggregates SUM/AVG/MIN/MAX/COUNT; running totals; groupings with subtotals + page breaks; filters (prompted/required); orientation; totals row; header info; save/copy/delete named designs; visibility Private/Shared/System; per-user ownership; CustomReportsWin (run/new/edit/save) with an in-window designer form; ReportGridVw; ribbon-reachable (Reports.CustomReports) |
| Barcode | BarWin (barcode records), BcdGenWin (generate/assign for materials), BcdDesWin (label design: size, fonts, colors, visible elements, live Code-128 preview, direct print) — reachable via RptNav |
| POS options | CashDrawerOptionsWin behind the Tools.Options key (POS devices only) |
| Invoice printing | InvPrn: FIXED layout for all document kinds (InvDocKind covers sales/purchase invoices, returns, stock in/out, transfers, opening, counts, quotations); hardcoded colors/headers; totals on one line; notes. NumWords (amount-in-words) exists and is used by CshWin |
| Settings storage | UsrPref (~30 settings: language, currency, theme, accent, font size, compact mode, toolbar/statusbar, animations, date/number formats, decimals, separators, confirmations, autosave, autocomplete, autofocus, tooltips, auto-lock, auto-open company, splash, window geometry) — persisted per user; read at apply points (App startup, VisuId) |

## Gaps → builds in this phase (each its own gated commit)

- **B0 — Encrypted design files (shared).** `DesignFileSvc`:
  AES-256-GCM encrypt/decrypt of a design payload (JSON) with a
  product key + random nonce; the GCM tag is the integrity check — a
  tampered or foreign file fails decryption and is rejected with a
  cataloged error (new DSG-81xx codes ×4 languages). Extensions
  (documented here, used by all three designers): report designs
  **`.strd`**, invoice designs **`.stid`**, barcode label designs
  **`.stld`**. Honest note: "readable only by the program" means the
  file format is ShouTech's and the key ships with the product — the
  standard model for template files in this product class.
- **B1 — Options window (OptWin).** One Al-Ameen-style options
  surface over UsrPref: sections (General/Appearance, Language &
  formats, Behavior & confirmations, Session) editing the stored
  settings, saved through UsrPref.Save() and applied live where the
  app has appliers (language, accent, font size via the existing
  VisuId/localization entry points). The Tools.Options key currently
  opens the POS-only CashDrawerOptionsWin; it will open OptWin, whose
  POS section button opens CashDrawerOptionsWin — the drawer options
  stay one click away and the key's meaning ("Options") is widened,
  not moved. Ribbon registration: existing Tools.Options button (the
  nav pane/command bar pick it up automatically).
- **B2 — Report Designer completion** (owner spec 13:03 — flexible):
  the core already covers columns/formulas/grouping/filters/totals.
  Missing and built here:
  1. **Styling + page setup** on ReportDefinition: title/header/body/
     totals fonts (family, size, bold), header background/text
     colors, row banding color, grid-lines toggle, page size
     (A4/A5/Letter), margins. Migration 115 adds the columns to
     CustomReportDefinitions (both dialects); ReportRepo maps them;
     the RptRend print path honors them (PageModel carries the style).
  2. **Cell-level formulas in totals/subtotal cells**: a per-column
     `TotalsFormula` — an expression over the column aggregates
     (e.g. `SUM(Profit) / SUM(Sales) * 100`) evaluated for the totals
     row and group subtotals, edited and validated through the same
     FormulaEngine (a bad formula is rejected with the engine's
     error, never a crash). Body cells are already covered by
     formula columns; this lands the remaining cell class the spec
     names. Migration 115 also adds the column to
     CustomReportColumns.
  3. **Company header block**: ShowHeaderInfo exists; add the
     company address block toggle fed from the company profile
     (no logo data exists in the company profile today — the block
     renders name + address; a logo element is NOT faked).
  4. **Encrypted export/import** of a design (`.strd`) in
     CustomReportsWin; an import lands as a new private design of
     the importing user in the current company store.
  - **B2 OUTCOME (built, gated):** all four items landed. Migration
    115 (both dialects) — discovery while building: the four
    CustomReport* tables had never been created by ANY migration
    (the designer core shipped without its schema), so 115 creates
    them IF NOT EXISTS in the exact shape ReportRepo expects, then
    adds the 18 style/page columns + TotalsFormula (guarded ALTERs).
    ReportDefinition carries the style and projects it to RptStyle;
    ReportRepo maps + persists it and validates TotalsFormula at
    save (CompiledFormula.TryCompile, same as body formulas);
    ReportDesignerVM.ComputeTotals evaluates aggregates first, then
    totals formulas in display order over the aggregate map (a
    formula result feeds later formulas); RptRend honors the style
    (page size A4/A5/Letter × orientation, mm margins, title/header/
    body/totals fonts, header colors, banding override, grid-line
    hairlines, company address block) — a null style reproduces the
    previous rendering exactly, and the renderer's stale #0067C0
    accent fallback became slate #51708E. XptServ threads Style +
    CompanyAddress through print/PDF (page size + rows-per-page
    follow the style). CustomReportsWin: style fields + totals-
    formula editor in the designer form, totals row on run, new
    Print / PDF / Excel buttons rendering the last run through the
    real path, Export/Import design buttons over DesignFileSvc
    (.strd) with ReportDesignJson (versioned DTO) as the payload.
    19 new CustomReports.* Lang keys × 4 languages (parity verified:
    35 keys, 0 missing). Gates: ALL-GREEN (build 0/0; migrator fresh
    through 115 + idempotent; 4 audit scripts exit 0; Errors parity
    736 ×4; P2pHarness 39/39; W1Harness 36/36). Schema smoke check:
    fresh DB carries the 4 tables (40/21/12/14 columns) with all B2
    columns present. Windows-owed: WPF click-through of the designer
    + styled print/PDF; live PostgreSQL run of migration 115.
- **B3 — Invoice Designer (InvDesWin)** (owner spec 13:07).
  Model choice (researched): Phenix's document designer is a
  fixed-form layout editor; modern banded designers (FastReport /
  Stimulsoft / DevExpress report designers) organize a document as
  bands (header, data, totals, footer) with per-band elements — the
  banded model covers all six document types with one engine and
  matches how InvPrn already composes a page, so the banded model
  is adopted and Phenix's per-document-type tailoring is kept
  (a separate design per document kind). Candidates compared:
  Phenix fixed-form (rejected: one-off per kind, weak reuse),
  banded (adopted), free-canvas (rejected: no WPF canvas-designer
  substrate in the repo; precision layout without one is fake).
  Build: `DocDesign` model + store — migration 116 `doc_designs`
  (company-scoped, JSON payload, named designs + one default per
  kind) for the six kinds (sales invoice, purchase invoice, sales
  return, purchase return, stock entry, stock exit); elements:
  company header block, party block, document meta (number/date/
  warehouse), lines columns (choose/reorder/resize from the InvDoc
  line fields), totals + tax block composition, amount-in-words
  (NumWords), notes, signature blocks, barcode/QR placement (the
  document number rendered as a Code-128 barcode via the existing
  encoder; QR has no encoder in the repo — the element is the
  barcode, and QR is recorded as not available, not faked).
  InvPrn.BuildPage becomes design-driven (falls back to today's
  layout as the built-in default design); InvDesWin provides the
  design surface + live preview (renders a sample document through
  InvPrn) + save/copy/delete + encrypted export/import (`.stid`).
  Ribbon: new button under the Invoices tab (key
  Invoices.InvoiceDesigner) routed in RibbonNav.
- **B4 — Barcode Designer completion.** BcdDesWin keeps its working
  surface; added: **named designs in the DB per company** (migration
  117 `label_designs`, JSON payload; the existing local-file design
  remains the fallback working design), a **symbology choice** —
  Code 128 (existing encoder) + Code 39 (new encoder in Common) —
  design list (load/save-as/delete/set-print-default), print uses
  the selected design, encrypted export/import (`.stld`).
- **B5 — Maintenance completion.** MaintenanceWin gains the two
  Al-Ameen audits that map to real ShouTech engines: **Recalculate
  costs** (StockRecalculationService, exposed through
  OperationsService) with its job result, and a **trial-balance
  integrity audit** (per-period Dr=Cr verification + orphan journal
  line check, read-only). FX-parity audit: NOT built — ShouTech has
  no parity-rate revaluation engine to audit against; recorded as a
  gap, not faked.

## Not built (recorded, with reasons)

- Month-period close UI: year close/lock exists (RolloverWin) and
  posting enforces period locks; a separate monthly close surface
  duplicates the year tool — the owner can direct it later.
- FX parity audit (above). Company logo storage: no logo field
  exists in the company profile; adding one is a ComWin/company
  schema change outside this phase's scope — the designers' header
  blocks use the company name/address that do exist.
- QR code element: no QR encoder exists in the repo (labels and
  documents use the Code-128/Code-39 encoders that do).
