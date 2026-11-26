using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Recurring sales engine (owner sales spec sections 3 + 5, migration
/// 106, workstream W11): a TEMPLATE sales document (kind SO or SAL, draft
/// or posted) is cloned into a NEW DRAFT of the same kind on every due
/// occurrence — header and lines copied through the normal draft-creation
/// path of <see cref="ICommercialDocumentRepository"/>, the number taken
/// from the existing numbering service (the template's number is never
/// reused), the generated document stamped with its schedule
/// (sales_documents.recurring_schedule_id) and linked template → generated
/// in document_links, following the chain-link convention.
///
/// GENERATION IS IDEMPOTENT: an occurrence is CLAIMED in the repository
/// (run row + schedule advance in one transaction, unique
/// (schedule_id, occurrence_no)) before anything is cloned, and the
/// schedule's next_run_date only ever moves forward — re-running
/// <see cref="GenerateDueAsync"/> for the same date creates nothing new.
///
/// AUTO-POST goes through the normal post path
/// (<see cref="ICommercialDocumentRepository.PostSalesAsync"/>), so every
/// posting guard — credit, pricing, approval policy — applies unchanged.
/// A failed post leaves the draft in place and records the failure on the
/// run and on the schedule's last-run status; nothing fails silently.
///
/// HEADLESS HOOK POINT: <see cref="GenerateDueAsync"/> is a plain public
/// service method — a future scheduler (or an app-startup task, once a
/// startup-tasks pattern exists) calls it with the current date; the
/// RecurWin "Run Now" button calls the same path for one schedule.
///
/// This engine is deliberately GENERIC (template + interval + occurrence
/// bookkeeping): the L4 subscription layer is expected to sit on top of
/// it later; no subscription-specific concepts live here.
/// </summary>
public sealed class RecurService
{
    public const string ViewPermission = "SALES.RECURRING.VIEW";
    public const string ManagePermission = "SALES.RECURRING.MANAGE";

    /// <summary>Safety bound for one schedule in one pass (a decade of daily occurrences).</summary>
    private const int MaxOccurrencesPerPass = 3660;

    private readonly IRecurRepository _repository;
    private readonly ICommercialDocumentRepository _documents;
    private readonly IOrderDocumentRepository _orders;

    public RecurService(IRecurRepository repository, ICommercialDocumentRepository documents, IOrderDocumentRepository orders)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
    }

    // ------------------------------------------------------------------
    // Management
    // ------------------------------------------------------------------
    public Task<IReadOnlyList<RecurSchedule>> GetSchedulesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetSchedulesAsync(RequireCompany(companyId), cancellationToken);

    public Task<IReadOnlyList<RecurRun>> GetRunsAsync(string companyId, Guid scheduleId, CancellationToken cancellationToken = default)
        => _repository.GetRunsAsync(RequireCompany(companyId), scheduleId, cancellationToken);

    /// <summary>
    /// Creates an Active schedule from a template document. Every
    /// validation failure is the cataloged SAL-7024
    /// (<see cref="ErrorCodes.RecurringScheduleInvalid"/>): the template
    /// must exist, belong to the company, be a sales order or sales
    /// invoice in Draft or Posted status and carry at least one line; the
    /// interval must be a known type with a positive count; the end date
    /// (when given) must not precede the start; the occurrence cap (when
    /// given) must be positive.
    /// </summary>
    public async Task<RecurSchedule> CreateScheduleAsync(
        string companyId, long templateDocId, string intervalType, int intervalCount,
        DateOnly startDate, DateOnly? endDate, int? maxOccurrences, bool autoPost,
        string userId, CancellationToken cancellationToken = default)
    {
        var company = RequireCompany(companyId);
        RequireUser(userId);
        if (templateDocId <= 0)
            throw Invalid("a template document is required", templateDocId.ToString(CultureInfo.InvariantCulture));
        if (!RecurIntervalTypes.IsValid(intervalType))
            throw Invalid($"interval type '{intervalType}' is not supported", intervalType ?? string.Empty);
        if (intervalCount < 1)
            throw Invalid("the interval count must be positive", intervalCount.ToString(CultureInfo.InvariantCulture));
        if (endDate is { } end && end < startDate)
            throw Invalid("the end date precedes the start date", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (maxOccurrences is { } max && max < 1)
            throw Invalid("the maximum occurrences must be positive", max.ToString(CultureInfo.InvariantCulture));

        var template = await _documents.FindSalesByIdAsync(templateDocId, cancellationToken).ConfigureAwait(false)
            ?? throw Invalid($"template document '{templateDocId}' was not found", templateDocId.ToString(CultureInfo.InvariantCulture));
        if (template.DocKind is not (CommercialDocKinds.SalesOrder or CommercialDocKinds.SalesInvoice))
            throw Invalid($"only sales orders and sales invoices can recur; '{template.DocNo}' is a '{template.DocKind}'", template.DocNo);
        if (template.Status is not (CommercialDocumentStatus.Draft or CommercialDocumentStatus.Posted))
            throw Invalid($"template document '{template.DocNo}' must be a draft or posted document", template.DocNo);
        if (template.Lines.Count == 0)
            throw Invalid($"template document '{template.DocNo}' has no lines to clone", template.DocNo);
        var templateCompany = await _repository.ResolveCompanyCodeAsync(
            template.CompanyId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(templateCompany, company, StringComparison.OrdinalIgnoreCase))
            throw Invalid($"template document '{template.DocNo}' belongs to another company", template.DocNo);

        var schedule = new RecurSchedule
        {
            TemplateDocId = template.DocId,
            TemplateDocKind = template.DocKind,
            TemplateDocNo = template.DocNo,
            IntervalType = intervalType,
            IntervalCount = intervalCount,
            StartDate = startDate,
            NextRunDate = startDate,
            EndDate = endDate,
            MaxOccurrences = maxOccurrences,
            AutoPost = autoPost,
            Status = RecurScheduleStatus.Active
        };
        await _repository.InsertScheduleAsync(company, schedule, userId, cancellationToken).ConfigureAwait(false);
        return schedule;
    }

    public async Task PauseScheduleAsync(string companyId, Guid scheduleId, string userId, CancellationToken cancellationToken = default)
    {
        RequireUser(userId);
        var schedule = await RequireScheduleAsync(companyId, scheduleId, cancellationToken).ConfigureAwait(false);
        if (schedule.Status != RecurScheduleStatus.Active)
            throw Invalid($"only an active schedule can be paused; this one is '{schedule.Status}'", schedule.TemplateDocNo);
        await _repository.SetStatusAsync(RequireCompany(companyId), scheduleId, RecurScheduleStatus.Active, RecurScheduleStatus.Paused, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeScheduleAsync(string companyId, Guid scheduleId, string userId, CancellationToken cancellationToken = default)
    {
        RequireUser(userId);
        var schedule = await RequireScheduleAsync(companyId, scheduleId, cancellationToken).ConfigureAwait(false);
        if (schedule.Status != RecurScheduleStatus.Paused)
            throw Invalid($"only a paused schedule can be resumed; this one is '{schedule.Status}'", schedule.TemplateDocNo);
        await _repository.SetStatusAsync(RequireCompany(companyId), scheduleId, RecurScheduleStatus.Paused, RecurScheduleStatus.Active, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelScheduleAsync(string companyId, Guid scheduleId, string userId, CancellationToken cancellationToken = default)
    {
        RequireUser(userId);
        var schedule = await RequireScheduleAsync(companyId, scheduleId, cancellationToken).ConfigureAwait(false);
        if (schedule.Status is not (RecurScheduleStatus.Active or RecurScheduleStatus.Paused))
            throw Invalid($"a '{schedule.Status}' schedule can no longer be cancelled", schedule.TemplateDocNo);
        await _repository.SetStatusAsync(RequireCompany(companyId), scheduleId, schedule.Status, RecurScheduleStatus.Cancelled, userId, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Generation
    // ------------------------------------------------------------------
    /// <summary>
    /// Generates every due occurrence of every Active schedule (all
    /// companies) with next_run_date ≤ <paramref name="asOf"/>, catching up
    /// missed occurrences one by one. Safe to call repeatedly: a second
    /// call for the same date creates nothing new.
    /// </summary>
    public async Task<RecurGenerationSummary> GenerateDueAsync(DateOnly asOf, string userId, CancellationToken cancellationToken = default)
    {
        RequireUser(userId);
        var due = await _repository.GetDueSchedulesAsync(asOf, cancellationToken).ConfigureAwait(false);
        var created = 0;
        var posted = 0;
        var failures = 0;
        var completed = 0;
        foreach (var (companyId, schedule) in due)
        {
            var result = await GenerateScheduleOccurrencesAsync(companyId, schedule, asOf, forceOne: false, userId, cancellationToken).ConfigureAwait(false);
            created += result.Created;
            posted += result.Posted;
            failures += result.Failures;
            if (result.Completed) completed++;
        }
        return new RecurGenerationSummary(due.Count, created, posted, failures, completed);
    }

    /// <summary>
    /// Run Now: generates the schedule's pending occurrence immediately,
    /// even when its next run date is still in the future (the occurrence
    /// keeps its scheduled date as the document date base). Returns the
    /// same tally shape as a due sweep, for this one schedule.
    /// </summary>
    public async Task<RecurGenerationSummary> RunScheduleNowAsync(string companyId, Guid scheduleId, DateOnly asOf, string userId, CancellationToken cancellationToken = default)
    {
        RequireUser(userId);
        var schedule = await RequireScheduleAsync(companyId, scheduleId, cancellationToken).ConfigureAwait(false);
        if (schedule.Status != RecurScheduleStatus.Active)
            throw Invalid($"only an active schedule can run; this one is '{schedule.Status}'", schedule.TemplateDocNo);
        var result = await GenerateScheduleOccurrencesAsync(RequireCompany(companyId), schedule, asOf, forceOne: true, userId, cancellationToken).ConfigureAwait(false);
        return new RecurGenerationSummary(1, result.Created, result.Posted, result.Failures, result.Completed ? 1 : 0);
    }

    private sealed record ScheduleRunResult(int Created, int Posted, int Failures, bool Completed);

    private async Task<ScheduleRunResult> GenerateScheduleOccurrencesAsync(
        string companyId, RecurSchedule schedule, DateOnly asOf, bool forceOne, string userId, CancellationToken cancellationToken)
    {
        var created = 0;
        var posted = 0;
        var failures = 0;
        var completedNow = false;

        for (var guard = 0; guard < MaxOccurrencesPerPass; guard++)
        {
            if (schedule.Status != RecurScheduleStatus.Active)
                break;
            var runDate = schedule.NextRunDate;
            if (runDate > asOf && !(forceOne && created == 0 && failures == 0))
                break;

            // End conditions are evaluated BEFORE the occurrence exists:
            // a run date past the end date (or beyond the cap) completes
            // the schedule without generating anything.
            if (schedule.EndDate is { } endDate && runDate > endDate)
            {
                await _repository.SetStatusAsync(companyId, schedule.Id, RecurScheduleStatus.Active, RecurScheduleStatus.Completed, userId, cancellationToken).ConfigureAwait(false);
                schedule.Status = RecurScheduleStatus.Completed;
                completedNow = true;
                break;
            }
            var occurrenceNo = schedule.OccurrencesCreated + 1;
            if (schedule.MaxOccurrences is { } cap && occurrenceNo > cap)
            {
                await _repository.SetStatusAsync(companyId, schedule.Id, RecurScheduleStatus.Active, RecurScheduleStatus.Completed, userId, cancellationToken).ConfigureAwait(false);
                schedule.Status = RecurScheduleStatus.Completed;
                completedNow = true;
                break;
            }

            var nextRun = RecurScheduleMath.Advance(runDate, schedule.IntervalType, schedule.IntervalCount);
            var completes = (schedule.MaxOccurrences is { } maxCap && occurrenceNo >= maxCap)
                            || (schedule.EndDate is { } end && nextRun > end);
            // next_run_date stays populated even when this occurrence
            // completes the schedule (the column is NOT NULL; the status
            // is what stops further generation).
            var runId = await _repository.ClaimOccurrenceAsync(companyId, schedule.Id, occurrenceNo, runDate,
                nextRun, completes, userId, cancellationToken).ConfigureAwait(false);
            if (runId is null)
            {
                // Already claimed by an earlier/concurrent pass: the claim
                // transaction advanced the stored schedule, so reload and
                // re-evaluate instead of cloning a duplicate.
                var reloaded = await _repository.GetScheduleAsync(companyId, schedule.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Recurring schedule '{schedule.Id}' disappeared during generation.");
                schedule.OccurrencesCreated = reloaded.OccurrencesCreated;
                schedule.NextRunDate = reloaded.NextRunDate;
                schedule.Status = reloaded.Status;
                continue;
            }
            schedule.OccurrencesCreated = occurrenceNo;
            schedule.LastRunDate = runDate;
            if (completes)
            {
                schedule.Status = RecurScheduleStatus.Completed;
                completedNow = true;
            }
            else
            {
                schedule.NextRunDate = nextRun;
            }

            // ---- Clone the template into a new draft of the same kind.
            long newDocId;
            try
            {
                newDocId = await CloneTemplateAsync(schedule, runDate, userId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Recorded on the run AND the schedule — never silent. The
                // occurrence stays claimed (the schedule already advanced),
                // so a retry pass cannot double-create it later.
                await _repository.FailOccurrenceAsync(companyId, runId.Value, TrimError(ex.Message), userId, cancellationToken).ConfigureAwait(false);
                failures++;
                if (forceOne) break;
                continue;
            }
            created++;

            // ---- Optional auto-post through the normal post path.
            var outcome = RecurRunOutcome.Draft;
            string? error = null;
            if (schedule.AutoPost)
            {
                try
                {
                    await _documents.PostSalesAsync(newDocId, userId, cancellationToken).ConfigureAwait(false);
                    outcome = RecurRunOutcome.Posted;
                    posted++;
                }
                catch (Exception ex)
                {
                    // The draft REMAINS; the failure is recorded on the run
                    // and the schedule so an operator can post it manually.
                    outcome = RecurRunOutcome.PostFailed;
                    error = TrimError(ex.Message);
                    failures++;
                }
            }
            await _repository.CompleteOccurrenceAsync(companyId, runId.Value, newDocId, outcome, error, userId, cancellationToken).ConfigureAwait(false);
            schedule.LastGeneratedDocId = newDocId;
            schedule.LastRunStatus = error is null ? outcome : $"{outcome}: {error}";

            if (forceOne)
                break;
        }
        return new ScheduleRunResult(created, posted, failures, completedNow);
    }

    /// <summary>
    /// Clones the template's header + lines into a new draft of the same
    /// kind via the normal creation path. The new document takes the
    /// occurrence's run date, a fresh number from the numbering service,
    /// the header discount / extra amounts in full (a recurring clone is
    /// a FULL copy, unlike a partial chain conversion), and a note naming
    /// its schedule. The template itself is never modified.
    /// </summary>
    private async Task<long> CloneTemplateAsync(RecurSchedule schedule, DateOnly runDate, string userId, CancellationToken cancellationToken)
    {
        var template = await _documents.FindSalesByIdAsync(schedule.TemplateDocId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Template document '{schedule.TemplateDocId}' no longer exists.");
        if (template.DocKind != schedule.TemplateDocKind)
            throw new InvalidOperationException($"Template document '{template.DocNo}' changed kind from '{schedule.TemplateDocKind}' to '{template.DocKind}'.");
        if (template.Lines.Count == 0)
            throw new InvalidOperationException($"Template document '{template.DocNo}' has no lines to clone.");

        var docNo = await _orders.NextDocNoAsync(CommercialDocSide.Sales, template.CompanyId, template.DocKind, cancellationToken).ConfigureAwait(false);

        var lines = new List<CommercialLineDraft>(template.Lines.Count);
        foreach (var line in template.Lines)
        {
            lines.Add(new CommercialLineDraft(
                line.MaterialId,
                line.MaterialCode,
                line.Description,
                line.Unit,
                line.Qty,
                line.UnitPrice,
                line.DiscountPct,
                line.TaxRate,
                line.WarehouseId,
                line.LineNo));
        }

        var note = string.IsNullOrWhiteSpace(template.Notes)
            ? $"Recurring: generated from template {template.DocNo} by schedule {schedule.Id:D}."
            : $"{template.Notes} | Recurring: generated from template {template.DocNo} by schedule {schedule.Id:D}.";

        var draft = new CommercialDocumentDraft(
            template.CompanyId,
            template.DocKind,
            docNo,
            runDate,
            template.PartyId,
            template.PartyName,
            template.WarehouseId,
            template.CurrencyCode,
            template.ExchangeRate,
            note,
            lines,
            template.PartyCode,
            template.DeliveryDate,
            template.ValidTill,
            template.CostCenter,
            template.BranchName,
            template.SalesRepName,
            template.WarehouseName,
            template.DiscountAmount,
            template.ExtraAmount,
            template.DeliveryCost);

        var newDocId = await _documents.CreateSalesDraftAsync(draft, userId, cancellationToken).ConfigureAwait(false);

        // Chain-link convention (migration 024): template -> generated.
        await _documents.AddLinkAsync(template.CompanyId, template.DocKind, template.DocId,
            template.DocKind, newDocId, userId, cancellationToken).ConfigureAwait(false);
        return newDocId;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private async Task<RecurSchedule> RequireScheduleAsync(string companyId, Guid scheduleId, CancellationToken cancellationToken)
    {
        var schedule = await _repository.GetScheduleAsync(RequireCompany(companyId), scheduleId, cancellationToken).ConfigureAwait(false);
        return schedule ?? throw Invalid($"schedule '{scheduleId}' was not found", scheduleId.ToString("D"));
    }

    private static AppError Invalid(string detail, string arg)
        => new(ErrorCodes.RecurringScheduleInvalid, $"The recurring schedule is invalid: {detail}.", arg);

    private static string TrimError(string message)
        => message.Length <= 500 ? message : message[..500];

    private static string RequireCompany(string companyId)
        => string.IsNullOrWhiteSpace(companyId)
            ? throw new ArgumentException("Company is required.", nameof(companyId))
            : companyId.Trim();

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User is required.", nameof(userId));
    }
}
