# INFO_WINDOW_STUDY — Global Information Window (phase-gate study)

Owner directive (Man, 2026-11-24 17:55–18:00, scope correction +
emphasis): the Information Window is NOT a POS-only pane. It fetches
information about ANYTHING the user selects with the mouse — materials,
accounts, customers, sales reps (مناديب), and other entities — and it
can be OPENED FROM ANY WINDOW in the program. Its displayed properties
are configured via RIGHT-CLICK on the top of the info window (a
checkbox property list, the Phenix pattern). This study is committed
before the first build commit of the phase, per the owner's phase-gate
rule. Owner evidence: Phenix screenshots transcribed in
~/workspace/research_notes/phenix-tools-tab-inventory-20261124-1335/
notes/screenshot-menus-pos-20261124.md §§1+7.

## 1. Benchmarks and the pattern they converge on

- **Phenix (owner's own install, verified)**: a floating «نافذة
  المعلومات» opened from the POS function rail and the main quick
  toolbar. For the material under the cursor it shows the PROJECTED
  total balance (347.500 ← 346.500 — stock after the current line's
  effect), per-warehouse balances, sale price, weighted-average
  purchase cost, last purchase price + date, and the last price sold
  to THIS customer. Right-click on the pane's top opens «خصائص
  متقدمة»: a checkbox list of ~35 data points (price levels
  wholesale/half-wholesale/export/distribution, min/max sale and
  purchase, last purchase from the supplier, last sale to the customer
  net, manufacturer, barcode, packing, size, source, spare, chemical
  composition, weight, notes, category, groups, sales snapshot…); the
  checked set is what the pane displays.
- **ERPNext**: hover/link quick info + a per-DocType sidebar
  (timeline, connections, assignments) — the inspector is anchored to
  the record in focus, not to a module.
- **Odoo**: stat buttons and smart-button counts on the form header,
  plus chatter — again: facts about the record in focus, one click
  away, no navigation.
- **Dynamics 365 F&O**: FactBoxes — a docked fact pane per form,
  personalization decides which facts show; the same
  per-user-visible-fact-set idea as Phenix's checkbox list.
- **Global desktop pattern** (VS Properties window, macOS Get Info):
  one shared inspector surface whose content follows the current
  selection; opening it is a single chord available everywhere.

The common core: **one inspector, selection-driven, entity-aware,
with a per-user configurable fact set.** Not per-window popups, not a
report you navigate to.

## 2. Decisions (find-the-best, fitted to this shell)

Our shell is a window environment: every tool is a BaseWin window
owned by MWin, there is no dockable pane host inside arbitrary
windows, and Phenix itself — the owner's reference — uses a floating
window. Therefore:

1. **Surface**: ONE modeless floating window, `InfoWin`, shared
   app-wide (a second open request refocuses it and swaps its
   content). It is owned by the window that invoked it, so z-order
   follows the standing ownership rule (no Topmost). It is NOT a
   docked pane: docking would require a pane host in every window —
   a parallel shell — for no functional gain over the floating form
   the benchmark itself uses.
2. **Invocation (central, not per-window hacks)**: BaseWin gains a
   central **F9** chord next to the existing F1 help chord. F9 asks
   the host window for its current selection through the
   `IInfoContextProvider` contract; with a selection it opens/refreshes
   InfoWin, without one it shows a graceful localized notice (never an
   error). MWin gains the matching shell entry (quick-access button
   beside the recycle-bin corner button), which reopens InfoWin on
   the last published selection. POS gets its own labelled button on
   the function rail, mirroring Phenix.
3. **Selection contract**: `InfoRef` (entity kind + numeric id and/or
   code) plus an optional `InfoContext` (the party in focus, the
   warehouse in focus, a pending stock-out quantity). Windows publish
   selection changes to a central hub (`InfoContextHub`); BaseWin
   exposes `PublishInfoSelection()` so a provider is two lines: return
   the ref, call publish on selection change. While InfoWin is open
   it follows the hub (follow mode, on by default; the user can pin
   the current entity from the window's bottom bar).
4. **Computation**: one central service, `InfoSvc`, resolves a ref to
   an ordered property sheet. Every value comes from the system's own
   stores — `material_stock_balances` (the engine-maintained snapshot
   reports already trust), `party_ledger` BASE amounts (the Rpt
   convention), `journal_entry_lines` over posted entries (status = 1,
   the Accounting convention), the document tables for last prices —
   or from the entity's own card record (`Materials.LoadById`,
   `parties`, `sales_reps`, `chart_of_accounts`). No parallel
   computation paths, no cached copies.
5. **Configuration**: per entity type, the visible-property set is
   chosen from the window header's right-click context menu
   (checkbox items, Phenix-style) and persists per user + company via
   the existing WinPref store (InfoWin's own scope). Defaults mirror
   the Phenix checked set for materials and the closest analogues for
   the other types.
6. **POS first surface**: PosWin publishes its selected cart line's
   material with context (current customer, session warehouse, the
   cart's total quantity for that material) so the projected balance
   is real: total balance today, and balance-after-this-sale. The POS
   screen also gains the live **amount-in-words total** under the
   total panel, rendered by the existing `NumWords` engine (the
   program's standing English legal wording; see §5 for the recorded
   limitation).

## 3. Property sets per entity type (build set)

Formats: Qty = N3, Money = N2 + company currency where monetary,
Date = yyyy-MM-dd. "—" is shown for a property whose source exists but
has no value yet (e.g. no purchase ever); a property is only listed
when its source exists in the schema at all.

### Material (sources: materials card, material_stock_balances, sales/purchase lines)
Code · Name · Barcode (unit barcodes + extra barcodes) · Category ·
Group · Default unit · **Total balance** · **Projected balance**
(only with a pending-out context) · **Warehouse balances** (one row
per warehouse) · Reserved qty · Stock value (Σ available × average
cost, the balances' own snapshot) · Weighted-average cost ·
Sale price (retail) · Wholesale price · Special price · Export
price · Distributor price · Online price · Minimum price ·
Last purchase price · Last purchase date · Last purchase from
supplier (party name) · Last sale price · Last sale date ·
**Last sale price to the current customer** (context) ·
Min/Max sale price (posted lines) · Min/Max purchase price ·
Average sale price · Average purchase price · Cost (card) ·
Standard cost · Cost method · Manufacturer · Brand · Source/origin
country · Pack qty · Net weight · Size · Notes · Service/non-stock
flag · Reorder point · Sales this month (qty + amount, posted SAL/POS).

### Account (sources: chart_of_accounts by company GUID, journal lines of posted entries)
Code · Name · Type · Parent account · Debit total · Credit total ·
**Balance** (with natural side) · Movements count · Last movement
date · Estimated budget (migration 118) · Warning side · Account
side · Currency + parity · Reconciliation date · Closing account ·
Postable/Summary · Active.

### Customer / Supplier (sources: parties, party_ledger, document tables)
Code · Name · Group · **Balance** (base currency) · Credit limit ·
Credit used % · Credit days · Open amount (posted, unpaid) ·
Oldest open document date · Last transaction (date, doc no, amount) ·
Last price for the context material (when the host supplies one) ·
Price tier · Tax no · Phone · Mobile · Email · Active.

### Sales rep (sources: sales_reps, targets (036), sco_commission_entries (097))
Code · Name · Group · Phone · Commission % · **Target (current
period)** · **Sales this month** (commission-basis Sales sum — the
commission engine's own resolution of rep → invoices) ·
Commission earned this month · Commission pending ·
**Achievement %** (when a target exists) · Active.

### Warehouse (sources: warehouses, material_stock_balances)
Code · Name · Default · Active · Materials with stock · Total qty ·
Stock value · Address · City · Phone · Parent warehouse.

### Document (sources: sales_documents / purchase_documents + lines)
Doc no · Kind · Date · Party · Status · Subtotal · Discount · Tax ·
**Grand total** · Paid · **Open amount** · Currency · Warehouse ·
Lines count · Sales rep · Posted by/at · Created by.

## 4. Deferred with reasons (never faked)

- **Chemical composition, spare-part (الغيار)**: the material card has
  no such fields anywhere in the schema — no source exists. Deferred
  until the card grows the fields (a MatCard decision, not this
  phase's).
- **Party aging buckets in the info sheet**: aging is a report-grade
  as-of computation (Rpt already owns it); the sheet serves balance,
  open amount and oldest open date instead. Revisit if the owner
  asks for buckets here.
- **Rep sales from raw invoice text**: `sales_documents.sales_rep_name`
  is free text; the commission engine already resolves it to rep
  codes, so the sheet reads the engine's entries. A month with no
  generated commissions shows zero — stated on the sheet's own terms
  (the property is labelled commission-basis).
- **Arabic tafqeet**: `NumWords` is English-only BY DESIGN (printed
  legal wording must not depend on UI language). The POS live total
  uses it as-is; an Arabic words engine is a separate decision.
- **3-decimal currencies in words**: `NumWords` renders 2 fraction
  digits; KWD/JOD totals on POS are displayed N2 across the whole POS
  surface today, so the words match the surface's own precision. A
  currency-precision pass over NumWords + POS display is recorded
  here as a known limitation, not silently "fixed" inside this phase
  (it would change printed voucher wording program-wide).

## 5. Coverage and exceptions

- The F9 affordance exists in **every BaseWin window** by
  construction (central chord). Providers wired in this phase:
  PosWin, InvoiceWin, MatDirWin, MatCard, CoaWin, AccCard, CstCard,
  RepCard, WhsWin, DocSearchWin. Any other BaseWin window answers F9
  with the graceful no-selection notice until it grows a provider —
  recorded as coverage state, not an exception.
- **Exceptions** (cannot offer the affordance, with reasons):
  the startup chain (SplWin, Lang, StpWiz, ComWin, LogWin — no
  company context, not BaseWin); modal pickers (BatchSerialPickerWin
  and kin — the "selection" is a transient choice being made, an
  inspector would recurse); HelpWin (the help surface itself);
  InfoWin (itself). MWin is not a BaseWin: it gets the shell button
  instead of the chord.
- POS projection honesty: the cart quantity is in the line's unit;
  the projection converts through the material's unit equivalents
  (the المعادل factors on the card). When the line unit cannot be
  resolved to a factor, the projected-balance property is omitted —
  never computed in mixed units.

## 6. Errors, languages, persistence

- New catalog codes: INF-8401 (entity not found), INF-8402 (info load
  failed) — ×4 languages, parity-gated. All UI text through the Lang
  catalogs (`InfoWin` section) ×4.
- **No migration**: configuration rides the existing WinPref store;
  the service is read-only over existing tables. Migrations therefore
  stay at 124 in both dialects (the migrator gate still runs).

## 7. Proof plan

`tools/InfoHarness` (BinHarness pattern, real SchemaMigrator SQLite):
seed a company with a material + stock in two warehouses, a posted
sale to a customer and a posted purchase, a party with a credit
limit, a posted journal on an account, a rep with a target and a
commission entry; then assert through InfoSvc itself: total balance
and per-warehouse balances; projected balance = total − pending;
weighted-average cost; last purchase price/date/supplier; last sale
price to that customer vs to another; party balance/open amount;
account balance and movement count; rep month sales, target and
achievement; document sheet totals; not-found maps to INF-8401.
Plus the standing gates (build 0/0, migrator through 124 idempotent,
4 audits, errors parity, P2p 39/39, W1 36/36, Bin 22/22, Num 27/27).

## 8. As built (2026-11-24, reduce)

Landed in two gated steps: S1 = InfoSvc + catalogs + InfoHarness;
S2 = InfoWin + BaseWin F9 affordance + shell button + providers +
POS surface. Gates at both landings: build 0/0, migrator through
124 idempotent (no new migration — §6), 4 audits exit 0, errors
parity 171 codes ×4, P2p 39/39, W1 36/36, Bin 22/22, Num 27/27,
InfoHarness 35/35.

Served property sets (registry order; the header right-click menu
toggles each per entity type, persisted via WinPref):

- **Material**: Code, Name, Group, Category, TotalBalance,
  ProjectedBalance (context only), ReservedQty, AvailableQty,
  WarehouseBalances (child rows), StockValue, AvgCost, LastCost,
  CardCost, StandardCost, CostMethod, SalePrice, WholesalePrice,
  HalfWholesalePrice (the card's special price), ExportPrice,
  DistributorPrice, OnlinePrice, MinSalePrice, MinPurchasePrice,
  MaxPurchasePrice, AvgSalePrice, AvgPurchasePrice, LastPurchasePrice,
  LastPurchaseDate, LastPurchaseSupplier, LastSalePrice,
  LastSalePriceToCustomer (context party), SalesThisMonthQty,
  SalesThisMonthAmount, Manufacturer, Brand, OriginCountry, Source,
  Barcode, ExtraBarcodes, Packing (pack qty + units per parcel),
  Weight, Size, DefaultUnit, ServiceFlag, Notes.
- **Account**: Code, Name, Type, ParentCode, Balance (signed
  debit−credit, rendered magnitude + side), DebitTotal, CreditTotal,
  MovementsCount, LastMovementDate, Currency, Budget, WarningSide,
  AccountSide, ReconciliationDate, Postable, Active.
- **Customer / Supplier**: Code, Name, Group, Balance (same signed
  convention), CreditLimit, CreditUsedPct, CreditDays, OpenAmount,
  OldestOpenDate, LastTransactionDate, LastTransactionAmount,
  LastPriceForMaterial (context material), Phone, TaxNumber.
- **SalesRep**: Code, Name, Group, CommissionPct, MonthSales
  (commission-engine basis), Target (targets table, card fallback),
  AchievementPct, MonthCommission, PendingCommission, Phone, Active.
- **Warehouse**: Code, Name, ParentWarehouse, MaterialsCount,
  TotalQty, StockValue, DefaultFlag, Active.
- **Document**: DocNo, Kind, Date, Party, Status, Warehouse,
  Currency, SubTotal, DiscountTotal, TaxTotal, GrandTotal,
  PaidAmount, OpenAmount, LinesCount.

Providers wired (publish on selection change; F9 reads live):
PosWin (cart line material + sale context + projected balance +
labelled Information button + live amount-in-words under the POS
total), InvoiceWin (line material / document party), MatDirWin
(material tree nodes), MatCard, CoaWin, AccCard, CstCard (customer
and supplier kinds), RepCard, WhsWin, DocSearchWin (document rows).
Every other BaseWin window answers F9 with the last published
selection or the localized empty state.

Exceptions and deferrals (unchanged from §5, restated): startup
chain and modal pickers offer no F9; chemical composition and
spare-part properties have no schema fields and are NOT served;
party aging buckets stay with the reports; rep month figures are
commission-engine based and labelled as such; the amount-in-words
engine renders 2 fraction digits (KWD/JOD 3-decimal precision is a
recorded NumWords limitation, not silently changed here).
