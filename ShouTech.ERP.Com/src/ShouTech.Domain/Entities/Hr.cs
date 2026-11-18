namespace ShouTech.Domain.Entities;

/// <summary>Employee lifecycle (migration 065, design section 3.2).
/// Only Active employees enter a payroll run or the attendance grid;
/// Terminated employees keep all history and leave payroll.</summary>
public static class HrEmployeeStatuses
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
    public const string Terminated = "Terminated";
    public static readonly IReadOnlyList<string> All = new[] { Active, Suspended, Terminated };
}

/// <summary>Employment types (stored vocabulary, extensible per company).</summary>
public static class HrEmploymentTypes
{
    public const string FullTime = "Full-time";
    public const string PartTime = "Part-time";
    public const string Contract = "Contract";
    public const string Intern = "Intern";
    public const string Temporary = "Temporary";
    public static readonly IReadOnlyList<string> All = new[] { FullTime, PartTime, Contract, Intern, Temporary };
}

/// <summary>How the salary is paid (employee card, design section 3.2).</summary>
public static class HrSalaryModes
{
    public const string Bank = "Bank";
    public const string Cash = "Cash";
    public const string Cheque = "Cheque";
    public static readonly IReadOnlyList<string> All = new[] { Bank, Cash, Cheque };
}

/// <summary>Daily attendance statuses (design section 3.3).</summary>
public static class HrAttendanceStatuses
{
    public const string Present = "Present";
    public const string Absent = "Absent";
    public const string Late = "Late";
    public const string Leave = "Leave";
    public const string HalfDay = "HalfDay";
    public static readonly IReadOnlyList<string> All = new[] { Present, Absent, Late, Leave, HalfDay };

    /// <summary>One-letter bulk-grid codes (HrAttendanceWin): P/A/L/V/H.</summary>
    public static string? FromCode(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "P" => Present, "A" => Absent, "L" => Late, "V" => Leave, "H" => HalfDay,
        _ => null
    };
    public static string ToCode(string status) => status switch
    {
        Present => "P", Absent => "A", Late => "L", Leave => "V", HalfDay => "H",
        _ => string.Empty
    };
}

/// <summary>Leave application lifecycle (design section 3.4).</summary>
public static class HrLeaveStatuses
{
    public const string Open = "Open";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public static readonly IReadOnlyList<string> All = new[] { Open, Approved, Rejected, Cancelled };
}

/// <summary>Salary component kinds (design section 3.5). Employer
/// components are company cost only: they never touch gross or net.</summary>
public static class HrComponentKinds
{
    public const string Earning = "Earning";
    public const string Deduction = "Deduction";
    public const string Employer = "Employer";
    public static readonly IReadOnlyList<string> All = new[] { Earning, Deduction, Employer };
}

/// <summary>Component calculation kinds. Rates live in DATA (structure
/// lines), never in code — no country's law is hardcoded.</summary>
public static class HrCalcTypes
{
    public const string Fixed = "Fixed";
    public const string PercentOfBase = "PercentOfBase";
    public const string PercentOfGross = "PercentOfGross";
    public static readonly IReadOnlyList<string> All = new[] { Fixed, PercentOfBase, PercentOfGross };
}

/// <summary>Payroll run / payslip lifecycles (design section 3.6).
/// Reversal is an event, not a status: a reversed run returns to Draft.</summary>
public static class HrPayrollStatuses
{
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    public const string Final = "Final";
}

/// <summary>Canonical GL accounts for payroll defaults (every seeded
/// chart template carries both postable; see design section 5).</summary>
public static class HrGlAccounts
{
    public const string SalaryExpense = "501";
    public const string SalariesPayable = "2204";
    public const string Cash = "131";
    public const string Bank = "132";
}

/// <summary>A department (tree master, design section 3.1).</summary>
public sealed class HrDepartment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public Guid? ManagerEmployeeId { get; set; }
    public string? CostCenter { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => string.IsNullOrWhiteSpace(NameEn) ? Code : $"{Code} — {NameEn}";
}

