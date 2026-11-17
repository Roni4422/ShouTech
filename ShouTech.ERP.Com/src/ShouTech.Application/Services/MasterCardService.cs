using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>Master-data cards service (cost centers, banks, currencies, parties, sales reps).</summary>
public sealed class MasterCardService
{
    private readonly IMasterCardRepository _repository;
    public MasterCardService(IMasterCardRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    internal IMasterCardRepository Repository => _repository;
}
