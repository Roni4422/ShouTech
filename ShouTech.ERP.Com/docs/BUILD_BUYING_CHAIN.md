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

## Known scoped gaps (deliberate, for review)

- No incoming quality gate call on RCV yet (the outgoing gate is wired on the
  sales DLV; the incoming inspection trigger needs a decision on Q-profiles).
- No UI window yet — service + repository layer only; the WPF window and its
  RBAC keys follow the "feature-complete" checklist once the service is approved.
- No return document in the buying chain yet (purchase return doc kind is
  reserved: `PRT` proposal).

## Tests

`BuyingChainTests` covers: RFQ -> PO totals + link, partial receipt then
remaining, two-step stock accumulation (10 units, 80.00 value exact), draft
sources rejected, over-remaining quantities rejected.
