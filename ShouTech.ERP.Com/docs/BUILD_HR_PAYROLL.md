# BUILD — HR / Payroll: Build Report (migration 065, branch `build/hr-payroll`)

Design: `docs/BUILD_HR_DESIGN.md` (written first, research-first, from
`~/workspace/research/sweep/compare-mfg-hr-crm.md` §2 + ERPNext HRMS
practice). Baseline grade: **HR 0/42 CORE, 0%**. This report maps
design → built, documents the GL mapping as implemented, ships the
per-window comparison tables and the CORE re-grade, the pillars
checklists, the headless proof, and the gates.

**Not pushed, not merged.** WPF interaction and a live PostgreSQL run
are still owed (Linux build/proof environment) — stated plainly, per
the standing rule: what is proven below is proven headlessly against
the real repositories on real migrated SQLite; the windows compile
into the 0/0 build and follow the app standards, but no one has
clicked them on Windows yet.

---

## 1. Design-vs-built mapping

| Design item | Built | Where |
|---|---|---|
| Migration 065, both providers, idempotent | ✅ | `dba/mig/sqlite/065_hr_payroll.sql`, `dba/mig/postgresql/065_hr_payroll.sql` — 14 tables, 14 `HR.*` permissions (display_order 369–382) |
| Departments / designations masters | ✅ | `hr_departments` (parent + cost-center link), `hr_designations`; `HrDeptWin`, `HrDesigWin` |
| Employee card, §2.1 CORE fields, lifecycle Active/Suspended/Terminated | ✅ | `hr_employees`; `HrEmployeeWin` (navigator card); terminated keep history, leave payroll |
| Attendance daily records + bulk monthly grid | ✅ | `hr_attendance` (unique company+employee+date); `HrAttendanceWin` (employees × days grid, in/out/overtime detail) |
| Leave types + balances + applications with approve/reject | ✅ | `hr_leave_types/_balances/_applications`; `HrLeaveWin`; approval deducts + writes Leave attendance in ONE transaction; negative balance refused (HR-1003) unless `HR.LEAVE.OVERRIDE` |
| Salary components / structures / assignments | ✅ | `hr_salary_components/_structures/_structure_lines`, `hr_employee_salary` (employee XOR designation); `HrStructureWin` |
| Pure payroll calculator (divisor 30, explicit system lines) | ✅ | `PayrollCalculator` in `src/ShouTech.Domain/Entities/Hr.cs` |
| Monthly Payroll Run → payslips → post via GlPosting → reverse | ✅ | `hr_payroll_runs/_payslips/_payslip_lines`; `HrPayrollWin`; `HrRepository.PostRunAsync/ReverseRunAsync` |
| Salary payment via existing CshWin path (link, no second payer) | ✅ | Payroll window *Pay salaries* → `RptNav.OpenPaymentVoucher` (CshWin payment voucher against 2204) |
| Reports: payroll summary by dept, payslip, attendance summary, leave balances | ✅ | `HrPayrollSummaryWin`, `HrPayslipWin`, `HrAttendanceSummaryWin`, `HrLeaveBalanceWin` (RptBase family) |
| HR.* RBAC (view/edit/approve/post separations) | ✅ | `PermCat.Hr`; ADMINISTRATOR all, USER all but OVERRIDE+POST, VIEWER read-only |
| Error catalog HR-1001…HR-1010 ×4 languages | ✅ | `Resources/Errors/{En,Ar,Fr,Tr}.json` + `docs/ERROR_CATALOG.md` |
| Lang keys ×4 (193 HR keys at parity) | ✅ | `Resources/Lang/{En,Ar,Fr,Tr}.json` (`Hr.*`, `HR.*`, `Menu.HR`) |
| Ribbon: HR tab + menu, module `HumanResources` | ✅ | `MWin.xaml(.cs)`, `RibModMap`, `RibbonNav` (`HR.*` keys), `RptNav` openers |

