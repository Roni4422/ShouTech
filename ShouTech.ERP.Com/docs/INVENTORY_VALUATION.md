# Inventory valuation engine

ShouTech's application layer contains a deterministic, side-effect-free perpetual valuation engine for FIFO and moving-average stock costing. It calculates receipt and issue costs using `decimal`, rejects invalid quantities/costs and prevents an issue from taking stock below zero. FIFO issues consume receipt layers in order; moving-average issues use the current balance value per unit. Issuing the full balance clears quantity and value exactly.

## Integration contract

- Keep an independent `InventoryValuationState` for each company, material, and warehouse.
- `Receive` and `Issue` return a new state plus the movement's extended cost; the input state is unchanged if validation fails.
- Persist the new balance, any FIFO layers, immutable stock ledger entry, and source-document posting in one database transaction. Do not update the material master quantity as a substitute for the ledger.
- Persist values at the application's agreed currency precision at the accounting boundary. This calculation layer intentionally does not round intermediate unit costs.
- Reversals should be represented by compensating stock movements, not by editing a posted ledger row.

This engine is a calculation foundation, not a claim that stock valuation has been wired into every receipt, issue, POS, transfer, return, or accounting workflow. Database persistence, migrations, UI, permissions, and posting-path integration remain follow-up work and must be completed and tested before enabling valuation in production.

## Verification

The unit tests cover FIFO layer consumption, moving-average receipts/issues, full-balance clearing, invalid inputs, and insufficient stock. Run the repository's Windows CI workflow to build the WPF solution and execute the full test suite.
