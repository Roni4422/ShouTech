using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.App.Reporting;
using ShouTech.App.Services;

namespace ShouTech.App.Views.Reports;

/// <summary>Shared filter parsing for the HR report family: the period
/// filter is the run number (PR-YYYY-MM) or a bare YYYY-MM.</summary>
internal static class HrRptUtil
{
    public static (int Year, int Month) ParsePeriod(string? period)
    {
        if (!string.IsNullOrWhiteSpace(period))
        {
            var text = period.Trim().ToUpperInvariant();
            if (text.StartsWith("PR-", StringComparison.Ordinal)) text = text[3..];
            var parts = text.Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var year)
                && int.TryParse(parts[1], out var month))
                return (year, month);
        }
        var today = DateTime.Today;
        return (today.Year, today.Month);
    }

    public static string CurrentPeriod()
        => $"PR-{DateTime.Today.Year}-{DateTime.Today.Month:D2}";
}

/// <summary>
/// Payroll summary (Build HR 065): per-department gross / deductions /
/// net / employer cost for one payroll run, with company totals — the
/// department distribution of design section 5. Real data from
/// HrRepository.GetPayrollSummaryByDeptAsync over stored payslips.
/// </summary>
public sealed class HrPayrollSummaryWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "HR.PayrollSummary",
        TitleTextKey = "Hr.Reports.PayrollSummary",
        Columns = new RptCol[]
        {
            new() { Key = "Department", HeaderTextKey = "Hr.Col.Department", Type = RptColType.Text, Width = 220 },
            new() { Key = "Employees", HeaderTextKey = "Hr.Col.Employees", Type = RptColType.WholeNumber, Width = 90 },
            new() { Key = "Gross", HeaderTextKey = "Hr.Col.Gross", Type = RptColType.Money, Width = 120 },
            new() { Key = "Deductions", HeaderTextKey = "Hr.Col.Deductions", Type = RptColType.Money, Width = 120 },
            new() { Key = "Net", HeaderTextKey = "Hr.Col.Net", Type = RptColType.Money, Width = 120 },
            new() { Key = "Employer", HeaderTextKey = "Hr.Col.Employer", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Period", LabelTextKey = "Hr.Filter.Run", Kind = RptFltKind.Text, DefaultValue = HrRptUtil.CurrentPeriod() }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var service = AppHost.Hr;
        if (service is not null)
        {
            var (year, month) = HrRptUtil.ParsePeriod(filters.GetString("Period"));
            var runs = await service.GetRunsAsync(CurrentCompanyCode, ct);
            var run = runs.FirstOrDefault(r => r.PeriodYear == year && r.PeriodMonth == month);
            if (run is not null)
            {
                var summary = await service.GetPayrollSummaryByDeptAsync(CurrentCompanyCode, run.Id, ct);
                foreach (var r in summary)
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["Department"] = string.IsNullOrWhiteSpace(r.DepartmentName) ? "—" : r.DepartmentName,
                        ["Employees"] = r.EmployeeCount,
                        ["Gross"] = r.Gross,
                        ["Deductions"] = r.Deductions,
                        ["Net"] = r.Net,
                        ["Employer"] = r.Employer
                    });
            }
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Department")
        };
    }
}

/// <summary>
/// Employee payslip (Build HR 065): one employee's payslip lines for a
/// run — the printable slip (RptBase print). Earnings, deductions and
/// employer lines are listed in line order with the slip's base, paid
/// days and day-count facts in the line notes the calculator wrote.
/// </summary>
public sealed class HrPayslipWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "HR.Payslip",
        TitleTextKey = "Hr.Reports.Payslip",
        Columns = new RptCol[]
        {
            new() { Key = "Component", HeaderTextKey = "Hr.Col.Component", Type = RptColType.Text, Width = 150 },
            new() { Key = "Name", HeaderTextKey = "Hr.Col.Name", Type = RptColType.Text, Width = 220 },
            new() { Key = "Kind", HeaderTextKey = "Hr.Col.Kind", Type = RptColType.Text, Width = 110 },
            new() { Key = "Amount", HeaderTextKey = "Hr.Col.Amount", Type = RptColType.Money, Width = 120 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Period", LabelTextKey = "Hr.Filter.Run", Kind = RptFltKind.Text, DefaultValue = HrRptUtil.CurrentPeriod() },
            new() { Key = "Employee", LabelTextKey = "Hr.Filter.Employee", Kind = RptFltKind.Text }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var service = AppHost.Hr;
        if (service is not null)
        {
            var (year, month) = HrRptUtil.ParsePeriod(filters.GetString("Period"));
            var employeeCode = filters.GetString("Employee")?.Trim();
            var runs = await service.GetRunsAsync(CurrentCompanyCode, ct);
            var header = runs.FirstOrDefault(r => r.PeriodYear == year && r.PeriodMonth == month);
            if (header is not null)
            {
                var run = await service.GetRunAsync(CurrentCompanyCode, header.Id, ct);
                var slip = run?.Payslips.FirstOrDefault(p =>
                    string.IsNullOrWhiteSpace(employeeCode)
                    || string.Equals(p.EmployeeCode, employeeCode, StringComparison.OrdinalIgnoreCase));
                if (slip is not null)
                    foreach (var line in slip.Lines.OrderBy(l => l.LineNo))
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["Component"] = line.ComponentCode,
                            ["Name"] = line.ComponentName ?? line.ComponentCode,
                            ["Kind"] = line.Kind,
                            ["Amount"] = line.Amount
                        });
            }
        }
        // No totals row: summing earnings + deductions + employer lines
        // together would be meaningless on a slip.
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = null
        };
    }
}

