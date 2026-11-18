using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ShouTech.App.Services;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Infrastructure;
using Xunit;

namespace Kern.Tests;

/// <summary>
/// BUILD HR/Payroll (migration 065) — pins, against real migrated
/// SQLite databases:
///   * the pure PayrollCalculator: the design's worked case nets
///     exactly 1213.33, % -of-gross is order-independent, employer
///     components never touch gross/net, per-line rounding foots;
///   * the employee round-trip (full card persists + reloads
///     identically) and the lifecycle rule: a terminated employee is
///     excluded from a new payroll run;
///   * leave: allocate 30 → approve 5 → balance 25; a 26-day
///     application is refused with HR-1003 and allowed with the
///     override; approval writes Leave attendance rows;
///   * payroll posting: GL balanced with the per-component account
///     split, source-linked to the run; reversal nets GL to zero and
///     re-opens the payslips; a payment voucher through GlPosting
///     reduces the 2204 payable by the net.
/// Execution requires Windows (net8.0-windows testhost); the same
/// scenarios are verified headlessly by
/// ~/workspace/research/build-hr/hr_harness.py (see
/// docs/BUILD_HR_PAYROLL.md).
/// </summary>
public class HrPayrollTests
{
    private const string Actor = "hr-test";
    private const string Company = "HR1";

    // ------------------------------------------------------------------
    // Pure calculator
    // ------------------------------------------------------------------

    [Fact]
    public void Calculator_WorkedCase_NetIs1213_33()
    {
        var result = PayrollCalculator.Calculate(1000m, new[]
        {
            new PayrollCalculator.CalcLine("HOUSING", "Housing Allowance", HrComponentKinds.Earning, HrCalcTypes.PercentOfBase, 0m, 25m, "501"),
            new PayrollCalculator.CalcLine("TRANSPORT", "Transport Allowance", HrComponentKinds.Earning, HrCalcTypes.Fixed, 100m, 0m, "501"),
            new PayrollCalculator.CalcLine("SOCIAL", "Social Insurance", HrComponentKinds.Deduction, HrCalcTypes.PercentOfBase, 0m, 7m, "2211")
        }, unpaidLeaveDays: 2m, absentDays: 0m);

        Assert.Equal(1350m, result.GrossPay);
        Assert.Equal(136.67m, result.TotalDeductions);
        Assert.Equal(1213.33m, result.NetPay);
        Assert.Equal(28m, result.PaidDays);
        var unpaid = Assert.Single(result.Lines, l => l.ComponentCode == PayrollCalculator.UnpaidLeaveComponentCode);
        Assert.True(unpaid.IsSystem);
        Assert.Equal(66.67m, unpaid.Amount);
        Assert.Equal(0m, result.EmployerCost);
    }

    [Fact]
    public void Calculator_PercentOfGross_OrderIndependent_EmployerExcludedFromNet()
    {
        PayrollCalculator.CalcLine[] Lines() => new[]
        {
            new PayrollCalculator.CalcLine("BONUS_PCT", "Bonus", HrComponentKinds.Earning, HrCalcTypes.PercentOfGross, 0m, 10m, "501"),
            new PayrollCalculator.CalcLine("ALLOW", "Allowance", HrComponentKinds.Earning, HrCalcTypes.Fixed, 200m, 0m, "501"),
            new PayrollCalculator.CalcLine("TAX", "Tax", HrComponentKinds.Deduction, HrCalcTypes.PercentOfGross, 0m, 10m, "2204"),
            new PayrollCalculator.CalcLine("SOCIAL_ER", "Employer Social", HrComponentKinds.Employer, HrCalcTypes.PercentOfBase, 0m, 12m, "501")
        };
        var forward = PayrollCalculator.Calculate(1000m, Lines(), 0m, 0m);
        var reversed = PayrollCalculator.Calculate(1000m, Lines().Reverse().ToArray(), 0m, 0m);
        // Gross = 1000 + 200 + 10% x 1200 = 1320; tax = 10% x 1320 = 132; net = 1188.
        Assert.Equal(1320m, forward.GrossPay);
        Assert.Equal(132m, forward.TotalDeductions);
        Assert.Equal(1188m, forward.NetPay);
        Assert.Equal(120m, forward.EmployerCost);
        Assert.Equal(forward.GrossPay, reversed.GrossPay);
        Assert.Equal(forward.NetPay, reversed.NetPay);
    }

