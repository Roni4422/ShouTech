# ShouTech ERP — Missing Windows Inventory (Phase 3)

*Compiled 2026-11-17 on branch `feat/missing-windows-phase3` (base: `ready/combined-2026-11-17`).*

Sources used: `dba/sys/NAV_README.md` (the `dba/sys/nav.sql` + `dba/sds/sds_nav.sql`
SQL Server catalog described there is **legacy/reference only** — it is not in the
active SQLite/PostgreSQL tree; the live catalog is the MWin ribbon, 242 ribbon
elements in `MWin.xaml`), `cod/app/src/Navigation/RibModMap.cs` (17 tabs →
modules), `cod/app/src/Services/RptNav.cs` (the live command router),
`cod/app/src/Services/RibbonReportRouter.cs`, `cod/app/src/Services/Data.cs`
module definitions, a grep for `ShowOperationUnavailable` /
`ShowPersistenceUnavailable` across `cod/app/src/Views`, and
`docs/COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md`.

## 1. How navigation actually works today

Ribbon/menu action keys are routed by `RptNav.TryHandle` (~70 keys) and
`RibbonReportRouter.TryHandle` (5 keys) to concrete windows. Anything not routed
falls through to the generic `ModuleWorkspaceView` placeholder. The nav database
catalog in `dba/sys/NAV_README.md` (17 menus · 17 tabs · 55 groups · 215 items ·
209 permissions) describes the *intended* catalog; the table below maps every
routed command family to its window and status.

Status key: ✅ window exists and is data-backed · 🟡 window exists but a core
action is a placeholder (`ShowOperationUnavailable` / `ShowPersistenceUnavailable`)
· 🔀 routed to a neighbouring window (no distinct window) · ❌ missing entirely.

## 2. Command → window map (RptNav + RibbonReportRouter + MWin direct)

