<!-- source: docs/BUILD_HR_DESIGN.md -->

# BUILD — HR / Payroll: Design (migration 065, branch `build/hr-payroll`)

> Research-first deliverable (owner's standing rule). Sources:
> `~/workspace/research/sweep/compare-mfg-hr-crm.md` §2 in full (employee
> card §2.1, organisation masters §2.2, attendance/shifts §2.3, leave
> §2.4, payroll §2.5, loans/EOS §2.6 — baseline grade **0/42 CORE, 0%**),
> Al-Ameen V10 / Phenix v10 HR practice (⚠ pattern rows of the sweep),
> and ERPNext HRMS (Employee, Attendance, Leave Type / Allocation /
> Application, Salary Component / Structure / Structure Assignment,
> Payroll Entry, Salary Slip). Base: tip of `build/manufacturing`
> (348c3e9; fix steps 1–13 + builds 054–058 + foundations 059–064).
> Migration **065** (059–064 are taken). This document fixes the data
> model, the payroll calculation model and the window set; the build
> implements exactly this document. Deviations found during the build
> are recorded in `docs/BUILD_HR_PAYROLL.md`, not silently absorbed.

---

## 1. Scope decisions (what v1 is, and what it deliberately is not)

**In scope (the task's priority order):**
1. Masters + employee card: departments, designations, employees with
   the comparison's CORE field set, status lifecycle.
2. Attendance (daily records, bulk monthly entry) + leave (types,
   balances, applications with approve/reject, balance protection).
3. Payroll: salary components + structures + assignments, a monthly
   Payroll Run that generates payslips, posts to GL through
   `GlPosting`, reverses symmetrically, and is paid through the
   existing cash/bank voucher path.
4. Reports (RptBase family): payroll summary, payslip, attendance
   summary, leave balances — real data.

**Out of scope for v1 (named GAPs, not silent omissions — §2.6/§2.3 of
the sweep):** employee loans/advances with slip recovery, end-of-service
indemnity + provision, full & final settlement, shift types /
assignments / biometric import, holiday lists, overtime auto-pricing,
leave accrual jobs + carry-forward automation, leave encashment, WPS /
bank payment files, ESS, recruitment / appraisal / expense claims.
Each is listed again in the re-grade in `BUILD_HR_PAYROLL.md` so the
0% → v1 grade is honest. The schema leaves room for all of them
(components are data; attendance carries in/out + overtime hours).

**Market posture (owner's instruction):** monthly payroll; statutory
deductions (social-insurance-style percentages, tax) are **configurable
components with rates in data** — no country's law is hardcoded. The
engine knows only: earning / deduction / employer-contribution,
fixed amount, % of base, % of gross. A Saudi GOSI setup, a Syrian
social-insurance setup and a zero-statutory setup are all just rows.

## 2. Conventions inherited from the chain

- **Company keying:** HR lives in the party world, so every HR table
  keys the company by its **CODE** (TEXT), exactly like `parties` /
  `sales_reps` (031) and CRM (057). The one journal-world touchpoint —
  the payroll GL posting — resolves the company's **Guid** from the
  `companies` registry inside the posting transaction (the CRM
  quotation precedent) and calls `GlPosting` with it.
- **Ids:** TEXT Guid (`"D"` format) on SQLite, `uuid` on PostgreSQL —
  the 057/058 dual-provider shape. Money/quantities `REAL` (SQLite) /
  `NUMERIC` (PG); flags `INTEGER 0/1` / `BOOLEAN`.
- **Writes:** one transaction per operation, parameterized SQL only,
  soft delete (`is_deleted`) on masters, audit columns
  (`created_by/at`, `updated_by/at`) everywhere.
- **Errors:** HR family codes `HR-1001…` in `ErrorCodes.cs` + the four
  error catalogs; windows report through `AppHost.Errors` (no silent
  catches). src/ code throws `AppError`; it never swallows.
- **RBAC:** `HR.*` permissions seeded in 065 (step-1 pattern:
  INSERT OR IGNORE + UPDATE, role grants for ADMINISTRATOR / USER /
  VIEWER), constants in `PermCat.Hr`, checked in every window and —
  for the two separations the brief demands — in the service:
  leave **approval** is a distinct permission from leave **edit**, and
  payroll **posting** is distinct from payroll **edit/generate**;
  the negative-balance **override** is its own sensitive permission.
- **Language:** all UI text via Lang keys (`Hr.*` section ×4 catalogs,
  parity 0). No Arabic outside `Ar.json`.
- **Windows:** `BaseWin` (shell context, RTL, bottom command bar,
  unique schedule tint per window), reports on `RptBase`.

## 3. Data model (migration 065)

### 3.1 Organisation masters

`hr_departments` — `id, company_id, code, name_en, name_ar,
parent_id (→ hr_departments, tree per ERPNext Department),
manager_employee_id (→ hr_employees, nullable), cost_center
(payroll distribution link, §2.2 CORE), is_active, is_deleted, audit`.
Unique (company, code) among live rows.

`hr_designations` — `id, company_id, code, name_en, name_ar,
department_id (nullable), description, is_active, is_deleted, audit`.
Unique (company, code) among live rows.

### 3.2 Employee (`hr_employees`) — §2.1 CORE field set

| Group | Columns |
|---|---|
| Identity | `employee_code` (unique per company), `first_name`, `middle_name`, `last_name`, `name_ar`, `gender`, `date_of_birth`, `national_id`, `passport_no`, `social_insurance_no`, `tax_id` |
| Contact | `phone`, `mobile`, `personal_email`, `work_email`, `address_present`, `address_permanent`, `emergency_contact_name`, `emergency_contact_phone` |
| Organisation | `department_id`, `designation_id`, `branch` (text code — branches are an accounting master; stored as the branch code the shell uses), `reports_to_id` (→ hr_employees), `cost_center` |
| Employment | `hire_date`, `employment_type` (Full-time / Part-time / Contract / Intern / Temporary — stored vocabulary, extensible), `status` (`Active` / `Suspended` / `Terminated`), `termination_date`, `termination_reason` |
| Payroll | `salary_mode` (Bank / Cash / Cheque), `bank_name`, `bank_account_no` (IBAN), `base_salary` (current contractual base — the assignment in §3.5 is the dated source for runs; the card field is the live convenience copy kept in sync by the service), `currency_code` |
| Link | `user_id` (app user name — the ESS/attendance link of §2.1; a reference, not a login) |
| System | `notes`, `is_deleted`, audit columns |

**Lifecycle:** `Active → Suspended → Active`, `Active/Suspended →
Terminated` (termination date required). Only `Active` employees are
picked up by a payroll run generation and by the attendance grid;
`Suspended` employees keep leave balances and history but are excluded
from new runs (documented choice: suspension in this market normally
means unpaid suspension; a suspended employee who must be paid is
re-activated first — the run's employee list is reviewable before
posting, so nothing is silent). Terminated employees keep every
historical row (attendance, leave, payslips) and can never be deleted
once a payslip exists; soft delete is refused for them by the service.

### 3.3 Attendance (`hr_attendance`)

`id, company_id, employee_id, attendance_date, status`
(`Present` / `Absent` / `Late` / `Leave` / `HalfDay`),
`in_time`, `out_time` (text `HH:mm`, nullable), `overtime_hours`
(recorded; auto-pricing is a v1 GAP — an overtime earning is entered
as a normal fixed component or a payslip adjustment line),
`notes`, audit. Unique (company, employee, date) — saving a day
upserts, so the bulk grid is idempotent. The monthly window edits a
whole month for all active employees in ONE transaction.

### 3.4 Leave

`hr_leave_types` — `id, company_id, code, name_en, name_ar,
annual_days, is_paid (0 = leave without pay → payroll LWP),
is_carry_forward, max_carry_forward, is_active, is_deleted, audit`.
Seeded per company **on first use** by the repository (a migration
cannot know future companies — the 057 precedent): `ANNUAL` 30 paid,
`SICK` 15 paid, `UNPAID` 0 unpaid, `EMERGENCY` 5 paid. These are
defaults a company edits freely; nothing in code depends on the codes
except `is_paid`.

`hr_leave_balances` — `id, company_id, employee_id, leave_type_id,
year, allocated_days, used_days, audit`. Unique (company, employee,
type, year). `AllocateLeaveAsync` upserts the allocation;
`GetBalance = allocated − used` (may go negative **only** through an
approved override application, §3.4 workflow).

`hr_leave_applications` — `id, company_id, employee_id, leave_type_id,
from_date, to_date, total_days, half_day (single-day half),
reason, status (Open / Approved / Rejected / Cancelled),
decided_by, decided_at, decision_notes, is_deleted, audit`.
`total_days` = calendar days inclusive of both ends (holiday-list
awareness is a v1 GAP — no holiday master exists yet); a single-day
application with `half_day` counts 0.5.

**Workflow:** create (Open) → Approve / Reject (separate
`HR.LEAVE.APPROVE` permission). Approval, in ONE transaction:
re-checks status is Open; computes the year's balance; if
`total_days > balance` and the caller carries no override
(`HR.LEAVE.OVERRIDE`, passed by the service after its permission
check) → `HR-1003`; otherwise deducts (`used_days += total_days`)
and, for each day of an **approved** application, writes/refreshes
the attendance row as `Leave` (so attendance, leave and payroll can
never disagree). Rejection changes status only. Cancelling an
approved application restores the balance and removes only the
attendance rows that application created (status `Leave` rows inside
its range are reset by deletion of the application-owned rows —
tracked by the application id in `hr_attendance.notes`? No: the
attendance rows written by approval carry the application's id in a
dedicated column `leave_application_id`; cancel deletes exactly
those rows).

### 3.5 Salary components, structures, assignments

`hr_salary_components` (company catalog) — `id, company_id, code,
name_en, name_ar, kind (Earning / Deduction / Employer),
default_calc_type (Fixed / PercentOfBase / PercentOfGross),
default_amount, default_percent, is_active, is_deleted, audit`.
Seeded per company on first use with the neutral starter set:
`BASIC` is **not** a component (the base salary is the assignment's
`base_salary`); starters: `HOUSING` (Earning, % of base),
`TRANSPORT` (Earning, Fixed), `SOCIAL` (Deduction, % of base —
the social-insurance-style configurable percentage),
`TAX` (Deduction, % of gross), `SOCIAL_ER` (Employer, % of base).
All rates start at 0/blank in data — the company sets its own.

`hr_salary_structures` — `id, company_id, code, name_en, name_ar,
applies_to (Employee / Designation), is_active, is_deleted, audit`.

`hr_salary_structure_lines` — `id, structure_id, component_id,
line_no, calc_type, amount, percent, account_code, is_active`.
`account_code` is the component's GL mapping **for this structure**:
for an Earning / Employer line it is the **expense (debit)** account
(default `501` Salaries and Wages); for a Deduction line it is the
**payable (credit)** account (default `2204` Salaries Payable;
a social-insurance deduction typically maps to `2211` Payroll Taxes
Payable where the chart carries it postable). Resolution at posting
time uses `GlPosting.ResolveAccount` — a missing/non-postable mapped
account fails the posting with `ACC-5011`, never a silent fallback
(the optional-leg pattern is **not** used for payroll: money owed to
employees and authorities must land where the mapping says).

`hr_employee_salary` (assignment, ERPNext Salary Structure Assignment)
— `id, company_id, employee_id, structure_id, base_salary,
currency_code, from_date, to_date (nullable), is_active, audit`.
At most one active assignment per employee (enforced in the save
transaction). Saving an assignment also updates the employee card's
`base_salary` / `currency_code` convenience copy. A structure may
alternatively be assigned per designation: the run resolves an
employee's structure as (1) their active employee assignment, else
(2) the active assignment whose employee is a *designation holder*?
No — designation assignment is modelled directly: an assignment row
with `employee_id NULL` and `designation_id` set is a designation
default; resolution order is employee assignment → designation
default → no structure (base only). `hr_employee_salary` therefore
carries a nullable `designation_id` beside the nullable
`employee_id`, with a CHECK that exactly one is set.

### 3.6 Payroll runs and payslips

`hr_payroll_runs` — `id, company_id, run_no (PR-YYYY-MM, unique per
company), period_year, period_month, period_from, period_to,
status (Draft / Posted), total_gross, total_deductions, total_net,
total_employer, employee_count, currency_code, journal_entry_id,
journal_doc_no, reversed_journal_doc_no, notes, posted_by,
posted_at, audit`. A reversed run returns to `Draft` (its payslips
re-open) and keeps `reversed_journal_doc_no` as the audit trace —
reversal is not a status, it is an event (the manufacturing events
precedent). One run per (company, year, month) among non-deleted
rows: re-generating a Draft run replaces its payslips; a Posted run
must be reversed before it can be regenerated.

`hr_payslips` — `id, company_id, run_id, employee_id,
employee_code / employee_name / department_name / designation_name
(snapshots — a payslip is a historical document), base_salary,
period_days (the divisor basis, §4), paid_days, unpaid_leave_days,
absent_days, gross_pay, total_deductions, net_pay, employer_cost,
status (Draft / Final), notes, audit`.

`hr_payslip_lines` — `id, payslip_id, line_no, component_code,
component_name, kind (Earning / Deduction / Employer), calc_type,
amount, is_system (1 for the two attendance-driven deduction lines),
account_code, notes`. Draft payslip lines are editable in the
payroll window (exceptional items: adjust a fixed line, add an
ad-hoc earning/deduction line, remove an ad-hoc line); system lines
and %-computed lines are read-only there — change the inputs
(attendance / leave / structure) and regenerate instead. Editing a
line recomputes the payslip + run totals in the same transaction.

## 4. Payroll calculation model (pure — `PayrollCalculator`)

Implemented as a pure static calculator in `ShouTech.Domain`
(no I/O), unit-tested directly and used unchanged by the repository.

**Inputs:** `baseSalary`, the structure's lines (kind, calc type,
amount, percent), `unpaidLeaveDays` (approved applications of
unpaid leave types overlapping the period), `absentDays`
(attendance rows `Absent` in the period; `HalfDay` counts 0.5),
`periodDivisor = 30`.

**Rules:**
1. **Divisor:** the daily rate is `baseSalary / 30` — the regional
   monthly-payroll convention the brief fixes (monthly/30), stored
   on the payslip as `period_days = 30` regardless of the calendar
   month, so February and July price an absence identically.
2. **Earnings:** `Fixed` → amount; `PercentOfBase` → base × %/100;
   `PercentOfGross` → (base + all Fixed/%-of-base earnings) × %/100
   (computed after the other earnings, so the result is independent
   of line order). **Gross = base + Σ earnings.** The base itself is
   always a payslip line (`BASIC`, Earning, Fixed = baseSalary) so a
   printed slip shows it.
3. **Deductions (structure):** `Fixed` → amount; `PercentOfBase` →
   base × %/100; `PercentOfGross` → gross × %/100.
4. **Attendance/leave deductions — explicit, never silent:** two
   system deduction lines are appended when the counts are non-zero:
   `UNPAID_LEAVE` = dailyRate × unpaidLeaveDays and
   `ABSENCE` = dailyRate × absentDays, each labelled with its day
   count in the line note. Paid leave never deducts. `Late` never
   deducts in v1 (recorded only).
5. **Employer contributions:** computed like earnings (Fixed /
   % of base / % of gross) but excluded from gross and net; they sum
   to the payslip's `employer_cost` and the run's `total_employer`,
   and post Dr expense / Cr payable (§5).
6. **Rounding:** every line amount is rounded to 2 decimals
   (`MidpointRounding.AwayFromZero`) at computation; totals are sums
   of the rounded lines, so a slip always foots exactly.
   `net = gross − Σ deductions`. `paid_days = 30 − unpaid − absent`.
7. **Eligibility:** generation covers employees who are `Active` on
   the period end, hired on/before the period end, and not terminated
   on/before the period start. No pro-rating by hire date in v1
   (a mid-month joiner is paid the full month unless the operator
   adjusts the draft slip — named GAP; the draft-edit path exists
   precisely for this).

**Worked proof case (harness (c), pinned):** base 1000, HOUSING
25% of base = 250.00, TRANSPORT fixed 100.00, SOCIAL 7% of base =
70.00, 2 unpaid-leave days → 1000/30 × 2 = 66.67.
Gross = 1350.00; deductions = 136.67; **net = 1213.33**.

## 5. GL posting model (through `GlPosting`, step-3/7 conventions)

Posting a run (ONE transaction: run + payslips finalize + journal):

- Source: `source_doc_type = 'PAYROLL'`, `source_doc_id = run id`,
  `source_doc_no = run_no`; entry date = period end; currency = the
  run currency (base-currency convention of step 7: lines carry
  transaction + base amounts via `GlPosting` itself).
- **Lines (aggregated per account by `GlPosting`):**
  - Dr each Earning line's expense account (incl. `BASIC` → `501`
    default) — per payslip line `account_code`, with the employee's
    `cost_center` when set (cost-center splits J-3 apply);
  - Dr each Employer line's expense account;
  - Cr `2204` Salaries and Wages Payable — the run's **total net**
    (one leg: net pay is owed to employees collectively until paid);
  - Cr each Deduction line's payable account (its `account_code`;
    lines mapped to `2204` merge into the net leg by aggregation,
    which is arithmetically identical).
  - **Never-owed deductions carry NO account** (the `UNPAID_LEAVE` /
    `ABSENCE` system lines): nobody is owed them — the amount was
    never earned — so they are **not** a payable. They reduce the
    salary-expense debit instead (absorbed into the `501` debit
    first, then the remaining expense debits largest-first). The
    balanced identity is therefore: Dr (gross + employer −
    never-owed) = Cr net + Σ mapped deductions + Σ employer.
    Crediting them to `2204` would overstate the payable by exactly
    the unpaid amount (caught by the headless proof, scenario (d):
    payable must equal the run net to the cent).
