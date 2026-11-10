# ShouTech ERP — Modern UI 2030 & AI Report Assistant

Target: a friendly, modern desktop experience that feels current in 2030 and beyond, with an AI assistant that helps users build professional reports.

## 1. UI Direction (WPF, MVVM)

**Visual language**
- Fluent Design / WPF UI (open-source Fluent 3 library for WPF): rounded 8px corners, acrylic/mica-style window backdrops, subtle elevation, 60/30/10 color rhythm.
- Dark mode by default with Light mode; true black OLED option.
- Dynamic accent color + tinted controls; 4-language RTL/LTR (Ar/En/Fr/Tr) with automatic flow-direction switching and mirrored navigation.
- Typography: Segoe UI Variable (Win11+) with graceful fallback; 14px body, high contrast ratios (WCAG AA+), user-scalable UI zoom 100–200%.

**Interaction**
- Command palette (Ctrl+K): fuzzy search across windows, documents, customers, and actions.
- Zero-step navigation: Ribbon retained for accountants (Al-Ameen familiarity) but a modern left NavView with collapsible icon rail is primary.
- Smart forms: inline validation, auto-complete from history, keyboard-first entry with Enter-to-next, and quick-add popovers for customers/materials mid-entry.
- Live dashboards: KPI cards, sparklines, and drill-down lists on the home screen (sales today, receivables, low stock, cash position).
- Skeleton loaders (never blocking spinners), optimistic saves, undo toasts with 8s action window.
- Full offline operation by default; server sync status as an ambient badge, never a dialog.

**Accessibility & trust**
- Full keyboard map, screen-reader (UIA) annotations, reduced-motion mode.
- Audit-visible actions: every posting shows a one-line plain-language summary before confirm ("This will issue 10 PCS of MAT-42 from WH-1 and debit customer X by 250 SAR").

## 2. AI Report Assistant ("Report Copilot")

**Principle:** AI assists, the user approves, the audit trail records. AI never posts, never writes to the ledger, and runs only over data the user is already authorized to see (RBAC-filtered).

**Online mode (server/optional):**
- Natural-language report building: "sales by customer group for Q3, grouped monthly, with margin" → assistant returns a structured report spec (title, filters, columns, grouping, chart type) that the user reviews and runs.
- Narrative summaries: generated plain-language insight paragraphs for report packs (trends, outliers, receivable aging).
- Translate/polish: reword report titles and notes in the 4 supported languages.

**Offline mode (default, no internet required):**
- Template library: curated professional report specs (sales analysis, aging, stock turnover, P&L, top customers) with saved user presets.
- Deterministic "explain this report" text generated from the data, not from a model.
- When a server or cloud AI endpoint is configured, copilot features upgrade automatically; without one, the UI shows the template experience without error states.

**Architecture (clean, in `src/`):**
```
MainApp (WPF) → ReportCopilotViewModel
   → ShouTech.Application/Services/ReportAssistantService
        → ReportSpecFactory (offline templates + presets)
        → IReportNarrativeProvider (pluggable: cloud AI adapter / deterministic local)
        → ShouTech.Infrastructure query layer (RBAC-scoped)
```
- `ReportSpec` is a versioned JSON contract (id, title, filters, columns, grouping, sort, chart, permissions).
- Every AI-generated spec is stamped: model/endpoint used, prompt hash, timestamp, user; specs are stored and auditable.
- Execution always goes through the same validated report engine as human-built reports — one code path.

## 3. Delivery Phases

1. **UI foundation:** WPF UI Fluent theme, dark/light, NavView + command palette, UI zoom, RTL polish.
2. **Home dashboard + report engine spec (`ReportSpec` + template library).**
3. **Copilot online adapter (server mode) + audit stamping; narrative summaries.**
4. **Sell the difference:** one click from "question" to "professional bilingual report" — Al-Ameen v10 and ERPNext both require manual report building.

*References: docs/ERPNEXT_PARITY_ROADMAP.md (Phase 3 polish), issue #7 (selling chain UI).*
