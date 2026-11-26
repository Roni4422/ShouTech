using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>HR / Payroll persistence (migration 065, design
/// docs/BUILD_HR_DESIGN.md). HR lives in the party world, so every
/// method keys the company by its CODE — the key parties / sales_reps
/// use — unlike the journal-world repositories, which key by Guid.
/// The one journal touchpoint (payroll posting) resolves the Guid
/// inside its transaction and posts through GlPosting.</summary>
public interface IHrRepository
{
    // ---- Organisation masters -------------------------------------------
    Task<IReadOnlyList<HrDepartment>> GetDepartmentsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveDepartmentAsync(string companyId, HrDepartment department, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteDepartmentAsync(string companyId, Guid departmentId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrDesignation>> GetDesignationsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveDesignationAsync(string companyId, HrDesignation designation, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteDesignationAsync(string companyId, Guid designationId, string userId, CancellationToken cancellationToken = default);

    // ---- Employees ---------------------------------------------------------
    Task<IReadOnlyList<HrEmployee>> GetEmployeesAsync(string companyId, string? status = null, CancellationToken cancellationToken = default);
    Task<HrEmployee?> GetEmployeeAsync(string companyId, Guid employeeId, CancellationToken cancellationToken = default);
    Task SaveEmployeeAsync(string companyId, HrEmployee employee, string userId, CancellationToken cancellationToken = default);
    Task<string> NextEmployeeCodeAsync(string companyId, CancellationToken cancellationToken = default);
    Task SoftDeleteEmployeeAsync(string companyId, Guid employeeId, string userId, CancellationToken cancellationToken = default);

    // ---- Attendance ----------------------------------------------------------
    Task<IReadOnlyList<HrAttendanceRecord>> GetAttendanceAsync(string companyId, int year, int month, CancellationToken cancellationToken = default);
    /// <summary>Bulk upsert of one month's records in ONE transaction
    /// (unique key company+employee+date makes re-saving idempotent).</summary>
    Task SaveAttendanceBatchAsync(string companyId, IReadOnlyList<HrAttendanceRecord> records, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrAttendanceSummaryRow>> GetAttendanceSummaryAsync(string companyId, int year, int month, CancellationToken cancellationToken = default);

    // ---- Leave -----------------------------------------------------------------
    /// <summary>The company's leave types; seeds the design's neutral
    /// defaults on first use for the company.</summary>
    Task<IReadOnlyList<HrLeaveType>> GetLeaveTypesAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveLeaveTypeAsync(string companyId, HrLeaveType leaveType, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteLeaveTypeAsync(string companyId, Guid leaveTypeId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrLeaveBalance>> GetLeaveBalancesAsync(string companyId, int year, CancellationToken cancellationToken = default);
    /// <summary>Upserts one allocation (allocated_days) for
    /// employee × type × year; used_days is never touched here.</summary>
    Task AllocateLeaveAsync(string companyId, Guid employeeId, Guid leaveTypeId, int year, decimal allocatedDays, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrLeaveApplication>> GetLeaveApplicationsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default);
    Task SaveLeaveApplicationAsync(string companyId, HrLeaveApplication application, string userId, CancellationToken cancellationToken = default);
    /// <summary>Approval in ONE transaction: status re-check, balance
    /// check (HR-1003 unless allowOverride — the service passes it only
    /// when the caller holds HR.LEAVE.OVERRIDE), balance deduction, and
    /// Leave attendance rows for the application's days.</summary>
    Task ApproveLeaveAsync(string companyId, Guid applicationId, bool allowOverride, string userId, CancellationToken cancellationToken = default);
    Task RejectLeaveAsync(string companyId, Guid applicationId, string? notes, string userId, CancellationToken cancellationToken = default);
    /// <summary>Cancels an application; an approved one restores its
    /// balance and removes exactly the attendance rows it created.</summary>
    Task CancelLeaveAsync(string companyId, Guid applicationId, string userId, CancellationToken cancellationToken = default);

    // ---- Salary components / structures / assignments -------------------------
    /// <summary>The company's component catalog; seeds the design's
    /// neutral starter set (all rates zero) on first use.</summary>
    Task<IReadOnlyList<HrSalaryComponent>> GetComponentsAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveComponentAsync(string companyId, HrSalaryComponent component, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteComponentAsync(string companyId, Guid componentId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrSalaryStructure>> GetStructuresAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveStructureAsync(string companyId, HrSalaryStructure structure, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteStructureAsync(string companyId, Guid structureId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrSalaryAssignment>> GetAssignmentsAsync(string companyId, CancellationToken cancellationToken = default);
    /// <summary>Saves an assignment; activating one deactivates the
    /// previous active assignment for the same employee (or
    /// designation), and an employee assignment syncs the employee
    /// card's base salary / currency convenience copy.</summary>
    Task SaveAssignmentAsync(string companyId, HrSalaryAssignment assignment, string userId, CancellationToken cancellationToken = default);
    Task DeactivateAssignmentAsync(string companyId, Guid assignmentId, string userId, CancellationToken cancellationToken = default);

    // ---- Payroll runs -------------------------------------------------------------
    /// <summary>Generates (or regenerates) the Draft run for a period:
    /// payslips for every eligible Active employee through the pure
    /// PayrollCalculator. A Posted run refuses with HR-1005.</summary>
    Task<HrPayrollRun> GenerateRunAsync(string companyId, int year, int month, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HrPayrollRun>> GetRunsAsync(string companyId, CancellationToken cancellationToken = default);
    Task<HrPayrollRun?> GetRunAsync(string companyId, Guid runId, CancellationToken cancellationToken = default);
    /// <summary>Replaces a DRAFT payslip's lines (exceptional-item
    /// editing) and recomputes payslip + run totals in one transaction.
    /// System lines must come back unchanged (HR-1006 otherwise).</summary>
    Task UpdatePayslipLinesAsync(string companyId, Guid payslipId, IReadOnlyList<HrPayslipLine> lines, string userId, CancellationToken cancellationToken = default);
    /// <summary>Posts a Draft run: payslips finalize and the totals post
    /// to GL through GlPosting per the component account mapping
    /// (design section 5), in ONE transaction. Returns the journal
    /// document number.</summary>
    Task<string> PostRunAsync(string companyId, Guid runId, string userId, CancellationToken cancellationToken = default);
    /// <summary>Reverses a Posted run's GL entry (GlPosting reversal)
    /// and re-opens the run + payslips to Draft, in ONE transaction.
    /// Returns the reversal journal document number.</summary>
    Task<string> ReverseRunAsync(string companyId, Guid runId, string userId, CancellationToken cancellationToken = default);

    // ---- Reports ---------------------------------------------------------------------
    Task<IReadOnlyList<HrPayrollDeptRow>> GetPayrollSummaryByDeptAsync(string companyId, Guid runId, CancellationToken cancellationToken = default);
}