| Command family (action keys) | Window | Status |
|---|---|---|
| Trial balance (`Accounts.TrialBalance`, `Reports.TrialBalance`, `Reports.Statistics`) | `TrialBalanceWin` | ✅ |
| Account balance | `AccBalWin` | ✅ |
| Balance sheet | `BalSheetWin` | ✅ |
| Income statement | `IncStmtWin` | ✅ |
| Ledger / account statement / customer statement | `AccStmtWin` | ✅ |
| General journal / journal list | `JrnListWin` | ✅ |
| Stock balances / warehouse inventory / material overview | `StkBalWin` | ✅ |
| Material movement / cost-center material movement | `MatMoveWin` | ✅ |
| Stock adjustment report | `StkAdjRptWin` | ✅ |
| Item profitability | `ItemProfitWin` | ✅ |
| Customer balances / overview | `CustBalWin` | ✅ |
| Customer aging | `CustAgingWin` | ✅ |
| Supplier balances | `SupBalWin` | ✅ |
| Supplier aging | `SupAgingWin` | ✅ |
| Material profits (`Inventory.MaterialProfits`) | `ItemProfitWin` | 🔀 router alias |
| Material group movement | `MatMoveWin` | 🔀 router alias |
| Sales report (`Inventory.SalesReport`) | `SalListWin` | 🔀 router alias |
| Aging receivables (`Accounts.AgingReceivables`) | `CustAgingWin` | 🔀 router alias |
| Daily report (`Accounts.DailyReport`) | `JrnListWin` | 🔀 router alias |
| POS / cash sales (`Invoices.CashSales`, `POS.OpenCashier`) | `PosWin` (+ `PosCheckoutWin`) | ✅ |
| Sales invoice / sales return | `InvoiceWin(SalesInvoice/SalesReturn)` | ✅ |
| Purchase invoice / purchase return | `InvoiceWin(PurchaseInvoice/PurchaseReturn)` | ✅ |
| Sales / purchase invoice lists | `SalListWin` / `PurListWin` | ✅ |
| Stock transfer, opening stock, stock in/out, inventory count, quotation | `InvoiceWin(kind)` | ✅ |
| Journal voucher | `JrnWin` | 🟡 Print = `ShowOperationUnavailable` |
| Receipt / payment voucher | `CshWin(Receipt/Payment)` | 🟡 Save has no persistence (`ShowPersistenceUnavailable` on some actions) |
| Cheque voucher | `ChqWin` | 🟡 Print = placeholder; some actions unpersisted |
| Customer / supplier card | `CstCard` | 🟡 Statement action = placeholder; some actions unpersisted |
| Cost-center card | `CcCard` | 🟡 Save/Delete = `ShowPersistenceUnavailable` (no DB wiring at all) |
| Currency card | `CurCard` | 🟡 some actions unpersisted |
| Bank card | `BnkCard` | 🟡 some actions unpersisted |
| Sales-rep card | `RepCard` | 🟡 some actions unpersisted |
| Manufacturing order | `MfgWin` | 🟡 some actions unpersisted |
| Sales / purchase order | `OrdWin(Sales/Purchase)` | 🟡 Print + Convert-to-invoice = placeholders; some actions unpersisted |
| Barcode create / generate / design | `BarWin` / `BcdGenWin` / `BcdDesWin` | ✅ |
| Material list / search | `MatListWin` | ✅ |
| Price list | `PriceListWin` | ✅ |
| Shortage list | `ShortListWin` | ✅ |
| Material card, material groups, material directory, categories, warehouses, stock count | `MatCard`, `MatGroupWin`, `MatDirWin`, `CatWin`, `WhsWin` (+ `StockCountDialog`, `MatMergeWin`) | ✅ (opened from MWin) |
| Chart of accounts, account card | `CoaWin`, `AccCard` | ✅ |
| Permissions | `PrmWin` | ✅ |
| Data import/export | `DatWin` | ✅ |
| Backup / restore, recycle bin, company restore | `BkpWin`, `BinWin`, `RestoreCoWin` | ✅ |
| Serials (`SerWin`) | `SerWin` | ✅ |
| Cash drawer options | `CashDrawerOptionsWin` | ✅ |
| Company / login / language / splash / setup wizard | `ComWin`, `LogWin`, `Lang`, `SplWin`, `StpWiz` | ✅ startup flow |
| **Payment reconciliation** (`Accounts` gap) | — | ❌ **built in Phase 3** |
| **Projects** | — | ❌ **built in Phase 3** |
| **Assets register + depreciation** | — | ❌ **built in Phase 3** |
| **CRM opportunity pipeline** | — | ❌ backlog (after Phase 3 core) |
| **Quality management** | — | ❌ backlog |
| Budgets (`Accounts` gap) | — (`AccCard` has a budget flag only) | ❌ backlog |
| Batch/serial tracking, stock valuation (FIFO/Moving Avg), stock ledger audit | partial (`SerWin` exists) | ❌ backlog (engine work, not just windows) |
| RFQ → PO → receipt chain (`Buying` gap) | `OrdWin(Purchase)` + `InvoiceWin` only; `document_links` schema exists (migration 024) | 🟡 chain windows partial — backlog |
| Quotation → SO → delivery → invoice chain | `InvoiceWin(Quotation)`, `OrdWin(Sales)`; `document_links` schema exists | 🟡 Convert action is a placeholder in `OrdWin` — backlog |
| MRP / job cards (`Manufacturing` gap) | `MfgWin` only | ❌ backlog |
| Projects timesheets | — | ❌ backlog (Projects window built; timesheets not) |
| Maintenance, Support/Helpdesk, Subcontracting | — | ❌ backlog (low priority per roadmap) |
| Bank statement import, e-invoicing (Kuwait NIC/PACS) | outbox webhooks only | ❌ backlog |
| Payroll & leave (HRMS) | basic HR folder only | ❌ backlog |
| AI Report Copilot | design doc only (`docs/UI_2030_AI_REPORT_ASSISTANT.md`) | ❌ backlog |

## 3. Counts

