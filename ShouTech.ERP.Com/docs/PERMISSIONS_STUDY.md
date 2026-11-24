# PERMISSIONS_STUDY — Permissions system + templates (feat/permissions-templates)

Owner authorization: 2026-11-24 19:49, as an independent workstream.
Owner scope statement (2026-11-24 19:47): "الصلاحيات تبويباتها كثيرة جدا
وهي تشمل جميع نوافذ البرنامج" — permissions have very many tabs and
cover ALL of the program's windows.
Owner IP rule (2026-11-24 19:49): do NOT copy Phenix's windows as-is
(intellectual property). Functional scope and logic are modelled on
the evidence; the FORM is our own (§5).

Evidence: Phenix permission-template window, owner's screenshots,
transcribed in
`~/workspace/research_notes/phenix-tools-tab-inventory-20261124-1335/notes/options-sections-20261124.md`
(bonus section): a template window with tabs (القوالب، الصلاحيات،
صلاحيات الفواتير، صلاحية السندات، صلاحية الطلبات، صلاحية نقاط البيع،
الأوراق المالية، صلاحيات متقدمة), a module tree spanning every window
family, and a per-invoice-type matrix × (عرض، إضافة، تعديل، حذف،
طباعة، إظهار الحقول، توليد سند قيد، تحديد الحساب) — view, create,
update, delete, print, show-fields, generate-journal, set-account.

Cross-program research (same date, treated as unverified
web-derived reference data per that directory's house rules —
patterns adopted below were each checked against our own measured
substrate in §1 before being ruled on):
`~/workspace/research_notes/phenix-tools-tab-inventory-20261124-1335/notes/other-programs-windows-research-20261124.md`
§3 (permissions survey of ERPNext/Frappe, Odoo, Dynamics 365 BC/F&O,
SAP Business One, Al-Ameen, Tally, iDempiere, Tryton) and its
shortlist. §2.6 records what was folded in and what was ruled out.

All counts below were measured on this branch's base (reduce tip
6cf627e5): a fresh SQLite database migrated through 124, plus static
scans of `cod/app/src`.

---

## 1. Substrate audit — what exists today

### 1.1 The catalog exists twice, and the two copies disagree

- **In code**: `PermCat` (Services/Permissions.cs) declares ~327
  permission constants in 26 nested classes.
- **In the database**: the `permissions` table holds **371 rows**
  (seeded by migrations 002/004/015/028/038/053–057/065/066/071/083/
  085/086/095–106…), each with group/feature/operation codes,
  bilingual names, display order and a sensitivity flag.
- **Drift, code → DB**: 14 `PermCat` codes have NO row in a freshly
  migrated database: `ACC.CUSTOMER_LEDGER.VIEW`,
  `ACC.FINANCIAL_ANALYSIS.VIEW/.AI`, `ACC.FINANCIAL_STATEMENTS.VIEW`,
  `AI.ASSISTANT`, `AI.FINANCIAL_ANALYSIS`, `AI.FORECAST`,
  `AI.INVENTORY_ANALYSIS`, `AI.SALES_ANALYSIS`,
  `AUDIT.SECURITY_EVENTS.VIEW`, `BANK.RECONCILIATION.VIEW/.EXECUTE`,
  `VCH.PAYMENT_METHOD.MANAGE` — and, pointedly,
  `SYS.PERMISSION_TEMPLATES.MANAGE`: the key that would govern
  permission templates exists in code but has no database row, no
  window and no consumer. Templates are a named-but-absent feature.
- **Drift, DB → code**: 58 database codes have no `PermCat` constant:
  the legacy underscore family (`COMPANY_VIEW`, `SALES_EDIT`,
  `USER_CREATE`, … 26 codes from migration 002), the short
  `PermKey` family (`ACC.VIEW`, `PURCH.POST`, …), and the
  purchase-cycle `PUR.*` family (13 codes, used by the P2P services
  as string literals).
- **In-code catalog is itself incomplete**: `PermCat.BuildCatalog()`
  reflects over only 21 of the 26 nested classes. `Quality`, `Hr`,
  `Crm`, `Projects` and `Assets` (≈42 keys) are declared but never
  enter `PermCat.All`, so `PermCat.Contains()` denies them and the
  administrator fallback `GrantAllCatalogPermissions` silently omits
  them.

### 1.2 Runtime self-healing exists on SQLite only — and it is destructive

