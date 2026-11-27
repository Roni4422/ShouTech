using System.Security.Cryptography;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
using ShouTech.Domain;

namespace ShouTech.Application.Services;

/// <summary>
/// Secure company-scoped document attachments. File bytes are stored outside
/// the database; only metadata and hashes are persisted. Migration 098
/// generalized the 086 purchase store: the same service now serves purchase
/// documents (PURDOC), sales documents (SALDOC) and parties (PARTY) through
/// the owner discriminator — permissions switch with the owner kind
/// (PUR.ATTACH.* vs SALES.ATTACH.*), validation always resolves the owner
/// inside the active company first.
/// </summary>
public sealed class PurchaseDocumentAttachmentService
{
    private const long MaxBytes=25*1024*1024;
    private static readonly HashSet<string> AllowedExtensions=new(StringComparer.OrdinalIgnoreCase){".pdf",".png",".jpg",".jpeg",".xlsx",".docx",".csv"};
    private readonly IPurchaseDocumentAttachmentRepository _repository;
    private readonly OrderService _orders;
    private readonly Func<string,bool> _hasPermission;
    private readonly Func<string,string,string,Task> _audit;
    private readonly string _root;
    public PurchaseDocumentAttachmentService(IPurchaseDocumentAttachmentRepository repository,OrderService orders,Func<string,bool> hasPermission,Func<string,string,string,Task> audit,string? storageRoot=null)
    {
        _repository=repository??throw new ArgumentNullException(nameof(repository));_orders=orders??throw new ArgumentNullException(nameof(orders));
        _hasPermission=hasPermission??throw new ArgumentNullException(nameof(hasPermission));_audit=audit??throw new ArgumentNullException(nameof(audit));
        _root=Path.GetFullPath(storageRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShouTech","Attachments"));
    }

    // ------------------------------------------------------------ purchase (086)

    public async Task<PurchaseDocumentAttachment> UploadAsync(Guid companyId,long documentId,string fileName,string mediaType,Stream source,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentId);RequirePermission("PUR.ATTACH.UPLOAD");RequireActor(actor);
        var document=await _orders.FindByIdAsync(CommercialDocSide.Purchase,documentId,ct).ConfigureAwait(false);
        if(document is null||document.CompanyId!=companyId||document.DocKind is not (CommercialDocKinds.PurchaseOrder or CommercialDocKinds.PurchaseReceipt or CommercialDocKinds.PurchaseReceiptReturn or CommercialDocKinds.PurchaseInvoice or CommercialDocKinds.PurchaseReturn or CommercialDocKinds.SupplierQuotation or CommercialDocKinds.RequestForQuotation or CommercialDocKinds.PurchaseRequisition))
            throw new InvalidOperationException("The purchase document was not found in the active company.");
        return await UploadCoreAsync(companyId,AttachmentOwnerKind.PurchaseDocument,documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),documentId,fileName,mediaType,source,actor,"PurchaseAttachment",ct).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<PurchaseDocumentAttachment>> ListAsync(Guid companyId,long documentId,CancellationToken ct=default)
    {RequireCompany(companyId);RequirePermission("PUR.ATTACH.VIEW");var doc=await _orders.FindByIdAsync(CommercialDocSide.Purchase,documentId,ct).ConfigureAwait(false);if(doc is null||doc.CompanyId!=companyId)throw new InvalidOperationException("Purchase document was not found.");return await _repository.ListAsync(companyId,documentId,ct).ConfigureAwait(false);}

    // ------------------------------------------- generalized owners (W23, 098)