- Routed command keys: ~75 (`RptNav` ~70 + `RibbonReportRouter` 5).
- Distinct windows/views in `cod/app/src/Views`: 23 XAML windows + 3 module
  views, 17 code-built windows/cards, 19 report windows (incl. `StkAdjRptWin`),
  plus nested `StockCountDialog` / `MatMergeWin` / `CashDrawerOptionsWin`.
- ✅ exists & data-backed: **38** window families.
- 🟡 exists but placeholder/unpersisted actions: **9** (`JrnWin` print,
  `CshWin`, `ChqWin`, `CstCard`, `CcCard`, `CurCard`, `BnkCard`, `RepCard`,
  `MfgWin`, `OrdWin` — 10 files, counted as 9 families since CshWin covers two
  voucher kinds).
- 🔀 alias-routed (no distinct window): **5** commands.
- ❌ missing entirely at inventory time: **11** module areas (payment
  reconciliation, projects, assets, CRM pipeline, quality, budgets, batch/serial
  + valuation engine, MRP/job cards, maintenance, helpdesk, subcontracting,
  bank import/e-invoice, payroll/leave, AI copilot) — Phase 3 builds the top 3.

## 4. Roadmap gap → schema / service / window (at inventory time)

| Gap | Schema? | Service? | Window? |
|---|---|---|---|
| Payment reconciliation | Partial: `party_ledger` (010), `sales_documents.paid_amount`, `document_links` is doc-chain only — a dedicated `payment_allocations` table is needed | No | No |
| Projects | No | No | No |
| Assets + depreciation | No | No | No |
| CRM opportunities | Leads exist in CRM folder; no pipeline tables | Partial | No |
| Quality | No | No | No |
| Budgets | No (flag on account card) | No | No |
| Batch/serial + valuation | Serials partial | Partial | `SerWin` only |

## 5. Buttons-to-bottom sweep (user rule: action buttons at the BOTTOM)

Legend: ✅ moved in Phase 3 · ⏸ deliberately left (reason) · ✔ already at bottom.