- The entry is balanced by construction (net = gross − deductions);
  `GlPosting` re-validates and throws `ACC-5001` if not.
- Account codes `501` / `2204` exist postable in **every** seeded
  chart template (verified against `Resources/COA/*.shc`); `2211`
  exists in most and is used only when a company maps a component
  to it.
- **Reversal:** `GlPosting.ReverseInTransaction` on the stored
  entry (same open-period rule as every reversal); run → Draft,
  payslips → Draft, journal fields cleared, reversal doc no kept.
  GL nets to zero exactly (stored base amounts are mirrored).
- **Payment (the link, not a second payer):** salaries are paid with
  the existing **CshWin Payment voucher** (or JrnWin): Dr `2204` /
  Cr cash `131` or bank `132`. The payroll window's *Pay salaries*
  button opens exactly that voucher (`RptNav.OpenPaymentVoucher`);
  the run header shows the payable account + outstanding net so the
  operator vouchers the right amount. The proof harness posts the
  identical voucher through `GlPosting` and asserts the `2204`
  balance falls by the run's net.

## 6. Window set

| Window | Base / tint | Contents |
|---|---|---|
| `HrDeptWin` | BaseWin / Sage | Department list + card (code, names, parent, manager, cost center, active); delete guarded when employees reference it |
| `HrDesigWin` | BaseWin / Fern | Designation list + card (code, names, department, description, active) |
| `HrEmployeeWin` | BaseWin / Jade | Full employee card (MatCard standard): record navigator (first/prev/next/last + list), all §3.2 fields in grouped form pairs, status lifecycle buttons, salary assignment section (structure, base, currency, from date) |
| `HrAttendanceWin` | BaseWin / Cyan | Month picker; bulk grid = active employees × days of month, cell = status code (P/A/L/V/H, blank = no record); in/out + overtime edited per selected employee-day below the grid; save = one transaction upsert |
| `HrLeaveWin` | BaseWin / Denim | Three sections: leave types, balances (employee × type × year with Allocate), applications (create, approve/reject with the permission separation, cancel) |
| `HrStructureWin` | BaseWin / Topaz | Component catalog + structure editor (lines grid: component, calc type, amount/%, account) + designation defaults list |
| `HrPayrollWin` | BaseWin / Cobalt | Run list; Generate (period → draft slips for all eligible), payslip grid + selected slip's lines (draft-editable), Post / Reverse (permission-separated), Pay salaries → CshWin payment voucher |
| `HrPayrollSummaryWin` | RptBase | Per-department gross / deductions / net / employer + company totals for a posted-or-draft period (filter: run) |
| `HrPayslipWin` | RptBase | One employee's payslip lines for a run (filter: run + employee) — the printable slip (RptBase print) |
| `HrAttendanceSummaryWin` | RptBase | Per employee per month: present / absent / late / leave / half days + overtime hours |
| `HrLeaveBalanceWin` | RptBase | Per employee × type: allocated / used / balance for a year |