/// <summary>
/// Attendance summary (Build HR 065): per employee for one month —
/// present / absent / late / leave / half days and overtime hours,
/// from the same records the payroll generation reads.
/// </summary>
public sealed class HrAttendanceSummaryWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "HR.AttendanceSummary",
        TitleTextKey = "Hr.Reports.AttendanceSummary",
        Columns = new RptCol[]
        {
            new() { Key = "Employee", HeaderTextKey = "Hr.Col.Employee", Type = RptColType.Text, Width = 240 },
            new() { Key = "Present", HeaderTextKey = "Hr.Col.Present", Type = RptColType.WholeNumber, Width = 85 },
            new() { Key = "Absent", HeaderTextKey = "Hr.Col.Absent", Type = RptColType.WholeNumber, Width = 85 },
            new() { Key = "Late", HeaderTextKey = "Hr.Col.Late", Type = RptColType.WholeNumber, Width = 85 },
            new() { Key = "Leave", HeaderTextKey = "Hr.Col.LeaveDays", Type = RptColType.WholeNumber, Width = 85 },
            new() { Key = "Half", HeaderTextKey = "Hr.Col.HalfDays", Type = RptColType.FractionalNumber, Width = 90, Format = "N1" },
            new() { Key = "Overtime", HeaderTextKey = "Hr.Col.Overtime", Type = RptColType.FractionalNumber, Width = 95, Format = "N2" }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Year", LabelTextKey = "Hr.Filter.Year", Kind = RptFltKind.Text, DefaultValue = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture) },
            new() { Key = "Month", LabelTextKey = "Hr.Filter.Month", Kind = RptFltKind.Text, DefaultValue = DateTime.Today.Month.ToString(CultureInfo.InvariantCulture) }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var service = AppHost.Hr;
        if (service is not null)
        {
            var year = RptWinUtil.ParseInt(filters.GetString("Year"), DateTime.Today.Year);
            var month = Math.Clamp(RptWinUtil.ParseInt(filters.GetString("Month"), DateTime.Today.Month), 1, 12);
            var summary = await service.GetAttendanceSummaryAsync(CurrentCompanyCode, year, month, ct);
            foreach (var r in summary)
                rows.Add(new Dictionary<string, object?>
                {
                    ["Employee"] = $"{r.EmployeeCode} — {r.EmployeeName}",
                    ["Present"] = r.PresentDays,
                    ["Absent"] = r.AbsentDays,
                    ["Late"] = r.LateDays,
                    ["Leave"] = r.LeaveDays,
                    ["Half"] = r.HalfDays,
                    ["Overtime"] = r.OvertimeHours
                });
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Employee")
        };
    }
}

/// <summary>
/// Leave balance report (Build HR 065): allocated / used / balance per
/// employee × leave type for a year — negative balances (override
/// approvals) show as negative, never hidden.
/// </summary>
public sealed class HrLeaveBalanceWin : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "HR.LeaveBalance",
        TitleTextKey = "Hr.Reports.LeaveBalance",
        Columns = new RptCol[]
        {
            new() { Key = "Employee", HeaderTextKey = "Hr.Col.Employee", Type = RptColType.Text, Width = 240 },
            new() { Key = "LeaveType", HeaderTextKey = "Hr.Col.LeaveType", Type = RptColType.Text, Width = 160 },
            new() { Key = "Allocated", HeaderTextKey = "Hr.Col.Allocated", Type = RptColType.FractionalNumber, Width = 100, Format = "N1" },
            new() { Key = "Used", HeaderTextKey = "Hr.Col.Used", Type = RptColType.FractionalNumber, Width = 100, Format = "N1" },
            new() { Key = "Balance", HeaderTextKey = "Hr.Col.Balance", Type = RptColType.FractionalNumber, Width = 100, Format = "N1" }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "Year", LabelTextKey = "Hr.Filter.Year", Kind = RptFltKind.Text, DefaultValue = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture) }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var def = BuildDefinition();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var service = AppHost.Hr;
        if (service is not null)
        {
            var year = RptWinUtil.ParseInt(filters.GetString("Year"), DateTime.Today.Year);
            var balances = await service.GetLeaveBalancesAsync(CurrentCompanyCode, year, ct);
            foreach (var b in balances)
                rows.Add(new Dictionary<string, object?>
                {
                    ["Employee"] = $"{b.EmployeeCode} — {b.EmployeeName}",
                    ["LeaveType"] = b.LeaveTypeCode ?? string.Empty,
                    ["Allocated"] = b.AllocatedDays,
                    ["Used"] = b.UsedDays,
                    ["Balance"] = b.Balance
                });
        }
        return new RptData
        {
            Columns = def.Columns,
            Rows = rows,
            Totals = RptWinUtil.SumTotals(rows, def.Columns, T("Reports.Totals"), "Employee")
        };
    }
}

