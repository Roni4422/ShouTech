namespace ShouTech.Domain.Entities;

/// <summary>
/// Attachment owner discriminators (migration 098): the 086 attachment
/// store is generalized, not duplicated — one table/service serves
/// purchase documents (legacy default), sales documents and parties.
/// </summary>
public static class AttachmentOwnerKind
{
    public const string PurchaseDocument = "PURDOC";
    public const string SalesDocument = "SALDOC";
    public const string Party = "PARTY";
}

/// <summary>
/// Attachment metadata. <see cref="DocumentId"/> is the numeric owner id
/// for document owners (0 for parties); <see cref="OwnerKind"/> /
/// <see cref="OwnerRef"/> carry the generalized owner identity
/// (owner_ref = document id as text, or the party id for parties).
/// </summary>
public sealed record PurchaseDocumentAttachment(
    Guid Id, Guid CompanyId, long DocumentId, string StorageKey, string OriginalName,
    string MediaType, long ByteLength, string Sha256, string UploadedBy, DateTime UploadedAt,
    string OwnerKind = AttachmentOwnerKind.PurchaseDocument, string? OwnerRef = null);