/// <summary>A designation / job title (design section 3.1).</summary>
public sealed class HrDesignation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => string.IsNullOrWhiteSpace(NameEn) ? Code : $"{Code} — {NameEn}";
}

/// <summary>The employee card (design section 3.2 — the comparison's
/// CORE field set). Department/Designation/ReportsTo display names are
/// repository-resolved snapshots for grids; the ids are the truth.</summary>
public sealed class HrEmployee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EmployeeCode { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string? NameAr { get; set; }
    public string? Gender { get; set; }
    public string? DateOfBirth { get; set; }
    public string? NationalId { get; set; }
    public string? PassportNo { get; set; }
    public string? SocialInsuranceNo { get; set; }
    public string? TaxId { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? PersonalEmail { get; set; }
    public string? WorkEmail { get; set; }
    public string? AddressPresent { get; set; }
    public string? AddressPermanent { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public Guid? DesignationId { get; set; }
    public string? DesignationName { get; set; }
    public string? Branch { get; set; }
    public Guid? ReportsToId { get; set; }
    public string? ReportsToName { get; set; }
    public string? CostCenter { get; set; }
    public string HireDate { get; set; } = string.Empty;
    public string EmploymentType { get; set; } = HrEmploymentTypes.FullTime;
    public string Status { get; set; } = HrEmployeeStatuses.Active;
    public string? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }
    public string SalaryMode { get; set; } = HrSalaryModes.Bank;
    public string? BankName { get; set; }
    public string? BankAccountNo { get; set; }
    public decimal BaseSalary { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public string? UserId { get; set; }
    public string? Notes { get; set; }

    /// <summary>Full display name: first + middle + last, trimmed.</summary>
    public string FullName => string.Join(" ", new[] { FirstName, MiddleName, LastName }
        .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    public string DisplayName => string.IsNullOrWhiteSpace(EmployeeCode) ? FullName : $"{EmployeeCode} — {FullName}";
}

/// <summary>One daily attendance record (design section 3.3).</summary>
public sealed class HrAttendanceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public string AttendanceDate { get; set; } = string.Empty;
    public string Status { get; set; } = HrAttendanceStatuses.Present;
    public string? InTime { get; set; }
    public string? OutTime { get; set; }
    public decimal OvertimeHours { get; set; }
    public Guid? LeaveApplicationId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Per-employee monthly attendance totals (report + payroll input).</summary>
public sealed record HrAttendanceSummaryRow(
    Guid EmployeeId, string EmployeeCode, string EmployeeName,
    int PresentDays, int AbsentDays, int LateDays, int LeaveDays,
    decimal HalfDays, decimal OvertimeHours)
{
    /// <summary>Absence weight for payroll: full absent days + half of half-days.</summary>
    public decimal AbsentWeight => AbsentDays + HalfDays * 0.5m;
}

/// <summary>A leave type master (design section 3.4).</summary>
public sealed class HrLeaveType
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public decimal AnnualDays { get; set; }
    public bool IsPaid { get; set; } = true;
    public bool IsCarryForward { get; set; }
    public decimal MaxCarryForward { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => string.IsNullOrWhiteSpace(NameEn) ? Code : $"{Code} — {NameEn}";
}

/// <summary>One employee × type × year balance row.</summary>
public sealed class HrLeaveBalance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public string? EmployeeCode { get; set; }
    public string? EmployeeName { get; set; }
    public Guid LeaveTypeId { get; set; }
    public string? LeaveTypeCode { get; set; }
    public string? LeaveTypeName { get; set; }
    public int Year { get; set; }
    public decimal AllocatedDays { get; set; }
    public decimal UsedDays { get; set; }
    public decimal Balance => AllocatedDays - UsedDays;
}

/// <summary>A leave application with its approval workflow state.</summary>
public sealed class HrLeaveApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public string? EmployeeCode { get; set; }
    public string? EmployeeName { get; set; }
    public Guid LeaveTypeId { get; set; }
    public string? LeaveTypeCode { get; set; }
    public string? LeaveTypeName { get; set; }
    public bool LeaveTypeIsPaid { get; set; } = true;
    public string FromDate { get; set; } = string.Empty;
    public string ToDate { get; set; } = string.Empty;
    public decimal TotalDays { get; set; }
    public bool HalfDay { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = HrLeaveStatuses.Open;
    public string? DecidedBy { get; set; }
    public string? DecidedAt { get; set; }
    public string? DecisionNotes { get; set; }

    /// <summary>Calendar days inclusive of both ends; a single-day
    /// half-day application counts 0.5 (design section 3.4).</summary>
    public static decimal ComputeDays(string fromDate, string toDate, bool halfDay)
    {
        var from = DateOnly.Parse(fromDate, System.Globalization.CultureInfo.InvariantCulture);
        var to = DateOnly.Parse(toDate, System.Globalization.CultureInfo.InvariantCulture);
        var days = to.DayNumber - from.DayNumber + 1;
        if (days == 1 && halfDay) return 0.5m;
        return days;
    }
}

/// <summary>A salary component in the company catalog (design 3.5).</summary>
public sealed class HrSalaryComponent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string Kind { get; set; } = HrComponentKinds.Earning;
    public string DefaultCalcType { get; set; } = HrCalcTypes.Fixed;
    public decimal DefaultAmount { get; set; }
    public decimal DefaultPercent { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayName => string.IsNullOrWhiteSpace(NameEn) ? Code : $"{Code} — {NameEn}";
}

/// <summary>One structure line: a component with this structure's
/// calculation parameters and GL account mapping.</summary>
public sealed class HrStructureLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ComponentId { get; set; }
    public string? ComponentCode { get; set; }
    public string? ComponentName { get; set; }
    public string Kind { get; set; } = HrComponentKinds.Earning;
    public int LineNo { get; set; }
    public string CalcType { get; set; } = HrCalcTypes.Fixed;
    public decimal Amount { get; set; }
    public decimal Percent { get; set; }
    /// <summary>Earning/Employer: the expense (Dr) account. Deduction:
    /// the payable (Cr) account. Null = the design defaults
    /// (501 / 2204) applied by the calculator's caller.</summary>
    public string? AccountCode { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A salary structure (design section 3.5).</summary>
public sealed class HrSalaryStructure
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string AppliesTo { get; set; } = "Employee";
    public bool IsActive { get; set; } = true;
    public List<HrStructureLine> Lines { get; set; } = new();
    public string DisplayName => string.IsNullOrWhiteSpace(NameEn) ? Code : $"{Code} — {NameEn}";
}

/// <summary>A salary assignment: an employee (or a designation default)
/// bound to a structure with a contractual base (design section 3.5).
/// Exactly one of EmployeeId / DesignationId is set.</summary>
public sealed class HrSalaryAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? EmployeeId { get; set; }
    public Guid? DesignationId { get; set; }
    public Guid StructureId { get; set; }
    public string? StructureCode { get; set; }
    public decimal BaseSalary { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public string FromDate { get; set; } = string.Empty;
    public string? ToDate { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One computed payslip line (calculator output / stored row).</summary>
public sealed class HrPayslipLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int LineNo { get; set; }
    public string ComponentCode { get; set; } = string.Empty;
    public string? ComponentName { get; set; }
    public string Kind { get; set; } = HrComponentKinds.Earning;
    public string CalcType { get; set; } = HrCalcTypes.Fixed;
    public decimal Amount { get; set; }
    public bool IsSystem { get; set; }
    public string? AccountCode { get; set; }
    public string? Notes { get; set; }
}

/// <summary>One payslip (design section 3.6). Name/department snapshots
/// make the slip a historical document.</summary>
public sealed class HrPayslip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    public string? DesignationName { get; set; }
    public string? CostCenter { get; set; }
    public decimal BaseSalary { get; set; }
    public decimal PeriodDays { get; set; } = PayrollCalculator.PeriodDivisor;
    public decimal PaidDays { get; set; } = PayrollCalculator.PeriodDivisor;
    public decimal UnpaidLeaveDays { get; set; }
    public decimal AbsentDays { get; set; }
    public decimal GrossPay { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal NetPay { get; set; }
    public decimal EmployerCost { get; set; }
    public string Status { get; set; } = HrPayrollStatuses.Draft;
    public string? Notes { get; set; }
    public List<HrPayslipLine> Lines { get; set; } = new();
}

/// <summary>A monthly payroll run (design section 3.6).</summary>
public sealed class HrPayrollRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RunNo { get; set; } = string.Empty;
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public string PeriodFrom { get; set; } = string.Empty;
    public string PeriodTo { get; set; } = string.Empty;
    public string Status { get; set; } = HrPayrollStatuses.Draft;
    public decimal TotalGross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNet { get; set; }
    public decimal TotalEmployer { get; set; }
    public int EmployeeCount { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public Guid? JournalEntryId { get; set; }
    public string? JournalDocNo { get; set; }
    public string? ReversedJournalDocNo { get; set; }
    public string? Notes { get; set; }
    public string? PostedBy { get; set; }
    public string? PostedAt { get; set; }
    public List<HrPayslip> Payslips { get; set; } = new();
}

/// <summary>Report row: payroll totals per department for one run.</summary>
public sealed record HrPayrollDeptRow(
    string DepartmentName, int EmployeeCount,
    decimal Gross, decimal Deductions, decimal Net, decimal Employer);

/// <summary>
/// The payroll calculation model (design section 4) — PURE: no I/O, no
/// clock, no database. The repository feeds it stored inputs and stores
/// its output verbatim, so the Kern.Tests facts and the live engine can
/// never disagree.
///
/// Rules: daily rate = base / 30 (the monthly-payroll divisor, stored
/// per payslip); earnings Fixed / % of base / % of gross (gross-% lines
/// are computed on base + all other earnings, order-independent);
/// deductions likewise (% of gross uses the final gross); unpaid leave
/// and absence are EXPLICIT system deduction lines at the daily rate —
/// never silent; employer components are cost-only. Every line is
/// rounded to 2 decimals (AwayFromZero) and totals are sums of the
/// rounded lines, so a slip always foots exactly.
/// </summary>
public static class PayrollCalculator
{
    /// <summary>The monthly divisor: daily rate = base salary / 30.</summary>
    public const decimal PeriodDivisor = 30m;

    public const string BaseComponentCode = "BASIC";
    public const string UnpaidLeaveComponentCode = "UNPAID_LEAVE";
    public const string AbsenceComponentCode = "ABSENCE";

    public static decimal Round(decimal value)
        => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>One structure line as the calculator sees it.</summary>
    public sealed record CalcLine(
        string ComponentCode, string? ComponentName, string Kind,
        string CalcType, decimal Amount, decimal Percent, string? AccountCode);

    /// <summary>The computed slip: lines + footed totals.</summary>
    public sealed record CalcResult(
        IReadOnlyList<HrPayslipLine> Lines, decimal GrossPay,
        decimal TotalDeductions, decimal NetPay, decimal EmployerCost,
        decimal PaidDays, decimal DailyRate);

    public static CalcResult Calculate(
        decimal baseSalary, IReadOnlyList<CalcLine> structureLines,
        decimal unpaidLeaveDays, decimal absentDays)
    {
        if (baseSalary < 0) throw new ArgumentOutOfRangeException(nameof(baseSalary));
        if (unpaidLeaveDays < 0) throw new ArgumentOutOfRangeException(nameof(unpaidLeaveDays));
        if (absentDays < 0) throw new ArgumentOutOfRangeException(nameof(absentDays));
        var dailyRate = baseSalary / PeriodDivisor;

        var lines = new List<HrPayslipLine>();
        var lineNo = 0;
        void Add(string code, string? name, string kind, string calc, decimal amount,
            bool system, string? account, string? notes)
            => lines.Add(new HrPayslipLine
            {
                LineNo = ++lineNo, ComponentCode = code, ComponentName = name,
                Kind = kind, CalcType = calc, Amount = Round(amount),
                IsSystem = system, AccountCode = account, Notes = notes
            });

        // The base itself is always a line (BASIC) so a printed slip shows it.
        Add(BaseComponentCode, "Basic Salary", HrComponentKinds.Earning,
            HrCalcTypes.Fixed, baseSalary, false, HrGlAccounts.SalaryExpense, null);

        decimal Value(CalcLine l, decimal grossBase) => l.CalcType switch
        {
            HrCalcTypes.Fixed => l.Amount,
            HrCalcTypes.PercentOfBase => baseSalary * l.Percent / 100m,
            HrCalcTypes.PercentOfGross => grossBase * l.Percent / 100m,
            _ => throw new InvalidOperationException($"Unknown calculation type '{l.CalcType}'.")
        };

        // Earnings: non-gross-% first, then gross-% on (base + those).
        var earnings = structureLines.Where(l => l.Kind == HrComponentKinds.Earning).ToList();
        var plainEarnings = earnings.Where(l => l.CalcType != HrCalcTypes.PercentOfGross).ToList();
        var grossEarnings = earnings.Where(l => l.CalcType == HrCalcTypes.PercentOfGross).ToList();
        var plainSum = 0m;
        foreach (var l in plainEarnings)
        {
            var v = Round(Value(l, 0m));
            plainSum += v;
            Add(l.ComponentCode, l.ComponentName, l.Kind, l.CalcType, v, false, l.AccountCode, null);
        }
        var gross = baseSalary + plainSum;
        foreach (var l in grossEarnings)
        {
            var v = Round(Value(l, gross));
            gross += v;
            Add(l.ComponentCode, l.ComponentName, l.Kind, l.CalcType, v, false, l.AccountCode, null);
        }

        // Deductions: structure lines (% of gross uses the final gross).
        var deductions = 0m;
        foreach (var l in structureLines.Where(l => l.Kind == HrComponentKinds.Deduction))
        {
            var v = Round(Value(l, gross));
            deductions += v;
            Add(l.ComponentCode, l.ComponentName, l.Kind, l.CalcType, v, false, l.AccountCode, null);
        }

        // Explicit attendance/leave deductions — never silent (design 4.4).
        if (unpaidLeaveDays > 0)
        {
            var v = Round(dailyRate * unpaidLeaveDays);
            deductions += v;
            Add(UnpaidLeaveComponentCode, "Unpaid Leave Deduction", HrComponentKinds.Deduction,
                HrCalcTypes.Fixed, v, true, null,
                $"{unpaidLeaveDays} day(s) x {Round(dailyRate)}");
        }
        if (absentDays > 0)
        {
            var v = Round(dailyRate * absentDays);
            deductions += v;
            Add(AbsenceComponentCode, "Absence Deduction", HrComponentKinds.Deduction,
                HrCalcTypes.Fixed, v, true, null,
                $"{absentDays} day(s) x {Round(dailyRate)}");
        }

        // Employer contributions: cost only, excluded from gross/net.
        var employer = 0m;
        foreach (var l in structureLines.Where(l => l.Kind == HrComponentKinds.Employer))
        {
            var v = Round(Value(l, gross));
            employer += v;
            Add(l.ComponentCode, l.ComponentName, l.Kind, l.CalcType, v, false, l.AccountCode, null);
        }

        var paidDays = PeriodDivisor - unpaidLeaveDays - absentDays;
        if (paidDays < 0) paidDays = 0;
        return new CalcResult(lines, Round(gross), Round(deductions),
            Round(gross - deductions), Round(employer), paidDays, dailyRate);
    }
}