    [Fact]
    public void Calculator_AbsenceLine_Explicit_AndRoundingFoots()
    {
        var result = PayrollCalculator.Calculate(999m, Array.Empty<PayrollCalculator.CalcLine>(), 0m, 1m);
        var absence = Assert.Single(result.Lines, l => l.ComponentCode == PayrollCalculator.AbsenceComponentCode);
        Assert.True(absence.IsSystem);
        Assert.Equal(33.30m, absence.Amount); // 999 / 30 = 33.30 exactly
        Assert.Equal(999m - 33.30m, result.NetPay);
        Assert.Equal(result.Lines.Where(l => l.Kind == HrComponentKinds.Earning).Sum(l => l.Amount), result.GrossPay);
    }

    // ------------------------------------------------------------------
    // Repository integration
    // ------------------------------------------------------------------

    [Fact]
    public async Task Employee_RoundTrip_Identical_AndTerminatedExcludedFromRun()
    {
        using var db = await SeededAsync();
        var repo = new HrRepository("SQLite", db.ConnectionString);
        var dept = new HrDepartment { Code = "OPS", NameEn = "Operations" };
        await repo.SaveDepartmentAsync(Company, dept, Actor);
        var desig = new HrDesignation { Code = "MGR", NameEn = "Manager", DepartmentId = dept.Id };
        await repo.SaveDesignationAsync(Company, desig, Actor);

        var employee = FullEmployee("E-001", dept.Id, desig.Id);
        await repo.SaveEmployeeAsync(Company, employee, Actor);
        var loaded = await repo.GetEmployeeAsync(Company, employee.Id);
        Assert.NotNull(loaded);
        Assert.Equal(employee.EmployeeCode, loaded!.EmployeeCode);
        Assert.Equal(employee.FirstName, loaded.FirstName);
        Assert.Equal(employee.MiddleName, loaded.MiddleName);
        Assert.Equal(employee.LastName, loaded.LastName);
        Assert.Equal(employee.NameAr, loaded.NameAr);
        Assert.Equal(employee.NationalId, loaded.NationalId);
        Assert.Equal(employee.SocialInsuranceNo, loaded.SocialInsuranceNo);
        Assert.Equal(employee.BankAccountNo, loaded.BankAccountNo);
        Assert.Equal(employee.DepartmentId, loaded.DepartmentId);
        Assert.Equal(employee.DesignationId, loaded.DesignationId);
        Assert.Equal(employee.HireDate, loaded.HireDate);
        Assert.Equal(employee.BaseSalary, loaded.BaseSalary);
        Assert.Equal(employee.CostCenter, loaded.CostCenter);
        Assert.Equal(employee.EmergencyContactPhone, loaded.EmergencyContactPhone);

        var second = FullEmployee("E-002", dept.Id, desig.Id);
        await repo.SaveEmployeeAsync(Company, second, Actor);
        second.Status = HrEmployeeStatuses.Terminated;
        second.TerminationDate = "2026-02-15";
        second.TerminationReason = "Resigned";
        await repo.SaveEmployeeAsync(Company, second, Actor);

        var run = await repo.GenerateRunAsync(Company, 2026, 3, Actor);
        var slip = Assert.Single(run.Payslips);
        Assert.Equal(employee.Id, slip.EmployeeId);
    }

