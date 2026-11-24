using System.Globalization;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// HR / Payroll application service (migration 065, design
/// docs/BUILD_HR_DESIGN.md). All invariants are validated here before
/// the repository is touched (the repository re-checks company scope,
/// duplicates and state inside its transactions as a second line of
/// defence):
///   * masters carry a code and a name; an employee carries a code, a
///     first name and a real hire date; a Terminated employee carries
///     a termination date (HR-1002);
///   * attendance rows carry a known status and a real date;
///   * a leave application spans a valid range (its day count is the
///     repository's — one computation, one place);
///   * components / structures / assignments carry known kinds and
///     calculation types with non-negative amounts; an assignment
///     targets exactly one employee or designation (HR-1007);
///   * leave APPROVAL and payroll POSTING are separate operations
///     from editing — the windows gate them on HR.LEAVE.APPROVE /
///     HR.PAYROLL.POST, and the negative-balance override arrives
///     here as an explicit flag the window sets only when the caller
///     holds HR.LEAVE.OVERRIDE.
/// </summary>
public sealed class HrService
{
    private readonly IHrRepository _repository;

    public HrService(IHrRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    internal IHrRepository Repository => _repository;

    // ---- Organisation ---------------------------------------------------
    public Task<IReadOnlyList<HrDepartment>> GetDepartmentsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetDepartmentsAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveDepartmentAsync(string companyId, HrDepartment department, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(department);
        if (string.IsNullOrWhiteSpace(department.Code))
            throw new InvalidOperationException("Department code is required.");
        if (string.IsNullOrWhiteSpace(department.NameEn) && string.IsNullOrWhiteSpace(department.NameAr))
            throw new InvalidOperationException("Department name is required.");
        if (department.ParentId == department.Id)
            throw new InvalidOperationException("A department cannot be its own parent.");
        department.Code = department.Code.Trim();
        await _repository.SaveDepartmentAsync(companyId, department, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return department.Id;
    }

    public Task DeleteDepartmentAsync(string companyId, Guid departmentId, string userId, CancellationToken cancellationToken = default)
    {
        if (departmentId == Guid.Empty) throw new ArgumentException("Department is required.", nameof(departmentId));
        return _repository.SoftDeleteDepartmentAsync(RequireCompany(companyId), departmentId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrDesignation>> GetDesignationsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetDesignationsAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveDesignationAsync(string companyId, HrDesignation designation, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(designation);
        if (string.IsNullOrWhiteSpace(designation.Code))
            throw new InvalidOperationException("Designation code is required.");
        if (string.IsNullOrWhiteSpace(designation.NameEn) && string.IsNullOrWhiteSpace(designation.NameAr))
            throw new InvalidOperationException("Designation name is required.");
        designation.Code = designation.Code.Trim();
        await _repository.SaveDesignationAsync(companyId, designation, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return designation.Id;
    }

    public Task DeleteDesignationAsync(string companyId, Guid designationId, string userId, CancellationToken cancellationToken = default)
    {
        if (designationId == Guid.Empty) throw new ArgumentException("Designation is required.", nameof(designationId));
        return _repository.SoftDeleteDesignationAsync(RequireCompany(companyId), designationId, RequireUser(userId), cancellationToken);
    }

    // ---- Employees ---------------------------------------------------------
    public Task<IReadOnlyList<HrEmployee>> GetEmployeesAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
        => _repository.GetEmployeesAsync(RequireCompany(companyId), status, cancellationToken);

    public Task<HrEmployee?> GetEmployeeAsync(string companyId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        if (employeeId == Guid.Empty) throw new ArgumentException("Employee is required.", nameof(employeeId));
        return _repository.GetEmployeeAsync(RequireCompany(companyId), employeeId, cancellationToken);
    }

    public Task<string> NextEmployeeCodeAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.NextEmployeeCodeAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveEmployeeAsync(string companyId, HrEmployee employee, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ValidateEmployee(employee);
        await _repository.SaveEmployeeAsync(companyId, employee, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return employee.Id;
    }

    public Task DeleteEmployeeAsync(string companyId, Guid employeeId, string userId, CancellationToken cancellationToken = default)
    {
        if (employeeId == Guid.Empty) throw new ArgumentException("Employee is required.", nameof(employeeId));
        return _repository.SoftDeleteEmployeeAsync(RequireCompany(companyId), employeeId, RequireUser(userId), cancellationToken);
    }

    private static void ValidateEmployee(HrEmployee employee)
    {
        ArgumentNullException.ThrowIfNull(employee);
        if (string.IsNullOrWhiteSpace(employee.EmployeeCode))
            throw new AppError(ErrorCodes.HrEmployeeInvalid, "Employee code is required.");
        if (string.IsNullOrWhiteSpace(employee.FirstName))
            throw new AppError(ErrorCodes.HrEmployeeInvalid, "Employee first name is required.", employee.EmployeeCode.Trim());
        if (!IsDate(employee.HireDate))
            throw new AppError(ErrorCodes.HrEmployeeInvalid, "A valid hire date is required.", employee.EmployeeCode.Trim());
        if (!HrEmployeeStatuses.All.Contains(employee.Status))
            throw new AppError(ErrorCodes.HrEmployeeInvalid, $"Unknown employee status '{employee.Status}'.", employee.EmployeeCode.Trim());
        if (!HrEmploymentTypes.All.Contains(employee.EmploymentType))
            throw new AppError(ErrorCodes.HrEmployeeInvalid, $"Unknown employment type '{employee.EmploymentType}'.", employee.EmployeeCode.Trim());
        if (!HrSalaryModes.All.Contains(employee.SalaryMode))
            throw new AppError(ErrorCodes.HrEmployeeInvalid, $"Unknown salary mode '{employee.SalaryMode}'.", employee.EmployeeCode.Trim());
        if (employee.Status == HrEmployeeStatuses.Terminated && !IsDate(employee.TerminationDate))
            throw new AppError(ErrorCodes.HrEmployeeInvalid, "A terminated employee requires a termination date.", employee.EmployeeCode.Trim());
        if (employee.Status != HrEmployeeStatuses.Terminated)
        {
            employee.TerminationDate = null;
            employee.TerminationReason = null;
        }
        if (employee.BaseSalary < 0)
            throw new AppError(ErrorCodes.HrEmployeeInvalid, "Base salary cannot be negative.", employee.EmployeeCode.Trim());
        if (employee.ReportsToId == employee.Id)
            throw new AppError(ErrorCodes.HrEmployeeInvalid, "An employee cannot report to themselves.", employee.EmployeeCode.Trim());
        employee.EmployeeCode = employee.EmployeeCode.Trim();
        employee.FirstName = employee.FirstName.Trim();
        if (string.IsNullOrWhiteSpace(employee.CurrencyCode)) employee.CurrencyCode = "SAR";
    }

    // ---- Attendance ------------------------------------------------------------
    public Task<IReadOnlyList<HrAttendanceRecord>> GetAttendanceAsync(string companyId, int year, int month, CancellationToken cancellationToken = default)
        => _repository.GetAttendanceAsync(RequireCompany(companyId), year, month, cancellationToken);

    public Task SaveAttendanceBatchAsync(string companyId, IReadOnlyList<HrAttendanceRecord> records, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            if (record.EmployeeId == Guid.Empty)
                throw new InvalidOperationException("Every attendance record requires an employee.");
            if (!IsDate(record.AttendanceDate))
                throw new InvalidOperationException("Every attendance record requires a valid date.");
            if (!HrAttendanceStatuses.All.Contains(record.Status))
                throw new InvalidOperationException($"Unknown attendance status '{record.Status}'.");
            if (record.OvertimeHours < 0)
                throw new InvalidOperationException("Overtime hours cannot be negative.");
        }
        return _repository.SaveAttendanceBatchAsync(companyId, records, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrAttendanceSummaryRow>> GetAttendanceSummaryAsync(string companyId, int year, int month, CancellationToken cancellationToken = default)
        => _repository.GetAttendanceSummaryAsync(RequireCompany(companyId), year, month, cancellationToken);

    // ---- Leave ---------------------------------------------------------------------
    public Task<IReadOnlyList<HrLeaveType>> GetLeaveTypesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetLeaveTypesAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveLeaveTypeAsync(string companyId, HrLeaveType leaveType, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(leaveType);
        if (string.IsNullOrWhiteSpace(leaveType.Code))
            throw new InvalidOperationException("Leave type code is required.");
        if (string.IsNullOrWhiteSpace(leaveType.NameEn) && string.IsNullOrWhiteSpace(leaveType.NameAr))
            throw new InvalidOperationException("Leave type name is required.");
        if (leaveType.AnnualDays < 0 || leaveType.MaxCarryForward < 0)
            throw new InvalidOperationException("Leave day counts cannot be negative.");
        leaveType.Code = leaveType.Code.Trim();
        await _repository.SaveLeaveTypeAsync(companyId, leaveType, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return leaveType.Id;
    }

    public Task DeleteLeaveTypeAsync(string companyId, Guid leaveTypeId, string userId, CancellationToken cancellationToken = default)
    {
        if (leaveTypeId == Guid.Empty) throw new ArgumentException("Leave type is required.", nameof(leaveTypeId));
        return _repository.SoftDeleteLeaveTypeAsync(RequireCompany(companyId), leaveTypeId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrLeaveBalance>> GetLeaveBalancesAsync(string companyId, int year, CancellationToken cancellationToken = default)
        => _repository.GetLeaveBalancesAsync(RequireCompany(companyId), year, cancellationToken);

    public Task AllocateLeaveAsync(string companyId, Guid employeeId, Guid leaveTypeId, int year, decimal allocatedDays, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (employeeId == Guid.Empty) throw new ArgumentException("Employee is required.", nameof(employeeId));
        if (leaveTypeId == Guid.Empty) throw new ArgumentException("Leave type is required.", nameof(leaveTypeId));
        if (allocatedDays < 0) throw new InvalidOperationException("Allocated days cannot be negative.");
        return _repository.AllocateLeaveAsync(companyId, employeeId, leaveTypeId, year, allocatedDays, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrLeaveApplication>> GetLeaveApplicationsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
        => _repository.GetLeaveApplicationsAsync(RequireCompany(companyId), status, cancellationToken);

    public async Task<Guid> SaveLeaveApplicationAsync(string companyId, HrLeaveApplication application, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(application);
        if (application.EmployeeId == Guid.Empty)
            throw new InvalidOperationException("A leave application requires an employee.");
        if (application.LeaveTypeId == Guid.Empty)
            throw new InvalidOperationException("A leave application requires a leave type.");
        if (!IsDate(application.FromDate) || !IsDate(application.ToDate))
            throw new InvalidOperationException("A leave application requires valid from/to dates.");
        if (string.CompareOrdinal(application.ToDate, application.FromDate) < 0)
            throw new InvalidOperationException("The leave end date cannot be before its start date.");
        await _repository.SaveLeaveApplicationAsync(companyId, application, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return application.Id;
    }

    /// <summary>Approval is a separate operation from editing (the
    /// window gates it on HR.LEAVE.APPROVE); allowOverride must be set
    /// by the caller only when the user holds HR.LEAVE.OVERRIDE.</summary>
    public Task ApproveLeaveAsync(string companyId, Guid applicationId, bool allowOverride, string userId, CancellationToken cancellationToken = default)
    {
        if (applicationId == Guid.Empty) throw new ArgumentException("Application is required.", nameof(applicationId));
        return _repository.ApproveLeaveAsync(RequireCompany(companyId), applicationId, allowOverride, RequireUser(userId), cancellationToken);
    }

    public Task RejectLeaveAsync(string companyId, Guid applicationId, string? notes, string userId, CancellationToken cancellationToken = default)
    {
        if (applicationId == Guid.Empty) throw new ArgumentException("Application is required.", nameof(applicationId));
        return _repository.RejectLeaveAsync(RequireCompany(companyId), applicationId, notes, RequireUser(userId), cancellationToken);
    }

    public Task CancelLeaveAsync(string companyId, Guid applicationId, string userId, CancellationToken cancellationToken = default)
    {
        if (applicationId == Guid.Empty) throw new ArgumentException("Application is required.", nameof(applicationId));
        return _repository.CancelLeaveAsync(RequireCompany(companyId), applicationId, RequireUser(userId), cancellationToken);
    }

    // ---- Salary structures ------------------------------------------------------------
    public Task<IReadOnlyList<HrSalaryComponent>> GetComponentsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetComponentsAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveComponentAsync(string companyId, HrSalaryComponent component, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(component);
        if (string.IsNullOrWhiteSpace(component.Code))
            throw new InvalidOperationException("Component code is required.");
        if (string.IsNullOrWhiteSpace(component.NameEn) && string.IsNullOrWhiteSpace(component.NameAr))
            throw new InvalidOperationException("Component name is required.");
        if (!HrComponentKinds.All.Contains(component.Kind))
            throw new InvalidOperationException($"Unknown component kind '{component.Kind}'.");
        if (!HrCalcTypes.All.Contains(component.DefaultCalcType))
            throw new InvalidOperationException($"Unknown calculation type '{component.DefaultCalcType}'.");
        if (component.DefaultAmount < 0 || component.DefaultPercent < 0)
            throw new InvalidOperationException("Component amounts and percentages cannot be negative.");
        component.Code = component.Code.Trim();
        await _repository.SaveComponentAsync(companyId, component, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return component.Id;
    }

    public Task DeleteComponentAsync(string companyId, Guid componentId, string userId, CancellationToken cancellationToken = default)
    {
        if (componentId == Guid.Empty) throw new ArgumentException("Component is required.", nameof(componentId));
        return _repository.SoftDeleteComponentAsync(RequireCompany(companyId), componentId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrSalaryStructure>> GetStructuresAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetStructuresAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveStructureAsync(string companyId, HrSalaryStructure structure, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(structure);
        if (string.IsNullOrWhiteSpace(structure.Code))
            throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "Salary structure code is required.");
        if (string.IsNullOrWhiteSpace(structure.NameEn) && string.IsNullOrWhiteSpace(structure.NameAr))
            throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "Salary structure name is required.", structure.Code.Trim());
        var seen = new HashSet<Guid>();
        foreach (var line in structure.Lines)
        {
            if (line.ComponentId == Guid.Empty)
                throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "Every structure line requires a component.", structure.Code.Trim());
            if (!seen.Add(line.ComponentId))
                throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "A component appears more than once on this structure.", structure.Code.Trim());
            if (!HrCalcTypes.All.Contains(line.CalcType))
                throw new AppError(ErrorCodes.HrSalaryStructureInvalid, $"Unknown calculation type '{line.CalcType}'.", structure.Code.Trim());
            if (line.Amount < 0 || line.Percent < 0)
                throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "Structure line amounts and percentages cannot be negative.", structure.Code.Trim());
        }
        structure.Code = structure.Code.Trim();
        await _repository.SaveStructureAsync(companyId, structure, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return structure.Id;
    }

    public Task DeleteStructureAsync(string companyId, Guid structureId, string userId, CancellationToken cancellationToken = default)
    {
        if (structureId == Guid.Empty) throw new ArgumentException("Structure is required.", nameof(structureId));
        return _repository.SoftDeleteStructureAsync(RequireCompany(companyId), structureId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrSalaryAssignment>> GetAssignmentsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetAssignmentsAsync(RequireCompany(companyId), cancellationToken);

    public async Task<Guid> SaveAssignmentAsync(string companyId, HrSalaryAssignment assignment, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(assignment);
        if ((assignment.EmployeeId is null) == (assignment.DesignationId is null))
            throw new AppError(ErrorCodes.HrSalaryStructureInvalid,
                "A salary assignment targets exactly one employee or one designation.");
        if (assignment.StructureId == Guid.Empty)
            throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "A salary assignment requires a structure.");
        if (assignment.BaseSalary < 0)
            throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "Base salary cannot be negative.");
        if (!IsDate(assignment.FromDate))
            throw new AppError(ErrorCodes.HrSalaryStructureInvalid, "A salary assignment requires a valid from date.");
        if (string.IsNullOrWhiteSpace(assignment.CurrencyCode)) assignment.CurrencyCode = "SAR";
        if (assignment.EmployeeId is Guid employeeId)
        {
            var employee = await _repository.GetEmployeeAsync(companyId, employeeId, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrEmployeeInvalid, "The assignment's employee was not found for this company.");
            if (employee.Status == HrEmployeeStatuses.Terminated)
                throw new AppError(ErrorCodes.HrEmployeeNotPayable,
                    $"Employee '{employee.EmployeeCode}' is terminated and cannot receive a salary assignment.", employee.EmployeeCode);
        }
        await _repository.SaveAssignmentAsync(companyId, assignment, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        return assignment.Id;
    }

    public Task DeactivateAssignmentAsync(string companyId, Guid assignmentId, string userId, CancellationToken cancellationToken = default)
    {
        if (assignmentId == Guid.Empty) throw new ArgumentException("Assignment is required.", nameof(assignmentId));
        return _repository.DeactivateAssignmentAsync(RequireCompany(companyId), assignmentId, RequireUser(userId), cancellationToken);
    }

    // ---- Payroll ---------------------------------------------------------------------------
    public Task<HrPayrollRun> GenerateRunAsync(string companyId, int year, int month, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (month is < 1 or > 12) throw new InvalidOperationException("Payroll month must be between 1 and 12.");
        if (year is < 2000 or > 2100) throw new InvalidOperationException("Payroll year is out of range.");
        return _repository.GenerateRunAsync(companyId, year, month, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrPayrollRun>> GetRunsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetRunsAsync(RequireCompany(companyId), cancellationToken);

    public Task<HrPayrollRun?> GetRunAsync(string companyId, Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run is required.", nameof(runId));
        return _repository.GetRunAsync(RequireCompany(companyId), runId, cancellationToken);
    }

    public Task UpdatePayslipLinesAsync(string companyId, Guid payslipId, IReadOnlyList<HrPayslipLine> lines, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        if (payslipId == Guid.Empty) throw new ArgumentException("Payslip is required.", nameof(payslipId));
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.ComponentCode))
                throw new InvalidOperationException("Every payslip line requires a component code.");
            if (!HrComponentKinds.All.Contains(line.Kind))
                throw new InvalidOperationException($"Unknown payslip line kind '{line.Kind}'.");
            if (line.Amount < 0)
                throw new InvalidOperationException("Payslip line amounts cannot be negative.");
        }
        return _repository.UpdatePayslipLinesAsync(companyId, payslipId, lines, RequireUser(userId), cancellationToken);
    }

    /// <summary>Posting is a separate operation from generating/editing
    /// (the window gates it on HR.PAYROLL.POST).</summary>
    public Task<string> PostRunAsync(string companyId, Guid runId, string userId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run is required.", nameof(runId));
        return _repository.PostRunAsync(RequireCompany(companyId), runId, RequireUser(userId), cancellationToken);
    }

    public Task<string> ReverseRunAsync(string companyId, Guid runId, string userId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run is required.", nameof(runId));
        return _repository.ReverseRunAsync(RequireCompany(companyId), runId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<HrPayrollDeptRow>> GetPayrollSummaryByDeptAsync(string companyId, Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Run is required.", nameof(runId));
        return _repository.GetPayrollSummaryByDeptAsync(RequireCompany(companyId), runId, cancellationToken);
    }

    // ---- Helpers --------------------------------------------------------------------------------
    private static bool IsDate(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User is required.", nameof(userId));
        return userId;
    }
}
