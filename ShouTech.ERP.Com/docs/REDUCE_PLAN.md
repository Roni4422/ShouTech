# REDUCE Plan — file-count consolidation on branch `reduce`

Owner directive (2026-11-24): reorganize/consolidate the repo to reduce the
total file count as much as safely possible, across ALL file types, even if
files get somewhat bigger. Work slowly and carefully on the new branch
`reduce` (base: stabilization tip `d7ff5d0`); the stabilization branch stays
untouched as the fallback. This is a file-layout change only: zero behavior
change, zero renames of types/members/namespaces/resource keys/public APIs.
Every merged file keeps the dominant (largest / natural-anchor) file's
existing name, in the repo's short-name style.

## Baseline inventory (tracked files at d7ff5d0)

Total: **950**

| Category | Count | Notes |
|---|---|---|
| .cs | 480 | app 335 in cod/, libraries 157 in src/, tests 50, tools/svc ~14 |
| .sql | 259 | 213 in dba/mig (OFF LIMITS), 45 in archive/, 1 stray in dba/sys |
| .md | 89 | 80 in docs/, rest root/dba/archive READMEs |
| .xaml | 27 | all paired with .xaml.cs — excluded |
| .json | 20 | Lang/Errors/COA catalogs + configs — loader-addressed, excluded |
| .cpp/.h | 24 | sec/ native library — excluded (see below) |
| .shc | 16 | COA templates, loaded by filename — excluded |
| .csproj/.sln/.props | 12 | project structure — excluded |
| .yml | 5 | CI workflows — excluded |
| .sys | 5 | dba schema descriptors — kept |
| .py | 4 | tools/ audit scripts — kept (gate tooling) |
| other | ~14 | png/txt/config/misc — kept |

## Hard exclusions (not candidates, with reason)

1. **Applied migrations dba/mig/{sqlite,postgresql} 001–114 (213 files)** —
   OFF LIMITS: they exist in real databases' applied ledgers (the owner's
   company DBs run this branch). No merge/rename/renumber/delete. (A
   fresh-install-only consolidation would be theoretically possible but is
   excluded by the owner's constraint.)
2. **Lang/Errors catalog JSONs and COA/Currencies resources** — the loaders
   address them by language/filename; structure must stay.
3. **XAML + code-behind pairs (27 xaml)** — x:Class pairing forbids merging.
4. **sec/ C++ library (26 files)** — native toolchain (MSVC/CMake) cannot be
   build-verified in this environment; an unverifiable merge violates the
   green-branch rule. Skipped.
5. **svc/WebhookSigner.cs** — linked by path from tests/Kern.Tests.csproj
   (`Compile Include="..\..\svc\WebhookSigner.cs"`); merging it would break
   the test project's file link.
6. **docs/USER_GUIDE_EN.md** — copied to build output by explicit path in
   MainApp.csproj; the path must stay valid.
7. **CI workflows (.github)** — merging changes trigger semantics; not a
   layout-only change.
8. **tls/frp/kern_tests.cs** — not code at all (a V9 spec note with a .cs
   extension); harmless, out of build. Left as-is.
9. **Large cohesive single-class files** (Infra repositories, giant app
   services like InvRepo/Rpt/Inventory, main feature windows) — merging
   them would create mega-files with no organizational gain.

Verified mechanics: MainApp.csproj globs `**\*.cs` (EnableDefaultCompileItems
is false but a single glob Compile item covers everything), and Domain /
Infra / App / Kern.Tests / inv / svc projects use default compile items —
so C# file merges need **no csproj edits**. Both migrators (SqlMigrator
tool and app SchMig) read **only** `dba/mig/{provider}` — proven from source.

## Consolidation groups (safest first; gates after EACH group)

Gate set after every group: `dotnet build ShouTech.sln --no-incremental`
= 0 errors / 0 warnings; SqlMigrator fresh apply through 114 + second run 0;
tools/audit_window_context.py, verify_screenfit.py,
verify_english_source.py, schema_audit.py exit 0; tools/P2pHarness 39/39;
tools/W1Harness 36/36; Errors parity 0 missing. A group that fails gates
for a non-trivial cause is reverted and marked SKIPPED with the reason.

