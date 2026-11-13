# ShouTech ERP — ERPNext Feature-Parity Roadmap (Offline Desktop)

Goal: reach functional parity with [ERPNext](https://github.com/frappe/erpnext) as an **offline-first Windows desktop ERP**, and outperform **Al-Ameen ERP v10** in the SMB market.

**Deployment model (target):** ShouTech ERP is an **offline desktop application**. The complete business workflow must run locally without internet access. SQLite is the standard standalone database; PostgreSQL may be supported for customer-controlled on-premise installations where explicitly configured. No cloud dependency, mandatory online activation, or web-hosted workflow is required for normal operation. Licensing and backup must remain usable offline.

> **Reference:** ERPNext is a web ERP (Python/Frappe, ~41k stars, actively updated). Its module list below was taken from the current `develop` branch module folders.

---

## 1. Legal Boundary — Read First

ERPNext is licensed under **GPL-3.0**. ShouTech ERP is proprietary.

- **Allowed:** studying *concepts* (workflows, document states, report definitions) and re-implementing them in our own C#/WPF/SQLite code.
- **NOT allowed:** copying ERPNext source code, schemas, or UI assets into this repository. That would force the whole product to GPL and destroy the proprietary license.
- All parity work must be clean-room: write from scratch against our own `src/ShouTech.*` layers and `dba/` schemas.

---

## 2. ERPNext Module Map vs ShouTech Coverage

ERPNext current modules: accounts, assets, buying, crm, edi, maintenance, manufacturing, projects, quality_management, selling, stock, subcontracting, support (helpdesk), telephony, shopping_cart/portal (website).

| ERPNext module | What it does | ShouTech today | Gap |
|---|---|---|---|
| **Accounts** | GL, AP/AR, payment reconciliation, taxes, budgets | GL, chart of accounts, journals, vouchers, periods, year close | Payment reconciliation & budgets missing |
| **Stock** | Multi-warehouse, batches, serial nos, valuation (FIFO/Moving Avg), stock ledger | Inventory core, warehouses, movements, barcodes | Batch/serial tracking, valuation methods, stock ledger audit |
| **Selling** | Quotations, SO, delivery notes, pricing rules | Orders, customer pricing | Quotation→SO→Delivery→Invoice chain incomplete |
| **Buying** | RFQ, PO, receipt, purchase invoices | Purchase invoices | RFQ/PO/receipt chain missing |
| **CRM** | Leads, opportunities, activities | CRM folder, leads, sales reps | Opportunity pipeline & activity logging |
| **Manufacturing** | BOM, work orders, MRP, job cards | BOM, production orders | MRP planning, job-card tracking |
| **Projects** | Projects, tasks, timesheets | — | Missing |
| **Assets** | Asset register, depreciation | — | Missing |
| **Quality Management** | QI, non-conformance | — | Missing |
| **Maintenance** | Asset maintenance visits | — | Missing (low priority) |
| **Support / Helpdesk** | Tickets, SLA | — | Missing (low priority for offline desktop) |
| **Subcontracting** | Subcontract POs | — | Missing |
| **EDI / Integrations** | E-invoicing, bank sync, shipping | Outbox webhooks only | Bank statement import, e-invoice (Kuwait NIC/PACS) |
| **Portal / Website** | Self-service portal, e-commerce | — | **Not applicable** — desktop-first is our positioning, not a gap |
| **HRMS** (separate repo) | Payroll, leave, attendance | HR folder (basic) | Payroll & leave |
| **POS** | POS | POS | Exists — parity OK |

---

## 3. Why ShouTech Beats Al-Ameen ERP v10

Positioning advantages to protect and advertise:

1. **Hybrid offline-first** — full standalone operation without internet; when a server is available, the same client connects for multi-user sync, encrypted cloud backup, and outbox webhooks. Al-Ameen v10 has no comparable online↔offline continuity.
2. **Modern stack** — .NET 8 / WPF / MVVM vs legacy WinForms-era code; dark mode, 4 languages (Ar/En/Fr/Tr) with proper RTL.
3. **Clean architecture path** — Domain/Application/Infrastructure split (Al-Ameen is monolithic).
4. **Auditability** — SHA-256-checked migrations, audit trail, RBAC with 209 permission keys, native C++ security engine.
5. **Per-fiscal-year database isolation** — superior to flat-file yearly archives.
6. **Licensing model** — per-user activation; server-connected deployments validate licenses server-side, offline machines enforce via **hardware dongle**.

---

## 4. Phased Roadmap

### Phase 1 — Close the accounting/inventory gaps (core ERP credibility)
- Payment reconciliation (open invoice matching against receipts/payments)
- Stock valuation methods (FIFO, Moving Average) + permanent stock ledger
- Batch & serial number tracking
- Quotation → Sales Order → Delivery Note → Invoice document chain with statuses (Draft/Submitted/Cancelled — ERPNext-style document workflow, re-implemented)
- RFQ → PO → Purchase Receipt → Purchase Invoice chain

### Phase 2 — Differentiators Al-Ameen lacks
- Asset register with depreciation schedules posting to GL
- Projects & timesheets
- Bank statement import (CSV/MT940) with auto-matching
- Kuwait e-invoicing (NIC/PACS) clearance integration via outbox worker
- MRP for manufacturing
- FastReport .NET report pack (currently "planned")

### Phase 3 — Polish & scale
- CRM opportunity pipeline with activities
- Quality management + subcontracting
- Payroll & leave (HRMS parity)
- Install/update framework (clickonce/MSIX) — ERPNext's weakness offline is deployment; ours must be one-click
- **Server mode (when ready):** central PostgreSQL server + sync service for multi-branch/multi-user; the WPF client must switch transparently between local SQLite and server PostgreSQL (both providers already exist in `ShouTech.Infrastructure`)
- **Dongle licensing (when ready):** SecEng dongle check at startup with grace period; server deployments validate licenses server-side so thin clients need no dongle

### Engineering rules for every phase
- Feature-complete = schema (`dba/mig`) + service + WPF view + RBAC permission key + tests (`Kern.Tests`) + docs entry.
- No new "legacy-style" code in `cod/`; all new features go through `src/ShouTech.Domain|Application|Infrastructure`.
- Journal reversal must create compensating entries (known caveat in ARCHITECTURE.md) before Phase 2 accounting features ship.

---

*Created from an ERPNext parity review on 2026-11-10. Updated same day: hybrid server mode + dongle licensing added to the target deployment model.*
