using System.Security.Cryptography;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
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
