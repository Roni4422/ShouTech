namespace ShouTech.Domain.Entities;
public sealed record PurchaseRequisitionApprovalStep(int StepNo,string ApproverUserId);

/// <summary>Requisition purpose (migration 092, R5), modelled on the
/// ERPNext Material Request types ShouTech supports. Stored as text on
/// the document header; only Purchase converts to RFQ/PO in this build.</summary>
public static class PurchaseRequisitionRequestTypes
{
    public const string Purchase = "Purchase";
    public const string MaterialTransfer = "MaterialTransfer";
    public const string MaterialIssue = "MaterialIssue";
    public const string Manufacture = "Manufacture";

    public static readonly IReadOnlyList<string> All = new[] { Purchase, MaterialTransfer, MaterialIssue, Manufacture };

    public static string Normalize(string? value)
        => All.FirstOrDefault(x => string.Equals(x, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Purchase;
}

public sealed class PurchaseRequisitionWorkflow
{
    public long RequisitionId { get; set; }
    public Guid CompanyId { get; set; }
    public string Status { get; set; } = "Draft";
    public long? PurchaseOrderId { get; set; }
    /// <summary>The RFQ created by PRQ -> RFQ conversion (migration 092, R4).</summary>
    public long? RfqDocId { get; set; }
    /// <summary>Current approval round (migration 092, R3). Round 1 is the
    /// first submission; resubmission after rejection starts round 2+.</summary>
    public int RoundNo { get; set; } = 1;
    public List<PurchaseRequisitionApproval> Approvals { get; set; } = new();
    /// <summary>Append-only workflow event history, oldest first.</summary>
    public List<PurchaseRequisitionEvent> Events { get; set; } = new();
}
public sealed class PurchaseRequisitionApproval
{
    public long ApprovalId { get; set; }
    /// <summary>Approval round this step belongs to (migration 092, R3).</summary>
    public int RoundNo { get; set; } = 1;
    public int StepNo { get; set; }
    public string ApproverUserId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? Reason { get; set; }
    public DateTime? DecidedAt { get; set; }
}
public sealed record PurchaseRequisitionEvent(long EventId,string Action,string Actor,string? Reason,DateTime CreatedAt);
public sealed record PurchaseRequisitionConversion(long PurchaseOrderId,string PurchaseOrderNo);
public sealed record PurchaseRequisitionRfqConversion(long RfqId,string RfqNo);
