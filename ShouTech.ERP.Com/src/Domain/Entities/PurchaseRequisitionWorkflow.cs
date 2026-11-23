namespace ShouTech.Domain.Entities;
public sealed record PurchaseRequisitionApprovalStep(int StepNo,string ApproverUserId);
public sealed class PurchaseRequisitionWorkflow
{
    public long RequisitionId { get; set; }
    public Guid CompanyId { get; set; }
    public string Status { get; set; } = "Draft";
    public long? PurchaseOrderId { get; set; }
    public List<PurchaseRequisitionApproval> Approvals { get; set; } = new();
}
public sealed class PurchaseRequisitionApproval
{
    public long ApprovalId { get; set; }
    public int StepNo { get; set; }
    public string ApproverUserId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? Reason { get; set; }
    public DateTime? DecidedAt { get; set; }
}
public sealed record PurchaseRequisitionConversion(long PurchaseOrderId,string PurchaseOrderNo);

public sealed record PurchaseRequisitionConversion(long PurchaseOrderId,string PurchaseOrderNo);