`SqliteSchemaBootstrapper.EnsureEnterprisePermissionSchema`
(Services/Data.cs) runs on every company open (and inside
`PermServ.LoadSqliteAsync`). It upserts every `PermCat.All` code into
`permissions` (healing the code→DB drift above, with derived names)
and then **rebuilds the ROLE_USER and ROLE_VIEWER baselines with
DELETE + re-INSERT**: ROLE_USER receives every active permission
except a protected list; ROLE_VIEWER receives every `VIEW`-operation
permission. Consequences:

- Any administrator customization of `role_permissions` for the two
  non-admin roles is silently destroyed at the next company open.
- PostgreSQL has NO equivalent bootstrapper: on PG the catalog is
  exactly what migrations seeded, so the 14 missing codes stay
  missing there. Provider parity for the catalog does not exist.

### 1.3 Roles are a fixed trio; templates do not exist

- Exactly three roles per company — `ROLE_ADMINISTRATOR`,
  `ROLE_USER`, `ROLE_VIEWER` — created at company setup
  (Company.cs); the administrator role receives every permission.
  There is no custom-role or template concept anywhere: no tables
  (the only `*template*` tables in the schema are quality
  templates), no service, no UI.
- The ribbon proves the gap: `OtherTools.PermissionTemplates` and
  `OtherTools.Permissions` BOTH open `PrmWin`, the per-user override
  editor. The "templates" entry is a label over the user editor.
- `PermAdminSvc` (the PrmWin backend) edits ONLY per-user overrides
  (`user_permissions` allow/deny deltas against the role baseline),
  refuses to edit administrator-role users, and requires
  `SYS.PERMISSIONS.MANAGE`.

### 1.4 The only administration window cannot show 19% of the catalog

`PrmWinVM` builds its tabs from `permission_groups` (21 rows) but
matches permissions to tabs by CODE PREFIX via a hard-coded map.
Measured on the migrated database: **71 of 371 permissions (19%)
appear in no tab** — all of HR (14), QUALITY (9), the `PUR.*`
purchase family (13), AST (4), PRJ (5) and the 26 legacy underscore
codes — because their prefixes are unmapped and their group codes
(`HR`, `QUALITY`, `ASSETS`, `PROJECTS`, `PURCHASES`) have no
`permission_groups` row at all. An administrator literally cannot
see, grant or deny a fifth of the system from the UI.

### 1.5 Enforcement — broad probing, thin action gating, no per-type granularity

- 84 of 114 view files reference the permission service; 30 do not
  (among them CoaWin, MatDirWin, CatWin, WhsWin, PosTermWin,
  PosSalesWin, PurchaseReturnWin, OptWin — plus startup/picker
  windows where a check is not meaningful).
- The dominant pattern is `Has(...)` probes that enable/disable UI
  and deny-close gates when a window opens. The audited action seam
  `Require(...)` — the one that writes an AccessDenied audit row —
  has exactly **2 call sites** (both in PayRecWin).
- `RibbonNav` performs NO permission gating: every nav entry opens
  its window unconditionally; windows self-gate after opening.
- **No per-document-type granularity exists.** InvoiceWin — the one
  window for all invoice kinds — checks the generic
  `SALES.INVOICE.*` / `PURCH.INVOICE.*` keys regardless of the kind
  on screen. There is no way to let a cashier post POS sales but not
  purchase invoices, which is the evidenced Phenix matrix's core
  capability.
- Denials surface through the error catalog (`AUTH-2005`
  PermissionDenied) in guarded windows; the loader resolves
  effective permissions as role grants ∪ per-user allows − per-user
  denies, with `ROLE_ADMINISTRATOR` receiving every active
  permission minus explicit denies.

### 1.6 Family-by-family enforcement summary