| Window | Before | Phase 3 |
|---|---|---|
| `JrnWin` | Toolbar top; totals footer bottom | ✅ toolbar moved to bottom bar directly above totals footer |
| `InvoiceWin` | Toolbar in grid row 0 | ✅ toolbar moved to new bottom row under the totals/notes band |
| `CshWin` (receipt/payment) | Toolbar top; totals footer bottom | ✅ above footer |
| `ChqWin` | Toolbar top; totals footer bottom | ✅ above footer |
| `MfgWin` | Toolbar top; cost footer bottom | ✅ above footer |
| `OrdWin` | Toolbar top; totals footer bottom | ✅ above footer |
| `BnkCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `CcCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `CstCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `RepCard` | Toolbar top; metrics bottom | ✅ above metrics bar |
| `CurCard` | Toolbar top; no footer | ✅ bottom bar |
| `BarWin` | Toolbar top; no footer | ✅ bottom bar |
| `BcdGenWin` | Action toolbar top; status line bottom | ✅ action bar moved above status line (prefix input travels with its actions) |
| `BcdDesWin` | Toolbar added first, un-docked (defaulted to left in DockPanel) | ✅ explicitly docked bottom |
| `RptBase` (all 19 report windows) | Refresh/Columns/Export/Print in title row | ✅ moved to a bottom action bar under the status line |
| `MatDirWin` (XAML) | Toolbar border in row 1 (top) | ✅ moved to new bottom row 3, right-aligned |
| `CoaWin` (XAML) | Export/Import/Refresh under search (top) | ✅ moved into the bottom row next to Close/status |
| `CatWin` (XAML) | Refresh in header | ✅ moved into the bottom action bar; New/Edit/Delete/Save/Close were already bottom |
| `AccCard`, `MatCard`, `WhsWin`, `MatGroupWin`, `BinWin`, `BkpWin`, `ColChsWin`, `PrmWin`, `SerWin`, `RestoreCoWin`, `DatWin`, `PosCheckoutWin` | — | ✔ already at bottom (record-navigation arrows in `AccCard`/`WhsWin` headers are navigation, not actions) |
| `MWin` shell | Ribbon + menus | ⏸ exception per instructions (shell) |
| `SplWin`, `Lang`, `LogWin`, `ComWin`, `StpWiz` | Startup flow | ⏸ exception (no top toolbar; primary buttons are in content flow / wizard footer) |
| `PosWin` | Checkout/Clear already in bottom action row of the summary panel; payment methods in totals panel | ⏸ left: retail layout is function-driven; "Add item" stays with the scan/header area cashiers use |
| `CashDrawerOptionsWin` | Save/Test/Close in content-flow panel at the end of the form | ⏸ left: buttons already render last (bottom of the form) |
| `DatWin` Start button | In content flow (starts the import/export operation it configures) | ⏸ left: it is the operation's own execute control inside its card; Close/Details are bottom |
| `ModuleWorkspaceView`, `ReportGridVw`, `DashVw` | Embedded views in the shell, not windows | ⏸ left: header bands belong to the shell workspace layout |
| New: `PayRecWin`, `ProjWin`, `AssetWin` | — | ✅ built with bottom action bars from the start |

## 6. Phase 3 build results (this branch)

### 6.1 JrnWin Print — BUILT (`6e1f71a`)
`PrintJournal_Click` now prints a real A4 voucher page (company header, doc no/date,
narrative, account lines with debit/credit, totals, balance state) through a
`PrintDialog`, following the `InvPrn` invoice print pattern; labels via existing
`Vouchers.*` Lang keys. No schema/service needed (prints the loaded journal).

### 6.2 Payment Reconciliation — BUILT (`df1d3b2`, `6ea07bc`, fix `2b15216`)
- **Schema**: `dba/mig/{sqlite,postgresql}/025_payment_allocations.sql` — new
  `payment_allocations` table (the only schema needed; documents/ledger already
  existed). Model: a posted sales document debits the customer in `party_ledger`,
  a purchase document credits the supplier; money-in is a credit entry (customer)
  / debit entry (supplier). Allocation links a payment entry to an open document.
- **Domain**: `OpenDocumentRow`/`UnallocatedPaymentRow` records +
  `IReconciliationRepository` (`src/ShouTech.Domain`).
- **Infrastructure**: `ReconciliationRepository` — provider-neutral; lists capped
  at 500; `AllocateAsync` runs in ONE transaction and re-reads/re-validates the
  payment balance, document status (Posted), same-party rule, and open amount
  before inserting the allocation and increasing `paid_amount` (the settlement
  source of truth). Over-allocation and double submission are rejected inside
  the transaction (proven by a runtime smoke test against a migrated SQLite DB).
- **Service**: `ReconciliationService` (input validation) in `src/ShouTech.Application`.
- **Window**: `Views/PayRecWin.cs` (BaseWin) — two schedule grids (open documents /
  unallocated payments), party-kind selector + amount in the content area,
  Allocate/Refresh/Close in the bottom command bar, status bar with counts/totals.
- **RBAC**: `ACC.PAYREC.VIEW` / `ACC.PAYREC.ALLOCATE` (PermCat.Accounting), seeded
  by migration 028 with role grants (Admin all, User all, Viewer view-only).
- **Nav**: ribbon `Accounts.PaymentReconciliation` (TabAccounts → Analysis group)
  → `RptNav.OpenPaymentReconciliation`. **Lang**: `PaymentReconciliation.*` in all
  4 files (En complete; Ar/Fr/Tr translated).

### 6.3 Projects — BUILT (`6ea07bc`, `df1d3b2`)
- **Schema**: `026_projects.sql` (both providers): `projects` + `project_tasks`,
  partial unique index on (company, code) among live rows, audit columns,
  soft delete.
- **Domain/Service**: `Project`, `ProjectTask`, statuses; `IProjectRepository`;
  `ProjectService` validates code/name required, budget ≥ 0, end ≥ start, known
  statuses; `ProjectRepository` is transactional with in-transaction uniqueness
  re-checks and company scoping on every statement.
- **Window**: `Views/ProjWin.cs` — project list (main content) + editor in the
  content area + tasks grid/editor below; New/Save/Delete/AddTask/DeleteTask/
  Refresh/Close in the bottom command bar; status bar with count + total budget.
- **RBAC**: `PRJ.PROJECT.VIEW/CREATE/UPDATE/DELETE`, `PRJ.TASK.MANAGE` (migration
  028 grants). **Nav**: ribbon `Projects.Projects` (TabOrders → General group).
  **Lang**: `Projects.*` ×4.

### 6.4 Fixed Assets — BUILT (`6ea07bc`, `df1d3b2`)
- **Schema**: `027_fixed_assets.sql` (both providers): register only —
  depreciation is computed, never stored.
- **Domain/Service**: `FixedAsset`, `DepreciationMethods`, `AssetStatuses`;
  `AssetService` validates (cost ≥ 0, 0 ≤ salvage ≤ cost, life ≥ 1, known
  method/status) and computes schedules: straight-line and double-declining
  balance, final year lands exactly on salvage, accumulated depreciation
  pro-rated by whole months and capped at the depreciable base.
- **Window**: `Views/AssetWin.cs` — register list + editor + computed
  depreciation schedule grid; read-only accumulated/book-value fields; status
  bar with total cost + total book value.
- **RBAC**: `AST.ASSET.VIEW/CREATE/UPDATE/DELETE` (migration 028). **Nav**:
  ribbon `Assets.Assets` (TabAccounts → General group). **Lang**: `Assets.*` ×4.

### 6.5 Permissions seed — migration 028 (both providers)
11 new permissions + role grants, insert-or-update / `ON CONFLICT` idempotent.
Verified on SQLite: fresh migrator run applies, second run SKIPs; with roles
present the grants resolve to Administrator 11 / User 9 / Viewer 3 and are
idempotent on re-run.

### 6.6 NOT built in Phase 3 (deliberate)
- **OrdWin Convert-to-invoice**: blocked on order persistence — OrdWin itself
  has no storage (Save is `ShowPersistenceUnavailable`), so a solid Convert
  cannot exist yet. It is backlog #1 (wire card/voucher/order persistence,
  CcCard first, then Convert via the `document_links` schema).
- **CRM pipeline / Quality (priority 5)**: not started — priorities 1–4 plus
  verification consumed the phase, per the task's own stop rule.

## 6b. How the File Explorer reference was applied (per window type)
- **All swept windows**: the bottom action bar is an Explorer-style command bar
  moved to the bottom — flat `Border` with transparent background, subtle 1px
  top divider (`BorderBrush`), commands right-aligned (mirrored in RTL), sitting
  directly above any totals/status footer. No colored toolbar blocks remain in
  secondary windows.
- **List/manager windows (ProjWin, AssetWin, PayRecWin, card windows)**: the
  list is the main content; details/editing live in the content area; a slim
  status bar at the very bottom shows counts/totals (Explorer status-bar logic).
- **Document windows (JrnWin, InvoiceWin, CshWin, ChqWin, MfgWin, OrdWin)**:
  header fields and line grid keep the content area; the command bar sits above
  the existing totals/balance footer, which stays the last element.
- **Report windows (RptBase)**: Refresh/Columns/Export/Print form the bottom
  command bar under the grid; the totals bar is the footer.
- Surfaces/padding: windows keep the central Surface brushes; new windows use
  12px outer padding and the central implicit control styles. MWin/ribbon were
  NOT redone (separate follow-up, per instructions).

## 6c. The Al-Ameen V10 schedule pattern, as implemented here
Implemented centrally, not per window:
- **RptBase (all 19 report windows)**: strong header row (central DataGrid
  styles), banded rows, sortable + resizable + **reorderable** columns, numeric
  columns right-aligned, date columns centered, and a **totals footer pinned at
  the bottom** (bold, `SurfaceSecondaryBrush`). Reports that supply their own
  Totals dictionary keep them; reports that pass none now get totals computed
  from the bound rows for numeric columns (`RenderTotals` fallback).
- **Exceptions (documented)**: `BalSheetWin` and `IncStmtWin` opt out of computed
  totals (`AllowComputedTotals = false`) — their rows are statement sections and
  subtotals, so summing every row would double-count. Editable line grids
  (JrnWin/InvoiceWin) keep sorting/reordering off to preserve document line
  order; they already carry bottom totals footers.
- **New windows**: ProjWin/AssetWin/PayRecWin grids use the same pattern
  (sortable/resizable/reorderable, numeric right-aligned, dates centered,
  standard context menu via `DataGridTableBehavior`, totals in the status bar).
- Print/Export stay reachable from the RptBase bottom action bar.

## 6d. Heavy-duty checklist (per shipped item)
| Item | Transactions | Validation | RBAC | Audit | Migrations (SQLite+PG) | Tests |
|---|---|---|---|---|---|---|
| Payment allocations | Single tx; balances re-read + re-validated inside tx; rollback on failure | Service + repository (amount > 0, Posted doc, same party/kind, open & available caps) | `ACC.PAYREC.VIEW/ALLOCATE`, checked in window + Allocate handler | `allocated_by`/`allocated_at` on every allocation row | 025 both providers, idempotent (double-run proven) | Service validation tests; runtime smoke test incl. over-allocation rejection |
| Projects + tasks | Single tx per save; in-tx uniqueness + parent checks | Service (code/name, budget ≥ 0, dates, statuses) + partial unique index | `PRJ.*` checked on open/save/delete/task actions | created_by/at, updated_by/at columns; soft delete only | 026 both providers | Service validation + round-trip tests; repository smoke test |
| Fixed assets | Single tx per save; in-tx uniqueness check | Service (cost/salvage/life/method/status) + CHECK constraints | `AST.*` checked on open/save/delete | created_by/at, updated_by/at; soft delete only | 027 both providers | Depreciation math tests (SL, DB, pro-rata, caps) + validation tests |
| Permissions 028 | Migrator per-file tx | Insert-or-update / ON CONFLICT | — (it IS the RBAC seed) | — | 028 both providers; grants proven 11/9/3, idempotent | Migrator double-run + grant re-run check |
| JrnWin Print | N/A (read-only print) | Prints only a loaded journal | Existing journal perms apply | N/A | N/A | Build-verified; visual check needs Windows run |
| Buttons sweep + RptBase schedules | N/A (layout) | N/A | N/A | N/A | N/A | Build + XAML parse verified |

Notes: `dotnet test` cannot execute on this Linux VM (testhost targets
net8.0-windows) — the test project compiles green and must be run on Windows.
PostgreSQL migrations are written to the same patterns as the existing PG set
and parse-checked, but only SQLite was executed here (no live PostgreSQL).

## 7. Remaining backlog (priority order)

1. Wire persistence for the 🟡 card/voucher windows (`CcCard` first — it has no
   DB wiring at all; then `BnkCard`, `CurCard`, `RepCard`, `CshWin`, `ChqWin`,
   `MfgWin` save paths) and `OrdWin` Convert-to-invoice using `document_links`.
2. `JrnWin` / `ChqWin` / `OrdWin` Print via the existing reporting engine
   (`RptRend` / `InvPrn` patterns) instead of `ShowOperationUnavailable`.
3. CRM opportunity pipeline window (+ activity logging).
4. Quality management window (inspections + non-conformance).
5. Budgets window + budget-vs-actual report.
6. Batch/serial + valuation engine (FIFO/Moving Average) and stock ledger audit.
7. MRP planning + job cards for manufacturing.
8. Bank statement import (CSV/MT940) with auto-matching; Kuwait e-invoicing.
9. Payroll & leave (HRMS parity).
10. AI Report Copilot (per `docs/UI_2030_AI_REPORT_ASSISTANT.md`).
11. Maintenance, Support/Helpdesk, Subcontracting (low priority, offline desktop).
