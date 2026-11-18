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
    which is arithmetically identical: Dr gross+employer =
    Cr net + Σ deductions + Σ employer).
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