| # | Group | Files in → out | Δ | Rationale / risk |
|---|---|---|---|---|
| G1 | dba/sys duplicate | 1 → 0 | −1 | dba/sys/023_invoice_header_fields_postgresql.sql is byte-identical to the applied dba/mig/postgresql/023 file; both migrators read only dba/mig (proven); zero references anywhere. Delete the stray copy only. |
| G2 | Docs: fix-phase records | 13 → 1 | −12 | FIX_PHASE_STEP1..13.md are sequential chapters of one fix-phase record → FIX_PHASE.md. |
| G3 | Docs: build records | 10 → 3 | −7 | BUILD_PHASEB_054..057 → BUILD_PHASEB.md (−3); BUILD_HR_DESIGN + BUILD_HR_PAYROLL → BUILD_HR_PAYROLL.md (−1); BATCH2 + BATCH3 + MERGE_BATCH1 → MERGE_BATCH1_FOUNDATION.md (−2); BUILD_MANUFACTURING + BUILD_POS stay (distinct builds). |
| G4 | Docs: topical pairs + root logs + V9 stubs | 15 → 6 | −9 | DOCUMENT_NUMBERING pair → DOCUMENT_NUMBERING.md (−1); ENGLISH_ONLY pair → ENGLISH_ONLY_MIGRATION_PLAN.md (−1); PURCHASE_TO_PAY_CYCLE + _WORKFLOW + THREE_WAY_MATCH → PURCHASE_TO_PAY_CYCLE.md (−2); ACCOUNTING_CHART pair → ACCOUNTING_CHART_PERIODS_YEAR_CLOSE.md (−1); root CLEANUP_LOG + PHASE6_CHANGELOG → CLEANUP_LOG.md (−1); V9 stubs arc/api/sec → usr.md (−3). |
| G5 | Docs: competitive set | 6 → 1 | −5 | COMPETITIVE_GAP_ASSESSMENT (dominant) absorbs ERP-COMPETITIVE-ROADMAP, PHENIX10-COMPETITIVE-ROADMAP, ERPNEXT_PARITY_ROADMAP, MANAGER-IO-COMPETITIVE-BENCHMARK, ERP-PROCESS-PARITY-MATRIX — one competitive-analysis corpus. |
| G6 | Docs: inventories & audits | 7 → 2 | −5 | Window inventories (WINDOW-INVENTORY, MISSING_WINDOWS_INVENTORY, FILE-INVENTORY) → RIBBON_COMPLETION_INVENTORY.md (−3); window audits (WINDOW_CONTEXT_AUDIT, WINDOW_DB_FIELD_AUDIT) → window-field-persistence-audit.md (−2). |
| G7 | Archive legacy SQL | 45 → 11 | −34 | archive/legacy_sqlserver per-folder concatenation (ai/fin/hr/inv/mfg/pos/post/root/sys/tmpl → 1 file each) + legacy_schema_snapshots lite+pg → 1 file. Content preserved verbatim with file-boundary markers; nothing executes archive SQL (schema_audit.py only inventories it, by prefix). READMEs kept. |
| G8 | cod/cor (ShouTech.Core.Inv) | 7 → 1 | −6 | The whole legacy mini-project is 203 lines in one namespace (Models/Security/invmod/invrep/invsvc/invuow) → navrep.cs (dominant). |
| G9 | Domain enums | 5 → 1 | −4 | AppMode/LicenseType/LicStat/LicEd → ModKey.cs; one namespace ShouTech.Domain.Enums, 70 lines total. |
| G10a | Domain interfaces: core + finance | 16 → 2 | −14 | IMasterCardRepository.cs absorbs IDefinition/IAsset/IProject/IFYear/ICompany/ICom/IUser/IUsrRepo/IDatabaseHealth/INavigation/ILicProv (−11); IReconciliationRepository.cs absorbs IJournal/ICheque/IBudget (−3). All one namespace. |
| G10b | Domain interfaces: purchase + operations | 11 → 2 | −9 | ICommercialDocumentRepository.cs absorbs IOrderDocument/IPurchaseRequisitionWorkflow/IPurchaseDocumentAttachment/IPurchaseMatchPolicy/IPurchaseLandedCost (−5); IOperationsRepository.cs absorbs IPlanning/IBom/IMfgOrder/IQuality/IPos (−5 → recount: 6 files → 1 = −5). ICrm/IHr stay standalone. |
| G11 | Domain entities (small) | 10 → 4 | −6 | Rol.cs absorbs RolPerm + Perm (−2); JournalEntry.cs absorbs JournalEntrySnapshot + GlPosting (−2); PurchaseMatchPolicy.cs absorbs PurchaseMatchEvaluationSnapshot (−1); PurchaseLandedCost.cs absorbs PurchaseDocumentAttachment (−1). |
| G12 | Reports windows: financial | 19 → 1 | −18 | Uniform code-only RptBase subclasses, one class per file, one namespace, no xaml, no partials → ClosingAccountsWin.cs (dominant). |
| G13 | Reports windows: inventory | 19 → 1 | −18 | Same pattern → MatMoveWin.cs (dominant). |
| G14 | Reports windows: party/sales/purchase | 17 → 1 | −16 | Same pattern → SupAgingWin.cs (dominant). |
| G15 | Reports windows: operations | 9 → 1 | −8 | Mfg/POS/QC/targets/price-list → PriceListWin.cs (dominant). GrniWin, CrmFunnelWin, HrReports, SalExceptWin, RptWinUtil stay standalone (large/feature-specific). |
| G16 | src/App services clusters | 11 → 2 | −9 | Purchase services (LandedCost/Match/Attachment) → PurchaseDocumentAttachmentService.cs (−3); platform services (Usr/FYear/Lic/ErpPlan/ModMenu/Com) → ErpSvc.cs (−6). One namespace ShouTech.Application.Services. |
| G17 | Infra landed-cost pair | 2 → 1 | −1 | LandedCostConsumptionAllocator → LandedCostAllocator.cs. |
| G18 | cod/app small helpers | 9 → 4 | −5 | BaseVM + DockedWinItem → ModWsVM.cs (−2); Localization Loc.cs → LocProps.cs (−1); EntityContextAction → QuickBarProps.cs (−1); RptDef → ReportModels.cs (−1). |
| G19 | svc host | 2 → 1 | −1 | ServiceHealth → Program.cs (host anchor). WebhookSigner excluded (test link); OutboundMessageWorker stays. |
| G20 | Tests: subject pairs | 7 → 3 | −4 | LandedCostAllocatorTests absorbs LandedCostConsumptionAllocatorTests (−1); PurchaseMatchServiceTests absorbs PurchaseMatchPolicyEvaluatorTests (−1); SchemaMigratorTests absorbs ErpPlanTests + BudgetServicePermissionTests (−2). |

