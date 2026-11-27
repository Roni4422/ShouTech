namespace ShouTech.Domain.Entities;

/// <summary>Parameter kinds (migration 056). Numeric readings must fall
/// inside the template line's min-max band; Value readings must equal
/// the accepted value; PassFail carries no reading, only a result.
/// Formula criteria are deferred (owner decision Q3).</summary>
public static class QualityParamTypes
{
    public const string Numeric = "Numeric";
    public const string Value = "Value";
    public const string PassFail = "PassFail";

    public static readonly IReadOnlyList<string> All = new[] { Numeric, Value, PassFail };
}

/// <summary>Inspection kinds (ERPNext pattern): Incoming against a
/// receipt, InProcess against a manufacturing order, Outgoing against
/// a delivery / sales document.</summary>
public static class QualityInspectionTypes
{
    public const string Incoming = "Incoming";
    public const string InProcess = "InProcess";
    public const string Outgoing = "Outgoing";

    public static readonly IReadOnlyList<string> All = new[] { Incoming, InProcess, Outgoing };
}

/// <summary>Inspection lifecycle. Draft = created (usually by the gate
/// at receipt time) and awaiting results; the three terminal states are
/// written by completion from the accepted / rejected quantities.</summary>
public static class QualityInspectionStatuses
{
    public const string Draft = "Draft";
    public const string Accepted = "Accepted";
    public const string PartiallyAccepted = "PartiallyAccepted";
    public const string Rejected = "Rejected";

    public static readonly IReadOnlyList<string> All = new[] { Draft, Accepted, PartiallyAccepted, Rejected };

    /// <summary>The terminal status for a completion's quantities:
    /// all accepted, all rejected, or a split (partial pass).</summary>
    public static string FromQuantities(decimal acceptedQty, decimal rejectedQty)
    {
        if (rejectedQty <= 0) return Accepted;
        if (acceptedQty <= 0) return Rejected;
        return PartiallyAccepted;
    }
}

/// <summary>What happens to the rejected quantity (design section 5.2).
/// ReturnToSupplier / Scrap move the stock out of quarantine through
/// the valuation engine; UseAsIs releases it to the destination
/// warehouse like an acceptance; Rework / Quarantine leave it in the
/// quarantine warehouse under the recorded disposition.</summary>
public static class QualityDispositions
{
    public const string ReturnToSupplier = "ReturnToSupplier";
    public const string Scrap = "Scrap";
    public const string Rework = "Rework";
    public const string UseAsIs = "UseAsIs";
    public const string Quarantine = "Quarantine";

    public static readonly IReadOnlyList<string> All =
        new[] { ReturnToSupplier, Scrap, Rework, UseAsIs, Quarantine };
}

/// <summary>Per-company gate modes (migration 056 quality_settings,
/// owner decision Q1: Warn is the seeded default). Stop = the hard
/// gate (quarantined stock cannot be sold; an outgoing-required
/// delivery without an accepted inspection is refused). Warn = the
/// soft gate (the same flows proceed and are surfaced as alerts).</summary>
public static class QualityGateModes
{
    public const string Stop = "Stop";
    public const string Warn = "Warn";

    public static readonly IReadOnlyList<string> All = new[] { Stop, Warn };
    public const string Default = Warn;
}

/// <summary>Alert lifecycle (Odoo quality-alert pattern) and kinds.</summary>
public static class QualityAlertStatuses
{
    public const string Open = "Open";
    public const string InProgress = "InProgress";
    public const string Closed = "Closed";

    public static readonly IReadOnlyList<string> All = new[] { Open, InProgress, Closed };
}

public static class QualityAlertTypes
{
    public const string FailedInspection = "FailedInspection";
    public const string OverdueInspection = "OverdueInspection";
    public const string GateWarning = "GateWarning";

    public static readonly IReadOnlyList<string> All =
        new[] { FailedInspection, OverdueInspection, GateWarning };
}

/// <summary>One measurable characteristic (ERPNext Quality Inspection
/// Parameter): a named check whose readings are judged by template
/// lines.</summary>
public sealed class QualityParameter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string ParamType { get; set; } = QualityParamTypes.Numeric;
    public string? Unit { get; set; }
    public string? GroupName { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One template line: a parameter plus its acceptance
/// criteria on this template (numeric band or accepted value).</summary>
public sealed class QualityTemplateLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParamId { get; set; }
    public string ParamCode { get; set; } = string.Empty;
    public string? ParamName { get; set; }
    public string ParamType { get; set; } = QualityParamTypes.Numeric;
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string? AcceptedValue { get; set; }
    public bool IsMandatory { get; set; } = true;
    public int Sort { get; set; }
}

