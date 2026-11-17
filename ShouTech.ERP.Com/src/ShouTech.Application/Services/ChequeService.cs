using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Cheque portfolio service (RS-4): lifecycle transitions post through the
/// journal engine. Posting map:
///   IN save    : Dr Portfolio/Box (total), Cr Party (per cheque) -> InPortfolio
///   IN Deposit : no GL (bank + date only)                        -> Deposited
///   IN Clear   : Dr Deposit Bank GL, Cr Portfolio                -> Cleared
///   IN Bounce  : reverse the receipt entry                       -> Bounced
///   IN Endorse : Dr Endorsed-To account, Cr Portfolio            -> Endorsed
///   OUT save   : Dr Party (per cheque), Cr Box (total)           -> Issued
///   OUT Clear  : no GL (status + date only)                      -> Cleared
///   OUT Cancel : reverse the payment entry                       -> Cancelled
/// Every transition is validated against the legal-transitions table here and
/// re-validated against the stored status inside the repository transaction.
/// Transitions that move GL require a journal repository; without one they
/// fail honestly instead of recording a status-only fiction.
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

    // ------------------------------------------------------------------
    // Legal transitions (direction, from) -> allowed targets.
    // ------------------------------------------------------------------
    private static readonly IReadOnlyDictionary<(string Direction, string From), string[]> Legal = new Dictionary<(string, string), string[]>
    {
        [(ChequeDirections.In, ChequeStatuses.InPortfolio)] = new[] { ChequeStatuses.Deposited, ChequeStatuses.Endorsed, ChequeStatuses.Bounced },
        [(ChequeDirections.In, ChequeStatuses.Deposited)] = new[] { ChequeStatuses.Cleared, ChequeStatuses.Bounced },
        [(ChequeDirections.Out, ChequeStatuses.Issued)] = new[] { ChequeStatuses.Cleared, ChequeStatuses.Cancelled },
    };

    public static bool CanTransition(string direction, string fromStatus, string toStatus)
        => Legal.TryGetValue((direction, fromStatus), out var targets) && targets.Contains(toStatus);

    public static void EnsureTransition(Cheque cheque, string toStatus)
    {
        ArgumentNullException.ThrowIfNull(cheque);
        if (!CanTransition(cheque.Direction, cheque.Status, toStatus))
            throw new InvalidOperationException(
                $"Cheque in status '{cheque.Status}' ({cheque.Direction}) cannot move to '{toStatus}'.");
    }

    // ------------------------------------------------------------------
    // Queries
    // ------------------------------------------------------------------
    public Task<IReadOnlyList<Cheque>> GetAllAsync(string companyId, string? status = null, string? direction = null, CancellationToken cancellationToken = default)
        => _repository.GetAllAsync(RequireCompany(companyId), status, direction, cancellationToken);

    public Task<Cheque?> GetByIdAsync(string companyId, Guid chequeId, CancellationToken cancellationToken = default)
    {
        if (chequeId == Guid.Empty) throw new ArgumentException("Cheque is required.", nameof(chequeId));
        return _repository.GetByIdAsync(RequireCompany(companyId), chequeId, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Batch save + receipt/payment journal (one entry per batch).
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<Guid>> SaveBatchAsync(
        string companyId,
        IReadOnlyList<Cheque> cheques,
        string boxAccountCode,
        string userId,
        DateOnly journalDate,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        RequireCompany(companyId);
        RequireUser(userId);
        ArgumentNullException.ThrowIfNull(cheques);
        if (cheques.Count == 0)
            throw new InvalidOperationException("At least one cheque is required.");
        if (string.IsNullOrWhiteSpace(boxAccountCode))
            throw new InvalidOperationException("The portfolio / box account code is required.");
        var journal = RequireJournal();
        var direction = cheques[0].Direction;
        foreach (var cheque in cheques)
        {
            ValidateNew(cheque);
            if (cheque.Direction != direction)
                throw new InvalidOperationException("A batch cannot mix incoming and outgoing cheques.");
            cheque.Status = direction == ChequeDirections.In ? ChequeStatuses.InPortfolio : ChequeStatuses.Issued;
            cheque.PortfolioAccountCode = boxAccountCode.Trim();
        }

        var companyGuid = await _repository.ResolveCompanyGuidAsync(companyId, cancellationToken).ConfigureAwait(false);
        var boxId = await _repository.ResolveAccountIdAsync(companyGuid, boxAccountCode, cancellationToken).ConfigureAwait(false);

        // Step 7 (J-5 / cash-payments cheque BLOCKER): a batch whose
        // cheques share ONE currency and ONE rate now posts as a proper
        // transaction-currency journal entry — header currency + rate
        // are the batch's, lines carry the RAW cheque amounts, and
        // JournalRepository derives the base amounts (migration 062).
        // Before, lines were pre-multiplied by each cheque's rate while
        // the header claimed rate 1, so transaction amounts and base
        // amounts were conflated in the same columns. A batch mixing
        // currencies/rates cannot be represented by the one-rate-per-
        // entry journal model; it keeps the legacy pre-converted shape
        // (lines in base, header rate 1) — flagged in FIX_PHASE_STEP7
        // as an owner decision, not silently "fixed" into wrong money.
        var uniform = cheques.All(c =>
            string.Equals(c.CurrencyCode, cheques[0].CurrencyCode, StringComparison.OrdinalIgnoreCase)
            && c.ExchangeRate == cheques[0].ExchangeRate);
        var total = uniform
            ? cheques.Sum(c => c.Amount)
            : cheques.Sum(c => c.Amount * c.ExchangeRate);
        decimal LineAmount(Cheque c) => uniform ? c.Amount : c.Amount * c.ExchangeRate;
        var entry = new JournalEntry(
            journalDate,
            string.IsNullOrWhiteSpace(description) ? $"Cheque batch ({direction})" : description.Trim(),
            referenceNumber: null,
            currencyCode: cheques[0].CurrencyCode,
            exchangeRate: uniform ? cheques[0].ExchangeRate : 1m,
            documentNumber: ($"CHQ-{journalDate:yyyyMMdd}-{Guid.NewGuid():N}")[..36]);
        if (direction == ChequeDirections.In)
        {
            entry.AddLine(boxId, total, 0m, "Cheques received into portfolio");
            foreach (var cheque in cheques)
            {
                var partyId = await _repository.ResolveAccountIdAsync(companyGuid, cheque.PartyAccountCode!, cancellationToken).ConfigureAwait(false);
                entry.AddLine(partyId, 0m, LineAmount(cheque), $"Cheque {cheque.ChequeNo.Trim()}");
            }
        }
        else
        {
            foreach (var cheque in cheques)
            {
                var partyId = await _repository.ResolveAccountIdAsync(companyGuid, cheque.PartyAccountCode!, cancellationToken).ConfigureAwait(false);
                entry.AddLine(partyId, LineAmount(cheque), 0m, $"Cheque {cheque.ChequeNo.Trim()}");
            }
            entry.AddLine(boxId, 0m, total, "Cheques issued");
        }
        entry.EnsureBalanced();

        await journal.SaveDraftAsync(companyGuid, entry, userId, cancellationToken).ConfigureAwait(false);
        await journal.PostAsync(entry.Id, userId, cancellationToken).ConfigureAwait(false);

        foreach (var cheque in cheques)
            cheque.JournalEntryId = entry.Id;
        try
        {
            await _repository.InsertBatchAsync(companyId, cheques, userId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Compensation: the GL entry is already posted, so reverse it
            // (best effort) rather than leaving an orphan receipt/payment.
            try { await journal.ReverseAsync(entry.Id, userId, "Cheque batch insert failed; compensating reversal.", CancellationToken.None).ConfigureAwait(false); }
            catch { /* original exception is the one the caller must see */ }
            throw;
        }
        return cheques.Select(c => c.Id).ToArray();
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------
    public async Task DepositAsync(string companyId, Guid chequeId, string depositBankAccountCode, DateOnly depositDate, string userId, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Deposited);
        if (string.IsNullOrWhiteSpace(depositBankAccountCode))
            throw new InvalidOperationException("The deposit bank account code is required.");
        var expected = cheque.Status;
        cheque.DepositBankAccountCode = depositBankAccountCode.Trim();
        cheque.DepositDate = depositDate;
        cheque.Status = ChequeStatuses.Deposited;
        await _repository.UpdateTransitionAsync(companyId, cheque, expected, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(string companyId, Guid chequeId, DateOnly clearanceDate, string userId, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Cleared);
        var expected = cheque.Status;
        if (cheque.Direction == ChequeDirections.In)
        {
            // Dr Deposit Bank GL, Cr Portfolio.
            var bankCode = !string.IsNullOrWhiteSpace(cheque.DepositBankAccountCode)
                ? cheque.DepositBankAccountCode!
                : throw new InvalidOperationException("Deposit the cheque before clearing it (a deposit bank account is required).");
            var portfolioCode = !string.IsNullOrWhiteSpace(cheque.PortfolioAccountCode)
                ? cheque.PortfolioAccountCode!
                : throw new InvalidOperationException("The cheque has no portfolio account recorded.");
            await PostSimpleEntryAsync(companyId, cheque, clearanceDate, userId,
                debitCode: bankCode, creditCode: portfolioCode,
                description: $"Cheque {cheque.ChequeNo} cleared", cancellationToken).ConfigureAwait(false);
        }
        cheque.ClearanceDate = clearanceDate;
        cheque.Status = ChequeStatuses.Cleared;
        await _repository.UpdateTransitionAsync(companyId, cheque, expected, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public async Task BounceAsync(string companyId, Guid chequeId, DateOnly bounceDate, string? reason, decimal feesAmount, string userId, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Bounced);
        if (feesAmount < 0) throw new InvalidOperationException("Bounce fees cannot be negative.");
        var journal = RequireJournal();
        if (cheque.JournalEntryId is not Guid entryId)
            throw new InvalidOperationException("The cheque has no receipt journal entry to reverse.");
        var expected = cheque.Status;
        await journal.ReverseAsync(entryId, RequireUser(userId), $"Cheque {cheque.ChequeNo} bounced.", cancellationToken).ConfigureAwait(false);
        cheque.BounceDate = bounceDate;
        cheque.BounceReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        cheque.FeesAmount = feesAmount;
        cheque.Status = ChequeStatuses.Bounced;
        await _repository.UpdateTransitionAsync(companyId, cheque, expected, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task EndorseAsync(string companyId, Guid chequeId, string endorsedToAccountCode, DateOnly endorseDate, string userId, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Endorsed);
        if (string.IsNullOrWhiteSpace(endorsedToAccountCode))
            throw new InvalidOperationException("The endorsed-to account code is required.");
        var portfolioCode = !string.IsNullOrWhiteSpace(cheque.PortfolioAccountCode)
            ? cheque.PortfolioAccountCode!
            : throw new InvalidOperationException("The cheque has no portfolio account recorded.");
        var expected = cheque.Status;
        await PostSimpleEntryAsync(companyId, cheque, endorseDate, userId,
            debitCode: endorsedToAccountCode.Trim(), creditCode: portfolioCode,
            description: $"Cheque {cheque.ChequeNo} endorsed", cancellationToken).ConfigureAwait(false);
        cheque.EndorsedToAccountCode = endorsedToAccountCode.Trim();
        cheque.Status = ChequeStatuses.Endorsed;
        await _repository.UpdateTransitionAsync(companyId, cheque, expected, RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelOutgoingAsync(string companyId, Guid chequeId, string userId, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Cancelled);
        var journal = RequireJournal();
        if (cheque.JournalEntryId is not Guid entryId)
            throw new InvalidOperationException("The cheque has no payment journal entry to reverse.");
        var expected = cheque.Status;
        await journal.ReverseAsync(entryId, RequireUser(userId), $"Outgoing cheque {cheque.ChequeNo} cancelled.", cancellationToken).ConfigureAwait(false);
        cheque.Status = ChequeStatuses.Cancelled;
        await _repository.UpdateTransitionAsync(companyId, cheque, expected, userId, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private async Task PostSimpleEntryAsync(string companyId, Cheque cheque, DateOnly entryDate, string userId,
        string debitCode, string creditCode, string description, CancellationToken cancellationToken)
    {
        var journal = RequireJournal();
        var companyGuid = await _repository.ResolveCompanyGuidAsync(companyId, cancellationToken).ConfigureAwait(false);
        var debitId = await _repository.ResolveAccountIdAsync(companyGuid, debitCode, cancellationToken).ConfigureAwait(false);
        var creditId = await _repository.ResolveAccountIdAsync(companyGuid, creditCode, cancellationToken).ConfigureAwait(false);
        // Step 7: a single-cheque entry is uniform by construction —
        // raw amount as the transaction amount, the cheque's own rate
        // on the header, base derived by JournalRepository.
        var amount = cheque.Amount;
        var entry = new JournalEntry(entryDate, description, null, cheque.CurrencyCode, cheque.ExchangeRate,
            ($"CHQ-{cheque.ChequeNo.Trim()}-{Guid.NewGuid():N}")[..36]);
        entry.AddLine(debitId, amount, 0m, description);
        entry.AddLine(creditId, 0m, amount, description);
        entry.EnsureBalanced();
        await journal.SaveDraftAsync(companyGuid, entry, RequireUser(userId), cancellationToken).ConfigureAwait(false);
        await journal.PostAsync(entry.Id, userId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Cheque> LoadAsync(string companyId, Guid chequeId, CancellationToken cancellationToken)
    {
        RequireCompany(companyId);
        if (chequeId == Guid.Empty) throw new ArgumentException("Cheque is required.", nameof(chequeId));
        return await _repository.GetByIdAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cheque was not found for this company.");
    }

    private IJournalRepository RequireJournal()
        => _journal ?? throw new InvalidOperationException(
            "The journal engine is not available; cheque operations that move GL cannot run.");

    private static void ValidateNew(Cheque cheque)
    {
        ArgumentNullException.ThrowIfNull(cheque);
        if (!ChequeDirections.All.Contains(cheque.Direction))
            throw new InvalidOperationException($"Unknown cheque direction '{cheque.Direction}'.");
        if (string.IsNullOrWhiteSpace(cheque.ChequeNo))
            throw new InvalidOperationException("Cheque number is required.");
        if (string.IsNullOrWhiteSpace(cheque.BankName))
            throw new InvalidOperationException("Bank name is required.");
        if (cheque.Amount <= 0)
            throw new InvalidOperationException("Cheque amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(cheque.PartyAccountCode))
            throw new InvalidOperationException("The party account code is required for every cheque.");
        if (cheque.ExchangeRate <= 0)
            throw new InvalidOperationException("Exchange rate must be greater than zero.");
        if (string.IsNullOrWhiteSpace(cheque.CurrencyCode) || cheque.CurrencyCode.Trim().Length != 3)
            throw new InvalidOperationException("Currency code must be a three-letter ISO code.");
        cheque.ChequeNo = cheque.ChequeNo.Trim();
        cheque.BankName = cheque.BankName.Trim();
        cheque.CurrencyCode = cheque.CurrencyCode.Trim().ToUpperInvariant();
    }

    private static string RequireCompany(string companyId)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("Company is required.", nameof(companyId));
        return companyId;
    }

    private static string RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("A current user is required for audited writes.");
        return userId;
    }
}
