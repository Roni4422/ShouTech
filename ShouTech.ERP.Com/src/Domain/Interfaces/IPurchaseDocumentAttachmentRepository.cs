using ShouTech.Domain.Entities;
namespace ShouTech.Domain.Interfaces;
public interface IPurchaseDocumentAttachmentRepository
{
    Task AddAsync(PurchaseDocumentAttachment attachment,CancellationToken ct=default);
    Task<IReadOnlyList<PurchaseDocumentAttachment>> ListAsync(Guid companyId,long documentId,CancellationToken ct=default);
    Task<PurchaseDocumentAttachment?> FindAsync(Guid companyId,Guid attachmentId,CancellationToken ct=default);
    Task SoftDeleteAsync(Guid companyId,Guid attachmentId,string actor,CancellationToken ct=default);
    Task RestoreAsync(Guid companyId,Guid attachmentId,CancellationToken ct=default);
}
