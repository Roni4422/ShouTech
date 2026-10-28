# Batch 2 — Core Reports & Commercial Invoices (2026-10-05)

## Goal
Ship a competitive **core** of standard reports and invoice documents on top of Batch 1 foundation (`RptBase`, export, crypto).

## Reports (`Views/Reports/`)
| Window | ReportKey | Purpose |
|--------|-----------|---------|
| TrialBalanceWin | Fin.TrialBalance | Trial balance |
| AccStmtWin | Fin.AccountStatement | Account statement + running balance |
| JrnListWin | Fin.JournalList | Journal register |
| StkBalWin | Inv.StockBalances | Stock balances + valuation |
| MatMoveWin | Inv.MaterialMoves | Material movement card |
| CustBalWin | AR.CustomerBalances | Customer balances |
| SupBalWin | AP.SupplierBalances | Supplier balances |
| SalListWin | Sales.InvoiceList | Sales document register |
| PurListWin | Purch.InvoiceList | Purchase document register |

Open via `RptNav.Open*` helpers.

## Invoices
| Component | Role |
|-----------|------|
| Reporting/Invoices/InvDoc.cs | Document + line model, tax per line |
| Services/DocSchema.cs | sales/purchase documents, lines, party_ledger, stock_movements |
| Services/InvRepo.cs | Draft save, post (immutable), numbering |
| Services/InvPrn.cs | Print + PDF |
| Views/SalInvWin.cs | Sales invoice UI |
| Views/PurInvWin.cs | Purchase invoice UI |

## Accounting rules applied
- Posted documents are immutable; corrections via return documents.
- Party ledger updates on post (customers debit / suppliers credit for normal invoices).
- Line-level tax rates for mixed VAT (GCC / Levant / EU-ready).
- Company-scoped queries only.

## Honest scope note
This is a **production core**, not full Al-Ameen/Phoenix parity. Missing by design for later batches:
- Full COA drill-down, multi-currency revaluation reports
- Advanced print designer templates per customer
- Stock postings from invoice lines into cost layers/FIFO
- POS receipt specialized layout
- ZATCA/e-invoice XML payloads

## Localization
`Resources/Lang/*.json` sections: `Reports.*`, `Invoice.*` (ar/en/fr/tr).