**Projected total: 950 → ~758 files (−192, ≈ −20%).**

## Outcomes

(Filled in as each phase lands; final summary at branch completion.)

- **Consolidation G1–G20 — DONE.** Tracked files 950 → 765 (−185).
  Applied migrations 001–114 untouched throughout. Gates green per
  group (build 0/0, migrator idempotent, audit scripts exit 0).
- **Visual V1–V3 — DONE** (see VISUAL_PASS.md): central Win11 pass;
  Phenix v10 invoices/vouchers + Sahli Soft reports modelling; MWin
  Explorer-style shell, all 264 nav entries routed via RibbonNav.
- **Color (owner directive 2026-11-24 14:01) — DONE** (commit
  4ec471e): default accent = BLUE-GRAY slate (#51708E family) in
  VisuId palette + VisuId.xaml tokens; UsrPref default "slate";
  explicitly stored choices keep working. Supersedes the 13:59
  "keep gray" answer. Gates: ALL-GREEN.
- **Window content pass V4 — DONE** (commit b46b0a2): central-style
  audit + per-window cleanup (WhsWin/CatWin local label styles
  re-based on the central contract; CashDrawerOptionsWin fully
  localized, status line above the bottom button row). Presentation
  + wording only. Gates: ALL-GREEN.
- **Services B0 — DONE:** DesignFileSvc encrypted design files
  (AES-256-GCM; .strd/.stid/.stld; errors DSG-8101..8103 ×4).
- **Services B1 — DONE:** OptWin options window over UsrPref with
  live language/accent apply; Tools.Options routes to it.
- **Services B2 — DONE** (Report Designer completion; migration
  115): see SERVICES_GAP_INVENTORY.md B2 outcome. Gates: ALL-GREEN
  (build 0/0; migrator fresh through 115 + second run 0; 4 audit
  scripts exit 0; Errors parity 736 ×4; P2pHarness 39/39;
  W1Harness 36/36; Lang parity for touched keys 0 missing).
- **Services B3 — DONE** (Invoice Designer; migration 116): see
  SERVICES_GAP_INVENTORY.md B3 outcome. Gates: ALL-GREEN (build
  0/0; migrator fresh through 116 + second run 0; 4 audit scripts
  exit 0; Errors parity 736 ×4; P2pHarness 39/39; W1Harness 36/36;
  Lang parity for touched keys 0 missing).
- **Services B4 — DONE** (Barcode Designer; migration 117): see
  SERVICES_GAP_INVENTORY.md B4 outcome. Gates: ALL-GREEN (build
  0/0; migrator fresh through 117 + second run 0; 4 audit scripts
  exit 0; Errors parity pass; P2pHarness 39/39; W1Harness 36/36;
  Lang parity for touched keys 0 missing).
- **Services B5 — DONE** (Maintenance completion; no migration):
  Recalculate Costs window action (engine via OperationsService)
  + trial-balance integrity audit in MaintenanceWin; see
  SERVICES_GAP_INVENTORY.md B5 outcome. Gates: ALL-GREEN.
- **RTL mirroring sweep — DONE** (owner directive 2026-11-24
  14:55): docs/RTL_MIRROR_AUDIT.md — central FlowAlign end/start
  alignment applied to 96 physical-pin sites (incl. F2Ui.BottomBar
  and RptBase); CatWin verified explicitly. Gates: ALL-GREEN.
- **Ribbon audit + R1 — DONE:** docs/RIBBON_AUDIT.md (216 (a) /
  36 (b) / 4 (c) / 9 shell commands); the (c) language entries
  wired to the live language switch. Gates: ALL-GREEN.
- **Grid program R2a–R2d — DONE:** docs/GRID_PROGRAM.md — invoice
  grids, voucher grids, report grids, MatDirWin tree. R2e
  (CoaWin + AccCard) follows.
- **Bottom-buttons audit V5 + card sizes V6 — DONE** (see
  VISUAL_PASS.md; ComWin is the owner's named exception).
- **H1 F1 help routing — DONE** (owner decisions 2026-11-24
  15:36 + 15:38, action 1): BaseWin.HelpTopicKey (default =
  class name) + App-level F1 class handler for non-BaseWin
  windows; HelpWin takes the topic key and lands on a matching
  section or shows the graceful topic-pending note
  (Help.TopicPending ×4). Gates: ALL-GREEN.

## Standing rule (owner, 2026-11-24 15:54)

**Find the best and work with it** — program-wide, not only for
the help system: every window, workflow, pattern and technical
choice is based on the best pattern found in research (the four
benchmarks, global products, documented best practice), adopted
and adapted — never the familiar or first option by default.
Comparison notes record what was chosen, what was rejected, and
why the chosen one is better on the owner's criterion: POWER +
EASE for the user (2026-11-24 15:30).

**Benchmark pool + weighting (owner, 2026-11-24 16:31 + 16:32):**
the four benchmarks (ERPNext, Odoo, Al-Ameen, Phenix) are the
CLOSEST to what ShouTech aims to be and remain the PRIMARY
reference in every study. The pool is not closed: each phase
study also scans for any other world-class product leading in
that phase's area (SAP/Dynamics/QuickBooks/Sage/Tally/Zoho,
global SaaS UX, as the area suggests) — but an outside product
enters only per-item, when demonstrably better on a specific
feature or pattern, and then only that piece is taken. Default
to the four; extras earn their place item by item, never by
general reputation. Each study records which products were
added beyond the core four and what was taken from each.
Criteria for every comparison (phase-gate rule, 16:28): POWER,
FLEXIBILITY, USER-FRIENDLINESS, FORM (fields/buttons counted,
shapes and proportions compared). Commercial bar (15:29):
ShouTech must compete with and be better than the four
benchmarks to be sellable as enterprise commercial software.

## Commercial-phase workstreams (registered, not in this branch's scope)

- **Help system (owner decisions 2026-11-24 15:36 + 15:38;
  design RESEARCHED and RATIFIED by the owner 2026-11-24 15:54
  — "find the best and work with it"; full sourced report:
  ~/workspace/research_notes/global-f1-help-display-protection-20261124-1248/report.md):**
  the manual becomes the F1 help system. (a) Package = SIGNED +
  ENCRYPTED: a manifest with a vendor signature (the viewer
  refuses tampered packs — MS Help Viewer precedent) AND
  AES-256-GCM encryption under the DesignFileSvc discipline,
  proprietary extension **.sthx**, opened only by ShouTech;
  never CHM (decompilable by a built-in Windows command);
  (b) viewer = in-app **WebView2** (topics never extracted to
  disk, no external browser/hh.exe), tri-pane Contents / Index /
  Search, RTL-mirrored skin; the search index is a PRE-BUILT
  Lucene.Net index shipped inside the package (Arabic analyser
  for the Arabic pack); context mapping by stable topic IDs via
  a map/alias file (the universal standard — Oracle/Flare/
  RoboHelp/CHM); WPF has no built-in F1 provider — the canonical
  pattern is an attached property + ApplicationCommands.Help;
  F1 fallback chain per press: field topic → window topic →
  module topic → help home; (c) per-window INDEX: every window
  has an index entry mapping it to its topic(s) with keywords
  per topic per help language; index coverage (windows indexed /
  total windows) is reported in the commercial-phase report; a
  window without an index entry is a defect (the audit's
  topic-key column is the enforcement mechanism — H1 landed the
  keys and the routing); (d) the same content exportable as a
  permission-protected PDF (official printable/archive copy) —
  true digital signing is a RELEASE-PROCESS item unless in-stack
  signing tooling is confirmed in the commercial phase; the
  protected PDF ships, the signature step is not faked;
  (e) content authored per window as windows finalize, Arabic +
  English first. Honest protection limit (from the research,
  recorded verbatim in the spec): encryption = casual-copy
  resistance, signing = tamper-evidence; nothing stops a
  licensed user copying what they can read — no researched
  vendor achieves that offline, and we do not claim it.
  Source material: the owner's uploaded manuals, incl. the
  Al-Ameen classic help text for benchmark structure.
- **POS family completion** (owner workstream 2026-11-24 15:31):
  IN PROGRESS in this branch — docs/POS_FAMILY.md (audit, P1
  terminal definition DONE; P2 session picker, P3 session sales
  follow).
- **Phenix-verified named gaps** (owner's own install, 2026-11-24
  17:47; full annex in docs/PHENIX_V10_EVIDENCE.md): document
  audit (تدقيق المستندات), task scheduling (جدولة المهام),
  automated-report options, permission templates, glossary,
  helper phrases, external tools, the DELIVERY family (driver
  card, delivery prices, pickup orders, delivery search, driver
  cash-box closing), and the manufacturing report gaps (four
  cost-variance reports, manufactured-material costs, feasibility,
  re-posting of manufacturing operations). Recorded for the
  commercial-phase/comparison backlog — owner's decision; NOT
  scoped into the bin/numbering workstreams.
- **PosTermWin comparison + gap build** (owner directive
  2026-11-24 17:50): QUEUED immediately after the numbering
  workstream — field-by-field vs the verified Phenix «تعريف نقاط
  البيع» window; build the genuine gaps (per-point document-type
  binding, separate cashbox-closing account, per-point currency,
  per-point print designs bound to doc_designs/.stid), weigh
  customer-group scoping and touch/field customization, record
  where ours is ahead (warehouse, price level, permitted users).
- **POS material information pane + live tafqeet** (owner
  evidence 2026-11-24 17:55; transcription §7): QUEUED with the
  POS comparison work — the POS sales screen gains an info pane
  for the selected/entered material showing PROJECTED total
  balance (before ← after the current line), per-warehouse
  balances, sale price, weighted-average purchase cost, last
  purchase price + date, and last price sold to the current
  customer; the displayed property set is configurable per
  user/point from a checkbox list (Phenix offers ~35 properties
  incl. price levels, min/max sale & purchase, FIFO, barcode,
  packing, weight, category, groups, sales snapshot) and persists
  through WinPref. Plus the live amount-in-words total (KWD
  tafqeet) on the POS screen. The build step records which
  properties are implemented vs deferred with reasons.


## Commercial-readiness phase (owner go-ahead 2026-11-24 16:22) — PLAN

Starts when this branch's work is complete (R3–R5 + the final
reduce report) and the owner gives the merge/phase decision. Goal:
the program ready for WORK and SALE, with maximum protection
against tampering, hackers and reverse engineering — honestly
bounded (see the limit at the end of this section).