Named v1 GAPs (from the design, unchanged): employee loans/advances,
end-of-service/gratuity + full & final settlement, shift masters and
biometric/device import, holiday lists, overtime auto-pricing from
attendance (hours are recorded; pricing is a manual structure line),
leave accrual/carry-forward automation, leave encashment, half-day
leave applications, WPS/bank payment files, ESS, recruitment/
appraisal, hire-date pro-rating inside the first month, Late
attendance never auto-deducts.

## 2. GL mapping (as implemented and proven)

Posting a run is ONE transaction (run + payslips finalize + journal)
through `GlPosting`: source `PAYROLL`, source id = run id, doc no =
run number (`PR-YYYY-MM`), entry date = period end, base currency
(step-7 convention).

- **Dr** each Earning and Employer payslip line's account (line
  mapping; default `501`), carrying the employee's cost center when
  set — payroll cost distribution to cost centers.
- **Cr** each Deduction line **with** an account mapping to that
  payable (component mapping; e.g. social insurance → `2211` when the
  chart carries it, else the configured account).
- **Cr** the run's total net to `2204` Salaries and Wages Payable
  (negative net becomes a Dr to `2204` — amount owed back — explicit,
  still balanced).
- **Never-owed deductions carry no account** (the `UNPAID_LEAVE` /
  `ABSENCE` system lines): nobody is owed them, so they reduce the
  salary-expense debit (absorbed into `501` first, then remaining
  expense debits largest-first). Identity: Dr (gross + employer −
  never-owed) = Cr net + Σ mapped deductions + Σ employer. *(This rule
  was corrected during the build: the first implementation credited
  unpaid leave to `2204`, overstating the payable by exactly the
  unpaid amount — 1280.00 instead of 1213.33. The headless proof
  caught it; the calculator's system lines now carry no account and
  the posting absorbs them against expense.)*
- **Reversal**: `GlPosting.ReverseInTransaction` on the stored entry;
  run and payslips return to Draft, the reversal doc number is kept
  on the run. GL nets to zero exactly.
- **Payment**: the existing CshWin payment voucher (Dr `2204` / Cr
  cash `131` or bank), opened from the payroll window; no second
  payer was built.

## 3. Comparison tables + CORE re-grade

Counterparts: Al-Ameen V10 / Phenix v10 (⚠ = regional practice per the
sweep), ERPNext HRMS, Odoo HR/Payroll. Scoring follows the sweep's
method (present-and-usable = 1, partial = ½, absent = 0).

### 3.1 HrEmployeeWin — Employee card (§2.1, was 0/15)

| CORE field/group | Al-Ameen / Phenix | ERPNext | Odoo | ShouTech now |
|---|---|---|---|---|
| Employee code | ✅ | ✅ | ✅ | ✅ manual code, unique per company (HR-1001) |
| Full name (first/middle/last + Arabic name) | ✅ | ✅ | ✅ | ✅ |
| Department / Designation (lookups) | ✅ | ✅ Link | ✅ | ✅ governed masters |
| Branch / Company | ✅ | ✅ | ✅ | ✅ branch on card; company = shell context |
| Employment type | ✅ | ✅ | ✅ | ✅ governed vocabulary on card |
| Hire date | ✅ | ✅ | ✅ | ✅ |
| Date of birth / gender | ✅ | ✅ | ✅ | ✅ |
| National ID / passport | ✅ | ✅ | ✅ | ✅ (+ social insurance no, tax id) |
| Phone / mobile / personal + work email | ✅ | ✅ | ✅ | ✅ |
| Address present / permanent | ✅ | ✅ | ✅ | ✅ |
| Reports-to / approver chain | ✅ | ✅ | ✅ | ❌ GAP (leave approval is permission-based, no per-employee approver) |
| Salary mode + bank name/account (IBAN) | ✅ | ✅ | ✅ | ✅ |
| Social insurance number | ✅ GOSI ⚠ | ✅ localization | ✅ localization | ✅ field + configurable % components (rates in data, no law hardcoded) |
| Status lifecycle + termination date/reason | ✅ | ✅ | ✅ | ✅ Active/Suspended/Terminated; terminated excluded from runs, history kept |
| User account link (ESS) | — | ✅ | ✅ | ❌ GAP (ESS is a named v1 exclusion) |