    /// <summary>Uploads an attachment for a sales document or a party.</summary>
    public async Task<PurchaseDocumentAttachment> UploadForOwnerAsync(Guid companyId,string ownerKind,string ownerRef,string fileName,string mediaType,Stream source,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);RequireActor(actor);
        var (documentId,auditPrefix)=await ValidateOwnerAsync(companyId,ownerKind,ownerRef,"SALES.ATTACH.UPLOAD",ct).ConfigureAwait(false);
        return await UploadCoreAsync(companyId,ownerKind,ownerRef,documentId,fileName,mediaType,source,actor,auditPrefix,ct).ConfigureAwait(false);
    }

    /// <summary>Lists the attachments of a sales document or a party.</summary>
    public async Task<IReadOnlyList<PurchaseDocumentAttachment>> ListForOwnerAsync(Guid companyId,string ownerKind,string ownerRef,CancellationToken ct=default)
    {
        RequireCompany(companyId);
        await ValidateOwnerAsync(companyId,ownerKind,ownerRef,"SALES.ATTACH.VIEW",ct).ConfigureAwait(false);
        return await _repository.ListByOwnerAsync(companyId,ownerKind,ownerRef,ct).ConfigureAwait(false);
    }

    /// <summary>Validates the generalized owner inside the company and the
    /// caller's owner-kind permission; returns the legacy numeric document
    /// id (0 for parties) and the audit prefix for the owner family.</summary>
    private async Task<(long DocumentId,string AuditPrefix)> ValidateOwnerAsync(Guid companyId,string ownerKind,string ownerRef,string permission,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(ownerRef))throw new ArgumentException("Attachment owner is required.",nameof(ownerRef));
        ownerRef=ownerRef.Trim();
        switch(ownerKind)
        {
            case AttachmentOwnerKind.SalesDocument:
            {
                RequirePermission(permission);
                if(!long.TryParse(ownerRef,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var documentId)||documentId<=0)
                    throw new ArgumentException("A sales document owner reference must be a document id.",nameof(ownerRef));
                var document=await _orders.FindByIdAsync(CommercialDocSide.Sales,documentId,ct).ConfigureAwait(false);
                if(document is null||document.CompanyId!=companyId||document.DocKind is not (CommercialDocKinds.SalesQuotation or CommercialDocKinds.SalesOrder or CommercialDocKinds.DeliveryNote or CommercialDocKinds.SalesInvoice))
                    throw new InvalidOperationException("The sales document was not found in the active company.");
                return (documentId,"SalesAttachment");
            }
            case AttachmentOwnerKind.Party:
            {
                RequirePermission(permission);
                if(!await _repository.PartyExistsAsync(companyId,ownerRef,ct).ConfigureAwait(false))
                    throw new InvalidOperationException("The party was not found in the active company.");
                return (0,"PartyAttachment");
            }
            default:
                throw new ArgumentException($"Unsupported attachment owner kind '{ownerKind}'.",nameof(ownerKind));
        }
    }

    // ------------------------------------------------------------ shared core

    private async Task<PurchaseDocumentAttachment> UploadCoreAsync(Guid companyId,string ownerKind,string ownerRef,long documentId,string fileName,string mediaType,Stream source,string actor,string auditPrefix,CancellationToken ct)
    {
        if(source is null||!source.CanRead)throw new ArgumentException("Readable attachment content is required.",nameof(source));
        var safeName=Path.GetFileName(fileName??string.Empty).Trim();
        if(string.IsNullOrWhiteSpace(safeName)||safeName.Length>240||!AllowedExtensions.Contains(Path.GetExtension(safeName)))throw new InvalidOperationException("Attachment file type or name is not allowed.");
        await using var memory=new MemoryStream();
        var buffer=new byte[81920];long length=0;
        while(true){var read=await source.ReadAsync(buffer.AsMemory(0,buffer.Length),ct).ConfigureAwait(false);if(read==0)break;length+=read;if(length>MaxBytes)throw new InvalidOperationException("Attachment exceeds the 25 MB limit.");await memory.WriteAsync(buffer.AsMemory(0,read),ct).ConfigureAwait(false);}
        if(length==0)throw new InvalidOperationException("Empty attachments are not allowed.");
        var bytes=memory.ToArray();ValidateSignature(Path.GetExtension(safeName),bytes);
        var id=Guid.NewGuid();var storageKey=id.ToString("N");var companyFolder=Path.Combine(_root,companyId.ToString("N"));Directory.CreateDirectory(companyFolder);
        var fullPath=SafePath(companyFolder,storageKey);await File.WriteAllBytesAsync(fullPath,bytes,ct).ConfigureAwait(false);
        var item=new PurchaseDocumentAttachment(id,companyId,documentId,storageKey,safeName,NormalizeMediaType(mediaType,Path.GetExtension(safeName)),length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),actor.Trim(),DateTime.UtcNow,ownerKind,ownerRef);
        try
        {
            await _repository.AddAsync(item,ct).ConfigureAwait(false);
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }
        try
        {
            await _audit(actor,$"{auditPrefix}.Upload",$"{companyId:D}:{ownerKind}:{ownerRef}:{id:D}:{item.Sha256}").ConfigureAwait(false);
        }
        catch (Exception auditError)
        {
            try
            {
                await _repository.SoftDeleteAsync(companyId, id, actor, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                // Keep the file when metadata rollback fails: an active
                // metadata row must never be left pointing at missing bytes.
                throw new AggregateException(
                    "Attachment audit failed and the metadata rollback also failed; the file was retained for recovery.",
                    auditError, rollbackError);
            }
            if (File.Exists(fullPath)) File.Delete(fullPath);
            throw;
        }
        return item;
    }

    public async Task<Stream> OpenReadAsync(Guid companyId,Guid attachmentId,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);
        RequireActor(actor);
        var item=await _repository.FindAsync(companyId,attachmentId,ct).ConfigureAwait(false)
            ??throw new FileNotFoundException("Attachment was not found.");
        RequirePermission(ViewPermissionFor(item.OwnerKind));
        var path=SafePath(Path.Combine(_root,companyId.ToString("N")),item.StorageKey);
        if(!File.Exists(path))
            throw new FileNotFoundException("Attachment content is missing from secure storage.");

        var bytes=await File.ReadAllBytesAsync(path,ct).ConfigureAwait(false);
        var actualHash=SHA256.HashData(bytes);
        byte[] expectedHash;
        try { expectedHash=Convert.FromHexString(item.Sha256); }
        catch(FormatException ex) { throw new InvalidDataException("Attachment integrity metadata is invalid.",ex); }
        if(bytes.LongLength!=item.ByteLength ||
           !CryptographicOperations.FixedTimeEquals(actualHash,expectedHash))
            throw new InvalidDataException("Attachment content failed its integrity check.");

        await _audit(actor,$"{AuditPrefixFor(item.OwnerKind)}.Download",$"{companyId:D}:{item.OwnerKind}:{item.OwnerRef}:{attachmentId:D}:{item.Sha256}").ConfigureAwait(false);
        return new MemoryStream(bytes,writable:false);
    }
    public async Task DeleteAsync(Guid companyId,Guid attachmentId,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);RequireActor(actor);
        var item=await _repository.FindAsync(companyId,attachmentId,ct).ConfigureAwait(false)
            ??throw new FileNotFoundException("Attachment was not found.");
        RequirePermission(DeletePermissionFor(item.OwnerKind));
        await _repository.SoftDeleteAsync(companyId,attachmentId,actor,ct).ConfigureAwait(false);
        try
        {
            await _audit(actor,$"{AuditPrefixFor(item.OwnerKind)}.Delete",$"{companyId:D}:{item.OwnerKind}:{item.OwnerRef}:{attachmentId:D}:{item.Sha256}").ConfigureAwait(false);
        }
        catch (Exception auditError)
        {
            try
            {
                await _repository.RestoreAsync(companyId, attachmentId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Attachment audit failed and the soft-delete rollback also failed.",
                    auditError, rollbackError);
            }
            throw;
        }
    }
    private static string AuditPrefixFor(string ownerKind)=>ownerKind switch{AttachmentOwnerKind.SalesDocument=>"SalesAttachment",AttachmentOwnerKind.Party=>"PartyAttachment",_=>"PurchaseAttachment"};
    private static string ViewPermissionFor(string ownerKind)=>ownerKind==AttachmentOwnerKind.PurchaseDocument?"PUR.ATTACH.VIEW":"SALES.ATTACH.VIEW";
    private static string DeletePermissionFor(string ownerKind)=>ownerKind==AttachmentOwnerKind.PurchaseDocument?"PUR.ATTACH.DELETE":"SALES.ATTACH.DELETE";
    private void RequirePermission(string code){if(!_hasPermission(code))throw new UnauthorizedAccessException("You do not have permission to access attachments.");}
    private static void RequireCompany(Guid id){if(id==Guid.Empty)throw new ArgumentException("Company is required.",nameof(id));}
    private static void RequireActor(string actor){if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Actor is required.",nameof(actor));}
    private static string SafePath(string folder,string key){if(key.Length!=32||key.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidOperationException("Invalid attachment storage key.");var full=Path.GetFullPath(Path.Combine(folder,key));var root=Path.GetFullPath(folder)+Path.DirectorySeparatorChar;if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Attachment path escaped its company folder.");return full;}
    private static string NormalizeMediaType(string? value,string extension)=>extension.ToLowerInvariant() switch{".pdf"=>"application/pdf",".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".xlsx"=>"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document",".csv"=>"text/csv",_=>string.IsNullOrWhiteSpace(value)?"application/octet-stream":value.Trim()};
    private static void ValidateSignature(string extension,byte[] bytes)
    {
        var valid=extension.ToLowerInvariant() switch
        {
            ".pdf"=>bytes.Length>=5&&bytes.AsSpan(0,5).SequenceEqual("%PDF-"u8),
            ".png"=>bytes.Length>=8&&bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),
            ".jpg" or ".jpeg"=>bytes.Length>=3&&bytes[0]==0xff&&bytes[1]==0xd8&&bytes[2]==0xff,
            ".xlsx" or ".docx"=>bytes.Length>=4&&bytes[0]==0x50&&bytes[1]==0x4b&&bytes[2] is 0x03 or 0x05 or 0x07,
            ".csv"=>!bytes.AsSpan(0,Math.Min(bytes.Length,512)).Contains((byte)0),
            _=>false
        };
        if(!valid)throw new InvalidOperationException("Attachment content does not match its file type.");
    }
}

// ---- merged from src/App/Services/PurchaseLandedCostService.cs ----
/// <summary>
/// Authorization boundary for purchase landed-cost drafts, posting and
/// reversal. Every operation rechecks the current user's permission.
/// </summary>
public sealed class PurchaseLandedCostService
{
    private readonly IPurchaseLandedCostRepository _repository;
    private readonly Func<string, bool> _hasPermission;

    public PurchaseLandedCostService(IPurchaseLandedCostRepository repository, Func<string, bool> hasPermission)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _hasPermission = hasPermission ?? throw new ArgumentNullException(nameof(hasPermission));
    }

    public Task<IReadOnlyList<PurchaseLandedCostSummary>> ListAsync(
        Guid companyId, string? search = null, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListAsync(companyId, search, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostReceiptOption>> ListReceiptsAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListReceiptsAsync(companyId, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostReceiptLineOption>> ListReceiptLinesAsync(
        Guid companyId, long receiptDocumentId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListReceiptLinesAsync(companyId, receiptDocumentId, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostPayeeOption>> ListPayeesAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListPayeesAsync(companyId, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseLandedCostLinkableDocumentOption>> ListLinkableDocumentsAsync(
        Guid companyId, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.VIEW");
        return _repository.ListLinkableDocumentsAsync(companyId, cancellationToken);
    }

    public Task<Guid> CreateDraftAsync(Guid companyId, IReadOnlyList<PurchaseLandedCostReceiptSelection> receipts, string documentNumber,
        DateOnly documentDate, string currencyCode, decimal exchangeRate,
        IReadOnlyList<PurchaseLandedCostChargeDraft> charges, string actor,
        CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.EDIT");
        RequireActor(actor);
        return _repository.CreateDraftAsync(companyId, receipts, documentNumber,
            documentDate, currencyCode, exchangeRate, charges, actor, cancellationToken);
    }

    public Task<Guid> PostAsync(Guid companyId, Guid landedCostId, string actor,
        CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.POST");
        RequireActor(actor);
        return _repository.PostAsync(companyId, landedCostId, actor, cancellationToken);
    }

    public Task<Guid> ReverseAsync(Guid companyId, Guid landedCostId, string reversalNumber,
        DateOnly reversalDate, string reason, string actor, CancellationToken cancellationToken = default)
    {
        Require("PUR.LANDEDCOST.REVERSE");
        RequireActor(actor);
        return _repository.ReverseAsync(companyId, landedCostId, reversalNumber,
            reversalDate, reason, actor, cancellationToken);
    }

    private void Require(string permission)
    {
        if (!_hasPermission(permission))
            throw new UnauthorizedAccessException($"Permission required: {permission}.");
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }
}

// ---- merged from src/App/Services/PurchaseMatchService.cs ----
/// <summary>
/// Application boundary for purchase match settings and exception releases.
/// Authorization is evaluated on every call so a stale window cannot retain
/// privileges after the current user's permission set changes.
/// </summary>
public sealed class PurchaseMatchService
{
    private readonly IPurchaseMatchPolicyRepository _repository;
    private readonly Func<string, bool> _hasPermission;
    private readonly Func<string, string, string, Task>? _audit;

    public PurchaseMatchService(
        IPurchaseMatchPolicyRepository repository,
        Func<string, bool> hasPermission,
        Func<string, string, string, Task>? audit = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _hasPermission = hasPermission ?? throw new ArgumentNullException(nameof(hasPermission));
        _audit = audit;
    }

    public Task<PurchaseMatchPolicy?> GetPolicyAsync(string companyCode, CancellationToken cancellationToken = default)
    {
        RequirePermission("PURCH.MATCH.POLICY.MANAGE");
        return _repository.GetPolicyAsync(RequireCompany(companyCode), cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseMatchEvaluationSnapshot>> GetRecentEvaluationsAsync(
        string companyCode,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        RequireAnyPermission("PURCH.VIEW", "PURCH.REPORTS.VIEW",
            "PURCH.MATCH.POLICY.MANAGE", "PURCH.MATCH.OVERRIDE");
        return _repository.GetRecentEvaluationsAsync(RequireCompany(companyCode), take, cancellationToken);
    }

    public async Task SavePolicyAsync(
        string companyCode,
        PurchaseMatchPolicy policy,
        int expectedVersion,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequirePermission("PURCH.MATCH.POLICY.MANAGE");
        RequireActor(actor);
        await _repository.SavePolicyAsync(
            RequireCompany(companyCode), policy, expectedVersion, actor, cancellationToken).ConfigureAwait(false);
        if (_audit is not null)
            await _audit(actor, "Purchase match policy updated",
                $"Company={companyCode}; Enabled={policy.IsEnabled}; ExpectedVersion={expectedVersion}.").ConfigureAwait(false);
    }

    public async Task<PurchaseMatchEvaluationSnapshot> ReevaluateInvoiceAsync(
        string companyCode,
        long invoiceDocId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequirePermission("PURCH.MATCH.POLICY.MANAGE");
        RequireActor(actor);
        var result = await _repository.ReevaluateLatestAsync(
            RequireCompany(companyCode), invoiceDocId, actor, cancellationToken).ConfigureAwait(false);
        if (_audit is not null)
            await _audit(actor, "Purchase invoice match re-evaluated",
                $"Company={companyCode}; Invoice={invoiceDocId}; Evaluation={result.EvaluationId:D}; PolicyVersion={result.PolicyVersion}; Decision={result.Decision}.").ConfigureAwait(false);
        return result;
    }

    public async Task<Guid> ReleaseBlockedEvaluationAsync(
        string companyCode,
        long invoiceDocId,
        Guid evaluationId,
        string actor,
        string reason,
        CancellationToken cancellationToken = default)
    {
        RequirePermission("PURCH.MATCH.OVERRIDE");
        RequireActor(actor);
        var overrideId = await _repository.RecordOverrideAsync(
            RequireCompany(companyCode), invoiceDocId, evaluationId, actor, reason, cancellationToken).ConfigureAwait(false);
        if (_audit is not null)
            await _audit(actor, "Purchase match block released",
                $"Company={companyCode}; Invoice={invoiceDocId}; Evaluation={evaluationId:D}; Override={overrideId:D}; Reason={reason.Trim()}.").ConfigureAwait(false);
        return overrideId;
    }

    private void RequirePermission(string permission)
    {
        if (!_hasPermission(permission))
            throw new UnauthorizedAccessException($"Permission required: {permission}.");
    }

    private void RequireAnyPermission(params string[] permissions)
    {
        if (!permissions.Any(_hasPermission))
            throw new UnauthorizedAccessException("A purchasing view or match-control permission is required.");
    }

    private static string RequireCompany(string companyCode)
    {
        if (string.IsNullOrWhiteSpace(companyCode))
            throw new ArgumentException("A company code is required.", nameof(companyCode));
        return companyCode.Trim();
    }

    private static void RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An authenticated actor is required.", nameof(actor));
    }
}

// ---- merged from src/App/Services/PurchaseRequisitionWorkflowService.cs ----
/// <summary>Sequential approval workflow for non-posting internal purchase requisitions stored as PRQ commercial drafts.</summary>
public sealed class PurchaseRequisitionWorkflowService
{
    private readonly IPurchaseRequisitionWorkflowRepository _repository;
    private readonly OrderService _orders;
    private readonly Func<string,bool> _hasPermission;
    public PurchaseRequisitionWorkflowService(IPurchaseRequisitionWorkflowRepository repository,OrderService orders,Func<string,bool> hasPermission)
    { _repository=repository??throw new ArgumentNullException(nameof(repository));_orders=orders??throw new ArgumentNullException(nameof(orders));_hasPermission=hasPermission??throw new ArgumentNullException(nameof(hasPermission)); }

    public async Task<long> CreateDraftAsync(CommercialDocumentDraft draft,string actor,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.EDIT");
        ArgumentNullException.ThrowIfNull(draft);
        if(draft.CompanyId==Guid.Empty||draft.DocKind!=CommercialDocKinds.PurchaseRequisition||draft.Lines.Count==0)
            throw new InvalidOperationException("A purchase requisition draft requires a company and at least one line.");
        if(draft.PartyId is not null) throw new InvalidOperationException("An internal purchase requisition must not select a supplier before approval.");
        if(draft.Lines.Any(x=>x.Qty<=0||x.UnitPrice<0||string.IsNullOrWhiteSpace(x.Description)))
            throw new InvalidOperationException("Requisition lines require a description, positive quantity, and non-negative estimate.");
        return await _orders.Orders.CreateDraftAsync(CommercialDocSide.Purchase,draft,RequireActor(actor),ct).ConfigureAwait(false);
    }

    public Task<PurchaseRequisitionWorkflow?> GetAsync(Guid companyId,long id,CancellationToken ct=default)
    {
        if(!_hasPermission("PUR.REQ.VIEW")&&!_hasPermission("PUR.REQ.APPROVE")&&!_hasPermission("PUR.REQ.CONVERT"))throw new AppError(ErrorCodes.PermissionDenied,"Purchase requisition view permission is required.");
        return _repository.GetAsync(RequireCompany(companyId),id,ct);
    }

    public async Task UpdateDraftAsync(Guid companyId,long id,CommercialDocumentDraft draft,string actor,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.EDIT");
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);ArgumentNullException.ThrowIfNull(draft);
        if(draft.CompanyId!=companyId||draft.DocKind!=CommercialDocKinds.PurchaseRequisition||draft.PartyId is not null||draft.Lines.Count==0)
            throw new InvalidOperationException("A requisition update must retain its company, kind, supplier-neutral status, and at least one line.");
        if(draft.Lines.Any(x=>x.Qty<=0||x.UnitPrice<0||string.IsNullOrWhiteSpace(x.Description)))
            throw new InvalidOperationException("Requisition lines require descriptions, positive quantities, and non-negative estimates.");
        var workflow=await _repository.GetAsync(companyId,id,ct).ConfigureAwait(false);
        // R3 (migration 107): a rejected requisition returns to editable
        // draft state so it can be corrected and resubmitted as a new
        // approval round. Every other workflow state locks the document.
        if(workflow is not null&&workflow.Status!="Rejected")
            throw new AppError(ErrorCodes.RequisitionNotEditable,"A requisition can only be edited before submission or after rejection.");
        await _orders.UpdatePurchaseRequisitionDraftAsync(id,draft,RequireActor(actor),ct).ConfigureAwait(false);
    }

    public Task CancelAsync(Guid companyId,long id,string actor,string? reason,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.CANCEL");
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        if(string.IsNullOrWhiteSpace(reason))throw new AppError(ErrorCodes.RequisitionReasonRequired,"A cancellation reason is required.");
        return _repository.CancelAsync(companyId,id,RequireActor(actor),reason.Trim(),ct);
    }

    public Task CloseAsync(Guid companyId,long id,string actor,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.CLOSE");
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return _repository.CloseAsync(companyId,id,RequireActor(actor),ct);
    }



    public Task SubmitAsync(Guid companyId,long id,IReadOnlyList<PurchaseRequisitionApprovalStep> steps,string actor,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.EDIT");
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        if(steps is null||steps.Count==0)throw new InvalidOperationException("At least one approval step is required.");
        var ordered=steps.OrderBy(x=>x.StepNo).ToArray();
        if(ordered.Any(x=>x.StepNo<=0||string.IsNullOrWhiteSpace(x.ApproverUserId))||
           ordered.Select(x=>x.StepNo).Distinct().Count()!=ordered.Length||
           ordered.Select(x=>x.StepNo).Where((step,index)=>step!=index+1).Any()||
           ordered.Select(x=>x.ApproverUserId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=ordered.Length)
            throw new InvalidOperationException("Approval steps must be contiguous from one and approvers must be unique.");
        return _repository.SubmitAsync(companyId,id,ordered,RequireActor(actor),ct);
    }

    public Task DecideAsync(Guid companyId,long id,long approvalId,string approver,bool approve,string? reason,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.APPROVE");
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(approvalId);
        if(!approve&&string.IsNullOrWhiteSpace(reason))throw new InvalidOperationException("A rejection reason is required.");
        return _repository.DecideAsync(companyId,id,approvalId,RequireActor(approver),approve,reason,ct);
    }

    public async Task<PurchaseRequisitionConversion> ConvertToPurchaseOrderDraftAsync(Guid companyId,long requisitionId,long supplierId,string supplierName,string currencyCode,decimal exchangeRate,long? warehouseId,string actor,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.CONVERT");
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requisitionId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(supplierId);
        if(string.IsNullOrWhiteSpace(supplierName)||string.IsNullOrWhiteSpace(currencyCode)||exchangeRate<=0)throw new InvalidOperationException("Supplier, currency, and positive exchange rate are required.");
        var workflow=await _repository.GetAsync(companyId,requisitionId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Purchase requisition workflow was not found.");
        if(workflow.Status=="Converted"&&workflow.PurchaseOrderId is long existingId)
        {
            var existing=await _orders.FindByIdAsync(CommercialDocSide.Purchase,existingId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Linked purchase order is missing.");
            return new(existing.DocId,existing.DocNo);
        }
        var link=(await _orders.Orders.GetLinksFromAsync(companyId,requisitionId,ct).ConfigureAwait(false))
            .FirstOrDefault(x=>x.SourceDocKind==CommercialDocKinds.PurchaseRequisition&&x.TargetDocKind==CommercialDocKinds.PurchaseOrder);
        if(link is not null)
        {
            var existing=await _orders.FindByIdAsync(CommercialDocSide.Purchase,link.TargetDocId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Linked purchase order is missing.");
            await _repository.MarkConvertedAsync(companyId,requisitionId,existing.DocId,RequireActor(actor),ct).ConfigureAwait(false);
            return new(existing.DocId,existing.DocNo);
        }
        if(workflow.Status!="Approved")throw new InvalidOperationException("Only an approved requisition that is not already converting can be converted.");
        var requisition=await _orders.FindByIdAsync(CommercialDocSide.Purchase,requisitionId,ct).ConfigureAwait(false)
            ??throw new InvalidOperationException("Purchase requisition document was not found.");
        if(requisition.DocKind!=CommercialDocKinds.PurchaseRequisition)throw new InvalidOperationException("Source document is not a purchase requisition.");
        var number=await _orders.NextDocNoAsync(CommercialDocSide.Purchase,companyId,CommercialDocKinds.PurchaseOrder,ct).ConfigureAwait(false);
        var lines=requisition.Lines.Select(x=>new CommercialLineDraft(x.MaterialId,x.MaterialCode,x.Description,x.Unit,x.Qty,x.UnitPrice,x.DiscountPct,0m,x.WarehouseId??warehouseId,x.LineNo)).ToArray();
        var draft=new CommercialDocumentDraft(companyId,CommercialDocKinds.PurchaseOrder,number,DateOnly.FromDateTime(DateTime.Today),supplierId,supplierName.Trim(),warehouseId,currencyCode.Trim().ToUpperInvariant(),exchangeRate,$"Created from requisition {requisition.DocNo}",lines);
        var conversionActor=RequireActor(actor);
        await _repository.BeginConversionAsync(companyId,requisitionId,conversionActor,ct).ConfigureAwait(false);
        try
        {
            var poId=await _orders.Orders.CreateLinkedDraftAsync(
                CommercialDocSide.Purchase,draft,CommercialDocKinds.PurchaseRequisition,
                requisitionId,conversionActor,ct).ConfigureAwait(false);
            await _repository.MarkConvertedAsync(companyId,requisitionId,poId,conversionActor,ct).ConfigureAwait(false);
            return new(poId,number);
        }
        catch(Exception conversionError)
        {
            try
            {
                await _repository.AbortConversionAsync(companyId,requisitionId,conversionActor,
                    conversionError.Message,CancellationToken.None).ConfigureAwait(false);
            }
            catch(Exception rollbackError)
            {
                throw new AggregateException(
                    "Purchase-order creation failed and the requisition conversion state could not be reconciled.",
                    conversionError,rollbackError);
            }
            throw;
        }
    }
    /// <summary>R4 (migration 107): convert an approved requisition into
    /// a supplier-neutral RFQ draft, with the same reservation /
    /// idempotency discipline as the PO conversion: an existing RFQ link
    /// is returned instead of duplicated, the conversion is claimed via
    /// BeginConversionAsync, and a failed creation aborts the claim.</summary>
    public async Task<PurchaseRequisitionRfqConversion> ConvertToRequestForQuotationDraftAsync(Guid companyId,long requisitionId,long? warehouseId,string actor,CancellationToken ct=default)
    {
        RequirePermission("PUR.REQ.CONVERT");
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requisitionId);
        var workflow=await _repository.GetAsync(companyId,requisitionId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Purchase requisition workflow was not found.");
        if(workflow.Status=="Converted"&&workflow.RfqDocId is long existingRfqId)
        {
            var existing=await _orders.FindByIdAsync(CommercialDocSide.Purchase,existingRfqId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Linked request for quotation is missing.");
            return new(existing.DocId,existing.DocNo);
        }
        var link=(await _orders.Orders.GetLinksFromAsync(companyId,requisitionId,ct).ConfigureAwait(false))
            .FirstOrDefault(x=>x.SourceDocKind==CommercialDocKinds.PurchaseRequisition&&x.TargetDocKind==CommercialDocKinds.RequestForQuotation);
        if(link is not null)
        {
            var existing=await _orders.FindByIdAsync(CommercialDocSide.Purchase,link.TargetDocId,ct).ConfigureAwait(false)??throw new InvalidOperationException("Linked request for quotation is missing.");
            await _repository.MarkConvertedToRfqAsync(companyId,requisitionId,existing.DocId,RequireActor(actor),ct).ConfigureAwait(false);
            return new(existing.DocId,existing.DocNo);
        }
        if(workflow.Status!="Approved")throw new InvalidOperationException("Only an approved requisition that is not already converting can be converted.");
        var requisition=await _orders.FindByIdAsync(CommercialDocSide.Purchase,requisitionId,ct).ConfigureAwait(false)
            ??throw new InvalidOperationException("Purchase requisition document was not found.");
        if(requisition.DocKind!=CommercialDocKinds.PurchaseRequisition)throw new InvalidOperationException("Source document is not a purchase requisition.");
        var number=await _orders.NextDocNoAsync(CommercialDocSide.Purchase,companyId,CommercialDocKinds.RequestForQuotation,ct).ConfigureAwait(false);
        var lines=requisition.Lines.Select(x=>new CommercialLineDraft(x.MaterialId,x.MaterialCode,x.Description,x.Unit,x.Qty,x.UnitPrice,x.DiscountPct,0m,x.WarehouseId??warehouseId,x.LineNo)).ToArray();
        var draft=new CommercialDocumentDraft(companyId,CommercialDocKinds.RequestForQuotation,number,DateOnly.FromDateTime(DateTime.Today),null,null,warehouseId,requisition.CurrencyCode,requisition.ExchangeRate,$"Created from requisition {requisition.DocNo}",lines);
        var conversionActor=RequireActor(actor);
        await _repository.BeginConversionAsync(companyId,requisitionId,conversionActor,ct).ConfigureAwait(false);
        try
        {
            var rfqId=await _orders.Orders.CreateLinkedDraftAsync(
                CommercialDocSide.Purchase,draft,CommercialDocKinds.PurchaseRequisition,
                requisitionId,conversionActor,ct).ConfigureAwait(false);
            await _repository.MarkConvertedToRfqAsync(companyId,requisitionId,rfqId,conversionActor,ct).ConfigureAwait(false);
            return new(rfqId,number);
        }
        catch(Exception conversionError)
        {
            try
            {
                await _repository.AbortConversionAsync(companyId,requisitionId,conversionActor,
                    conversionError.Message,CancellationToken.None).ConfigureAwait(false);
            }
            catch(Exception rollbackError)
            {
                throw new AggregateException(
                    "RFQ creation failed and the requisition conversion state could not be reconciled.",
                    conversionError,rollbackError);
            }
            throw;
        }
    }
    /// <summary>Service-layer enforcement (owner report section 11 item
    /// 6): every public operation re-checks its PUR.REQ.* permission here,
    /// not only in the window, and a denial is a catalogued error
    /// (AUTH-2005) so it resolves through the error catalog like every
    /// other deliberate failure.</summary>
    private void RequirePermission(string code){if(!_hasPermission(code))throw new AppError(ErrorCodes.PermissionDenied,$"Missing permission: {code}.");}
    private static Guid RequireCompany(Guid id)=>id==Guid.Empty?throw new ArgumentException("Company is required.",nameof(id)):id;
    private static string RequireActor(string value)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("Actor is required.",nameof(value)):value.Trim();
}
