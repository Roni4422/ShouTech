using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Infrastructure;

/// <summary>
/// Provider-neutral HR / Payroll persistence (migration 065, design
/// docs/BUILD_HR_DESIGN.md): parameterized SQL only, company scoping
/// (by CODE — the party world) on every statement, single-transaction
/// writes, soft deletes. Payroll generation runs the pure
/// <see cref="PayrollCalculator"/>; posting runs through
/// <see cref="GlPosting"/> inside the run's own transaction, so the
/// run, its payslips and the journal commit or roll back together.
/// </summary>
public sealed class HrRepository : IHrRepository
{
    private readonly string _provider;
    private readonly string _connectionString;

    public HrRepository(string provider, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        _provider = NormalizeProvider(provider);
        _connectionString = connectionString;
    }

    private string NotDeleted => _provider == "PostgreSQL" ? "is_deleted = false" : "is_deleted = 0";
    private string BoolTrue => _provider == "PostgreSQL" ? "true" : "1";
    private string BoolFalse => _provider == "PostgreSQL" ? "false" : "0";
    private string NowExpr => _provider == "PostgreSQL" ? "now()" : "datetime('now')";
    private object Bool(bool value) => _provider == "PostgreSQL" ? value : (value ? 1 : 0);

    // ==================================================================
    // Departments / designations
    // ==================================================================

