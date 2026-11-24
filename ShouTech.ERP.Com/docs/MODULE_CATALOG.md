# ShouTech Module Catalog

This catalog is the developer-facing map of business modules and their required boundaries. It is intentionally conservative: a module's presence in the source tree does not prove its workflow is complete.

## Status vocabulary

- **Foundation present:** related code or schema is visible; end-to-end behavior is not yet certified.
- **Partial / verify:** some workflows exist, but acceptance coverage is incomplete or unknown.
- **Gap / not evidenced:** current architecture and inventories do not establish a working implementation.
- **In progress:** implementation is actively being changed on the development branch.
- **Verified:** defined acceptance tests passed for the stated scope and providers.
- **Done:** all applicable Definition-of-Done criteria in [Developer Roadmap](DEVELOPER_ROADMAP.md) are met.

Update the status only with source and test evidence. Do not infer completion from file, class, table, menu or window counts.

## Module map

| Module | Primary business responsibility | Expected integration points | Key acceptance evidence |
|---|---|---|---|
| Platform and company setup | Company identity, periods, currency, language, DB setup and isolation | Security, accounting, localization, migrations | Company provisioning, database isolation, authorized user access, backup and restore |
| General ledger | Chart, journals, posting, periods, closing and statements | Sales, purchasing, inventory, POS, tax, assets, payroll, bank | Balanced journals, source links, reversal integrity, trial balance and statements |
| Accounts receivable | Customer balances, invoices, receipts, credit notes and ageing | CRM, sales, tax, cash/bank, GL | Customer ledger reconciles to control account; allocations and ageing correct |
| Accounts payable | Supplier balances, bills, payments, debit notes and ageing | Purchasing, inventory, tax, cash/bank, GL | Supplier ledger reconciles to control account; payment allocation correct |
| Sales | Quotes, orders, delivery, invoices, returns and customer pricing | CRM, inventory, tax, AR, GL, POS | Full and partial lifecycle, source links, stock and accounting reconciliation |
| Purchasing | Requisitions, supplier quotes, orders, receipts, bills and returns | Inventory, AP, tax, GL, approvals | Ordered/received/billed matching, partial receipts, landed costs and returns |
| Inventory and warehouses | Item master, units, barcodes, locations, transfers, counts and valuation | Sales, buying, POS, manufacturing, GL | Quantity/value reconciliation, deterministic costing, reservation and traceability |
| POS | Checkout, tenders, shifts, cash drawer, returns and offline recovery | Sales, inventory, AR, tax, GL, payment providers | Idempotent sale/refund, session close, tender and stock/GL reconciliation |
| Manufacturing | BOM, routing, planning, work orders, WIP and costing | Inventory, purchasing, quality, maintenance, GL | Material/output/WIP/cost/variance reconciliation |
| CRM | Leads, opportunities, activities, customer interactions and pipeline | Sales, support, projects, communications | Conversion without duplicate data; company-scoped activity history |
| Projects and service | Tasks, time, expenses, service cases, billing and SLA | CRM, HR, inventory, AR, GL | Project profitability and service SLA drill-through |
| Fixed assets | Acquisition, depreciation, transfer, disposal and impairment | AP, GL, maintenance, tax | Asset register and depreciation schedules reconcile to GL |
| Cash and banking | Cash accounts, bank import, matching, reconciliation and cheques | AR, AP, POS, GL, FX | Statement-to-ledger reconciliation and traceable unmatched items |
| HR and payroll | Employee records, attendance, leave, payroll and statutory deductions | Security, country rules, GL, cash/bank | Reproducible payroll, approval/lock, privacy and balanced payroll journal |
| Tax and compliance | Company opt-in, tax codes, rates, treatment, reporting and e-invoicing | Company setup, sales, purchasing, GL, localization | No activation without explicit consent; effective-dated rule and return reconciliation |
| Reporting and analytics | Operational/financial reports, dashboards and drill-through | All business modules and permissions | Parameterized queries, correct totals, authorization and source lineage |
| Data exchange and integrations | Import/export, APIs, webhooks and external adapters | All modules, security, audit, background worker | Staging preview, schema validation, idempotency, retries and audit |
| AI assistance | Retrieval, extraction, forecasts, anomaly detection and approved actions | Permissions, reporting, document store, audit | No permission bypass, human approval, provenance, isolation and safe failure |
| Security and administration | Users, roles, permissions, audit, licensing and system controls | All modules | Service-level enforcement, least privilege, audit coverage and isolation |

## Architectural ownership map

The current repository is transitional. Follow actual references and runtime paths before moving code.

| Area | Current repository location | Developer rule |
|---|---|---|
| WPF application | `cod/app/src/` | Existing live UI, navigation, views, services, reporting and localization |
| Inventory core | `cod/cor/` | Trace callers before modifying or duplicating core stock behavior |
| Domain layer | `src/Domain/` | Provider- and UI-independent business concepts |
| Application layer | `src/App/` | Use cases and orchestration; avoid new presentation dependencies |
| Infrastructure layer | `src/Infra/` | Provider-backed persistence and technical adapters |
| Database migrations | `dba/mig/sqlite/`, `dba/mig/postgresql/` | Sole home for active schema evolution |
| Background service | `svc/` | Durable background processing and outbound delivery |
| Migration CLI | `tools/SqlMigrator/` | Apply and verify provider migrations |
| Managed tests | `tests/Kern.Tests/` | Automated domain, application, integration and regression tests |
| Native security | `sec/` | Separate native build/test lifecycle; verify packaging integration |
| Localization | `cod/app/src/Resources/` | Resource-driven text; English is default |
| Documentation | `docs/` | Design, architecture, module status, standards and release evidence |

## Module change checklist

For each module change, record:

1. Owning UI entry point and navigation/Ribbon location.
2. Application service/use case and authoritative business rules.
3. Repository and company-scope enforcement.
4. SQLite and PostgreSQL schema/migration impact.
5. Related modules and side effects (GL, stock, tax, cash, notifications).
6. Permission keys and audit events.
7. Localization/resource keys and RTL behavior.
8. Unit, integration, migration and end-to-end tests.
9. Operational logs, failure recovery and support diagnostics.
10. Roadmap status and linked PR/CI evidence.

## Current status policy

This catalog does not assign unverified completion percentages. The detailed evidence and gaps are tracked in the existing [ERP Process Parity Matrix](COMPETITIVE_GAP_ASSESSMENT_2026-11-21.md), [Window Inventory](WINDOW-INVENTORY.md), [Ribbon Completion Inventory](RIBBON_COMPLETION_INVENTORY.md), and the relevant feature status documents. Update those inventories when a feature is verified.