/// <summary>A reusable inspection template (ERPNext Quality Inspection
/// Template), attached to materials as their incoming / outgoing
/// template.</summary>
public sealed class QualityTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string InspectionType { get; set; } = QualityInspectionTypes.Incoming;
    public bool IsActive { get; set; } = true;
    public List<QualityTemplateLine> Lines { get; set; } = new();
}

/// <summary>One reading of one inspection line (up to 3 per line in
/// v1, design section 5.2).</summary>
public sealed class QualityReading
{
    public int ReadingNo { get; set; }
    public decimal? ReadingNum { get; set; }
    public string? ReadingText { get; set; }
}

/// <summary>One inspection line: a template parameter snapshotted onto
/// the inspection, its readings, and its Pass/Fail result (auto-
/// evaluated from the readings, inspector-overridable with the
/// override visible).</summary>
public sealed class QualityInspectionLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParamId { get; set; }
    public string ParamCode { get; set; } = string.Empty;
    public string? ParamName { get; set; }
    public string ParamType { get; set; } = QualityParamTypes.Numeric;
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public string? AcceptedValue { get; set; }
    public bool IsMandatory { get; set; } = true;
    public string? Result { get; set; }
    public bool IsOverridden { get; set; }
    public string? Notes { get; set; }
    public int Sort { get; set; }
    public List<QualityReading> Readings { get; set; } = new();
}

/// <summary>One inspection document: the quality record of one receipt
/// / production / delivery quantity of one material (optionally one
/// batch). While Draft, its quantity sits in the quarantine warehouse;
/// completion moves it per the accepted / rejected split.</summary>
public sealed class QualityInspection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string InspectionType { get; set; } = QualityInspectionTypes.Incoming;
    public Guid? TemplateId { get; set; }
    public string RefDocKind { get; set; } = string.Empty;
    public string RefDocId { get; set; } = string.Empty;
    public string RefDocNo { get; set; } = string.Empty;
    public long MaterialId { get; set; }
    public string? MaterialCode { get; set; }
    public string? MaterialName { get; set; }
    public long? BatchId { get; set; }
    public string? BatchNo { get; set; }
    /// <summary>Where accepted stock is released TO (the receipt's
    /// document warehouse).</summary>
    public long WarehouseId { get; set; }
    /// <summary>Where the inspected stock sits while Draft (null for
    /// outgoing inspections, which hold no stock).</summary>
    public long? QuarantineWarehouseId { get; set; }
    public long? PartyId { get; set; }
    public decimal TotalQty { get; set; }
    public decimal SampleQty { get; set; }
    public decimal AcceptedQty { get; set; }
    public decimal RejectedQty { get; set; }
    public string Status { get; set; } = QualityInspectionStatuses.Draft;
    public string? Disposition { get; set; }
    public string? Inspector { get; set; }
    public string? InspectedAt { get; set; }
    public string? DueDate { get; set; }
    public string? Notes { get; set; }
    public List<QualityInspectionLine> Lines { get; set; } = new();
}

/// <summary>One quality alert (Odoo pattern): raised automatically by
/// a failed inspection, an overdue pending inspection, or a Warn-mode
/// gate pass; closed with root cause + corrective action.</summary>
public sealed class QualityAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AlertType { get; set; } = QualityAlertTypes.FailedInspection;
    public Guid? InspectionId { get; set; }
    public long? MaterialId { get; set; }
    public string? MaterialCode { get; set; }
    public long? BatchId { get; set; }
    public long? PartyId { get; set; }
    public string? Message { get; set; }
    public string? RootCause { get; set; }
    public string? CorrectiveAction { get; set; }
    public string? PreventiveAction { get; set; }
    public string Status { get; set; } = QualityAlertStatuses.Open;
    public string? Owner { get; set; }
    public string? DueDate { get; set; }
}

/// <summary>A material as the template-assignment editor offers it,
/// with its current QC wiring.</summary>
public sealed record QualityMaterialOption(
    long MaterialId, string Code, string Name,
    bool QualityCheckRequired, bool OutgoingRequired,
    Guid? TemplateInId, Guid? TemplateOutId);

/// <summary>One inspection-summary row (the QcSummaryWin report):
/// inspections, accepted / rejected quantities and the reject rate,
/// per material (or per supplier when grouped by party).</summary>
public sealed record QualitySummaryRow(
    string GroupCode, string GroupName,
    int Inspections, decimal TotalQty, decimal AcceptedQty, decimal RejectedQty)
{
    public decimal RejectRatePct => TotalQty == 0m
        ? 0m
        : Math.Round(RejectedQty * 100m / TotalQty, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The outcome of completing an inspection: the terminal
/// status plus the alert raised for the rejected part, if any.</summary>
public sealed record QualityCompletionResult(
    Guid InspectionId, string Status, decimal AcceptedQty, decimal RejectedQty,
    Guid? AlertId);