    [Fact]
    public async Task Leave_GrantApprove_Balance25_OverBalanceRefusedWithoutOverride()
    {
        using var db = await SeededAsync();
        var repo = new HrRepository("SQLite", db.ConnectionString);
        var employee = FullEmployee("E-001", null, null);
        await repo.SaveEmployeeAsync(Company, employee, Actor);
        var annual = (await repo.GetLeaveTypesAsync(Company)).Single(t => t.Code == "ANNUAL");
        await repo.AllocateLeaveAsync(Company, employee.Id, annual.Id, 2026, 30m, Actor);

        var five = new HrLeaveApplication
        {
            EmployeeId = employee.Id, LeaveTypeId = annual.Id,
            FromDate = "2026-03-02", ToDate = "2026-03-06", Reason = "Family"
        };
        await repo.SaveLeaveApplicationAsync(Company, five, Actor);
        Assert.Equal(5m, five.TotalDays);
        await repo.ApproveLeaveAsync(Company, five.Id, allowOverride: false, Actor);

        var balance = Assert.Single(await repo.GetLeaveBalancesAsync(Company, 2026));
        Assert.Equal(25m, balance.Balance);
        var attendance = await repo.GetAttendanceAsync(Company, 2026, 3);
        Assert.Equal(5, attendance.Count(r => r.Status == HrAttendanceStatuses.Leave && r.LeaveApplicationId == five.Id));

        var big = new HrLeaveApplication
        {
            EmployeeId = employee.Id, LeaveTypeId = annual.Id,
            FromDate = "2026-04-01", ToDate = "2026-04-26", Reason = "Long trip"
        };
        await repo.SaveLeaveApplicationAsync(Company, big, Actor);
        Assert.Equal(26m, big.TotalDays);
        var error = await Assert.ThrowsAsync<AppError>(() => repo.ApproveLeaveAsync(Company, big.Id, allowOverride: false, Actor));
        Assert.Equal(ErrorCodes.HrLeaveBalanceInsufficient, error.Code);
        await repo.ApproveLeaveAsync(Company, big.Id, allowOverride: true, Actor);
        var after = Assert.Single(await repo.GetLeaveBalancesAsync(Company, 2026));
        Assert.Equal(-1m, after.Balance);
    }

