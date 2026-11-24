# OptWin per-section completion — study & comparison (2026-11-24)

Governing standard (owner, 2026-11-24 19:46): **"هذه النافذة تتحكم بكل
البرنامج" — the Options window controls the whole program.** Every
setting built here must therefore name the program-wide consumer it
governs (the exact engine / window / behaviour), and its proof targets
the consumer effect, not only the setting's persistence. Where a
behaviour exists today but is hardcoded or scattered, the setting
**wires that behaviour to the central control** instead of adding a
parallel control. Where the governed behaviour does not exist in
ShouTech at all, the field is **not** built (no fake fields — owner
rule, 2026-11-24 16:43) and the missing subsystem is surfaced as a
program-level gap in §2 so the owner can prioritise building it.

IP rule (owner, 2026-11-24 19:49): functional scope and logic come
from the Phenix evidence; the **form** is our own original design in
the Win11 central system (category rail + section pages, slate
accent, bottom action bar — the OptWin built in phase B2), with
deliberate divergence in arrangement wherever a Phenix arrangement
would otherwise be mirrored. Divergences are noted per section.

Evidence: the owner's Phenix screenshots transcribed in
`~/workspace/research_notes/phenix-tools-tab-inventory-20261124-1335/notes/options-sections-20261124.md`
(19 rail categories photographed) plus the same file's bonus windows
(permission templates, payment-method definitions, POS-definition
«قائمة المهام» tab). Sections the owner has **not** photographed —
الخيارات العامة، الحسابات، إضافي الفواتير، النسخ الاحتياطي — are
marked **awaiting owner evidence** throughout; nothing is guessed
into them, and our existing Backup section stays exactly as built
until evidence arrives.

Status legend, per field:
**B** exists + bound to a real consumer · **U** exists but unbound
(stored, nothing reads it — a defect this study fixes or removes) ·
**M** missing, consumer exists → **build** · **A** absent-with-reason
(no consumer / no governed subsystem; never a dead toggle) ·
**D** deferred to a named later landing (substrate exists, scope
exceeds this workstream) · **W** awaiting owner evidence.

Stores used, by nature of the setting (no new store invented):
per-user presentation → `UsrPref` (existing JSON, per user+machine);
company-wide behaviour → the company database (existing precedents:
`pos_settings` mig 066, `company_tax_settings` mig 067,
`document_number_sequences` mig 123); company identity → the
`companies` row / company profile (`CoLocalStore` + DB). New schema
starts at migration **125**, both dialects, idempotent, as always.

---

## 1. Rail comparison — ours vs Phenix

Phenix rail (19, photographed order): الخيارات العامة · خيارات
متقدمة · الحسابات · إضافي الفواتير · الطباعة · النسخ الاحتياطي ·
خيارات المستخدم · الباركود · خيارات التصدير · التصنيع · مسجل
الحركات · فواتير المبيعات النقدية · الأرقام والتاريخ والوقت ·
إعدادات الخط · نقاط البيع · الملحقات · خيارات الرسائل النصية ·
البريد الالكتروني · التوصيل.

