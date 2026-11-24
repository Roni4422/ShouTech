# NUMBERING_STUDY — Automatic sequential numbering (phase-gate study)

Owner directive (Man, 2026-11-24 17:28): AUTOMATIC SEQUENTIAL NUMBERING
for ALL entry windows — invoices, vouchers, journals, orders, cards
(material/customer/account/etc.), and every window that has entry fields
or entry grids. No number the system can assign may be left to manual
typing. This study is committed before the first build commit of the
phase, per the owner's phase-gate rule.

## 1. Benchmarks (researched 2026-11-24)

- **ERPNext naming series**: per-document-type series; pattern = prefix +
  date tokens (`.YYYY.` / `.FY.` / `.MM.`) + counter with padding
  (`.####`, default 5). Counters are stored per series and updated
  transactionally; Document Naming Rules set prefix + counter start;
  the current value of a series can be viewed and reset from settings
  ("Update Series"). A year/fiscal token in the pattern gives a separate
  counter per segment — numbering restarts each year by construction.
- **Odoo `ir.sequence`**: per-code sequences with prefix/suffix (date
  tokens), padding, next-number, per-company rows, and date-range
  subsequences for periodic resets. Two implementations: *standard*
  (fast; gaps possible on rollback) and *no_gap* (a locked counter row;
  gaps only from deletions). Production guidance: no_gap only where a
  regulator demands it.
- **Al-Ameen / Phenix**: per-document-type sequential numbers with a
  configurable start, conventionally year-segmented in the region; card
  codes auto-suggested and still editable before first save.
- **SAP**: number-range objects per document type with intervals and
  buffering — the same counter-table idea at enterprise scale.

The common core: **one counter store, keyed per series (and per
company/segment), advanced atomically inside the caller's transaction,
formatted from prefix + segment + padded counter, configurable start.**
That is exactly the pattern this codebase already uses for invoice
documents (below) — the phase generalizes it instead of inventing a
second mechanism.

## 2. Audit — numbering in ShouTech today (`reduce` @ 2a2ac1f)

### Already right (sequence-table backed)
- **Commercial documents (InvRepo)**: `document_number_sequences`
  (migration 075; company × kind × year → last_value). Allocation is an
  UPSERT inside an immediate transaction:
  `next = MAX(stored + 1, existing-documents MAX + 1)` — atomic,
  monotonic, self-seeding from history. Format `PREFIX-YEAR-D4`
  (SAL-2026-0001). CRM quotations share the QUO series the same way.
- **Material group card_no**: `material_group_sequences`, same pattern.

### Broken or missing
| Family | Today | Defect |
|---|---|---|
| Chain documents (SO/PO/RFQ/SQ/DLV/RCV/RCR + chain SAL/PUR) | `OrderDocumentRepository.NextDocNoAsync`: full-scan MAX on its own connection, no transaction, starts at 1000, format `PREFIX-NNNN` (no year) | Race → duplicates under two users; chain SAL/PUR numbers differ in format from InvRepo's for the same kind |
| Journals & cash vouchers (JrnWin/CshWin) | Window pre-fills a TIMESTAMP (`JV-20261124-143022`, `RCV-…`/`PAY-…` + timestamp); user-editable | Not sequential at all; the unique index on (company, document_number) turns collisions into save errors |
| POS sessions | `COUNT(*) + 1` over `SES-YEAR-%` | Count-based: purging old sessions lowers the count → numbers REISSUED |
| Manufacturing orders | `MAX(doc_no) + 1`, no transaction | Race; reissue after purge |
| Warehouse codes | `MAX(warehouse_code) + 1` with `is_deleted = 0` | Binned warehouses' codes REISSUED (breaks the bin rule) |
| Material codes | `Inventory.NextCode`: lexicographic string MAX, `is_deleted = 0`, no transaction | Same reissue defect; lexicographic ordering mis-seeds mixed-width codes |
| Parties (customer/supplier codes) | Typed manually in the card windows | System-assignable number left to typing |
| Employee codes | Typed manually; duplicate-checked | Same |
| Company codes | `MAX(numeric) + 1` in the master/setup store | Out of scope — see exclusions |

### Exclusions (with reasons)
- **Account codes (CoaWin)**: structural identifiers of the chart
  itself; benchmarks treat them as user-defined (auto-assigning would
  fight the canonical seeded chart and cost-center conventions).
- **Cheque numbers**: issued by the bank and printed on the physical
  cheque — an external number the system records, never assigns.
- **Mnemonic definition codes** (units KG/PCS, brands, regions…):
  meaning-based identifiers chosen by the user, not sequences.
- **Company codes**: assigned in the master/setup store, outside any
  company database where the sequence tables live; single-administrator,
  low-frequency flow whose MAX runs inside the setup transaction.
  Recorded as a named residual, not silently skipped.
- **System-derived journal numbers** (`JV-<doc no>` created by posting
  engines): references to their source documents, not user entry.

## 3. Design

