-- @shoutech-migration 065_hr_payroll.sql 7469D4F7A8129D18E184C35449DF5831A565DBF1E612BF42C8680744E72B0A29 01D15DC08B4D479F9CD2B89B5F721F0E3429C5DFD7DAAC6FA77C82F77DDA8812 01D15DC08B4D479F9CD2B89B5F721F0E3429C5DFD7DAAC6FA77C82F77DDA8812 01D15DC08B4D479F9CD2B89B5F721F0E3429C5DFD7DAAC6FA77C82F77DDA8812
-- ============================================================================
-- 065 - HR / Payroll build (SQLite)
--
-- Build HR/Payroll (see docs/BUILD_HR_DESIGN.md). Baseline was 0%: the
-- only prior HR traces were an inert appset.json descriptor, a license
-- key, error-catalog promises and four orphan PostgreSQL tables in
-- 003/004 (UUID/company-FK shape, referenced by zero code). This
-- migration creates the real module in the app's own conventions:
-- company keyed by company CODE (TEXT, the parties/CRM world), TEXT
-- Guid ids, soft delete, audit columns. Tables:
--   hr_departments / hr_designations      organisation masters (2.2)
--   hr_employees                          employee card (2.1 CORE set)
--   hr_attendance                         daily records (2.3)
--   hr_leave_types / hr_leave_balances / hr_leave_applications (2.4)
--   hr_salary_components / hr_salary_structures /
--   hr_salary_structure_lines / hr_employee_salary   (2.5 structures)
--   hr_payroll_runs / hr_payslips / hr_payslip_lines (2.5 run + slips)
-- Leave types and salary components are seeded PER COMPANY ON FIRST
-- USE by HrRepository (a migration cannot know future companies; the
-- 057 precedent) -- nothing is seeded here except permissions.
--
-- GL ACCOUNT CODES (canonical templates, cod/app/src/Resources/COA):
--   501 "Salaries and Wages" (postable in every template) -- the
--   default earning/employer expense; 2204 "Salaries and Wages
--   Payable" (every template) -- net pay + default deduction payable;
--   2211 "Payroll Taxes Payable" (most templates) -- a typical
--   social-insurance mapping, used only when a company maps a
--   component to it. The mapping lives on hr_salary_structure_lines.
--
-- Idempotent: the checksum ledger skips a re-run; inside the file
-- every CREATE is IF NOT EXISTS and permission seeds are
-- INSERT OR IGNORE + UPDATE.
-- ============================================================================

