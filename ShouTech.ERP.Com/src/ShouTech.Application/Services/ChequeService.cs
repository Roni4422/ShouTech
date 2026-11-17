using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Cheque portfolio service: lifecycle transitions post through the journal
/// engine (the repository owns the cheques table; the journal repository owns
/// the GL side; both writes happen per-transition with status re-validation).
/// </summary>
public sealed class ChequeService
{
    private readonly IChequeRepository _repository;
    private readonly IJournalRepository? _journal;

    public ChequeService(IChequeRepository repository, IJournalRepository? journal = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _journal = journal;
    }

    internal IChequeRepository Repository => _repository;
    internal IJournalRepository? Journal => _journal;
}
