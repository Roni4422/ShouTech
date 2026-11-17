using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>Manufacturing orders service: posting consumes/produces stock transactionally.</summary>
public sealed class MfgOrderService
{
    private readonly IMfgOrderRepository _repository;
    public MfgOrderService(IMfgOrderRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IMfgOrderRepository Repository => _repository;
}