| Family | Catalog keys (DB) | Enforcement today |
|---|---|---|
| System/users/roles | SYS.* 20 (+26 legacy) | PrmWin gate (SYS.PERMISSIONS.MANAGE); user/role admin windows self-gate |
| Companies/branches/warehouses (ORG) | 12 | ComWin/WhsWin: WhsWin has NO checks; company setup is admin-only by flow |
| Inventory (INV) | 38 | MatCard/CatWin partial probes; MatDirWin, CatWin, WhsWin no checks |
| Accounting (ACC) | 30 | JrnWin (reverse only), CoaWin NO checks, ledgers/reports probe VIEW keys |
| Vouchers (VCH) | 12 | CshWin View/Create/Post probes; ChqWin securities probes |
| Sales (SALES) | 47 | InvoiceWin generic-kind probes (create/update/post/approve/overrides); OrdWin 20 refs; returns via SalesReturnWin |
| Purchasing (PURCH + PUR) | 19 + 13 | OrdWin probes; P2P services check PUR.* literals at action seams |
| POS | 18 | PosWin override probes (discount/price/hold/bypass); PosTermWin & PosSalesWin NO checks |
| Customers/CRM (CRM) | 24 | CstCard + CRM windows probe VIEW/EDIT keys |
| Representatives (REP) | 12 | RepCard probes |
| Manufacturing (MFG) | 16 | Mfg windows probe VIEW/MANAGE keys |
| HR | 14 | HR windows probe keys — but the keys are invisible in PrmWin (§1.4) |
| Banking (BANK) | 6 | BankRecWin probes |
| Delivery | 11 | DeliveryDeskWin probes |
| Quality | 9 | QC windows probe keys — invisible in PrmWin |
| Projects (PRJ) / Assets (AST) | 5 / 4 | ProjWin/AssetWin probe keys — invisible in PrmWin |
| Reporting | 8 | RptBase probes REPORT.VIEW family |
| Data/Backup/Audit/Licensing/Updates/AI/Help | 6/6/2/5/3/1/4 | DatWin/BkpWin/ActivityLogWin probes; licensing deliberately unenforced (owner-postponed) |

---

## 2. Target model

### 2.1 One catalog, completed and reconciled (migration 125)

- Seed the 14 missing `PermCat` codes into `permissions` (fixes
  PostgreSQL parity; SQLite's bootstrapper upsert already heals
  them with derived names — the migration gives both providers the
  same curated rows).
- Add the missing `permission_groups`: HR, QUALITY, ASSETS,
  PROJECTS, and a new DOCUMENTS group for §2.3; normalize the stray
  `PURCHASES` group code to `PURCHASING`.
- Fix `PermCat.BuildCatalog()` to include the five omitted nested
  classes (Quality, Hr, Crm, Projects, Assets) so `PermCat.All` is
  the whole declared catalog.
- Fix `PrmWinVM` grouping to use each permission's stored
  `group_code` (extended through `PermAdminSvc`) instead of the
  prefix map — every catalog row becomes visible in its family tab.

### 2.2 Templates — named, reusable grant sets (migration 125)

New tables (both dialects, §3):

- `permission_templates` — one row per template: owning company
  (empty/NULL = global system template), unique code per company,
  bilingual name, description, `is_system` (seeded, immutable
  through the service), active/deleted flags, full audit columns.
- `permission_template_grants` — template × permission rows.
- `user_permission_templates` — user × template assignments with
  assigner and timestamp.

Resolution rule (extends the existing loader semantics, nothing
replaced): **effective(user) = role grants ∪ template grants (via
assignments) ∪ per-user allows − per-user denies**; administrators
still receive every active permission minus explicit denies. The
loader change is a single additional `OR EXISTS` arm in both
provider queries of `PermServ`, restricted to active, non-deleted
templates of the user's own company or global ones. Because
templates live outside `role_permissions`, the bootstrapper's
baseline rebuild (§1.2) cannot destroy them.

Seeded global system templates (grants inserted by permission-code
pattern, administrator-copyable into company templates):
`SYS_READ_ONLY` (every VIEW operation + report print),
`SYS_CASHIER` (POS family + cash-sale creation),
`SYS_ACCOUNTANT` (accounting + vouchers + banking + report
view/print/export). System templates cannot be edited or deleted;
Copy makes an editable company template from any template.

Administration is guarded by `SYS.PERMISSION_TEMPLATES.MANAGE`
(now seeded, §2.1) at the service layer, mirroring the
`PermAdminSvc` guard pattern, and every create/update/delete/
assign/copy writes a `security_audit_log` row (the existing
migration-029 trail) inside the same transaction.

### 2.3 Per-document-type matrix (the evidenced core)

New catalog family `DOC.<KIND>.<OP>` in a new DOCUMENTS group:

- Kinds (the InvRepo document kinds + the two order kinds):
  SAL sales invoice, SRT sales return, POS POS sale, PUR purchase
  invoice, PRT purchase return, ENT stock entry, EXT stock exit,
  TRN stock transfer, OPN opening stock, CNT inventory count,
  QUO quotation, SO sales order, PO purchase order.
