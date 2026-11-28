using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Quality application service (Phase B 056). All invariants are
/// validated here before the repository is touched (the repository and
/// the gate re-check company scope and state inside their transactions
/// as a second line of defence):
///   * parameters carry a code, a name, and a known kind; template
///     lines carry criteria that match their parameter's kind (a
///     numeric band for Numeric, an accepted value for Value, neither
///     for PassFail — formula criteria are deferred, decision Q3);
///   * an inspection's readings are at most 3 per line (v1 rule), and
///     a manual result that disagrees with the auto-evaluation is an
///     override and must be flagged as one;
///   * completion quantities are non-negative, and a rejected part
///     requires a disposition.
/// </summary>
public sealed class QualityService
{
    private readonly IQualityRepository _repository;

    public QualityService(IQualityRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    // ---- Gate mode ------------------------------------------------------
    public Task<string> GetGateModeAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetGateModeAsync(RequireCompany(companyId), cancellationToken);

    public Task SetGateModeAsync(string companyId, string gateMode, string userId, CancellationToken cancellationToken = default)
    {
        if (!QualityGateModes.All.Contains(gateMode))
            throw new InvalidOperationException($"Unknown QC gate mode '{gateMode}'.");
        return _repository.SetGateModeAsync(RequireCompany(companyId), gateMode, RequireUser(userId), cancellationToken);
    }

    // ---- Parameters / templates -------------------------------------------
    public Task<IReadOnlyList<QualityParameter>> GetParametersAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetParametersAsync(RequireCompany(companyId), cancellationToken);

    public async Task SaveParameterAsync(string companyId, QualityParameter parameter, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (string.IsNullOrWhiteSpace(parameter.Code))
            throw new InvalidOperationException("Parameter code is required.");
        if (string.IsNullOrWhiteSpace(parameter.NameEn) && string.IsNullOrWhiteSpace(parameter.NameAr))
            throw new InvalidOperationException("Parameter name is required.");
        if (!QualityParamTypes.All.Contains(parameter.ParamType))
            throw new InvalidOperationException($"Unknown parameter type '{parameter.ParamType}'.");
        parameter.Code = parameter.Code.Trim();
        await _repository.SaveParameterAsync(RequireCompany(companyId), parameter, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteParameterAsync(string companyId, Guid parameterId, string userId, CancellationToken cancellationToken = default)
    {
        if (parameterId == Guid.Empty) throw new ArgumentException("Parameter is required.", nameof(parameterId));
        return _repository.SoftDeleteParameterAsync(RequireCompany(companyId), parameterId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<QualityTemplate>> GetTemplatesAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetTemplatesAsync(RequireCompany(companyId), cancellationToken);

    public async Task SaveTemplateAsync(string companyId, QualityTemplate template, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrWhiteSpace(template.Code))
            throw new InvalidOperationException("Template code is required.");
        if (!QualityInspectionTypes.All.Contains(template.InspectionType))
            throw new InvalidOperationException($"Unknown inspection type '{template.InspectionType}'.");
        var parameters = await _repository.GetParametersAsync(RequireCompany(companyId), cancellationToken).ConfigureAwait(false);
        var byId = parameters.ToDictionary(p => p.Id);
        var seen = new HashSet<Guid>();
        foreach (var line in template.Lines)
        {
            if (!byId.TryGetValue(line.ParamId, out var parameter))
                throw new AppError(ErrorCodes.QualityTemplateInvalid,
                    $"Template '{template.Code}' references an unknown parameter.", template.Code);
            if (!seen.Add(line.ParamId))
                throw new AppError(ErrorCodes.QualityTemplateInvalid,
                    $"Template '{template.Code}' carries parameter '{parameter.Code}' twice.", template.Code, parameter.Code);
            switch (parameter.ParamType)
            {
                case QualityParamTypes.Numeric:
                    if (line.MinValue is null && line.MaxValue is null)
                        throw new AppError(ErrorCodes.QualityTemplateInvalid,
                            $"Numeric parameter '{parameter.Code}' needs a minimum, a maximum, or both.", parameter.Code);
                    if (line.MinValue is not null && line.MaxValue is not null && line.MinValue > line.MaxValue)
                        throw new AppError(ErrorCodes.QualityTemplateInvalid,
                            $"Parameter '{parameter.Code}' has a minimum above its maximum.", parameter.Code);
                    break;
                case QualityParamTypes.Value:
                    if (string.IsNullOrWhiteSpace(line.AcceptedValue))
                        throw new AppError(ErrorCodes.QualityTemplateInvalid,
                            $"Value parameter '{parameter.Code}' needs an accepted value.", parameter.Code);
                    break;
                default:
                    if (line.MinValue is not null || line.MaxValue is not null || !string.IsNullOrWhiteSpace(line.AcceptedValue))
                        throw new AppError(ErrorCodes.QualityTemplateInvalid,
                            $"Pass/fail parameter '{parameter.Code}' carries no acceptance criteria.", parameter.Code);
                    break;
            }
        }
        template.Code = template.Code.Trim();
        await _repository.SaveTemplateAsync(companyId, template, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteTemplateAsync(string companyId, Guid templateId, string userId, CancellationToken cancellationToken = default)
    {
        if (templateId == Guid.Empty) throw new ArgumentException("Template is required.", nameof(templateId));
        return _repository.SoftDeleteTemplateAsync(RequireCompany(companyId), templateId, RequireUser(userId), cancellationToken);
    }

    public Task<IReadOnlyList<QualityMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default)
        => _repository.GetMaterialOptionsAsync(RequireCompany(companyId), cancellationToken);

    public Task SetMaterialQualityAsync(string companyId, long materialId, bool qualityCheckRequired,
        bool outgoingRequired, Guid? templateInId, Guid? templateOutId, string userId,
        CancellationToken cancellationToken = default)
    {
        if (materialId <= 0) throw new ArgumentException("Material is required.", nameof(materialId));
        return _repository.SetMaterialQualityAsync(RequireCompany(companyId), materialId,
            qualityCheckRequired, outgoingRequired, templateInId, templateOutId, RequireUser(userId), cancellationToken);
    }

    // ---- Inspections -------------------------------------------------------
    public Task<IReadOnlyList<QualityInspection>> GetInspectionsAsync(string companyId, bool pendingOnly = false, CancellationToken cancellationToken = default)
        => _repository.GetInspectionsAsync(RequireCompany(companyId), pendingOnly, cancellationToken);

    public Task<QualityInspection?> GetInspectionAsync(string companyId, Guid inspectionId, CancellationToken cancellationToken = default)
    {
        if (inspectionId == Guid.Empty) throw new ArgumentException("Inspection is required.", nameof(inspectionId));
        return _repository.GetInspectionAsync(RequireCompany(companyId), inspectionId, cancellationToken);
    }

    public Task<Guid> CreateInspectionAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (!QualityInspectionTypes.All.Contains(inspection.InspectionType))
            throw new InvalidOperationException($"Unknown inspection type '{inspection.InspectionType}'.");
        if (inspection.MaterialId <= 0)
            throw new InvalidOperationException("An inspection requires a material.");
        if (string.IsNullOrWhiteSpace(inspection.RefDocNo))
            throw new InvalidOperationException("An inspection requires a reference document number.");
        return _repository.CreateInspectionAsync(RequireCompany(companyId), inspection, RequireUser(userId), cancellationToken);
    }

    public async Task SaveResultsAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        foreach (var line in inspection.Lines)
        {
            if (line.Readings.Count > 3)
                throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                    $"Line '{line.ParamCode}' carries {line.Readings.Count} readings; at most 3 are supported.",
                    line.ParamCode);
            if (line.Readings.Select(r => r.ReadingNo).Distinct().Count() != line.Readings.Count
                || line.Readings.Any(r => r.ReadingNo is < 1 or > 3))
                throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                    $"Line '{line.ParamCode}' has duplicate or out-of-range reading numbers.", line.ParamCode);
            if (line.Result is not null && line.Result is not "Pass" and not "Fail")
                throw new AppError(ErrorCodes.QualityInspectionStateInvalid,
                    $"Line '{line.ParamCode}' has an unknown result '{line.Result}'.", line.ParamCode, line.Result);
        }
        await _repository.SaveResultsAsync(RequireCompany(companyId), inspection, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public Task<QualityCompletionResult> CompleteAsync(string companyId, Guid inspectionId,
        decimal acceptedQty, decimal rejectedQty, string? disposition,
        string inspector, string? notes, CancellationToken cancellationToken = default)
    {
        if (inspectionId == Guid.Empty) throw new ArgumentException("Inspection is required.", nameof(inspectionId));
        if (acceptedQty < 0 || rejectedQty < 0)
            throw new AppError(ErrorCodes.QualityInspectionQtyMismatch,
                "Accepted and rejected quantities cannot be negative.");
        if (rejectedQty > 0 && (string.IsNullOrWhiteSpace(disposition) || !QualityDispositions.All.Contains(disposition)))
            throw new AppError(ErrorCodes.QualityInspectionQtyMismatch,
                "A rejected quantity requires a valid disposition (ReturnToSupplier / Scrap / Rework / UseAsIs / Quarantine).");
        return _repository.CompleteAsync(RequireCompany(companyId), inspectionId,
            acceptedQty, rejectedQty, disposition, RequireUser(inspector), notes, cancellationToken);
    }

    public Task<IReadOnlyList<QualityInspection>> GetOverdueInspectionsAsync(string companyId, string asOfDate, CancellationToken cancellationToken = default)
        => _repository.GetOverdueInspectionsAsync(RequireCompany(companyId), asOfDate, cancellationToken);

    // ---- Alerts --------------------------------------------------------------
    public Task<IReadOnlyList<QualityAlert>> GetAlertsAsync(string companyId, bool openOnly = false, CancellationToken cancellationToken = default)
        => _repository.GetAlertsAsync(RequireCompany(companyId), openOnly, cancellationToken);

    public async Task SaveAlertAsync(string companyId, QualityAlert alert, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (!QualityAlertStatuses.All.Contains(alert.Status))
            throw new InvalidOperationException($"Unknown alert status '{alert.Status}'.");
        if (alert.Status == QualityAlertStatuses.Closed
            && (string.IsNullOrWhiteSpace(alert.RootCause) || string.IsNullOrWhiteSpace(alert.CorrectiveAction)))
            throw new InvalidOperationException(
                "A quality alert can only be closed with a root cause and a corrective action (the Odoo rule).");
        await _repository.SaveAlertAsync(RequireCompany(companyId), alert, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    // ---- Reports ----------------------------------------------------------------
    public Task<IReadOnlyList<QualitySummaryRow>> GetSummaryAsync(string companyId, bool bySupplier, CancellationToken cancellationToken = default)
        => _repository.GetSummaryAsync(RequireCompany(companyId), bySupplier, cancellationToken);

    private static string RequireCompany(string companyId)
        => string.IsNullOrWhiteSpace(companyId)
            ? throw new ArgumentException("Company is required.", nameof(companyId))
            : companyId.Trim();

    private static string RequireUser(string userId)
        => string.IsNullOrWhiteSpace(userId)
            ? throw new ArgumentException("User is required.", nameof(userId))
            : userId.Trim();
}
