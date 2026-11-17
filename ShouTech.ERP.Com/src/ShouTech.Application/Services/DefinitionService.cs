using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>Generic definition-set service (voucher types, terminals, areas, targets, devices, drivers...).</summary>
public sealed class DefinitionService
{
    private readonly IDefinitionRepository _repository;
    public DefinitionService(IDefinitionRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IDefinitionRepository Repository => _repository;
}
