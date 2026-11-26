# Phenix v10 evidence — owner-shared training playlist (2026-11-24)

Source: the owner's Phenix v10 training-course playlist
("دورة تدريبية في شرح استخدام ومميزات أنظمة فينيكس الاصدار 10"),
16 video titles visible. Titles are feature EVIDENCE (the
capability exists in Phenix v10); screen-level detail is not
claimed. Recorded for phase comparisons and the
commercial-phase study — NOT a scope expansion of the current
branch (owner instruction).

| Phenix v10 capability (from titles) | ShouTech status |
|---|---|
| Invoice payment from customer LOYALTY POINTS, without generating/printing a purchase voucher | Loyalty module exists (LoyaltyWin; loyalty_accounts/entries, migration 036). Points-as-payment tender on invoices/POS: NOT verified as wired — commercial-phase study item |
| Chart-of-accounts tree + account management | DONE — CoaWin tree + AccCard in-chart (R2e, Al-Ameen 2004 structure, modern visuals) |
| Inventory guide: material definition → profitability analysis | DONE — MatCard/MatDirWin + ItemProfitWin/InvoiceProfitsWin |
| Wholesale fabric-shop system (vertical) | Vertical packaging — commercial-phase consideration, not a feature gap per se |
| Property management & project revenues | ProjWin (projects) exists; dedicated property management — not present; vertical, commercial-phase consideration |
| Manufacturing + cost calculation | DONE — MfgWin family (built in the fix/build phase) |
| PAYMENT TERMINAL integration (A90 PRO, Alhamrani) with Phenix v10 | NOT present — candidate gap for the commercial-phase study (hardware integration; region-specific device) |
| Manufacturing order window | DONE — MfgWin |
| Restaurant system / restaurant POS | NOT present — already a named gap in docs/POS_FAMILY.md |
| REMOTE approval of permissions (approval from outside the office) | Approval workflows exist in-app (document approval states, W23); REMOTE (off-LAN) approval — not present; candidate for the commercial-phase study (would ride the licensing/online story; offline-first tension recorded) |
| HR leave definitions | DONE — HrLeaveWin family (built in the fix/build phase) |
| Material card + SERVICE card | DONE — MatCard with the service-material rule (sellable/purchasable, never stocked) enforced across consumers |
| New-company creation + post-creation steps | DONE — StpWiz + ComWin |

Use rule: phase studies cite this file for Phenix v10
capability claims instead of re-deriving them; items marked
as candidates are weighed in the commercial-phase study with
the owner's power + ease criterion — they do not enter the
current branch's scope silently.

## Annex 2026-11-24 17:47 — owner's own Phenix install (VERIFIED verbatim)

Source: five screenshots from the owner's Phenix install (company
البيت السوري), transcribed verbatim at
`~/workspace/research_notes/phenix-tools-tab-inventory-20261124-1335/notes/screenshot-menus-pos-20261124.md`.
This closes the item-by-item gap the web research could not fill.
Status column = ShouTech today; GAPs are recorded for the
commercial-phase/comparison backlog — they are NOT scoped into the
bin/numbering workstreams.

### «أدوات» (Tools)
| Phenix item | ShouTech status |
|---|---|
| Calculator / on-screen keyboard | OS accessories — not ERP scope |
| Options (خيارات) | DONE — OptWin |
| Maintenance wizard | DONE — maintenance windows (services phase) |
| Backup | DONE — BkpWin |
| Language | DONE — ribbon language switch (live re-mirror) |
| Change password | Covered by user administration |
| Movement recorder (مسجل الحركات) | DONE — ActivityLogWin |
| Program add-ons | AddonsWin — partial BY DESIGN (licensing phase) |

### «أدوات أخرى» (Other Tools)
| Phenix item | ShouTech status |
|---|---|
| Rollover (التدوير) | DONE — RolloverWin |
| Permissions | DONE — permissions windows |
| **Permission TEMPLATES (قوالب الصلاحيات)** | **GAP — named** |
| **Glossary (المصطلحات)** | **GAP — named (terminology dictionary)** |
| Custom fields | Partial — definition group awaits its engine (ribbon audit (b)) |
| UoM definitions | DONE — UomWin |
| Payment-method definitions | Partial — definition group (ribbon audit (b)) |
| Material-spec definitions | Partial — definition group (ribbon audit (b)) |
| **DELIVERY family: driver card, delivery prices, pickup orders, delivery search, driver cash-box closing report** | **GAP — named family (delivery prices partially defined; the rest absent)** |
| External tools | GAP — named (external-program launcher; security-sensitive, low priority) |
| **DOCUMENT AUDIT (تدقيق المستندات)** | **GAP — named (document-level audit/verification; distinct from the movement recorder)** |
| **Automated-report options (خيارات التقارير الآلية)** | **GAP — named** |
| **TASK SCHEDULING (جدولة المهام)** | **GAP — named** |
| **Helper phrases (قائمة الجمل المساعدة)** | **GAP — named (reusable note phrases)** |

### «التصنيع» (Manufacturing) — comparison set for MfgWin + reports
Menu is report-heavy: manufacturing models, manufacturing process,
manufacturing movement — present (MfgWin family). **Named GAPs**:
the four cost-variance reports (manufactured materials / raw
materials / additional costs / additional costs on manufactured),
manufactured-material costs report (partial), feasibility
(إمكانية التصنيع — MRP-lite covers part), manufacturing search
(partial), and **RE-POSTING of manufacturing operations
(إعادة ترحيل عمليات التصنيع) — GAP**.

### POS screen structure (evidence for the POS comparison; our Win11 surface stays)
7-column grid (#, material code, material, qty, unit, price, total);
16-button function rail (invoice discount, cancel invoice, sales
return, open drawer, add customer, reprint previous invoice, info
window, switch user, price check, edit qty, advanced material search,
protection screen, close cashbox, print barcode, material movement,
item card); action stack (choose customer/rep, swap material, print
GIFT invoice, cancel item); totals block (total qty, line discount,
invoice discount, lines total); keypad; tenders as first-class
buttons: cash, ON-ACCOUNT (أجل), credit card, K-net, Visa/Master,
total F6. Invoice number auto-increments visibly (74865 → 74866) —
live confirmation of the auto-numbering rule.

### «تعريف نقاط البيع» (POS terminal definition) — QUEUED comparison
Owner-confirmed 2026-11-24 17:49 (transcription §6 of the notes
file). A PosTermWin comparison + gap-build step is QUEUED after the
numbering workstream (owner directive 17:50). Field verdicts:
- Latin name — COVERED (terminals carry name_ar/name_en).
- **Per-point DOCUMENT-TYPE binding** (out-document = sales invoice
  type; returns document type) — **GAP, to build**.
- Cash account — COVERED; **separate cashbox-CLOSING account —
  GAP, to build**.
- **Per-point currency — GAP, to build**.
- Customer GROUP scoping vs our default customer — weigh in the
  queued step, do not blindly copy.
- Touch-screen properties + per-point POS-field customization —
  GAP, scope weighed in the queued step.
- **Per-point print designs (sale / return / GIFT) — GAP, to build
  by binding doc_designs (.stid)**, not a parallel mechanism.
- Tabs قائمة المهام / قسيمة شراء — contents UNEXAMINED; recorded,
  not invented.
- **Ours ahead** (recorded): warehouse binding, bank/card account,
  cash drawer, printer name, price level, permitted users — none
  visible on the Phenix tab.