- Operations (the evidenced set): VIEW, CREATE, UPDATE, DELETE,
  PRINT, POST (= توليد سند قيد, generating the journal),
  FIELDS (= إظهار الحقول, seeing sensitive fields — cost/margin),
  ACCOUNT (= تحديد الحساب, setting the document's account).
  13 kinds × 8 operations = 104 keys; POST/DELETE/FIELDS/ACCOUNT
  are sensitive.

**Enforcement semantics (no lockout, real denial).** `PermServ`
additionally loads the user's explicit per-user DENY codes into a
separate set (`IsExplicitlyDenied`). A document action is allowed
iff: NOT explicitly denied the DOC key, AND (holds the DOC key OR
holds the pre-existing generic key for that action). Template
grants therefore ADD per-type rights on top of any role, per-user
denies SUBTRACT a single type from a broad generic grant, and every
existing installation behaves exactly as before until an
administrator uses the new controls. Action seams call `Require`
with the document context, so per-type denials are audited like
every other action denial.

Wiring in this workstream:
- **InvoiceWin** (all 11 InvRepo kinds): open = VIEW, save = CREATE
  (new) / UPDATE (existing), post = POST, delete/bin = DELETE,
  print = PRINT, live margin + cost readouts visible only with
  FIELDS, the header account pickers editable only with ACCOUNT.
- **OrdWin** (SO/PO): open/save/post/cancel seams with the same
  rule (generic fallbacks = the existing Sales/Purchasing order
  keys).
- **POS**: posting a till sale = DOC.POS.POST (fallback
  POS.INVOICE.CREATE); PosSalesWin (the session's sales — today
  unchecked) opens with DOC.POS.VIEW (fallback POS.REPORTS.VIEW).

### 2.4 Management windows — our form (see §5 for divergences)

- **PermTplWin** (new): template manager. Left rail = template
  list (company templates + global system templates, badged);
  body = the selected template's grants as a family rail +
  matrix: for the DOCUMENTS family a true kind × operation matrix
  of checkboxes; for every other family, feature rows with a grant
  checkbox and sensitivity marker. An "Assigned users" checklist
  (company users) sits with the template it belongs to. All action
  buttons at the bottom. Ribbon `OtherTools.PermissionTemplates`
  now opens THIS window (gated by SYS.PERMISSION_TEMPLATES.MANAGE);
  `OtherTools.Permissions` keeps opening PrmWin.
- **PrmWin** stays the per-user override editor, fixed per §2.1 so
  all families (incl. DOCUMENTS, HR, QUALITY…) are visible.

### 2.5 Errors and language

- New error block **PRM-8501…8599** (8501 load failed, 8502 save
  failed, 8503 code duplicate, 8504 system template immutable,
  8505 assignment company mismatch, 8506 template not found) in
  ErrorCodes + the four Errors catalogs.
- All new UI text through a new `PermTpl` Lang section ×4.

### 2.6 Cross-program patterns folded in (research shortlist, ruled per item)

ERPNext/Frappe's core lesson — *separate the mechanisms* — validates
the shape of §2.2–2.3: a per-document-type role matrix, record
restrictions, field levels and sharing are four different machines,
not one grid. Our model keeps them separate too; each mechanism is
ruled on below on its own merits.

- **Permission Inspector (ERPNext) — BUILT.** The research
  shortlist's power+ease winner, and cheap on top of §2.2: a
  `PermServ.ExplainAsync(userId, permissionKey)` that answers WHY —
  returning the deciding source (administrator role / which role's
  grant / which assigned template's grant / per-user allow /
  explicit per-user deny / granted nowhere). It reads the same
  tables the loader reads, so the explanation cannot disagree with
  enforcement. PermTplWin carries a compact effective-access check
  strip (user picker + permission picker + verdict line) — our own
  placement of the idea, not ERPNext's DocType form. The harness
  proves the verdicts against the loader's actual outcomes.
- **Role Profiles (ERPNext) / Master Role (iDempiere) / composable
  Permission Sets (Dynamics BC) — CONFIRMS §2.2.** Bundled named
  grant sets are the industry template mechanism; our system
  templates + Copy are the same idea in our form. One deliberate
  exclusion: set-includes-set nesting (BC) and implied-group chains
  (Odoo) are NOT in the resolution rule — §2.2's loader stays a
  single flat union so the Inspector's verdict is always one
  named source, never a chain to unravel. The ease those products
  get from hierarchy-by-dropdown is answered in our form by the
  one-click system-template starting profiles.
- **Secrecy degrees (Al-Ameen: low/medium/high per operation) —
  PARTIAL ANALOGUE, full model is a recorded gap.** Our catalog
  already carries the binary end of this axis: `is_sensitive`
  marks the high-secrecy operations, and they are exactly the keys
  the seeds protect (admin-only defaults, ROLE_USER exclusions).
  A graded three-degree scale with a per-user clearance level is
  a different axis with no consumer today; §4 records it.
- **Field-level permissions (Frappe Permission Levels, Odoo
  groups-on-fields) — COARSE FORM BUILT, fine form gap.**
  `DOC.<KIND>.FIELDS` is the field-visibility gate where it
  matters most commercially (cost/margin on documents). Arbitrary
  per-field levels on every window are not built (§4).
- **Record-level restrictions (Frappe User Permissions, Odoo
  Record Rules) — GAP with reason (§4).** Our tenancy is one
  company per database (SQLite) / company-keyed rows (PG); the
  only record scoping in the product is POS terminal
  customer-group scoping (migration 124). A general domain-rule
  engine is a platform feature of its own, not a permissions
  workstream add-on.
- **Sharing (per-record grants to a named user) — GAP (§4).**
  No record-sharing substrate exists; documents carry company
  ownership only.
- **Four-eyes button rules (Tryton: N *different* users must
  approve) — DIFFERENTIATOR CANDIDATE, recorded with hook
  points.** Our approval seams already separate duties through
  dedicated APPROVE keys (SALES.QUOTATION/ORDER/INVOICE.APPROVE,
  SALES.RETURN.APPROVE, PUR.REQ.APPROVE) and record
  `sau_approval_history`; a distinct-user/quorum rule layers onto
  those seams without touching the permission schema. It is the
  first follow-up candidate after this workstream (§4), not part
  of it — approval *workflow* semantics, not grant resolution.

---

## 3. Schema (migration 125, both dialects)

```sql
permission_templates(
  template_id   INTEGER PK / BIGINT IDENTITY,
  company_id    TEXT / UUID NULL,          -- NULL/'' = global
  template_code TEXT NOT NULL,
  name_en TEXT NOT NULL, name_ar TEXT NOT NULL,
  description TEXT NULL,
  is_system INTEGER/BOOLEAN NOT NULL DEFAULT 0,
  is_active ... DEFAULT 1/TRUE,
  is_deleted ... DEFAULT 0/FALSE,
  created_by TEXT, created_at ..., updated_by TEXT, updated_at ...,
  UNIQUE(company_id, template_code))
permission_template_grants(
  template_id FK, permission_id FK, PK(template_id, permission_id))
user_permission_templates(
  user_id TEXT/UUID, template_id FK,
  assigned_by TEXT, assigned_at ..., PK(user_id, template_id))
```

Plus the catalog seeds of §2.1/§2.2/§2.3 and DOC grant mirroring
for PostgreSQL role baselines (roles holding a generic document
key receive the matching DOC keys, replicating what the SQLite
bootstrapper derives automatically).

**Merge note**: this branch numbers its migration 125. The reduce
branch may land its own 125 first (a parallel workstream is
active); a renumber at merge time is expected — precedent: the
purchase cycle renumbered 092–099 → 107–114 at merge.

---

## 4. As-built scope vs recorded gaps

**Built in this workstream**: §2.1 catalog reconciliation +
PrmWin visibility fix; §2.2 templates (schema, service, loader
union, system seeds, audit); §2.3 DOC catalog + InvoiceWin /
OrdWin / POS enforcement as specified; §2.4 PermTplWin + ribbon
rewiring; §2.5 errors + Lang; §2.6 Permission Inspector
(`ExplainAsync` + PermTplWin access-check strip); a PermHarness
proving grant/deny end-to-end through the real service + loader on
a migrated SQLite database, including Inspector verdicts matching
loader outcomes.

**As-built verification (2026-11-24, branch
`feat/permissions-templates`)**: landings L1–L3 `20fbf684` +
`aecf67a3` (migration 125 both dialects, PermCat completion,
loader union + deny set, Inspector, PermTplSvc, PrmWin fix, PRM
error catalog, PermTpl Lang), L4 `2d1a52b1` (PermTplWin, Myrtle
tint, RibbonNav), L5 `5295560e` (DOC enforcement in InvoiceWin /
OrdWin / PosWin / PosSalesWin), L6 (PermHarness + this note).
Gates at the final tip: solution build 0 errors / 0 warnings
(--no-incremental); SqlMigrator fresh-apply + idempotent through
125 (489 permissions, 104 DOC, 26 groups, system templates
148/58/27 grants); the 4 audit scripts exit 0; Errors parity
177 keys ×4 exact-set; Lang PermTpl section 41 keys ×4 exact-set
(the base catalogs carry a pre-existing divergence outside this
section — untouched). Harnesses: PermHarness 37/37 — including
the proof that the set of stored codes with no `PermCat` constant
is EXACTLY the 58-code drift documented in §1.1 (26 migration-002
underscore relics + 17 PermKey short codes + 13 PUR.* + 2
INV.VALUATION.*), no more, no less — plus the pre-existing suites
unchanged: P2p 39/39, W1 36/36, Bin 22/22, Num 27/27, Info 35/35.
PostgreSQL legs are code-mirrored and migrator-verified for
syntax shape only; a live PG run and the WPF click-through remain
Windows-owed, as for every workstream on this base.

**Recorded gaps (not built, with reasons)**:
- **Full ribbon/nav gating** (every nav entry checked before its
  window opens): the shell's established pattern is window
  self-gating on open; converting 266 entries is a shell workstream
  of its own. Only the two permission entries gain nav gates here.
- **Voucher/securities per-type matrices** (Phenix's صلاحية
  السندات / الأوراق المالية tabs): CshWin/ChqWin keep their
  existing VCH.* probes; a DOC-style VCH.<type> family is a
  follow-up once the invoice/order matrix proves out.
- **Windows with no checks at all** (CoaWin, MatDirWin, WhsWin,
  CatWin…): their families' VIEW keys exist and are now visible/
  grantable, but adding open-gates to master-data browsers is a
  behavior change for every existing user and belongs to a
  dedicated enforcement sweep, not this branch.
- **PostgreSQL runtime proof**: gates run the migrator on SQLite;
  PG DDL is written dialect-parallel and reviewed, but no live PG
  instance is available on this machine (standing Windows/PG-owed
  item, same as every prior workstream).
- **Kern.Tests on Windows**: cannot execute on this Linux VM
  (standing owed item).
- **Graded secrecy degrees (Al-Ameen model, §2.6)**: our
  `is_sensitive` flag covers the binary high-secrecy end; a
  low/medium/high scale with per-user clearance has no consumer
  in the product today.
- **Record-level restrictions and per-record sharing (§2.6)**:
  no domain-rule engine or sharing substrate exists; POS
  customer-group scoping (migration 124) is the only record
  scoping in the product. Platform-sized features, recorded for
  a future workstream.
- **Fine-grained field levels beyond documents (§2.6)**: only
  the DOC.*.FIELDS gate is built; per-field levels on arbitrary
  windows are not.
- **Four-eyes / quorum approval rules (Tryton model, §2.6)**:
  the first follow-up candidate — the APPROVE-key seams and
  `sau_approval_history` already exist; the distinct-user/quorum
  rule itself is approval-workflow work, not grant resolution.

---

## 5. IP design-divergence note (owner rule, 2026-11-24 19:49)

Phenix's FUNCTIONAL scope is the model — templates, an
all-windows permission catalog, per-document-type operation
matrices. The FORM is deliberately NOT Phenix's:

- Phenix presents one window of many sibling TABS (templates,
  general permissions, then one tab per document family). Ours is
  TWO windows with distinct jobs — PrmWin (per-user overrides,
  pre-existing) and PermTplWin (templates) — because in our shell a
  ribbon entry maps to one focused window, and template editing is
  a rarer, heavier task than user overrides.
- Phenix's template window uses a tab strip + module tree +
  per-cell dropdowns. PermTplWin uses our established OptWin idiom
  instead: a left selection rail (templates), a family rail, and a
  checkbox matrix grid in the window's own schedule tint — the
  same visual language as the rest of the program (Win11 central
  design system, slate accent), with assignment co-located in the
  same window rather than in a separate user screen.
- Permission and operation NAMES, codes (`DOC.SAL.POST`…), and all
  texts are our own catalog's; no Phenix labels, layout geometry,
  icons or wording are reproduced.
