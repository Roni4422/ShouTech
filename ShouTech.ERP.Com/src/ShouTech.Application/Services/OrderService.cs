using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>Sales/purchase order service over the commercial-document engine (orders post no GL/stock).</summary>
public sealed class OrderService
{
    private readonly ICommercialDocumentRepository _repository;
    public OrderService(ICommercialDocumentRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal ICommercialDocumentRepository Repository => _repository;
}
