# Voucher and journal standards (ShouTech)

Aligned with double-entry practice, regional ERP (Al-Ameen / Phenix) behaviour,
and enterprise patterns (SAP document + journal entry, Dynamics chronological
vouchers).

## Model

| Layer | Role | Numbering |
|-------|------|-----------|
| **Source document** | User entry (invoice, JV, receipt, payment, cash day-book line) | Own series: SAL, PUR, JV, RCV, PAY, … |
| **Ledger journal** | Posted double-entry in the general ledger | Central **JRN** series per company and fiscal period |

Rules:

1. **One source document posts at most one ledger journal** for its primary effect. No duplicate GL rows for the same economic event.
2. **Source number is preserved** (`source_doc_no`). After post, `document_number` may hold the central **JRN-n** value.
3. **JRN is allocated only inside the posting transaction**, never when a draft form opens. Failed posts do not consume a JRN number.
4. **Each entry window shows only its series** (RCV window navigates RCV-*, PAY navigates PAY-*, JV navigates JV-*). The cash day-book lists RCV+PAY for one day.
5. **Posted documents are immutable**; corrections use **reversal** journals linked to the original (`reversal_of_entry_id`), not silent edits or deletes.
6. **Debits equal credits** before post (`EnsureBalanced` / GlPosting). Unbalanced drafts cannot post.
7. **Fiscal period must be open** for the entry date; closed periods reject posting.

## Windows

| Window | Series | Behaviour |
|--------|--------|-----------|
| InvoiceWin | SAL / PUR / returns / … | Posts inventory and/or GL per InvPostingPolicy |
| JrnWin | JV | Manual general journal |
| CshWin (Receipt) | RCV | Classic voucher form; filters RCV |
| CshWin (Payment) | PAY | Classic voucher form; filters PAY |
| **CashDayWin** | RCV / PAY | **Cash day-book**: one day, dual entry + report; open ready to type; each added line creates and posts one source voucher + one JRN |

## Cash day-book

- Default date = **today**.
- On open: loads that day's RCV and PAY movements (draft and posted) with source number, central JRN, direction, amounts.
- Entry strip: treasury, direction, counter account, amount, memo — **Add & post** without a separate New/Edit cycle.
- Full receipt/payment forms remain available for party-linked settlement and multi-line vouchers.

## Implementation anchors

- `docs/DOCUMENT_NUMBERING.md` — JRN period counters and source series policy
- `src/Infra/JournalRepository.cs` — SaveDraft, Post, Reverse, Preview/Next numbers, **ListSourceVouchersForDateAsync**
- `src/Infra/GlPosting.cs` — document-to-ledger posting engine
- `app/Views/CashDayWin.cs` — cash day-book UI
- `app/Services/RptNav.cs` — `OpenCashDayBook`
- Ribbon: `Bonds.CashJournal` → cash day-book

## Non-claims

This document describes product accounting controls. It does not claim a formal
audit certification or country-specific tax compliance by itself.