**§2.1 re-grade: 13/15.**

### 3.2 HrDeptWin / HrDesigWin — Organisation masters (§2.2, was 0/6)

| CORE master | Counterparts | ShouTech now |
|---|---|---|
| Department (parent, cost-center link) | ✅ tree | ✅ parent + cost center + Ar/En names (no per-department manager field — noted) |
| Designation / job title | ✅ | ✅ with department link |
| Branch / work location | ✅ master | ½ branch value on the employee card; the accounting branch master is not an HR master |
| Employment type | ✅ master | ½ governed vocabulary, not an editable master window |
| Shift type master | ✅ | ❌ named GAP |
| Holiday list | ✅ | ❌ named GAP |

**§2.2 re-grade: 3/6.**

### 3.3 HrAttendanceWin — Attendance (§2.3, was 0/6)

| CORE function | ERPNext | Odoo | Phenix/Al-Ameen | ShouTech now |
|---|---|---|---|---|
| Attendance record (status, in/out, overtime) | ✅ | ✅ | ✅ | ✅ P/A/L/V/H + half day, in/out, overtime hours |
| Monthly register feeding payroll | ✅ | ✅ | ✅ كشف الحضور | ✅ bulk month grid; absence + approved leave feed the run |
| Check-in/out capture | ✅ + device | ✅ kiosk | ✅ biometric ⚠ | ½ manual in/out on the record; device import ❌ named GAP |
| Overtime recording + approval → pay | ✅ | ✅ | ✅ | ½ hours recorded + reported; auto-pricing ❌ named GAP |
| Shift type / assignment | ✅ | ✅ | ✅ | ❌ named GAP |

**§2.3 re-grade: 3/6.**

### 3.4 HrLeaveWin — Leave (§2.4, was 0/5)

| CORE function | ERPNext | Odoo | Al-Ameen/Phenix | ShouTech now |
|---|---|---|---|---|
| Leave type (paid/unpaid, annual days) | ✅ | ✅ | ✅ | ✅ seeded ANNUAL/SICK/UNPAID/EMERGENCY, editable |
| Allocation / balance | ✅ + ledger | ✅ | ✅ | ✅ per employee×type×year; applications are the movement trail (no separate ledger table) |
| Application + approve/reject | ✅ | ✅ | ✅ | ✅ balance check on approval (HR-1003), override permission, cancel restores balance + removes exactly its attendance rows |
| Accrual / carry-forward job | ✅ | ✅ | ✅ | ❌ named GAP (allocations are explicit) |
| Leave reports | ✅ | ✅ | ✅ | ✅ leave balance report |

**§2.4 re-grade: 4/5.** (Half-day applications are a noted sub-gap:
applications are whole-day date ranges in v1.)

### 3.5 HrStructureWin + HrPayrollWin — Payroll (§2.5, was 0/11)

| CORE function | ERPNext | Odoo | Phenix/Al-Ameen | ShouTech now |
|---|---|---|---|---|
| Salary component (earning/deduction/employer, GL account) | ✅ | ✅ rules | ✅ elements ⚠ | ✅ catalog with kind, default calc, account mapping |
| Salary structure (fixed + % of base + % of gross) | ✅ formula | ✅ | ✅ template ⚠ | ✅ lines with calc type/amount/percent/account |
| Structure assignment (per employee or designation, base, from date) | ✅ | ✅ contract | ✅ | ✅ one active per target; designation default fallback |
| Employee contract document | ✅ | ✅ | ✅ | ½ contract facts live on the card + assignment (hire date, type, from date); no contract document/dates pair |
| Payroll run (period → bulk slips) | ✅ Payroll Entry | ✅ batches | ✅ مسير الرواتب | ✅ monthly run, eligibility by status/hire/termination |
| Salary slip (earnings/deductions/net, payment days) | ✅ | ✅ | ✅ | ✅ stored lines incl. explicit UNPAID_LEAVE/ABSENCE system lines; paid days = 30 − unpaid − absent |
| Attendance → payroll (LOP) link | ✅ | ✅ | ✅ | ✅ approved unpaid leave + recorded absences deduct at base/30 |
| Additional salary / one-offs | ✅ | ✅ inputs | ✅ | ½ exceptional lines are editable on draft payslips (system lines locked); no recurring additional-salary document |
| Social insurance employee + employer shares | ✅ localization | ✅ localization | ✅ GOSI ⚠ | ✅ configurable deduction + employer components; employer is cost-only, never in net |
| Payroll journal posting | ✅ | ✅ | ✅ | ✅ §2 mapping above, source-linked, reversible |
| Payroll reports | ✅ | ✅ | ✅ | ✅ summary by department + totals, payslip print |
| WPS / bank file | ✅ localization | ✅ localization | ✅ WPS ⚠ | ❌ named GAP |

