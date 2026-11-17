using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>Operations service: bank reconciliation marks, POS cash closings, support tickets.</summary>
public sealed class OperationsService
{
    private readonly IOperationsRepository _repository;
    public OperationsService(IOperationsRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IOperationsRepository Repository => _repository;
}
