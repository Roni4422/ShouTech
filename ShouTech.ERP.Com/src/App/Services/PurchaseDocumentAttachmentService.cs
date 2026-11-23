using System.Security.Cryptography;
using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;
namespace ShouTech.Application.Services;

/// <summary>Secure company-scoped document attachments. File bytes are stored outside the database; only metadata and hashes are persisted.</summary>
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
    public async Task<PurchaseDocumentAttachment> UploadAsync(Guid companyId,long documentId,string fileName,string mediaType,Stream source,string actor,CancellationToken ct=default)
    {
        RequireCompany(companyId);ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentId);RequirePermission("PUR.ATTACH.UPLOAD");RequireActor(actor);
        if(source is null||!source.CanRead)throw new ArgumentException("Readable attachment content is required.",nameof(source));
        var safeName=Path.GetFileName(fileName??string.Empty).Trim();
        if(string.IsNullOrWhiteSpace(safeName)||safeName.Length>240||!AllowedExtensions.Contains(Path.GetExtension(safeName)))throw new InvalidOperationException("Attachment file type or name is not allowed.");
        var document=await _orders.FindByIdAsync(CommercialDocSide.Purchase,documentId,ct).ConfigureAwait(false);
        if(document is null||document.CompanyId!=companyId||document.DocKind is not (CommercialDocKinds.PurchaseOrder or CommercialDocKinds.PurchaseReceipt or CommercialDocKinds.PurchaseReceiptReturn or CommercialDocKinds.PurchaseInvoice or CommercialDocKinds.SupplierQuotation or CommercialDocKinds.RequestForQuotation or CommercialDocKinds.PurchaseRequisition))
            throw new InvalidOperationException("The purchase document was not found in the active company.");
        await using var memory=new MemoryStream();
        var buffer=new byte[81920];long length=0;
        while(true){var read=await source.ReadAsync(buffer.AsMemory(0,buffer.Length),ct).ConfigureAwait(false);if(read==0)break;length+=read;if(length>MaxBytes)throw new InvalidOperationException("Attachment exceeds the 25 MB limit.");await memory.WriteAsync(buffer.AsMemory(0,read),ct).ConfigureAwait(false);}
        if(length==0)throw new InvalidOperationException("Empty attachments are not allowed.");
        var bytes=memory.ToArray();ValidateSignature(Path.GetExtension(safeName),bytes);
        var id=Guid.NewGuid();var storageKey=id.ToString("N");var companyFolder=Path.Combine(_root,companyId.ToString("N"));Directory.CreateDirectory(companyFolder);
        var fullPath=SafePath(companyFolder,storageKey);await File.WriteAllBytesAsync(fullPath,bytes,ct).ConfigureAwait(false);
        var item=new PurchaseDocumentAttachment(id,companyId,documentId,storageKey,safeName,NormalizeMediaType(mediaType,Path.GetExtension(safeName)),length,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),actor.Trim(),DateTime.UtcNow);
        try{await _repository.AddAsync(item,ct).ConfigureAwait(false);await _audit(actor,"PurchaseAttachment.Upload",$"{companyId:D}:{documentId}:{id:D}:{item.Sha256}").ConfigureAwait(false);return item;}
        catch{File.Delete(fullPath);throw;}
    }
    public async Task<IReadOnlyList<PurchaseDocumentAttachment>> ListAsync(Guid companyId,long documentId,CancellationToken ct=default)
    {RequireCompany(companyId);RequirePermission("PUR.ATTACH.VIEW");var doc=await _orders.FindByIdAsync(CommercialDocSide.Purchase,documentId,ct).ConfigureAwait(false);if(doc is null||doc.CompanyId!=companyId)throw new InvalidOperationException("Purchase document was not found.");return await _repository.ListAsync(companyId,documentId,ct).ConfigureAwait(false);}
    public async Task<Stream> OpenReadAsync(Guid companyId,Guid attachmentId,CancellationToken ct=default)
    {RequireCompany(companyId);RequirePermission("PUR.ATTACH.VIEW");var item=await _repository.FindAsync(companyId,attachmentId,ct).ConfigureAwait(false)??throw new FileNotFoundException("Attachment was not found.");var path=SafePath(Path.Combine(_root,companyId.ToString("N")),item.StorageKey);if(!File.Exists(path))throw new FileNotFoundException("Attachment content is missing from secure storage.");return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous|FileOptions.SequentialScan);}
    public async Task DeleteAsync(Guid companyId,Guid attachmentId,string actor,CancellationToken ct=default)
    {RequireCompany(companyId);RequirePermission("PUR.ATTACH.DELETE");RequireActor(actor);var item=await _repository.FindAsync(companyId,attachmentId,ct).ConfigureAwait(false)??throw new FileNotFoundException("Attachment was not found.");await _repository.SoftDeleteAsync(companyId,attachmentId,actor,ct).ConfigureAwait(false);await _audit(actor,"PurchaseAttachment.Delete",$"{companyId:D}:{item.DocumentId}:{attachmentId:D}:{item.Sha256}").ConfigureAwait(false);}
    private void RequirePermission(string code){if(!_hasPermission(code))throw new UnauthorizedAccessException("You do not have permission to access purchase attachments.");}
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