### 3.1 One allocator
A central `SequenceAllocator` (src/Infra, GlPosting-style: static,
takes the caller's connection + transaction + provider) over the
existing `document_number_sequences` table:

```
AllocateValue(conn, tx, provider, companyKey, seriesKey, segmentYear, seedMax, startFloor)
    → INSERT (company, series, segment, last_value = max(seedMax, startFloor) + 1)
      ON CONFLICT DO UPDATE SET last_value = MAX(last_value + 1, max(seedMax, startFloor) + 1)
      RETURNING last_value
```

- **Atomicity**: the UPSERT runs inside the caller's transaction
  (SQLite immediate / PG row lock on the conflict target) — two
  simultaneous users can never receive the same value. This is Odoo's
  no_gap counter-row model, which this codebase already proves.
- **Seeding**: `seedMax` is the family's own MAX over existing numbers
  **including soft-deleted rows** (see §3.3), so a fresh sequence row
  starts above all history; `startFloor` comes from configuration (§3.4).
- Formatting stays with each family (documents `PREFIX-YEAR-D4`, chain
  unified to the same, journals `PREFIX-YEAR-D4`, materials
  prefix + padded value, integer families consume the raw value).
  An app-layer `SeqSvc` wraps the allocator for windows/services that
  hold no transaction of their own.

### 3.2 Allocation point and gaps
Numbers are allocated **when the number is assigned** — document
windows at open (today's InvRepo behaviour), journal/voucher windows at
New, cards pre-filled at open for a new card. An abandoned entry burns
its number: gaps are accepted (ERPNext series and Odoo standard
sequences behave identically; the alternative — allocating at post
time — would renumber documents users have already seen and printed
as drafts). Card code fields stay **editable before first save** (the
Al-Ameen/Phenix convention): a user may replace the suggested code with
their own, uniqueness-enforced; the sequence only ever moves forward.

### 3.3 Interaction with the Recycle Bin (owner-required ruling)
- A soft-deleted item **keeps its number reserved**: its row still
  exists, and every family's seed MAX counts soft-deleted rows, so the
  number cannot be reissued while the item is in the bin. (This fixes
  the warehouse/material seed filters, which today exclude deleted
  rows.)
- **On permanent purge the number is NEVER reused — for posted
  documents and for drafts alike.** The sequence row outlives the
  record and only moves forward. Reason: a purged number can survive
  outside the database (printed copies, emailed PDFs, the
  deletion_audit_log snapshot), and reusing it would give two different
  documents one identity. This is the accounting-safe default the owner
  named; the draft exception some products allow is deliberately not
  taken, because a draft's number is visible and printable from the
  moment the window opens.

### 3.4 Configuration (OptWin, per-tab-own-fields rule)
Migration 123 adds `number_series_config` (company × series → prefix,
padding, yearly_segment, start_value, updated_by/at). A new **Numbering**
section in OptWin lists every series with its own fields — prefix,
digit padding, yearly-segment toggle, start value — plus the current
counter read from `document_number_sequences`. Rules:
- No config row → today's defaults (kind prefix, D4, yearly for
  documents/journals/POS; start 1).
- Configuration affects **future allocations only**; existing documents
  keep their numbers (no renumbering of history, ever).
- Raising `start_value` raises the allocation floor; lowering it never
  moves a sequence backward (monotonicity wins — the allocator takes
  MAX(stored, seed, start−1) + 1).
- Changing a prefix changes only the printed prefix; the series key
  (and its counter) is the document kind, so history stays one series.

### 3.5 Series inventory (what gets numbered by the mechanism)
Documents: SAL SRT POS PUR PRT ENT EXT TRN OPN CNT QUO (existing) +
chain SO PO RFQ SQ DLV RCV RCR (SAL/PUR unified). Journals: JV RCV PAY.
POS sessions: SES. Manufacturing: MFG (integer). Materials: MAT per
group prefix. Warehouses: WH (integer code). Parties: CUS / SUP
(pre-fill). Employees: EMP (pre-fill). Material groups: existing
`material_group_sequences` (already compliant; left as-is).

### 3.6 Errors
NUM-8301 sequence limit reached · NUM-8302 invalid series
configuration (bad prefix/padding/start) · NUM-8303 allocation failed —
×4 languages, parity-checked. All user text through Lang ×4 (new
`Numbering.*` keys for the OptWin section and series names).

## 4. Proof plan
A new headless harness `tools/NumHarness` (BinHarness pattern):
(1) two interleaved allocations never collide and are consecutive;
(2) config start/prefix/padding/yearly applied to formatted output;
(3) monotonicity across the bin: allocate a document number → post →
soft-delete → purge → the next allocation is strictly greater (never
reused); (4) chain NextDocNoAsync returns the unified format and shares
the SAL counter with InvRepo; (5) journal number allocation is
sequential (no timestamps); (6) POS session allocation does not reuse
a purged session's number; (7) material NextCode seeds above a binned
material's code. Existing gates stay green throughout (build 0/0,
migrator through 123 idempotent, 4 audit scripts, errors/lang parity,
P2pHarness 39/39, W1Harness 36/36, BinHarness 22/22).

## 5. What remains Windows-owed
The same standing debt: WPF click-through of the Numbering section and
the pre-filled number fields, Kern.Tests execution, and a live
PostgreSQL run of migration 123 + the allocation paths.