-- ---- Organisation masters ---------------------------------------------------
CREATE TABLE IF NOT EXISTS hr_departments (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    parent_id TEXT REFERENCES hr_departments(id),
    manager_employee_id TEXT,
    cost_center TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_departments_company_code
    ON hr_departments(company_id, code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_hr_departments_company
    ON hr_departments(company_id, is_active) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS hr_designations (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    department_id TEXT REFERENCES hr_departments(id),
    description TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_designations_company_code
    ON hr_designations(company_id, code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_hr_designations_company
    ON hr_designations(company_id, is_active) WHERE is_deleted = 0;

-- ---- Employee card ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS hr_employees (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    employee_code TEXT NOT NULL,
    first_name TEXT NOT NULL DEFAULT '',
    middle_name TEXT,
    last_name TEXT,
    name_ar TEXT,
    gender TEXT,
    date_of_birth TEXT,
    national_id TEXT,
    passport_no TEXT,
    social_insurance_no TEXT,
    tax_id TEXT,
    phone TEXT,
    mobile TEXT,
    personal_email TEXT,
    work_email TEXT,
    address_present TEXT,
    address_permanent TEXT,
    emergency_contact_name TEXT,
    emergency_contact_phone TEXT,
    department_id TEXT REFERENCES hr_departments(id),
    designation_id TEXT REFERENCES hr_designations(id),
    branch TEXT,
    reports_to_id TEXT REFERENCES hr_employees(id),
    cost_center TEXT,
    hire_date TEXT NOT NULL,
    employment_type TEXT NOT NULL DEFAULT 'Full-time',
    status TEXT NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active','Suspended','Terminated')),
    termination_date TEXT,
    termination_reason TEXT,
    salary_mode TEXT NOT NULL DEFAULT 'Bank',
    bank_name TEXT,
    bank_account_no TEXT,
    base_salary REAL NOT NULL DEFAULT 0 CHECK (base_salary >= 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    user_id TEXT,
    notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_employees_company_code
    ON hr_employees(company_id, employee_code) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_hr_employees_company_status
    ON hr_employees(company_id, status) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_hr_employees_company_dept
    ON hr_employees(company_id, department_id) WHERE is_deleted = 0;

-- ---- Attendance -----------------------------------------------------------------
CREATE TABLE IF NOT EXISTS hr_attendance (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    employee_id TEXT NOT NULL REFERENCES hr_employees(id),
    attendance_date TEXT NOT NULL,
    status TEXT NOT NULL
        CHECK (status IN ('Present','Absent','Late','Leave','HalfDay')),
    in_time TEXT,
    out_time TEXT,
    overtime_hours REAL NOT NULL DEFAULT 0 CHECK (overtime_hours >= 0),
    leave_application_id TEXT,
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_attendance_company_emp_date
    ON hr_attendance(company_id, employee_id, attendance_date);
CREATE INDEX IF NOT EXISTS ix_hr_attendance_company_date
    ON hr_attendance(company_id, attendance_date);

-- ---- Leave -----------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS hr_leave_types (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    annual_days REAL NOT NULL DEFAULT 0 CHECK (annual_days >= 0),
    is_paid INTEGER NOT NULL DEFAULT 1,
    is_carry_forward INTEGER NOT NULL DEFAULT 0,
    max_carry_forward REAL NOT NULL DEFAULT 0 CHECK (max_carry_forward >= 0),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_leave_types_company_code
    ON hr_leave_types(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS hr_leave_balances (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    employee_id TEXT NOT NULL REFERENCES hr_employees(id),
    leave_type_id TEXT NOT NULL REFERENCES hr_leave_types(id),
    year INTEGER NOT NULL,
    allocated_days REAL NOT NULL DEFAULT 0 CHECK (allocated_days >= 0),
    used_days REAL NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_leave_balances_company_emp_type_year
    ON hr_leave_balances(company_id, employee_id, leave_type_id, year);

CREATE TABLE IF NOT EXISTS hr_leave_applications (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    employee_id TEXT NOT NULL REFERENCES hr_employees(id),
    leave_type_id TEXT NOT NULL REFERENCES hr_leave_types(id),
    from_date TEXT NOT NULL,
    to_date TEXT NOT NULL,
    total_days REAL NOT NULL CHECK (total_days > 0),
    half_day INTEGER NOT NULL DEFAULT 0,
    reason TEXT,
    status TEXT NOT NULL DEFAULT 'Open'
        CHECK (status IN ('Open','Approved','Rejected','Cancelled')),
    decided_by TEXT,
    decided_at TEXT,
    decision_notes TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE INDEX IF NOT EXISTS ix_hr_leave_apps_company_emp
    ON hr_leave_applications(company_id, employee_id, status) WHERE is_deleted = 0;
CREATE INDEX IF NOT EXISTS ix_hr_leave_apps_company_dates
    ON hr_leave_applications(company_id, from_date, to_date) WHERE is_deleted = 0;

-- ---- Salary components / structures / assignments --------------------------------
CREATE TABLE IF NOT EXISTS hr_salary_components (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    kind TEXT NOT NULL CHECK (kind IN ('Earning','Deduction','Employer')),
    default_calc_type TEXT NOT NULL DEFAULT 'Fixed'
        CHECK (default_calc_type IN ('Fixed','PercentOfBase','PercentOfGross')),
    default_amount REAL NOT NULL DEFAULT 0,
    default_percent REAL NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_salary_components_company_code
    ON hr_salary_components(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS hr_salary_structures (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    code TEXT NOT NULL,
    name_en TEXT NOT NULL DEFAULT '',
    name_ar TEXT NOT NULL DEFAULT '',
    applies_to TEXT NOT NULL DEFAULT 'Employee'
        CHECK (applies_to IN ('Employee','Designation')),
    is_active INTEGER NOT NULL DEFAULT 1,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_salary_structures_company_code
    ON hr_salary_structures(company_id, code) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS hr_salary_structure_lines (
    id TEXT PRIMARY KEY,
    structure_id TEXT NOT NULL REFERENCES hr_salary_structures(id),
    component_id TEXT NOT NULL REFERENCES hr_salary_components(id),
    line_no INTEGER NOT NULL DEFAULT 0,
    calc_type TEXT NOT NULL DEFAULT 'Fixed'
        CHECK (calc_type IN ('Fixed','PercentOfBase','PercentOfGross')),
    amount REAL NOT NULL DEFAULT 0 CHECK (amount >= 0),
    percent REAL NOT NULL DEFAULT 0 CHECK (percent >= 0),
    account_code TEXT,
    is_active INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS ix_hr_salary_structure_lines_structure
    ON hr_salary_structure_lines(structure_id, line_no);

CREATE TABLE IF NOT EXISTS hr_employee_salary (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    employee_id TEXT REFERENCES hr_employees(id),
    designation_id TEXT REFERENCES hr_designations(id),
    structure_id TEXT NOT NULL REFERENCES hr_salary_structures(id),
    base_salary REAL NOT NULL DEFAULT 0 CHECK (base_salary >= 0),
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    from_date TEXT NOT NULL,
    to_date TEXT,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT,
    CHECK ((employee_id IS NOT NULL AND designation_id IS NULL)
        OR (employee_id IS NULL AND designation_id IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_hr_employee_salary_company_emp
    ON hr_employee_salary(company_id, employee_id, is_active);
CREATE INDEX IF NOT EXISTS ix_hr_employee_salary_company_desig
    ON hr_employee_salary(company_id, designation_id, is_active);

-- ---- Payroll runs / payslips -------------------------------------------------------
CREATE TABLE IF NOT EXISTS hr_payroll_runs (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    run_no TEXT NOT NULL,
    period_year INTEGER NOT NULL,
    period_month INTEGER NOT NULL CHECK (period_month BETWEEN 1 AND 12),
    period_from TEXT NOT NULL,
    period_to TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Posted')),
    total_gross REAL NOT NULL DEFAULT 0,
    total_deductions REAL NOT NULL DEFAULT 0,
    total_net REAL NOT NULL DEFAULT 0,
    total_employer REAL NOT NULL DEFAULT 0,
    employee_count INTEGER NOT NULL DEFAULT 0,
    currency_code TEXT NOT NULL DEFAULT 'SAR',
    journal_entry_id TEXT,
    journal_doc_no TEXT,
    reversed_journal_doc_no TEXT,
    notes TEXT,
    posted_by TEXT,
    posted_at TEXT,
    is_deleted INTEGER NOT NULL DEFAULT 0,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_payroll_runs_company_period
    ON hr_payroll_runs(company_id, period_year, period_month) WHERE is_deleted = 0;

CREATE TABLE IF NOT EXISTS hr_payslips (
    id TEXT PRIMARY KEY,
    company_id TEXT NOT NULL,
    run_id TEXT NOT NULL REFERENCES hr_payroll_runs(id),
    employee_id TEXT NOT NULL REFERENCES hr_employees(id),
    employee_code TEXT NOT NULL DEFAULT '',
    employee_name TEXT NOT NULL DEFAULT '',
    department_name TEXT,
    designation_name TEXT,
    base_salary REAL NOT NULL DEFAULT 0,
    period_days REAL NOT NULL DEFAULT 30,
    paid_days REAL NOT NULL DEFAULT 30,
    unpaid_leave_days REAL NOT NULL DEFAULT 0,
    absent_days REAL NOT NULL DEFAULT 0,
    gross_pay REAL NOT NULL DEFAULT 0,
    total_deductions REAL NOT NULL DEFAULT 0,
    net_pay REAL NOT NULL DEFAULT 0,
    employer_cost REAL NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Final')),
    notes TEXT,
    created_by TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now')),
    updated_by TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_hr_payslips_run_employee
    ON hr_payslips(run_id, employee_id);
CREATE INDEX IF NOT EXISTS ix_hr_payslips_company_emp
    ON hr_payslips(company_id, employee_id);

CREATE TABLE IF NOT EXISTS hr_payslip_lines (
    id TEXT PRIMARY KEY,
    payslip_id TEXT NOT NULL REFERENCES hr_payslips(id),
    line_no INTEGER NOT NULL DEFAULT 0,
    component_code TEXT NOT NULL,
    component_name TEXT,
    kind TEXT NOT NULL CHECK (kind IN ('Earning','Deduction','Employer')),
    calc_type TEXT NOT NULL DEFAULT 'Fixed',
    amount REAL NOT NULL DEFAULT 0,
    is_system INTEGER NOT NULL DEFAULT 0,
    account_code TEXT,
    notes TEXT
);
CREATE INDEX IF NOT EXISTS ix_hr_payslip_lines_payslip
    ON hr_payslip_lines(payslip_id, line_no);

-- ---- Permissions (step-1 pattern; display_order continues at 369) ------------------
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.EMPLOYEE.VIEW','HR Employee - View','HR','EMPLOYEE','VIEW','HR Employee - View','HR Employee - View','View employees and the employee card (Build HR 065)',369,0);
UPDATE permissions SET group_code='HR',feature_code='EMPLOYEE',operation_code='VIEW',name_ar='HR Employee - View',name_en='HR Employee - View',display_order=369,is_sensitive=0 WHERE permission_code='HR.EMPLOYEE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.EMPLOYEE.EDIT','HR Employee - Edit','HR','EMPLOYEE','EDIT','HR Employee - Edit','HR Employee - Edit','Create and edit employees, change status lifecycle (Build HR 065)',370,0);
UPDATE permissions SET group_code='HR',feature_code='EMPLOYEE',operation_code='EDIT',name_ar='HR Employee - Edit',name_en='HR Employee - Edit',display_order=370,is_sensitive=0 WHERE permission_code='HR.EMPLOYEE.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.ORG.VIEW','HR Organisation - View','HR','ORG','VIEW','HR Organisation - View','HR Organisation - View','View departments and designations (Build HR 065)',371,0);
UPDATE permissions SET group_code='HR',feature_code='ORG',operation_code='VIEW',name_ar='HR Organisation - View',name_en='HR Organisation - View',display_order=371,is_sensitive=0 WHERE permission_code='HR.ORG.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.ORG.EDIT','HR Organisation - Edit','HR','ORG','EDIT','HR Organisation - Edit','HR Organisation - Edit','Create and edit departments and designations (Build HR 065)',372,0);
UPDATE permissions SET group_code='HR',feature_code='ORG',operation_code='EDIT',name_ar='HR Organisation - Edit',name_en='HR Organisation - Edit',display_order=372,is_sensitive=0 WHERE permission_code='HR.ORG.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.ATTENDANCE.VIEW','HR Attendance - View','HR','ATTENDANCE','VIEW','HR Attendance - View','HR Attendance - View','View attendance records (Build HR 065)',373,0);
UPDATE permissions SET group_code='HR',feature_code='ATTENDANCE',operation_code='VIEW',name_ar='HR Attendance - View',name_en='HR Attendance - View',display_order=373,is_sensitive=0 WHERE permission_code='HR.ATTENDANCE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.ATTENDANCE.EDIT','HR Attendance - Edit','HR','ATTENDANCE','EDIT','HR Attendance - Edit','HR Attendance - Edit','Record and edit daily attendance (Build HR 065)',374,0);
UPDATE permissions SET group_code='HR',feature_code='ATTENDANCE',operation_code='EDIT',name_ar='HR Attendance - Edit',name_en='HR Attendance - Edit',display_order=374,is_sensitive=0 WHERE permission_code='HR.ATTENDANCE.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.LEAVE.VIEW','HR Leave - View','HR','LEAVE','VIEW','HR Leave - View','HR Leave - View','View leave types, balances and applications (Build HR 065)',375,0);
UPDATE permissions SET group_code='HR',feature_code='LEAVE',operation_code='VIEW',name_ar='HR Leave - View',name_en='HR Leave - View',display_order=375,is_sensitive=0 WHERE permission_code='HR.LEAVE.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.LEAVE.EDIT','HR Leave - Edit','HR','LEAVE','EDIT','HR Leave - Edit','HR Leave - Edit','Create leave applications, manage types and allocations (Build HR 065)',376,0);
UPDATE permissions SET group_code='HR',feature_code='LEAVE',operation_code='EDIT',name_ar='HR Leave - Edit',name_en='HR Leave - Edit',display_order=376,is_sensitive=0 WHERE permission_code='HR.LEAVE.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.LEAVE.APPROVE','HR Leave - Approve','HR','LEAVE','APPROVE','HR Leave - Approve','HR Leave - Approve','Approve or reject leave applications (Build HR 065)',377,0);
UPDATE permissions SET group_code='HR',feature_code='LEAVE',operation_code='APPROVE',name_ar='HR Leave - Approve',name_en='HR Leave - Approve',display_order=377,is_sensitive=0 WHERE permission_code='HR.LEAVE.APPROVE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.LEAVE.OVERRIDE','HR Leave - Override Balance','HR','LEAVE','OVERRIDE','HR Leave - Override Balance','HR Leave - Override Balance','Approve leave beyond the available balance (negative balance) (Build HR 065)',378,1);
UPDATE permissions SET group_code='HR',feature_code='LEAVE',operation_code='OVERRIDE',name_ar='HR Leave - Override Balance',name_en='HR Leave - Override Balance',display_order=378,is_sensitive=1 WHERE permission_code='HR.LEAVE.OVERRIDE';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.PAYROLL.VIEW','HR Payroll - View','HR','PAYROLL','VIEW','HR Payroll - View','HR Payroll - View','View salary structures, payroll runs and payslips (Build HR 065)',379,0);
UPDATE permissions SET group_code='HR',feature_code='PAYROLL',operation_code='VIEW',name_ar='HR Payroll - View',name_en='HR Payroll - View',display_order=379,is_sensitive=0 WHERE permission_code='HR.PAYROLL.VIEW';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.PAYROLL.EDIT','HR Payroll - Edit','HR','PAYROLL','EDIT','HR Payroll - Edit','HR Payroll - Edit','Edit salary structures and assignments, generate runs, adjust draft payslips (Build HR 065)',380,0);
UPDATE permissions SET group_code='HR',feature_code='PAYROLL',operation_code='EDIT',name_ar='HR Payroll - Edit',name_en='HR Payroll - Edit',display_order=380,is_sensitive=0 WHERE permission_code='HR.PAYROLL.EDIT';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.PAYROLL.POST','HR Payroll - Post','HR','PAYROLL','POST','HR Payroll - Post','HR Payroll - Post','Post and reverse payroll runs to the general ledger (Build HR 065)',381,1);
UPDATE permissions SET group_code='HR',feature_code='PAYROLL',operation_code='POST',name_ar='HR Payroll - Post',name_en='HR Payroll - Post',display_order=381,is_sensitive=1 WHERE permission_code='HR.PAYROLL.POST';
INSERT OR IGNORE INTO permissions(permission_code,permission_name,group_code,feature_code,operation_code,name_ar,name_en,description,display_order,is_sensitive) VALUES ('HR.REPORT.VIEW','HR Reports - View','HR','REPORT','VIEW','HR Reports - View','HR Reports - View','View HR and payroll reports (Build HR 065)',382,0);
UPDATE permissions SET group_code='HR',feature_code='REPORT',operation_code='VIEW',name_ar='HR Reports - View',name_en='HR Reports - View',display_order=382,is_sensitive=0 WHERE permission_code='HR.REPORT.VIEW';

INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_ADMINISTRATOR' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('HR.EMPLOYEE.VIEW','HR.EMPLOYEE.EDIT','HR.ORG.VIEW','HR.ORG.EDIT','HR.ATTENDANCE.VIEW','HR.ATTENDANCE.EDIT','HR.LEAVE.VIEW','HR.LEAVE.EDIT','HR.LEAVE.APPROVE','HR.LEAVE.OVERRIDE','HR.PAYROLL.VIEW','HR.PAYROLL.EDIT','HR.PAYROLL.POST','HR.REPORT.VIEW');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_USER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('HR.EMPLOYEE.VIEW','HR.EMPLOYEE.EDIT','HR.ORG.VIEW','HR.ORG.EDIT','HR.ATTENDANCE.VIEW','HR.ATTENDANCE.EDIT','HR.LEAVE.VIEW','HR.LEAVE.EDIT','HR.LEAVE.APPROVE','HR.PAYROLL.VIEW','HR.PAYROLL.EDIT','HR.REPORT.VIEW');
INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
SELECT r.role_id, p.permission_id FROM roles r CROSS JOIN permissions p
WHERE r.role_code = 'ROLE_VIEWER' AND r.is_active = 1 AND p.is_active = 1 AND p.permission_code IN ('HR.EMPLOYEE.VIEW','HR.ORG.VIEW','HR.ATTENDANCE.VIEW','HR.LEAVE.VIEW','HR.PAYROLL.VIEW','HR.REPORT.VIEW');