Our rail after this workstream (our own arrangement — grouped
presentation → formats → output → operations, not Phenix's order):
General (W — kept as built) · Appearance · Behavior · Session ·
**Font** (new, §3.1) · **Numbers, date & time** (new, §3.2) ·
**Printing** (expanded, §3.3) · **Barcode** (new, §3.4) ·
**Point of sale** (expanded, §3.5–3.6) · Numbering (ours since mig
123 — Phenix has no equivalent section; series live inside each
Phenix document window instead. Ours is the stronger pattern:
one central series table) · Backup (W — kept as built) · Layouts.

Phenix sections with **no ShouTech section**, by ruling: Advanced
(§3.7 — its guardrails live in our engines, per rule, not in a
miscellaneous page) · User options (§3.8 — deferred landing) ·
Movement recorder (§3.9 — deliberate divergence: always-on audit) ·
Cash sales invoices (§3.6 — folded into our POS section; one POS
control surface, not two) · Export (§3.10) · Manufacturing (§3.11) ·
Accessories (§3.12 — peripherals; the one evidenced item with a
consumer, POS receipt header/footer text, is built inside our POS
section) · SMS (§3.13) · Email (§3.14) · Delivery (§3.15) ·
Restaurant / Tables (not in the photographed rail; recorded absent
in SVC1 — no restaurant module exists) · Accounts (W) ·
Invoice extras (W) · General (W).

## 2. HEADLINE — program-level gaps the evidence exposes

These are Phenix **controls whose governed subsystem does not exist
in ShouTech**. No toggle is built for any of them (no fake fields);
they are listed here, at the top, because the owner — not this
workstream — prioritises building the subsystems. When a subsystem
is built, its Options section arrives with it.

| # | Missing control surface | Governed subsystem that is missing | Substrate that already exists |
|---|---|---|---|
| G1 | SMS options (provider, sender name, templates, send-on-receipt/close) | An SMS **transport**. Nothing in the program can send an SMS today. | `outbound_messages` outbox (mig 006, channels EMAIL/SMS/WEBHOOK, queue + retry) managed by OperationsService — a transport landing plugs into it. |
| G2 | Email options (SMTP/Outlook, send-on-receipt, report attachments) | An email **transport + send points** in the document flows. | Same outbox. **Also blocked on the owner's open quotation-email decision** — recorded, not invented. |
| G3 | Delivery options (approvals system, driver coordinator, GPS account) | Delivery **dispatch** (approval workflow, coordinator role, GPS tracking integration). | Delivery definition entities exist (Definitions.cs dedicated-module block); driver/delivery windows were already in the commercial backlog from the Tools-menu evidence. |
| G4 | Accessories: customer display (VFD), PBX/caller-id, modem, data collector | **Device integrations** — no consumer code exists for any of these devices. | Cash drawer only (CashDrawerService, built). Barcode reader needs no config (keyboard wedge). |
| G5 | Weighing-scale options (type, COM port, baud, weight factor) | The **scale protocol** itself. | **Owner's open decision** (weighing-scale protocol, sales-cycle list). Settings without the protocol would be fake. The composite-barcode landing (§3.4) covers scale-*printed* labels, which need no device. |
| G6 | Net-amount rounding at POS (جبر المبلغ الصافي — beneficiary customer/company/nearest) | **Cash-rounding posting**: a rounding-difference account and GL treatment for the rounded difference. Display-only rounding would make till totals disagree with the GL — refused. | PosTenderMath exact-settlement model (POS 066). |
| G7 | Hijri date adoption (اعتماد التاريخ الهجري) | A **calendar-display subsystem** (UmAlQura conversion at every date surface). | Company profile carries `CalendarType` — stored, unconsumed; it becomes the switch when the subsystem is built. |
| G8 | Per-user guardrails (§3.8: discount caps, post-print edit lock, 24-hour edit window, hide others' invoices, POS closing level) | A **per-user policy store + multi-window enforcement**. The users table carries no policy columns and no per-user settings window exists to extend. | RBAC (346 codes, roles/user_roles/role_permissions since mig 001) + POS.DISCOUNT.OVERRIDE precedent. Named follow-up landing, §3.8. |
| G9 | Permission templates (§5) | **Template objects** in RBAC (named bundles applied to roles/users). | Full substrate: permissions catalog + roles; `SYS.PERMISSION_TEMPLATES.MANAGE` code already reserved. Design note §5; build belongs to the commercial phase. |
| G10 | Export options (audited-only export, lock-after-export, per-type selective export) | **Selective per-type export** in DatWin (today: full-dataset exchange). | DatWin + STEI encrypted datasets (mig-era, built). Backlog with this evidence attached. |

## 3. Per-section study

### 3.1 Font (إعدادات الخط) — BUILD (Landing 1)

Phenix: font picker (Tahoma on the owner's machine), الأساسي
dropdown, حجم الخط 0 (= theme default), live preview (مثال).

| Field | Status | Consumer |
|---|---|---|
| Font family | **U→M** — no `UsrPref.FontFamily` exists; windows take the `ApplicationFontFamily` resource ("Segoe UI") in `VisuId.ApplyShellVisualProperties` | Every window: `VisuId.ApplyShellVisualProperties` is the single shell-wide font point → wire it to UsrPref (family + size), resource stays the fallback. |
| Font size | **U** — `UsrPref.FontSize` (default 13) is stored by OptWin today and read by **nothing** | Same point as above. This is the clearest "exists-unbound" defect in the current OptWin. |
| Live preview | **M** | The section page itself (a preview line rendered in the chosen family/size before save). |
| الأساسي dropdown | **A** | Phenix-specific font-role concept; our central type scale (VisuId tokens) already fixes roles. Noted, not copied. |

Form divergence: Phenix shows a bare picker + size + preview strip;
ours is a section page with family picker, size stepper, and a
preview card showing heading/body/grid sample lines in the real
central styles.

### 3.2 Numbers, date & time (الأرقام والتاريخ والوقت) — BUILD (Landing 1)

Phenix: decimal digits quantities 3 / prices 3 · rounding ✓ ·
thousand separator ✓ · show zero after decimal ✓ · date format
dd/MM/yyyy · time format h:mm tt · Hijri ☐.

Today our formatting is **fragmented and partly hardcoded** —
exactly what the owner's framing forbids: `F2Ui.Fmt` hardcodes
"N2"; DocDesign print cells hardcode Qty N3 / money N2 / pct N1;
the POS receipt hardcodes N3/N2 and `yyyy-MM-dd`/`HH:mm`;
`UsrPref.DecimalPlaces` **and** `UsrPref.DecimalDigits` duplicate
one meaning, and `ShellContext.DateFormat` / `DecimalPlaces` /
`CurrencyCode` have **zero consumers** (grep-verified) — the
current General section's date/decimal fields are stored-but-unbound
(**U**, defect).

| Field | Status | Consumer (program-wide) |
|---|---|---|
| Quantity decimal digits (3) | **M** — new `UsrPref.QtyDecimalPlaces` | Central formatter `AppFormat` → DocDesign print cells, POS receipt lines, F2 windows. |
| Price decimal digits (3) | **M** — new `UsrPref.PriceDecimalPlaces`; legacy `DecimalPlaces`/`DecimalDigits` become synced aliases (JSON compatibility), set from the same field | Same formatter → unit-price/money cells everywhere the formatter is used. |
| Rounding | **B (display)** — .NET display formatting always rounds to the shown digits; the toggle's honest meaning is *round the displayed value* vs *truncate* — we implement it as the formatter's midpoint mode and say so in the UI text. | `AppFormat` only. |
| Thousand separator ✓ | **M** — new bool `GroupThousands` (the existing `ThousandSeparator` string is a different, unconsumed concept — removed from the section, kept in JSON for compat) | `AppFormat` (grouped vs plain format strings). |
| Show zero after decimal ✓ | **M** — new bool `ShowTrailingZeros` (fixed-digit vs trimmed format strings) | `AppFormat`. |
| Date format dd/MM/yyyy | **U→B** — `UsrPref.DateFormat` exists (default already dd/MM/yyyy) with no consumer | `AppFormat.FormatDate` → POS receipt date line, invoice print date lines, F2 date displays wired in this landing. |
| Time format h:mm tt | **M** — new `UsrPref.TimeFormat` (default "HH:mm" — our current behaviour; the owner's Phenix value is a choice, not a mandate) | `AppFormat.FormatTime` → receipt time line. |
| Hijri adoption | **A** → headline gap **G7**. | — |

Scope honesty: data-grid cell display across ~97 windows binds raw
values through many local converters; this landing wires the
**central** consumers (print pipeline, POS receipt, F2 windows,
AppFormat callers). Grid-wide StringFormat adoption is a separate
mechanical sweep, recorded here as a follow-up, not silently
claimed.

Form divergence: one "Numbers, date & time" page with two grouped
cards (Numbers / Date & time) and a live sample line (a quantity, a
price, a date, a time rendered by the real formatter as the user
edits) — Phenix has no live sample.

### 3.3 Printing (الطباعة) — BUILD (Landing 2; migration 125)

Phenix: default printer · company info in report header (vs footer)
with per-field checkboxes (name, address, phone 1, phone 2, mobile,
fax, website, commercial register, industrial register, email,
PO box) · **logo pick + clear** · advanced print system · print
customer balance ☐ · print **previous** customer balance ✓ ·
discount/extra as % ☐ · discount/extra details ☐ · print serial
numbers ✓ · print item balance on vouchers ✓ · auto-print reports
☐ · show user name on POS invoices ✓ · print user name on invoices
☐ · invoice number as barcode at POS ☐ · print unit price on cash
invoices ✓ · print gifts on cash invoices ✓ · free-text print
header · roll margins R5/L5/T0 + roll width 80mm · test page.

Today: invoice print = `InvPrn.Print/ExportPdf(doc, companyName,
design, companyAddress)`; the header band prints the company name +
optionally the address (`DocDesign.ShowCompanyAddress`);
`DocDesignRepo.ResolveForPrintAsync` composes the address from the
company profile. **No logo exists anywhere** (previously recorded
out-of-scope in B3; the owner's evidence now puts it in scope).
Reports render through `RptRend` with a company-name header.

New store: `print_settings` (company_id PK) + `companies.logo_data`
/ `logo_media_type` columns — migration 125, both dialects. One
central composer, `PrintHeaderComposer` (pure, headless-testable),
turns profile + settings into the ordered header lines + logo for
**both** consumers (InvPrn header band and RptRend report header) —
one control point for the whole program's paper identity.

| Field | Status | Consumer |
|---|---|---|
| Company logo pick / clear | **M** | `companies.logo_data` (BLOB/BYTEA) → header composer → InvPrn + RptRend headers. Edited here (current company), per the evidence. |
| Header field checkboxes: name, address, phone, mobile, website, email, commercial register, licence | **M** — one bool column per field in `print_settings` | Header composer includes exactly the checked profile fields. (Our profile has all of these: Phone, Mobile, Website, Email, commercial_registration, License.) |
| Header fields: phone 2, fax, PO box, industrial register (separate) | **A** | The company profile has no such fields — nothing to print. Recorded; if the profile grows them, the checkboxes arrive with the fields. |
| Free-text print header | **M** — `print_settings.header_text` | Composer emits it under the header block on invoices + reports. |
| Print previous customer balance ✓ | **M** — `print_settings.print_previous_balance` | InvPrn party band: previous balance = party ledger balance **before** this document (same reader the Information Window uses), printed only for party documents. |
| Print current customer balance ☐ | **M** — same mechanism, balance **after** this document; independent toggle | InvPrn party band. |
| Default printer | **M** — `print_settings.default_printer` | InvPrn presets the print dialog's queue when set (dialog still shown; POS receipt keeps its own flow). |
| Print copies — invoices / vouchers | **M** — per-user (`UsrPref.PrintInvoiceCopies`, `PrintVoucherCopies`; Phenix files them under User options, §3.8 — built here because the consumer is this section's engine) | InvPrn sets the print ticket copy count by document family (invoice kinds vs voucher kinds). |
| Discount/extra as % · discount/extra details | **B** | Governed per-design in the Invoice Designer (DocDesign columns carry discount % and tax; B3) — a global toggle would fight the designer. Exists in stronger, per-design form. |
| Print serial numbers in invoices ✓ | **B** | Serial capture subsystem (mig 054) + designed invoice lines carry serial data where the design includes it. |
| Show user name on POS invoices ✓ | **B** | Receipt already prints the cashier (`BuildReceiptPage`). |
| Print user name on invoices ☐ | **M** — `print_settings.print_user_on_invoice` | InvPrn footer/meta band adds the posting user. |
| Print item balance on vouchers ✓ | **A** | Vouchers (CshWin/JrnWin) print through the journal print path, which has no per-line material balance concept — the governed behaviour (stock balance per voucher line) does not exist. Noted for the voucher-print backlog. |
| Invoice number as barcode at POS ☐ | **A** | The designed-invoice barcode exists (`DocDesign.ShowBarcode`, **B** for invoices). The thermal receipt is a fixed text layout; a receipt barcode is a receipt-layout change, recorded with the receipt backlog — not faked as a toggle. |
| Print unit price / gifts on cash invoices ✓✓ | **B** | Receipt prints unit prices per line today; gift lines are ordinary zero-price lines on the POS document and print as such. |
| Advanced print system ✓ | **B** | Our equivalent is the design system itself (Invoice Designer B3 + Report Designer B2) — per-document designs, stronger than a mode flag. |
| Auto-print reports ☐ | **A** | Reports print on demand from RptBase; an auto-print-on-open behaviour has no requester flow in our report windows. Not built. |
| Roll width 80mm + margins | **M** → built in §3.5 (POS store) | POS receipt width is hardcoded 302px today → `pos_settings.receipt_width_mm` + margins wire `PrintPosReceipt`/`BuildReceiptPage`. |
| Test page button | **B-equivalent** | The section's own "print test page" action uses InvPrn's built-in layout — added as a section action, not a setting. |

Form divergence: Phenix stacks ~25 loose checkboxes in one long
page. Ours groups them into cards — *Paper identity* (logo +
header fields + header text), *Invoice printing* (balances, user
name, copies, default printer), each card captioned with the
consumer it governs.

### 3.4 Barcode (الباركود) — BUILD (Landing 3; migration 126)

Phenix's Barcode page is **not** label design (our BcdDesWin covers
that, B4) — it is a **composite scan template**: a prefix + fixed
positions from which a scanned code is split into segments —
material barcode digits 3→6, weight integer 7→8, weight fraction
9→11 in the owner's example — so a scale-printed label resolves to
*material + embedded weight* at scan time. Plus a data-collector
block (port/parity/stop bits/flow).

We have **no composite parsing anywhere** (grep-verified): scans
resolve by exact code/barcode/name via `TaxSvc.ResolveMaterial`
(POS) and `TaxSvc.MatchMaterial` (document grids).

| Piece | Status | Consumer |
|---|---|---|
| Template store (one active template per company: prefix, total length, segments code/weight-int/weight-frac as start+length) | **M** — `barcode_templates`, mig 126 | Parser below. |
| Parser (pure core in `src/`: prefix + length match → extract material code + weight = int + frac/10^n) | **M** | `TaxSvc` scan resolution: POS `AddScannedItem` applies the parsed weight as the line quantity; InvoiceWin's material-cell commit (the `MatchMaterial` call site) applies it to the line quantity the same way. Both call sites, or the feature is half-wired — complete-pillars rule. |
| Data-collector block | **A** | No data-collector import consumer exists → headline-adjacent backlog (device family, G4). |

Form divergence: Phenix edits a free segment grid with start/end
*codes* and positions. Ours edits three named segment rows
(material code / weight integer / weight fraction) with start +
length steppers and a **live test field**: type a sample code, see
the extracted material code and weight — the parser itself is the
preview, so what the owner tests is what the till will do.

### 3.5 Point of sale (نقاط البيع) + cash sales invoices (فواتير المبيعات النقدية) — BUILD (Landing 4; migration 127)

The two Phenix sections govern one subsystem; we keep **one** POS
section (divergence, deliberate: two pages of till behaviour invites
drift). Settings store: `pos_settings` (mig 066) extended by mig
127. Fields evidenced across both sections (~45) map as:

**Build (missing, consumer exists):**

| Field | Consumer |
|---|---|
| Held invoices count = 3 | `PosRepository.HoldBillAsync` refuses a new hold when the company's open held bills reach the limit (0 = unlimited; new error code ×4). Today holds are **uncapped** (grep-verified). |
| Returns allowed within N days | POS return flow refuses a return whose original sale is older than the window (0 = no limit). |
| Auto-print cash invoices ✓ | `PosCheckoutWin` prints the receipt **unconditionally** today (verified at its completion path) → the setting makes it conditional; reprint stays available in PosSalesWin. |
| Receipt header text / footer text (from الملحقات common footer) | `InvPrn.BuildReceiptPage` (fixed layout today: company name only) prints header text under the company name and footer text at the end. |
| Roll width (80mm) + margins (from الطباعة) | `PrintPosReceipt` page size + `BuildReceiptPage` width/padding. |

**Exists + bound (B), no build:** show material balance/price on
search (POS search shows both; InfoWin F9 deepens it) · warn when
material not found ✓ (scan path reports and keeps focus) · edit
unit price when total edited (grid behaviour in PosWin) · audit
expiry date / enable alerts (batch/expiry subsystem, mig 054,
FEFO) · multi-currency settlement (POS 066 sessions carry currency;
P4 per-point currency) · audit cashbox closing / close by counted
value with variance (POS 066 Z-close) · show remaining-value window
on save (checkout shows change/remaining) · cash sales invoice +
returns patterns (P4 per-point document kinds) · per-PC POS
assignment (PosTermWin permitted users, P1/P4) · date shift
(session dates are server-stamped; a shift field would falsify
audit times — **A**, refused by design).

**Absent-with-reason (A):** print exchange as two invoices ·
settle returns by purchase voucher · new voucher on partial
collection · POS discounts priority to cheapest materials
(discounts are explicit per line in our pricing engine — an
allocation strategy subsystem does not exist) · central cash system
(our sessions are per-terminal by design; a central-cash pooling
model does not exist) · block returns of invoices from other POS
(cross-terminal return policy — the return flow resolves the
original document program-wide today; a restriction policy is a
named backlog item, not a toggle without the policy) · invoice
recovery system on unsaved (till crash-recovery of an *unsaved*
bill — held bills are the recovery mechanism that exists; unsaved
keystroke recovery does not) · touch properties block (material
images / tree categories / cell geometry on the touch surface —
the touch surface itself is a deferred POS phase; settings would
be fake) · sale-invoice design button (exists as P4 per-point
design bindings — **B**, lives in PosTermWin where it belongs).

### 3.6 Loyalty (نقاط الزبائن, inside نقاط البيع) — BUILD (Landing 5; migration 128)

Phenix: accumulate points as % of invoice ☐ · points % of invoice
(main currency) · redeem % of invoice · minimum points · purchase
voucher picker.

Our substrate: `loyalty_accounts` / `loyalty_entries` + LoyaltyWin
with **manual** Earn/Redeem in one transaction
(DefinitionRepository). **No rates exist anywhere** (grep-verified)
and **POS checkout never touches loyalty** (grep-verified).

Build: rates on `pos_settings` (mig 128: loyalty_enabled,
loyalty_earn_pct, loyalty_redeem_pct, loyalty_min_points) →
consumers in the POS completion flow: on a posted sale with an
identified customer, **earn** = invoice total × earn% points,
written as a loyalty entry carrying the document reference
(idempotent per document; a return **reverses** the earn of the
original). At checkout, **redeem**: the cashier applies points up
to min(balance, invoice total × redeem%) — settled as a **Points
tender** line (`pos_sale_tenders.tender_type = 'Points'`, so the
Z-report and tender math see it as a settlement, never as a
discount), debited from the account in the same completion flow.
The Phenix *purchase-voucher picker* has no counterpart because
our redemption is a settlement tender, not a voucher — divergence
recorded; the points ledger entry carries the document reference
either way. Proof: a dedicated harness drives the Infra-level
earn/redeem service against a migrated SQLite DB (earn math,
redeem caps — % cap, balance cap, minimum — idempotency,
return-reversal).

### 3.7 Advanced (خيارات متقدمة) — mapping only, no build

Phenix's ~35 guardrails are, in ShouTech, **engine rules** — most
exist in a stronger, more specific form than a global toggle:

| Phenix guardrail | Our status |
|---|---|
| Warn below cost+margin / block below cost | **B, stronger** — the pricing engine enforces per-material minimum price and margin target as hard posting breaches (`PriceBelowMinimum`, `MarginBelowMinimum`). A warn-only mode would *weaken* the owner's own sales-cycle enforcement decision → not built (**A** for the toggle). |
| Warn/block negative sale | **B, stronger** — stock engine blocks negative stock per material (`materials.allow_negative`), per-material beats global. |
| Expiry warn ≤15d / block ≤90d (sale + purchase) | **B, per-material** — `shelf_life_days` / `alert_before_expiry_days` on the material card + FEFO. Global day-counts would override per-material shelf lives → **A** for globals. |
| Block sale to rep over credit limit | **B** — credit-limit enforcement exists in the sales cycle (party credit limits checked at posting). |
| Serial rules (block negative serial, auto-generate, consolidate, search) | **B/A** — serial subsystem exists (mig 054, BatchSerialCaptureSvc); capture is scan-driven by design; the individual mode toggles are **A** (no separate modes exist to switch). |
| VAT enable / VAT on collection | **B** — `company_tax_settings` (mig 067) + TaxSvc; edited in the tax surface, not duplicated here. VAT-on-collection is a tax-regime variant → **A** until a regime demands it. |
| UoM system ☐ | **B** — the units system (equivalents, mig 121 names) is core, not optional. |
| Statistical-unit system · multi-spec materials | **A / partial** — materials carry spec classes (class1..5); a statistical-unit reporting subsystem does not exist. |
| Save-confirmation message ✓ · password-only login ✓ | **B** — behaviours exist. |
| Allow duplicate material/category names ☐ | **A** — uniqueness is structural (codes unique; names governed by data), not a mode. |
| Ask about installment on save | **A** — installments are their own document family (InstallmentsWin); an intercept prompt has no flow to attach to. |
| Auto-generate invoice in orders ✓ | **B** — order→invoice conversion exists in the order chain (mig 064 + sales cycle). |
| Block main-warehouse / main-cost-center use | **A** — no "main warehouse" singleton concept exists to block; warehouses are per-document. |

### 3.8 User options (خيارات المستخدم) — DEFERRED landing (named), one item built in §3.3

Phenix (per-user, user picker at top): idle screensaver minutes ·
auto-shutdown minutes · confirm on exit · discount % and
discount-2 % caps · block sale below wholesale + margin · block
zero-price sale · lock editing invoices/vouchers within 24h ·
no operation after invoice print · hide other users' invoices ·
block negative sale · block customer with a due invoice · print
copies (invoices/vouchers) · shortcut/jملة-shortcuts tabs ·
copy settings to another user · per-user POS assignment ·
main-menu customisation.

Rulings: **print copies** → built in §3.3 (consumer is the print
engine). **Per-user POS assignment** → **B** (PosTermWin permitted
users). **Confirm on exit** → **U** (grep-verified: `UsrPref.
ConfirmOnExit` is read by nothing outside OptWin) → wired in
Landing 1 to the shell exit path (MWin close / application exit
asks when the pref is on). **Idle auto-lock** →
`UsrPref.AutoLockMinutes` is likewise **U** (only OptWin touches
it); Landing 1 wires it if the shell already has a lock surface,
otherwise it is recorded here as a named deferral — a lock screen
is a shell feature, not an options field. The remaining **guardrails**
(discount caps, post-print lock, 24-hour edit window, hide-others,
due-invoice block, wholesale floor) are headline gap **G8**:
they need policy columns on `users`, a per-user administration
surface (none exists today — users are administered through
Auth/Company flows), and enforcement at the InvoiceWin / PosWin /
voucher save paths. That is one coherent future landing; building
any single toggle now would leave the rest as fake neighbours.
**Screensaver/shutdown timers** → **A** (OS-level behaviours; an
ERP does not own the workstation power policy). **Shortcut
customisation** → **D** with the POS rail (§4a) — same mechanism
(per-user/per-point shortcut maps), one design.

### 3.9 Movement recorder (مسجل الحركات) — deliberate divergence, no build

Phenix configures *which* windows × events (open/close, add, edit,
delete, other) get recorded, plus record-on-login/logout. ShouTech's
audit is **always-on by design**: security events
(`security_audit_log`, mig 029, written by Auth incl. login/logout)
+ journal audit events + document audit trails, read in
ActivityLogWin. A matrix whose purpose is to switch recording
**off** for deletions would directly weaken the owner's audit
pillar (and the recycle-bin/accountability model built on it).
FIND-THE-BEST ruling: always-on beats configurable-off; the
evidenced *reading* surface (per-window activity) already exists
as ActivityLogWin filters. Recorded as a conscious divergence,
not a gap.

### 3.10 Export (خيارات التصدير) — no build (DatWin owns its surface)

SVC1 ruling stands: DatWin carries export/import (Excel, PDF,
encrypted .stei datasets) with its own window. Phenix's additions —
audited-only exports, lock-after-export, per-type pickers,
compression, PTP internet transfer credentials — govern a
**selective** export subsystem we do not have (headline gap
**G10**); PTP credentials additionally describe a Phenix cloud
service that does not exist here (**A**). The default-folder /
target-path conveniences belong to DatWin's own backlog, not to
OptWin.

### 3.11 Manufacturing (التصنيع) — no build

Phenix drives manufacturing through invoice *patterns* (which
patterns generate a journal voucher, which do not; settlement
invoices as the non-voucher pair). ShouTech manufacturing is a
native order engine (MfgOrderService + BOM + GlPosting) — posting
is engine-defined, not pattern-mapped, so the pickers have nothing
to bind to (**A**, architecture divergence, ours is the stricter
model). Warn/block negative manufacturing → governed by the stock
engine's per-material `allow_negative` (**B**, §3.7). Model
determines quantity ✓ → **B** (BOM quantities drive orders).
Auto distribution ratio → **B-equivalent** (cost distribution
follows BOM/actuals in the costing engine). Barcode printing in
manufacturing → **A** (no mfg label print flow exists).

### 3.12 Accessories (الملحقات) — no build except receipt texts (§3.5)

Customer display, barcode reader, PBX, modem, scale sub-pages:
device family → headline gap **G4/G5**. The sub-pages' common
footer (POS invoice header/footer text, POS-buttons customisation,
customer-display options) splits: header/footer text → **built**
(§3.5); POS-buttons customisation → **§4a**; customer-display
options → **G4**.

### 3.13 SMS (خيارات الرسائل النصية) — no build → G1
### 3.14 Email (البريد الالكتروني) — no build → G2 (owner decision pending)
### 3.15 Delivery (التوصيل) — no build → G3

(Each: every evidenced field recorded in the evidence file; none
built, per the no-fake-fields rule; the outbox substrate (G1/G2)
means a future transport landing lands its settings here then.)

### 3.16 Awaiting owner evidence (W)

الخيارات العامة · الحسابات · إضافي الفواتير · النسخ الاحتياطي —
not photographed; **nothing guessed**. Our General and Backup
sections remain exactly as built in B2. (SVC1 recorded Accounts /
Invoice-extras as absent-with-reason from *tutorial* evidence;
the owner's screenshots supersede tutorials, so both revert to W
until he photographs them.)

## 4. Bonus items

### 4a. POS function-rail configuration per terminal — BUILD (Landing 6; migration 129)

Evidence (POS definition «قائمة المهام»): per-point grid —
operation · shortcut · group · colour (التعبئة) · order arrows ·
show-task-list ✓ — over 17 operations. Today PosWin's action rail
is a **hardcoded sequence** (Products, Session, Hold bill, Recall
held, POS return, Clear sale, Checkout) built in code — precisely
the scattered behaviour the owner's framing says to centralise.

Build: `pos_terminal_tasks` (per terminal: operation key, shortcut,
group, colour, sort order, visible) + a **Tasks tab in PosTermWin**
(our form: a tinted grid with the rail's own preview column, not
Phenix's arrow-list) + PosWin builds its rail from the terminal's
configuration (defaults = today's sequence, seeded on first read).
Operations offered = the operations PosWin actually implements
(the seven above + payment-method buttons are §4b's surface);
Phenix operations we do not implement (gift-invoice printing,
user switch, price check…) are **A** — they enter the list when
the operations themselves are built. Proof: repository-level
harness (config round-trip, ordering, visibility, default seeding).

### 4b. Payment-method definitions → POS tenders — BUILD (Landing 7; migration 130)

Evidence (تعريف طرق الدفع): method (K-net) · account (133 كي نت…)
· commission account (3114…) · shortcut · commission cap ·
commission % · method image; rows K-net + Visa/Master on one
account; card buttons at the bottom of the window.

Our substrate is **already built and entirely unconsumed**:
the `PaymentMethods` definition set (Definitions.cs) — table
`payment_methods` with code, names, kind (Cash/BankTransfer/
Cheque/Card/Other), `gl_account_code`, `settlement_bank_code`,
`commission_pct`, `commission_account_code`, `requires_reference`,
**`is_pos_enabled`, `pos_sort_order`** — editable today in
DefManagerWin, read by **nothing** (grep-verified). PosWin's
payment buttons are hardcoded (Cash / Credit / K-Net / Visa).

Build: PosWin builds its payment buttons from the POS-enabled
methods (ordered by `pos_sort_order`), mapping kind → tender type
(Cash→Cash, Card→Card, BankTransfer/Cheque→Card-with-reference,
Other→OnAccount only when the method is the credit method) and
passing the chosen method into checkout, where the tender records
it. Add the two evidenced fields the set lacks — `commission_cap`
(mig 130 column + DefSet column) and `shortcut` — with their
consumer being the button itself (shortcut focuses/selects the
method). **Commission GL posting is NOT built here**: posting
commission expense at sale time is a posting-engine change that
needs its own accounting proof cycle; it is recorded as backlog
with this evidence, and the commission fields stay definition data
until then (they are Phenix-parity data, visibly labelled as
awaiting the posting landing — not presented as live behaviour).
Method image → **A** (buttons are text in our rail design;
no image-button surface exists).

### 4c. Permission templates — DESIGN NOTE ONLY (§5)

Per the brief: evidence + design + quantified gap; no build in
this workstream (the build belongs with the commercial-phase
RBAC/licensing work; `SYS.PERMISSION_TEMPLATES.MANAGE` is already
reserved in PermCat).

## 5. Permission templates — target model vs our RBAC substrate

**Target model (owner evidence).** Window «قوالب صلاحيات
المستخدمين», tabs: القوالب · الصلاحيات · صلاحيات الفواتير ·
صلاحية السندات · صلاحية الطلبات · صلاحية نقاط البيع · الأوراق
المالية · صلاحيات متقدمة. The الصلاحيات tab is a **module tree
spanning every window family** (~21 modules in the photographed
tree: companies, materials, accounts, customers, suppliers, POS,
invoices, vouchers, orders, reports, manufacturing, HR, …) with
per-module grant states; the document tabs are **matrices**:
rows = document types (11 invoice types in the photographed
matrix), columns = operations — عرض (view) · إضافة (add) · تعديل
(edit) · حذف (delete) · طباعة (print) · إظهار الحقول (show
fields) · توليد سند قيد (generate journal voucher) · تحديد
الحساب (set the account) — each cell an individual grant. The
owner's framing (2026-11-24 19:47): the permissions system's tabs
are very many **and it covers all of the program's windows** —
coverage completeness is the point of the system.

**Our substrate today.** PermCat: **346 permission codes** in
MODULE.RESOURCE.ACTION form across **25 families** (System,
Company, Inventory, Accounting, Quality, HR, CRM, Projects,
Assets, Vouchers, Sales, Purchasing, POS, Customers,
Representatives, Manufacturing, Banking, Delivery, Reporting,
Data, Backup, Audit, Licensing, Updates, AI, Help). Storage:
`roles`, `user_roles`, `role_permissions` (mig 001) + the
`permissions` catalog table (mig 004, seeded per migration).
Enforcement: 84 of 136 view files call `Permissions.Has(...)`
directly, plus service-layer checks; SYS.ADMIN bypass exists
(BinWin purge precedent). Action vocabulary actually used:
VIEW ×101, MANAGE ×38, CREATE ×29, UPDATE ×25, DELETE ×20,
EDIT ×15, POST ×9, OVERRIDE ×8, APPROVE ×6, CONVERT ×4,
CANCEL ×4, REVERSE ×3, EXECUTE ×3, REPOST ×2, IMPORT ×2, …

**Quantified gap vs the target model, family by family:**

| Phenix matrix dimension | Ours today | Gap |
|---|---|---|
| View / Add / Edit / Delete per document type | Present as VIEW/CREATE/UPDATE/DELETE codes for the main document families (Sales, Purchasing, Vouchers, POS) | Small — vocabulary exists; per-*type* granularity (11 invoice types as separate rows) is coarser in ours: invoice kinds share SALES.INVOICE.* codes rather than one row per kind. |
| Print (طباعة) per document type | **Essentially absent** — exactly **1** PRINT code in the whole catalog (`INV.BARCODE.PRINT`); printing is not permission-gated anywhere | **Large** — a PRINT action family must be minted per document family and checked at every print entry point (InvPrn callers, RptBase). |
| Show fields (إظهار الحقول) | **Absent as a concept** — no field-level visibility permissions (nearest precedent: margin-visibility discussions in the sales cycle, undecided) | **Large** — needs a field-visibility model per window + the owner's margin-visibility decision. |
| Generate journal voucher (توليد سند قيد) | Approximated by POST codes (×9) on the posting families | Medium — POST governs posting the document itself; Phenix separates *generating the voucher* from saving the document. Our posting is atomic with the document (a deliberate engine property), so this column maps to POST with a noted divergence. |
| Set the account (تحديد الحساب) | **Absent** — account fields on documents are governed by UPDATE on the document | Medium — a dedicated sensitive-action code per document family + checks at the account pickers. |
| Module tree covering every window | 25 families / 346 codes; 84/136 view files check permissions directly (the rest inherit via services/menus) | Medium — the tree exists in substance; a templates UI must render PermCat as the tree and prove per-window coverage family by family (the ribbon audit's 266 entries are the coverage checklist). |
| Templates themselves (named bundles, apply/copy) | **Absent** — roles are the only bundles; `SYS.PERMISSION_TEMPLATES.MANAGE` reserved, no table, no window | The build: `permission_templates` + template_permissions tables, a templates window (tree + matrices rendered from PermCat), apply-to-role/user. Sized as a commercial-phase landing, not an options-section fix. |
| Advanced permissions tab (صلاحيات متقدمة) | Partially present as OVERRIDE/APPROVE sensitive codes (×8/×6, is_sensitive flag in the catalog) | Small-medium — the sensitive-action family exists; the tab is a view over it. |

**Design ruling for the future build:** templates are *named,
copyable bundles over the existing PermCat catalog* (never a
second permission vocabulary); the matrix tabs are projections of
the catalog by family + action; PRINT and SET-ACCOUNT action
families are minted first, since they are the two columns with no
substrate; field-visibility (show fields) waits for the owner's
margin-visibility decision. Divergence by design: our tab set
follows our 25 families, not Phenix's 8 tabs — same coverage
obligation, our own form (IP rule).

## 6. Landing plan (each gated + pushed before the next)

| Landing | Content | Schema |
|---|---|---|
| 0 | This study | — |
| 1 | Font section (family/size → VisuId) + Numbers/date/time (AppFormat centralisation; DocDesign, receipt, F2 consumers; General section's unbound fields replaced) + Behavior honesty wiring (ConfirmOnExit → shell exit path; AutoLockMinutes → shell lock if a lock surface exists) | — (UsrPref) |
| 2 | Printing (print_settings + company logo; header composer; InvPrn + RptRend; previous/current balance; default printer; user-on-invoice; print copies) | mig 125 |
| 3 | Composite barcode template + parser + POS/grid scan consumers | mig 126 |
| 4 | POS behaviours (held cap, returns window, auto-print, receipt header/footer/width) + OptWin POS section | mig 127 |
| 5 | Loyalty earn/redeem wiring at POS | mig 128 |
| 6 | Bonus (a): per-terminal POS task rail | mig 129 |
| 7 | Bonus (b): payment-method definitions → POS tender buttons (+ commission cap / shortcut columns) | mig 130 |

Gates per landing (branch standard): solution build 0/0
(--no-incremental) · SqlMigrator fresh + idempotent · 4 audit
scripts exit 0 · errors + lang parity ×4 · harnesses P2p 39/39,
W1 36/36, Bin 22/22, Num 27/27, Info 35/35 · plus the landing's
own consumer proof (formatter / header composer / barcode parser /
held-cap / loyalty / task-rail harnesses as applicable).