    [Fact]
    public async Task Payroll_Posting_GlSplitExact_ReversalNetsZero_PaymentReducesPayable()
    {
        using var db = await SeededAsync();
        var repo = new HrRepository("SQLite", db.ConnectionString);
        var employee = FullEmployee("E-001", null, null);
        await repo.SaveEmployeeAsync(Company, employee, Actor);

        var components = await repo.GetComponentsAsync(Company);
        Guid Comp(string code) => components.Single(c => c.Code == code).Id;
        var structure = new HrSalaryStructure
        {
            Code = "STD", NameEn = "Standard", AppliesTo = "Employee",
            Lines = new List<HrStructureLine>
            {
                new() { ComponentId = Comp("HOUSING"), CalcType = HrCalcTypes.PercentOfBase, Percent = 25m, AccountCode = "501", IsActive = true },
                new() { ComponentId = Comp("TRANSPORT"), CalcType = HrCalcTypes.Fixed, Amount = 100m, AccountCode = "501", IsActive = true },
                new() { ComponentId = Comp("SOCIAL"), CalcType = HrCalcTypes.PercentOfBase, Percent = 7m, AccountCode = "2211", IsActive = true }
            }
        };
        await repo.SaveStructureAsync(Company, structure, Actor);
        await repo.SaveAssignmentAsync(Company, new HrSalaryAssignment
        {
            EmployeeId = employee.Id, StructureId = structure.Id, BaseSalary = 1000m,
            CurrencyCode = "SAR", FromDate = "2026-01-01", IsActive = true
        }, Actor);

        var unpaidType = (await repo.GetLeaveTypesAsync(Company)).Single(t => t.Code == "UNPAID");
        var leave = new HrLeaveApplication
        {
            EmployeeId = employee.Id, LeaveTypeId = unpaidType.Id,
            FromDate = "2026-03-10", ToDate = "2026-03-11", Reason = "Personal"
        };
        await repo.SaveLeaveApplicationAsync(Company, leave, Actor);
        await repo.ApproveLeaveAsync(Company, leave.Id, allowOverride: true, Actor);

        var run = await repo.GenerateRunAsync(Company, 2026, 3, Actor);
        var slip = Assert.Single(run.Payslips);
        Assert.Equal(1213.33m, slip.NetPay);
        Assert.Equal(run.TotalNet, run.Payslips.Sum(p => p.NetPay));
        Assert.Equal(1350m, run.TotalGross);

        var doc = await repo.PostRunAsync(Company, run.Id, Actor);
        Assert.StartsWith("JV-", doc);
        Assert.Equal(1283.33m, GlNet(db, "PAYROLL", run.Id, "501"));   // Dr salary expense (1350 gross - 66.67 never-owed unpaid leave)
        Assert.Equal(-70m, GlNet(db, "PAYROLL", run.Id, "2211"));       // Cr social payable
        Assert.Equal(-1213.33m, GlNet(db, "PAYROLL", run.Id, "2204"));  // Cr net payable
        var posted = await repo.GetRunAsync(Company, run.Id);
        Assert.Equal(HrPayrollStatuses.Posted, posted!.Status);
        Assert.Equal(HrPayrollStatuses.Final, posted.Payslips[0].Status);

        await repo.ReverseRunAsync(Company, run.Id, Actor);
        Assert.Equal(0m, GlNet(db, "PAYROLL", run.Id, "501", includeReversed: true));
        Assert.Equal(0m, GlNet(db, "PAYROLL", run.Id, "2204", includeReversed: true));
        var reopened = await repo.GetRunAsync(Company, run.Id);
        Assert.Equal(HrPayrollStatuses.Draft, reopened!.Status);
        Assert.Equal(HrPayrollStatuses.Draft, reopened.Payslips[0].Status);

        // Re-post, then pay through the voucher path (GlPosting, the
        // engine CshWin uses): Dr 2204 / Cr 131 settles the payable.
        // Balances here are all-entries (original + reversal + repost).
        await repo.PostRunAsync(Company, run.Id, Actor);
        Assert.Equal(-1213.33m, GlNet(db, "PAYROLL", run.Id, "2204", includeReversed: true));
        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            GlPosting.PostInTransaction(connection, transaction, "SQLite",
                new GlDocumentRequest(db.CompanyId, new DateOnly(2026, 4, 1),
                    "Salaries payment PR-2026-03", "PAYROLL-PAY", run.Id.ToString("D") + "-PAY", "PR-2026-03-PAY",
                    "SAR", 0m, new[]
                    {
                        new GlLineRequest("2204", 1213.33m, 0m),
                        new GlLineRequest("131", 0m, 1213.33m)
                    }), Actor);
            transaction.Commit();
        }
        Assert.Equal(0m, GlNet(db, "PAYROLL", run.Id, "2204", includeReversed: true)
            + GlNet(db, "PAYROLL-PAY", run.Id, "2204", includeReversed: true, sourceIdSuffix: "-PAY"));
    }

    // ------------------------------------------------------------------
    // Fixtures + helpers
    // ------------------------------------------------------------------

    private static HrEmployee FullEmployee(string code, Guid? departmentId, Guid? designationId) => new()
    {
        EmployeeCode = code, FirstName = "Ahmad", MiddleName = "Khaled", LastName = "Haddad",
        NameAr = "Ahmad Haddad", Gender = "Male", DateOfBirth = "1990-05-01",
        NationalId = "NAT-123", PassportNo = "P-456", SocialInsuranceNo = "SI-789", TaxId = "T-1",
        Phone = "011", Mobile = "055", PersonalEmail = "a@home.test", WorkEmail = "a@work.test",
        AddressPresent = "Street 1", AddressPermanent = "Street 2",
        EmergencyContactName = "Sara", EmergencyContactPhone = "056",
        DepartmentId = departmentId, DesignationId = designationId, Branch = "HQ",
        CostCenter = null, HireDate = "2025-01-15", EmploymentType = HrEmploymentTypes.FullTime,
        Status = HrEmployeeStatuses.Active, SalaryMode = HrSalaryModes.Bank,
        BankName = "Test Bank", BankAccountNo = "SA00-IBAN", BaseSalary = 1000m, CurrencyCode = "SAR"
    };

    private static decimal GlNet(TestDb db, string sourceType, Guid runId, string accountCode, bool includeReversed = false, string? sourceIdSuffix = null)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(l.debit - l.credit), 0)
            FROM journal_entry_lines l
            JOIN journal_entries e ON e.journal_entry_id = l.journal_entry_id
            JOIN chart_of_accounts a ON a.account_id = l.account_id
            WHERE e.source_doc_type = $type AND e.source_doc_id = $source
              AND a.account_code = $account AND ($all = 1 OR e.status = 1);
            """;
        cmd.Parameters.AddWithValue("$type", sourceType);
        cmd.Parameters.AddWithValue("$source", runId.ToString("D") + (sourceIdSuffix ?? string.Empty));
        cmd.Parameters.AddWithValue("$account", accountCode);
        cmd.Parameters.AddWithValue("$all", includeReversed ? 1 : 0);
        return Convert.ToDecimal(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed class TestDb : IDisposable
    {
        public required string Root { get; init; }
        public required string DbPath { get; init; }
        public required Guid CompanyId { get; init; }
        public string ConnectionString => $"Data Source={DbPath};";
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private static async Task<TestDb> SeededAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "hr-build-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "hr.db");
        var companyId = Guid.NewGuid();
        var connectionString = $"Data Source={dbPath};";
        Assert.True((await SchemaMigrator.ApplyAllAsync("SQLite", dbPath)).Success);
        var db = new TestDb { Root = root, DbPath = dbPath, CompanyId = companyId };

        void Exec(string sql)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec($"""
            INSERT INTO companies (company_id, company_code, company_name_ar, company_name_en, currency_code)
            VALUES ('{companyId:D}', '{Company}', 'HR Test Co', 'HR Test Co', 'SAR');
            """);
        Exec($"""
            INSERT INTO fiscal_years (company_id, fiscal_year, database_name, start_date, end_date, is_current, is_closed)
            VALUES ('{companyId:D}', 2026, '', '2026-01-01', '2026-12-31', 1, 0);
            """);
        long yearId;
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT fiscal_year_id FROM fiscal_years WHERE company_id = '{companyId:D}' AND fiscal_year = 2026";
            yearId = Convert.ToInt64(cmd.ExecuteScalar());
        }
        for (var m = 1; m <= 12; m++)
            Exec($"""
                INSERT INTO fiscal_periods (fiscal_period_id, company_id, fiscal_year_id, period_number, period_name, start_date, end_date, status)
                VALUES ('{Guid.NewGuid():D}', '{companyId:D}', {yearId}, {m}, '2026-{m:D2}', '2026-{m:D2}-01', '{new DateTime(2026, m, 1).AddMonths(1).AddDays(-1):yyyy-MM-dd}', 'OPEN');
                """);
        foreach (var (code, name, type) in new[]
        {
            ("131", "Cash", "ASSET"), ("501", "Salaries and Wages", "EXPENSE"),
            ("2204", "Salaries and Wages Payable", "LIABILITY"), ("2211", "Payroll Taxes Payable", "LIABILITY")
        })
            Exec($"""
                INSERT INTO chart_of_accounts (account_id, company_id, account_code, account_name, account_type, is_postable, is_active, is_deleted)
                VALUES ('{Guid.NewGuid():D}', '{companyId:D}', '{code}', '{name}', '{type}', 1, 1, 0);
                """);
        return db;
    }
}
