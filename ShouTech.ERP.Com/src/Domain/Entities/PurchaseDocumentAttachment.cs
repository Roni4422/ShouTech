namespace ShouTech.Domain.Entities;
public sealed record PurchaseDocumentAttachment(
    Guid Id, Guid CompanyId, long DocumentId, string StorageKey, string OriginalName,
    string MediaType, long ByteLength, string Sha256, string UploadedBy, DateTime UploadedAt);