**§2.5 re-grade: 10/12 enumerated CORE rows** (income-tax slabs are
EXTENDED for the owner's Gulf installs per the sweep and excluded;
they are buildable today as %-of-gross deduction components).

### 3.6 §2.6 — Loans / EOS / distribution (was 0/5)

Employee advance ❌, loans ❌, end-of-service ❌, full & final ❌ —
all named v1 GAPs. Payroll-to-cost-centre distribution ✅ (employee
cost center rides the Dr expense lines). **§2.6 re-grade: 1/5.**

### 3.7 Consolidated re-grade

| Section | Was | Now |
|---|---|---|
| §2.1 Employee card | 0/15 | **13/15** |
| §2.2 Org masters | 0/6 | **3/6** |
| §2.3 Attendance & shifts | 0/6 | **3/6** |
| §2.4 Leave | 0/5 | **4/5** |
| §2.5 Payroll | 0/11–12 | **10/12** |
| §2.6 Loans/EOS/rest | 0/5 | **1/5** |
| **Total (section-enumerated)** | **0%** | **34/49 ≈ 69%** |

The remaining 31% is concentrated in the named GAPs: shifts +
holiday lists + device import (§§2.2–2.3), accrual (§2.4), WPS
(§2.5), and the whole loans/EOS/settlement family (§2.6) — each is a
follow-up build, not a defect in what shipped.

## 4. Pillars checklists (per window)

Legend: PROVEN = exercised by the headless proof or the 0/0 build +
audit; GAP = named above.

**HrDeptWin / HrDesigWin** — UI ✅ (BaseWin, bottom bar, tint Sage/Fern) · DB ✅ 065 tables · Linking ✅ designation→department, employee→both, delete guards HR-1008 · Posting n/a (masters) · Persistence ✅ repository CRUD round-trip in proof (a) · Downstream ✅ employee card + payroll summary by department.
**HrEmployeeWin** — UI ✅ full card + navigator + lifecycle buttons · DB ✅ · Linking ✅ dept/desig lookups, salary assignment section · Posting n/a (card) · Persistence ✅ proof (a) field-by-field identical reload · Downstream ✅ payroll eligibility + reports.
**HrAttendanceWin** — UI ✅ bulk month grid + cell detail (in/out/overtime) · DB ✅ unique per day · Linking ✅ leave-owned rows protected from overwrite · Posting → feeds payroll deductions (proof b/c) · Persistence ✅ batch upsert · Downstream ✅ attendance summary report + payroll.
**HrLeaveWin** — UI ✅ types/balances/applications · DB ✅ · Linking ✅ approval writes attendance; cancel restores · Posting → unpaid leave hits payroll (proof c) · Persistence ✅ proof (b) · Downstream ✅ leave balance report.
**HrStructureWin** — UI ✅ components + structures + assignments · DB ✅ · Linking ✅ component→line→assignment→employee/designation · Posting → account mapping drives §2 GL split (proof d) · Persistence ✅ · Downstream ✅ payroll generation.
**HrPayrollWin** — UI ✅ runs/payslips/draft line editing · DB ✅ · Linking ✅ Pay salaries → CshWin voucher · Posting ✅ proof (d)/(e): GL split, reversal, payment settles 2204 · Persistence ✅ · Downstream ✅ reports read the same stored slips.
**Reports (×4)** — UI ✅ RptBase standard (filters, totals, print/export) · DB ✅ read stored runs/payslips/attendance/balances only · GAP: not exercised headlessly (they read the same repository queries the proof verifies: `GetPayrollSummaryByDeptAsync`, `GetRunAsync`, `GetAttendanceSummaryAsync`, `GetLeaveBalancesAsync`).