    public async Task<IReadOnlyList<HrDepartment>> GetDepartmentsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, parent_id, manager_employee_id, cost_center, is_active FROM hr_departments WHERE company_id = @company AND {NotDeleted} ORDER BY code");
        Add(command, "@company", companyId.Trim());
        var result = new List<HrDepartment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrDepartment
            {
                Id = ReadGuid(reader, 0), Code = ReadString(reader, 1) ?? string.Empty,
                NameEn = ReadString(reader, 2) ?? string.Empty, NameAr = ReadString(reader, 3) ?? string.Empty,
                ParentId = ReadGuidOrNull(reader, 4), ManagerEmployeeId = ReadGuidOrNull(reader, 5),
                CostCenter = ReadString(reader, 6), IsActive = ReadBool(reader, 7)
            });
        return result;
    }

    public async Task SaveDepartmentAsync(string companyId, HrDepartment department, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(department);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_departments WHERE company_id = @company AND code = @code AND id <> @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@code", department.Code.Trim()), ("@id", FormatGuid(department.Id))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterCodeDuplicate,
                    $"Department code '{department.Code.Trim()}' already exists for this company.", department.Code.Trim());
            await UpsertAsync(connection, transaction, "hr_departments", department.Id, companyId, userId,
                new[]
                {
                    ("code", (object)department.Code.Trim()), ("name_en", (object)department.NameEn.Trim()),
                    ("name_ar", (object)department.NameAr.Trim()),
                    ("parent_id", (object?)NullGuid(department.ParentId)),
                    ("manager_employee_id", (object?)NullGuid(department.ManagerEmployeeId)),
                    ("cost_center", (object?)NullIfBlank(department.CostCenter)),
                    ("is_active", Bool(department.IsActive))
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeleteDepartmentAsync(string companyId, Guid departmentId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_employees WHERE company_id = @company AND department_id = @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@id", FormatGuid(departmentId))).ConfigureAwait(false) > 0
                || await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_departments WHERE company_id = @company AND parent_id = @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@id", FormatGuid(departmentId))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterInUse,
                    "This department is still referenced by employees or child departments and cannot be deleted.");
            await SoftDeleteAsync(connection, transaction, "hr_departments", companyId, departmentId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<HrDesignation>> GetDesignationsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, department_id, description, is_active FROM hr_designations WHERE company_id = @company AND {NotDeleted} ORDER BY code");
        Add(command, "@company", companyId.Trim());
        var result = new List<HrDesignation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrDesignation
            {
                Id = ReadGuid(reader, 0), Code = ReadString(reader, 1) ?? string.Empty,
                NameEn = ReadString(reader, 2) ?? string.Empty, NameAr = ReadString(reader, 3) ?? string.Empty,
                DepartmentId = ReadGuidOrNull(reader, 4), Description = ReadString(reader, 5),
                IsActive = ReadBool(reader, 6)
            });
        return result;
    }

    public async Task SaveDesignationAsync(string companyId, HrDesignation designation, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(designation);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_designations WHERE company_id = @company AND code = @code AND id <> @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@code", designation.Code.Trim()), ("@id", FormatGuid(designation.Id))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterCodeDuplicate,
                    $"Designation code '{designation.Code.Trim()}' already exists for this company.", designation.Code.Trim());
            await UpsertAsync(connection, transaction, "hr_designations", designation.Id, companyId, userId,
                new[]
                {
                    ("code", (object)designation.Code.Trim()), ("name_en", (object)designation.NameEn.Trim()),
                    ("name_ar", (object)designation.NameAr.Trim()),
                    ("department_id", (object?)NullGuid(designation.DepartmentId)),
                    ("description", (object?)NullIfBlank(designation.Description)),
                    ("is_active", Bool(designation.IsActive))
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeleteDesignationAsync(string companyId, Guid designationId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_employees WHERE company_id = @company AND designation_id = @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@id", FormatGuid(designationId))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterInUse,
                    "This designation is still referenced by employees and cannot be deleted.");
            await SoftDeleteAsync(connection, transaction, "hr_designations", companyId, designationId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    // ==================================================================
    // Employees
    // ==================================================================

    private const string EmployeeSelect = """
        SELECT e.id, e.employee_code, e.first_name, e.middle_name, e.last_name, e.name_ar,
               e.gender, e.date_of_birth, e.national_id, e.passport_no, e.social_insurance_no,
               e.tax_id, e.phone, e.mobile, e.personal_email, e.work_email, e.address_present,
               e.address_permanent, e.emergency_contact_name, e.emergency_contact_phone,
               e.department_id, d.name_en, e.designation_id, g.name_en, e.branch,
               e.reports_to_id, e.cost_center, e.hire_date, e.employment_type, e.status,
               e.termination_date, e.termination_reason, e.salary_mode, e.bank_name,
               e.bank_account_no, e.base_salary, e.currency_code, e.user_id, e.notes
        FROM hr_employees e
        LEFT JOIN hr_departments d ON d.id = e.department_id
        LEFT JOIN hr_designations g ON g.id = e.designation_id
        """;

    public async Task<IReadOnlyList<HrEmployee>> GetEmployeesAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var statusFilter = string.IsNullOrWhiteSpace(status) ? string.Empty : " AND e.status = @status";
        await using var command = CreateCommand(connection, null,
            EmployeeSelect + $" WHERE e.company_id = @company AND e.{NotDeleted}{statusFilter} ORDER BY e.employee_code");
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(status)) Add(command, "@status", status.Trim());
        var result = new List<HrEmployee>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadEmployee(reader));
        // Reports-to display names resolve in a second pass (self-join
        // would double the column map for one label).
        var names = result.ToDictionary(e => e.Id, e => e.FullName);
        foreach (var employee in result)
            if (employee.ReportsToId is Guid managerId && names.TryGetValue(managerId, out var managerName))
                employee.ReportsToName = managerName;
        return result;
    }

    public async Task<HrEmployee?> GetEmployeeAsync(string companyId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            EmployeeSelect + $" WHERE e.company_id = @company AND e.id = @id AND e.{NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(employeeId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEmployee(reader) : null;
    }

    public async Task SaveEmployeeAsync(string companyId, HrEmployee employee, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(employee);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_employees WHERE company_id = @company AND employee_code = @code AND id <> @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@code", employee.EmployeeCode.Trim()), ("@id", FormatGuid(employee.Id))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrEmployeeCodeDuplicate,
                    $"Employee code '{employee.EmployeeCode.Trim()}' already exists for this company.", employee.EmployeeCode.Trim());
            await UpsertAsync(connection, transaction, "hr_employees", employee.Id, companyId, userId,
                new[]
                {
                    ("employee_code", (object)employee.EmployeeCode.Trim()),
                    ("first_name", (object)employee.FirstName.Trim()),
                    ("middle_name", (object?)NullIfBlank(employee.MiddleName)),
                    ("last_name", (object?)NullIfBlank(employee.LastName)),
                    ("name_ar", (object?)NullIfBlank(employee.NameAr)),
                    ("gender", (object?)NullIfBlank(employee.Gender)),
                    ("date_of_birth", (object?)NullIfBlank(employee.DateOfBirth)),
                    ("national_id", (object?)NullIfBlank(employee.NationalId)),
                    ("passport_no", (object?)NullIfBlank(employee.PassportNo)),
                    ("social_insurance_no", (object?)NullIfBlank(employee.SocialInsuranceNo)),
                    ("tax_id", (object?)NullIfBlank(employee.TaxId)),
                    ("phone", (object?)NullIfBlank(employee.Phone)),
                    ("mobile", (object?)NullIfBlank(employee.Mobile)),
                    ("personal_email", (object?)NullIfBlank(employee.PersonalEmail)),
                    ("work_email", (object?)NullIfBlank(employee.WorkEmail)),
                    ("address_present", (object?)NullIfBlank(employee.AddressPresent)),
                    ("address_permanent", (object?)NullIfBlank(employee.AddressPermanent)),
                    ("emergency_contact_name", (object?)NullIfBlank(employee.EmergencyContactName)),
                    ("emergency_contact_phone", (object?)NullIfBlank(employee.EmergencyContactPhone)),
                    ("department_id", (object?)NullGuid(employee.DepartmentId)),
                    ("designation_id", (object?)NullGuid(employee.DesignationId)),
                    ("branch", (object?)NullIfBlank(employee.Branch)),
                    ("reports_to_id", (object?)NullGuid(employee.ReportsToId)),
                    ("cost_center", (object?)NullIfBlank(employee.CostCenter)),
                    ("hire_date", (object)employee.HireDate.Trim()),
                    ("employment_type", (object)employee.EmploymentType.Trim()),
                    ("status", (object)employee.Status.Trim()),
                    ("termination_date", (object?)NullIfBlank(employee.TerminationDate)),
                    ("termination_reason", (object?)NullIfBlank(employee.TerminationReason)),
                    ("salary_mode", (object)employee.SalaryMode.Trim()),
                    ("bank_name", (object?)NullIfBlank(employee.BankName)),
                    ("bank_account_no", (object?)NullIfBlank(employee.BankAccountNo)),
                    ("base_salary", (object)employee.BaseSalary),
                    ("currency_code", (object)employee.CurrencyCode.Trim().ToUpperInvariant()),
                    ("user_id", (object?)NullIfBlank(employee.UserId)),
                    ("notes", (object?)NullIfBlank(employee.Notes))
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeleteEmployeeAsync(string companyId, Guid employeeId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An employee with payslips is a historical document holder:
            // terminate them instead of deleting (design section 3.2).
            if (await CountAsync(connection, transaction,
                    "SELECT COUNT(*) FROM hr_payslips WHERE company_id = @company AND employee_id = @id",
                    ("@company", companyId.Trim()), ("@id", FormatGuid(employeeId))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterInUse,
                    "This employee has payslips and cannot be deleted; terminate the employee instead.");
            await SoftDeleteAsync(connection, transaction, "hr_employees", companyId, employeeId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    private static HrEmployee ReadEmployee(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0), EmployeeCode = ReadString(reader, 1) ?? string.Empty,
        FirstName = ReadString(reader, 2) ?? string.Empty, MiddleName = ReadString(reader, 3),
        LastName = ReadString(reader, 4), NameAr = ReadString(reader, 5),
        Gender = ReadString(reader, 6), DateOfBirth = ReadString(reader, 7),
        NationalId = ReadString(reader, 8), PassportNo = ReadString(reader, 9),
        SocialInsuranceNo = ReadString(reader, 10), TaxId = ReadString(reader, 11),
        Phone = ReadString(reader, 12), Mobile = ReadString(reader, 13),
        PersonalEmail = ReadString(reader, 14), WorkEmail = ReadString(reader, 15),
        AddressPresent = ReadString(reader, 16), AddressPermanent = ReadString(reader, 17),
        EmergencyContactName = ReadString(reader, 18), EmergencyContactPhone = ReadString(reader, 19),
        DepartmentId = ReadGuidOrNull(reader, 20), DepartmentName = ReadString(reader, 21),
        DesignationId = ReadGuidOrNull(reader, 22), DesignationName = ReadString(reader, 23),
        Branch = ReadString(reader, 24), ReportsToId = ReadGuidOrNull(reader, 25),
        CostCenter = ReadString(reader, 26), HireDate = ReadString(reader, 27) ?? string.Empty,
        EmploymentType = ReadString(reader, 28) ?? HrEmploymentTypes.FullTime,
        Status = ReadString(reader, 29) ?? HrEmployeeStatuses.Active,
        TerminationDate = ReadString(reader, 30), TerminationReason = ReadString(reader, 31),
        SalaryMode = ReadString(reader, 32) ?? HrSalaryModes.Bank, BankName = ReadString(reader, 33),
        BankAccountNo = ReadString(reader, 34), BaseSalary = ReadDecimal(reader, 35),
        CurrencyCode = ReadString(reader, 36) ?? "SAR", UserId = ReadString(reader, 37),
        Notes = ReadString(reader, 38)
    };

    // ==================================================================
    // Attendance
    // ==================================================================

    public async Task<IReadOnlyList<HrAttendanceRecord>> GetAttendanceAsync(string companyId, int year, int month, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var (from, to) = PeriodBounds(year, month);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            "SELECT id, employee_id, attendance_date, status, in_time, out_time, overtime_hours, leave_application_id, notes FROM hr_attendance WHERE company_id = @company AND attendance_date >= @from AND attendance_date <= @to ORDER BY attendance_date, employee_id");
        Add(command, "@company", companyId.Trim());
        Add(command, "@from", from);
        Add(command, "@to", to);
        var result = new List<HrAttendanceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadAttendance(reader));
        return result;
    }

    public async Task SaveAttendanceBatchAsync(string companyId, IReadOnlyList<HrAttendanceRecord> records, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(records);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var record in records)
            {
                // Leave rows owned by an approved application are not
                // overwritten by the bulk grid (they are leave, not entry).
                await using var command = CreateCommand(connection, transaction, $"""
                    INSERT INTO hr_attendance (id, company_id, employee_id, attendance_date, status, in_time, out_time, overtime_hours, notes, created_by, updated_by, updated_at)
                    VALUES (@id, @company, @emp, @date, @status, @in, @out, @ot, @notes, @actor, @actor, {NowExpr})
                    ON CONFLICT(company_id, employee_id, attendance_date) DO UPDATE SET
                        status = excluded.status, in_time = excluded.in_time, out_time = excluded.out_time,
                        overtime_hours = excluded.overtime_hours, notes = excluded.notes,
                        updated_by = excluded.updated_by, updated_at = excluded.updated_at
                    WHERE hr_attendance.leave_application_id IS NULL
                    """);
                Add(command, "@id", FormatGuid(record.Id == Guid.Empty ? Guid.NewGuid() : record.Id));
                Add(command, "@company", companyId.Trim());
                Add(command, "@emp", FormatGuid(record.EmployeeId));
                Add(command, "@date", record.AttendanceDate.Trim());
                Add(command, "@status", record.Status.Trim());
                Add(command, "@in", (object?)NullIfBlank(record.InTime) ?? DBNull.Value);
                Add(command, "@out", (object?)NullIfBlank(record.OutTime) ?? DBNull.Value);
                Add(command, "@ot", record.OvertimeHours);
                Add(command, "@notes", (object?)NullIfBlank(record.Notes) ?? DBNull.Value);
                Add(command, "@actor", userId.Trim());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<HrAttendanceSummaryRow>> GetAttendanceSummaryAsync(string companyId, int year, int month, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var records = await GetAttendanceAsync(companyId, year, month, cancellationToken).ConfigureAwait(false);
        var employees = await GetEmployeesAsync(companyId, null, cancellationToken).ConfigureAwait(false);
        var byEmployee = records.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<HrAttendanceSummaryRow>();
        foreach (var employee in employees)
        {
            if (!byEmployee.TryGetValue(employee.Id, out var rows)) rows = new List<HrAttendanceRecord>();
            result.Add(new HrAttendanceSummaryRow(employee.Id, employee.EmployeeCode, employee.FullName,
                rows.Count(r => r.Status == HrAttendanceStatuses.Present),
                rows.Count(r => r.Status == HrAttendanceStatuses.Absent),
                rows.Count(r => r.Status == HrAttendanceStatuses.Late),
                rows.Count(r => r.Status == HrAttendanceStatuses.Leave),
                rows.Count(r => r.Status == HrAttendanceStatuses.HalfDay),
                rows.Sum(r => r.OvertimeHours)));
        }
        return result;
    }

    private static HrAttendanceRecord ReadAttendance(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0), EmployeeId = ReadGuid(reader, 1),
        AttendanceDate = ReadString(reader, 2) ?? string.Empty, Status = ReadString(reader, 3) ?? HrAttendanceStatuses.Present,
        InTime = ReadString(reader, 4), OutTime = ReadString(reader, 5),
        OvertimeHours = ReadDecimal(reader, 6), LeaveApplicationId = ReadGuidOrNull(reader, 7),
        Notes = ReadString(reader, 8)
    };

    // ==================================================================
    // Leave
    // ==================================================================

    public async Task<IReadOnlyList<HrLeaveType>> GetLeaveTypesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLeaveTypesAsync(connection, null, companyId, "system", cancellationToken).ConfigureAwait(false);
        return await LoadLeaveTypesAsync(connection, null, companyId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<HrLeaveType>> LoadLeaveTypesAsync(DbConnection connection, DbTransaction? transaction, string companyId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            $"SELECT id, code, name_en, name_ar, annual_days, is_paid, is_carry_forward, max_carry_forward, is_active FROM hr_leave_types WHERE company_id = @company AND {NotDeleted} ORDER BY code");
        Add(command, "@company", companyId.Trim());
        var result = new List<HrLeaveType>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrLeaveType
            {
                Id = ReadGuid(reader, 0), Code = ReadString(reader, 1) ?? string.Empty,
                NameEn = ReadString(reader, 2) ?? string.Empty, NameAr = ReadString(reader, 3) ?? string.Empty,
                AnnualDays = ReadDecimal(reader, 4), IsPaid = ReadBool(reader, 5),
                IsCarryForward = ReadBool(reader, 6), MaxCarryForward = ReadDecimal(reader, 7),
                IsActive = ReadBool(reader, 8)
            });
        return result;
    }

    /// <summary>Seeds the design's neutral defaults on first use
    /// (ANNUAL 30 paid / SICK 15 paid / UNPAID unpaid / EMERGENCY 5
    /// paid). Editable data — no code depends on the codes.</summary>
    private async Task EnsureLeaveTypesAsync(DbConnection connection, DbTransaction? outerTransaction, string companyId, string userId, CancellationToken cancellationToken)
    {
        if (await CountAsync(connection, outerTransaction,
                $"SELECT COUNT(*) FROM hr_leave_types WHERE company_id = @company AND {NotDeleted}",
                ("@company", companyId.Trim())).ConfigureAwait(false) > 0)
            return;
        var ownTransaction = outerTransaction is null;
        var transaction = outerTransaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (code, name, days, paid) in new[]
            {
                ("ANNUAL", "Annual Leave", 30m, true), ("SICK", "Sick Leave", 15m, true),
                ("UNPAID", "Unpaid Leave", 0m, false), ("EMERGENCY", "Emergency Leave", 5m, true)
            })
            {
                await using var insert = CreateCommand(connection, transaction, $"""
                    INSERT INTO hr_leave_types (id, company_id, code, name_en, name_ar, annual_days, is_paid, is_carry_forward, max_carry_forward, is_active, is_deleted, created_by)
                    VALUES (@id, @company, @code, @name, @name, @days, @paid, {BoolFalse}, 0, {BoolTrue}, {BoolFalse}, @actor)
                    """);
                Add(insert, "@id", FormatGuid(Guid.NewGuid()));
                Add(insert, "@company", companyId.Trim());
                Add(insert, "@code", code);
                Add(insert, "@name", name);
                Add(insert, "@days", days);
                Add(insert, "@paid", Bool(paid));
                Add(insert, "@actor", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (ownTransaction) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (ownTransaction) await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
        finally { if (ownTransaction) await transaction.DisposeAsync().ConfigureAwait(false); }
    }

    public async Task SaveLeaveTypeAsync(string companyId, HrLeaveType leaveType, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(leaveType);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_leave_types WHERE company_id = @company AND code = @code AND id <> @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@code", leaveType.Code.Trim()), ("@id", FormatGuid(leaveType.Id))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterCodeDuplicate,
                    $"Leave type code '{leaveType.Code.Trim()}' already exists for this company.", leaveType.Code.Trim());
            await UpsertAsync(connection, transaction, "hr_leave_types", leaveType.Id, companyId, userId,
                new[]
                {
                    ("code", (object?)leaveType.Code.Trim()), ("name_en", (object)leaveType.NameEn.Trim()),
                    ("name_ar", (object)leaveType.NameAr.Trim()), ("annual_days", (object)leaveType.AnnualDays),
                    ("is_paid", Bool(leaveType.IsPaid)), ("is_carry_forward", Bool(leaveType.IsCarryForward)),
                    ("max_carry_forward", (object)leaveType.MaxCarryForward), ("is_active", Bool(leaveType.IsActive))
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeleteLeaveTypeAsync(string companyId, Guid leaveTypeId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SoftDeleteAsync(connection, transaction, "hr_leave_types", companyId, leaveTypeId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<HrLeaveBalance>> GetLeaveBalancesAsync(string companyId, int year, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT b.id, b.employee_id, e.employee_code, e.first_name, e.middle_name, e.last_name,
                   b.leave_type_id, t.code, t.name_en, b.year, b.allocated_days, b.used_days
            FROM hr_leave_balances b
            JOIN hr_employees e ON e.id = b.employee_id
            JOIN hr_leave_types t ON t.id = b.leave_type_id
            WHERE b.company_id = @company AND b.year = @year
            ORDER BY e.employee_code, t.code
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@year", year);
        var result = new List<HrLeaveBalance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrLeaveBalance
            {
                Id = ReadGuid(reader, 0), EmployeeId = ReadGuid(reader, 1),
                EmployeeCode = ReadString(reader, 2),
                EmployeeName = string.Join(" ", new[] { ReadString(reader, 3), ReadString(reader, 4), ReadString(reader, 5) }.Where(p => !string.IsNullOrWhiteSpace(p))),
                LeaveTypeId = ReadGuid(reader, 6), LeaveTypeCode = ReadString(reader, 7),
                LeaveTypeName = ReadString(reader, 8), Year = Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture),
                AllocatedDays = ReadDecimal(reader, 10), UsedDays = ReadDecimal(reader, 11)
            });
        return result;
    }

    public async Task AllocateLeaveAsync(string companyId, Guid employeeId, Guid leaveTypeId, int year, decimal allocatedDays, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(connection, transaction, $"""
                INSERT INTO hr_leave_balances (id, company_id, employee_id, leave_type_id, year, allocated_days, used_days, created_by, updated_by, updated_at)
                VALUES (@id, @company, @emp, @type, @year, @days, 0, @actor, @actor, {NowExpr})
                ON CONFLICT(company_id, employee_id, leave_type_id, year) DO UPDATE SET
                    allocated_days = excluded.allocated_days, updated_by = excluded.updated_by, updated_at = excluded.updated_at
                """);
            Add(command, "@id", FormatGuid(Guid.NewGuid()));
            Add(command, "@company", companyId.Trim());
            Add(command, "@emp", FormatGuid(employeeId));
            Add(command, "@type", FormatGuid(leaveTypeId));
            Add(command, "@year", year);
            Add(command, "@days", allocatedDays);
            Add(command, "@actor", userId.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    private const string LeaveAppSelect = """
        SELECT a.id, a.employee_id, e.employee_code, e.first_name, e.middle_name, e.last_name,
               a.leave_type_id, t.code, t.name_en, t.is_paid, a.from_date, a.to_date, a.total_days,
               a.half_day, a.reason, a.status, a.decided_by, a.decided_at, a.decision_notes
        FROM hr_leave_applications a
        JOIN hr_employees e ON e.id = a.employee_id
        JOIN hr_leave_types t ON t.id = a.leave_type_id
        """;

    public async Task<IReadOnlyList<HrLeaveApplication>> GetLeaveApplicationsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var statusFilter = string.IsNullOrWhiteSpace(status) ? string.Empty : " AND a.status = @status";
        await using var command = CreateCommand(connection, null,
            LeaveAppSelect + $" WHERE a.company_id = @company AND a.{NotDeleted}{statusFilter} ORDER BY a.from_date DESC, e.employee_code");
        Add(command, "@company", companyId.Trim());
        if (!string.IsNullOrWhiteSpace(status)) Add(command, "@status", status.Trim());
        var result = new List<HrLeaveApplication>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadLeaveApplication(reader));
        return result;
    }

    private async Task<HrLeaveApplication?> LoadLeaveApplicationAsync(DbConnection connection, DbTransaction? transaction, string companyId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            LeaveAppSelect + $" WHERE a.company_id = @company AND a.id = @id AND a.{NotDeleted}");
        Add(command, "@company", companyId.Trim());
        Add(command, "@id", FormatGuid(applicationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLeaveApplication(reader) : null;
    }

    private static HrLeaveApplication ReadLeaveApplication(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0), EmployeeId = ReadGuid(reader, 1), EmployeeCode = ReadString(reader, 2),
        EmployeeName = string.Join(" ", new[] { ReadString(reader, 3), ReadString(reader, 4), ReadString(reader, 5) }.Where(p => !string.IsNullOrWhiteSpace(p))),
        LeaveTypeId = ReadGuid(reader, 6), LeaveTypeCode = ReadString(reader, 7), LeaveTypeName = ReadString(reader, 8),
        LeaveTypeIsPaid = ReadBool(reader, 9), FromDate = ReadString(reader, 10) ?? string.Empty,
        ToDate = ReadString(reader, 11) ?? string.Empty, TotalDays = ReadDecimal(reader, 12),
        HalfDay = ReadBool(reader, 13), Reason = ReadString(reader, 14),
        Status = ReadString(reader, 15) ?? HrLeaveStatuses.Open, DecidedBy = ReadString(reader, 16),
        DecidedAt = ReadString(reader, 17), DecisionNotes = ReadString(reader, 18)
    };

    public async Task SaveLeaveApplicationAsync(string companyId, HrLeaveApplication application, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(application);
        application.TotalDays = HrLeaveApplication.ComputeDays(application.FromDate, application.ToDate, application.HalfDay);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadLeaveApplicationAsync(connection, transaction, companyId, application.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.Status != HrLeaveStatuses.Open)
                throw new AppError(ErrorCodes.HrLeaveStateInvalid,
                    $"A leave application in status '{existing.Status}' can no longer be edited.", existing.Status);
            await UpsertAsync(connection, transaction, "hr_leave_applications", application.Id, companyId, userId,
                new[]
                {
                    ("employee_id", FormatGuid(application.EmployeeId)),
                    ("leave_type_id", FormatGuid(application.LeaveTypeId)),
                    ("from_date", (object)application.FromDate.Trim()), ("to_date", (object)application.ToDate.Trim()),
                    ("total_days", (object)application.TotalDays), ("half_day", Bool(application.HalfDay)),
                    ("reason", (object?)NullIfBlank(application.Reason)),
                    ("status", (object)HrLeaveStatuses.Open)
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task ApproveLeaveAsync(string companyId, Guid applicationId, bool allowOverride, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = await LoadLeaveApplicationAsync(connection, transaction, companyId, applicationId, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrLeaveStateInvalid, "Leave application was not found for this company.");
            if (app.Status != HrLeaveStatuses.Open)
                throw new AppError(ErrorCodes.HrLeaveStateInvalid,
                    $"Only an Open leave application can be approved (current status: '{app.Status}').", app.Status);

            var year = DateOnly.Parse(app.FromDate, CultureInfo.InvariantCulture).Year;
            var balance = await ScalarDecimalAsync(connection, transaction,
                "SELECT COALESCE(allocated_days - used_days, 0) FROM hr_leave_balances WHERE company_id = @company AND employee_id = @emp AND leave_type_id = @type AND year = @year",
                ("@company", companyId.Trim()), ("@emp", FormatGuid(app.EmployeeId)),
                ("@type", FormatGuid(app.LeaveTypeId)), ("@year", year)).ConfigureAwait(false);
            var hasRow = await CountAsync(connection, transaction,
                "SELECT COUNT(*) FROM hr_leave_balances WHERE company_id = @company AND employee_id = @emp AND leave_type_id = @type AND year = @year",
                ("@company", companyId.Trim()), ("@emp", FormatGuid(app.EmployeeId)),
                ("@type", FormatGuid(app.LeaveTypeId)), ("@year", year)).ConfigureAwait(false) > 0;
            if (!hasRow) balance = 0m;
            if (app.TotalDays > balance && !allowOverride)
                throw new AppError(ErrorCodes.HrLeaveBalanceInsufficient,
                    $"Leave balance is insufficient: {app.TotalDays} day(s) requested against a balance of {balance} day(s). Approving beyond the balance requires the HR.LEAVE.OVERRIDE permission.", app.EmployeeCode ?? string.Empty);

            // Deduct (creating the balance row at the type's annual
            // entitlement when none exists yet, so a first approval is
            // not blocked by a missing allocation — the balance check
            // above already used 0 in that case, so reaching here with
            // no row means an override approval; the row then starts
            // from the type's annual days for honest reporting).
            if (!hasRow)
            {
                var annual = await ScalarDecimalAsync(connection, transaction,
                    "SELECT annual_days FROM hr_leave_types WHERE company_id = @company AND id = @type",
                    ("@company", companyId.Trim()), ("@type", FormatGuid(app.LeaveTypeId))).ConfigureAwait(false);
                await using var create = CreateCommand(connection, transaction, $"""
                    INSERT INTO hr_leave_balances (id, company_id, employee_id, leave_type_id, year, allocated_days, used_days, created_by)
                    VALUES (@id, @company, @emp, @type, @year, @alloc, 0, @actor)
                    """);
                Add(create, "@id", FormatGuid(Guid.NewGuid()));
                Add(create, "@company", companyId.Trim());
                Add(create, "@emp", FormatGuid(app.EmployeeId));
                Add(create, "@type", FormatGuid(app.LeaveTypeId));
                Add(create, "@year", year);
                Add(create, "@alloc", annual);
                Add(create, "@actor", userId.Trim());
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await ExecAsync(connection, transaction,
                $"UPDATE hr_leave_balances SET used_days = used_days + @days, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND employee_id = @emp AND leave_type_id = @type AND year = @year",
                ("@days", (object)app.TotalDays), ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()),
                ("@emp", FormatGuid(app.EmployeeId)), ("@type", FormatGuid(app.LeaveTypeId)), ("@year", (object)year)).ConfigureAwait(false);

            // Attendance: one Leave row per application day (a half-day
            // application writes a HalfDay row), owned by the application.
            var from = DateOnly.Parse(app.FromDate, CultureInfo.InvariantCulture);
            var to = DateOnly.Parse(app.ToDate, CultureInfo.InvariantCulture);
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                await using var upsert = CreateCommand(connection, transaction, $"""
                    INSERT INTO hr_attendance (id, company_id, employee_id, attendance_date, status, leave_application_id, created_by)
                    VALUES (@id, @company, @emp, @date, @status, @app, @actor)
                    ON CONFLICT(company_id, employee_id, attendance_date) DO UPDATE SET
                        status = excluded.status, leave_application_id = excluded.leave_application_id
                    """);
                Add(upsert, "@id", FormatGuid(Guid.NewGuid()));
                Add(upsert, "@company", companyId.Trim());
                Add(upsert, "@emp", FormatGuid(app.EmployeeId));
                Add(upsert, "@date", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Add(upsert, "@status", app.HalfDay && from == to ? HrAttendanceStatuses.HalfDay : HrAttendanceStatuses.Leave);
                Add(upsert, "@app", FormatGuid(app.Id));
                Add(upsert, "@actor", userId.Trim());
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ExecAsync(connection, transaction,
                $"UPDATE hr_leave_applications SET status = @status, decided_by = @actor, decided_at = {NowExpr}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@status", (object)HrLeaveStatuses.Approved), ("@actor", (object)userId.Trim()),
                ("@company", companyId.Trim()), ("@id", FormatGuid(app.Id))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task RejectLeaveAsync(string companyId, Guid applicationId, string? notes, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = await LoadLeaveApplicationAsync(connection, transaction, companyId, applicationId, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrLeaveStateInvalid, "Leave application was not found for this company.");
            if (app.Status != HrLeaveStatuses.Open)
                throw new AppError(ErrorCodes.HrLeaveStateInvalid,
                    $"Only an Open leave application can be rejected (current status: '{app.Status}').", app.Status);
            await ExecAsync(connection, transaction,
                $"UPDATE hr_leave_applications SET status = @status, decided_by = @actor, decided_at = {NowExpr}, decision_notes = @notes, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@status", (object)HrLeaveStatuses.Rejected), ("@actor", (object)userId.Trim()),
                ("@notes", (object?)NullIfBlank(notes) ?? DBNull.Value), ("@company", companyId.Trim()),
                ("@id", FormatGuid(app.Id))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task CancelLeaveAsync(string companyId, Guid applicationId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = await LoadLeaveApplicationAsync(connection, transaction, companyId, applicationId, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrLeaveStateInvalid, "Leave application was not found for this company.");
            if (app.Status is not (HrLeaveStatuses.Open or HrLeaveStatuses.Approved))
                throw new AppError(ErrorCodes.HrLeaveStateInvalid,
                    $"A leave application in status '{app.Status}' cannot be cancelled.", app.Status);
            if (app.Status == HrLeaveStatuses.Approved)
            {
                var year = DateOnly.Parse(app.FromDate, CultureInfo.InvariantCulture).Year;
                await ExecAsync(connection, transaction,
                    $"UPDATE hr_leave_balances SET used_days = used_days - @days, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND employee_id = @emp AND leave_type_id = @type AND year = @year",
                    ("@days", (object)app.TotalDays), ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()),
                    ("@emp", FormatGuid(app.EmployeeId)), ("@type", FormatGuid(app.LeaveTypeId)), ("@year", (object)year)).ConfigureAwait(false);
                await ExecAsync(connection, transaction,
                    "DELETE FROM hr_attendance WHERE company_id = @company AND leave_application_id = @app",
                    ("@company", companyId.Trim()), ("@app", FormatGuid(app.Id))).ConfigureAwait(false);
            }
            await ExecAsync(connection, transaction,
                $"UPDATE hr_leave_applications SET status = @status, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@status", (object)HrLeaveStatuses.Cancelled), ("@actor", (object)userId.Trim()),
                ("@company", companyId.Trim()), ("@id", FormatGuid(app.Id))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    // ==================================================================
    // Salary components / structures / assignments
    // ==================================================================

    public async Task<IReadOnlyList<HrSalaryComponent>> GetComponentsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureComponentsAsync(connection, null, companyId, "system", cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, kind, default_calc_type, default_amount, default_percent, is_active FROM hr_salary_components WHERE company_id = @company AND {NotDeleted} ORDER BY kind, code");
        Add(command, "@company", companyId.Trim());
        var result = new List<HrSalaryComponent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrSalaryComponent
            {
                Id = ReadGuid(reader, 0), Code = ReadString(reader, 1) ?? string.Empty,
                NameEn = ReadString(reader, 2) ?? string.Empty, NameAr = ReadString(reader, 3) ?? string.Empty,
                Kind = ReadString(reader, 4) ?? HrComponentKinds.Earning,
                DefaultCalcType = ReadString(reader, 5) ?? HrCalcTypes.Fixed,
                DefaultAmount = ReadDecimal(reader, 6), DefaultPercent = ReadDecimal(reader, 7),
                IsActive = ReadBool(reader, 8)
            });
        return result;
    }

    /// <summary>Seeds the neutral starter catalog on first use — all
    /// rates ZERO in data; the company sets its own percentages
    /// (social-insurance-style deductions are configuration, not law).</summary>
    private async Task EnsureComponentsAsync(DbConnection connection, DbTransaction? outerTransaction, string companyId, string userId, CancellationToken cancellationToken)
    {
        if (await CountAsync(connection, outerTransaction,
                $"SELECT COUNT(*) FROM hr_salary_components WHERE company_id = @company AND {NotDeleted}",
                ("@company", companyId.Trim())).ConfigureAwait(false) > 0)
            return;
        var ownTransaction = outerTransaction is null;
        var transaction = outerTransaction ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (code, name, kind, calc) in new[]
            {
                ("HOUSING", "Housing Allowance", HrComponentKinds.Earning, HrCalcTypes.PercentOfBase),
                ("TRANSPORT", "Transport Allowance", HrComponentKinds.Earning, HrCalcTypes.Fixed),
                ("SOCIAL", "Social Insurance (Employee)", HrComponentKinds.Deduction, HrCalcTypes.PercentOfBase),
                ("TAX", "Income Tax", HrComponentKinds.Deduction, HrCalcTypes.PercentOfGross),
                ("SOCIAL_ER", "Social Insurance (Employer)", HrComponentKinds.Employer, HrCalcTypes.PercentOfBase)
            })
            {
                await using var insert = CreateCommand(connection, transaction, $"""
                    INSERT INTO hr_salary_components (id, company_id, code, name_en, name_ar, kind, default_calc_type, default_amount, default_percent, is_active, is_deleted, created_by)
                    VALUES (@id, @company, @code, @name, @name, @kind, @calc, 0, 0, {BoolTrue}, {BoolFalse}, @actor)
                    """);
                Add(insert, "@id", FormatGuid(Guid.NewGuid()));
                Add(insert, "@company", companyId.Trim());
                Add(insert, "@code", code);
                Add(insert, "@name", name);
                Add(insert, "@kind", kind);
                Add(insert, "@calc", calc);
                Add(insert, "@actor", userId);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (ownTransaction) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (ownTransaction) await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
        finally { if (ownTransaction) await transaction.DisposeAsync().ConfigureAwait(false); }
    }

    public async Task SaveComponentAsync(string companyId, HrSalaryComponent component, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(component);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_salary_components WHERE company_id = @company AND code = @code AND id <> @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@code", component.Code.Trim()), ("@id", FormatGuid(component.Id))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterCodeDuplicate,
                    $"Salary component code '{component.Code.Trim()}' already exists for this company.", component.Code.Trim());
            await UpsertAsync(connection, transaction, "hr_salary_components", component.Id, companyId, userId,
                new[]
                {
                    ("code", (object?)component.Code.Trim()), ("name_en", (object)component.NameEn.Trim()),
                    ("name_ar", (object)component.NameAr.Trim()), ("kind", (object)component.Kind.Trim()),
                    ("default_calc_type", (object)component.DefaultCalcType.Trim()),
                    ("default_amount", (object)component.DefaultAmount), ("default_percent", (object)component.DefaultPercent),
                    ("is_active", Bool(component.IsActive))
                }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeleteComponentAsync(string companyId, Guid componentId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SoftDeleteAsync(connection, transaction, "hr_salary_components", companyId, componentId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<HrSalaryStructure>> GetStructuresAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            $"SELECT id, code, name_en, name_ar, applies_to, is_active FROM hr_salary_structures WHERE company_id = @company AND {NotDeleted} ORDER BY code");
        Add(command, "@company", companyId.Trim());
        var result = new List<HrSalaryStructure>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new HrSalaryStructure
                {
                    Id = ReadGuid(reader, 0), Code = ReadString(reader, 1) ?? string.Empty,
                    NameEn = ReadString(reader, 2) ?? string.Empty, NameAr = ReadString(reader, 3) ?? string.Empty,
                    AppliesTo = ReadString(reader, 4) ?? "Employee", IsActive = ReadBool(reader, 5)
                });
        }
        foreach (var structure in result)
            structure.Lines = await LoadStructureLinesAsync(connection, null, structure.Id, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<List<HrStructureLine>> LoadStructureLinesAsync(DbConnection connection, DbTransaction? transaction, Guid structureId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT l.id, l.component_id, c.code, c.name_en, c.kind, l.line_no, l.calc_type, l.amount, l.percent, l.account_code, l.is_active
            FROM hr_salary_structure_lines l
            JOIN hr_salary_components c ON c.id = l.component_id
            WHERE l.structure_id = @structure ORDER BY l.line_no
            """);
        Add(command, "@structure", FormatGuid(structureId));
        var result = new List<HrStructureLine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrStructureLine
            {
                Id = ReadGuid(reader, 0), ComponentId = ReadGuid(reader, 1),
                ComponentCode = ReadString(reader, 2), ComponentName = ReadString(reader, 3),
                Kind = ReadString(reader, 4) ?? HrComponentKinds.Earning,
                LineNo = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                CalcType = ReadString(reader, 6) ?? HrCalcTypes.Fixed,
                Amount = ReadDecimal(reader, 7), Percent = ReadDecimal(reader, 8),
                AccountCode = ReadString(reader, 9), IsActive = ReadBool(reader, 10)
            });
        return result;
    }

    public async Task SaveStructureAsync(string companyId, HrSalaryStructure structure, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(structure);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_salary_structures WHERE company_id = @company AND code = @code AND id <> @id AND {NotDeleted}",
                    ("@company", companyId.Trim()), ("@code", structure.Code.Trim()), ("@id", FormatGuid(structure.Id))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterCodeDuplicate,
                    $"Salary structure code '{structure.Code.Trim()}' already exists for this company.", structure.Code.Trim());
            await UpsertAsync(connection, transaction, "hr_salary_structures", structure.Id, companyId, userId,
                new[]
                {
                    ("code", (object?)structure.Code.Trim()), ("name_en", (object)structure.NameEn.Trim()),
                    ("name_ar", (object)structure.NameAr.Trim()), ("applies_to", (object)structure.AppliesTo.Trim()),
                    ("is_active", Bool(structure.IsActive))
                }, cancellationToken).ConfigureAwait(false);
            await ExecAsync(connection, transaction,
                "DELETE FROM hr_salary_structure_lines WHERE structure_id = @structure",
                ("@structure", FormatGuid(structure.Id))).ConfigureAwait(false);
            var lineNo = 0;
            foreach (var line in structure.Lines)
            {
                line.LineNo = ++lineNo;
                if (line.Id == Guid.Empty) line.Id = Guid.NewGuid();
                await using var insert = CreateCommand(connection, transaction, $"""
                    INSERT INTO hr_salary_structure_lines (id, structure_id, component_id, line_no, calc_type, amount, percent, account_code, is_active)
                    VALUES (@id, @structure, @component, @no, @calc, @amount, @percent, @account, @active)
                    """);
                Add(insert, "@id", FormatGuid(line.Id));
                Add(insert, "@structure", FormatGuid(structure.Id));
                Add(insert, "@component", FormatGuid(line.ComponentId));
                Add(insert, "@no", line.LineNo);
                Add(insert, "@calc", line.CalcType.Trim());
                Add(insert, "@amount", line.Amount);
                Add(insert, "@percent", line.Percent);
                Add(insert, "@account", (object?)NullIfBlank(line.AccountCode) ?? DBNull.Value);
                Add(insert, "@active", Bool(line.IsActive));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task SoftDeleteStructureAsync(string companyId, Guid structureId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await CountAsync(connection, transaction,
                    $"SELECT COUNT(*) FROM hr_employee_salary WHERE company_id = @company AND structure_id = @id AND is_active = {BoolTrue}",
                    ("@company", companyId.Trim()), ("@id", FormatGuid(structureId))).ConfigureAwait(false) > 0)
                throw new AppError(ErrorCodes.HrMasterInUse,
                    "This salary structure still has active assignments and cannot be deleted.");
            await SoftDeleteAsync(connection, transaction, "hr_salary_structures", companyId, structureId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<IReadOnlyList<HrSalaryAssignment>> GetAssignmentsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT a.id, a.employee_id, a.designation_id, a.structure_id, s.code, a.base_salary, a.currency_code, a.from_date, a.to_date, a.is_active
            FROM hr_employee_salary a
            JOIN hr_salary_structures s ON s.id = a.structure_id
            WHERE a.company_id = @company ORDER BY a.from_date DESC
            """);
        Add(command, "@company", companyId.Trim());
        var result = new List<HrSalaryAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrSalaryAssignment
            {
                Id = ReadGuid(reader, 0), EmployeeId = ReadGuidOrNull(reader, 1),
                DesignationId = ReadGuidOrNull(reader, 2), StructureId = ReadGuid(reader, 3),
                StructureCode = ReadString(reader, 4), BaseSalary = ReadDecimal(reader, 5),
                CurrencyCode = ReadString(reader, 6) ?? "SAR", FromDate = ReadString(reader, 7) ?? string.Empty,
                ToDate = ReadString(reader, 8), IsActive = ReadBool(reader, 9)
            });
        return result;
    }

    public async Task SaveAssignmentAsync(string companyId, HrSalaryAssignment assignment, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(assignment);
        if ((assignment.EmployeeId is null) == (assignment.DesignationId is null))
            throw new AppError(ErrorCodes.HrSalaryStructureInvalid,
                "A salary assignment targets exactly one employee or one designation.");
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (assignment.IsActive)
            {
                // One active assignment per target: deactivate the others.
                var targetColumn = assignment.EmployeeId is not null ? "employee_id" : "designation_id";
                var targetValue = FormatGuid((assignment.EmployeeId ?? assignment.DesignationId)!.Value);
                await ExecAsync(connection, transaction,
                    $"UPDATE hr_employee_salary SET is_active = {BoolFalse}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND {targetColumn} = @target AND id <> @id AND is_active = {BoolTrue}",
                    ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()),
                    ("@target", targetValue), ("@id", FormatGuid(assignment.Id))).ConfigureAwait(false);
            }
            await UpsertAsync(connection, transaction, "hr_employee_salary", assignment.Id, companyId, userId,
                new[]
                {
                    ("employee_id", (object?)NullGuid(assignment.EmployeeId)),
                    ("designation_id", (object?)NullGuid(assignment.DesignationId)),
                    ("structure_id", FormatGuid(assignment.StructureId)),
                    ("base_salary", (object)assignment.BaseSalary),
                    ("currency_code", (object)assignment.CurrencyCode.Trim().ToUpperInvariant()),
                    ("from_date", (object)assignment.FromDate.Trim()),
                    ("to_date", (object?)NullIfBlank(assignment.ToDate)),
                    ("is_active", Bool(assignment.IsActive))
                }, cancellationToken).ConfigureAwait(false);
            if (assignment.EmployeeId is Guid employeeId && assignment.IsActive)
            {
                // The card's base/currency is a convenience copy of the
                // live assignment (design section 3.5) — keep them in sync.
                await ExecAsync(connection, transaction,
                    $"UPDATE hr_employees SET base_salary = @base, currency_code = @currency, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @emp AND {NotDeleted}",
                    ("@base", (object)assignment.BaseSalary), ("@currency", (object)assignment.CurrencyCode.Trim().ToUpperInvariant()),
                    ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()), ("@emp", FormatGuid(employeeId))).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task DeactivateAssignmentAsync(string companyId, Guid assignmentId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecAsync(connection, transaction,
                $"UPDATE hr_employee_salary SET is_active = {BoolFalse}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()), ("@id", FormatGuid(assignmentId))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    // ==================================================================
    // Payroll runs
    // ==================================================================

    public async Task<HrPayrollRun> GenerateRunAsync(string companyId, int year, int month, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        var (from, to) = PeriodBounds(year, month);
        var runNo = $"PR-{year}-{month:D2}";
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Guid runId;
            var existingId = await ScalarGuidOrNullAsync(connection, transaction,
                $"SELECT id FROM hr_payroll_runs WHERE company_id = @company AND period_year = @year AND period_month = @month AND {NotDeleted}",
                ("@company", companyId.Trim()), ("@year", (object)year), ("@month", (object)month)).ConfigureAwait(false);
            if (existingId is Guid found)
            {
                var status = await ScalarStringAsync(connection, transaction,
                    "SELECT status FROM hr_payroll_runs WHERE id = @id", ("@id", FormatGuid(found))).ConfigureAwait(false);
                if (status == HrPayrollStatuses.Posted)
                    throw new AppError(ErrorCodes.HrPayrollAlreadyPosted,
                        $"Payroll run {runNo} is already posted. Reverse it before regenerating.", runNo);
                runId = found;
                await ExecAsync(connection, transaction,
                    "DELETE FROM hr_payslip_lines WHERE payslip_id IN (SELECT id FROM hr_payslips WHERE run_id = @run)",
                    ("@run", FormatGuid(runId))).ConfigureAwait(false);
                await ExecAsync(connection, transaction,
                    "DELETE FROM hr_payslips WHERE run_id = @run", ("@run", FormatGuid(runId))).ConfigureAwait(false);
            }
            else
            {
                runId = Guid.NewGuid();
                await using var insertRun = CreateCommand(connection, transaction, $"""
                    INSERT INTO hr_payroll_runs (id, company_id, run_no, period_year, period_month, period_from, period_to, status, currency_code, created_by)
                    VALUES (@id, @company, @no, @year, @month, @from, @to, 'Draft', 'SAR', @actor)
                    """);
                Add(insertRun, "@id", FormatGuid(runId));
                Add(insertRun, "@company", companyId.Trim());
                Add(insertRun, "@no", runNo);
                Add(insertRun, "@year", year);
                Add(insertRun, "@month", month);
                Add(insertRun, "@from", from);
                Add(insertRun, "@to", to);
                Add(insertRun, "@actor", userId.Trim());
                await insertRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Eligible employees: Active, hired by period end, not
            // terminated on/before period start (design section 4.7).
            var employees = new List<HrEmployee>();
            await using (var command = CreateCommand(connection, transaction,
                EmployeeSelect + $" WHERE e.company_id = @company AND e.{NotDeleted} AND e.status = 'Active' AND e.hire_date <= @to AND (e.termination_date IS NULL OR e.termination_date > @from) ORDER BY e.employee_code"))
            {
                Add(command, "@company", companyId.Trim());
                Add(command, "@to", to);
                Add(command, "@from", from);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    employees.Add(ReadEmployee(reader));
            }

            var assignments = new List<HrSalaryAssignment>();
            await using (var command = CreateCommand(connection, transaction,
                "SELECT id, employee_id, designation_id, structure_id, base_salary, currency_code, from_date, to_date FROM hr_employee_salary WHERE company_id = @company AND is_active = " + BoolTrue))
            {
                Add(command, "@company", companyId.Trim());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    assignments.Add(new HrSalaryAssignment
                    {
                        Id = ReadGuid(reader, 0), EmployeeId = ReadGuidOrNull(reader, 1),
                        DesignationId = ReadGuidOrNull(reader, 2), StructureId = ReadGuid(reader, 3),
                        BaseSalary = ReadDecimal(reader, 4), CurrencyCode = ReadString(reader, 5) ?? "SAR",
                        FromDate = ReadString(reader, 6) ?? string.Empty, ToDate = ReadString(reader, 7),
                        IsActive = true
                    });
            }

            var structures = new Dictionary<Guid, List<PayrollCalculator.CalcLine>>();
            await using (var command = CreateCommand(connection, transaction,
                $"SELECT id FROM hr_salary_structures WHERE company_id = @company AND {NotDeleted} AND is_active = {BoolTrue}"))
            {
                Add(command, "@company", companyId.Trim());
                var ids = new List<Guid>();
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        ids.Add(ReadGuid(reader, 0));
                foreach (var structureId in ids)
                {
                    var lines = await LoadStructureLinesAsync(connection, transaction, structureId, cancellationToken).ConfigureAwait(false);
                    structures[structureId] = lines.Where(l => l.IsActive).Select(l => new PayrollCalculator.CalcLine(
                        l.ComponentCode ?? string.Empty, l.ComponentName, l.Kind, l.CalcType,
                        l.Amount, l.Percent,
                        l.AccountCode ?? (l.Kind == HrComponentKinds.Deduction ? HrGlAccounts.SalariesPayable : HrGlAccounts.SalaryExpense))).ToList();
                }
            }

            HrSalaryAssignment? ResolveAssignment(HrEmployee employee)
            {
                bool Covers(HrSalaryAssignment a) =>
                    string.CompareOrdinal(a.FromDate, to) <= 0
                    && (a.ToDate is null || string.CompareOrdinal(a.ToDate, from) >= 0);
                return assignments.Where(a => a.EmployeeId == employee.Id && Covers(a))
                        .OrderByDescending(a => a.FromDate).FirstOrDefault()
                    ?? (employee.DesignationId is Guid designationId
                        ? assignments.Where(a => a.DesignationId == designationId && Covers(a))
                            .OrderByDescending(a => a.FromDate).FirstOrDefault()
                        : null);
            }

            var totalGross = 0m; var totalDeductions = 0m; var totalNet = 0m; var totalEmployer = 0m;
            string? runCurrency = null;
            foreach (var employee in employees)
            {
                var assignment = ResolveAssignment(employee);
                var baseSalary = assignment?.BaseSalary ?? employee.BaseSalary;
                runCurrency ??= assignment?.CurrencyCode ?? employee.CurrencyCode;
                var calcLines = assignment is not null && structures.TryGetValue(assignment.StructureId, out var foundLines)
                    ? foundLines : new List<PayrollCalculator.CalcLine>();
                var unpaid = await UnpaidLeaveDaysAsync(connection, transaction, companyId, employee.Id, from, to, cancellationToken).ConfigureAwait(false);
                var absent = await AbsentDaysAsync(connection, transaction, companyId, employee.Id, from, to, cancellationToken).ConfigureAwait(false);
                var calc = PayrollCalculator.Calculate(baseSalary, calcLines, unpaid, absent);

                var slip = new HrPayslip
                {
                    Id = Guid.NewGuid(), RunId = runId, EmployeeId = employee.Id,
                    EmployeeCode = employee.EmployeeCode, EmployeeName = employee.FullName,
                    DepartmentName = employee.DepartmentName, DesignationName = employee.DesignationName,
                    CostCenter = employee.CostCenter, BaseSalary = baseSalary,
                    PeriodDays = PayrollCalculator.PeriodDivisor, PaidDays = calc.PaidDays,
                    UnpaidLeaveDays = unpaid, AbsentDays = absent,
                    GrossPay = calc.GrossPay, TotalDeductions = calc.TotalDeductions,
                    NetPay = calc.NetPay, EmployerCost = calc.EmployerCost,
                    Status = HrPayrollStatuses.Draft, Lines = calc.Lines.ToList()
                };
                await InsertPayslipAsync(connection, transaction, companyId, slip, userId, cancellationToken).ConfigureAwait(false);
                totalGross += slip.GrossPay; totalDeductions += slip.TotalDeductions;
                totalNet += slip.NetPay; totalEmployer += slip.EmployerCost;
            }

            await ExecAsync(connection, transaction,
                $"UPDATE hr_payroll_runs SET total_gross = @gross, total_deductions = @ded, total_net = @net, total_employer = @er, employee_count = @count, currency_code = @currency, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@gross", (object)totalGross), ("@ded", (object)totalDeductions), ("@net", (object)totalNet),
                ("@er", (object)totalEmployer), ("@count", (object)employees.Count),
                ("@currency", (object)(runCurrency ?? "SAR")), ("@actor", (object)userId.Trim()),
                ("@company", companyId.Trim()), ("@id", FormatGuid(runId))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }

        return await GetRunAsync(companyId, await RunIdByPeriodAsync(companyId, year, month, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The generated payroll run could not be reloaded.");
    }

    private async Task<Guid> RunIdByPeriodAsync(string companyId, int year, int month, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarGuidOrNullAsync(connection, null,
            $"SELECT id FROM hr_payroll_runs WHERE company_id = @company AND period_year = @year AND period_month = @month AND {NotDeleted}",
            ("@company", companyId.Trim()), ("@year", (object)year), ("@month", (object)month)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Payroll run was not found after generation.");
    }

    /// <summary>Approved UNPAID leave overlapping the period, in days
    /// (an application's overlap is its calendar overlap; a half-day
    /// application contributes 0.5 when its day falls inside).</summary>
    private async Task<decimal> UnpaidLeaveDaysAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid employeeId, string from, string to, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT a.from_date, a.to_date, a.total_days, a.half_day
            FROM hr_leave_applications a
            JOIN hr_leave_types t ON t.id = a.leave_type_id
            WHERE a.company_id = @company AND a.employee_id = @emp AND a.status = 'Approved'
              AND t.is_paid = @unpaid AND a.from_date <= @to AND a.to_date >= @from
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@emp", FormatGuid(employeeId));
        Add(command, "@unpaid", Bool(false));
        Add(command, "@from", from);
        Add(command, "@to", to);
        var periodFrom = DateOnly.Parse(from, CultureInfo.InvariantCulture);
        var periodTo = DateOnly.Parse(to, CultureInfo.InvariantCulture);
        var total = 0m;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var appFrom = DateOnly.Parse(ReadString(reader, 0)!, CultureInfo.InvariantCulture);
            var appTo = DateOnly.Parse(ReadString(reader, 1)!, CultureInfo.InvariantCulture);
            var half = ReadBool(reader, 3);
            if (half && appFrom == appTo)
            {
                if (appFrom >= periodFrom && appFrom <= periodTo) total += 0.5m;
                continue;
            }
            var overlapFrom = appFrom > periodFrom ? appFrom : periodFrom;
            var overlapTo = appTo < periodTo ? appTo : periodTo;
            if (overlapTo >= overlapFrom) total += overlapTo.DayNumber - overlapFrom.DayNumber + 1;
        }
        return total;
    }

    /// <summary>Absence weight in the period: Absent = 1, HalfDay = 0.5.
    /// Rows owned by a leave application never count (they are leave).</summary>
    private async Task<decimal> AbsentDaysAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid employeeId, string from, string to, CancellationToken cancellationToken)
    {
        var absent = await ScalarDecimalAsync(connection, transaction,
            "SELECT COUNT(*) FROM hr_attendance WHERE company_id = @company AND employee_id = @emp AND attendance_date >= @from AND attendance_date <= @to AND status = 'Absent'",
            ("@company", companyId.Trim()), ("@emp", FormatGuid(employeeId)), ("@from", from), ("@to", to)).ConfigureAwait(false);
        var half = await ScalarDecimalAsync(connection, transaction,
            "SELECT COUNT(*) FROM hr_attendance WHERE company_id = @company AND employee_id = @emp AND attendance_date >= @from AND attendance_date <= @to AND status = 'HalfDay' AND leave_application_id IS NULL",
            ("@company", companyId.Trim()), ("@emp", FormatGuid(employeeId)), ("@from", from), ("@to", to)).ConfigureAwait(false);
        return absent + half * 0.5m;
    }

    private async Task InsertPayslipAsync(DbConnection connection, DbTransaction transaction, string companyId, HrPayslip slip, string userId, CancellationToken cancellationToken)
    {
        await using (var insert = CreateCommand(connection, transaction, """
            INSERT INTO hr_payslips (id, company_id, run_id, employee_id, employee_code, employee_name, department_name, designation_name, base_salary, period_days, paid_days, unpaid_leave_days, absent_days, gross_pay, total_deductions, net_pay, employer_cost, status, notes, created_by)
            VALUES (@id, @company, @run, @emp, @code, @name, @dept, @desig, @base, @pdays, @paid, @unpaid, @absent, @gross, @ded, @net, @er, @status, @notes, @actor)
            """))
        {
            Add(insert, "@id", FormatGuid(slip.Id));
            Add(insert, "@company", companyId.Trim());
            Add(insert, "@run", FormatGuid(slip.RunId));
            Add(insert, "@emp", FormatGuid(slip.EmployeeId));
            Add(insert, "@code", slip.EmployeeCode);
            Add(insert, "@name", slip.EmployeeName);
            Add(insert, "@dept", (object?)slip.DepartmentName ?? DBNull.Value);
            Add(insert, "@desig", (object?)slip.DesignationName ?? DBNull.Value);
            Add(insert, "@base", slip.BaseSalary);
            Add(insert, "@pdays", slip.PeriodDays);
            Add(insert, "@paid", slip.PaidDays);
            Add(insert, "@unpaid", slip.UnpaidLeaveDays);
            Add(insert, "@absent", slip.AbsentDays);
            Add(insert, "@gross", slip.GrossPay);
            Add(insert, "@ded", slip.TotalDeductions);
            Add(insert, "@net", slip.NetPay);
            Add(insert, "@er", slip.EmployerCost);
            Add(insert, "@status", slip.Status);
            Add(insert, "@notes", (object?)NullIfBlank(slip.Notes) ?? DBNull.Value);
            Add(insert, "@actor", userId.Trim());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var line in slip.Lines)
            await InsertPayslipLineAsync(connection, transaction, slip.Id, line, cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertPayslipLineAsync(DbConnection connection, DbTransaction transaction, Guid payslipId, HrPayslipLine line, CancellationToken cancellationToken)
    {
        if (line.Id == Guid.Empty) line.Id = Guid.NewGuid();
        await using var insert = CreateCommand(connection, transaction, """
            INSERT INTO hr_payslip_lines (id, payslip_id, line_no, component_code, component_name, kind, calc_type, amount, is_system, account_code, notes)
            VALUES (@id, @slip, @no, @code, @name, @kind, @calc, @amount, @system, @account, @notes)
            """);
        Add(insert, "@id", FormatGuid(line.Id));
        Add(insert, "@slip", FormatGuid(payslipId));
        Add(insert, "@no", line.LineNo);
        Add(insert, "@code", line.ComponentCode);
        Add(insert, "@name", (object?)line.ComponentName ?? DBNull.Value);
        Add(insert, "@kind", line.Kind);
        Add(insert, "@calc", line.CalcType);
        Add(insert, "@amount", line.Amount);
        Add(insert, "@system", Bool(line.IsSystem));
        Add(insert, "@account", (object?)NullIfBlank(line.AccountCode) ?? DBNull.Value);
        Add(insert, "@notes", (object?)NullIfBlank(line.Notes) ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string RunSelect = """
        SELECT id, run_no, period_year, period_month, period_from, period_to, status,
               total_gross, total_deductions, total_net, total_employer, employee_count,
               currency_code, journal_entry_id, journal_doc_no, reversed_journal_doc_no,
               notes, posted_by, posted_at
        FROM hr_payroll_runs
        """;

    private static HrPayrollRun ReadRun(DbDataReader reader) => new()
    {
        Id = ReadGuid(reader, 0), RunNo = ReadString(reader, 1) ?? string.Empty,
        PeriodYear = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
        PeriodMonth = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
        PeriodFrom = ReadString(reader, 4) ?? string.Empty, PeriodTo = ReadString(reader, 5) ?? string.Empty,
        Status = ReadString(reader, 6) ?? HrPayrollStatuses.Draft,
        TotalGross = ReadDecimal(reader, 7), TotalDeductions = ReadDecimal(reader, 8),
        TotalNet = ReadDecimal(reader, 9), TotalEmployer = ReadDecimal(reader, 10),
        EmployeeCount = Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture),
        CurrencyCode = ReadString(reader, 12) ?? "SAR",
        JournalEntryId = ReadGuidOrNull(reader, 13), JournalDocNo = ReadString(reader, 14),
        ReversedJournalDocNo = ReadString(reader, 15), Notes = ReadString(reader, 16),
        PostedBy = ReadString(reader, 17), PostedAt = ReadString(reader, 18)
    };

    public async Task<IReadOnlyList<HrPayrollRun>> GetRunsAsync(string companyId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null,
            RunSelect + $" WHERE company_id = @company AND {NotDeleted} ORDER BY period_year DESC, period_month DESC");
        Add(command, "@company", companyId.Trim());
        var result = new List<HrPayrollRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadRun(reader));
        return result;
    }

    public async Task<HrPayrollRun?> GetRunAsync(string companyId, Guid runId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadRunAsync(connection, null, companyId, runId, withPayslips: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HrPayrollRun?> LoadRunAsync(DbConnection connection, DbTransaction? transaction, string companyId, Guid runId, bool withPayslips, CancellationToken cancellationToken)
    {
        HrPayrollRun? run;
        await using (var command = CreateCommand(connection, transaction,
            RunSelect + $" WHERE company_id = @company AND id = @id AND {NotDeleted}"))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@id", FormatGuid(runId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            run = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
        }
        if (run is null || !withPayslips) return run;

        await using (var command = CreateCommand(connection, transaction, """
            SELECT p.id, p.employee_id, p.employee_code, p.employee_name, p.department_name, p.designation_name,
                   e.cost_center, p.base_salary, p.period_days, p.paid_days, p.unpaid_leave_days, p.absent_days,
                   p.gross_pay, p.total_deductions, p.net_pay, p.employer_cost, p.status, p.notes
            FROM hr_payslips p JOIN hr_employees e ON e.id = p.employee_id
            WHERE p.company_id = @company AND p.run_id = @run ORDER BY p.employee_code
            """))
        {
            Add(command, "@company", companyId.Trim());
            Add(command, "@run", FormatGuid(runId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                run.Payslips.Add(new HrPayslip
                {
                    Id = ReadGuid(reader, 0), RunId = runId, EmployeeId = ReadGuid(reader, 1),
                    EmployeeCode = ReadString(reader, 2) ?? string.Empty, EmployeeName = ReadString(reader, 3) ?? string.Empty,
                    DepartmentName = ReadString(reader, 4), DesignationName = ReadString(reader, 5),
                    CostCenter = ReadString(reader, 6), BaseSalary = ReadDecimal(reader, 7),
                    PeriodDays = ReadDecimal(reader, 8), PaidDays = ReadDecimal(reader, 9),
                    UnpaidLeaveDays = ReadDecimal(reader, 10), AbsentDays = ReadDecimal(reader, 11),
                    GrossPay = ReadDecimal(reader, 12), TotalDeductions = ReadDecimal(reader, 13),
                    NetPay = ReadDecimal(reader, 14), EmployerCost = ReadDecimal(reader, 15),
                    Status = ReadString(reader, 16) ?? HrPayrollStatuses.Draft, Notes = ReadString(reader, 17)
                });
        }
        foreach (var slip in run.Payslips)
        {
            await using var command = CreateCommand(connection, transaction,
                "SELECT id, line_no, component_code, component_name, kind, calc_type, amount, is_system, account_code, notes FROM hr_payslip_lines WHERE payslip_id = @slip ORDER BY line_no");
            Add(command, "@slip", FormatGuid(slip.Id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                slip.Lines.Add(new HrPayslipLine
                {
                    Id = ReadGuid(reader, 0), LineNo = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    ComponentCode = ReadString(reader, 2) ?? string.Empty, ComponentName = ReadString(reader, 3),
                    Kind = ReadString(reader, 4) ?? HrComponentKinds.Earning, CalcType = ReadString(reader, 5) ?? HrCalcTypes.Fixed,
                    Amount = ReadDecimal(reader, 6), IsSystem = ReadBool(reader, 7),
                    AccountCode = ReadString(reader, 8), Notes = ReadString(reader, 9)
                });
        }
        return run;
    }

    public async Task UpdatePayslipLinesAsync(string companyId, Guid payslipId, IReadOnlyList<HrPayslipLine> lines, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        ArgumentNullException.ThrowIfNull(lines);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runId = await ScalarGuidOrNullAsync(connection, transaction,
                "SELECT run_id FROM hr_payslips WHERE company_id = @company AND id = @id",
                ("@company", companyId.Trim()), ("@id", FormatGuid(payslipId))).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrPayrollStateInvalid, "Payslip was not found for this company.");
            var run = await LoadRunAsync(connection, transaction, companyId, runId, withPayslips: true, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrPayrollStateInvalid, "The payslip's payroll run was not found.");
            if (run.Status != HrPayrollStatuses.Draft)
                throw new AppError(ErrorCodes.HrPayrollAlreadyPosted,
                    $"Payroll run {run.RunNo} is posted; its payslips are final and cannot be edited.", run.RunNo);
            var slip = run.Payslips.Single(p => p.Id == payslipId);
            foreach (var systemLine in slip.Lines.Where(l => l.IsSystem))
            {
                var incoming = lines.FirstOrDefault(l => l.ComponentCode == systemLine.ComponentCode && l.IsSystem);
                if (incoming is null || incoming.Amount != systemLine.Amount)
                    throw new AppError(ErrorCodes.HrPayrollStateInvalid,
                        $"System line '{systemLine.ComponentCode}' is computed from attendance/leave and cannot be edited on the payslip; fix the attendance or leave record and regenerate.", systemLine.ComponentCode);
            }

            await ExecAsync(connection, transaction,
                "DELETE FROM hr_payslip_lines WHERE payslip_id = @slip", ("@slip", FormatGuid(payslipId))).ConfigureAwait(false);
            var lineNo = 0;
            foreach (var line in lines)
            {
                line.LineNo = ++lineNo;
                line.Id = Guid.NewGuid();
                await InsertPayslipLineAsync(connection, transaction, payslipId, line, cancellationToken).ConfigureAwait(false);
            }
            var gross = lines.Where(l => l.Kind == HrComponentKinds.Earning).Sum(l => l.Amount);
            var deductions = lines.Where(l => l.Kind == HrComponentKinds.Deduction).Sum(l => l.Amount);
            var employer = lines.Where(l => l.Kind == HrComponentKinds.Employer).Sum(l => l.Amount);
            await ExecAsync(connection, transaction,
                $"UPDATE hr_payslips SET gross_pay = @gross, total_deductions = @ded, net_pay = @net, employer_cost = @er, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@gross", (object)gross), ("@ded", (object)deductions), ("@net", (object)(gross - deductions)),
                ("@er", (object)employer), ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()),
                ("@id", FormatGuid(payslipId))).ConfigureAwait(false);
            await RefreshRunTotalsAsync(connection, transaction, companyId, runId, userId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    private async Task RefreshRunTotalsAsync(DbConnection connection, DbTransaction transaction, string companyId, Guid runId, string userId, CancellationToken cancellationToken)
    {
        var gross = await ScalarDecimalAsync(connection, transaction,
            "SELECT COALESCE(SUM(gross_pay), 0) FROM hr_payslips WHERE run_id = @run", ("@run", FormatGuid(runId))).ConfigureAwait(false);
        var ded = await ScalarDecimalAsync(connection, transaction,
            "SELECT COALESCE(SUM(total_deductions), 0) FROM hr_payslips WHERE run_id = @run", ("@run", FormatGuid(runId))).ConfigureAwait(false);
        var net = await ScalarDecimalAsync(connection, transaction,
            "SELECT COALESCE(SUM(net_pay), 0) FROM hr_payslips WHERE run_id = @run", ("@run", FormatGuid(runId))).ConfigureAwait(false);
        var er = await ScalarDecimalAsync(connection, transaction,
            "SELECT COALESCE(SUM(employer_cost), 0) FROM hr_payslips WHERE run_id = @run", ("@run", FormatGuid(runId))).ConfigureAwait(false);
        var count = await CountAsync(connection, transaction,
            "SELECT COUNT(*) FROM hr_payslips WHERE run_id = @run", ("@run", FormatGuid(runId))).ConfigureAwait(false);
        await ExecAsync(connection, transaction,
            $"UPDATE hr_payroll_runs SET total_gross = @gross, total_deductions = @ded, total_net = @net, total_employer = @er, employee_count = @count, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
            ("@gross", (object)gross), ("@ded", (object)ded), ("@net", (object)net), ("@er", (object)er),
            ("@count", (object)count), ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()),
            ("@id", FormatGuid(runId))).ConfigureAwait(false);
    }

    public async Task<string> PostRunAsync(string companyId, Guid runId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await LoadRunAsync(connection, transaction, companyId, runId, withPayslips: true, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrPayrollStateInvalid, "Payroll run was not found for this company.");
            if (run.Status == HrPayrollStatuses.Posted)
                throw new AppError(ErrorCodes.HrPayrollAlreadyPosted, $"Payroll run {run.RunNo} is already posted.", run.RunNo);
            if (run.Payslips.Count == 0)
                throw new AppError(ErrorCodes.HrPayrollStateInvalid, $"Payroll run {run.RunNo} has no payslips to post.", run.RunNo);
            if (run.TotalGross + run.TotalEmployer <= 0)
                throw new AppError(ErrorCodes.HrPayrollStateInvalid, $"Payroll run {run.RunNo} totals zero and cannot be posted.", run.RunNo);

            // GL lines per the design section 5 mapping: Dr earning /
            // employer expense accounts (employee cost center when set),
            // Cr deduction payables per component mapping, Cr net pay to
            // Salaries Payable. A negative net (deductions above gross)
            // becomes a Dr to Salaries Payable (amount owed back), which
            // keeps the entry balanced and explicit.
            var glLines = new List<GlLineRequest>();
            foreach (var slip in run.Payslips)
            {
                foreach (var line in slip.Lines)
                {
                    if (line.Amount == 0) continue;
                    var account = string.IsNullOrWhiteSpace(line.AccountCode)
                        ? (line.Kind == HrComponentKinds.Deduction ? HrGlAccounts.SalariesPayable : HrGlAccounts.SalaryExpense)
                        : line.AccountCode.Trim();
                    if (line.Kind == HrComponentKinds.Deduction)
                        glLines.Add(new GlLineRequest(account, 0m, line.Amount, null, $"{slip.EmployeeCode} {line.ComponentCode}"));
                    else
                        glLines.Add(new GlLineRequest(account, line.Amount, 0m,
                            string.IsNullOrWhiteSpace(slip.CostCenter) ? null : slip.CostCenter,
                            $"{slip.EmployeeCode} {line.ComponentCode}"));
                }
            }
            if (run.TotalNet > 0)
                glLines.Add(new GlLineRequest(HrGlAccounts.SalariesPayable, 0m, run.TotalNet, null, $"Net pay {run.RunNo}"));
            else if (run.TotalNet < 0)
                glLines.Add(new GlLineRequest(HrGlAccounts.SalariesPayable, -run.TotalNet, 0m, null, $"Net pay {run.RunNo}"));

            var companyGuid = await ResolveCompanyGuidAsync(connection, transaction, companyId).ConfigureAwait(false);
            var result = GlPosting.PostInTransaction(connection, transaction, _provider,
                new GlDocumentRequest(companyGuid,
                    DateOnly.Parse(run.PeriodTo, CultureInfo.InvariantCulture),
                    $"Payroll run {run.RunNo} ({run.PeriodYear}-{run.PeriodMonth:D2})",
                    "PAYROLL", run.Id.ToString("D"), run.RunNo,
                    run.CurrencyCode, 0m, glLines), userId.Trim());

            await ExecAsync(connection, transaction,
                $"UPDATE hr_payroll_runs SET status = 'Posted', journal_entry_id = @entry, journal_doc_no = @doc, posted_by = @actor, posted_at = {NowExpr}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@entry", (object)result.JournalEntryId.ToString("D")), ("@doc", (object)result.DocumentNumber),
                ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()), ("@id", FormatGuid(runId))).ConfigureAwait(false);
            await ExecAsync(connection, transaction,
                "UPDATE hr_payslips SET status = 'Final' WHERE company_id = @company AND run_id = @run",
                ("@company", companyId.Trim()), ("@run", FormatGuid(runId))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result.DocumentNumber;
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    public async Task<string> ReverseRunAsync(string companyId, Guid runId, string userId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await LoadRunAsync(connection, transaction, companyId, runId, withPayslips: false, cancellationToken).ConfigureAwait(false)
                ?? throw new AppError(ErrorCodes.HrPayrollStateInvalid, "Payroll run was not found for this company.");
            if (run.Status != HrPayrollStatuses.Posted || run.JournalEntryId is not Guid entryId)
                throw new AppError(ErrorCodes.HrPayrollStateInvalid,
                    $"Only a posted payroll run can be reversed (run {run.RunNo} status: '{run.Status}').", run.RunNo);

            var reversalId = GlPosting.ReverseInTransaction(connection, transaction, _provider,
                entryId, userId.Trim(), $"Payroll run {run.RunNo} reversed");
            var reversalDoc = await ScalarStringAsync(connection, transaction,
                "SELECT document_number FROM journal_entries WHERE journal_entry_id = @id",
                ("@id", FormatGuid(reversalId))).ConfigureAwait(false) ?? string.Empty;

            await ExecAsync(connection, transaction,
                $"UPDATE hr_payroll_runs SET status = 'Draft', journal_entry_id = NULL, reversed_journal_doc_no = @old, journal_doc_no = NULL, posted_by = NULL, posted_at = NULL, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
                ("@old", (object)(run.JournalDocNo ?? string.Empty)), ("@actor", (object)userId.Trim()),
                ("@company", companyId.Trim()), ("@id", FormatGuid(runId))).ConfigureAwait(false);
            await ExecAsync(connection, transaction,
                "UPDATE hr_payslips SET status = 'Draft' WHERE company_id = @company AND run_id = @run",
                ("@company", companyId.Trim()), ("@run", FormatGuid(runId))).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return reversalDoc;
        }
        catch { await RollbackAsync(transaction).ConfigureAwait(false); throw; }
    }

    private async Task<Guid> ResolveCompanyGuidAsync(DbConnection connection, DbTransaction transaction, string companyId)
    {
        var value = await ScalarStringAsync(connection, transaction,
            "SELECT company_id FROM companies WHERE company_code = @code", ("@code", companyId.Trim())).ConfigureAwait(false);
        if (value is null || !Guid.TryParse(value, out var guid))
            throw new AppError(ErrorCodes.HrPayrollStateInvalid,
                $"Company '{companyId.Trim()}' has no Guid identity in the companies registry; payroll cannot post to GL.", companyId.Trim());
        return guid;
    }

    // ==================================================================
    // Reports
    // ==================================================================

    public async Task<IReadOnlyList<HrPayrollDeptRow>> GetPayrollSummaryByDeptAsync(string companyId, Guid runId, CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, null, """
            SELECT COALESCE(department_name, '') AS dept, COUNT(*), COALESCE(SUM(gross_pay), 0),
                   COALESCE(SUM(total_deductions), 0), COALESCE(SUM(net_pay), 0), COALESCE(SUM(employer_cost), 0)
            FROM hr_payslips WHERE company_id = @company AND run_id = @run
            GROUP BY department_name ORDER BY dept
            """);
        Add(command, "@company", companyId.Trim());
        Add(command, "@run", FormatGuid(runId));
        var result = new List<HrPayrollDeptRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new HrPayrollDeptRow(ReadString(reader, 0) ?? string.Empty,
                Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                ReadDecimal(reader, 2), ReadDecimal(reader, 3), ReadDecimal(reader, 4), ReadDecimal(reader, 5)));
        return result;
    }

    // ==================================================================
    // Shared helpers (BomRepository conventions)
    // ==================================================================

    private async Task UpsertAsync(DbConnection connection, DbTransaction transaction, string table, Guid id,
        string companyId, string userId, (string Column, object? Value)[] columns, CancellationToken cancellationToken)
    {
        var exists = await CountAsync(connection, transaction,
            $"SELECT COUNT(*) FROM {table} WHERE company_id = @company AND id = @id",
            ("@company", companyId.Trim()), ("@id", FormatGuid(id))).ConfigureAwait(false) > 0;
        if (exists)
        {
            var sets = string.Join(", ", columns.Select((c, i) => $"{c.Column} = @p{i}"));
            await using var update = CreateCommand(connection, transaction,
                $"UPDATE {table} SET {sets}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id");
            for (var i = 0; i < columns.Length; i++) Add(update, $"@p{i}", columns[i].Value ?? DBNull.Value);
            Add(update, "@actor", userId.Trim());
            Add(update, "@company", companyId.Trim());
            Add(update, "@id", FormatGuid(id));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var cols = string.Join(", ", columns.Select(c => c.Column));
            var pars = string.Join(", ", columns.Select((c, i) => $"@p{i}"));
            await using var insert = CreateCommand(connection, transaction,
                $"INSERT INTO {table} (id, company_id, {cols}, created_by, updated_by) VALUES (@id, @company, {pars}, @actor, @actor)");
            Add(insert, "@id", FormatGuid(id));
            Add(insert, "@company", companyId.Trim());
            for (var i = 0; i < columns.Length; i++) Add(insert, $"@p{i}", columns[i].Value ?? DBNull.Value);
            Add(insert, "@actor", userId.Trim());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SoftDeleteAsync(DbConnection connection, DbTransaction transaction, string table,
        string companyId, Guid id, string userId, CancellationToken cancellationToken)
    {
        await ExecAsync(connection, transaction,
            $"UPDATE {table} SET is_deleted = {BoolTrue}, updated_by = @actor, updated_at = {NowExpr} WHERE company_id = @company AND id = @id",
            ("@actor", (object)userId.Trim()), ("@company", companyId.Trim()), ("@id", FormatGuid(id))).ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<decimal> ScalarDecimalAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? 0m : Convert.ToDecimal(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task<Guid?> ScalarGuidOrNullAsync(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var value = await ScalarStringAsync(connection, transaction, sql, parameters).ConfigureAwait(false);
        return value is not null && Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static async Task ExecAsync(DbConnection connection, DbTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var (name, value) in parameters) Add(command, name, value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task RollbackAsync(DbTransaction transaction)
    {
        try { await transaction.RollbackAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { /* already completed */ }
    }

    private static (string From, string To) PeriodBounds(int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        return (from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static void RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private object? NullGuid(Guid? value) => value is Guid guid ? FormatGuid(guid) : null;

    private DbConnection CreateConnection() => _provider == "SQLite"
        ? new SqliteConnection(_connectionString)
        : new NpgsqlConnection(_connectionString);

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        if (transaction != null) command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private object FormatGuid(Guid value) => _provider == "PostgreSQL" ? value : value.ToString("D");

    private static Guid ReadGuidValue(object value) => value switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out var parsed) => parsed,
        _ => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!)
    };

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => ReadGuidValue(reader.GetValue(ordinal));

    private static Guid? ReadGuidOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuidValue(reader.GetValue(ordinal));

    private static string? ReadString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0m : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool ReadBool(DbDataReader reader, int ordinal)
        => !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string NormalizeProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "sqlite" or "sqlite3" => "SQLite",
        "postgres" or "postgresql" or "npgsql" => "PostgreSQL",
        _ => throw new NotSupportedException($"Unsupported database provider '{provider}'. Use SQLite or PostgreSQL.")
    };
}