1. **Licensing build** — implement
   ~/workspace/research/dongle-module-licensing-design.md
   (AppHost.Entitlements + nav guard over the 215 mapped ribbon
   buttons; provider chain Dongle → LicenseFile → Online →
   DevMode → read-only). The design's open decisions are the
   OWNER's, put to him as options when the phase opens — not
   decided by the builders:
   - **D1** lapsed-module data policy (proposal: hide UI + block
     writes, data preserved/readable via Core reports; alternates
     in the design doc §6).
   - **D2** unlicensed install behaviour (proposal: app opens
     read-only, LicenseWin/AddonsWin reachable, LIC-8004 shown —
     a licensing failure never bricks access to the customer's
     own data).
   - **D5** dongle hardware reality (is there real HID hardware /
     a vendor SDK behind VID 0x5348/PID 0x4552? Until the native
     stubs are productized, the signed license file bound to the
     dongle serial is the shippable mechanism).
   The R5 pass in this branch completes the LicenseWin/AddonsWin
   UI foundation the build stands on.
2. **Protection workstream** (research-first per the standing
   find-the-best rule; every evaluation records chosen vs
   rejected with reasons, from vendor docs + independent
   sources):
   a. **.NET code protection** — evaluate obfuscation /
      virtualization protectors for the shipping build
      (commercial protectors vs open-source options): impact on
      the build pipeline, WPF/XAML pitfalls (resource names,
      reflection over view-models, the Lang/Errors JSON loaders),
      startup/runtime performance cost. Deliverable = evaluation
      + recommendation; the owner picks before anything is
      applied to the shipping configuration.
   b. **License enforcement** = workstream 1 above (signed
      license blobs, fail-closed for granting, fail-open only
      for read access to existing data, every grant/block in
      security_audit_log with its LIC code).
   c. **Authenticode signing** of all binaries + the installer
      (certificate procurement is an owner/commercial action;
      the pipeline signs when a certificate is configured and
      reports plainly when it is not — unsigned builds are
      never described as signed).
   d. **Integrity + encryption, one coherent story** — verify
      end-to-end what this branch already built: design files
      (.strd/.stid/.stld, AES-256-GCM + integrity, DSG-8101..03),
      the help package (.sthx, signed + encrypted, commercial
      workstream above), encrypted backups; add startup
      integrity self-checks for the shipped binaries where the
      chosen protector does not already provide them; gaps
      recorded, not papered over.
   e. **Database-level protection for an offline-first app** —
      only what is real on a customer machine: encrypted backup
      files, the existing RBAC + audit trail, guidance for
      NTFS permissions on the data folder, PostgreSQL's own
      auth/TLS when the customer runs a server. NO fake
      server-side promises: a local SQLite file is the
      customer's data on the customer's disk.
