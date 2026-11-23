using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
namespace ShouTech.Application.Services;

/// <summary>Sequential approval workflow for non-posting internal purchase requisitions stored as PRQ commercial drafts.</summary>
public sealed class PurchaseRequisitionWorkflowService
{
    private readonly IPurchaseRequisitionWorkflowRepository _repository;
    private readonly OrderService _orders;
    public PurchaseRequisitionWorkflowService(IPurchaseRequisitionWorkflowRepository repository,OrderService orders)
    { _repository=repository??throw new ArgumentNullException(nameof(repository));_orders=orders??throw new ArgumentNullException(nameof(orders)); }

    public async Task<long> CreateDraftAsync(CommercialDocumentDraft draft,string actor,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if(draft.CompanyId==Guid.Empty||draft.DocKind!=CommercialDocKinds.PurchaseRequisition||draft.Lines.Count==0)
            throw new InvalidOperationException("A purchase requisition draft requires a company and at least one line.");
        if(draft.PartyId is not null) throw new InvalidOperationException("An internal purchase requisition must not select a supplier before approval.");
        if(draft.Lines.Any(x=>x.Qty<=0||x.UnitPrice<0||string.IsNullOrWhiteSpace(x.Description)))
            throw new InvalidOperationException("Requisition lines require a description, positive quantity, and non-negative estimate.");
        return await _orders.Orders.CreateDraftAsync(CommercialDocSide.Purchase,draft,RequireActor(actor),ct).ConfigureAwait(false);
    }

    public Task<PurchaseRequisitionWorkflow?> GetAsync(Guid companyId,long id,CancellationToken ct=default)
        => _repository.GetAsync(RequireCompany(companyId),id,ct);

    public Task UpdateDraftAsync(Guid companyId,long id,CommercialDocumentDraft draft,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);ArgumentNullException.ThrowIfNull(draft);
        if(draft.CompanyId!=companyId||draft.DocKind!=CommercialDocKinds.PurchaseRequisition||draft.PartyId is not null||draft.Lines.Count==0)
            throw new InvalidOperationException("A requisition update must retain its company, kind, supplier-neutral status, and at least one line.");
        if(draft.Lines.Any(x=>x.Qty<=0||x.UnitPrice<0||string.IsNullOrWhiteSpace(x.Description)))
            throw new InvalidOperationException("Requisition lines require descriptions, positive quantities, and non-negative estimates.");
        return _orders.UpdatePurchaseRequisitionDraftAsync(id,draft,RequireActor(actor),ct);
    }



    public Task SubmitAsync(Guid companyId,long id,IReadOnlyList<PurchaseRequisitionApprovalStep> steps,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        if(steps is null||steps.Count==0)throw new InvalidOperationException("At least one approval step is required.");
        var ordered=steps.OrderBy(x=>x.StepNo).ToArray();
        if(ordered.Any(x=>x.StepNo<=0||string.IsNullOrWhiteSpace(x.ApproverUserId))||
           ordered.Select(x=>x.StepNo).Distinct().Count()!=ordered.Length||
           ordered.Select(x=>x.ApproverUserId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=ordered.Length)
            throw new InvalidOperationException("Approval sequence numbers and approvers must be unique.");
        return _repository.SubmitAsync(companyId,id,ordered,RequireActor(actor),ct);
    }

    public Task DecideAsync(Guid companyId,long id,long approvalId,string approver,bool approve,string? reason,CancellationToken ct=default)
    {
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(approvalId);
        if(!approve&&string.IsNullOrWhiteSpace(reason))throw new InvalidOperationException("A rejection reason is required.");
        return _repository.DecideAsync(companyId,id,approvalId,RequireActor(approver),approve,reason,ct);
    }

    public async Task<PurchaseRequisitionConversion> ConvertToPurchaseOrderDraftAsync(Guid companyId,long requisitionId,long supplierId,string supplierName,string currencyCode,decimal exchangeRate,long? warehouseId,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requisitionId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(supplierId);
        if(string.IsNullOrWhiteSpace(supplierName)||string.IsNullOrWhiteSpace(currencyCode)||exchangeRate<=0)throw new InvalidOperationException("Supplier, currency, and positive exchange rate are required.");
        var workflow=await _repository.GetAsync(companyId,requisitionId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Purchase requisition workflow was not found.");
        if(workflow.Status=="Converted"&&workflow.PurchaseOrderId is long existingId)
        {
            var existing=await _orders.FindByIdAsync(CommercialDocSide.Purchase,existingId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Linked purchase order is missing.");
            return new(existing.DocId,existing.DocNo);
        }
        if(workflow.Status!="Approved")throw new InvalidOperationException("Only an approved requisition can be converted.");
        var link=(await _orders.Orders.GetLinksFromAsync(companyId,requisitionId,ct).ConfigureAwait(false))
            .FirstOrDefault(x=>x.SourceDocKind==CommercialDocKinds.PurchaseRequisition&&x.TargetDocKind==CommercialDocKinds.PurchaseOrder);
        if(link is not null)
        {
            var existing=await _orders.FindByIdAsync(CommercialDocSide.Purchase,link.TargetDocId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Linked purchase order is missing.");
            await _repository.MarkConvertedAsync(companyId,requisitionId,existing.DocId,actor,ct).ConfigureAwait(false);
            return new(existing.DocId,existing.DocNo);
        }
        var requisition=await _orders.FindByIdAsync(CommercialDocSide.Purchase,requisitionId,ct).ConfigureAwait(false)
            ??throw new InvalidOperationException("Purchase requisition document was not found.");
        if(requisition.DocKind!=CommercialDocKinds.PurchaseRequisition)throw new InvalidOperationException("Source document is not a purchase requisition.");
        var number=await _orders.NextDocNoAsync(CommercialDocSide.Purchase,companyId,CommercialDocKinds.PurchaseOrder,ct).ConfigureAwait(false);
        var lines=requisition.Lines.Select(x=>new CommercialLineDraft(x.MaterialId,x.MaterialCode,x.Description,x.Unit,x.Qty,x.UnitPrice,x.DiscountPct,0m,x.WarehouseId??warehouseId,x.LineNo)).ToArray();
        var draft=new CommercialDocumentDraft(companyId,CommercialDocKinds.PurchaseOrder,number,DateOnly.FromDateTime(DateTime.Today),supplierId,supplierName.Trim(),warehouseId,currencyCode.Trim().ToUpperInvariant(),exchangeRate,$"Created from requisition {requisition.DocNo}",lines);
        var poId=await _orders.Orders.CreateLinkedDraftAsync(CommercialDocSide.Purchase,draft,CommercialDocKinds.PurchaseRequisition,requisitionId,RequireActor(actor),ct).ConfigureAwait(false);
        await _repository.MarkConvertedAsync(companyId,requisitionId,poId,actor,ct).ConfigureAwait(false);
        return new(poId,number);
    }
    private static Guid RequireCompany(Guid id)=>id==Guid.Empty?throw new ArgumentException("Company is required.",nameof(id)):id;
    private static string RequireActor(string value)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("Actor is required.",nameof(value)):value.Trim();
}