Ribbon: a real **HR tab** (menu item + `TabHR`) with groups
Cards (Employees, Departments, Designations), Attendance & Leave
(Attendance, Leave), Payroll (Salary structures, Payroll runs),
Reports (the four reports) — routed through `RibbonNav` keys
`HR.*`, with `RptNav` openers. `RibModMap` gains `TabHR →
HumanResources` so the (already enabled) `appset.json`
HumanResources descriptor finally has code behind it (sweep HR-1).

## 7. Proof plan (mirrors the brief)

Headless harness on SQLite (real SqlMigrator ×2 + real repositories):
(a) employee round-trip identical; terminated employee excluded from
a new run; (b) allocate 30 → approve 5 → balance 25; a 26-day
application refused without override (HR-1003) and allowed with it;
absence days recorded; (c) the §4 worked case nets **1213.33** and
run totals = Σ payslips; (d) posting: GL balanced, Dr/Cr split per
mapping, source-linked; reversal nets GL to zero and re-opens slips;
(e) a payment voucher through GlPosting reduces the 2204 payable by
the net; (f) migration double-run idempotent. Kern.Tests facts pin
the pure calculator (worked case, % of gross ordering, rounding,
employer exclusion) + the repository scenarios. WPF + live PG owed.

---

<!-- source: docs/BUILD_HR_PAYROLL.md -->

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