3. **Installer / packaging** — one signed installer: fresh
   install, in-place upgrade that runs migrations safely,
   uninstall that preserves customer data by default; the
   .NET runtime story (self-contained vs framework-dependent)
   decided in the evaluation under 2a.
4. **F1 help system** — as registered in the workstreams
   section above (ratified design; .sthx package, WebView2
   viewer, per-window index with coverage reporting).
5. **Manuals** — user manual generated from the help content
   (the protected PDF is the official copy); installation /
   administration guide (deployment, backup, licensing,
   protection settings as actually implemented).
6. **Demo data** — a clearly-labelled demo company with a
   realistic dataset (chart, materials, parties, documents
   across a fiscal period) + a one-click reset; demo data
   never mixes into real companies.
7. **Release QA gate** — the Windows verification backlog is
   this phase's exit gate: Kern.Tests execution, WPF
   click-through of the full window set, live PostgreSQL
   migration + posting run, and a clean-machine install test.

**HONEST LIMIT (owner, 2026-11-24 16:22 — recorded, never
overstated):** a desktop application running on the customer's
own machine cannot be made uncrackable. The professional goal
is layered cost-raising (protection + licensing + signing),
tamper-EVIDENCE (integrity checks, signed packages, audit
logs), and enforceable licensing — not impossibility. Any
protection claim in documentation or marketing copy must match
what is actually implemented at the time it is written.

### IP / rights layer (owner addition 2026-11-24 16:24)

Alongside the technical protection — "of course we want RIGHTS
protection":

1. **EULA** shipped with the installer (accepted at install)
   and viewable in-program (About window), Arabic + English at
   minimum. A professional template is drafted in this branch
   at docs/legal/EULA_TEMPLATE.md (grant, restrictions,
   liability, term/termination, governing-law placeholders).
2. **Copyright / ownership notices** in the About window, the
   splash screen, and printed-output footers where professional
   products carry them (invoice/report print footers via the
   designer defaults — text only, no layout change).
3. **Per-customer license certificates** generated by the
   licensing system at issuance: customer name, licensed
   modules, seats, term, license id — a printable record
   (PDF/print from LicenseWin) documenting ownership and grant
   per sale, matching the signed license blob's contents.
4. **Activation binding** — a license binds to the customer's
   installation exactly as the licensing design specifies
   (dongle serial / machine binding per D5's outcome).

**Lawyer's step (recorded plainly):** formal legal review of
the EULA and any trademark/copyright registration in the
owner's jurisdiction is a lawyer's work. We prepare the
templates and the in-product surfaces; we do not replace that
review, and the shipped EULA must be the reviewed text.
