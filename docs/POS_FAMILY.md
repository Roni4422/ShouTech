# POS family completion (owner workstream 2026-11-24 15:31)

The POS is a FAMILY, not a checkout screen: (1) terminal
definition, (2) session/window creation + opening, (3) the sales
screen on that session, (4) the session's sales, (5) close/Z,
held bills, drawer movements, returns. Audit first; build only
the missing pieces.

## Audit of what existed (2026-11-24, before P1)

| Piece | State found | Verdict |
|---|---|---|
| Terminal definition | pos_terminals table (036) with only code/names/warehouse/cash account; UI = generic DefManagerWin set | (b) thin — P1 |
| Session opening | PosSessionWin: real — opening float, warehouse resolved from the terminal, live Z, close with counted/expected/variance, paid-outs (migration 066/104) | (a) with one weakness: terminal was free text — P2 |
| Sales screen | PosWin: bound to the open session of the settings-default terminal; sales gated by RequireSessionForSaleAsync; session-less only via the audited bypass permission + setting | (a) conformed |
| Session sales | No dedicated window — session sales visible only through reports (PosCashRptWin / PosZReportWin) | missing — P3 |
| Close / Z | PosSessionWin close + PosZReportWin; expected/variance computed from real tenders by PosRepository | (a) conformed |
| Held bills | pos_held_bills(+lines) (066) + PosWin hold/recall | (a) conformed |
| Drawer movements | Paid-OUT at the session window (reason required, reduces expected cash); drawer kick via CashDrawer + CashDrawerOptionsWin (printer-bound) | (a) for out; paid-IN missing — named gap below |
| POS returns | PosReturnWin (session-scoped, loads the original bill, posts a linked SalesReturn through the same engine) | (a) conformed |

## P1 — terminal definition (DONE, gates ALL-GREEN)

- **Migration 119 (both dialects)**: pos_terminals gains
  bank_account_code, drawer_name, printer_name,
  default_customer_code, price_level, permitted_users.
- **PosTermWin** (dedicated; POS.DefinePOS rewired from the
  generic DefManagerWin set): grid of terminals + full editor —
  warehouse combo (real warehouses), cash/bank account combos
  (chart codes), drawer, printer, default customer (double-click
  opens PartySearchWin), price level (editable combo fed by the
  distinct material price levels), permitted users, active flag.
  Every field persists via the new PosTermRepo.
