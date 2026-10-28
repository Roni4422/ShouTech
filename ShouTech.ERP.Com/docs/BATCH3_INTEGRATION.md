# Batch 3 — Shell Integration + Stock Post + Tax + Deep Reports (2026-10-05)

## Done
1. **Main window wiring**: `MWin.OnRibbonButtonClick` → `RptNav.TryHandle(LocProps key)`.
2. **TabReports expanded** with financial / inventory / parties groups.
3. **Sales/Purchase return** ribbon buttons → `SalInvWin(SalesReturn)` / `PurInvWin(PurchaseReturn)`.
4. **Stock posting** on document post via `StkPost.ApplyDocument` (movements + balances + avg cost).
5. **TaxSvc**: default rate from `tax_codes` or country defaults (GCC / Levant / N.Africa / EU); party + material resolve from DB.
6. **Invoice fields ↔ DB**: party code LostFocus, material code CellEditEnding fill name/price/unit/tax.
7. **Deep reports**: customer/supplier aging, item profitability, stock movements register.

## How to open
Ribbon keys handled by `RptNav.TryHandle` (see Services/RptNav.cs switch).
