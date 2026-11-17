using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Interfaces;

/// <summary>Quality persistence (migration 056). Quality lives in the
/// stock world, so every method keys the company by its CODE — the key
/// materials / warehouses / stock_batches are written with — unlike
/// the journal-world repositories, which key by Guid.</summary>
public interface IQualityRepository
{
    // ---- Gate setting -------------------------------------------------
    /// <summary>The company's QC_GATE_MODE (quality_settings); the
    /// seeded default (Warn, decision Q1) when no row exists.</summary>
    Task<string> GetGateModeAsync(string companyId, CancellationToken cancellationToken = default);
    Task SetGateModeAsync(string companyId, string gateMode, string userId, CancellationToken cancellationToken = default);

    // ---- Parameters / templates ----------------------------------------
    Task<IReadOnlyList<QualityParameter>> GetParametersAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveParameterAsync(string companyId, QualityParameter parameter, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteParameterAsync(string companyId, Guid parameterId, string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QualityTemplate>> GetTemplatesAsync(string companyId, CancellationToken cancellationToken = default);
    Task SaveTemplateAsync(string companyId, QualityTemplate template, string userId, CancellationToken cancellationToken = default);
    Task SoftDeleteTemplateAsync(string companyId, Guid templateId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Materials with their QC wiring, for the assignment
    /// editor in QcTemplateWin.</summary>
    Task<IReadOnlyList<QualityMaterialOption>> GetMaterialOptionsAsync(string companyId, CancellationToken cancellationToken = default);

    /// <summary>Sets a material's QC wiring (check-required flags and
    /// incoming / outgoing templates) in one transaction.</summary>
    Task SetMaterialQualityAsync(string companyId, long materialId, bool qualityCheckRequired,
        bool outgoingRequired, Guid? templateInId, Guid? templateOutId, string userId,
        CancellationToken cancellationToken = default);

    // ---- Inspections ----------------------------------------------------
    /// <summary>All live inspections of the company (newest first),
    /// optionally only the pending (Draft) worklist.</summary>
    Task<IReadOnlyList<QualityInspection>> GetInspectionsAsync(string companyId, bool pendingOnly = false, CancellationToken cancellationToken = default);

    /// <summary>One inspection with its lines and readings.</summary>
    Task<QualityInspection?> GetInspectionAsync(string companyId, Guid inspectionId, CancellationToken cancellationToken = default);

    /// <summary>Creates a Draft inspection manually (the Inspect action
    /// on a document that was not gate-diverted, e.g. an outgoing
    /// inspection). Lines are snapshotted from the template when one
    /// is given (or resolved from the material's assignment).</summary>
    Task<Guid> CreateInspectionAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default);

    /// <summary>Saves a Draft inspection's line results + readings
    /// (wholesale replace inside one transaction). Terminal
    /// inspections are immutable.</summary>
    Task SaveResultsAsync(string companyId, QualityInspection inspection, string userId, CancellationToken cancellationToken = default);

    /// <summary>Completes a Draft inspection: evaluates any unjudged
    /// lines, writes the terminal status, moves the accepted quantity
    /// from quarantine to the destination warehouse and dispositions
    /// the rejected quantity — all through the valuation engine inside
    /// ONE transaction — and raises the failure alert.</summary>
    Task<QualityCompletionResult> CompleteAsync(string companyId, Guid inspectionId,
        decimal acceptedQty, decimal rejectedQty, string? disposition,
        string inspector, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Pending inspections whose due date has passed (the
    /// overdue worklist + the proof surface for overdue alerts).</summary>
    Task<IReadOnlyList<QualityInspection>> GetOverdueInspectionsAsync(string companyId, string asOfDate, CancellationToken cancellationToken = default);

    // ---- Alerts -----------------------------------------------------------
    Task<IReadOnlyList<QualityAlert>> GetAlertsAsync(string companyId, bool openOnly = false, CancellationToken cancellationToken = default);
    Task SaveAlertAsync(string companyId, QualityAlert alert, string userId, CancellationToken cancellationToken = default);

    // ---- Reports ------------------------------------------------------------
    /// <summary>Inspection summary grouped by material, or by supplier
    /// party when <paramref name="bySupplier"/> (the reject-rate
    /// report).</summary>
    Task<IReadOnlyList<QualitySummaryRow>> GetSummaryAsync(string companyId, bool bySupplier, CancellationToken cancellationToken = default);
}