- Chosen vs rejected (owner's find-the-best rule): a dedicated
  window was chosen over completing the generic definition set
  for this one entry — the benchmarks (ERPNext POS Profile,
  Odoo POS config, Al-Ameen/Phenix POS station definition) all
  use a dedicated profile surface with device/account bindings
  a generic key-value editor cannot express. The other
  DefManagerWin sets stay generic until R4's per-set audit.

## P2 — session opening on defined terminals (DONE, gates ALL-GREEN)

PosSessionWin's terminal is now an editable combo fed by the
defined, active terminals. Opening enforces the definition:
when the company defines terminals, the code must be a defined
active terminal and the user must be on its permitted list when
one is set (POS.TerminalNotDefined / POS.TerminalUserNotPermitted,
×4 languages). With no definitions at all, the legacy
settings-default path stays, so existing installs do not break
before their first terminal is defined.

## P3 — session sales window (DONE, gates ALL-GREEN)

**PosSalesWin** (new ribbon key POS.SessionSales, 265 → 266
entries): pick a session (defaults to the open one) → its
documents with the per-bill tender split (cash/card/on-account)
and session totals (sales / returns / net). Reprint runs the
real receipt path (InvRepo.Load + InvPrn.PrintPosReceipt with
the stored tenders); Return/Exchange opens PosReturnWin on the
chosen bill (new optional initialDocNo parameter — the bill
loads directly instead of being re-typed). Rows carry the
numbered row-header treatment; returns (SRT) are listed beside
sales, distinguished by kind.

## Named gaps (recorded, not papered over)

- **Paid-IN drawer movements**: pos_session_expenses models
  paid-outs only. A paid-in needs a kind column + Z expected-cash
  math changes in PosRepository + Kern.Tests coverage — money
  math is not half-wired from a headless branch; scheduled with
  the commercial-readiness phase.
- **Per-workstation terminal identity**: PosWin binds the
  settings-default terminal; choosing among several defined
  terminals per workstation needs a workstation identity
  concept the product does not have yet.
- **Terminal device enforcement**: drawer_name/printer_name are
  definition data; the receipt print path's printer selection
  lives in CashDrawerOptionsWin (company-level) — converging the
  two is a commercial-phase item.

## Four-system comparison — POS family

| Dimension | ERPNext | Odoo POS | Al-Ameen (classic help) | Phenix v10 | ShouTech after P1 |
|---|---|---|---|---|---|
| Terminal definition | POS Profile (warehouse, accounts, payment methods, user) | POS config (shop, pricelist, journal, users) | POS station options + restaurant-system options (help text) | POS station definition (vendor material) | PosTermWin: warehouse, cash+bank accounts, drawer, printer, default customer, price level, permitted users |
| Session | POS opening entry with float | session open with starting cash | shift open | session open | PosSessionWin float open, one open session per terminal (unique index) |
| Sales bound to session | invoice linked to opening entry | order linked to session | POS invoices per station | per session | PosWin gated on the open session; docs carry pos_session_id |
| Session sales list | POS invoice list per profile | orders per session | station sales review | session sales | PosSalesWin with reprint + return/exchange (P3) |
| Close | closing entry with variance | close + cash count | Z / day close | Z report | close with counted/expected/variance + PosZReportWin |

Al-Ameen evidence: the classic help text (owner upload 15:33)
has a real POS section (POS invoices/vouchers, restaurant
options) — logic/fields evidence, classic generation, not
Al-Ameen 10's screens.

**Where ShouTech exceeds:** offline-first tills on SQLite with
the same posting engine as the back office (no sync-then-post
gap); the session's expected cash is computed from real tenders
server-side, never typed; session-less selling is an audited,
permission-gated exception, not a silent mode.
**Where ShouTech still trails:** no restaurant mode (tables /
kitchen) — the benchmarks' POS families include it; out of this
workstream's scope, named here. No paid-in yet (above).
**Verdict (power + ease):** more powerful — one posting engine
from till to GL with session accountability end to end; easier —
the cashier opens a session with a counted float and sells;
every definition lives in one terminal record instead of
scattered settings.

## P4 — PosTermWin vs Phenix «تعريف نقاط البيع» (owner evidence 2026-11-24 17:49; step after numbering)

Field-by-field (Phenix field → ShouTech before → action):

| Phenix | ShouTech before | This step |
|---|---|---|
| Point name + Latin name | code + name_en + name_ar | COVERED already |
| Out-document type (which sales invoice type) | — | **BUILT**: sales_doc_kind (POS/SAL), consumed by PosWin checkout + PosPosting's kind guards |
| Returns document type | — | **NOT APPLICABLE, with reason**: our POS returns always post as SalesReturn documents linked to the original bill (PosPosting) — the return type is not a free choice in this posting model, so no selector is offered rather than a field that cannot change behavior |
| Cash account | ✓ | already |
| Cashbox CLOSING account (separate) | — | **BUILT**: closing_account_code, recorded on every close (pos_cash_closings) |
| Currency per point | — (session used UsrPref currency) | **BUILT**: currency_code, consumed at session open |
| Customer GROUP scoping | default customer only | **BUILT (weighed)**: customer_group_code; PosWin rejects a typed customer outside the terminal's group (scoped tills are real retail practice; the terminal's own default customer is exempt). Default customer kept — the two compose: group scopes, default pre-fills |
| Touch-screen properties | — | DEFERRED to the POS-screen step (its only consumer is the sales-screen surface) — named, not silent |
| POS-fields customization per point | — | DEFERRED with the same reason |
| Print designs per point (sale/return/GIFT) | — | **STORED + pickers BUILT** from doc_designs (.stid names); CONSUMER PARTIAL: the POS thermal receipt is a fixed code-built layout today (InvPrn.PrintPosReceipt), so the binding takes effect when the receipt becomes design-driven — recorded honestly, not claimed complete |
| Tabs قائمة المهام / قسيمة شراء | n/a | UNEXAMINED (owner transcription) — not invented |

**Ours ahead (recorded)**: warehouse binding, bank/card account,
cash drawer, printer name, price level, permitted users — none
visible on the Phenix tab. Migration 124 carries the new columns.
