# ShouTech ERP — Modern Functional Specification (vibe branch)

**Supersedes:** the Al-Ameen **10** functional guide (Arabic help file, SyrianSoft) as the reference feature bar for ShouTech ERP. This document re-states that bar in modern terms and raises it to 2026 standards, aligned with the existing `ERPNEXT_PARITY_ROADMAP.md`, `SECURITY_ENTERPRISE.md` and `UI_2030_AI_REPORT_ASSISTANT.md`.

## 1. Guiding principles (upgraded from Al-Ameen 10)

| Al-Ameen 10 concept | ShouTech 2026 equivalent |
|---|---|
| Windows desktop client + SQL Server / MSDE network install | Offline-first WPF (SQLite) + optional central PostgreSQL server mode, single codebase |
| Dongle ("hardware key") licensing | Hybrid licensing: server-side validation when connected; SecEng dongle with grace period when offline (per ERPNEXT_PARITY_ROADMAP §3) |
| Arabic-first overlapping Windows UI | Modern WPF/MVVM, 4 languages (Ar/En/Fr/Tr), real RTL, dark mode, window proportions/context persistence (see WINDOW_* docs) |
| Company/branch/year databases | Per-fiscal-year DB isolation + multi-company, multi-branch, RBAC (209 permission keys) |
| Menu + CHM help | In-app contextual help + docs/ as single source of truth |

## 2. Functional bar (must-have, target of Phase 1 parity)

1. **General Ledger**: chart of accounts, journal entries with full audit trail, linked compensating reversals (`reversal_of_entry_id` already done), fiscal periods + year close (done), payment reconciliation and budgets (missing).
2. **Sales chain**: Quotation → Sales Order → Delivery Note → Invoice with Draft/Submitted/Cancelled workflow states; payment receipts with open-invoice allocation.
3. **Buying chain**: RFQ → PO → Purchase Receipt → Purchase Invoice → supplier payment.
4. **Inventory**: multi-warehouse, item classes, multi price levels, min/max levels, barcodes; **add** batch/serial tracking, FIFO and Moving-Average valuation, permanent stock ledger (missing).
5. **Invoicing/tax**: multi-currency with revaluation and FX differences; configurable tax schemes ready for Kuwait e-invoicing (NIC/PACS) clearance via outbox worker.
6. **Reports**: financial statements (TB, BS, P&L, aging), warehouse and stock-ledger reports, FastReport .NET pack, export to PDF/XLSX with export governance audit (done).

## 3. Modern raises (beyond Al-Ameen 10)

- **Security (extends SECURITY_ENTERPRISE.md backlog):** enforce password history (`PreventReuseCount`) and expiry (`ExpirationDays`) with a change-password flow; ship TOTP 2FA (config exists, unimplemented); throttle per-account passcode brute force; push key (1) and (5) backlog items before server mode release.
- **Server mode:** central PostgreSQL + sync service; outbox webhooks stay at-least-once with HMAC signing (done); conflict rules must be defined per document type before enabling multi-branch sync.
- **Offline continuity:** the same dataset opens standalone or connected, no migration step; encrypted cloud backup with restore drill.
- **Bank integrations:** CSV/MT940 statement import with auto-matching against open invoices.
- **AI report assistant:** per UI_2030_AI_REPORT_ASSISTANT.md — natural-language report queries over the local dataset only (no cloud data egress by default).
- **Platform/quality:** .NET 8, zero-warning builds (ZERO_WARNING_CLEANUP), DBA schema ownership audit kept green, every feature = schema + service + WPF view + RBAC key + tests + docs entry.
- **Licensing:** per-user activation; dongle fallback validated at startup with grace period.

## 4. Milestones (acceptance-ready)

- **M1 — Core ERP credibility:** sales + buying chains, reconciliation, valuation, batch/serial, stock ledger.
- **M2 — Differentiators:** assets + depreciation, projects/timesheets, bank import, Kuwait e-invoicing clearance, MRP, report pack.
- **M3 — Scale & polish:** payroll/leave, CRM pipeline, quality/subcontracting, MSIX installer, server mode GA after conflict-rule review, 2FA GA.

## 5. Traceability

Every section above maps to an existing doc: accounting (ACCOUNTING_*), security (SECURITY_ENTERPRISE.md, sec.md), architecture (arc.md, ARCHITECTURE.md), UI (WINDOW_*, UIchart.md, QUICK_ACCESS_BAR.md), migration/English-only (DATABASE_MIGRATIONS.md, ENGLISH_ONLY_*). No Al-Ameen code or assets are used — concepts only, clean-room.
