using ShouTech.Domain.Entities;
namespace ShouTech.Domain.Interfaces;
public interface IPurchaseRequisitionWorkflowRepository
{
    Task<PurchaseRequisitionWorkflow?> GetAsync(Guid companyId,long requisitionId,CancellationToken ct=default);
    Task SubmitAsync(Guid companyId,long requisitionId,IReadOnlyList<PurchaseRequisitionApprovalStep> steps,string actor,CancellationToken ct=default);
    Task DecideAsync(Guid companyId,long requisitionId,long approvalId,string approver,bool approve,string? reason,CancellationToken ct=default);
    Task MarkConvertedAsync(Guid companyId,long requisitionId,long purchaseOrderId,string actor,CancellationToken ct=default);
}