## 5. Headless proof (SQLite, real migrator + real repositories)

Driver: `~/workspace/research/build-hr/hr_harness.py` (+ C# harness
in `~/workspace/research/build-hr/harness/`) — **ALL PASS**:

- (f) Migrator double-run: `APPLY 065_hr_payroll` then `SKIP` —
  idempotent; exactly one schema_migrations row; all 14 tables;
  14 `HR.*` permissions seeded.
- (a) Employee full card round-trips field-by-field identical;
  terminated employee excluded from the March run (1 slip, the
  active employee only).
- (b) Allocate 30 → approve 5 → balance **25**; approval wrote 5
  Leave attendance rows; a 26-day application refused with
  **HR-1003**; with override it approves to an explicit −1; absence
  days recorded (2 absent / 5 leave / 2h overtime in the summary).
- (c) Base 1000 + housing 25% (250) + transport 100 − social 7%
  (70) − 2 unpaid-leave days (2 × 1000/30 = 66.67) → gross **1350**,
  net **1213.33** exact; UNPAID_LEAVE is an explicit system line;
  run totals = Σ payslips.
- (d) Posting: Dr `501` = **1283.33** (1350 − 66.67 never-owed),
  Cr `2211` = 70, Cr `2204` = **1213.33** = run net; entry balanced;
  source-linked (`PAYROLL`, run id); run + payslip Final with the
  journal number stored. Reversal nets GL to **zero** on every
  mapped account and returns run + payslips to Draft.
- (e) Payment voucher through `GlPosting` (the CshWin engine):
  payable stands at 1213.33 after re-posting, settles to **zero**
  after Dr `2204` / Cr `131`.

Kern.Tests: `tests/Kern.Tests/HrPayrollTests.cs` — 6 facts mirroring
the harness (3 pure calculator facts incl. %-of-gross order
independence and employer exclusion; 3 repository/GL integration
facts). They compile in the 0/0 build; execution needs the Windows
testhost — **owed**.

## 6. Gates

| Gate | Result |
|---|---|
| Full build `dotnet build ShouTech.sln --no-incremental -p:EnableWindowsTargeting=true` | **0 errors, 0 warnings** |
| `tools/audit_window_context.py` | **exit 0** (all 7 HR windows listed, central shell context) |
| Lang parity — HR keys | **193 keys × 4 languages, 0 missing / 0 extra**; Errors catalogs 362 keys × 4 at parity |
| Migrator idempotent double-run | ✅ proof (f) |
| Arabic outside Ar.json | none in any new/changed code file |

Pre-existing drift flagged, NOT introduced here and not fixed on this
branch: the non-HR Lang catalogs already carried extra keys vs En on
the base commit (Ar +90, Fr/Tr +9 legacy keys). HR keys are at exact
parity; a catalog cleanup is a separate branch's work.

## 7. Owner flags

1. **Suspended employees are excluded from payroll generation** (a
   design decision: only Active employees are payable). If suspended
   staff must still be paid in the owner's practice, this is a
   one-line eligibility change — decide before merge.
2. **Daily rate divisor is 30** for every month (design §4,
   regional standard). Calendar-day pro-rating is a named GAP.
3. **Seeded leave types and components carry zero/default rates by
   design** — ANNUAL 30 / SICK 15 / EMERGENCY 5 days; HOUSING 25%,
   SOCIAL 7% appear only in the proof's own structure. No country's
   law is hardcoded anywhere; the owner's HR sets real rates as data.
4. **WPF + live PostgreSQL still owed**: windows unclicked on
   Windows; migration 065 PG file follows the chain's provider
   conventions but has not run against a live PG server here.
5. Follow-up builds, in the sweep's priority order: shifts +
   holiday lists + device import; leave accrual; WPS file; loans /
   advances; end-of-service + full & final settlement.
