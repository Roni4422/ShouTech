using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
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
        await using(var q=Cmd(c,null,"SELECT status,purchase_order_id FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id")){Add(q,"@c",Co(company));Add(q,"@id",id);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);if(await r.ReadAsync(ct).ConfigureAwait(false))w=new(){CompanyId=company,RequisitionId=id,Status=Text(r,0)??"Draft",PurchaseOrderId=r.IsDBNull(1)?null:Num(r,1)};}
        if(w is null)return null;
        await using(var q=Cmd(c,null,"SELECT approval_id,step_no,approver_user_id,status,reason,decided_at FROM purchase_requisition_approvals WHERE company_id=@c AND requisition_doc_id=@id ORDER BY step_no")){Add(q,"@c",Co(company));Add(q,"@id",id);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);while(await r.ReadAsync(ct).ConfigureAwait(false))w.Approvals.Add(new(){ApprovalId=Num(r,0),StepNo=Convert.ToInt32(r.GetValue(1),CultureInfo.InvariantCulture),ApproverUserId=Text(r,2)??"",Status=Text(r,3)??"Pending",Reason=Text(r,4),DecidedAt=r.IsDBNull(5)?null:Convert.ToDateTime(r.GetValue(5),CultureInfo.InvariantCulture)});}
        return w;
    }
    public async Task SubmitAsync(Guid company,long id,IReadOnlyList<PurchaseRequisitionApprovalStep> steps,string actor,CancellationToken ct=default)
    {
        Require(company,id);await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try{
            await using(var check=Cmd(c,t,"SELECT created_by FROM purchase_documents WHERE company_id=@c AND doc_id=@id AND doc_kind='PRQ' AND status='Draft' AND is_deleted=0")){Add(check,"@c",Co(company));Add(check,"@id",id);var owner=Convert.ToString(await check.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture);if(string.IsNullOrWhiteSpace(owner))throw new InvalidOperationException("Only a draft purchase requisition can be submitted.");}
            await using(var insert=Cmd(c,t,"INSERT INTO purchase_requisition_workflow(company_id,requisition_doc_id,status) VALUES(@c,@id,'Submitted')")){Add(insert,"@c",Co(company));Add(insert,"@id",id);await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
            foreach(var s in steps){await using var q=Cmd(c,t,"INSERT INTO purchase_requisition_approvals(company_id,requisition_doc_id,step_no,approver_user_id,status) VALUES(@c,@id,@step,@user,'Pending')");Add(q,"@c",Co(company));Add(q,"@id",id);Add(q,"@step",s.StepNo);Add(q,"@user",s.ApproverUserId.Trim());await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
            await Event(c,t,company,id,"Submitted",actor,null,ct);await t.CommitAsync(ct).ConfigureAwait(false);
        }catch{await t.RollbackAsync(CancellationToken.None).ConfigureAwait(false);throw;}
    }
    public async Task DecideAsync(Guid company,long id,long approvalId,string approver,bool approve,string? reason,CancellationToken ct=default)
    {
        Require(company,id);await using var c=Open();await c.OpenAsync(ct).ConfigureAwait(false);await using var t=await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        try{
            await using(var q=Cmd(c,t,"SELECT d.created_by FROM purchase_documents d JOIN purchase_requisition_workflow w ON w.company_id=d.company_id AND w.requisition_doc_id=d.doc_id WHERE d.company_id=@c AND d.doc_id=@id AND d.doc_kind='PRQ' AND w.status='Submitted'"+(Pg?" FOR UPDATE OF w":string.Empty))){Add(q,"@c",Co(company));Add(q,"@id",id);var owner=Convert.ToString(await q.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)??throw new InvalidOperationException("Only a submitted requisition can be decided.");if(string.Equals(owner,approver,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The requester cannot approve their own requisition.");}
            await using(var q=Cmd(c,t,"SELECT approval_id,approver_user_id FROM purchase_requisition_approvals WHERE company_id=@c AND requisition_doc_id=@id AND status='Pending' AND step_no=(SELECT MIN(step_no) FROM purchase_requisition_approvals WHERE company_id=@c AND requisition_doc_id=@id AND status='Pending') ORDER BY approval_id LIMIT 1")){Add(q,"@c",Co(company));Add(q,"@id",id);await using var r=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);if(!await r.ReadAsync(ct).ConfigureAwait(false)||Num(r,0)!=approvalId||!string.Equals(Text(r,1),approver,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Approval is not assigned to this user or is not the active step.");}
            await using(var q=Cmd(c,t,"UPDATE purchase_requisition_approvals SET status=@s,reason=@r,decided_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND approval_id=@a AND status='Pending'")){Add(q,"@s",approve?"Approved":"Rejected");Add(q,"@r",(object?)reason?.Trim()??DBNull.Value);Add(q,"@c",Co(company));Add(q,"@id",id);Add(q,"@a",approvalId);if(await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new InvalidOperationException("Approval was already decided.");}
            var status=approve?"Approved":"Rejected";if(approve){await using var q=Cmd(c,t,"SELECT COUNT(*) FROM purchase_requisition_approvals WHERE company_id=@c AND requisition_doc_id=@id AND status='Pending'");Add(q,"@c",Co(company));Add(q,"@id",id);if(Convert.ToInt32(await q.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)>0)status="Submitted";}
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
            await using(var current=Cmd(c,t,
                "SELECT status,purchase_order_id FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty)))
            {
                Add(current,"@c",Co(company));Add(current,"@id",id);
                await using var reader=await current.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Purchase requisition workflow was not found.");
                status=Text(reader,0)??"";
                existingPo=reader.IsDBNull(1)?null:Num(reader,1);
            }
            if(status=="Converted")
                throw new InvalidOperationException(existingPo is null
                    ? "The requisition is marked converted but has no linked purchase order."
                    : "The requisition has already been converted.");
            if(existingPo is not null)
                throw new InvalidOperationException("The requisition already carries a purchase-order link and cannot start another conversion.");
            if(status=="Converting")
                throw new InvalidOperationException("This requisition is already being converted to a purchase order.");
            if(status!="Approved")
                throw new InvalidOperationException("Only an approved requisition can begin conversion.");
            await using(var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Converting',updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Approved'"))
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
            await using(var current=Cmd(c,t,
                "SELECT status FROM purchase_requisition_workflow WHERE company_id=@c AND requisition_doc_id=@id"+(Pg?" FOR UPDATE":string.Empty)))
            {
                Add(current,"@c",Co(company));Add(current,"@id",id);
                status=Convert.ToString(await current.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)??"";
            }
            if(status!="Converting")
            {
                await t.CommitAsync(ct).ConfigureAwait(false);
                return;
            }
            await using(var linked=Cmd(c,t,"SELECT COUNT(*) FROM document_links WHERE company_id=@c AND source_doc_kind='PRQ' AND source_doc_id=@id AND target_doc_kind='PO'"))
            {
                Add(linked,"@c",Co(company));Add(linked,"@id",id);
                if(Convert.ToInt64(await linked.ExecuteScalarAsync(ct).ConfigureAwait(false),CultureInfo.InvariantCulture)>0)
                {
                    await t.CommitAsync(ct).ConfigureAwait(false);
                    return;
                }
            }
            await using(var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Approved',updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status='Converting'"))
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
            await using(var update=Cmd(c,t,"UPDATE purchase_requisition_workflow SET status='Converted',purchase_order_id=@po,updated_at=CURRENT_TIMESTAMP WHERE company_id=@c AND requisition_doc_id=@id AND status IN ('Approved','Converting')"))
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
    private async Task Event(DbConnection c,DbTransaction t,Guid co,long id,string action,string actor,string? reason,CancellationToken ct){await using var q=Cmd(c,t,"INSERT INTO purchase_requisition_events(company_id,requisition_doc_id,action,actor,reason) VALUES(@c,@id,@a,@u,@r)");Add(q,"@c",Co(co));Add(q,"@id",id);Add(q,"@a",action);Add(q,"@u",actor);Add(q,"@r",(object?)reason??DBNull.Value);await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);}
    private object Co(Guid id)=>Pg?id:id.ToString("D");
    private static void Require(Guid c,long id){if(c==Guid.Empty)throw new ArgumentException("Company is required.");ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);}
    private DbConnection Open()=>Pg?new NpgsqlConnection(_connection):new SqliteConnection(_connection);
    private static DbCommand Cmd(DbConnection c,DbTransaction? t,string sql){var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;return q;}
    private static void Add(DbCommand q,string n,object v){var p=q.CreateParameter();p.ParameterName=n;p.Value=v;q.Parameters.Add(p);}
    private static long Num(DbDataReader r,int i)=>Convert.ToInt64(r.GetValue(i),CultureInfo.InvariantCulture);
    private static string? Text(DbDataReader r,int i)=>r.IsDBNull(i)?null:Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture);
}
