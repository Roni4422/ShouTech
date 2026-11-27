using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using ShouTech.Domain;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
namespace ShouTech.Infrastructure;

/// <summary>Provider-neutral sequential approval persistence with company isolation and append-only decision events.</summary>
public sealed class PurchaseRequisitionWorkflowRepository:IPurchaseRequisitionWorkflowRepository
{
    private readonly string _provider,_connection;
    private bool Pg=>_provider=="PostgreSQL";
    public PurchaseRequisitionWorkflowRepository(string provider,string connection){if(string.IsNullOrWhiteSpace(connection))throw new ArgumentException("Connection string is required.",nameof(connection));_provider=GlPosting.NormalizeProvider(provider);_connection=connection;}
    public async Task<PurchaseRequisitionWorkflow?> GetAsync(Guid company,long id,CancellationToken ct=default)
    {
        Require(company,id);await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);
        PurchaseRequisitionWorkflow? w=null;
        await using(var q=Cmd(c,null,"SELECT CASE WHEN status='Approved' AND conversion_started_at IS NOT NULL THEN 'Converting' ELSE status END,purchase_order_id,rfq_doc_id,round_no FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id")){Add(q,"@c",Co(company));Add(q,"@id",id);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);if(await r.ReadAsync(ct).ConfigureAwait(false))w=new(){CompanyId=company,RequisitionId=id,Status=Text(r,0)??"Draft",PurchaseOrderId=r.IsDBNull(1)?null:Num(r,1),RfqDocId=r.IsDBNull(2)?null:Num(r,2),RoundNo=Convert.ToInt32(r.GetValue(3),CultureInfo.InvariantCulture)};}
        if(w is null)return null;
        await using(var q=Cmd(c,null,"SELECT approval_id,round_no,step_no,approver_user_id,status,reason,decided_at FROM purchase_requisition_approvals WHERE company_id=@c AND requisition_doc_id=@id ORDER BY round_no,step_no")){Add(q,"@c",Co(company));Add(q,"@id",id);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);while(await r.ReadAsync(ct).ConfigureAwait(false))w.Approvals.Add(new(){ApprovalId=Num(r,0),RoundNo=Convert.ToInt32(r.GetValue(1),CultureInfo.InvariantCulture),StepNo=Convert.ToInt32(r.GetValue(2),CultureInfo.InvariantCulture),ApproverUserId=Text(r,3)??"",Status=Text(r,4)??"Pending",Reason=Text(r,5),DecidedAt=r.IsDBNull(6)?null:Convert.ToDateTime(r.GetValue(6),CultureInfo.InvariantCulture)});}
        await using(var q=Cmd(c,null,"SELECT event_id,action,actor,reason,created_at FROM purchase_requisition_events WHERE company_id=@c AND requisition_doc_id=@id ORDER BY event_id")){Add(q,"@c",Co(company));Add(q,"@id",id);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);while(await r.ReadAsync(ct).ConfigureAwait(false))w.Events.Add(new(Num(r,0),Text(r,1)??"",Text(r,2)??"",Text(r,3),Convert.ToDateTime(r.GetValue(4),CultureInfo.InvariantCulture)));}
        return w;
    }
    public async Task SubmitAsync(Guid company,long id,IReadOnlyList<PurchaseRequisitionApprovalStep> steps,string actor,CancellationToken ct=default)
    {
        Require(company,id);await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        var budgetBlocked=new List<BudgetViolation>();
        try{
            DateOnly docDate;decimal estimatedBase;
            await using(var check=Cmd(c,t,"SELECT created_by,doc_date,grand_total,exchange_rate FROM purchase_documents WHERE company_id=@c AND doc_id=@id AND doc_kind='PRQ' AND status='Draft' AND is_deleted=0")){Add(check,"@c",Co(company));Add(check,"@id",id);await using var r=await check.ExecuteReaderAsync(ct).ConfigureAwait(false);if(!await r.ReadAsync(ct).ConfigureAwait(false))throw new InvalidOperationException("Only a draft purchase requisition can be submitted.");docDate=DateOnly.Parse(Convert.ToString(r.GetValue(1),CultureInfo.InvariantCulture)!,CultureInfo.InvariantCulture);var total=Convert.ToDecimal(r.GetValue(2),CultureInfo.InvariantCulture);var rate=Convert.ToDecimal(r.GetValue(3),CultureInfo.InvariantCulture);estimatedBase=total*(rate>0?rate:1m);}
            string? existingStatus=null;var round=1;
            await using(var current=Cmd(c,t,"SELECT status,round_no FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty))){Add(current,"@c",Co(company));Add(current,"@id",id);await using var r=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);if(await r.ReadAsync(ct).ConfigureAwait(false)){existingStatus=Text(r,0);round=Convert.ToInt32(r.GetValue(1),CultureInfo.InvariantCulture);}}
            if(existingStatus is not null&&existingStatus!="Rejected")
                throw new AppError(ErrorCodes.RequisitionStateInvalid,"Only a draft or rejected requisition can be submitted.");
            // R1 (migration 107): budget check at submit, inside the
            // submit transaction so a Stop rolls the whole submission
            // back. The requisition carries no account coding, so the
            // commitment is charged to the canonical Purchases account
            // dimension (see BudgetGuard.CheckCommitment). Warn-mode
            // warnings become a durable override event below.
            var budgetWarnings=BudgetGuard.CheckCommitment(c,t,_provider,company,docDate,GlAccounts.Purchases,estimatedBase,actor,budgetBlocked);
            bool resubmit;
            if(existingStatus is null)
            {
                resubmit=false;
                await using var insert=Cmd(c,t,"INSERT INTO purchase_requisition_workflow(company_id,requisition_doc_id,status,round_no) VALUES(@c,@id,'Submitted',1)");Add(insert,"@c",Co(company));Add(insert,"@id",id);await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else if(existingStatus=="Rejected")
            {
                // R3: resubmission starts a new approval round; the prior
                // round's approvals and events stay untouched.
                resubmit=true;round+=1;
                await using var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Submitted',round_no=@round,updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Rejected'");Add(update,"@round",round);Add(update,"@c",Co(company));Add(update,"@id",id);if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new InvalidOperationException("The requisition state changed; reload and retry.");
            }
            else throw new AppError(ErrorCodes.RequisitionStateInvalid,"Only a draft or rejected requisition can be submitted.");
            foreach(var s in steps){await using var q=Cmd(c,t,"INSERT INTO purchase_requisition_approvals(company_id,requisition_doc_id,round_no,step_no,approver_user_id,status) VALUES(@c,@id,@round,@step,@user,'Pending')");Add(q,"@c",Co(company));Add(q,"@id",id);Add(q,"@round",round);Add(q,"@step",s.StepNo);Add(q,"@user",s.ApproverUserId.Trim());await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
            await Event(c,t,company,id,resubmit?"Resubmitted":"Submitted",actor,resubmit?$"Round {round}":null,ct);
            foreach(var warning in budgetWarnings)
                await Event(c,t,company,id,"BudgetOverride",actor,$"Budget '{warning.BudgetName}' {warning.Horizon} limit {warning.BudgetAmount} exceeded by {warning.Overage} at submit; submission allowed in Warn mode.",ct);
            await t.CommitAsync(ct).ConfigureAwait(false);
        }catch{await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            // A budget Stop rolled the submission back; record the Blocked
            // budget-control events on the same connection in their own
            // transaction, mirroring JournalRepository's boundary pattern.
            if(budgetBlocked.Count>0){await using var bt=await c.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);try{BudgetGuard.RecordBlockedEvents(c,bt,_provider,company,actor,budgetBlocked);await bt.CommitAsync(CancellationToken.None).ConfigureAwait(false);}catch{await bt.RollbackAsync(CancellationToken.None).ConfigureAwait(false);throw;}}
            throw;}
    }
    public async Task CancelAsync(Guid company,long id,string actor,string reason,CancellationToken ct=default)
    {
        Require(company,id);
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Actor is required.",nameof(actor));
        if(string.IsNullOrWhiteSpace(reason))throw new AppError(ErrorCodes.RequisitionReasonRequired,"A cancellation reason is required.");
        await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try{
            await using(var check=Cmd(c,t,"SELECT COUNT(*) FROM purchase_documents WHERE company_id=@c AND doc_id=@id AND doc_kind='PRQ' AND is_deleted=0")){Add(check,"@c",Co(company));Add(check,"@id",id);if(Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)==0)throw new InvalidOperationException("Purchase requisition document was not found.");}
            string? status=null;
            await using(var current=Cmd(c,t,"SELECT status FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty))){Add(current,"@c",Co(company));Add(current,"@id",id);await using var r=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);if(await r.ReadAsync(ct).ConfigureAwait(false))status=Text(r,0);}
            // R2: cancellation is blocked once any downstream document
            // exists (conversion), and from the terminal states.
            if(status is "Converted" or "Closed" or "Cancelled")
                throw new AppError(ErrorCodes.RequisitionCancelBlocked,$"A requisition in state '{status}' cannot be cancelled.");
            await using(var links=Cmd(c,t,"SELECT COUNT(*) FROM document_links WHERE company_id=@c AND source_doc_kind='PRQ' AND source_doc_id=@id")){Add(links,"@c",Co(company));Add(links,"@id",id);if(Convert.ToInt64(await links.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)>0)throw new AppError(ErrorCodes.RequisitionCancelBlocked,"A requisition that has been converted cannot be cancelled.");}
            if(status is null)
            {
                await using var insert=Cmd(c,t,"INSERT INTO purchase_requisition_workflow(company_id,requisition_doc_id,status) VALUES(@c,@id,'Cancelled')");Add(insert,"@c",Co(company));Add(insert,"@id",id);await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Cancelled',updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status=@old");Add(update,"@c",Co(company));Add(update,"@id",id);Add(update,"@old",status);if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new InvalidOperationException("The requisition state changed; reload and retry.");
            }
            await Event(c,t,company,id,"Cancelled",actor.Trim(),reason.Trim(),ct);await t.CommitAsync(ct).ConfigureAwait(false);
        }catch{await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }
    public async Task CloseAsync(Guid company,long id,string actor,CancellationToken ct=default)
    {
        Require(company,id);
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Actor is required.",nameof(actor));
        await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try{
            // R2: Close is only meaningful after full conversion. The
            // workflow row is locked first on PostgreSQL (the d12eb0ac
            // serialization discipline every workflow write follows), so
            // a concurrent conversion/cancel cannot interleave.
            await using(var current=Cmd(c,t,"SELECT status FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty))){Add(current,"@c",Co(company));Add(current,"@id",id);await using var r=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);if(!await r.ReadAsync(ct).ConfigureAwait(false)||Text(r,0)!="Converted")throw new AppError(ErrorCodes.RequisitionCloseInvalid,"Only a fully converted requisition can be closed.");}
            await using var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Closed',updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Converted'");Add(update,"@c",Co(company));Add(update,"@id",id);
            if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new AppError(ErrorCodes.RequisitionCloseInvalid,"Only a fully converted requisition can be closed.");
            await Event(c,t,company,id,"Closed",actor.Trim(),null,ct);await t.CommitAsync(ct).ConfigureAwait(false);
        }catch{await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }
    public async Task DecideAsync(Guid company,long id,long approvalId,string approver,bool approve,string? reason,CancellationToken ct=default)
    {
        Require(company,id);await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try{
            await using(var q=Cmd(c,t,"SELECT d.created_by FROM purchase_documents d JOIN purchase_requisition_workflow w ON w.company_id=d.company_id AND w.requisition_doc_id=d.doc_id WHERE d.company_id=@c AND d.doc_id=@id AND d.doc_kind='PRQ' AND w.status='Submitted'"+(Pg?" FOR UPDATE OF w":string.Empty))){Add(q,"@c",Co(company));Add(q,"@id",id);var owner=Convert.ToString(await q.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)??throw new InvalidOperationException("Only a submitted requisition can be decided.");if(string.Equals(owner,approver,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The requester cannot approve their own requisition.");}
            await using(var q=Cmd(c,t,"SELECT a.approval_id,a.approver_user_id FROM purchase_requisition_approvals a JOIN purchase_requisition_workflow w ON w.company_id=a.company_id AND w.requisition_doc_id=a.requisition_doc_id WHERE a.company_id=@c AND a.requisition_doc_id=@id AND a.status='Pending' AND a.round_no=w.round_no AND a.step_no=(SELECT MIN(a2.step_no) FROM purchase_requisition_approvals a2 WHERE a2.company_id=@c AND a2.requisition_doc_id=@id AND a2.status='Pending' AND a2.round_no=w.round_no) ORDER BY a.approval_id LIMIT 1")){Add(q,"@c",Co(company));Add(q,"@id",id);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);if(!await r.ReadAsync(ct).ConfigureAwait(false)||Num(r,0)!=approvalId||!string.Equals(Text(r,1),approver,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Approval is not assigned to this user or is not the active step.");}
            await using(var q=Cmd(c,t,"UPDATE purchase_requisition_approvals SET status=@s,reason=@r,decided_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND approval_id=@a AND status='Pending'")){Add(q,"@s",approve?"Approved":"Rejected");Add(q,"@r",(object?)reason?.Trim()??DBNull.Value);Add(q,"@c",Co(company));Add(q,"@id",id);Add(q,"@a",approvalId);if(await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new InvalidOperationException("Approval was already decided.");}
            var status=approve?"Approved":"Rejected";if(approve){await using var q=Cmd(c,t,"SELECT COUNT(*) FROM purchase_requisition_approvals a JOIN purchase_requisition_workflow w ON w.company_id=a.company_id AND w.requisition_doc_id=a.requisition_doc_id WHERE a.company_id=@c AND a.requisition_doc_id=@id AND a.status='Pending' AND a.round_no=w.round_no");Add(q,"@c",Co(company));Add(q,"@id",id);if(Convert.ToInt32(await q.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)>0)status="Submitted";}
            await using(var q=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status=@s,updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Submitted'")){Add(q,"@s",status);Add(q,"@c",Co(company));Add(q,"@id",id);await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
            await Event(c,t,company,id,status,approver,reason,ct);await t.CommitAsync(ct).ConfigureAwait(false);
        }catch{await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }
    public async Task<bool> BeginConversionAsync(Guid company,long id,string actor,CancellationToken ct=default)
    {
        Require(company,id);
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Actor is required.",nameof(actor));
        await using var c=Open();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string status;
            long? existingPo;
            bool conversionStarted;
            await using(var current=Cmd(c,t,
                "SELECT status,purchase_order_id,conversion_started_at FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty)))
            {
                Add(current,"@c",Co(company));Add(current,"@id",id);
                await using var reader=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Purchase requisition workflow was not found.");
                status=Text(reader,0)??"";
                existingPo=reader.IsDBNull(1)?null:Num(reader,1);
                conversionStarted=!reader.IsDBNull(2);
            }
            if(status=="Converted")
                throw new InvalidOperationException(existingPo is null
                    ? "The requisition is marked converted but has no linked purchase order."
                    : "The requisition has already been converted.");
            if(existingPo is not null)
                throw new InvalidOperationException("The requisition already carries a purchase-order link and cannot start another conversion.");
            if(conversionStarted)
                throw new InvalidOperationException("This requisition is already being converted to a purchase order.");
            if(status!="Approved")
                throw new InvalidOperationException("Only an approved requisition can begin conversion.");
            await using(var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET conversion_started_at=CURRENT_TIMESTAMP,updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Approved' AND conversion_started_at IS NULL"))
            {
                Add(update,"@c",Co(company));Add(update,"@id",id);
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("The requisition conversion state changed; reload and retry.");
            }
            await Event(c,t,company,id,"ConversionStarted",actor.Trim(),null,ct);
            await t.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task AbortConversionAsync(Guid company,long id,string actor,string reason,CancellationToken ct=default)
    {
        Require(company,id);
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Actor is required.",nameof(actor));
        await using var c=Open();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string status;
            bool conversionStarted;
            await using(var current=Cmd(c,t,
                "SELECT status,conversion_started_at FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty)))
            {
                Add(current,"@c",Co(company));Add(current,"@id",id);
                await using var reader=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    status=Text(reader,0)??"";
                    conversionStarted=!reader.IsDBNull(1);
                }
                else
                {
                    status="";
                    conversionStarted=false;
                }
            }
            if(status!="Approved" || !conversionStarted)
            {
                await t.CommitAsync(ct).ConfigureAwait(false);
                return;
            }
            await using(var linked=Cmd(c,t,"SELECT COUNT(*) FROM document_links WHERE company_id=@c AND source_doc_kind='PRQ' AND source_doc_id=@id AND target_doc_kind IN ('PO','RFQ')"))
            {
                Add(linked,"@c",Co(company));Add(linked,"@id",id);
                if(Convert.ToInt64(await linked.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)>0)
                {
                    await t.CommitAsync(ct).ConfigureAwait(false);
                    return;
                }
            }
            await using(var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET conversion_started_at=NULL,updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Approved' AND conversion_started_at IS NOT NULL"))
            {
                Add(update,"@c",Co(company));Add(update,"@id",id);
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)==1)
                    await Event(c,t,company,id,"ConversionFailed",actor.Trim(),string.IsNullOrWhiteSpace(reason)?"Purchase-order draft creation failed.":reason.Trim(),ct);
            }
            await t.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task MarkConvertedAsync(Guid company,long id,long poId,string actor,CancellationToken ct=default)
    {
        Require(company,id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(poId);
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Actor is required.",nameof(actor));
        await using var c=Open();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using(var current=Cmd(c,t,
                "SELECT status,purchase_order_id FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty)))
            {
                Add(current,"@c",Co(company));
                Add(current,"@id",id);
                await using var reader=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Purchase requisition workflow was not found.");
                var status=Text(reader,0);
                var existingPo=reader.IsDBNull(1)?(long?)null:Num(reader,1);
                if(existingPo is long linkedPo && linkedPo!=poId)
                    throw new InvalidOperationException("A requisition conversion cannot replace an existing purchase-order link.");
                if(string.Equals(status,"Converted",StringComparison.Ordinal))
                {
                    if(existingPo!=poId)
                        throw new InvalidOperationException("A converted requisition cannot be linked to a different purchase order.");
                    await reader.DisposeAsync().ConfigureAwait(false);
                    await t.CommitAsync(ct).ConfigureAwait(false);
                    return;
                }
                if(!string.Equals(status,"Approved",StringComparison.Ordinal)
                    && !string.Equals(status,"Converting",StringComparison.Ordinal))
                    throw new InvalidOperationException("Only an approved requisition can be converted.");
            }
            await using(var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Converted',purchase_order_id=@po,conversion_started_at=NULL,updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status IN ('Approved','Converting')"))
            {
                Add(update,"@po",poId);
                Add(update,"@c",Co(company));
                Add(update,"@id",id);
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("The requisition conversion state changed; reload and retry.");
            }
            await Event(c,t,company,id,"Converted",actor.Trim(),$"PO:{poId}",ct);
            await t.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
    public async Task MarkConvertedToRfqAsync(Guid company,long id,long rfqId,string actor,CancellationToken ct=default)
    {
        Require(company,id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rfqId);
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Actor is required.",nameof(actor));
        await using var c=Open();
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using(var current=Cmd(c,t,
                "SELECT status,purchase_order_id,rfq_doc_id FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty)))
            {
                Add(current,"@c",Co(company));
                Add(current,"@id",id);
                await using var reader=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Purchase requisition workflow was not found.");
                var status=Text(reader,0);
                var existingPo=reader.IsDBNull(1)?(long?)null:Num(reader,1);
                var existingRfq=reader.IsDBNull(2)?(long?)null:Num(reader,2);
                if(existingPo is not null)
                    throw new InvalidOperationException("A requisition already converted to a purchase order cannot be converted to an RFQ.");
                if(existingRfq is long linkedRfq && linkedRfq!=rfqId)
                    throw new InvalidOperationException("A requisition conversion cannot replace an existing RFQ link.");
                if(string.Equals(status,"Converted",StringComparison.Ordinal))
                {
                    if(existingRfq!=rfqId)
                        throw new InvalidOperationException("A converted requisition cannot be linked to a different RFQ.");
                    await reader.DisposeAsync().ConfigureAwait(false);
                    await t.CommitAsync(ct).ConfigureAwait(false);
                    return;
                }
                if(!string.Equals(status,"Approved",StringComparison.Ordinal))
                    throw new InvalidOperationException("Only an approved requisition can be converted.");
            }
            await using(var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Converted',rfq_doc_id=@rfq,conversion_started_at=NULL,updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Approved'"))
            {
                Add(update,"@rfq",rfqId);
                Add(update,"@c",Co(company));
                Add(update,"@id",id);
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("The requisition conversion state changed; reload and retry.");
            }
            await Event(c,t,company,id,"ConvertedToRfq",actor.Trim(),$"RFQ:{rfqId}",ct);
            await t.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
    private async Task Event(DbConnection c,DbTransaction t,Guid co,long id,string action,string actor,string? reason,CancellationToken ct){await using var q=Cmd(c,t,"INSERT INTO purchase_requisition_events(company_id,requisition_doc_id,action,actor,reason) VALUES(@c,@id,@a,@u,@r)");Add(q,"@c",Co(co));Add(q,"@id",id);Add(q,"@a",action);Add(q,"@u",actor);Add(q,"@r",(object?)reason??DBNull.Value);await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
    private object Co(Guid id)=>Pg?id:id.ToString("D");
    private static void Require(Guid c,long id){if(c==Guid.Empty)throw new ArgumentException("Company is required.");ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);}
    private DbConnection Open()=>Pg?new NpgsqlConnection(_connection):new SqliteConnection(_connection);
    private static DbCommand Cmd(DbConnection c,DbTransaction? t,string sql){var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;return q;}
    private static void Add(DbCommand q,string n,object v){var p=q.CreateParameter();p.ParameterName=n;p.Value=v;q.Parameters.Add(p);}
    private static long Num(DbDataReader r,int i)=>Convert.ToInt64(r.GetValue(i),CultureInfo.InvariantCulture);
    private static string? Text(DbDataReader r,int i)=>r.IsDBNull(i)?null:Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture);
}
