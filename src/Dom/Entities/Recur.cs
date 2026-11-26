using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ShouTech.Domain.Entities;

/// <summary>Interval types stored in sre_recurring_schedules.interval_type (migration 106).</summary>
public static class RecurIntervalTypes
{
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";

    public static readonly IReadOnlyList<string> All = new[] { Daily, Weekly, Monthly, Yearly };

    public static bool IsValid(string? value) => value is Daily or Weekly or Monthly or Yearly;
}

/// <summary>Lifecycle statuses stored in sre_recurring_schedules.status (migration 106).</summary>
public static class RecurScheduleStatus
{
    public const string Active = "Active";
    public const string Paused = "Paused";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

/// <summary>Outcomes stored in sre_recurring_runs.outcome (migration 106).</summary>
public static class RecurRunOutcome
{
    /// <summary>The occurrence was claimed (run row inserted, schedule advanced) but its document was not finished yet.</summary>
    public const string Pending = "Pending";
    /// <summary>The document was cloned and left as a draft.</summary>
    public const string Draft = "Draft";
    /// <summary>The document was cloned and posted through the normal post path.</summary>
    public const string Posted = "Posted";
    /// <summary>The document was cloned but posting it failed; the draft remains and the failure is recorded.</summary>
    public const string PostFailed = "PostFailed";
    /// <summary>Cloning itself failed; no document exists for this occurrence and the failure is recorded.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// One recurring sales schedule (migration 106, sre_recurring_schedules):
/// a TEMPLATE sales document (kind SO or SAL, draft or posted) cloned into
/// a new draft of the SAME kind on every due occurrence. Company keying is
/// the company CODE, the party/master-data convention (097/100).
/// </summary>
public sealed class RecurSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long TemplateDocId { get; set; }
    public string TemplateDocKind { get; set; } = string.Empty;
    public string TemplateDocNo { get; set; } = string.Empty;
    public string IntervalType { get; set; } = RecurIntervalTypes.Monthly;
    public int IntervalCount { get; set; } = 1;
    public DateOnly StartDate { get; set; }
    public DateOnly NextRunDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? MaxOccurrences { get; set; }
    public int OccurrencesCreated { get; set; }
    public bool AutoPost { get; set; }
    public string Status { get; set; } = RecurScheduleStatus.Active;
    public DateOnly? LastRunDate { get; set; }
    public string? LastRunStatus { get; set; }
    public long? LastGeneratedDocId { get; set; }
}

/// <summary>One claimed occurrence of a schedule (migration 106, sre_recurring_runs).</summary>
public sealed record RecurRun(
    Guid RunId,
    Guid ScheduleId,
    int OccurrenceNo,
    DateOnly RunDate,
    long? GeneratedDocId,
    string Outcome,
    string? ErrorText);

/// <summary>The tally of one generation pass (due sweep or a single Run Now).</summary>
public sealed record RecurGenerationSummary(
    int SchedulesProcessed,
    int DocumentsCreated,
    int DocumentsPosted,
    int Failures,
    int SchedulesCompleted);

/// <summary>
/// Pure interval arithmetic for recurring schedules. Monthly/yearly steps
/// clamp to the target month's end, and a date that IS its month's last
/// day stays on month-ends (2026-01-31 monthly → 2026-02-28 → 2026-03-31),
/// the ERPNext recurring-invoice convention.
/// </summary>
public static class RecurScheduleMath
{
    public static DateOnly Advance(DateOnly date, string intervalType, int intervalCount)
    {
        if (intervalCount < 1)
            throw new AppError(ErrorCodes.RecurringScheduleInvalid,
                "A recurring schedule requires a positive interval count.", intervalCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return intervalType switch
        {
            RecurIntervalTypes.Daily => date.AddDays(intervalCount),
            RecurIntervalTypes.Weekly => date.AddDays(7 * intervalCount),
            RecurIntervalTypes.Monthly => AddMonthsClamped(date, intervalCount),
            RecurIntervalTypes.Yearly => AddMonthsClamped(date, 12 * intervalCount),
            _ => throw new AppError(ErrorCodes.RecurringScheduleInvalid,
                $"Recurring interval type '{intervalType}' is not supported.", intervalType ?? string.Empty)
        };
    }

    private static DateOnly AddMonthsClamped(DateOnly date, int months)
    {
        var isMonthEnd = date.Day == DateTime.DaysInMonth(date.Year, date.Month);
        var target = date.AddMonths(months); // DateOnly.AddMonths already clamps an overflowing day.
        if (isMonthEnd)
            target = new DateOnly(target.Year, target.Month, DateTime.DaysInMonth(target.Year, target.Month));
        return target;
    }
}

/// <summary>
/// Persistence for the recurring sales engine (migration 106). The
/// claim/complete split is the idempotency contract: an occurrence is
/// claimed (run row + schedule advance in ONE transaction, guarded by the
/// ux_sre_runs_schedule_occurrence unique index) BEFORE its document is
/// cloned, and completed once the clone exists — a re-run for the same
/// date therefore creates nothing new.
/// </summary>
public interface IRecurRepository
{
    Task<IReadOnlyList<RecurSchedule>> GetSchedulesAsync(string companyId, CancellationToken cancellationToken = default);
    Task<RecurSchedule?> GetScheduleAsync(string companyId, Guid scheduleId, CancellationToken cancellationToken = default);
    Task InsertScheduleAsync(string companyId, RecurSchedule schedule, string userId, CancellationToken cancellationToken = default);
    Task SetStatusAsync(string companyId, Guid scheduleId, string fromStatus, string toStatus, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecurRun>> GetRunsAsync(string companyId, Guid scheduleId, CancellationToken cancellationToken = default);

    /// <summary>Every Active schedule (all companies) whose next run is due at <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<(string CompanyId, RecurSchedule Schedule)>> GetDueSchedulesAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims occurrence <paramref name="occurrenceNo"/>: inserts
    /// its Pending run row and advances the schedule (occurrences_created,
    /// next_run_date, or Completed when <paramref name="completesSchedule"/>).
    /// Returns the run id, or null when the occurrence was already claimed.
    /// </summary>
    Task<Guid?> ClaimOccurrenceAsync(string companyId, Guid scheduleId, int occurrenceNo, DateOnly runDate,
        DateOnly? newNextRunDate, bool completesSchedule, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the finished clone on a claimed run: the generated document,
    /// its outcome, and the recurring_schedule_id stamp on the document —
    /// one transaction.
    /// </summary>
    Task CompleteOccurrenceAsync(string companyId, Guid runId, long generatedDocId, string outcome, string? errorText, string userId, CancellationToken cancellationToken = default);

    /// <summary>Records a clone failure on a claimed run and on the schedule's last-run status.</summary>
    Task FailOccurrenceAsync(string companyId, Guid runId, string errorText, string userId, CancellationToken cancellationToken = default);

    /// <summary>Resolves any company key (code or Guid text) to the company CODE via the companies registry.</summary>
    Task<string> ResolveCompanyCodeAsync(string companyKey, CancellationToken cancellationToken = default);
}