/// <summary>
/// Period-based cash-box movement report sourced exclusively from posted
/// general-ledger lines. Opening balances include all earlier posted activity.
/// </summary>
public sealed class CashJrnRpt : RptBase
{
    protected override RptDef BuildDefinition() => new()
    {
        ReportKey = "Bonds.CashJournal",
        TitleTextKey = "Reports.CashJournal",
        Columns = new RptCol[]
        {
            new() { Key = "EntryDate", HeaderTextKey = "Reports.Col.Date", Type = RptColType.Date, Width = 100 },
            new() { Key = "DocNo", HeaderTextKey = "Reports.Col.DocNo", Type = RptColType.Text, Width = 125 },
            new() { Key = "CashAccountName", HeaderTextKey = "Reports.Col.CashBox", Type = RptColType.Text, Width = 150 },
            new() { Key = "CounterAccount", HeaderTextKey = "Reports.Col.CounterAccount", Type = RptColType.Text, Width = 190 },
            new() { Key = "Description", HeaderTextKey = "Reports.Col.Description", Type = RptColType.Text, Width = 220 },
            new() { Key = "Reference", HeaderTextKey = "Reports.Col.Reference", Type = RptColType.Text, Width = 120 },
            new() { Key = "OpeningBalance", HeaderTextKey = "Reports.Col.OpeningBalance", Type = RptColType.Money, Width = 125 },
            new() { Key = "Receipt", HeaderTextKey = "Reports.Col.Receipts", Type = RptColType.Money, Width = 115 },
            new() { Key = "Payment", HeaderTextKey = "Reports.Col.Payments", Type = RptColType.Money, Width = 115 },
            new() { Key = "Balance", HeaderTextKey = "Reports.Col.Balance", Type = RptColType.Money, Width = 125 },
            new() { Key = "CreatedBy", HeaderTextKey = "Reports.Col.User", Type = RptColType.Text, Width = 110 }
        },
        Filters = new RptFlt[]
        {
            new() { Key = "From", LabelTextKey = "Reports.FromDate", Kind = RptFltKind.DateFrom, DefaultValue = DateTime.Today },
            new() { Key = "To", LabelTextKey = "Reports.ToDate", Kind = RptFltKind.DateTo, DefaultValue = DateTime.Today }
        }
    };

    protected override async Task<RptData> LoadDataAsync(RptFltValues filters, CancellationToken ct)
    {
        var today = DateTime.Today;
        var from = filters.GetDateOr("From", today).Date;
        var to = filters.GetDateOr("To", today).Date;
        if (to < from)
            (from, to) = (to, from);

        var definition = BuildDefinition();
        var raw = await RptQuery.CashJournalAsync(from, to, ct);
        var mapped = RptWinUtil.Map(raw, definition.Columns);
        var rows = new List<IReadOnlyDictionary<string, object?>>(mapped.Count);

        foreach (var item in mapped)
        {
            var row = item.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (string.Equals(Convert.ToString(row.GetValueOrDefault("DocNo"), CultureInfo.InvariantCulture), "OPENING", StringComparison.Ordinal))
            {
                row["Description"] = T("Reports.OpeningBalance");
                row["DocNo"] = string.Empty;
            }
            rows.Add(row);
        }

        var openingRows = raw.Where(r =>
            string.Equals(Convert.ToString(r.GetValueOrDefault("DocNo"), CultureInfo.InvariantCulture), "OPENING", StringComparison.Ordinal)).ToList();
        var opening = openingRows.Sum(r => RptWinUtil.Dec(r.GetValueOrDefault("Balance")));
        var movements = raw.Where(r =>
            !string.Equals(Convert.ToString(r.GetValueOrDefault("DocNo"), CultureInfo.InvariantCulture), "OPENING", StringComparison.Ordinal)).ToList();
        var receipts = movements.Sum(r => RptWinUtil.Dec(r.GetValueOrDefault("Receipt")));
        var payments = movements.Sum(r => RptWinUtil.Dec(r.GetValueOrDefault("Payment")));
        var closing = raw
            .GroupBy(r => Convert.ToString(r.GetValueOrDefault("CashAccountCode"), CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal)
            .Sum(group =>
            {
                var lastMovement = group.LastOrDefault(r =>
                    !string.Equals(Convert.ToString(r.GetValueOrDefault("DocNo"), CultureInfo.InvariantCulture), "OPENING", StringComparison.Ordinal));
                var lastRow = lastMovement ?? group.Last();
                return RptWinUtil.Dec(lastRow.GetValueOrDefault("Balance"));
            });

        return new RptData
        {
            Columns = definition.Columns,
            Rows = rows,
            Totals = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Description"] = T("Reports.Totals"),
                ["OpeningBalance"] = opening,
                ["Receipt"] = receipts,
                ["Payment"] = payments,
                ["Balance"] = closing
            }
        };
    }
}
