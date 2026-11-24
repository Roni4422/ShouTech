using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// CRM application service (Phase B 057). All invariants are validated
/// here before the repository is touched (the repository re-checks
/// company scope and state inside its transactions as a second line of
/// defence):
///   * a lead carries a name and a known status; conversion is refused
///     for converted / unqualified leads (CRM-9001 / CRM-9002);
///   * an opportunity carries a title, a non-negative amount and a
///     probability inside 0-100; its stage must exist (CRM-9003);
///   * an activity targets EXACTLY ONE of lead / opportunity / party
///     (CRM-9005) and carries a subject, a known type and a due date;
///   * moving into a Lost stage requires a lost reason (CRM-9006).
///
/// NO entitlement gate is called here: licensing/entitlement work is
/// postponed by the owner, so CRM runs under the CRM.* RBAC
/// permissions only. Entitlement gating arrives with the postponed
/// licensing work — no fake gate stands in its place.
/// </summary>
public sealed class CrmService
{
    private readonly ICrmRepository _repository;

    public CrmService(ICrmRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    // ---- Stages / lost reasons -------------------------------------------
    public Task<IReadOnlyList<CrmStage>> GetStagesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetStagesAsync(RequireCompany(companyId), cancellationToken);

    public Task SaveStageAsync(string companyId, CrmStage stage, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (string.IsNullOrWhiteSpace(stage.NameEn) && string.IsNullOrWhiteSpace(stage.NameAr))
            throw new InvalidOperationException("Stage name is required.");
        if (!CrmStageTypes.All.Contains(stage.StageType))
            throw new InvalidOperationException($"Unknown stage type '{stage.StageType}'.");
        if (stage.Probability is < 0m or > 100m)
            throw new InvalidOperationException("Stage probability must be between 0 and 100.");
        return _repository.SaveStageAsync(RequireCompany(companyId), stage, RequireUser(userId), cancellationToken);
    }

    public Task DeleteStageAsync(string companyId, Guid stageId, string userId, CancellationToken cancellationToken = default)
    {
        if (stageId == Guid.Empty) throw new ArgumentException("Stage is required.", nameof(stageId));
        return _repository.SoftDeleteStageAsync(RequireCompany(companyId), stageId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<CrmLostReason>> GetLostReasonsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetLostReasonsAsync(RequireCompany(companyId), cancellationToken);

    public Task SaveLostReasonAsync(string companyId, CrmLostReason reason, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (string.IsNullOrWhiteSpace(reason.NameEn) && string.IsNullOrWhiteSpace(reason.NameAr))
            throw new InvalidOperationException("Lost reason name is required.");
        return _repository.SaveLostReasonAsync(RequireCompany(companyId), reason, RequireUser(userId), cancellationToken);
    }

    // ---- Leads --------------------------------------------------------------
    public Task<IReadOnlyList<CrmLead>> GetLeadsAsync(string companyId, string? status = null, CancellationToken cancellationToken = default)
        => _repository.GetLeadsAsync(RequireCompany(companyId), status, cancellationToken);

    public Task<CrmLead?> GetLeadAsync(string companyId, Guid leadId, CancellationToken cancellationToken = default)
    {
        if (leadId == Guid.Empty) throw new ArgumentException("Lead is required.", nameof(leadId));
        return _repository.GetLeadAsync(RequireCompany(companyId), leadId, cancellationToken);
    }

    public async Task SaveLeadAsync(string companyId, CrmLead lead, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (string.IsNullOrWhiteSpace(lead.LeadName))
            throw new InvalidOperationException("Lead name is required.");
        if (!CrmLeadStatuses.All.Contains(lead.Status))
            throw new InvalidOperationException($"Unknown lead status '{lead.Status}'.");
        if (lead.Status == CrmLeadStatuses.Converted)
            throw new AppError(ErrorCodes.CrmLeadStateInvalid,
                "A lead becomes Converted only through conversion, never by editing its status.", lead.LeadName);
        lead.LeadName = lead.LeadName.Trim();
        var company = RequireCompany(companyId);
        // Duplicate detection (gap G1, CRM-9007): the repository
        // re-checks inside its save transaction.
        var duplicate = await _repository.FindLeadDuplicateAsync(company, lead, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
            throw DuplicateError(lead, duplicate);
        await _repository.SaveLeadAsync(company, lead, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds a duplicate for a lead (same email or phone on
    /// another lead or an existing party); null when there is none.
    /// The lead window uses this to offer the link-existing path.</summary>
    public Task<CrmDuplicateMatch?> FindLeadDuplicateAsync(string companyId, CrmLead lead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lead);
        return _repository.FindLeadDuplicateAsync(RequireCompany(companyId), lead, cancellationToken);
    }

    internal static AppError DuplicateError(CrmLead lead, CrmDuplicateMatch duplicate)
        => new(ErrorCodes.CrmDuplicateLeadOrParty,
            $"Lead '{lead.LeadName}' duplicates an existing record: {duplicate.Describe()}. Link the existing record instead of creating a duplicate.",
            duplicate.Describe());

    public Task DeleteLeadAsync(string companyId, Guid leadId, string userId, CancellationToken cancellationToken = default)
    {
        if (leadId == Guid.Empty) throw new ArgumentException("Lead is required.", nameof(leadId));
        return _repository.SoftDeleteLeadAsync(RequireCompany(companyId), leadId, RequireUser(userId), cancellationToken);
    }

    public async Task<CrmConversionResult> ConvertLeadAsync(string companyId, Guid leadId, string userId, string currencyCode, CancellationToken cancellationToken = default)
    {
        if (leadId == Guid.Empty) throw new ArgumentException("Lead is required.", nameof(leadId));
        var company = RequireCompany(companyId);
        // Duplicate detection on convert (gap G1, CRM-9007): if the
        // lead's email/phone already exists as another lead or a
        // party, conversion into a NEW party is refused — the caller
        // links the existing party instead (ConvertLeadToExistingPartyAsync).
        var lead = await _repository.GetLeadAsync(company, leadId, cancellationToken).ConfigureAwait(false);
        if (lead is not null)
        {
            var duplicate = await _repository.FindLeadDuplicateAsync(company, lead, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
                throw DuplicateError(lead, duplicate);
        }
        return await _repository.ConvertLeadAsync(company, leadId, RequireUser(userId),
            string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Converts a lead by LINKING an existing party (gap G2):
    /// no new party is created; the opportunity is created against
    /// <paramref name="existingPartyId"/>.</summary>
    public Task<CrmConversionResult> ConvertLeadToExistingPartyAsync(string companyId, Guid leadId, Guid existingPartyId, string userId, string currencyCode, CancellationToken cancellationToken = default)
    {
        if (leadId == Guid.Empty) throw new ArgumentException("Lead is required.", nameof(leadId));
        if (existingPartyId == Guid.Empty) throw new ArgumentException("Existing party is required.", nameof(existingPartyId));
        return _repository.ConvertLeadToExistingPartyAsync(RequireCompany(companyId), leadId, existingPartyId, RequireUser(userId),
            string.IsNullOrWhiteSpace(currencyCode) ? "SAR" : currencyCode.Trim(), cancellationToken);
    }

    // ---- Opportunities ---------------------------------------------------------
    public Task<IReadOnlyList<CrmOpportunity>> GetOpportunitiesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetOpportunitiesAsync(RequireCompany(companyId), cancellationToken);

    public Task<CrmOpportunity?> GetOpportunityAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        return _repository.GetOpportunityAsync(RequireCompany(companyId), oppId, cancellationToken);
    }

    public Task SaveOpportunityAsync(string companyId, CrmOpportunity opportunity, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        if (string.IsNullOrWhiteSpace(opportunity.Title))
            throw new InvalidOperationException("Opportunity title is required.");
        if (opportunity.ExpectedAmount < 0m)
            throw new InvalidOperationException("Expected amount cannot be negative.");
        if (opportunity.Probability is < 0m or > 100m)
            throw new InvalidOperationException("Probability must be between 0 and 100.");
        if (opportunity.OppType is not null && !CrmOpportunityTypes.All.Contains(opportunity.OppType))
            throw new InvalidOperationException($"Unknown opportunity type '{opportunity.OppType}'.");
        // Items (migration 092, CRM-9008): qty > 0, price >= 0,
        // discount 0-100, tax >= 0, description required. When items
        // exist the expected amount IS the items total.
        foreach (var item in opportunity.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Description))
                throw new AppError(ErrorCodes.CrmOpportunityItemInvalid,
                    "Every opportunity item requires a description.", opportunity.Title);
            if (item.Qty <= 0m)
                throw new AppError(ErrorCodes.CrmOpportunityItemInvalid,
                    $"Opportunity item '{item.Description}' must have a quantity greater than zero.", item.Description);
            if (item.UnitPrice < 0m)
                throw new AppError(ErrorCodes.CrmOpportunityItemInvalid,
                    $"Opportunity item '{item.Description}' cannot have a negative price.", item.Description);
            if (item.DiscountPct is < 0m or > 100m)
                throw new AppError(ErrorCodes.CrmOpportunityItemInvalid,
                    $"Opportunity item '{item.Description}' has a discount outside 0-100%.", item.Description);
            if (item.TaxRate < 0m)
                throw new AppError(ErrorCodes.CrmOpportunityItemInvalid,
                    $"Opportunity item '{item.Description}' cannot have a negative tax rate.", item.Description);
        }
        if (opportunity.Items.Count > 0)
            opportunity.ExpectedAmount = opportunity.Items.Sum(i => i.LineTotal);
        opportunity.Title = opportunity.Title.Trim();
        return _repository.SaveOpportunityAsync(RequireCompany(companyId), opportunity, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<CrmOpportunityItem>> GetOpportunityItemsAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        return _repository.GetOpportunityItemsAsync(RequireCompany(companyId), oppId, cancellationToken);
    }

    public Task DeleteOpportunityAsync(string companyId, Guid oppId, string userId, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        return _repository.SoftDeleteOpportunityAsync(RequireCompany(companyId), oppId, RequireUser(userId), cancellationToken);
    }

    public Task MoveStageAsync(string companyId, Guid oppId, Guid toStageId, decimal? probability,
        Guid? lostReasonId, string? lostNotes, string userId, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        if (toStageId == Guid.Empty) throw new ArgumentException("Target stage is required.", nameof(toStageId));
        if (probability is < 0m or > 100m)
            throw new InvalidOperationException("Probability must be between 0 and 100.");
        return _repository.MoveStageAsync(RequireCompany(companyId), oppId, toStageId, probability,
            lostReasonId, lostNotes, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<CrmStageHistoryEntry>> GetStageHistoryAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        return _repository.GetStageHistoryAsync(RequireCompany(companyId), oppId, cancellationToken);
    }

    public Task<CrmPipelineBoard> GetPipelineAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetPipelineAsync(RequireCompany(companyId), cancellationToken);

    // ---- Documents ---------------------------------------------------------------
    public Task<CrmQuotationResult> CreateQuotationAsync(string companyId, Guid oppId, string userId,
        IReadOnlyList<CrmQuotationLine>? lines = null, DateOnly? validTill = null, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        if (lines is not null)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line.Description))
                    throw new InvalidOperationException("Quotation lines require a description.");
                if (line.Qty <= 0m)
                    throw new InvalidOperationException("Quotation line quantities must be greater than zero.");
                if (line.UnitPrice < 0m)
                    throw new InvalidOperationException("Quotation line prices cannot be negative.");
                if (line.DiscountPct is < 0m or > 100m)
                    throw new InvalidOperationException("Quotation line discounts must be between 0 and 100.");
                if (line.TaxRate < 0m)
                    throw new InvalidOperationException("Quotation line tax rates cannot be negative.");
            }
        }
        return _repository.CreateQuotationAsync(RequireCompany(companyId), oppId, RequireUser(userId), lines, validTill, cancellationToken);
    }

    public Task LinkDocumentAsync(string companyId, Guid oppId, string linkType, long docId, string userId, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        if (docId <= 0) throw new ArgumentException("Document is required.", nameof(docId));
        if (!CrmDocumentLinkTypes.All.Contains(linkType))
            throw new ArgumentException($"Unknown CRM document link type '{linkType}'.", nameof(linkType));
        return _repository.LinkDocumentAsync(RequireCompany(companyId), oppId, linkType, docId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<CrmOpportunityDocument>> GetOpportunityDocumentsAsync(string companyId, Guid oppId, CancellationToken cancellationToken = default)
    {
        if (oppId == Guid.Empty) throw new ArgumentException("Opportunity is required.", nameof(oppId));
        return _repository.GetOpportunityDocumentsAsync(RequireCompany(companyId), oppId, cancellationToken);
    }

    // ---- Activities ------------------------------------------------------------------
    public Task<IReadOnlyList<CrmActivity>> GetActivitiesAsync(string companyId, bool openOnly = false, CancellationToken cancellationToken = default)
        => _repository.GetActivitiesAsync(RequireCompany(companyId), openOnly, cancellationToken);

    public Task<IReadOnlyList<CrmActivity>> GetOverdueActivitiesAsync(string companyId, string asOf, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(asOf)) throw new ArgumentException("An as-of moment is required.", nameof(asOf));
        return _repository.GetOverdueActivitiesAsync(RequireCompany(companyId), asOf.Trim(), cancellationToken);
    }

    public Task<IReadOnlyList<CrmActivity>> GetActivitiesForAsync(string companyId, Guid? leadId, Guid? oppId, Guid? partyId, CancellationToken cancellationToken = default)
        => _repository.GetActivitiesForAsync(RequireCompany(companyId), leadId, oppId, partyId, cancellationToken);

    public Task SaveActivityAsync(string companyId, CrmActivity activity, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (string.IsNullOrWhiteSpace(activity.Subject))
            throw new InvalidOperationException("Activity subject is required.");
        if (!CrmActivityTypes.All.Contains(activity.ActivityType))
            throw new InvalidOperationException($"Unknown activity type '{activity.ActivityType}'.");
        if (!CrmActivityStatuses.All.Contains(activity.Status))
            throw new InvalidOperationException($"Unknown activity status '{activity.Status}'.");
        if (string.IsNullOrWhiteSpace(activity.DueAt))
            throw new InvalidOperationException("Activity due date is required.");
        var targets = (activity.LeadId is null ? 0 : 1) + (activity.OppId is null ? 0 : 1) + (activity.PartyId is null ? 0 : 1);
        if (targets != 1)
            throw new AppError(ErrorCodes.CrmActivityTargetInvalid,
                $"Activity '{activity.Subject}' must target exactly one of a lead, an opportunity or a party.", activity.Subject);
        activity.Subject = activity.Subject.Trim();
        return _repository.SaveActivityAsync(RequireCompany(companyId), activity, RequireUser(userId), cancellationToken);
    }

    public Task CompleteActivityAsync(string companyId, Guid activityId, string? resultNotes, string userId, CancellationToken cancellationToken = default)
    {
        if (activityId == Guid.Empty) throw new ArgumentException("Activity is required.", nameof(activityId));
        return _repository.CompleteActivityAsync(RequireCompany(companyId), activityId, resultNotes, RequireUser(userId), cancellationToken);
    }

    public Task DeleteActivityAsync(string companyId, Guid activityId, string userId, CancellationToken cancellationToken = default)
    {
        if (activityId == Guid.Empty) throw new ArgumentException("Activity is required.", nameof(activityId));
        return _repository.SoftDeleteActivityAsync(RequireCompany(companyId), activityId, RequireUser(userId), cancellationToken);
    }

    // ---- Lookups ------------------------------------------------------------------------
    public Task<IReadOnlyList<CrmPartyOption>> GetPartyOptionsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetPartyOptionsAsync(RequireCompany(companyId), cancellationToken);

    public Task<IReadOnlyList<CrmRepOption>> GetRepOptionsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetRepOptionsAsync(RequireCompany(companyId), cancellationToken);

    private static string RequireCompany(string companyId)
        => string.IsNullOrWhiteSpace(companyId)
            ? throw new ArgumentException("Company is required.", nameof(companyId))
            : companyId.Trim();

    private static string RequireUser(string userId)
        => string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("User is required.", nameof(userId))
            : userId.Trim();
}
