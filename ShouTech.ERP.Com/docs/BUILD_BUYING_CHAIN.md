# Buying Chain — RFQ -> PO -> Receipt -> Purchase Invoice (M1)

Implements the buying twin of the selling chain, from the [VIBE modern spec](VIBE_MODERN_SPEC.md) milestone M1 (ERPNext "Buying" / Al-Ameen 10 purchasing parity, clean-room).

## What was added

| File | Role |
|---|---|
| `src/ShouTech.Domain/Interfaces/IPurchaseDocumentRepository.cs` | Purchase-side persistence contract (purchase_documents family only) |
| `src/ShouTech.Infrastructure/PurchaseDocumentRepository.cs` | Provider-neutral implementation (SQLite + PostgreSQL) |
| `src/ShouTech.Application/Services/BuyingChainService.cs` | RFQ -> PO -> RCV -> PUR workflow with partial fulfillment |
| `tests/Kern.Tests/BuyingChainTests.cs` | Chain, fulfillment and stock-in tests on a migrated schema |

## Posting contract (mirrors the selling side exactly)

- **RFQ / PO**: posted with no side effects.
- **RCV** (purchase receipt): for each stockable line, ONE
  `StockValuationEngine.Apply` QtyIn movement (type `RCV`, receipt unit cost =
  the line's unit price, quantity in the material's base unit via the shared
  `UnitEquivalents` rule), so valuation methods, batch/serial tracking and
  `material_stock_balances` behave identically to the sales side.
- **PUR** (purchase invoice): a `party_ledger` credit for the supplier
  (`party_kind = 'S'`) through `PartyLedger.WriteEntry` with base amounts +
  effective rate, and one balanced journal entry in the same transaction:
  Dr Purchases (402) [+ Dr Input VAT (1205) only when postable] / Cr Payable (2201).

## Purchase cycle completion (ERPNext parity)

The deliberate, review-scoped gaps above are now closed at the service layer:

### Purchase return (PRT)

`CommercialDocKinds.PurchaseReturn` (the reserved `PRT` kind). Created by
`BuyingChainService.CreateReturnFromSourceAsync` from a **posted RCV or posted
PUR**, limited by `document_links`-fulfilled quantities, so the combined
returns from one source can never exceed its received (respectively invoiced)
quantity. Posting a PRT (in `PurchaseDocumentRepository.PostPurchaseAsync`)
mirrors the receipt + invoice legs exactly, in one transaction:

- **Stock**: for each stockable line, ONE `StockValuationEngine.Apply` QtyOut
  movement (type `PRT`), base-unit quantities, serial-tracked lines
  auto-allocated — the engine values the issue from the balance/layer queue,
  so the return can never enter valuation through a price.
- **Supplier ledger**: a `party_ledger` DEBIT for the supplier
  (`PartyLedger.WriteEntry`, base amount + effective rate) — the exact mirror
  of the invoice credit.
- **GL**: one balanced journal entry in the same transaction: Dr Payable (2201)
  / Cr Purchase Returns (403) [+ Cr Input VAT (1205) only when postable, at
  the return's tax].

### Incoming quality gate on RCV (decision on Q-profiles taken)

The incoming inspection trigger reuses the existing Q-profile decision from
Phase B 056: the material's `quality_check_required` flag + per-item
`qc_template_in_id`, and the company's quarantine warehouse (decision Q2).
`QualityGate.DivertReceipt` (the helper the 056 class contract already
named) is called by the receipt posting BEFORE the engine: a receipt of a
QC-required material lands in quarantine — never sellable stock — and
`QualityGate.EnsureInspection` records a Draft **Incoming** inspection against
the receipt in the same transaction; completion releases it to sellable stock.
This mirrors the legacy `StkPost` receipt path exactly; a pre-056 database
(diverts nothing) is unaffected.

## Remaining known gap

- UI: the buying-chain windows (and their RBAC keys) follow the
  "feature-complete" checklist once the service is approved.

## Tests

`BuyingChainTests` covers: RFQ -> PO totals + link, partial receipt then
remaining, two-step stock accumulation (10 units, 80.00 value exact), draft
sources rejected, over-remaining quantities rejected — plus the cycle
completion: return-from-receipt (stock 6/48.00 after a 4-unit return of
10@8, supplier debit 32.00, balanced Dr Payable / Cr Purchase Returns,
over-return rejected, full remaining return drains stock), return-from-
invoice with the 15% VAT split (Dr Payable / Cr Purchase Returns / Cr
Input VAT), incoming QC gate (quarantine stock 10/80.00, main warehouse 0,
Draft Incoming inspection), and return rejected from non-quantifiable chain
documents (RFQ).
