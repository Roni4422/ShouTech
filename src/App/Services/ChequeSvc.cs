using ShouTech.Domain.Entities;
using ShouTech.Domain.Interfaces;

namespace ShouTech.Application.Services;

/// <summary>
/// Cheque portfolio service: lifecycle transitions post through the
/// Step-3 GlPosting engine (via IJournalRepository.PostDocumentAsync).
///
/// Step 8 posting map — EVERY cheque event is its OWN journal entry,
/// linked to that cheque alone (migration 060 source columns:
/// source_doc_id = the cheque id), so no cheque's GL can ever be
/// disturbed by a sibling's lifecycle:
///
///   IN save    : per cheque Dr Portfolio/Box, Cr Party          -> InPortfolio  [CHQ-ISSUE]
///   IN Deposit : no GL (bank + date only)                       -> Deposited
///   IN Clear   : per cheque Dr Deposit Bank GL, Cr Portfolio    -> Cleared      [CHQ-CLEAR]
///   IN Bounce  : reverse THE cheque's own issue entry           -> Bounced
///                (historical shared batch entry: per-cheque
///                compensating entry Dr Party, Cr Portfolio     [CHQ-BOUNCE],
///                the shared entry is NEVER reversed)
///                + fees, when charged: Dr Party, Cr Bank/Portfolio [CHQ-FEE]
///   IN Endorse : per cheque Dr Endorsed-To account, Cr Portfolio -> Endorsed    [CHQ-ENDORSE]
///   OUT save   : per cheque Dr Party, Cr Box (portfolio)        -> Issued       [CHQ-ISSUE]
///   OUT Clear  : per cheque Dr Portfolio, Cr Paying Bank        -> Cleared      [CHQ-CLEAR]
///                (CHQ-04: clearance now debits the liability/box leg
///                and credits the bank that actually paid out)
///   OUT Cancel : reverse THE cheque's own issue entry           -> Cancelled
///                (historical shared batch entry: compensating
///                entry Dr Portfolio, Cr Party                   [CHQ-CANCEL])
///
/// Atomicity (CHQ-03): a lifecycle transition first CLAIMS the new
/// status through the repository's compare-and-swap update, then posts
/// its GL; if the GL step fails, the status claim is rolled back with
/// a compensating CAS update before the error propagates — a retry can
/// never double-post, and a failed transition never strands the cheque
/// in a terminal status without its GL (or vice versa). Batch save
/// posts each cheque's entry, then inserts the batch; any failure
/// reverses every entry posted so far as compensation.
/// Base-currency treatment is Step 7's: each entry carries the
/// cheque's own currency + rate and GlPosting/JournalRepository derive
/// the dual base amounts (migration 062) — no pre-multiplication.
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
    // Batch save: ONE journal entry per cheque (Step 8, CHQ-02).
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
        var companyGuid = await _repository.ResolveCompanyGuidAsync(companyId, cancellationToken).ConfigureAwait(false);
        var direction = cheques[0].Direction;
        foreach (var cheque in cheques)
        {
            ValidateNew(cheque);
            if (cheque.Direction != direction)
                throw new InvalidOperationException("A batch cannot mix incoming and outgoing cheques.");
            cheque.Status = direction == ChequeDirections.In ? ChequeStatuses.InPortfolio : ChequeStatuses.Issued;
            cheque.PortfolioAccountCode = boxAccountCode.Trim();
        }

        var posted = new List<Guid>();
        try
        {
            foreach (var cheque in cheques)
            {
                var lines = direction == ChequeDirections.In
                    ? new[]
                    {
                        new GlLineRequest(boxAccountCode.Trim(), cheque.Amount, 0m, Note: "Cheques received into portfolio"),
                        new GlLineRequest(cheque.PartyAccountCode!, 0m, cheque.Amount, Note: $"Cheque {cheque.ChequeNo}")
                    }
                    : new[]
                    {
                        new GlLineRequest(cheque.PartyAccountCode!, cheque.Amount, 0m, Note: $"Cheque {cheque.ChequeNo}"),
                        new GlLineRequest(boxAccountCode.Trim(), 0m, cheque.Amount, Note: "Cheques issued")
                    };
                var result = await journal.PostDocumentAsync(new GlDocumentRequest(
                    companyGuid, journalDate,
                    string.IsNullOrWhiteSpace(description) ? $"Cheque {cheque.ChequeNo} ({direction})" : description.Trim(),
                    ChequeSourceTypes.Issue, cheque.Id.ToString("D"), cheque.ChequeNo,
                    cheque.CurrencyCode, cheque.ExchangeRate, lines), userId, cancellationToken).ConfigureAwait(false);
                cheque.JournalEntryId = result.JournalEntryId;
                posted.Add(result.JournalEntryId);
            }
            await _repository.InsertBatchAsync(companyId, cheques, userId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Compensation: reverse every entry this batch posted (best
            // effort per entry) rather than leaving orphan GL behind.
            foreach (var entryId in posted)
            {
                try { await journal.ReverseAsync(entryId, userId, "Cheque batch save failed; compensating reversal.", CancellationToken.None).ConfigureAwait(false); }
                catch { /* the original exception is the one the caller must see */ }
            }
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
        => await ClearAsync(companyId, chequeId, clearanceDate, userId, bankAccountCode: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Clears a cheque. Incoming: Dr Deposit Bank / Cr Portfolio.
    /// Outgoing: Dr Portfolio / Cr Paying Bank — the bank is the explicit
    /// <paramref name="bankAccountCode"/> when given, else the bank code
    /// recorded on the cheque (deposit/paying bank field).</summary>
    public async Task ClearAsync(string companyId, Guid chequeId, DateOnly clearanceDate, string userId, string? bankAccountCode, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Cleared);
        var portfolioCode = !string.IsNullOrWhiteSpace(cheque.PortfolioAccountCode)
            ? cheque.PortfolioAccountCode!
            : throw new InvalidOperationException("The cheque has no portfolio account recorded.");
        var bankCode = !string.IsNullOrWhiteSpace(bankAccountCode)
            ? bankAccountCode!.Trim()
            : !string.IsNullOrWhiteSpace(cheque.DepositBankAccountCode)
                ? cheque.DepositBankAccountCode!
                : throw new InvalidOperationException(cheque.Direction == ChequeDirections.In
                    ? "Deposit the cheque before clearing it (a deposit bank account is required)."
                    : "A paying bank account is required to clear an outgoing cheque.");
        var expected = cheque.Status;
        var previousDepositBank = cheque.DepositBankAccountCode;
        var previousDepositDate = cheque.DepositDate;
        cheque.ClearanceDate = clearanceDate;
        if (cheque.Direction == ChequeDirections.Out && string.IsNullOrWhiteSpace(cheque.DepositBankAccountCode))
        {
            cheque.DepositBankAccountCode = bankCode;
            cheque.DepositDate = clearanceDate;
        }
        cheque.Status = ChequeStatuses.Cleared;
        await ClaimAsync(companyId, cheque, expected, userId, cancellationToken).ConfigureAwait(false);
        try
        {
            var (debit, credit) = cheque.Direction == ChequeDirections.In
                ? (bankCode, portfolioCode)
                : (portfolioCode, bankCode);
            await PostChequeEntryAsync(companyId, cheque, clearanceDate, userId,
                ChequeSourceTypes.Clear, debit, credit,
                $"Cheque {cheque.ChequeNo} cleared", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            cheque.ClearanceDate = null;
            cheque.DepositBankAccountCode = previousDepositBank;
            cheque.DepositDate = previousDepositDate;
            await ReleaseClaimAsync(companyId, cheque, expected, userId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task BounceAsync(string companyId, Guid chequeId, DateOnly bounceDate, string? reason, decimal feesAmount, string userId, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Bounced);
        if (feesAmount < 0) throw new InvalidOperationException("Bounce fees cannot be negative.");
        RequireJournal();
        if (string.IsNullOrWhiteSpace(cheque.PartyAccountCode) || string.IsNullOrWhiteSpace(cheque.PortfolioAccountCode))
            throw new InvalidOperationException("The cheque has no party / portfolio account recorded; its receipt cannot be reversed safely.");
        if (cheque.JournalEntryId is not Guid entryId)
            throw new InvalidOperationException("The cheque has no receipt journal entry to reverse.");
        var expected = cheque.Status;
        cheque.BounceDate = bounceDate;
        cheque.BounceReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        cheque.FeesAmount = feesAmount;
        cheque.Status = ChequeStatuses.Bounced;
        await ClaimAsync(companyId, cheque, expected, userId, cancellationToken).ConfigureAwait(false);
        try
        {
            await ReverseOwnOrCompensateAsync(companyId, cheque, entryId, bounceDate, userId,
                ChequeSourceTypes.Bounce,
                debitCode: cheque.PartyAccountCode!, creditCode: cheque.PortfolioAccountCode!,
                description: $"Cheque {cheque.ChequeNo} bounced",
                reversalDetails: $"Cheque {cheque.ChequeNo} bounced.", cancellationToken).ConfigureAwait(false);
            if (feesAmount > 0)
            {
                // CHQ-07: bounce fees are POSTED, not just recorded:
                // Dr Party (the drawer owes the fee), Cr the bank that
                // charged it (deposit bank when deposited, else the
                // portfolio account).
                var feeCredit = !string.IsNullOrWhiteSpace(cheque.DepositBankAccountCode)
                    ? cheque.DepositBankAccountCode! : cheque.PortfolioAccountCode!;
                await PostChequeEntryAsync(companyId, cheque, bounceDate, userId,
                    ChequeSourceTypes.Fee, cheque.PartyAccountCode!, feeCredit,
                    $"Cheque {cheque.ChequeNo} bounce fees", cancellationToken,
                    amountOverride: feesAmount).ConfigureAwait(false);
            }
        }
        catch
        {
            cheque.BounceDate = null;
            cheque.BounceReason = null;
            cheque.FeesAmount = 0m;
            await ReleaseClaimAsync(companyId, cheque, expected, userId).ConfigureAwait(false);
            throw;
        }
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
        cheque.EndorsedToAccountCode = endorsedToAccountCode.Trim();
        cheque.Status = ChequeStatuses.Endorsed;
        await ClaimAsync(companyId, cheque, expected, userId, cancellationToken).ConfigureAwait(false);
        try
        {
            await PostChequeEntryAsync(companyId, cheque, endorseDate, userId,
                ChequeSourceTypes.Endorse, endorsedToAccountCode.Trim(), portfolioCode,
                $"Cheque {cheque.ChequeNo} endorsed", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            cheque.EndorsedToAccountCode = null;
            await ReleaseClaimAsync(companyId, cheque, expected, userId).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CancelOutgoingAsync(string companyId, Guid chequeId, string userId, CancellationToken cancellationToken = default)
        => await CancelOutgoingAsync(companyId, chequeId, userId, reason: null, cancelDate: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Cancels an issued cheque, recording the stop-payment
    /// note (<paramref name="reason"/>) on the cheque (migration 063).</summary>
    public async Task CancelOutgoingAsync(string companyId, Guid chequeId, string userId, string? reason, DateOnly? cancelDate, CancellationToken cancellationToken = default)
    {
        var cheque = await LoadAsync(companyId, chequeId, cancellationToken).ConfigureAwait(false);
        EnsureTransition(cheque, ChequeStatuses.Cancelled);
        RequireJournal();
        if (string.IsNullOrWhiteSpace(cheque.PartyAccountCode) || string.IsNullOrWhiteSpace(cheque.PortfolioAccountCode))
            throw new InvalidOperationException("The cheque has no party / portfolio account recorded; its payment cannot be reversed safely.");
        if (cheque.JournalEntryId is not Guid entryId)
            throw new InvalidOperationException("The cheque has no payment journal entry to reverse.");
        var expected = cheque.Status;
        cheque.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        cheque.Status = ChequeStatuses.Cancelled;
        await ClaimAsync(companyId, cheque, expected, userId, cancellationToken).ConfigureAwait(false);
        try
        {
            await ReverseOwnOrCompensateAsync(companyId, cheque, entryId, cancelDate ?? cheque.IssueDate ?? DateOnly.FromDateTime(DateTime.Today), userId,
                ChequeSourceTypes.Cancel,
                debitCode: cheque.PortfolioAccountCode!, creditCode: cheque.PartyAccountCode!,
                description: $"Outgoing cheque {cheque.ChequeNo} cancelled",
                reversalDetails: $"Outgoing cheque {cheque.ChequeNo} cancelled.", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            cheque.CancelReason = null;
            await ReleaseClaimAsync(companyId, cheque, expected, userId).ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// The Step-8 reversal rule. When the cheque's issue entry is its
    /// OWN (the normal case from this step on: exactly one live cheque
    /// references it), the entry is reversed outright. When the entry
    /// is a HISTORICAL batch entry shared with siblings (pre-Step-8
    /// data), reversing it would wipe the siblings' GL (CHQ-02), so a
    /// per-cheque compensating entry with the mirror legs is posted
    /// instead and the shared entry is left untouched — the siblings'
    /// balances stay exact, and each sibling later gets its own
    /// compensating entry (or, for the last one, the same treatment;
    /// the shared entry itself is never split or fabricated away —
    /// see FIX_PHASE_STEP1.md for the historical treatment).
    /// </summary>
    private async Task ReverseOwnOrCompensateAsync(
        string companyId, Cheque cheque, Guid entryId, DateOnly eventDate, string userId,
        string sourceType, string debitCode, string creditCode, string description, string reversalDetails,
        CancellationToken cancellationToken)
    {
        var journal = RequireJournal();
        var sharing = await _repository.CountByJournalEntryAsync(companyId, entryId, cancellationToken).ConfigureAwait(false);
        if (sharing <= 1)
        {
            await journal.ReverseAsync(entryId, RequireUser(userId), reversalDetails, cancellationToken).ConfigureAwait(false);
            return;
        }
        await PostChequeEntryAsync(companyId, cheque, eventDate, userId, sourceType,
            debitCode, creditCode, description, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostChequeEntryAsync(
        string companyId, Cheque cheque, DateOnly entryDate, string userId,
        string sourceType, string debitCode, string creditCode, string description,
        CancellationToken cancellationToken, decimal? amountOverride = null)
    {
        var journal = RequireJournal();
        var companyGuid = await _repository.ResolveCompanyGuidAsync(companyId, cancellationToken).ConfigureAwait(false);
        var amount = amountOverride ?? cheque.Amount;
        await journal.PostDocumentAsync(new GlDocumentRequest(
            companyGuid, entryDate, description,
            sourceType, cheque.Id.ToString("D"), cheque.ChequeNo,
            cheque.CurrencyCode, cheque.ExchangeRate,
            new[]
            {
                new GlLineRequest(debitCode, amount, 0m, Note: description),
                new GlLineRequest(creditCode, 0m, amount, Note: description)
            }), RequireUser(userId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Claims a lifecycle status via the repository CAS update.</summary>
    private Task ClaimAsync(string companyId, Cheque cheque, string expectedStatus, string userId, CancellationToken cancellationToken)
        => _repository.UpdateTransitionAsync(companyId, cheque, expectedStatus, RequireUser(userId), cancellationToken);

    /// <summary>Rolls a claimed status back after a GL failure: the
    /// cheque returns to its pre-transition status/fields so the
    /// operation can be retried cleanly (CHQ-03 compensation).</summary>
    private async Task ReleaseClaimAsync(string companyId, Cheque cheque, string originalStatus, string userId)
    {
        var claimedStatus = cheque.Status;
        cheque.Status = originalStatus;
        try
        {
            await _repository.UpdateTransitionAsync(companyId, cheque, claimedStatus, RequireUser(userId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackEx)
        {
            throw new InvalidOperationException(
                $"Cheque {cheque.ChequeNo}: the GL step failed and the status rollback also failed ({rollbackEx.Message}). " +
                $"The cheque may be stranded in status '{claimedStatus}'; reconcile it manually before retrying.", rollbackEx);
        }
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
